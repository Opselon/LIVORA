"""Formal repository-path handling: normalization + glob semantics.

Deterministic, no substring matching, fail-closed on malformed input.
A PathError here is a governance RED (parser failure), never a pass-through.

Semantics (documented in docs/architecture/governance/GOVERNANCE_MODEL.md):
  * '**' matches zero or more path segments (git pathspec style).
  * '*' matches within one segment and never crosses '/'.
  * 'dir/**' ALSO matches 'dir' itself — the registry convention in
    .github/OWNERSHIP.yaml ('Resources/Styles/**' owns the directory).
  * matching is case-sensitive (Git on Linux CI) with an explicit
    case-insensitive second pass available for collision detection.
"""
from __future__ import annotations

import re
import unicodedata

_GLOB_META = "*?["


class PathError(ValueError):
    """Malformed / hostile path input. Callers must fail closed."""


class PatternError(ValueError):
    """Invalid ownership glob pattern."""


_ROOT_TOKENS = {".", ""}


def normalize_repo_path(raw: str) -> str:
    """Canonical repository-relative POSIX path, or PathError.

      * NFC Unicode normalization (defeats look-alike renames)
      * backslashes -> forward slashes
      * leading './' and '/' stripped; '.' segments dropped; '//' collapsed
      * '..' resolved inside the repo; escaping the root is an ERROR
      * NUL / ASCII control characters rejected (Git forbids them)
      * trailing '/' rejected
    Case is preserved; case-collisions are detected separately.
    """
    if not isinstance(raw, str):
        raise PathError(f"path is not a string: {raw!r}")
    if raw == "":
        raise PathError("empty path")
    if "\x00" in raw:
        raise PathError("NUL byte in path")
    for ch in raw:
        if unicodedata.category(ch) == "Cc":
            raise PathError(f"control character {ord(ch):#x} in path {raw!r}")
    s = unicodedata.normalize("NFC", raw).replace("\\", "/")
    if s.endswith("/"):
        raise PathError(f"trailing slash in path: {raw!r}")
    segments: list[str] = []
    for seg in s.split("/"):
        if seg in _ROOT_TOKENS:
            continue
        if seg == "..":
            if not segments:
                raise PathError(f"path escapes repository root: {raw!r}")
            segments.pop()
            continue
        segments.append(seg)
    if not segments:
        raise PathError(f"path resolves to repository root: {raw!r}")
    return "/".join(segments)


def validate_pattern(raw: object) -> str:
    """Normalize a glob pattern; reject shapes we cannot match formally."""
    if not isinstance(raw, str) or not raw.strip():
        raise PatternError(f"empty pattern: {raw!r}")
    pat = raw.strip().replace("\\", "/")
    if pat == "**":
        return "**"
    if pat.startswith("/"):
        raise PatternError(f"pattern must be repository-relative: {raw!r}")
    segs: list[str] = []
    for seg in pat.split("/"):
        if seg == ".":
            continue
        if seg == "..":
            raise PatternError(f"pattern may not traverse outside root: {raw!r}")
        if seg == "":
            continue
        if "**" in seg and seg != "**":
            raise PatternError(f"'**' must occupy a whole segment: {raw!r}")
        if seg != "**":
            for c in seg:
                if unicodedata.category(c) == "Cc":
                    raise PatternError(f"control character in pattern: {raw!r}")
        segs.append(seg)
    if not segs:
        raise PatternError(f"pattern reduces to repository root: {raw!r}")
    return "/".join(segs)


def _glob_seg_to_regex(seg: str) -> str:
    out: list[str] = []
    i = 0
    while i < len(seg):
        c = seg[i]
        if c == "*":
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        elif c == "[":
            j = seg.find("]", i + 1)
            if j < 0:
                out.append(re.escape("["))
            else:
                cls = seg[i + 1 : j]
                if cls.startswith("!"):
                    cls = "^" + cls[1:]
                out.append("[" + cls + "]")
                i = j
        else:
            out.append(re.escape(c))
        i += 1
    return "".join(out)


def _compile(pattern: str) -> re.Pattern[str]:
    parts = pattern.split("/")
    out: list[str] = []
    prev_star = False
    for idx, seg in enumerate(parts):
        last = idx == len(parts) - 1
        if seg == "**":
            if last:
                # 'dir/**' -> dir itself, or anything under it
                out.append(r"(?:/(?:[^/]+/)*[^/]+)?" if out else r".*")
            else:
                if out and not prev_star:
                    out.append("/")
                out.append("(?:[^/]+/)*")
                prev_star = True
                continue
        else:
            if out and not prev_star:
                out.append("/")
            out.append(_glob_seg_to_regex(seg))
        prev_star = False
    return re.compile("^" + "".join(out) + "$")


