"""Determinism contract: identical inputs -> identical bytes, every time.

The manifest is a CI artifact: consumers diff it between runs. That only works
if classification reads nothing but its inputs (no clock, no dict order, no
YAML order) and equal registries hash equally.
"""
from __future__ import annotations

import hashlib
import os

import pytest

import conftest as cf
from governance.engine import (GovernanceInput, change_magnitude, classify,
                               fold_decisions)
from governance.model import (Decision, HardConstraint, Lifecycle, Operation,
                              Reason, Severity, TRANSITIONS,
                              lifecycle_transition_ok)
from governance.registry import canonical_task_hash, load_registry
from governance.report import build_manifest, dump_json, render_human


def _manifest(reg, files, contract, meta=None):
    gi = GovernanceInput(registry=reg, changeset=cf.changeset(*files),
                         contract=dict(contract),
                         meta=dict(meta or {"mergeable": "clean",
                                            "author": "agent-c"}),
                         branch_evidence=cf.branch_evidence(), diff_text="")
    return dump_json(build_manifest(gi, classify(gi), "repo", "b" * 40,
                                    "a" * 40, "c" * 40, 7))


def test_same_input_classifies_to_identical_manifest_bytes(reg_v1, lane,
                                                           mk_contract):
    files = [("Application/Intelligence/Foo.cs", Operation.CREATE),
             ("Application/Intelligence/Sub/Bar.cs", Operation.MODIFY),
             ("Application/Abstractions/IFoo.cs", Operation.DELETE)]
    contract = mk_contract(lane)
    first = _manifest(reg_v1, files, contract)
    for _ in range(4):
        assert _manifest(reg_v1, files, contract) == first, \
            "classify() is not a pure function of its inputs"
    assert '"final_decision": "RED"' in first


def test_reload_of_same_registry_file_reuses_identical_verdict(reg_v1,
                                                               tmp_path,
                                                               mk_contract):
    """A byte-identical copy of the registry must produce byte-identical
    manifests (the only difference allowed is nothing at all)."""
    src = open(cf.REGISTRY_V1, "rb").read()
    dst = os.path.join(str(tmp_path), "copy.yaml")
    with open(dst, "wb") as f:
        f.write(src)
    reg2 = load_registry(dst)
    files = [("Application/Intelligence/Foo.cs", Operation.CREATE)]
    m1 = _manifest(reg_v1, files, mk_contract(reg_v1.lanes["w3c-lane03"]))
    m2 = _manifest(reg2, files, mk_contract(reg2.lanes["w3c-lane03"]))
    assert m1 == m2
    assert reg2.sha256 == reg_v1.sha256 == hashlib.sha256(src).hexdigest()


def test_swapped_lane_order_registries_agree_on_verdict(tmp_path):
    """Two equivalent v2 registries with the lane blocks swapped in file order
    produce identical verdicts (registry_sha256 excluded: different bytes,
    equal policy)."""
    lanes = [
        {"id": "lane-a", "agent": "agent-a", "branch": "lane/a-*",
         "owns": ["Alpha/**"]},
        {"id": "lane-b", "agent": "agent-b", "branch": "lane/b-*",
         "owns": ["Beta/**"]},
        {"id": "lane-c", "agent": "agent-c", "branch": "lane/c-*",
         "owns": ["Gamma/**"]}]
    path_ref = cf.make_lane_registry_obj(tmp_path, {"version": 2, "lanes": lanes},
                                         name="ref.yaml")
    path_swap = cf.make_lane_registry_obj(tmp_path, {"version": 2,
                                                     "lanes": list(reversed(lanes))},
                                          name="swap.yaml")
    ref, swap = load_registry(path_ref), load_registry(path_swap)
    for lane_id in ("lane-a", "lane-b", "lane-c"):
        contract = cf.contract_for(ref.lanes[lane_id],
                                   lane_branch="lane/%s-01" % lane_id[-1],
                                   owned_scope="Alpha/**" if lane_id == "lane-a"
                                   else "Beta/**" if lane_id == "lane-b"
                                   else "Gamma/**")
        contract_swap = cf.contract_for(swap.lanes[lane_id],
                                        lane_branch=contract["lane_branch"],
                                        owned_scope=contract["owned_scope"])
        files = [({"lane-a": "Alpha/F.cs", "lane-b": "Beta/F.cs",
                   "lane-c": "Gamma/F.cs"}[lane_id], Operation.CREATE)]
        m_ref = hashlib.sha256(_dump_without_sha(
            _manifest_obj(ref, files, contract,
                          {"mergeable": "clean", "author": "agent-" + lane_id[-1]})).encode())
        m_swap = hashlib.sha256(_dump_without_sha(
            _manifest_obj(swap, files, contract_swap,
                          {"mergeable": "clean", "author": "agent-" + lane_id[-1]})).encode())
        assert m_ref.digest() == m_swap.digest(), \
            f"lane {lane_id}: YAML block order changed the verdict"


