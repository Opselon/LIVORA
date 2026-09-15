"""Property-based invariants of the LIVORA governance engine (hypothesis).

These encode the *algebra* of the decision layer, not individual verdicts:
normalization is idempotent and fail-closed, per-file decisions are
independent, YAML rule order cannot matter, protected families are
case-insensitive, exclusive conflicts can never stay GREEN, and severity
folding is monotone.

Hypothesis tests deliberately avoid pytest fixtures (they import the plain
builder functions from conftest instead); fixture-driven variants live in the
non-given tests at the bottom.
"""
from __future__ import annotations

import os
import re
import tempfile

from hypothesis import HealthCheck, assume, given, settings, strategies as st

import conftest as cf
from governance import git_evidence as gite
from governance.engine import REASON_SEVERITY, GovernanceInput, classify, fold_decisions
from governance.model import (Decision, HardConstraint, Operation, PolicyClass,
                              Reason)
from governance.paths import (Matcher, PathError, normalize_repo_path,
                              patterns_conflict)
from governance.registry import (RegistryError, canonical_task_hash,
                                 load_registry)
from governance.report import build_manifest, dump_json
from governance.resolver import build_trie, resolve_path

settings.register_profile(
    "gov", max_examples=40, deadline=None, derandomize=True,
    suppress_health_check=[HealthCheck.function_scoped_fixture,
                           HealthCheck.too_slow])
settings.load_profile("gov")

# text without control characters (Cc covers NUL and all ASCII/Unicode controls)
SAFE_TEXT = st.text(alphabet=st.characters(exclude_categories=("Cc",)),
                    min_size=1, max_size=60)
# hashable text additionally avoids lone surrogates: canonical_task_hash's
# utf-8 encode explodes on them (see ENGINE xfail in test_adversarial.py)
HASH_TEXT = st.text(alphabet=st.characters(exclude_categories=("Cc", "Cs")),
                    min_size=1, max_size=80)

LANE = "w3c-lane03"
LANE_OWNED = ["Application/Intelligence/Foo.cs",
              "Application/Intelligence/Sub/Bar.cs",
              "Infrastructure/IntelligenceProviders/Wave3c/Baz.cs"]
SAFE_EXTRA = ["Application/Intelligence/Provider%d.cs",
              "Application/Intelligence/Validator%d.cs",
              "Infrastructure/IntelligenceProviders/Wave3c/Extra%d.cs"]
FROZEN_SAMPLES = ["Application/Abstractions/IAi.cs",
                  "Domain/Enums/Status.cs",
                  "Application/Rules/RuleEngine.cs",
                  "Tests/Tests/TestFakes.cs"]

_V1 = None


def v1_registry():
    """Module-cached load of the pinned v1 registry snapshot."""
    global _V1
    if _V1 is None:
        _V1 = load_registry(cf.REGISTRY_V1)
    return _V1


def _gi(reg, files, contract, meta_author="agent-c"):
    return GovernanceInput(
        registry=reg, changeset=list(files), contract=dict(contract),
        meta={"mergeable": "clean", "author": meta_author},
        branch_evidence=cf.branch_evidence(), diff_text="")


def _canon(text: str) -> str:
    return re.sub(r"\s+", " ", text.strip()).casefold()


# --------------------------------------------------------------- (a) + (b) --

@given(SAFE_TEXT)
def test_normalization_is_idempotent(raw):
    try:
        once = normalize_repo_path(raw)
    except PathError:
        return  # rejected: nothing to compare
    again = normalize_repo_path(once)
    assert again == once, f"{raw!r}: {once!r} -> {again!r}"


@given(SAFE_TEXT)
def test_normalization_output_is_canonical(raw):
    try:
        out = normalize_repo_path(raw)
    except PathError:
        return
    assert out and not out.startswith("/") and not out.endswith("/")
    assert "//" not in out
    segs = out.split("/")
    assert "." not in segs and ".." not in segs


