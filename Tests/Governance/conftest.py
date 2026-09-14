"""Shared fixtures + builders for the LIVORA governance test suite.

Pure in-process tests: `scripts/integration` is put on sys.path so the
governance package imports exactly the way the CLI imports it. The only
subprocess use in this suite is the CLI smoke test (test_adversarial.py).

Registry note: the task brief pins ".github/OWNERSHIP.yaml (v1)" semantics
(3 hygiene warnings, lane w3c-lane03 planned owning Application/Intelligence/**,
lead owning '**'). A concurrent wave is upgrading the live registry in this
worktree to v2 mid-session, so the v1 semantics are pinned to the byte snapshot
in fixtures/OWNERSHIP.v1.yaml (git HEAD at suite authoring time). The live
registry is still exercised: test_live_registry_loads_valid + the CLI smoke.
"""
from __future__ import annotations

import os
import sys

import pytest

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, os.pardir, os.pardir))
INTEGRATION_DIR = os.path.join(REPO_ROOT, "scripts", "integration")

if INTEGRATION_DIR not in sys.path:
    sys.path.insert(0, INTEGRATION_DIR)

from governance import git_evidence as gite                      # noqa: E402
from governance.engine import GovernanceInput, classify          # noqa: E402
from governance.model import Operation                           # noqa: E402
from governance.registry import load_registry                    # noqa: E402

REGISTRY_V1 = os.path.join(HERE, "fixtures", "OWNERSHIP.v1.yaml")
REGISTRY_LIVE = os.path.join(REPO_ROOT, ".github", "OWNERSHIP.yaml")
CLI = os.path.join(INTEGRATION_DIR, "livora_gates.py")

# ---------------------------------------------------------------- builders --

def _lane_own_scope(lane):
    return ",".join(m.pattern for m in lane.owns) or None


def contract_for(lane, **over):
    """Complete, valid PR-body contract for `lane` (passes every stage unless
    an override says otherwise). None values are dropped -> field 'missing'."""
    c = {
        "task_id": lane.id,
        "agent_id": lane.agent,
        "wave": lane.wave,
        "base_commit": "c" * 40,
        "tests_run": "pytest suites/governance",
        "task_hash": lane.task_hash,
        "task_revision": str(lane.task_revision),
        "lane_branch": lane.branch,
        "owned_scope": _lane_own_scope(lane),
        "drift_ack": None,
    }
    c.update(over)
    return {k: v for k, v in c.items() if v is not None}


def branch_evidence(**over):
    """Clean branch evidence by default; override single fields to break it."""
    kw = dict(
        head_sha="a" * 40, base_sha="b" * 40, merge_base="c" * 40,
        commits_behind=0, commits_ahead=1, branch_exists=True,
        is_base_branch=False, force_push_suspect=False,
        architecture_changed_after_fork=False,
    )
    kw.update(over)
    kw.setdefault("gaps", [])
    return gite.BranchEvidence(**kw)


def changeset(*specs):
    """ChangedFile list from tuples: (path, op) or (path, op, old_path)."""
    out = []
    for s in specs:
        if isinstance(s, gite.ChangedFile):
            out.append(s)
            continue
        path, op = s[0], s[1]
        old = s[2] if len(s) > 2 else None
        operation = op if isinstance(op, Operation) else Operation(op)
        out.append(gite.ChangedFile(path=path, old_path=old, operation=operation))
    return out


def input_obj(registry, files, contract, be="default", meta=None, diff="",
              added=0, deleted=0, binary=frozenset(), timestamp="FROZEN"):
    branch = branch_evidence() if be == "default" else be
    return GovernanceInput(
        registry=registry, changeset=changeset(*files), contract=dict(contract),
        meta=dict(meta or {"mergeable": "clean", "author": "test-actor"}),
        branch_evidence=branch, diff_text=diff, added=added, deleted=deleted,
        binary_paths=binary, timestamp=timestamp)


def reasons_of(res):
    """All reason codes fired anywhere (PR-level + per-file)."""
    rs = {c.reason.value for c in res.hard_constraints}
    for fd in res.file_decisions:
        rs |= {c.reason.value for c in fd.hard_constraints}
    return sorted(rs)


def classify_input(registry, files, contract, be="default", meta=None, diff="",
                   added=0, deleted=0):
    gi = input_obj(registry, files, contract, be=be, meta=meta, diff=diff,
                   added=added, deleted=deleted)
    return classify(gi), gi


# --------------------------------------------------- v2 registry writer -----

_YAML = object()  # sentinel: not-set vs explicit None