def _manifest_obj(reg, files, contract, meta):
    gi = GovernanceInput(registry=reg, changeset=cf.changeset(*files),
                         contract=dict(contract), meta=dict(meta),
                         branch_evidence=cf.branch_evidence(), diff_text="")
    return build_manifest(gi, classify(gi), "repo", "b" * 40, "a" * 40,
                          "c" * 40, 7)


def _dump_without_sha(man):
    man = dict(man)
    man.pop("registry_sha256")
    return dump_json(man)


def test_registry_sha256_is_stable_across_reloads_and_tracks_bytes(tmp_path):
    p = cf.make_lane_registry_obj(tmp_path, {"version": 2, "lanes": [
        {"id": "x", "agent": "a", "branch": "b/1", "owns": ["X/**"]}]},
        name="sha.yaml")
    h1 = load_registry(p).sha256
    h2 = load_registry(p).sha256
    assert h1 == h2 == hashlib.sha256(open(p, "rb").read()).hexdigest()
    with open(p, "a", encoding="utf-8", newline="\n") as f:
        f.write("\n# trailing comment changes the bytes\n")
    assert load_registry(p).sha256 != h1, \
        "sha256 must be content-addressed: audit trail depends on it"


def test_change_magnitude_is_bounded_and_monotone():
    assert change_magnitude(0, 0, 0, 0, 0) == 0.0
    prev_f = -1.0
    for files in (1, 2, 5, 10, 40, 100, 500, 10_000):
        cm = change_magnitude(files, 50, 10, 0, 0)
        assert 0.0 <= cm <= 1.0
        assert cm >= prev_f, "CM must not shrink when files grow"
        prev_f = cm
    prev_a = -1.0
    for lines in (0, 10, 100, 1_000, 10_000, 1_000_000):
        cm = change_magnitude(3, lines, 0, 0, 0)
        assert 0.0 <= cm <= 1.0
        assert cm >= prev_a, "CM must not shrink when additions grow"
        prev_a = cm
    assert change_magnitude(500, 10 ** 6, 10 ** 6, 99, 99) == 1.0  # saturates


def test_fold_decisions_truth_table():
    R = lambda: HardConstraint(Reason.FROZEN_CONTRACT_TOUCHED,
                               Severity.HARD_RED, "r")
    Y = lambda: HardConstraint(Reason.STALE_BASE, Severity.HARD_YELLOW, "y")
    S = lambda: HardConstraint(Reason.SOFT_TRIPWIRE_PATTERN, Severity.SOFT_RISK,
                               "s")
    assert fold_decisions([]) == Decision.GREEN
    assert fold_decisions([Y()]) == Decision.YELLOW
    assert fold_decisions([Y(), Y()]) == Decision.YELLOW
    assert fold_decisions([R()]) == Decision.RED
    assert fold_decisions([Y(), R()]) == Decision.RED
    assert fold_decisions([R(), Y()]) == Decision.RED       # order-insensitive
    assert fold_decisions([S()]) == Decision.GREEN          # soft never folds
    assert fold_decisions([S(), Y()]) == Decision.YELLOW
    assert fold_decisions([S(), R()]) == Decision.RED


# ----------------------------------------------------------- lifecycle -----

ALL_STATES = list(Lifecycle)