class Matcher:
    """Pre-compiled ownership-pattern matcher (built once per rule)."""

    __slots__ = ("pattern", "_rx", "_rx_ci")

    def __init__(self, pattern: str):
        self.pattern = validate_pattern(pattern)
        self._rx = _compile(self.pattern)
        self._rx_ci = re.compile(self._rx.pattern, re.IGNORECASE | re.DOTALL)

    def match(self, norm_path: str, case_insensitive: bool = False) -> bool:
        rx = self._rx_ci if case_insensitive else self._rx
        return rx.match(norm_path) is not None

    @property
    def literal_prefix(self) -> tuple[str, ...]:
        """Leading literal segments before any wildcard — used as trie key."""
        segs: list[str] = []
        for seg in self.pattern.split("/"):
            if seg == "**" or any(c in seg for c in _GLOB_META):
                break
            segs.append(seg)
        return tuple(segs)

    @property
    def is_universal(self) -> bool:
        return self.pattern == "**"

    def specificity(self) -> int:
        """S = 10P + 5D + E (GOVERNANCE_MODEL.md §Precedence).

        P = literal (no-wildcard) path components; D = depth EXCLUDING '**'
        segments (a '**' adds reach, not specificity); E = 1 iff the pattern
        is an exact path. This ordering makes an exact file rule beat a
        'dir/**' rule, which beats '**'.
        """
        segs = self.pattern.split("/")
        concrete = [s for s in segs if s != "**"]
        p = sum(1 for s in concrete if not any(c in s for c in _GLOB_META))
        d = len(concrete)
        e = 1 if (p == d and d == len(segs)) else 0
        return 10 * p + 5 * d + e

    def depth(self) -> int:
        return len(self.pattern.split("/"))


def segs_conflict(a: str, b: str) -> bool:
    """Can two glob segments match a common non-empty name?

    Decidable cases are decided exactly; only wildcard-vs-wildcard remains
    conservative (True) unless provably disjoint (e.g. distinct literal
    prefixes like 'Wave3b*' vs 'Wave3c*').
    """
    if a == "**" or b == "**":
        return True
    a_lit = not any(c in a for c in _GLOB_META)
    b_lit = not any(c in b for c in _GLOB_META)
    if a_lit and b_lit:
        return a.casefold() == b.casefold()
    if a_lit or b_lit:
        lit, pat = (a, b) if a_lit else (b, a)
        rx = re.compile("^" + _glob_seg_to_regex(pat) + "$")
        return rx.match(lit) is not None
    # pattern vs pattern, both simple prefix-globs ('Wave3b*' / 'Wave3c*'):
    # they intersect iff one literal prefix is a prefix of the other
    if a.endswith("*") and b.endswith("*") and not any(c in a[:-1] + b[:-1] for c in "?["):
        pa, pb = a[:-1].casefold(), b[:-1].casefold()
        return pa.startswith(pb) or pb.startswith(pa)
    return True  # undecidable combination: assume overlap (never under-report)


def patterns_conflict(p1: str, p2: str) -> bool:
    """True if two patterns can match a common path.

    Over-reports (conservative) rather than under-reports: safe direction for
    exclusive-ownership conflict detection. '**' segments branch the walk.
    """
    sa = validate_pattern(p1).split("/")
    sb = validate_pattern(p2).split("/")
    seen: set[tuple[int, int]] = set()

    def walk(i: int, j: int) -> bool:
        if (i, j) in seen:
            return False
        seen.add((i, j))
        while i < len(sa) and j < len(sb):
            if sa[i] == "**" and sb[j] == "**":
                return True
            if sa[i] == "**":
                return walk(i + 1, j) or walk(i, j + 1)
            if sb[j] == "**":
                return walk(i, j + 1) or walk(i + 1, j)
            if not segs_conflict(sa[i], sb[j]):
                return False
            i += 1
            j += 1
        # one side exhausted: remaining '**' on the other absorbs the rest
        rest = sa[i:] or sb[j:]
        if not rest:
            return True
        return rest[0] == "**"

    return walk(0, 0)
