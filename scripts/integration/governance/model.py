"""Governance domain model: vocabularies, decisions, reason codes.

Every enum here is a machine enum. Human prose NEVER goes into these fields.
Order matters: Decision severity is used for max-severity folding.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum


class Decision(str, Enum):
    GREEN = "GREEN"
    YELLOW = "YELLOW"
    RED = "RED"

    @property
    def severity(self) -> int:
        return _SEVERITY[self.value]


_SEVERITY = {"GREEN": 0, "YELLOW": 1, "RED": 2}


def worst(decisions) -> Decision:
    best = Decision.GREEN
    for d in decisions:
        if d.severity > best.severity:
            best = d
    return best


class Severity(str, Enum):
    """Constraint class of a fired rule."""
    HARD_RED = "HARD_RED"
    HARD_YELLOW = "HARD_YELLOW"
    SOFT_RISK = "SOFT_RISK"


class Operation(str, Enum):
    CREATE = "create"
    MODIFY = "modify"
    DELETE = "delete"
    RENAME = "rename"
    COPY = "copy"
    UNKNOWN = "unknown"


class PolicyClass(str, Enum):
    """Which rule family owns a path (mutually exclusive; first match wins)."""
    FROZEN = "frozen"
    ARCHITECTURE = "architecture"
    INTEGRATION = "integration"
    LANE = "lane"
    LEAD = "lead"
    GENERATED = "generated"
    UNOWNED = "unowned"


class Lifecycle(str, Enum):
    PLANNED = "planned"
    ACTIVE = "active"
    PAUSED = "paused"
    STALLED = "stalled"
    SUPERSEDED = "superseded"
    MERGED = "merged"
    RELEASED = "released"
    ABANDONED = "abandoned"


#: lanes in these states may not deliver code (see GOVERNANCE_MODEL.md §Lifecycle)
NON_DELIVERING = {Lifecycle.RELEASED, Lifecycle.SUPERSEDED, Lifecycle.ABANDONED,
                  Lifecycle.MERGED}
#: lanes whose claims still gate others (merged lanes keep their exclusive claims
#: until released, so a later lane cannot silently take over their paths)
CLAIM_BEARING = {Lifecycle.PLANNED, Lifecycle.ACTIVE, Lifecycle.PAUSED,
                 Lifecycle.STALLED, Lifecycle.MERGED}

#: legal transitions; merged->active is deliberately absent (reopen = new lane)
TRANSITIONS: dict[Lifecycle, set[Lifecycle]] = {
    Lifecycle.PLANNED: {Lifecycle.ACTIVE, Lifecycle.ABANDONED},
    Lifecycle.ACTIVE: {Lifecycle.PAUSED, Lifecycle.STALLED, Lifecycle.MERGED,
                       Lifecycle.ABANDONED, Lifecycle.SUPERSEDED},
    Lifecycle.PAUSED: {Lifecycle.ACTIVE, Lifecycle.STALLED, Lifecycle.ABANDONED,
                       Lifecycle.SUPERSEDED},
    Lifecycle.STALLED: {Lifecycle.ACTIVE, Lifecycle.ABANDONED, Lifecycle.SUPERSEDED,
                        Lifecycle.MERGED},
    Lifecycle.MERGED: {Lifecycle.RELEASED},
    Lifecycle.RELEASED: set(),
    Lifecycle.SUPERSEDED: set(),
    Lifecycle.ABANDONED: set(),
}


def lifecycle_transition_ok(a: Lifecycle, b: Lifecycle) -> bool:
    return b in TRANSITIONS[a]


class Reason(str, Enum):
    """Machine-readable reason codes (explainability contract)."""

    # --- hard RED -------------------------------------------------------------
    REGISTRY_INVALID = "REGISTRY_INVALID"
    REGISTRY_CORRUPTION = "REGISTRY_CORRUPTION"
    PARSER_FAILURE = "PARSER_FAILURE"
    FROZEN_CONTRACT_TOUCHED = "FROZEN_CONTRACT_TOUCHED"
    ARCHITECTURE_FILE_TOUCHED_BY_LANE = "ARCHITECTURE_FILE_TOUCHED_BY_LANE"
    OWNERSHIP_CONFLICT = "OWNERSHIP_CONFLICT"
    UNKNOWN_FILE_OWNER = "UNKNOWN_FILE_OWNER"
    UNKNOWN_LANE = "UNKNOWN_LANE"
    UNKNOWN_TASK = "UNKNOWN_TASK"
    UNKNOWN_OPERATION = "UNKNOWN_OPERATION"
    UNKNOWN_LIFECYCLE_STATE = "UNKNOWN_LIFECYCLE_STATE"
    UNKNOWN_RULE = "UNKNOWN_RULE"
    MALFORMED_PATH = "MALFORMED_PATH"
    PATH_TRAVERSAL = "PATH_TRAVERSAL"
    PATH_BACKSLASH_ALIAS = "PATH_BACKSLASH_ALIAS"
    PATH_CASE_COLLISION = "PATH_CASE_COLLISION"
    PATH_DUPLICATE_ENTRY = "PATH_DUPLICATE_ENTRY"
    PR_AUTHOR_IDENTITY_MISMATCH = "PR_AUTHOR_IDENTITY_MISMATCH"
    LANE_NOT_DELIVERING = "LANE_NOT_DELIVERING"
    LIFECYCLE_TRANSITION_INVALID = "LIFECYCLE_TRANSITION_INVALID"
    TASK_DRIFT_HIGH = "TASK_DRIFT_HIGH"
    TASK_REVISION_UNKNOWN = "TASK_REVISION_UNKNOWN"
    GOVERNANCE_METADATA_MISSING = "GOVERNANCE_METADATA_MISSING"
    CONTRACT_FIELD_MISSING = "CONTRACT_FIELD_MISSING"
    SECRET_IN_DIFF = "SECRET_IN_DIFF"
    TRIPWIRE_PATTERN = "TRIPWIRE_PATTERN"
    BRANCH_IS_BASE = "BRANCH_IS_BASE"
    BRANCH_UNSAFE_REWRITE = "BRANCH_UNSAFE_REWRITE"
    MISSING_EVIDENCE = "MISSING_EVIDENCE"
    TEST_EVIDENCE_MISSING = "TEST_EVIDENCE_MISSING"

    # --- hard YELLOW ----------------------------------------------------------
    INTEGRATION_FILE_CHANGED = "INTEGRATION_FILE_CHANGED"
    LEAD_AMENDMENT = "LEAD_AMENDMENT"
    CROSS_LANE_IMPACT = "CROSS_LANE_IMPACT"
    OVERSIZED_CHANGE = "OVERSIZED_CHANGE"
    STALE_BASE = "STALE_BASE"
    ARCHITECTURE_STALE_BASE = "ARCHITECTURE_STALE_BASE"
    TASK_DRIFT_MEDIUM = "TASK_DRIFT_MEDIUM"
    TASK_DRIFT_LOW = "TASK_DRIFT_LOW"
    SCOPE_DRIFT = "SCOPE_DRIFT"
    DECLARED_SCOPE_TOO_BROAD = "DECLARED_SCOPE_TOO_BROAD"
    DUPLICATE_TYPE_SUSPECTED = "DUPLICATE_TYPE_SUSPECTED"
    GENERATED_FILE_COMMITTED = "GENERATED_FILE_COMMITTED"
    LANE_STALE = "LANE_STALE"
    MERGEABILITY_UNKNOWN = "MERGEABILITY_UNKNOWN"
    SOFT_TRIPWIRE_PATTERN = "SOFT_TRIPWIRE_PATTERN"
    BASE_METADATA_MISMATCH = "BASE_METADATA_MISMATCH"
    EMPTY_CHANGESET = "EMPTY_CHANGESET"
    WEAK_TASK_RELEVANCE = "WEAK_TASK_RELEVANCE"
    LIFECYCLE_CHANGED = "LIFECYCLE_CHANGED"

    # --- GREEN predicates (reported as evidence, not findings) ----------------
    OWNERSHIP_VALID = "OWNERSHIP_VALID"
    TASK_VALID = "TASK_VALID"
    BRANCH_VALID = "BRANCH_VALID"
    NO_CONTRACT_VIOLATION = "NO_CONTRACT_VIOLATION"
    NO_EXCLUSIVE_CONFLICT = "NO_EXCLUSIVE_CONFLICT"
    GOVERNANCE_METADATA_VALID = "GOVERNANCE_METADATA_VALID"
    NO_STALE_BASE_VIOLATION = "NO_STALE_BASE_VIOLATION"
    NO_UNCLASSIFIED_FILE = "NO_UNCLASSIFIED_FILE"
    LANE_REGISTERED = "LANE_REGISTERED"
    TEST_GATES_PENDING = "TEST_GATES_PENDING"

    # --- registry warnings (policy-hygiene, non-blocking at load) -------------
    REGISTRY_OVERLAP_WARNING = "REGISTRY_OVERLAP_WARNING"
    REGISTRY_HYGIENE = "REGISTRY_HYGIENE"


@dataclass(frozen=True)
class HardConstraint:
    """One fired constraint: which rule, which path, which lane, what evidence."""
    reason: Reason
    severity: Severity
    message: str
    path: str | None = None
    rule_id: str | None = None
    lane_id: str | None = None

    def to_dict(self) -> dict:
        return {
            "reason": self.reason.value,
            "severity": self.severity.value,
            "message": self.message,
            "path": self.path,
            "rule_id": self.rule_id,
            "lane_id": self.lane_id,
        }


@dataclass(frozen=True)
class RiskFactors:
    """Categorical risk factors, each in [0,1]. Explanatory, not authoritative:
    the decision comes from the hard-constraint layer (GOVERNANCE_MODEL.md)."""
    architectural_sensitivity: float = 0.0   # A
    ownership_ambiguity: float = 0.0         # O
    contract_impact: float = 0.0             # C
    security_sensitivity: float = 0.0        # S
    task_mismatch: float = 0.0               # T
    branch_staleness: float = 0.0            # B
    dependency_fanout: float = 0.0           # D
    cross_lane_impact: float = 0.0           # X

    def to_dict(self) -> dict:
        return {
            "A": round(self.architectural_sensitivity, 3),
            "O": round(self.ownership_ambiguity, 3),
            "C": round(self.contract_impact, 3),
            "S": round(self.security_sensitivity, 3),
            "T": round(self.task_mismatch, 3),
            "B": round(self.branch_staleness, 3),
            "D": round(self.dependency_fanout, 3),
            "X": round(self.cross_lane_impact, 3),
        }


@dataclass
class FileDecision:
    path: str
    operation: Operation
    old_path: str | None = None
    new_path: str | None = None
    policy_class: PolicyClass = PolicyClass.UNOWNED
    owners: list[str] = field(default_factory=list)
    active_owners: list[str] = field(default_factory=list)
    conflicting_owners: list[str] = field(default_factory=list)
    matched_rules: list[str] = field(default_factory=list)
    relevance_level: int = -1
    decision: Decision = Decision.GREEN
    hard_constraints: list[HardConstraint] = field(default_factory=list)
    risk: RiskFactors = field(default_factory=RiskFactors)
    generated: bool = False

    def to_dict(self) -> dict:
        return {
            "path": self.path,
            "operation": self.operation.value,
            "old_path": self.old_path,
            "new_path": self.new_path,
            "policy_class": self.policy_class.value,
            "owners": sorted(set(self.owners)),
            "active_owners": sorted(set(self.active_owners)),
            "conflicting_owners": sorted(set(self.conflicting_owners)),
            "matched_rules": sorted(self.matched_rules),
            "relevance_level": self.relevance_level,
            "decision": self.decision.value,
            "generated": self.generated,
            "hard_constraints": [h.to_dict() for h in self.hard_constraints],
            "risk": self.risk.to_dict(),
        }
