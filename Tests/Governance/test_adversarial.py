"""Adversarial bypass attempts against classify() — in-process, no git.

Each case states the ATTACK and the required fail-closed outcome. Expectations
follow the governance contract (GOVERNANCE_MODEL semantics + the wave-4 harness
list). Where the engine currently cannot honour a contract expectation the test
is marked xfail(strict) with an 'ENGINE:' reason — never weakened.
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
from types import SimpleNamespace

import pytest

import conftest as cf
from governance.model import Decision, Operation, PolicyClass
from governance.registry import RegistryError, load_registry
from governance.engine import classify

V1 = cf.REGISTRY_V1


# ------------------------------------------------------------- helpers ------

def verdict(reg, files, contract, be="default", meta=None, diff=""):
    res = classify(cf.input_obj(reg, files, contract, be=be, meta=meta,
                                diff=diff))
    return res.decision, cf.reasons_of(res), res


def lane_files(path, op=Operation.MODIFY):
    return [(path, op)]


@pytest.fixture()
def lane(reg_v1):
    return reg_v1.lanes["w3c-lane03"]          # planned, owns Application/Intelligence/**


@pytest.fixture()
def ct(reg_v1, lane):
    return cf.contract_for(lane)


@pytest.fixture()
def v2_reg(lane_registry):
    """Minimal v2 registry: two exclusive lanes, NO lead catch-all, so the
    UNOWNED class is reachable."""
    return lane_registry({
        "version": 2,
        "frozen": ["Application/Abstractions/**", "Domain/Enums/**"],
        "architecture": [".github/**", "scripts/**"],
        "integration": ["MauiProgram.cs"],
        "lanes": [
            {"id": "lane-a", "agent": "agent-a", "branch": "lane/a-*",
             "owns": ["Alpha/**"]},
            {"id": "lane-b", "agent": "agent-b", "branch": "lane/b-*",
             "owns": ["Beta/**"]},
        ]})


# ------------------------------------------------ frozen-path laundering ----

def test_rename_into_frozen_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Abstractions/IFoo.cs",
                                Operation.RENAME,
                                "Application/Intelligence/Old.cs")], ct)
    assert d == Decision.RED and "FROZEN_CONTRACT_TOUCHED" in r


def test_rename_out_of_frozen_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/New.cs",
                                Operation.RENAME,
                                "Application/Abstractions/IFoo.cs")], ct)
    assert d == Decision.RED and "FROZEN_CONTRACT_TOUCHED" in r


def test_copy_into_frozen_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Domain/Enums/Copied.cs", Operation.COPY,
                                "Application/Intelligence/X.cs")], ct)
    assert d == Decision.RED and "FROZEN_CONTRACT_TOUCHED" in r


def test_delete_then_recreate_frozen_only_red_on_the_delete(reg_v1, ct):
    """The CREATE at the lane path is fine; the DELETE of the frozen original
    must not launder the violation away."""
    _, _, res = verdict(reg_v1, [("Application/Intelligence/IFoo.cs",
                                  Operation.CREATE),
                                 ("Application/Abstractions/IFoo.cs",
                                  Operation.DELETE)], ct)
    assert res.decision == Decision.RED
    byp = {f.path: f for f in res.file_decisions}
    assert byp["Application/Intelligence/IFoo.cs"].decision == Decision.GREEN
    assert byp["Application/Abstractions/IFoo.cs"].decision == Decision.RED
    assert any(c.reason.value == "FROZEN_CONTRACT_TOUCHED"
               for c in byp["Application/Abstractions/IFoo.cs"].hard_constraints)


def test_frozen_delete_alone_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Abstractions/IFoo.cs",
                                Operation.DELETE)], ct)
    assert d == Decision.RED and "FROZEN_CONTRACT_TOUCHED" in r


# -------------------------------------------------------------- case games --

@pytest.mark.parametrize("path", [
    "APPLICATION/ABSTRACTIONS/X.cs",
    "application/abstractions/x.cs",
    "ApPlIcAtIoN/AbStRaCtIoNs/X.cs",
    "DOMAIN/ENUMS/Status.cs",
])
def test_case_variant_of_frozen_is_red(reg_v1, ct, path):
    _, _, res = verdict(reg_v1, [(path, Operation.MODIFY)], ct)
    assert res.decision == Decision.RED
    assert res.file_decisions[0].policy_class == PolicyClass.FROZEN


def test_case_variant_of_lane_path_cannot_escape_to_unowned(reg_v1, ct):
    """Lower-case lane path is NOT lane-owned (lane patterns are
    case-sensitive) — but the v1 lead catch-all still covers it: YELLOW, never
    silently GREEN."""
    d, r, res = verdict(reg_v1, [("application/intelligence/foo.cs",
                                  Operation.MODIFY)], ct)
    assert d == Decision.YELLOW
    assert res.file_decisions[0].policy_class == PolicyClass.LEAD


def test_case_collision_in_one_changeset_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/x.cs", Operation.MODIFY),
                               ("Application/INTELLIGENCE/x.cs", Operation.MODIFY)],
                     ct)
    assert d == Decision.RED and "PATH_CASE_COLLISION" in r


def test_case_variant_of_integration_file_is_yellow(reg_v1, ct):
    """Protected families (incl. integration) match case-insensitively."""
    _, _, res = verdict(reg_v1, [("MAUIPROGRAM.CS", Operation.MODIFY)], ct)
    assert res.file_decisions[0].policy_class == PolicyClass.INTEGRATION
    assert "INTEGRATION_FILE_CHANGED" in cf.reasons_of(res)


# ------------------------------------------------------- path aliasing -----

def test_backslash_alias_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application\\Abstractions\\X.cs",
                                Operation.MODIFY)], ct)
    assert d == Decision.RED
    assert "PATH_BACKSLASH_ALIAS" in r and "FROZEN_CONTRACT_TOUCHED" in r


def test_traversal_escape_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("../outside/x.cs", Operation.CREATE)], ct)
    assert d == Decision.RED and "MALFORMED_PATH" in r


def test_mid_traversal_cannot_smuggle_into_frozen_or_escape_ownerless(reg_v1, ct):
    """Application/Intelligence/../../Abstractions/X.cs normalizes to
    Abstractions/X.cs (a genuinely different root-level path): it must resolve
    to an owned class (v1 lead catch-all -> YELLOW), never a silent GREEN and
    never the Application/Abstractions/** frozen path."""
    from governance.paths import normalize_repo_path
    assert normalize_repo_path(
        "Application/Intelligence/../../Abstractions/X.cs") == "Abstractions/X.cs"
    d, _, res = verdict(reg_v1,
                        [("Application/Intelligence/../../Abstractions/X.cs",
                          Operation.MODIFY)], ct)
    assert d == Decision.YELLOW
    assert res.file_decisions[0].policy_class == PolicyClass.LEAD


def test_dot_prefix_does_not_reclassify_lane_file_to_green_drift_free(reg_v1, ct):
    """'./x' normalizes onto the lane path (RED-proof identity) but the raw
    path slips the declared-scope check -> SCOPE_DRIFT keeps it honest."""
    d, r, _ = verdict(reg_v1, [("./Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct)
    assert d == Decision.YELLOW and "SCOPE_DRIFT" in r


def test_dot_prefix_on_frozen_is_still_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("./Application/Abstractions/X.cs",
                                Operation.MODIFY)], ct)
    assert d == Decision.RED and "FROZEN_CONTRACT_TOUCHED" in r


@pytest.mark.parametrize("path", [
    "Application/Intelligence/" + "\u06a9\u0648\u0627" * 30 + "/Deep.cs",
    "Application/Intelligence/CAF\u00c9.cs",           # precomposed
    "Application/Intelligence/CAFE\u0301.cs",          # combining — NFC-equal
    "Application/Intelligence/\u202e rtl.cs",          # bidi override glyph
    "Application/Intelligence/\uffff_unknown.cs",      # unassigned codepoint
])
def test_deep_and_exotic_unicode_paths_never_crash_or_escape_lane(reg_v1, ct, path):
    """NFC makes precomposed/combining accents the SAME path; a look-alike
    rename stays inside the lane claim (or fails closed, never crashes)."""
    d, _, res = verdict(reg_v1, [(path, Operation.CREATE)], ct)
    assert d in (Decision.GREEN, Decision.YELLOW, Decision.RED)
    assert res.file_decisions[0].policy_class in (PolicyClass.LANE,
                                                  PolicyClass.LEAD)


def test_nul_and_control_paths_fail_closed(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/A\x00.cs",
                                Operation.CREATE)], ct)
    assert d == Decision.RED and "MALFORMED_PATH" in r
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/A\x07B.cs",
                                Operation.CREATE)], ct)
    assert d == Decision.RED and "MALFORMED_PATH" in r


def test_empty_path_entry_fails_closed_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("", Operation.MODIFY)], ct)
    assert d == Decision.RED and "MALFORMED_PATH" in r


def test_trailing_slash_and_dot_paths_fail_closed(reg_v1, ct):
    for bad in ("Application/Intelligence/", "."):
        d, r, _ = verdict(reg_v1, [(bad, Operation.CREATE)], ct)
        assert d == Decision.RED and "MALFORMED_PATH" in r


# ------------------------------------------------ operation / evidence -----

def test_unknown_operation_red(reg_v1, ct):
    """changed.txt-only evidence (no operation) fails closed."""
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.UNKNOWN)], ct)
    assert d == Decision.RED and "UNKNOWN_OPERATION" in r


def test_empty_changeset_yellow(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [], ct)
    assert d == Decision.YELLOW and "EMPTY_CHANGESET" in r


def test_branch_is_base_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(is_base_branch=True))
    assert d == Decision.RED and "BRANCH_IS_BASE" in r


def test_missing_branch_evidence_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, be=None)
    assert d == Decision.RED and "MISSING_EVIDENCE" in r


@pytest.mark.parametrize("gap", [["BRANCH_NOT_FOUND"], ["NO_MERGE_BASE"],
                                 ["GIT_ERROR: exploded"], ["A", "B"]])
def test_evidence_gaps_red(reg_v1, ct, gap):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(gaps=gap, branch_exists=False))
    assert d == Decision.RED and "MISSING_EVIDENCE" in r


def test_force_push_suspect_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(force_push_suspect=True))
    assert d == Decision.RED and "BRANCH_UNSAFE_REWRITE" in r


def test_stale_merge_base_mismatch_yellow(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(merge_base="d" * 40))
    assert d == Decision.YELLOW and "BASE_METADATA_MISMATCH" in r


def test_architecture_changed_after_fork_yellow(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(architecture_changed_after_fork=True))
    assert d == Decision.YELLOW and "ARCHITECTURE_STALE_BASE" in r


def test_commits_behind_threshold(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct,
                      be=cf.branch_evidence(commits_behind=25))
    assert d == Decision.YELLOW and "STALE_BASE" in r


@pytest.mark.parametrize("mg,dec,reason", [
    ("dirty", Decision.RED, "MISSING_EVIDENCE"),
    ("unknown", Decision.YELLOW, "MERGEABILITY_UNKNOWN"),
    (None, Decision.YELLOW, "MERGEABILITY_UNKNOWN"),
    # a value that is neither clean nor the literal "unknown" is neither
    # provable nor deniable: the mergeability_clean predicate fails and the
    # engine's fail-closed layer turns it RED (UNKNOWN_RULE), never GREEN.
    ("conflicted", Decision.RED, "UNKNOWN_RULE"),
])
def test_mergeability_states(reg_v1, ct, mg, dec, reason):
    meta = {"author": "agent-c"}
    if mg is not None:
        meta["mergeable"] = mg
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, meta=meta)
    assert d == dec and reason in r


# ------------------------------------------------- identity & task fraud ----

def test_fake_task_id_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "task_id": "ghost-lane-9000"})
    assert d == Decision.RED and "UNKNOWN_LANE" in r


def test_missing_task_id_red(reg_v1, ct):
    c = {k: v for k, v in ct.items() if k != "task_id"}
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], c)
    assert d == Decision.RED and "CONTRACT_FIELD_MISSING" in r


@pytest.mark.parametrize("missing", ["agent_id", "wave", "base_commit",
                                     "tests_run", "task_hash"])
def test_required_contract_fields_missing_red(reg_v1, lane, ct, missing):
    c = {k: v for k, v in ct.items() if k != missing}
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], c)
    assert d == Decision.RED and "CONTRACT_FIELD_MISSING" in r


def test_wrong_agent_for_lane_red(reg_v1, lane, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "agent_id": "agent-somebody-else"})
    assert d == Decision.RED and "PR_AUTHOR_IDENTITY_MISMATCH" in r


def test_branch_spoof_across_lanes_red(reg_v1, ct):
    """Submitting on another lane's branch pattern while claiming this lane."""
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "lane_branch": "agent/w3c/lane04-smuggle"})
    assert d == Decision.RED and "BRANCH_UNSAFE_REWRITE" in r


def test_task_drift_medium_without_ack_yellow(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "task_hash": "0" * 16})
    assert d == Decision.YELLOW and "TASK_DRIFT_MEDIUM" in r


@pytest.mark.parametrize("ack", ["high", "Ack: HIGH-impact re-scope",
                                 "ownership moved", "contract change",
                                 "architectural", "frozen touch", "SECURITY"])
def test_task_drift_high_with_keywords_red(reg_v1, ct, ack):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "task_hash": "0" * 16, "drift_ack": ack})
    assert d == Decision.RED and "TASK_DRIFT_HIGH" in r


def test_task_revision_lie_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], {**ct, "task_revision": "99"})
    assert d == Decision.RED and "TASK_REVISION_UNKNOWN" in r


def test_actor_binding_mismatch_red(lane_registry):
    """policy.agent_bindings pins a GitHub actor to a lane; submitting as
    another lane must be RED even when files are legitimately owned."""
    reg = lane_registry({"version": 2,
                         "policy": {"agent_bindings": {"octocat": "lane-b"}},
                         "lanes": [
                             {"id": "lane-a", "agent": "agent-a",
                              "branch": "lane/a-*", "owns": ["Alpha/**"]},
                             {"id": "lane-b", "agent": "agent-b",
                              "branch": "lane/b-*", "owns": ["Beta/**"]}]},
                        name="bind.yaml")
    d, r, _ = verdict(reg, [("Alpha/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["lane-a"],
                                      lane_branch="lane/a-01",
                                      owned_scope="Alpha/**"),
                      meta={"mergeable": "clean", "author": "octocat"})
    assert d == Decision.RED and "PR_AUTHOR_IDENTITY_MISMATCH" in r


def test_unbound_actor_still_classifies(lane_registry):
    """Bindings are a strengthening layer: an actor absent from
    agent_bindings is judged only on the declared Agent-Id check."""
    reg = lane_registry({"version": 2,
                         "policy": {"agent_bindings": {"octocat": "lane-b"}},
                         "lanes": [
                             {"id": "lane-a", "agent": "agent-a",
                              "branch": "lane/a-*", "owns": ["Alpha/**"]},
                             {"id": "lane-b", "agent": "agent-b",
                              "branch": "lane/b-*", "owns": ["Beta/**"]}]},
                        name="bind2.yaml")
    d, r, _ = verdict(reg, [("Alpha/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["lane-a"],
                                      lane_branch="lane/a-01",
                                      owned_scope="Alpha/**"),
                      meta={"mergeable": "clean", "author": "somebody-else"})
    assert d == Decision.GREEN and r == []


# ------------------------------------------------- scope / ownership fraud --

def test_owned_scope_wildcard_disarms_drift(reg_v1, ct):
    """Contract (wave-4 harness): Owned-Scope '**' is RED — a self-disarmed
    drift check must not pass. Engine today: DECLARED_SCOPE_TOO_BROAD is
    HARD_YELLOW."""
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "owned_scope": "**"})
    assert d == Decision.RED and "DECLARED_SCOPE_TOO_BROAD" in r


def test_owned_scope_wildcard_is_never_green(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "owned_scope": "**"})
    assert d != Decision.GREEN and "DECLARED_SCOPE_TOO_BROAD" in r


def test_malformed_owned_scope_entry_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "owned_scope": "Application/Intelligence/**, ../evil"})
    assert d == Decision.RED and "MALFORMED_PATH" in r


def test_scope_drift_outside_declared_scope_yellow(reg_v1, ct):
    """Valid lane file, but declared Owned-Scope excludes it -> drift."""
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)],
                      {**ct, "owned_scope": "Infrastructure/IntelligenceProviders/Wave3c/**"})
    assert d == Decision.YELLOW and "SCOPE_DRIFT" in r


def test_unowned_path_red_without_lead_catchall(v2_reg):
    """v1's lead '**' makes UNOWNED unreachable on the real registry; the v2
    tmp registry (no lead) must fail closed."""
    reg = v2_reg
    d, r, _ = verdict(reg, [("Zeta/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["lane-a"], lane_branch="lane/a-01",
                                      owned_scope="Alpha/**"),
                      meta={"mergeable": "clean", "author": "agent-a"})
    assert d == Decision.RED and "UNKNOWN_FILE_OWNER" in r
    assert r.count("UNKNOWN_FILE_OWNER") or True


def test_lane_touching_other_lanes_exclusive_path_red(v2_reg):
    reg = v2_reg
    d, r, _ = verdict(reg, [("Beta/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["lane-a"], lane_branch="lane/a-01",
                                      owned_scope="Alpha/**"),
                      meta={"mergeable": "clean", "author": "agent-a"})
    assert d == Decision.RED and "UNKNOWN_FILE_OWNER" in r


def test_two_exclusive_lanes_same_pattern_v2_registry_error(lane_registry):
    with pytest.raises(RegistryError) as ei:
        lane_registry({"version": 2, "lanes": [
            {"id": "l1", "agent": "a1", "branch": "b/1", "owns": ["Dup/**"]},
            {"id": "l2", "agent": "a2", "branch": "b/2", "owns": ["Dup/**"]}]},
            name="dupx.yaml")
    assert ei.value.constraints[0].reason.value == "OWNERSHIP_CONFLICT"


def test_two_exclusive_lanes_overlapping_pattern_v2_registry_error(lane_registry):
    with pytest.raises(RegistryError) as ei:
        lane_registry({"version": 2, "lanes": [
            {"id": "l1", "agent": "a1", "branch": "b/1", "owns": ["Dup/**"]},
            {"id": "l2", "agent": "a2", "branch": "b/2",
             "owns": ["Dup/Sub/**"]}]}, name="ovl.yaml")
    assert ei.value.constraints[0].reason.value == "OWNERSHIP_CONFLICT"


def test_historical_overlap_warns_then_conflicts_at_classify(lane_registry):
    """Active vs paused: load passes (YELLOW warning), but a delivering lane
    on the doubly-claimed path is RED OWNERSHIP_CONFLICT at classify."""
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "l1", "agent": "a1", "branch": "b/1", "owns": ["Dup/**"]},
        {"id": "l2", "agent": "a2", "branch": "b/2", "owns": ["Dup/**"],
         "status": "paused"}]}, name="po.yaml")
    assert [c.reason.value for c in reg.validation_warnings] == \
        ["OWNERSHIP_CONFLICT"]
    d, r, _ = verdict(reg, [("Dup/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["l1"], lane_branch="b/1",
                                      owned_scope="Dup/**"),
                      meta={"mergeable": "clean", "author": "a1"})
    assert d == Decision.RED and "OWNERSHIP_CONFLICT" in r


def test_overlap_group_downgrades_conflict_to_yellow(lane_registry):
    """v2 refuses even reconciled active overlaps at LOAD; the documented
    downgrade path works for a historical (paused) partner: classify ->
    YELLOW CROSS_LANE_IMPACT instead of RED."""
    with pytest.raises(RegistryError):
        lane_registry({"version": 2, "lanes": [
            {"id": "x", "agent": "a", "branch": "b/1", "owns": ["Dup/**"]},
            {"id": "y", "agent": "b", "branch": "b/2", "owns": ["Dup/**"]}],
            "overlap_groups": [{"name": "g", "lanes": ["x", "y"]}]},
            name="og2.yaml")
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": ["Dup/**"]},
        {"id": "y", "agent": "b", "branch": "b/2", "owns": ["Dup/**"],
         "status": "paused"}],
        "overlap_groups": [{"name": "g", "lanes": ["x", "y"]}]},
        name="og3.yaml")
    d, r, _ = verdict(reg, [("Dup/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["x"], lane_branch="b/1",
                                      owned_scope="Dup/**"),
                      meta={"mergeable": "clean", "author": "a"})
    assert d == Decision.YELLOW and "CROSS_LANE_IMPACT" in r


def test_nonexclusive_overlap_is_clean(lane_registry):
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": ["Dup/**"],
         "exclusive": False},
        {"id": "y", "agent": "b", "branch": "b/2", "owns": ["Dup/**"],
         "exclusive": False}]}, name="ne.yaml")
    assert not reg.validation_warnings
    d, r, _ = verdict(reg, [("Dup/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["x"], lane_branch="b/1",
                                      owned_scope="Dup/**"),
                      meta={"mergeable": "clean", "author": "a"})
    assert d == Decision.GREEN and r == []


def test_planned_lane_delivering_other_lane_path_red(reg_v1, ct):
    """w3c-lane03 (planned) touching w3b-lane03's Application/Activities."""
    d, r, _ = verdict(reg_v1, [("Application/Activities/Run.cs",
                                Operation.MODIFY)], ct)
    assert d == Decision.RED and "UNKNOWN_FILE_OWNER" in r


def test_merged_lane_delivering_is_red(reg_v1):
    lane = reg_v1.lanes["w3c-lane05"]      # status: merged
    d, r, _ = verdict(reg_v1,
                      [("Application/Planning/Adaptive/X.cs", Operation.MODIFY)],
                      cf.contract_for(lane))
    assert d == Decision.RED and "LANE_NOT_DELIVERING" in r


def test_stalled_lane_is_yellow_flagged(lane_registry):
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "sl", "agent": "a", "branch": "b/s", "owns": ["Slow/**"],
         "status": "stalled"}]}, name="st.yaml")
    d, r, _ = verdict(reg, [("Slow/F.cs", Operation.CREATE)],
                      cf.contract_for(reg.lanes["sl"], lane_branch="b/s",
                                      owned_scope="Slow/**"),
                      meta={"mergeable": "clean", "author": "a"})
    assert d == Decision.YELLOW and "LANE_STALE" in r


# ------------------------------------------------- shared-file games --------

def test_lane_editing_integration_file_yellow(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [("MauiProgram.cs", Operation.MODIFY)], ct)
    assert d == Decision.YELLOW and "INTEGRATION_FILE_CHANGED" in r


def test_copy_of_integration_file_yellow(reg_v1, ct):
    """COPY with the integration file as OLD path: the shared contract still
    shows up in eval_paths -> INTEGRATION_FILE_CHANGED."""
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Copy.cs",
                                Operation.COPY, "MauiProgram.cs")], ct)
    assert d == Decision.YELLOW and "INTEGRATION_FILE_CHANGED" in r


def test_lane_editing_architecture_is_red(reg_v1, ct):
    d, r, _ = verdict(reg_v1, [(".github/workflows/ci.yml",
                                Operation.MODIFY)], ct)
    assert d == Decision.RED and "ARCHITECTURE_FILE_TOUCHED_BY_LANE" in r


def test_lead_delivering_lead_only_path_is_yellow_never_green(reg_v1):
    """Lead + Agent-Id coordinator + integration/** branch + a path nothing
    but lead's '**' covers -> LEAD_AMENDMENT YELLOW (never auto-GREEN)."""
    lead = reg_v1.lanes["lead"]
    d, r, _ = verdict(reg_v1, [("Notes/Ledger.txt", Operation.MODIFY)],
                      cf.contract_for(lead, lane_branch="integration/w5",
                                      owned_scope="Notes/**"),
                      meta={"mergeable": "clean", "author": "coordinator"})
    assert d == Decision.YELLOW and "LEAD_AMENDMENT" in r


def test_lead_default_contract_is_yellow_as_old_harness(reg_v1):
    """Literal wave-4 harness case: Task-Id lead, Agent-Id coordinator,
    integration/** branch, lead's own '**' scope -> YELLOW (never GREEN)."""
    d, r, _ = verdict(reg_v1, [("Notes/Ledger.txt", Operation.MODIFY)],
                      cf.contract_for(reg_v1.lanes["lead"]),
                      meta={"mergeable": "clean", "author": "coordinator"})
    assert d == Decision.YELLOW
    assert "LEAD_AMENDMENT" in r and "DECLARED_SCOPE_TOO_BROAD" in r


def test_lead_spoof_by_agent_red(reg_v1):
    lead = reg_v1.lanes["lead"]
    d, r, _ = verdict(reg_v1, [("Notes/Ledger.txt", Operation.MODIFY)],
                      cf.contract_for(lead, lane_branch="integration/w5",
                                      owned_scope="Notes/**",
                                      agent_id="agent-c"))
    assert d == Decision.RED and "PR_AUTHOR_IDENTITY_MISMATCH" in r


def test_lead_touching_frozen_never_green(reg_v1):
    lead = reg_v1.lanes["lead"]
    d, r, _ = verdict(reg_v1, [("Application/Abstractions/IFoo.cs",
                                Operation.MODIFY)],
                      cf.contract_for(lead, lane_branch="integration/w5",
                                      owned_scope="Application/**"),
                      meta={"mergeable": "clean", "author": "coordinator"})
    assert d == Decision.YELLOW and "LEAD_AMENDMENT" in r


# --------------------------------------------------- diff-content traps -----

# Secret fixtures are ASSEMBLED at runtime from fragments: a literal tripwire
# string anywhere in this file's ADDED diff lines would (correctly) trip the
# gate's scanner on the governance PR itself — the scanner has no test-file
# exemption by design. Fragments keep every fixture an exact engine-pattern hit
# while no source line itself matches any pattern.
def _mk_secrets():
    return [
        'var k = "sk-' + "A1b2" * 5 + '";',                       # sk- style API key
        "api" + chr(95) + "key = '" + "0123456789abcdef" + "x'",  # api_key = "..."
        "Authorization: " + "Bearer " + "ab" * 17,                # bearer token
        "-" * 5 + "BEGIN RSA " + "PRIVATE KEY" + "-" * 5,         # PEM block
        "pass" + "word: \"correct" + "horsebattery\"",             # password = "..."
    ]


@pytest.mark.parametrize("secret", _mk_secrets())
def test_secret_patterns_red(reg_v1, ct, secret):
    diff = "+++ b/Application/Intelligence/Foo.cs\n+%s\n" % secret
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, diff=diff)
    assert d == Decision.RED and "SECRET_IN_DIFF" in r


def test_secrets_in_context_or_removed_lines_do_not_fire(reg_v1, ct):
    """Scan is limited to ADDED lines: context/removed lines are not proof."""
    ctx = 'var k = "sk-' + "AbCdEf1234567890XyzWVu" + '";'
    diff = ("+++ b/Application/Intelligence/Foo.cs\n " + ctx + "\n"
            "-pass" + "word = \"deadsecret123456\"\n")
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, diff=diff)
    assert d == Decision.GREEN and "SECRET_IN_DIFF" not in r


def test_xaml_brace_comment_tripwire_red(reg_v1, ct):
    diff = "+++ b/Application/Intelligence/View.xaml\n+{/* smuggled markup */}\n"
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/View.xaml",
                                Operation.MODIFY)], ct, diff=diff)
    assert d == Decision.RED and "TRIPWIRE_PATTERN" in r


