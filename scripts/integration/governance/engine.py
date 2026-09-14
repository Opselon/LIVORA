"""The governance decision engine.

Layered model (GOVERNANCE_MODEL.md §Decision-Layering):

  Policy   = registry (validated at load; invalid -> RED before anything else)
  State    = lanes / lifecycle / task revisions
  Evidence = git-derived changeset, branch topology, diff content
  Decision = HARD_RED > HARD_YELLOW > all-GREEN-predicates-proven > else RED

Numbers (risk factors, change magnitude) are EXPLANATORY ONLY: a score can
never cancel a hard constraint, and GREEN is proven by predicates rather than
granted by the absence of findings. No fake precision.
"""
from __future__ import annotations

import math
import re
from dataclasses import dataclass, field

from . import git_evidence as gite
from .model import (Decision, FileDecision, HardConstraint, Lifecycle,
                    Operation, PolicyClass, Reason, RiskFactors, Severity,
                    worst)
from .paths import Matcher, PathError, normalize_repo_path
from .registry import Registry
from .resolver import Trie, build_trie, overlap_reconciled, resolve_path

# ---------------------------------------------------------------------------
# evidence / input contracts
# ---------------------------------------------------------------------------

REQUIRED_CONTRACT_FIELDS = ("task_id", "agent_id", "wave", "base_commit", "tests_run")
CONTRACT_PATTERNS = {
    "task_id": r"Task-Id:\s*(\S+)",
    "agent_id": r"Agent-Id:\s*(\S+)",
    "wave": r"Wave:\s*(\S+)",
    "base_commit": r"Base-Commit:\s*([0-9a-fA-F]{7,40})",
    "owned_scope": r"Owned-Scope:\s*(.+)",
    "tests_run": r"Tests-Run:\s*(.+)",
    "task_hash": r"Task-Hash:\s*([0-9a-fA-F]{8,64})",
    "task_revision": r"Task-Revision:\s*(\d+)",
    "lane_branch": r"Lane-Branch:\s*(\S+)",
    "drift_ack": r"Task-Drift-Ack:\s*(.+)",
}

SECRET_PATTERNS = [
    re.compile(r"(?i)\bsk-[A-Za-z0-9][A-Za-z0-9._-]{16,}"),
    re.compile(r"(?i)api[_-]?key\s*[:=]\s*['\"][^'\"]{16,}['\"]"),
    re.compile(r"(?i)bearer\s+[A-Za-z0-9._-]{32,}"),
    re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    re.compile(r"(?i)(password|passwd|secret)\s*[:=]\s*['\"][^'\"]{8,}['\"]"),
]
HARD_TRIPWIRES = [
    ("XAML_BRACE_COMMENT", re.compile(r"\{" + chr(47) + r"\*"), ".xaml"),
]
SOFT_TRIPWIRES = [
    ("HOLLOW_DI", re.compile(r"services\.Add(Singleton|Scoped|Transient)<(\w+)>\(\);"), ".cs"),
    ("BRUSH_ON_COLOR",
     re.compile(r"(TextColor|BackgroundColor|FieldBackgroundColor)\s*=\s*\"\{StaticResource\s+\w*Brush\}\""),
     ".xaml"),
]
TYPE_DEF = re.compile(r"^\+\s*(?:public|internal|sealed|abstract|static|\s)*(?:interface|class|record|enum)\s+(\w+)")

from .engine_defaults import DEFAULT_GENERATED_PATTERNS  # single source of truth

# ---------------------------------------------------------------------------
# reason-code -> severity (explicit total map; mutation-proof: no prefix magic)
# ---------------------------------------------------------------------------

YELLOW_REASONS = frozenset({
    Reason.INTEGRATION_FILE_CHANGED, Reason.LEAD_AMENDMENT, Reason.CROSS_LANE_IMPACT,
    Reason.OVERSIZED_CHANGE, Reason.STALE_BASE, Reason.ARCHITECTURE_STALE_BASE,
    Reason.TASK_DRIFT_MEDIUM, Reason.TASK_DRIFT_LOW, Reason.SCOPE_DRIFT,
    Reason.DUPLICATE_TYPE_SUSPECTED,
    Reason.GENERATED_FILE_COMMITTED, Reason.LANE_STALE, Reason.MERGEABILITY_UNKNOWN,
    Reason.SOFT_TRIPWIRE_PATTERN, Reason.BASE_METADATA_MISMATCH, Reason.EMPTY_CHANGESET,
    Reason.WEAK_TASK_RELEVANCE, Reason.LIFECYCLE_CHANGED,
})
REASON_SEVERITY: dict[Reason, Severity] = {
    r: (Severity.HARD_YELLOW if r in YELLOW_REASONS else Severity.HARD_RED)
    for r in Reason
}