def registry_yaml(spec):
    """Render a minimal OWNERSHIP.yaml from a spec dict."""
    lines = ["version: %d" % spec.get("version", 2), "policy:"]
    pol = {"auto_merge_max_changed_files": 40, "stale_commits_threshold": 25}
    pol.update(spec.get("policy") or {})
    for k, v in pol.items():
        lines.append("  %s: %s" % (k, _scalar(v)))
    for key, blk in (("frozen", "frozen:"), ("architecture_owned", "architecture_owned:")):
        items = spec.get(key if key != "architecture_owned" else "architecture", _YAML)
        if items is _YAML:
            items = ["Application/Abstractions/**"] if key == "frozen" else [".github/**", "scripts/**"]
        lines.append(blk)
        lines.append("  owner: %s" % spec.get(key + ".owner", "lead"))
        lines.append("  paths:")
        for p in items:
            lines.append("    - %s" % _scalar(p))
    lines.append("integration_owned:")
    for p in spec.get("integration", ["MauiProgram.cs"]):
        lines.append("  - %s" % _scalar(p))
    if "generated" in spec:
        lines.append("generated:")
        lines.append("  paths:")
        for p in spec["generated"] or []:
            lines.append("    - %s" % _scalar(p))
    if spec.get("raw_top"):
        lines.append(spec["raw_top"].rstrip("\n"))
    lines.append("lanes:")
    for lane in spec.get("lanes", []):
        lines.append("  - id: %s" % lane["id"])
        lines.append("    agent: %s" % lane["agent"])
        lines.append("    wave: %s" % _scalar(lane.get("wave", "test")))
        lines.append("    branch: %s" % _scalar(lane["branch"]))
        lines.append("    task: %s" % _scalar(lane.get("task", "task " + lane["id"])))
        lines.append("    status: %s" % lane.get("status", "active"))
        if "exclusive" in lane:
            lines.append("    exclusive: %s" % ("true" if lane["exclusive"] else "false"))
        if "shared_branch" in lane:
            lines.append("    shared_branch: %s" % ("true" if lane["shared_branch"] else "false"))
        if "task_revision" in lane:
            lines.append("    task_revision: %d" % lane["task_revision"])
        if "expiry" in lane:
            lines.append("    expiry: %s" % _scalar(lane["expiry"]))
        lines.append("    owns:")
        for p in lane.get("owns", []):
            lines.append("      - %s" % _scalar(p))
    for grp in spec.get("overlap_groups", []):
        lines.append("overlap_groups:")
        break
    if spec.get("overlap_groups"):
        for grp in spec["overlap_groups"]:
            lines.append("  - name: %s" % grp["name"])
            lines.append("    lanes: [%s]" % ", ".join(grp["lanes"]))
            if grp.get("note"):
                lines.append("    note: %s" % _scalar(grp["note"]))
    return "\n".join(lines) + "\n"


def _scalar(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, (int, float)):
        return str(v)
    import json as _json
    return _json.dumps(v, ensure_ascii=False)


def make_lane_registry_obj(tmp_path, spec, name="OWNERSHIP.test.yaml"):
    """Write a minimal OWNERSHIP.yaml from `spec` and return (path, Registry)."""
    path = os.path.join(str(tmp_path), name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(registry_yaml(spec))
    return path


# ------------------------------------------------------------- fixtures -----

@pytest.fixture(scope="module")
def reg_v1():
    """Pinned v1 registry snapshot (loads valid with exactly 3 warnings)."""
    return load_registry(REGISTRY_V1)


@pytest.fixture(scope="module")
def reg_live():
    """The actual live .github/OWNERSHIP.yaml in this worktree."""
    return load_registry(REGISTRY_LIVE)


@pytest.fixture
def lane(reg_v1):
    """The v1 fixture lane used by most tests: w3c-lane03, owns
    Application/Intelligence/** (planned => claim-bearing + delivering)."""
    return reg_v1.lanes["w3c-lane03"]


@pytest.fixture
def mk_ch():
    return changeset


@pytest.fixture
def mk_contract():
    return contract_for


@pytest.fixture
def mk_be():
    return branch_evidence


@pytest.fixture
def mk_gi():
    return input_obj


@pytest.fixture
def gov_classify():
    return classify_input


@pytest.fixture
def reasons():
    return reasons_of


@pytest.fixture
def make_lane_registry(tmp_path):
    def _make(spec, name="OWNERSHIP.test.yaml"):
        path = make_lane_registry_obj(tmp_path, spec, name=name)
        return path
    return _make


@pytest.fixture
def lane_registry(make_lane_registry):
    """make_lane_registry(spec) -> loaded Registry (convenience wrapper)."""
    def _make(spec, name="OWNERSHIP.test.yaml"):
        path = make_lane_registry(spec, name=name)
        return load_registry(path)
    return _make