def test_lifecycle_all_legal_transitions_accepted():
    for src in ALL_STATES:
        for dst in TRANSITIONS[src]:
            assert lifecycle_transition_ok(src, dst), \
                f"{src.value}->{dst.value} is declared legal but rejected"


def test_lifecycle_illegal_transitions_rejected():
    for src in ALL_STATES:
        for dst in ALL_STATES:
            legal = dst in TRANSITIONS[src]
            assert lifecycle_transition_ok(src, dst) == legal, \
                f"{src.value}->{dst.value} misjudged"


def test_released_to_active_is_illegal_and_merged_cannot_reopen():
    assert not lifecycle_transition_ok(Lifecycle.RELEASED, Lifecycle.ACTIVE)
    assert not lifecycle_transition_ok(Lifecycle.MERGED, Lifecycle.ACTIVE)
    assert lifecycle_transition_ok(Lifecycle.MERGED, Lifecycle.RELEASED)
    for terminal in (Lifecycle.RELEASED, Lifecycle.SUPERSEDED,
                     Lifecycle.ABANDONED):
        assert TRANSITIONS[terminal] == set(), \
            f"{terminal.value} is terminal: no exits allowed"


def test_non_delivering_and_claim_bearing_sets_partition():
    """merged is claim-bearing but NOT delivering; released is neither."""
    from governance.model import CLAIM_BEARING, NON_DELIVERING
    assert Lifecycle.MERGED in CLAIM_BEARING
    assert Lifecycle.MERGED in NON_DELIVERING
    assert Lifecycle.RELEASED in NON_DELIVERING
    assert Lifecycle.RELEASED not in CLAIM_BEARING
    assert Lifecycle.ACTIVE not in NON_DELIVERING
    assert set(TRANSITIONS) == set(Lifecycle), "every state has a row"


# --------------------------------------------------- manifest plumbing -----

def test_dump_json_is_key_sorted_and_render_human_agrees(reg_v1, lane,
                                                          mk_contract):
    gi = GovernanceInput(registry=reg_v1,
                         changeset=cf.changeset(
                             ("Application/Abstractions/IFoo.cs",
                              Operation.MODIFY)),
                         contract=mk_contract(lane),
                         meta={"mergeable": "clean", "author": "agent-c"},
                         branch_evidence=cf.branch_evidence(), diff_text="")
    res = classify(gi)
    man = build_manifest(gi, res, "livora", "b" * 40, "a" * 40, "c" * 40, 12)
    dumped = dump_json(man)
    assert dumped == dump_json(dict(reversed(list(man.items())))), \
        "dump_json must sort keys, not preserve insertion order"
    human = render_human(man)
    assert human.startswith("GOVERNANCE VERDICT: RED")
    assert "FROZEN_CONTRACT_TOUCHED" in human
    assert man["final_decision"] == "RED"
    assert man["registry_sha256"] == reg_v1.sha256
    # per-file decisions are path-sorted in the manifest (stable for diffing)
    paths = [f["path"] for f in man["per_file_decisions"]]
    assert paths == sorted(paths)


def test_timestamp_is_supplied_never_read(reg_v1, lane, mk_contract):
    """Two classifications of the same input with different explicit
    timestamps differ ONLY in the timestamp field."""
    def dump(ts):
        gi = GovernanceInput(registry=reg_v1,
                             changeset=cf.changeset(
                                 ("Application/Intelligence/Foo.cs",
                                  Operation.CREATE)),
                             contract=mk_contract(lane),
                             meta={"mergeable": "clean", "author": "agent-c"},
                             branch_evidence=cf.branch_evidence(),
                             diff_text="", timestamp=ts)
        return dump_json(build_manifest(gi, classify(gi), "r", "b", "a", "c", 1))
    a, b = dump("2026-01-01T00:00:00Z"), dump("2026-02-02T00:00:00Z")
    assert a != b
    assert a.replace("2026-01-01T00:00:00Z", "X") == \
        b.replace("2026-02-02T00:00:00Z", "X")


def test_task_hash_never_crashes_on_any_str():
    canonical_task_hash("bad \ud800 surrogate")