@given(SAFE_TEXT)
def test_normalization_never_crashes_on_control_free_text(raw):
    # fail-closed contract: the only allowed outcomes are a normalized string
    # or PathError. Anything else escaping is an engine defect.
    try:
        out = normalize_repo_path(raw)
    except PathError:
        return
    assert isinstance(out, str)


@given(st.text(alphabet=st.characters(exclude_categories=("Cs",)),
               min_size=0, max_size=30))
def test_normalization_rejects_control_characters(raw):
    bad = raw + "\t"  # tab is Cc: must never pass normalization
    try:
        normalize_repo_path(bad)
    except PathError:
        return
    raise AssertionError(f"control character slipped through: {bad!r}")


# ------------------------------------------------------------------- (c) --

@given(st.integers(min_value=0, max_value=2),
       st.integers(min_value=0, max_value=2))
def test_unrelated_owned_file_cannot_change_another_files_decision(i_extra, lane_idx):
    """Per-file independence: adding an unrelated file the lane owns never
    changes the first file's FileDecision payload, whatever it is."""
    reg = v1_registry()
    lane = reg.lanes[LANE]
    contract = cf.contract_for(lane)
    target = gite.ChangedFile(path=LANE_OWNED[lane_idx], operation=Operation.CREATE)
    solo = classify(_gi(reg, [target], contract))
    s = [f.to_dict() for f in solo.file_decisions
         if f.path == LANE_OWNED[lane_idx]][0]
    for k in range(i_extra + 1):
        extra = gite.ChangedFile(path=SAFE_EXTRA[k % len(SAFE_EXTRA)] % k,
                                 operation=Operation.CREATE)
        pair = classify(_gi(reg, [target, extra], contract))
        p = [f.to_dict() for f in pair.file_decisions
             if f.path == LANE_OWNED[lane_idx]][0]
        assert s == p, f"{extra.path} leaked into {target.path}'s decision"


@given(st.permutations(list(range(len(LANE_OWNED)))))
def test_changeset_order_cannot_change_per_file_verdicts(order):
    reg = v1_registry()
    contract = cf.contract_for(reg.lanes[LANE])
    files = [gite.ChangedFile(path=LANE_OWNED[i], operation=Operation.CREATE)
             for i in order]
    res = classify(_gi(reg, files, contract))
    got = sorted((f.path, f.policy_class.value, f.decision.value)
                 for f in res.file_decisions)
    want = sorted((LANE_OWNED[i], "lane", "GREEN") for i in order)
    assert got == want


# ------------------------------------------------------------------- (d) --

DISJOINT = [("lane-a", "Alpha/**"), ("lane-b", "Beta/**"),
            ("lane-c", "Gamma/**"), ("lane-d", "Delta/**")]


def _write_v2(tmpdir, perm, name):
    lanes = [dict(id=DISJOINT[k][0], agent="agent-" + DISJOINT[k][0][-1],
                  branch="lane/" + DISJOINT[k][0][-1] + "-*",
                  owns=[DISJOINT[k][1]]) for k in perm]
    spec = {"version": 2, "lanes": lanes}
    path = os.path.join(tmpdir, name)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(cf.registry_yaml(spec))
    return path


def _manifest_for(path):
    reg = load_registry(path)
    lane = reg.lanes["lane-a"]
    contract = {"task_id": lane.id, "agent_id": lane.agent, "wave": "test",
                "base_commit": "c" * 40, "tests_run": "pytest",
                "task_hash": lane.task_hash,
                "task_revision": str(lane.task_revision),
                "lane_branch": "lane/a-01", "owned_scope": "Alpha/**"}
    gi = _gi(reg, [gite.ChangedFile(path="Alpha/Foo.cs",
                                    operation=Operation.CREATE)], contract,
             meta_author="agent-a")
    man = build_manifest(gi, classify(gi), "repo", "b" * 40, "a" * 40,
                         "c" * 40, 1)
    # registry_sha256 is a content hash of a DIFFERENT file: shuffling lane
    # blocks legitimately changes it. Everything else must be byte-identical.
    man.pop("registry_sha256")
    return dump_json(man)


