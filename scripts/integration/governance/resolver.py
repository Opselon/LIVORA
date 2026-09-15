"""Ownership resolution: match a path against all claims, compute precedence,
count conflicting exclusive claims (C), and produce per-file ownership evidence.

Deterministic tie-break order (GOVERNANCE_MODEL.md §Precedence):
  1. family priority (frozen > architecture > lane_exclusive > integration >
     lane > inherited > generated > lead_default > default)
  2. specificity S = 10P + 5D + E
  3. literal-prefix trie depth
  4. lexicographic rule_id  (total order — never YAML file order)
"""
from __future__ import annotations

from dataclasses import dataclass, field

from .model import Lifecycle, PolicyClass
from .paths import Matcher, normalize_repo_path
from .registry import Claim, Registry, FAMILY_PRIORITY


@dataclass
class OwnershipResult:
    path: str
    by_family: dict[str, list[Claim]] = field(default_factory=dict)
    exclusive_lane_claims: list[Claim] = field(default_factory=list)
    lane_claims_active: list[Claim] = field(default_factory=list)
    owners_all: list[str] = field(default_factory=list)     # any lifecycle
    owners_active: list[str] = field(default_factory=list)  # delivering or claim-bearing
    conflicting_owners: list[str] = field(default_factory=list)
    conflict_count: int = 0
    policy_class: PolicyClass = PolicyClass.UNOWNED
    matched_rules: list[str] = field(default_factory=list)
    best_claim: Claim | None = None
    relevance_lane_ids: set[str] = field(default_factory=set)

    @property
    def frozen(self) -> bool:
        return self.policy_class == PolicyClass.FROZEN

    @property
    def architecture(self) -> bool:
        return self.policy_class == PolicyClass.ARCHITECTURE


class Trie:
    """Prefix index so per-file matching is ~O(D + k) instead of O(R) per file.

    A claim is indexed under its literal prefix; a path lookup probes every
    prefix of the path (root prefix '' catches top-level wildcards) plus the
    universal '**' set.
    """

    def __init__(self, claims: list[Claim]):
        self.buckets: dict[tuple[str, ...], list[Claim]] = {}
        # casefolded mirror so a case-variant path still probes protected
        # claims (they match case-insensitively in resolve_path).
        self.buckets_cf: dict[tuple[str, ...], list[Claim]] = {}
        self.universal: list[Claim] = []
        for c in claims:
            if c.matcher.is_universal:
                self.universal.append(c)
                continue
            pref = c.matcher.literal_prefix
            self.buckets.setdefault(pref, []).append(c)
            self.buckets_cf.setdefault(tuple(x.casefold() for x in pref), []).append(c)

    def candidates(self, norm_path: str) -> list[Claim]:
        segs = norm_path.split("/")
        cf = [x.casefold() for x in segs]
        out: list[Claim] = []
        seen: set[str] = set()
        for depth in range(len(segs), -1, -1):
            for bucket in (self.buckets.get(tuple(segs[:depth]), []),
                           self.buckets_cf.get(tuple(cf[:depth]), [])):
                for c in bucket:
                    if c.rule_id not in seen:
                        out.append(c)
                        seen.add(c.rule_id)
        for c in self.universal:
            if c.rule_id not in seen:
                out.append(c)
                seen.add(c.rule_id)
        return out


def build_trie(reg: Registry) -> Trie:
    return Trie(reg.claims)


def resolve_path(path: str, reg: Registry, trie: Trie | None = None,
                 case_sensitive_root_check: bool = False) -> OwnershipResult:
    """Full ownership resolution for one normalized-or-raw path."""
    norm = normalize_repo_path(path)  # PathError propagates -> MALFORMED_PATH RED
    t = trie or build_trie(reg)
    res = OwnershipResult(path=norm)
    claim_bearing = {Lifecycle.PLANNED, Lifecycle.ACTIVE, Lifecycle.PAUSED,
                     Lifecycle.STALLED, Lifecycle.MERGED}
    delivering = {Lifecycle.PLANNED, Lifecycle.ACTIVE, Lifecycle.PAUSED, Lifecycle.STALLED}

    for c in t.candidates(norm):
        hit_ci = False
        try:
            hit = c.matcher.match(norm)
        except Exception:
            hit = False
            hit_ci = True
        if not hit and c.kind in ("frozen", "architecture", "integration"):
            # frozen families also match case-insensitively: a rename that only
            # changes case must not escape (Windows checkouts collide anyway).
            if c.matcher.match(norm, case_insensitive=True):
                hit = True
                hit_ci = True
                res.by_family.setdefault(c.kind, []).append(c)
                res.matched_rules.append(c.rule_id + "#ci")
                continue
        if not hit:
            continue
        res.by_family.setdefault(c.kind, []).append(c)
        res.matched_rules.append(c.rule_id)
        if c.kind in ("lane", "lead"):
            if c.subject not in res.owners_all:
                res.owners_all.append(c.subject)
            lane = reg.lanes.get(c.subject)
            if lane and lane.status in claim_bearing:
                res.relevance_lane_ids.add(c.subject)
            if lane and lane.status in delivering and c.subject not in res.owners_active:
                res.owners_active.append(c.subject)
            if c.exclusive and c.subject != "lead":
                res.exclusive_lane_claims.append(c)

    # C: number of DISTINCT lanes with exclusive claims on this path
    distinct = sorted({c.subject for c in res.exclusive_lane_claims})
    res.conflict_count = len(distinct)
    if res.conflict_count > 1:
        res.conflicting_owners = distinct

    # Family precedence: highest-priority family that matched wins the class.
    def fam_rank(kind: str) -> int:
        return FAMILY_PRIORITY.get(kind, -1)

    fams = sorted(res.by_family.keys(), key=fam_rank, reverse=True)
    top = fams[0] if fams else None
    if top == "frozen":
        res.policy_class = PolicyClass.FROZEN
    elif top == "architecture":
        res.policy_class = PolicyClass.ARCHITECTURE
    elif top == "integration":
        res.policy_class = PolicyClass.INTEGRATION
    elif top == "lane":
        res.policy_class = PolicyClass.LANE
    elif top == "lead":
        res.policy_class = PolicyClass.LEAD
    elif top == "generated":
        res.policy_class = PolicyClass.GENERATED
    else:
        res.policy_class = PolicyClass.UNOWNED

    # best claim = deterministic winner among all matches
    all_claims = [c for lst in res.by_family.values() for c in lst]
    if all_claims:
        res.best_claim = sorted(
            all_claims,
            key=lambda c: (-FAMILY_PRIORITY.get(c.kind, -1),
                           -c.matcher.specificity(),
                           -len(c.matcher.literal_prefix),
                           c.rule_id))[0]
    return res


def match_any(patterns: list[Matcher], norm_path: str) -> bool:
    for m in patterns:
        if m.match(norm_path):
            return True
    return False


def overlap_reconciled(reg: Registry, lane_ids: list[str]) -> str | None:
    """Return the name of an overlap group that declares ALL lane_ids together,
    or None. Used to downgrade an exclusive-ownership conflict from RED to the
    explicit-policy YELLOW path."""
    ids = set(lane_ids)
    for grp in reg.overlap_groups:
        if ids and ids <= set(grp.get("lanes") or []):
            return grp.get("name")
    return None