def fold_decisions(constraints: list[HardConstraint]) -> Decision:
    """HARD_RED > HARD_YELLOW > GREEN — the only severity fold in the system."""
    if any(c.severity == Severity.HARD_RED for c in constraints):
        return Decision.RED
    if any(c.severity == Severity.HARD_YELLOW for c in constraints):
        return Decision.YELLOW
    return Decision.GREEN


def parse_pr_contract(body: str) -> dict:
    out: dict[str, str | None] = {}
    body = body or ""
    for key, pat in CONTRACT_PATTERNS.items():
        m = re.search(pat, body)
        out[key] = m.group(1).strip() if m else None
    return out


def scan_added_lines(diff_text: str) -> dict:
    hits: dict = {"secret": [], "hard_tripwire": [], "soft_tripwire": [], "types": {}}
    cur = ""
    for ln in (diff_text or "").splitlines():
        if ln.startswith("+++ b/"):
            try:
                cur = normalize_repo_path(ln[6:].strip())
            except PathError:
                cur = ln[6:].strip()
            continue
        if not ln.startswith("+") or ln.startswith("+++"):
            continue
        code = ln[1:]
        loc = f"{cur}: {ln!r}"[:160]
        for pat in SECRET_PATTERNS:
            if pat.search(code):
                hits["secret"].append(loc)
                break
        for name, pat, ext in HARD_TRIPWIRES:
            if (not ext or cur.endswith(ext)) and pat.search(code):
                hits["hard_tripwire"].append(f"{name}: {loc}")
        for name, pat, ext in SOFT_TRIPWIRES:
            if (not ext or cur.endswith(ext)) and pat.search(code):
                hits["soft_tripwire"].append(f"{name}: {loc}")
        if cur.endswith(".cs"):
            m = TYPE_DEF.match(ln)
            if m:
                hits["types"].setdefault(m.group(1), set()).add(cur)
    return hits


# ---------------------------------------------------------------------------
# explanatory metrics
# ---------------------------------------------------------------------------

def change_magnitude(files: int, additions: int, deletions: int,
                     renames: int, binary: int) -> float:
    """CM in [0,1]:  0.5*min(F,F_CAP)/F_CAP + 0.2*log10(1+A+D)/4
                  + 0.2*min(R,R_CAP)/R_CAP + 0.1*min(B,B_CAP)/B_CAP

    Log-scaled line counts so 10x growth is one step, not ten. CM never
    produces RED by itself; size is a policy threshold (YELLOW) only.
    """
    F_CAP, R_CAP, B_CAP = 100.0, 20.0, 20.0
    v = (0.5 * min(files, F_CAP) / F_CAP
         + 0.2 * min(math.log10(1 + additions + deletions), 4.0) / 4.0
         + 0.2 * min(renames, R_CAP) / R_CAP
         + 0.1 * min(binary, B_CAP) / B_CAP)
    return round(min(1.0, v), 4)


def relevance_level(path: str, submitter_owns: bool, lane_modules: set[str]) -> int:
    """Task-to-diff relevance (0..4). Only >=3 may support GREEN."""
    if submitter_owns:
        return 3
    if path.split("/")[0] in lane_modules:
        return 2
    return 0


# ---------------------------------------------------------------------------
# engine
# ---------------------------------------------------------------------------

@dataclass
class GovernanceInput:
    registry: Registry
    changeset: list[gite.ChangedFile]
    contract: dict                      # parsed PR body fields
    meta: dict                          # author / mergeable / shas
    branch_evidence: gite.BranchEvidence | None = None
    diff_text: str = ""
    added: int = 0
    deleted: int = 0
    binary_paths: frozenset = frozenset()
    timestamp: str = "FROZEN"           # explicit input; never read the clock


