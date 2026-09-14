"""Git evidence gathering: safe subprocess invocation, topology-based staleness.

Security rules honored here (GOVERNANCE_MODEL.md §Security):
  * git is ALWAYS invoked as an argument array, never via a shell;
  * every path returned by git is re-normalized before use;
  * missing/broken git state yields explicit EVIDENCE_GAP constraints
    (fail-closed) — never an assumed value.
"""
from __future__ import annotations

import subprocess
from dataclasses import dataclass, field

from .model import Operation
from .paths import PathError, normalize_repo_path


class GitError(Exception):
    pass


@dataclass(frozen=True)
class ChangedFile:
    path: str                  # canonical normalized path (new path for renames)
    old_path: str | None = None
    operation: Operation = Operation.UNKNOWN
    binary: bool = False

    @property
    def eval_paths(self) -> list[str]:
        return [p for p in (self.old_path, self.path) if p]


@dataclass
class BranchEvidence:
    head_sha: str | None = None
    base_sha: str | None = None
    merge_base: str | None = None
    commits_behind: int = -1
    commits_ahead: int = -1
    branch_exists: bool = False
    detached: bool = False
    is_base_branch: bool = False
    force_push_suspect: bool = False
    gaps: list[str] = field(default_factory=list)  # EVIDENCE_GAP identifiers
    architecture_changed_after_fork: bool = False


def _git(repo: str, *args: str, check: bool = False) -> subprocess.CompletedProcess:
    r = subprocess.run(["git", "-C", repo, *args],
                       capture_output=True, text=True, encoding="utf-8",
                       errors="replace", shell=False)
    if check and r.returncode != 0:
        raise GitError(f"git {' '.join(args)} failed: {r.stderr.strip()[:200]}")
    return r


def read_name_status_diff(repo: str, base: str, head: str) -> list[ChangedFile]:
    """Parse `git diff --name-status -M -C base...head` into ChangedFiles.

    Uses NUL-separated raw output so exotic filenames (spaces, unicode) never
    break parsing. Renames carry old_path AND path; copies carry both;
    unparseable entries are returned with UNKNOWN operation (fail-closed).
    """
    for ref in (base, head):
        if _git(repo, "cat-file", "-e", f"{ref}^{{commit}}").returncode != 0:
            raise GitError(f"unknown revision: {ref}")
    r = _git(repo, "diff", "--raw", "-M", "-C", "--find-copies-harder",
             "-z", "--no-color", f"{base}...{head}", check=True)
    fields = r.stdout.split("\0")
    out: list[ChangedFile] = []
    i = 0
    status_re = __import__("re").compile(r"^[:]\d+ \w+ \w+")
    while i < len(fields):
        tok = fields[i]
        if not tok:
            i += 1
            continue
        if status_re.match(tok):
            meta = tok.split()
            status = meta[0][-1] if meta[0].startswith(":") else "?"
            status = tok.split(" ", 2)[2].strip() if len(meta) > 2 else status
            # recompute properly: ':100644 100644 sha sha M\0path\0' or R100
            head_part = tok
            stat = head_part.split(" ")[-1].strip() or "?"
            code = stat[0]
            if code in ("R", "C"):
                old = fields[i + 1] if i + 1 < len(fields) else ""
                new = fields[i + 2] if i + 2 < len(fields) else ""
                i += 3
                try:
                    out.append(ChangedFile(
                        path=normalize_repo_path(new),
                        old_path=normalize_repo_path(old),
                        operation=Operation.RENAME if code == "R" else Operation.COPY))
                except PathError:
                    out.append(ChangedFile(path=new or old or "?malformed?",
                                           operation=Operation.UNKNOWN))
                continue
            p = fields[i + 1] if i + 1 < len(fields) else ""
            i += 2
            try:
                norm = normalize_repo_path(p)
            except PathError:
                out.append(ChangedFile(path=p or "?malformed?", operation=Operation.UNKNOWN))
                continue
            op = {"A": Operation.CREATE, "M": Operation.MODIFY, "D": Operation.DELETE,
                  "T": Operation.MODIFY}.get(code, Operation.UNKNOWN)
            out.append(ChangedFile(path=norm, operation=op))
        else:
            # stray token (defensive): treat as unknown, keep walking
            out.append(ChangedFile(path=tok, operation=Operation.UNKNOWN))
            i += 1
    return out


def detect_binary(repo: str, base: str, head: str) -> set[str]:
    """Paths whose diff has no text hunks ('-' stat columns), from numstat."""
    r = _git(repo, "diff", "--numstat", "-M", f"{base}...{head}")
    binary: set[str] = set()
    if r.returncode != 0:
        return binary
    for line in r.stdout.splitlines():
        cols = line.split("\t")
        if len(cols) >= 3 and cols[0] == "-" and cols[1] == "-":
            try:
                binary.add(normalize_repo_path(cols[2]))
            except PathError:
                pass
    return binary


def branch_evidence(repo: str, lane_branch: str, base_branch: str,
                    declared_base: str | None = None,
                    frozen_prefixes: tuple[str, ...] = ()) -> BranchEvidence:
    ev = BranchEvidence()
    if not lane_branch:
        ev.gaps.append("NO_BRANCH_DECLARED")
        return ev
    if lane_branch == base_branch:
        ev.is_base_branch = True
        return ev
    r = _git(repo, "rev-parse", "--verify", f"refs/heads/{lane_branch}")
    if r.returncode != 0:
        r = _git(repo, "rev-parse", "--verify", f"refs/remotes/origin/{lane_branch}")
        if r.returncode != 0:
            ev.gaps.append("BRANCH_NOT_FOUND")
            return ev
    ev.branch_exists = True
    ev.head_sha = r.stdout.strip()
    rb = _git(repo, "rev-parse", "--verify", f"refs/heads/{base_branch}")
    if rb.returncode != 0:
        rb = _git(repo, "rev-parse", "--verify", f"refs/remotes/origin/{base_branch}")
    if rb.returncode != 0:
        ev.gaps.append("BASE_BRANCH_NOT_FOUND")
        return ev
    ev.base_sha = rb.stdout.strip()
    mb = _git(repo, "merge-base", ev.head_sha, ev.base_sha)
    if mb.returncode != 0:
        ev.gaps.append("NO_MERGE_BASE (unrelated histories / shallow clone)")
        return ev
    ev.merge_base = mb.stdout.strip()
    ev.commits_behind = int(_git(repo, "rev-list", "--count",
                                 f"{ev.merge_base}..{ev.base_sha}").stdout or 0)
    ev.commits_ahead = int(_git(repo, "rev-list", "--count",
                                f"{ev.merge_base}..{ev.head_sha}").stdout or 0)
    if declared_base:
        d = _git(repo, "cat-file", "-t", declared_base)
        if d.returncode != 0 or d.stdout.strip() != "commit":
            ev.gaps.append("DECLARED_BASE_COMMIT_NOT_IN_REPO")
        elif not ev.merge_base.startswith(declared_base) and not declared_base.startswith(ev.merge_base):
            ev.force_push_suspect = True
    if frozen_prefixes and ev.merge_base:
        changed = _git(repo, "diff", "--name-only", f"{ev.merge_base}..{ev.base_sha}")
        for line in changed.stdout.splitlines():
            try:
                p = normalize_repo_path(line)
            except PathError:
                continue
            if any(p == f or p.startswith(f + "/") for f in frozen_prefixes):
                ev.architecture_changed_after_fork = True
                break
    return ev


def current_time_iso() -> str:
    """NOT used by classification. Provided only for humans; the manifest
    timestamp must be passed in explicitly to stay deterministic."""
    import datetime
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