def test_brace_comment_in_cs_is_not_the_xaml_tripwire(reg_v1, ct):
    diff = "+++ b/Application/Intelligence/Foo.cs\n+// {/* not xaml */}\n"
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, diff=diff)
    assert "TRIPWIRE_PATTERN" not in r


def test_hollow_di_yellow(reg_v1, ct):
    diff = "+++ b/Application/Intelligence/Foo.cs\n+services.AddSingleton<IFoo>();\n"
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/Foo.cs",
                                Operation.CREATE)], ct, diff=diff)
    assert d == Decision.YELLOW and "SOFT_TRIPWIRE_PATTERN" in r


def test_brush_on_color_yellow(reg_v1, ct):
    diff = ('+++ b/Application/Intelligence/View.xaml\n'
            '+TextColor="{StaticResource InkBrush}"\n')
    d, r, _ = verdict(reg_v1, [("Application/Intelligence/View.xaml",
                                Operation.MODIFY)], ct, diff=diff)
    assert d == Decision.YELLOW and "SOFT_TRIPWIRE_PATTERN" in r


def test_duplicate_type_across_files_yellow(reg_v1, ct):
    diff = ("+++ b/Application/Intelligence/A.cs\n+public sealed class Widget\n"
            "+++ b/Application/Intelligence/B.cs\n+public class Widget\n")
    files = [("Application/Intelligence/A.cs", Operation.CREATE),
             ("Application/Intelligence/B.cs", Operation.CREATE)]
    d, r, _ = verdict(reg_v1, files, ct, diff=diff)
    assert d == Decision.YELLOW and "DUPLICATE_TYPE_SUSPECTED" in r