@dataclass
class GovernanceResult:
    decision: Decision = Decision.GREEN
    file_decisions: list[FileDecision] = field(default_factory=list)
    hard_constraints: list[HardConstraint] = field(default_factory=list)
    green_predicates: dict[str, bool] = field(default_factory=dict)
    risk: RiskFactors = field(default_factory=RiskFactors)
    change_magnitude: float = 0.0
    reason_codes: list[str] = field(default_factory=list)
    task_drift: str = "NONE"
    lane_id: str | None = None
    extras: dict = field(default_factory=dict)


def _hc(reason: Reason, msg: str, path=None, rule=None, lane=None) -> HardConstraint:
    return HardConstraint(reason=reason, severity=REASON_SEVERITY[reason],
                          message=msg, path=path, rule_id=rule, lane_id=lane)


def _lane_branch_matches(pat: str, declared: str) -> bool:
    """Branch patterns use the SAME formal glob semantics as paths (§6):
    'agent/w3c/lane03-*' and 'integration/**' both match, nothing substring-matches."""
    try:
        m = Matcher(pat)
    except Exception:
        return pat == declared
    try:
        return m.match(declared)
    except PathError:
        return pat == declared


def classify(gi: GovernanceInput) -> GovernanceResult:
    reg = gi.registry
    res = GovernanceResult()
    con = res.hard_constraints
    contract = gi.contract
    meta = gi.meta
    lane_id = contract.get("task_id")
    res.lane_id = lane_id

    # ---- stage 0: registry self-validation (policy may itself be broken) ----
    if reg.validation_findings:
        con.extend(reg.validation_findings)

    # ---- stage 1: lane + identity resolution ----
    lane = reg.lanes.get(lane_id) if lane_id else None
    if lane_id is None:
        con.append(_hc(Reason.CONTRACT_FIELD_MISSING,
                       "PR body has no 'Task-Id' line — lane cannot be resolved"))
    elif lane is None:
        con.append(_hc(Reason.UNKNOWN_LANE,
                       f"Task-Id '{lane_id}' is not a registered lane — register via lead PR first",
                       lane=lane_id))
    for k in REQUIRED_CONTRACT_FIELDS:
        if k != "task_id" and not contract.get(k):
            con.append(_hc(Reason.CONTRACT_FIELD_MISSING,
                           f"PR body missing '{k}' contract line"))
    acting_as_lead = bool(lane) and lane.id == "lead"

    if lane and contract.get("agent_id") and contract["agent_id"] != lane.agent:
        con.append(_hc(Reason.PR_AUTHOR_IDENTITY_MISMATCH,
                       f"declared Agent-Id '{contract['agent_id']}' != lane '{lane.id}' registered "
                       f"agent '{lane.agent}'", lane=lane.id))
    bindings = (reg.policy or {}).get("agent_bindings")
    actor = meta.get("author")
    if isinstance(bindings, dict) and actor and lane:
        bound = bindings.get(actor)
        if isinstance(bound, str) and bound != lane.id:
            con.append(_hc(Reason.PR_AUTHOR_IDENTITY_MISMATCH,
                           f"GitHub actor '{actor}' is bound to lane '{bound}' but submits as "
                           f"lane '{lane.id}'", lane=lane.id))

    if lane:
        if lane.status in (Lifecycle.RELEASED, Lifecycle.SUPERSEDED, Lifecycle.ABANDONED,
                           Lifecycle.MERGED):
            con.append(_hc(Reason.LANE_NOT_DELIVERING,
                           f"lane '{lane.id}' lifecycle={lane.status.value} cannot deliver code "
                           "(merged lanes need a re-open/new lane id)", lane=lane.id))
        elif lane.status == Lifecycle.STALLED:
            con.append(_hc(Reason.LANE_STALE,
                           f"lane '{lane.id}' is stalled — coordinator review required", lane=lane.id))
        declared_branch = contract.get("lane_branch")
        if lane.branch and declared_branch and not _lane_branch_matches(lane.branch, declared_branch):
            con.append(_hc(Reason.BRANCH_UNSAFE_REWRITE,
                           f"PR branch '{declared_branch}' does not match lane '{lane.id}' registered "
                           f"branch '{lane.branch}' — branch spoofing", lane=lane.id))

    # ---- stage 2: task drift (immutable id, revisioned content) ----
    if lane:
        th = (contract.get("task_hash") or "").lower()
        if not th:
            con.append(_hc(Reason.CONTRACT_FIELD_MISSING,
                           "Task-Hash absent — task identity cannot be proven (unknown fails closed)",
                           lane=lane.id))
        elif not lane.task_hash.startswith(th):
            ack = contract.get("drift_ack") or ""
            high = ("high" in ack.lower()
                    or bool(re.search(r"ownership|security|architect|contract|frozen", ack, re.I)))
            if high:
                con.append(_hc(Reason.TASK_DRIFT_HIGH,
                               f"task '{lane.id}' definition changed with ownership/security/"
                               "architecture implications — explicit re-authorization required",
                               lane=lane.id))
                res.task_drift = "HIGH"
            else:
                con.append(_hc(Reason.TASK_DRIFT_MEDIUM,
                               f"Task-Hash {th[:12]} != registry task_hash {lane.task_hash[:12]} — "
                               "task text changed without a matching re-authorization", lane=lane.id))
                res.task_drift = "MEDIUM"
        else:
            res.task_drift = "NONE"
        rev = contract.get("task_revision")
        if rev is not None and str(rev).isdigit() and lane.task_revision != int(rev):
            con.append(_hc(Reason.TASK_REVISION_UNKNOWN,
                           f"declared Task-Revision {rev} != registry {lane.task_revision}"))

    # ---- stage 3: evidence completeness (git + mergeability) ----
    be = gi.branch_evidence
    if be is not None:
        if be.is_base_branch:
            con.append(_hc(Reason.BRANCH_IS_BASE, "lane branch == base branch — never allowed"))
        for gap in be.gaps:
            con.append(_hc(Reason.MISSING_EVIDENCE, f"git evidence gap: {gap}"))
        if be.force_push_suspect:
            con.append(_hc(Reason.BRANCH_UNSAFE_REWRITE,
                           f"declared Base-Commit {str(contract.get('base_commit',''))[:8]} is not an "
                           f"ancestor of merge-base {str(be.merge_base)[:8]} — history rewritten"))
        stale_commits = int((reg.policy or {}).get("stale_commits_threshold", 25))
        if be.architecture_changed_after_fork:
            con.append(_hc(Reason.ARCHITECTURE_STALE_BASE,
                           "frozen/architecture paths changed on master after this branch forked — "
                           "rebase required (architecture staleness outranks time)"))
        elif be.commits_behind >= stale_commits:
            con.append(_hc(Reason.STALE_BASE,
                           f"branch is {be.commits_behind} commits behind master (threshold {stale_commits})"))
        elif contract.get("base_commit") and be.merge_base and \
                not be.merge_base.startswith(contract["base_commit"]):
            con.append(_hc(Reason.BASE_METADATA_MISMATCH,
                           f"declared Base-Commit {contract['base_commit'][:8]} != merge-base "
                           f"{be.merge_base[:8]}"))
    else:
        con.append(_hc(Reason.MISSING_EVIDENCE,
                       "no branch evidence supplied — branch validity cannot be proven"))

    mg = (meta.get("mergeable") or "unknown").lower()
    if mg == "dirty":
        con.append(_hc(Reason.MISSING_EVIDENCE, "mergeable=dirty — conflicts against current master"))
    elif mg == "unknown":
        con.append(_hc(Reason.MERGEABILITY_UNKNOWN, "mergeability unknown — engine will not guess"))

    # ---- stage 4: per-file decisions ----
    trie = build_trie(reg)
    declared = []
    bad_declared = []
    for s in (x.strip() for x in re.split(r"[,;]", contract.get("owned_scope") or "")):
        if not s:
            continue
        try:
            declared.append(Matcher(s))
        except Exception:
            bad_declared.append(s)
    if bad_declared:
        con.append(_hc(Reason.MALFORMED_PATH, f"Owned-Scope has unparseable entries: {bad_declared[:3]}"))
    if any(m.is_universal for m in declared):
        if acting_as_lead:
            # lead legitimately owns '**': drift is disarmed by policy, so the
            # lead keeps the drift-code on record but at YELLOW severity
            # (review-required, never auto-GREEN).
            con.append(HardConstraint(Reason.DECLARED_SCOPE_TOO_BROAD,
                                      Severity.HARD_YELLOW,
                                      "lead declared Owned-Scope '**' — drift check inert by "
                                      "policy for the lead; every lead file folds to review",
                                      lane_id="lead"))
            con.append(_hc(Reason.LEAD_AMENDMENT,
                           "lead amendments are always human-reviewed, never auto-GREEN"))
        else:
            con.append(_hc(Reason.DECLARED_SCOPE_TOO_BROAD,
                           "Owned-Scope '**' self-disarms the drift check — declare your lane's "
                           "own globs (RED: a lane must never disarm its own scope contract)"))
    if not gi.changeset:
        con.append(_hc(Reason.EMPTY_CHANGESET,
                       "empty changeset — a PR with zero files proves nothing and cannot be GREEN"))

    lane_modules = {p.split("/")[0] for c in reg.claims if c.subject == lane_id
                    for p in [c.pattern] if not c.matcher.is_universal} if lane_id else set()

    casefold_seen: dict[str, set[str]] = {}
    for ch in gi.changeset:
        for p in ch.eval_paths:
            try:
                n = normalize_repo_path(p)
                casefold_seen.setdefault(n.casefold(), set()).add(n)
            except PathError:
                pass

    files: list[FileDecision] = []
    touched_lanes: set[str] = set()
    for ch in gi.changeset:
        fd = FileDecision(path=ch.path, operation=ch.operation,
                          old_path=ch.old_path, new_path=ch.path)
        bad = []
        eval_paths: list[str] = []
        for p in ch.eval_paths:
            if p is None:
                continue
            try:
                eval_paths.append(normalize_repo_path(p))
            except PathError as e:
                bad.append(f"{p!r}: {e}")
        if bad:
            fd.decision = Decision.RED
            fd.hard_constraints.append(_hc(Reason.MALFORMED_PATH,
                                           f"git path rejected by normalization: {bad[0]}", path=ch.path))
            fd.risk = RiskFactors(ownership_ambiguity=1.0, task_mismatch=1.0)
            files.append(fd)
            continue
        if any("\\" in p for p in ch.eval_paths):
            fd.hard_constraints.append(_hc(Reason.PATH_BACKSLASH_ALIAS,
                                           "diff path contains backslashes (alias of a normalized path)",
                                           path=fd.path))
        cf = normalize_repo_path(fd.path).casefold()
        if len(casefold_seen.get(cf, set())) > 1:
            fd.hard_constraints.append(_hc(Reason.PATH_CASE_COLLISION,
                                           f"case-variant paths in one changeset: {sorted(casefold_seen[cf])}",
                                           path=fd.path))

        own = [resolve_path(p, reg, trie) for p in eval_paths]
        fd.owners = sorted({s for o in own for s in o.owners_all})
        fd.active_owners = sorted({s for o in own for s in o.owners_active})
        fd.conflicting_owners = sorted({s for o in own for s in o.conflicting_owners})
        fd.matched_rules = sorted({r for o in own for r in o.matched_rules})
        for o in own:
            touched_lanes |= o.relevance_lane_ids
        conflict_c = max((o.conflict_count for o in own), default=0)
        classes = [o.policy_class for o in own]
        # precedence: frozen > architecture > integration > lane > generated >
        # lead-default (** catch-all is the WEAKEST explicit claim) > unowned
        order = [PolicyClass.FROZEN, PolicyClass.ARCHITECTURE, PolicyClass.INTEGRATION,
                 PolicyClass.LANE, PolicyClass.GENERATED, PolicyClass.LEAD, PolicyClass.UNOWNED]
        fd.policy_class = next((c for c in order if c in classes), PolicyClass.UNOWNED)
        fd.generated = any(pc == PolicyClass.GENERATED for pc in classes) \
            and not any(pc in (PolicyClass.FROZEN, PolicyClass.ARCHITECTURE,
                               PolicyClass.INTEGRATION, PolicyClass.LANE) for pc in classes)
        if fd.generated:
            fd.hard_constraints.append(_hc(Reason.GENERATED_FILE_COMMITTED,
                                           f"{fd.path} matches a governed generated-file pattern — "
                                           "generated output must not be committed; if it must be, "
                                           "govern it as a normal owned path", path=fd.path,
                                           rule="generated", lane=lane_id))

        if fd.policy_class == PolicyClass.FROZEN:
            rule = next((r for r in fd.matched_rules if r.startswith("frozen:")), "frozen")
            if acting_as_lead:
                fd.hard_constraints.append(_hc(Reason.LEAD_AMENDMENT,
                                               f"{fd.path} matches {rule}; touched by the lead lane itself — "
                                               "human review required, never auto-GREEN",
                                               path=fd.path, rule=rule, lane="lead"))
            else:
                fd.hard_constraints.append(_hc(Reason.FROZEN_CONTRACT_TOUCHED,
                                               f"{fd.path} matches {rule}; authorized_owner=lead; "
                                               f"lane={lane_id or '?'}; amendment=absent",
                                               path=fd.path, rule=rule, lane=lane_id))
        elif fd.policy_class == PolicyClass.ARCHITECTURE:
            rule = next((r for r in fd.matched_rules if r.startswith("architecture:")), "architecture")
            if acting_as_lead:
                fd.hard_constraints.append(_hc(Reason.LEAD_AMENDMENT,
                                               f"{fd.path} matches {rule}; lead lane touching its own "
                                               "architecture rule needs review", path=fd.path, rule=rule,
                                               lane="lead"))
            else:
                fd.hard_constraints.append(_hc(Reason.ARCHITECTURE_FILE_TOUCHED_BY_LANE,
                                               f"{fd.path} matches {rule} — workflows/registry/scripts are "
                                               "lead-only", path=fd.path, rule=rule, lane=lane_id))
        elif fd.policy_class == PolicyClass.INTEGRATION and not acting_as_lead:
            fd.hard_constraints.append(_hc(Reason.INTEGRATION_FILE_CHANGED,
                                           f"{fd.path} is integration-owned: lane may PROPOSE (APPEND block), "
                                           "lead applies — report who/why/task", path=fd.path,
                                           rule="integration", lane=lane_id))

        if conflict_c > 1:
            grp = overlap_reconciled(reg, fd.conflicting_owners)
            if grp:
                fd.hard_constraints.append(_hc(Reason.CROSS_LANE_IMPACT,
                                               f"exclusive claims {fd.conflicting_owners} share overlap group "
                                               f"'{grp}' — explicit policy: lead arbitrates", path=fd.path,
                                               lane=lane_id))
            else:
                fd.hard_constraints.append(_hc(Reason.OWNERSHIP_CONFLICT,
                                               f"{fd.path}: C={conflict_c} exclusive claims "
                                               f"{fd.conflicting_owners} — ambiguous ownership is RED",
                                               path=fd.path, lane=lane_id))

        if fd.policy_class == PolicyClass.UNOWNED:
            fd.hard_constraints.append(_hc(Reason.UNKNOWN_FILE_OWNER,
                                           f"{fd.path} matches no ownership rule — unknown fails closed",
                                           path=fd.path, lane=lane_id))
        elif fd.policy_class == PolicyClass.LEAD:
            fd.hard_constraints.append(_hc(Reason.LEAD_AMENDMENT,
                                           f"{fd.path} is covered only by the lead default claim ('**') — "
                                           "lead-delivered paths need human review, never auto-GREEN",
                                           path=fd.path, lane=lane_id or "lead"))
        elif fd.policy_class == PolicyClass.LANE and lane and not acting_as_lead \
                and lane.id not in fd.active_owners:
            others = fd.owners or [f"<non-delivering:{','.join(fd.owners) or 'none'}>"]
            fd.hard_constraints.append(_hc(Reason.UNKNOWN_FILE_OWNER,
                                           f"{fd.path} is owned by {others}; submitting lane '{lane.id}' "
                                           "holds no active claim", path=fd.path, lane=lane.id))
        if fd.operation == Operation.UNKNOWN:
            fd.hard_constraints.append(_hc(Reason.UNKNOWN_OPERATION,
                                           f"git reported an unclassifiable operation for {fd.path}",
                                           path=fd.path))
        if declared and not any(m.match(fd.path) for m in declared):
            fd.hard_constraints.append(_hc(Reason.SCOPE_DRIFT,
                                           f"{fd.path} inside ownership but outside declared Owned-Scope",
                                           path=fd.path, lane=lane_id))

        submitter_owns = bool(lane) and (acting_as_lead or lane.id in fd.active_owners)
        fd.relevance_level = relevance_level(fd.path, submitter_owns, lane_modules)
        if fd.relevance_level <= 0 and fd.policy_class not in (PolicyClass.FROZEN,
                                                               PolicyClass.ARCHITECTURE,
                                                               PolicyClass.INTEGRATION):
            fd.hard_constraints.append(_hc(Reason.WEAK_TASK_RELEVANCE,
                                           f"{fd.path}: task relevance level {fd.relevance_level} — no "
                                           "ownership or module relationship to the assigned task",
                                           path=fd.path, lane=lane_id))
        fd.risk = RiskFactors(
            architectural_sensitivity=1.0 if fd.policy_class in (PolicyClass.FROZEN,
                                                                 PolicyClass.ARCHITECTURE) else 0.2,
            ownership_ambiguity=min(1.0, conflict_c / 3) if conflict_c else
            (0.5 if fd.policy_class == PolicyClass.UNOWNED else 0.0),
            contract_impact=1.0 if fd.policy_class == PolicyClass.FROZEN else 0.0,
            security_sensitivity=1.0 if "security" in fd.path.lower() else 0.0,
            task_mismatch=(3 - min(fd.relevance_level, 3)) / 3.0,
            branch_staleness=0.4 if (be and be.architecture_changed_after_fork) else 0.0,
            dependency_fanout=0.0,
            cross_lane_impact=0.5 if conflict_c > 1 else 0.0)
        fd.decision = fold_decisions(fd.hard_constraints)
        files.append(fd)
    res.file_decisions = files

    # ---- stage 5: PR-level content scans ----
    scans = scan_added_lines(gi.diff_text)
    for h in scans["secret"]:
        con.append(_hc(Reason.SECRET_IN_DIFF, f"secret pattern in added lines: {h}"))
    for h in scans["hard_tripwire"]:
        con.append(_hc(Reason.TRIPWIRE_PATTERN, f"hard tripwire: {h}"))
    for h in scans["soft_tripwire"]:
        con.append(_hc(Reason.SOFT_TRIPWIRE_PATTERN, f"soft tripwire: {h}"))
    for name, locs in sorted(scans["types"].items()):
        if len(set(locs)) > 1:
            con.append(_hc(Reason.DUPLICATE_TYPE_SUSPECTED,
                           f"type '{name}' newly defined in multiple files: {sorted(locs)}"))
    if lane:
        for grp in reg.overlap_groups:
            members = set(grp.get("lanes") or [])
            hit = (touched_lanes & members) - {lane.id}
            if lane.id in members and hit:
                con.append(_hc(Reason.CROSS_LANE_IMPACT,
                               f"change set spans overlap group '{grp['name']}' lanes "
                               f"{sorted(touched_lanes & members)} — one implementation must win",
                               lane=lane.id))

    # ---- stage 6: size gate (policy threshold, never RED by itself) ----
    max_files = int((reg.policy or {}).get("auto_merge_max_changed_files") or 40)
    renames = sum(1 for c in gi.changeset if c.operation == Operation.RENAME)
    binaries = sum(1 for c in gi.changeset if c.path in gi.binary_paths)
    res.change_magnitude = change_magnitude(len(gi.changeset), gi.added, gi.deleted,
                                            renames, binaries)
    if len(gi.changeset) > max_files:
        con.append(_hc(Reason.OVERSIZED_CHANGE,
                       f"{len(gi.changeset)} changed files > cap {max_files} (magnitude={res.change_magnitude})"))

    # ---- stage 7: fold to decision + prove GREEN ----
    all_cons = list(con)
    for fd in files:
        all_cons.extend(fd.hard_constraints)
    reds = [c for c in all_cons if c.severity == Severity.HARD_RED]
    yellows = [c for c in all_cons if c.severity == Severity.HARD_YELLOW]
    preds = compute_predicates(gi, res, reds, yellows)
    res.green_predicates = preds
    res.reason_codes = sorted({c.reason.value for c in all_cons})
    if reds:
        res.decision = Decision.RED
    elif yellows:
        res.decision = Decision.YELLOW
    else:
        failed = sorted(k for k, v in preds.items() if not v)
        if failed:
            res.decision = Decision.RED
            con.append(_hc(Reason.UNKNOWN_RULE,
                           f"no fired constraint but GREEN predicates failed: {failed} — engine "
                           "inconsistency fails closed"))
        else:
            res.decision = Decision.GREEN
    if files:
        def agg(attr: str) -> float:
            return round(sum(getattr(f.risk, attr) for f in files) / len(files), 3)
        res.risk = RiskFactors(*(agg(a) for a in (
            "architectural_sensitivity", "ownership_ambiguity", "contract_impact",
            "security_sensitivity", "task_mismatch", "branch_staleness",
            "dependency_fanout", "cross_lane_impact")))
    return res


