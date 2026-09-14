"""Registry loading (v1 compatible) + registry self-validation.

The registry is POLICY. It is itself an untrusted input: every parse or
schema failure becomes RED (REGISTRY_INVALID / REGISTRY_CORRUPTION), never a
silent pass. This module only builds the in-memory policy object; it does not
classify diffs.
"""
from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field

from .model import Lifecycle, Reason, Severity, HardConstraint
from .paths import Matcher, PatternError, validate_pattern

VALID_OPERATIONS = {"create", "modify", "delete", "rename", "copy"}
VALID_LIFECYCLE = {e.value for e in Lifecycle}
_LANE_STATUS_ALIASES = {"active": "active", "merged": "merged", "released": "released",
                        "stalled": "stalled", "planned": "planned", "paused": "paused",
                        "superseded": "superseded", "abandoned": "abandoned"}


class RegistryError(Exception):
    """Fatal registry problem; carries the RED hard constraints."""

    def __init__(self, constraints: list[HardConstraint]):
        self.constraints = constraints
        super().__init__("; ".join(c.message for c in constraints)[:400])


@dataclass
class Claim:
    """One OwnershipClaim: subject + pattern + operations + attributes."""
    subject: str            # lane id, or rule-set id ("frozen", "integration", ...)
    pattern: str
    operations: set[str]
    kind: str               # frozen|architecture|integration|lane|lead|generated
    priority: int           # rule-family priority (higher = stronger)
    exclusive: bool
    authority: str          # who may legitimately change it ("lead", lane id, ...)
    lifecycle: str | None   # lane lifecycle for lane claims; None for families
    expiry: str | None
    reason: str
    matcher: Matcher = field(repr=False, default=None)  # type: ignore[assignment]
    rule_id: str = ""

    def __post_init__(self):
        self.matcher = Matcher(self.pattern)
        if not self.rule_id:
            self.rule_id = f"{self.kind}:{self.subject}:{self.pattern}"


# rule-family priority (GOVERNANCE_MODEL.md §Precedence): higher number wins
FAMILY_PRIORITY = {
    "frozen": 900,
    "architecture": 800,
    "lane_exclusive": 700,
    "integration": 600,
    "lane": 500,
    "inherited": 400,
    "generated": 300,
    "lead_default": 200,
    "default": 0,
}


@dataclass
class Lane:
    id: str
    agent: str
    wave: str
    branch: str
    task: str
    task_id: str
    task_revision: int
    task_hash: str
    owns: list[Matcher]
    status: Lifecycle
    exclusive: bool
    raw_index: int


@dataclass
class Registry:
    version: int
    policy: dict
    integration_owned: list[Matcher]
    frozen: list[Matcher]
    architecture: list[Matcher]
    generated: list[Matcher]
    lanes: dict[str, Lane]
    claims: list[Claim]
    overlap_groups: list[dict]
    sha256: str
    validation_findings: list[HardConstraint] = field(default_factory=list)
    validation_warnings: list[HardConstraint] = field(default_factory=list)

    # -------- helpers -----------------------------------------------------
    @property
    def decision_input(self) -> dict:
        return {"version": self.version, "sha256": self.sha256}


def canonical_task_hash(task_text: str) -> str:
    """SHA-256 over a canonicalized (casefolded, whitespace-collapsed) task text."""
    canon = re.sub(r"\s+", " ", (task_text or "").strip()).casefold()
    return hashlib.sha256(canon.encode("utf-8")).hexdigest()


def _load_yaml(path: str) -> dict:
    """Load registry with PyYAML only. No bespoke parser: a second YAML
    implementation would classify differently on some hosts, violating
    determinism. Missing PyYAML => REGISTRY_DEPENDENCY_MISSING => RED (fail-closed).
    """
    try:
        text = open(path, encoding="utf-8").read()
    except OSError as e:
        raise RegistryError([HardConstraint(
            Reason.REGISTRY_INVALID, Severity.HARD_RED,
            f"registry file not readable: {path}: {e}")])
    try:
        import yaml  # type: ignore
    except ImportError:
        raise RegistryError([HardConstraint(
            Reason.REGISTRY_INVALID, Severity.HARD_RED,
            "PyYAML is not installed — registry parse would fall back to a "
            "reduced-fidelity parser; governance refuses to guess")])
    try:
        data = yaml.safe_load(text)
    except Exception as e:  # yaml.YAMLError and any parser escape
        raise RegistryError([HardConstraint(
            Reason.REGISTRY_CORRUPTION, Severity.HARD_RED,
            f"registry YAML parse failure: {type(e).__name__}: {e}")])
    if not isinstance(data, dict):
        raise RegistryError([HardConstraint(
            Reason.REGISTRY_CORRUPTION, Severity.HARD_RED,
            f"registry root must be a mapping, got {type(data).__name__}")])
    return data