@given(st.permutations(list(range(len(DISJOINT)))))
def test_yaml_rule_order_cannot_change_the_manifest(perm):
    tmp = tempfile.mkdtemp()
    reference = _manifest_for(_write_v2(tmp, [0, 1, 2, 3], "ref.yaml"))
    shuffled = _manifest_for(_write_v2(tmp, perm, "shuf.yaml"))
    assert shuffled == reference, "YAML lane order leaked into the verdict"


# ------------------------------------------------------------------- (e) --

@given(st.sampled_from(FROZEN_SAMPLES),
       st.sampled_from(["identity", "upper", "lower", "swapcase"]))
def test_protected_families_match_case_insensitively(sample, variant):
    path = sample if variant == "identity" else getattr(str, variant)(sample)
    reg = v1_registry()
    res = resolve_path(path, reg, build_trie(reg))
    assert res.policy_class == PolicyClass.FROZEN, \
        f"{path!r} ({variant}) escaped the frozen family"


@given(st.sampled_from(FROZEN_SAMPLES))
def test_frozen_case_variants_classify_red(sample):
    reg = v1_registry()
    contract = cf.contract_for(reg.lanes[LANE])
    classes = []
    for p in (sample, sample.upper()):
        res = classify(_gi(reg, [gite.ChangedFile(path=p,
                                                  operation=Operation.MODIFY)],
                           contract))
        assert res.decision == Decision.RED
        classes.append(res.file_decisions[0].policy_class)
    assert classes[0] == classes[1] == PolicyClass.FROZEN


# ------------------------------------------------------------------- (f) --

CONFLICTING_PATTERNS = ["Alpha/**", "Alpha/Sub/**", "**", "Alph*/**",
                        "Alpha/Foo.cs"]


@given(st.sampled_from(CONFLICTING_PATTERNS))
def test_injected_conflicting_exclusive_claim_cannot_keep_green(pattern):
    """A GREEN case + a conflicting exclusive claim -> RegistryError at load,
    or a non-GREEN decision. Silence is not an option."""
    assume(patterns_conflict("Alpha/**", pattern))  # premise must hold
    tmp = tempfile.mkdtemp()
    spec = {"version": 2, "lanes": [
        {"id": "lane-a", "agent": "agent-a", "branch": "lane/a-*",
         "owns": ["Alpha/**"]},
        {"id": "lane-z", "agent": "agent-z", "branch": "lane/z-*",
         "owns": [pattern]}]}
    path = os.path.join(tmp, "OWN.yaml")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(cf.registry_yaml(spec))
    try:
        reg = load_registry(path)
    except RegistryError:
        return  # rejected at load — the fail-closed outcome we want
    lane = reg.lanes["lane-a"]
    contract = {"task_id": lane.id, "agent_id": lane.agent, "wave": "test",
                "base_commit": "c" * 40, "tests_run": "pytest",
                "task_hash": lane.task_hash,
                "task_revision": str(lane.task_revision),
                "lane_branch": "lane/a-01", "owned_scope": "Alpha/**"}
    res = classify(_gi(reg, [gite.ChangedFile(path="Alpha/Foo.cs",
                                              operation=Operation.CREATE)],
                       contract, meta_author="agent-a"))
    assert res.decision != Decision.GREEN, \
        f"injected exclusive claim [{pattern}] kept the GREEN case GREEN"


# ------------------------------------------------------------------- (g) --