def compute_predicates(gi: GovernanceInput, res: GovernanceResult,
                       reds: list[HardConstraint], yellows: list[HardConstraint]) -> dict[str, bool]:
    """GREEN = ALL predicates proven. Absence of a finding is NOT proof — each
    predicate names the evidence it requires."""
    files = res.file_decisions
    lane = gi.registry.lanes.get(res.lane_id) if res.lane_id else None
    be = gi.branch_evidence
    R = {c.reason for c in reds}
    Y = {c.reason for c in yellows}

    def red_any(*reasons: Reason) -> bool:
        return bool(set(reasons) & R)

    def any_(*reasons: Reason) -> bool:
        return bool(set(reasons) & (R | Y))

    return {
        "registry_valid": not gi.registry.validation_findings,
        "governance_metadata_valid": not red_any(Reason.CONTRACT_FIELD_MISSING,
                                                Reason.GOVERNANCE_METADATA_MISSING)
        and not any_(Reason.CONTRACT_FIELD_MISSING, Reason.GOVERNANCE_METADATA_MISSING),
        "lane_registered": lane is not None,
        "ownership_valid": lane is not None and not red_any(
            Reason.UNKNOWN_FILE_OWNER, Reason.OWNERSHIP_CONFLICT, Reason.PATH_CASE_COLLISION,
            Reason.PATH_BACKSLASH_ALIAS, Reason.MALFORMED_PATH, Reason.LANE_NOT_DELIVERING),
        "task_valid": lane is not None and res.task_drift == "NONE" and not any_(
            Reason.UNKNOWN_TASK, Reason.TASK_DRIFT_HIGH, Reason.TASK_DRIFT_MEDIUM,
            Reason.TASK_REVISION_UNKNOWN, Reason.CONTRACT_FIELD_MISSING),
        "branch_valid": bool(be) and be.branch_exists and not be.is_base_branch
        and not be.force_push_suspect and not be.gaps,
        "no_contract_violation": not red_any(Reason.FROZEN_CONTRACT_TOUCHED,
                                             Reason.ARCHITECTURE_FILE_TOUCHED_BY_LANE,
                                             Reason.SECRET_IN_DIFF, Reason.TRIPWIRE_PATTERN),
        "no_exclusive_conflict": Reason.OWNERSHIP_CONFLICT not in R,
        "no_unclassified_file": all(
            f.operation != Operation.UNKNOWN and f.policy_class != PolicyClass.UNOWNED
            and not any(c.reason in (Reason.MALFORMED_PATH, Reason.UNKNOWN_OPERATION)
                        for c in f.hard_constraints) for f in files),
        "no_stale_base_violation": bool(be) and not be.architecture_changed_after_fork
        and not any_(Reason.STALE_BASE, Reason.ARCHITECTURE_STALE_BASE,
                     Reason.BASE_METADATA_MISMATCH, Reason.MISSING_EVIDENCE),
        "no_identity_mismatch": Reason.PR_AUTHOR_IDENTITY_MISMATCH not in R,
        "changeset_nonempty": len(files) > 0,
        "mergeability_clean": (gi.meta.get("mergeable") or "unknown").lower() == "clean",
        "test_gates_declared": bool(gi.contract.get("tests_run")),
    }