def _hygiene(version: int, findings: list, warnings: list,
             reason: Reason, msg: str, rule_id=None) -> None:
    """Schema corruption is RED at every version; *policy hygiene* problems
    (branch co-assignment, dangling overlap references) are RED for v2
    registries but warnings for legacy v1 registries, so historical files stay
    loadable while being reported (backward compatibility, rule 41)."""
    hc = HardConstraint(reason, Severity.HARD_RED if version >= 2 else
                        Severity.HARD_YELLOW, msg, rule_id=rule_id)
    (findings if version >= 2 else warnings).append(hc)


def _matcher_list(items: object, ctx: str, findings: list[HardConstraint]) -> list[Matcher]:
    out: list[Matcher] = []
    if items is None:
        return out
    if not isinstance(items, list):
        findings.append(HardConstraint(
            Reason.REGISTRY_INVALID, Severity.HARD_RED,
            f"registry key '{ctx}' must be a list of glob strings, got {type(items).__name__}"))
        return out
    for it in items:
        if not isinstance(it, str):
            findings.append(HardConstraint(
                Reason.REGISTRY_INVALID, Severity.HARD_RED,
                f"registry '{ctx}' entry must be a string, got {type(it).__name__}: {it!r}"))
            continue
        try:
            out.append(Matcher(it))
        except PatternError as e:
            findings.append(HardConstraint(
                Reason.REGISTRY_INVALID, Severity.HARD_RED,
                f"registry '{ctx}' invalid glob: {e}"))
    return out