@given(st.integers(min_value=0, max_value=8))
def test_frozen_violation_is_monotone_under_unrelated_safe_files(n_safe):
    reg = v1_registry()
    contract = cf.contract_for(reg.lanes[LANE])
    files = [gite.ChangedFile(path="Application/Abstractions/IFoo.cs",
                              operation=Operation.MODIFY)]
    files += [gite.ChangedFile(path="Application/Intelligence/S%d.cs" % i,
                               operation=Operation.CREATE)
              for i in range(n_safe)]
    res = classify(_gi(reg, files, contract))
    assert res.decision == Decision.RED
    frozen_fd = [f for f in res.file_decisions
                 if f.path == "Application/Abstractions/IFoo.cs"][0]
    assert frozen_fd.decision == Decision.RED


# ------------------------------------------------------------------- (h) --

@given(st.lists(st.sampled_from(list(Reason)), min_size=0, max_size=8),
       st.integers(min_value=0, max_value=8))
def test_fold_decisions_is_monotone_in_the_finding_set(reasons, cut):
    """severity(fold(subset)) <= severity(fold(superset)), always."""
    cons = [HardConstraint(reason=r, severity=REASON_SEVERITY[r], message="m")
            for r in reasons]
    assert fold_decisions(cons[:cut]).severity <= fold_decisions(cons).severity


# ------------------------------------------------------------------- (i) --

@given(st.sampled_from(FROZEN_SAMPLES))
def test_specificity_exact_gt_multiseg_dir_glob_gt_root_glob_gt_universal(path):
    segs = path.split("/")
    exact = Matcher(path)
    deep_dir = Matcher("/".join(segs[:-1]) + "/**")
    root_dir = Matcher(segs[0] + "/**")
    universal = Matcher("**")
    assert exact.specificity() > deep_dir.specificity() > root_dir.specificity() \
        > universal.specificity()


# ------------------------------------------------------------------- (j) --

@given(HASH_TEXT)
def test_task_hash_is_deterministic_and_whitespace_canonical(text):
    h1 = canonical_task_hash(text)
    assert h1 == canonical_task_hash(text) and len(h1) == 64
    assume(bool(text.strip()))
    padded = "  \t" + re.sub(r"\s+", " ", text.strip()) + " \n"
    assert canonical_task_hash(padded) == h1


@given(HASH_TEXT, HASH_TEXT)
def test_task_hash_matches_exactly_when_canonical_text_matches(a, b):
    """Deterministic AND collision-free on the canonicalization it defines:
    equal hashes iff whitespace-collapsed/casefolded texts are equal."""
    assert (canonical_task_hash(a) == canonical_task_hash(b)) == (_canon(a) == _canon(b))


# ------------------------------------------------- soundness of conflict scan

_PATTERN_SEGS = st.sampled_from(["a", "b", "*", "**", "a*", "?"])


@st.composite
def _patterns(draw):
    n = draw(st.integers(min_value=1, max_value=3))
    return "/".join(draw(_PATTERN_SEGS) for _ in range(n))


@st.composite
def _paths(draw):
    n = draw(st.integers(min_value=1, max_value=3))
    return "/".join(draw(st.sampled_from(["a", "b", "c"])) for _ in range(n))


@given(_patterns(), _patterns(), _paths())
def test_patterns_conflict_never_under_reports(p1, p2, probe):
    """If one concrete path matches BOTH patterns, the conflict scan must say
    True. Over-reporting is allowed (conservative); silence is not."""
    try:
        m1, m2 = Matcher(p1), Matcher(p2)
    except Exception:
        assume(False)
        return
    if m1.match(probe) and m2.match(probe):
        assert patterns_conflict(p1, p2), \
            f"{p1} and {p2} both match {probe} yet conflict==False"


# ------------------------------------------- fixture-driven support checks --

def test_green_baseline_proves_every_predicate(reg_v1, mk_contract, mk_gi):
    """Sanity anchor for the properties above: the plain lane case is GREEN
    with ALL green predicates proven (not merely absence of findings)."""
    res = classify(mk_gi(reg_v1, [(LANE_OWNED[0], "create")],
                         mk_contract(reg_v1.lanes[LANE])))
    assert res.decision == Decision.GREEN
    assert res.green_predicates and all(res.green_predicates.values())