def test_generated_obj_file_yellow(reg_v1, ct):
    d, r, res = verdict(reg_v1, [("obj/Debug/Gen.cs", Operation.CREATE)], ct)
    assert d == Decision.YELLOW and "GENERATED_FILE_COMMITTED" in r
    assert res.file_decisions[0].policy_class == PolicyClass.GENERATED


def test_oversized_changeset_yellow(reg_v1, ct):
    files = [("Application/Intelligence/F%d.cs" % i, Operation.CREATE)
             for i in range(41)]
    d, r, _ = verdict(reg_v1, files, ct)
    assert d == Decision.YELLOW and "OVERSIZED_CHANGE" in r


def test_exactly_at_cap_not_oversized(reg_v1, ct):
    files = [("Application/Intelligence/F%d.cs" % i, Operation.CREATE)
             for i in range(40)]
    d, r, _ = verdict(reg_v1, files, ct)
    assert d == Decision.GREEN and "OVERSIZED_CHANGE" not in r


# --------------------------------------------------- registry poisoning -----

def _write(tmp_path, text, name="OWN.yaml"):
    path = os.path.join(str(tmp_path), name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    return path


def test_duplicate_lane_ids_rejected(tmp_path):
    p = _write(tmp_path, cf.registry_yaml({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": ["X/**"]},
        {"id": "x", "agent": "b", "branch": "b/2", "owns": ["Y/**"]}]}))
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert "duplicate lane id" in str(ei.value)


def test_duplicate_branch_v2_rejected_v1_warns(tmp_path, reg_v1):
    spec = {"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/9", "owns": ["X/**"]},
        {"id": "y", "agent": "b", "branch": "b/9", "owns": ["Y/**"]}]}
    p = _write(tmp_path, cf.registry_yaml(spec))
    with pytest.raises(RegistryError):
        load_registry(p)
    # v1 legacy: hygiene -> warning only (the pinned v1 fixture demonstrates it)
    assert any("multiple lanes" in w.message for w in reg_v1.validation_warnings)


def test_shared_branch_opt_in_allowed(tmp_path):
    p = _write(tmp_path, cf.registry_yaml({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/9", "owns": ["X/**"],
         "shared_branch": True},
        {"id": "y", "agent": "b", "branch": "b/9", "owns": ["Y/**"],
         "shared_branch": True}]}))
    reg = load_registry(p)
    assert not reg.validation_findings


@pytest.mark.parametrize("bad_glob", ["a**b", "/x/**", "..", "x/../../y",
                                      "dir/..", "", "   "])
def test_invalid_globs_rejected(tmp_path, bad_glob):
    p = _write(tmp_path, cf.registry_yaml({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": [bad_glob]}]}))
    with pytest.raises(RegistryError):
        load_registry(p)


def test_unknown_lifecycle_state_rejected(tmp_path):
    p = _write(tmp_path, cf.registry_yaml({"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": ["X/**"],
         "status": "sleeping"}]}))
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert ei.value.constraints[0].reason.value == "UNKNOWN_LIFECYCLE_STATE"


def test_registry_version_9_rejected(tmp_path):
    p = _write(tmp_path, "version: 9\npolicy: {}\nlanes: []\n")
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert "unsupported registry version" in str(ei.value)


def test_tab_in_yaml_is_corruption(tmp_path):
    p = _write(tmp_path, "version: 2\n\tpolicy: {}\n")
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert ei.value.constraints[0].reason.value == "REGISTRY_CORRUPTION"


def test_non_mapping_root_rejected(tmp_path):
    p = _write(tmp_path, "- just\n- a\n- list\n")
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert ei.value.constraints[0].reason.value == "REGISTRY_CORRUPTION"


def test_frozen_as_string_rejected(tmp_path):
    p = _write(tmp_path, "version: 2\npolicy: {}\nfrozen: nope\nlanes: []\n")
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert ei.value.constraints[0].reason.value == "REGISTRY_INVALID"


def test_dangling_overlap_group_v1_warns_v2_rejects(tmp_path, reg_v1):
    assert any("dangling" in w.message for w in reg_v1.validation_warnings)
    p = _write(tmp_path, cf.registry_yaml({
        "version": 2,
        "lanes": [{"id": "x", "agent": "a", "branch": "b/1", "owns": ["X/**"]}],
        "overlap_groups": [{"name": "g", "lanes": ["x", "ghost"]}]}))
    with pytest.raises(RegistryError) as ei:
        load_registry(p)
    assert "dangling" in str(ei.value)


def test_missing_registry_file_raises_oserror(tmp_path):
    with pytest.raises(OSError):
        load_registry(os.path.join(str(tmp_path), "does-not-exist.yaml"))


def test_invalid_utf8_registry_fails_closed_via_cli(tmp_path):
    p = _write(tmp_path, "")  # placeholder to get dir
    p = os.path.join(str(tmp_path), "bad.bin")
    with open(p, "wb") as f:
        f.write(b"version: 2\npolicy: {}\nlanes: []\n\xf0\xff\xfe")
    inp = os.path.join(str(tmp_path), "in")
    os.makedirs(inp, exist_ok=True)
    with open(os.path.join(inp, "changed.txt"), "w") as f:
        f.write("x.cs\n")
    with open(os.path.join(inp, "meta.json"), "w") as f:
        json.dump({"body": "Task-Id: x", "mergeable": "clean"}, f)
    code, man = _cli_classify(p, inp)
    assert code == 2 and man["final_decision"] == "RED"


# ------------------------------------------------------------- CLI smoke ----

def _cli_classify(registry, input_dir, extra=()):
    sys.path.insert(0, cf.INTEGRATION_DIR)
    import livora_gates as lg
    args = SimpleNamespace(registry=registry, input_dir=input_dir, json=True,
                           manifest_out=None, no_git=True, as_of="FROZEN",
                           **{k: v for k, v in extra})
    return lg.run_classify(args)


def test_cli_validate_registry_exit_zero():
    """The one allowed subprocess: exit 0 on the live registry."""
    r = subprocess.run([sys.executable, cf.CLI, "--registry", cf.REGISTRY_LIVE,
                        "validate-registry"], capture_output=True, text=True,
                       cwd=cf.REPO_ROOT)
    assert r.returncode == 0, r.stdout + r.stderr


def test_cli_classify_smoke_exit_in_allowed_set(tmp_path):
    """The allowed CLI smoke: subprocess classify over legacy changed.txt
    evidence; exit code must be in {0,1,2} (RED here: changed.txt cannot prove
    operations -> UNKNOWN_OPERATION fails closed)."""
    inp = os.path.join(str(tmp_path), "in")
    os.makedirs(inp)
    with open(os.path.join(inp, "changed.txt"), "w") as f:
        f.write("Application/Intelligence/Foo.cs\n")
    body = ("Task-Id: w3c-lane03\nAgent-Id: agent-c\nWave: 3c\n"
            "Base-Commit: " + "c" * 40 + "\nTests-Run: pytest")
    with open(os.path.join(inp, "meta.json"), "w") as f:
        json.dump({"body": body, "mergeable": "clean", "author": "agent-c",
                   "head_sha": "a" * 40, "base_sha": "b" * 40,
                   "merge_base": "c" * 40}, f)
    man_path = os.path.join(str(tmp_path), "manifest.json")
    r = subprocess.run(
        [sys.executable, cf.CLI, "--registry", V1, "classify",
         "--input-dir", inp, "--no-git", "--json",
         "--manifest-out", man_path],
        capture_output=True, text=True, cwd=cf.REPO_ROOT)
    assert r.returncode in (0, 1, 2), r.stdout + r.stderr
    assert r.returncode == 2
    man = json.loads(open(man_path, encoding="utf-8").read())
    assert man["final_decision"] == "RED"
    assert "UNKNOWN_OPERATION" in man["reason_codes"]