def load_registry(path: str) -> Registry:
    raw_bytes = open(path, "rb").read()
    sha256 = hashlib.sha256(raw_bytes).hexdigest()
    data = _load_yaml(path)
    findings: list[HardConstraint] = []

    version = data.get("version", 1)
    if not isinstance(version, int) or version not in (1, 2):
        findings.append(HardConstraint(
            Reason.REGISTRY_INVALID, Severity.HARD_RED,
            f"unsupported registry version: {version!r} (engine supports 1, 2)"))
        version = 1

    policy = data.get("policy")
    if not isinstance(policy, dict):
        findings.append(HardConstraint(
            Reason.REGISTRY_INVALID, Severity.HARD_RED,
            "registry 'policy' must be a mapping"))
        policy = {}

    integration = _matcher_list(data.get("integration_owned"), "integration_owned", findings)
    frozen_raw = data.get("frozen") or {}
    if not isinstance(frozen_raw, dict):
        findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                       "'frozen' must be a mapping with owner+paths"))
        frozen_raw = {}
    frozen = _matcher_list(frozen_raw.get("paths"), "frozen.paths", findings)
    arch_raw = data.get("architecture_owned") or {}
    if not isinstance(arch_raw, dict):
        findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                       "'architecture_owned' must be a mapping"))
        arch_raw = {}
    arch = _matcher_list(arch_raw.get("paths"), "architecture_owned.paths", findings)
    gen_raw = data.get("generated") or {}
    if not isinstance(gen_raw, dict):
        findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                       "'generated' must be a mapping"))
        gen_raw = {}
    if "paths" in gen_raw:
        generated = _matcher_list(gen_raw.get("paths"), "generated.paths", findings)
    else:
        # v1 registries predate the generated key: apply the built-in floor so
        # bin/obj/build artifacts can never be mistaken for owned source.
        from .engine_defaults import DEFAULT_GENERATED_PATTERNS
        generated = _matcher_list(list(DEFAULT_GENERATED_PATTERNS), "generated.paths(builtin)", findings)

    lanes: dict[str, Lane] = {}
    claims: list[Claim] = []
    lanes_raw = data.get("lanes") or []
    if not isinstance(lanes_raw, list):
        findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                       "'lanes' must be a list"))
        lanes_raw = []
    seen_ids: set[str] = set()
    branch_owners: dict[str, list[str]] = {}
    for idx, lane_raw in enumerate(lanes_raw):
        if not isinstance(lane_raw, dict):
            findings.append(HardConstraint(
                Reason.REGISTRY_INVALID, Severity.HARD_RED,
                f"lane #{idx} must be a mapping, got {type(lane_raw).__name__}"))
            continue
        lid = lane_raw.get("id")
        if not isinstance(lid, str) or not lid.strip():
            findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                           f"lane #{idx}: missing 'id'"))
            continue
        lid = lid.strip()
        if lid in seen_ids:
            findings.append(HardConstraint(
                Reason.REGISTRY_INVALID, Severity.HARD_RED,
                f"duplicate lane id '{lid}' (lane #{idx})", lane_id=lid))
            continue
        seen_ids.add(lid)
        agent = str(lane_raw.get("agent") or "").strip()
        if not agent:
            findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                           f"lane '{lid}': missing 'agent'", lane_id=lid))
        branch = str(lane_raw.get("branch") or "").strip()
        if not branch:
            findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                           f"lane '{lid}': missing 'branch'", lane_id=lid))
        else:
            branch_owners.setdefault(branch, []).append(lid)
        status_raw = str(lane_raw.get("status") or "").strip().lower()
        status_raw = _LANE_STATUS_ALIASES.get(status_raw, status_raw)
        if status_raw not in VALID_LIFECYCLE:
            findings.append(HardConstraint(
                Reason.UNKNOWN_LIFECYCLE_STATE, Severity.HARD_RED,
                f"lane '{lid}': unknown lifecycle status {lane_raw.get('status')!r}",
                lane_id=lid))
            status = Lifecycle.ACTIVE  # keep parsing for further findings; claim flagged invalid below
        else:
            status = Lifecycle(status_raw)
        owns_raw = lane_raw.get("owns") or []
        ms = _matcher_list(owns_raw, f"lanes[{lid}].owns", findings)
        task = str(lane_raw.get("task") or "")
        # v1 registries predate exclusivity: their entries are NOT exclusive by
        # default (backward compatibility — never silently reinterpret old rules
        # as stronger constraints). v2 lanes are exclusive unless declared not.
        default_exclusive = int(version) >= 2
        exclusive = lane_raw.get("exclusive")
        if exclusive is None:
            exclusive = default_exclusive
        else:
            exclusive = bool(exclusive)
        revision = lane_raw.get("task_revision", 1)
        if not isinstance(revision, int) or revision < 1:
            findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                           f"lane '{lid}': task_revision must be int>=1", lane_id=lid))
            revision = 1
        lane = Lane(id=lid, agent=agent, wave=str(lane_raw.get("wave") or ""),
                    branch=branch, task=task, task_id=lid,
                    task_revision=revision, task_hash=canonical_task_hash(task),
                    owns=ms, status=status if status_raw in VALID_LIFECYCLE else Lifecycle.ACTIVE,
                    exclusive=exclusive, raw_index=idx)
        lanes[lid] = lane
        for m in ms:
            claims.append(Claim(
                subject=lid, pattern=m.pattern, operations=set(VALID_OPERATIONS),
                kind="lead" if lid == "lead" else "lane",
                priority=FAMILY_PRIORITY["lead_default"] if lid == "lead"
                else (FAMILY_PRIORITY["lane_exclusive"] if exclusive else FAMILY_PRIORITY["lane"]),
                exclusive=exclusive and lid != "lead",
                authority=agent, lifecycle=status_raw or "active",
                expiry=lane_raw.get("expiry"), reason=task, matcher=m,
                rule_id=f"lane:{lid}:{m.pattern}"))
    warnings: list[HardConstraint] = []
    for branch, owners in branch_owners.items():
        # lanes explicitly declaring shared_branch may co-own a branch
        raw_shared = {lid for lid in owners
                      if isinstance(lanes_raw, list) and any(
                          isinstance(x, dict) and x.get("id") == lid and x.get("shared_branch")
                          for x in lanes_raw)}
        if len(owners) > 1 and not branch.endswith("*") and set(owners) - raw_shared:
            _hygiene(int(version), findings, warnings, Reason.REGISTRY_INVALID,
                     f"branch '{branch}' assigned to multiple lanes {sorted(owners)} — "
                     "one branch, one lane (declare shared_branch: true to opt in)")

    for ctx, ms, kind, authority in (
        ("frozen", frozen, "frozen", str(frozen_raw.get("owner") or "lead")),
        ("architecture", arch, "architecture", str(arch_raw.get("owner") or "lead")),
    ):
        for m in ms:
            claims.append(Claim(subject=ctx, pattern=m.pattern, operations=set(VALID_OPERATIONS),
                                kind=kind, priority=FAMILY_PRIORITY[kind], exclusive=True,
                                authority=authority, lifecycle=None, expiry=None,
                                reason=f"{kind} rule family", matcher=m,
                                rule_id=f"{kind}:{m.pattern}"))
    for m in integration:
        claims.append(Claim(subject="integration", pattern=m.pattern,
                            operations=set(VALID_OPERATIONS), kind="integration",
                            priority=FAMILY_PRIORITY["integration"], exclusive=False,
                            authority="lead", lifecycle=None, expiry=None,
                            reason="integration-owned (lead applies APPEND blocks)", matcher=m,
                            rule_id=f"integration:{m.pattern}"))
    for m in generated:
        claims.append(Claim(subject="generated", pattern=m.pattern,
                            operations=set(VALID_OPERATIONS), kind="generated",
                            priority=FAMILY_PRIORITY["generated"], exclusive=False,
                            authority="any", lifecycle=None, expiry=None,
                            reason="generated file (excluded from ownership)", matcher=m,
                            rule_id=f"generated:{m.pattern}"))

    overlap_raw = data.get("overlap_groups") or []
    overlaps: list[dict] = []
    if not isinstance(overlap_raw, list):
        findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                       "'overlap_groups' must be a list"))
    else:
        for grp in overlap_raw:
            if not isinstance(grp, dict):
                findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                               f"overlap group entry must be a mapping: {grp!r}"))
                continue
            members = grp.get("lanes") or []
            if not isinstance(members, list):
                findings.append(HardConstraint(Reason.REGISTRY_INVALID, Severity.HARD_RED,
                                               f"overlap group '{grp.get('name')}' lanes must be a list"))
                members = []
            dangling = [x for x in members if x not in lanes]
            if dangling:
                _hygiene(int(version), findings, warnings, Reason.REGISTRY_INVALID,
                         f"overlap group '{grp.get('name')}' references unknown lanes "
                         f"{sorted(dangling)} — dangling authority",
                         rule_id=f"overlap:{grp.get('name')}")
            overlaps.append({"name": str(grp.get("name") or ""), "lanes": [str(x) for x in members],
                             "note": str(grp.get("note") or "")})

    # --- exclusive-ownership self-conflict scan (registry-level C>1) ---------
    lane_claims = [c for c in claims if c.kind in ("lane", "lead") and c.exclusive]
    for i in range(len(lane_claims)):
        for j in range(i + 1, len(lane_claims)):
            a, b = lane_claims[i], lane_claims[j]
            if a.subject == b.subject:
                continue
            try:
                conflict = patterns_conflict_safe(a.pattern, b.pattern)
            except PatternError:
                conflict = True
            if conflict:
                la, lb = lanes.get(a.subject), lanes.get(b.subject)
                both_active = (la and lb and la.status in (Lifecycle.ACTIVE, Lifecycle.PLANNED)
                               and lb.status in (Lifecycle.ACTIVE, Lifecycle.PLANNED))
                if both_active:
                    _hygiene(int(version), findings, warnings, Reason.OWNERSHIP_CONFLICT,
                             f"claims overlap between ACTIVE lanes: '{a.subject}' [{a.pattern}] vs "
                             f"'{b.subject}' [{b.pattern}] — ambiguous ownership must be resolved "
                             "by an explicit policy, not YAML order", rule_id=a.rule_id)
                else:
                    warnings.append(HardConstraint(
                        Reason.OWNERSHIP_CONFLICT, Severity.HARD_YELLOW,
                        f"claims overlap (historical lanes): '{a.subject}' [{a.pattern}] vs "
                        f"'{b.subject}' [{b.pattern}] — inert unless both reactivate",
                        rule_id=a.rule_id))

    reg = Registry(version=int(version), policy=policy, integration_owned=integration,
                   frozen=frozen, architecture=arch, generated=generated, lanes=lanes,
                   claims=claims, overlap_groups=overlaps, sha256=sha256,
                   validation_findings=findings, validation_warnings=warnings)
    if findings:
        raise RegistryError(findings)
    return reg


def patterns_conflict_safe(p1: str, p2: str) -> bool:
    from .paths import patterns_conflict
    return patterns_conflict(p1, p2)
