"""Unit tests for the engine's pure components and the resolver.

parse_pr_contract, relevance_level, _lane_branch_matches, fold severity map,
scan_added_lines, path glob semantics, Trie candidate completeness (must be a
superset of every brute-force-matching claim), and resolve_path over the real
pinned v1 registry + a lead-less v2 registry (for the UNOWNED class).
"""
from __future__ import annotations

import random

import pytest

import conftest as cf
from governance import git_evidence as gite
from governance.engine import (REASON_SEVERITY, YELLOW_REASONS,
                               GovernanceInput, _lane_branch_matches,
                               classify, parse_pr_contract, relevance_level,
                               scan_added_lines)
from governance.model import Decision, Operation, PolicyClass, Reason, Severity
from governance.paths import (Matcher, PathError, PatternError,
                              normalize_repo_path, patterns_conflict,
                              validate_pattern)
from governance.resolver import build_trie, resolve_path


# ------------------------------------------------------- parse_pr_contract --

FULL_BODY = "\n".join([
    "## Delivery",
    "Task-Id: w3c-lane03",
    "Agent-Id: agent-c",
    "Wave: 3c",
    "Base-Commit: " + "ab12cd34" + "0" * 32,
    "Owned-Scope: Application/Intelligence/**, Infrastructure/IntelligenceProviders/Wave3c/**",
    "Tests-Run: dotnet test --filter AI",
    "Task-Hash: " + "0" * 64,
    "Task-Revision: 3",
    "Lane-Branch: agent/w3c/lane03-01-ai",
    "Task-Drift-Ack: high impact re-scope",
])


def test_parse_pr_contract_all_fields():
    c = parse_pr_contract(FULL_BODY)
    assert c["task_id"] == "w3c-lane03"
    assert c["agent_id"] == "agent-c"
    assert c["wave"] == "3c"
    assert c["base_commit"].startswith("ab12cd34")
    assert c["owned_scope"].startswith("Application/Intelligence/**")
    assert c["tests_run"] == "dotnet test --filter AI"
    assert c["task_hash"] == "0" * 64
    assert c["task_revision"] == "3"
    assert c["lane_branch"] == "agent/w3c/lane03-01-ai"
    assert c["drift_ack"] == "high impact re-scope"


def test_parse_pr_contract_missing_fields_are_none():
    c = parse_pr_contract("just prose, no contract lines")
    assert set(c.keys()) == set(parse_pr_contract(FULL_BODY).keys())
    assert all(v is None for v in c.values())
    assert all(v is None for v in parse_pr_contract("").values())
    assert all(v is None for v in parse_pr_contract(None).values())


def test_parse_pr_contract_tolerates_labels_in_prose():
    c = parse_pr_contract("Task-Id:\tnope-1  \nTask-Id: real-lane")
    assert c["task_id"] == "nope-1"       # first match wins, documented behaviour


def test_parse_pr_contract_base_commit_length_bounds():
    assert parse_pr_contract("Base-Commit: abc12")["base_commit"] is None      # <7
    assert parse_pr_contract("Base-Commit: abc123d")["base_commit"] == "abc123d"
    assert parse_pr_contract("Base-Commit: " + "z" * 40)["base_commit"] is None  # not hex


# ------------------------------------------------------------ relevance --

def test_relevance_levels():
    assert relevance_level("anything/here.cs", True, set()) == 3
    assert relevance_level("Application/x.cs", False, {"Application"}) == 2
    assert relevance_level("Infrastructure/x.cs", False, {"Application"}) == 0
    # the module set is keyed on the FIRST path segment (a dir), so a
    # top-level file name never matches a directory module
    assert relevance_level("flat.cs", False, {"Application"}) == 0


# ------------------------------------------------------ branch matching --

@pytest.mark.parametrize("pat,declared,want", [
    ("agent/w3c/lane03-*", "agent/w3c/lane03-01-ai", True),
    ("integration/**", "integration/w5", True),
    ("agent/w3c/lane03-*", "agent/w3c/lane04-x", False),
    ("qa/**", "qa/w3c/round1", True),
    ("wave3c/integration", "wave3c/integration", True),      # exact
    ("wave3c/integration", "wave3c/integration-2", False),   # no substring match
    ("agent/w3c/lane03-*", "agent/w3c/lane03", False),       # '*' needs a char
    ("integration/**", "integration", True),                 # dir/** matches dir
    ("integration/**", "agent/w3c/lane03-x", False),
])
def test_lane_branch_matches(pat, declared, want):
    assert _lane_branch_matches(pat, declared) is want


# --------------------------------------------------- path semantics -------

def test_validate_pattern_accepts_and_rejects():
    assert validate_pattern("  a/** ") == "a/**"
    assert validate_pattern("**") == "**"
    assert validate_pattern("x\\\\y/**") == "x/y/**"     # backslash alias
    for bad in ("a**b", "/abs/**", "..", "x/../../y", "", "   "):
        with pytest.raises(PatternError):
            validate_pattern(bad)


@pytest.mark.parametrize("pat,path,want", [
    ("Application/Intelligence/**", "Application/Intelligence/a/b.cs", True),
    ("Application/Intelligence/**", "Application/Intelligence", True),
    ("dir/**", "dirfile.cs", False),                     # '**' is a SEGMENT
    ("A/*", "A/b/c", False),                             # '*' never crosses /
    ("A/*", "A/b", True),
    ("**", "any/where/at/all.cs", True),
    ("A/**/C", "A/x/y/C", True),
    ("A/B", "a/b", False),                               # case-sensitive
    ("[ab]x/**", "ax/f.cs", True),
    ("a?c/**", "abc/f.cs", True),
])
def test_matcher_glob_semantics(pat, path, want):
    assert Matcher(pat).match(path) is want


def test_matcher_case_insensitive_second_pass():
    m = Matcher("Application/Abstractions/**")
    assert not m.match("APPLICATION/ABSTRACTIONS/X.cs")
    assert m.match("APPLICATION/ABSTRACTIONS/X.cs", case_insensitive=True)


def test_normalize_repo_path_documented_rules():
    assert normalize_repo_path("./a//b") == "a/b"
    assert normalize_repo_path("a\\b\\c") == "a/b/c"
    assert normalize_repo_path("/a/b") == "a/b"
    assert normalize_repo_path("a/b/../c") == "a/c"
    assert normalize_repo_path("a/././b") == "a/b"
    assert normalize_repo_path("a\u0301/b") == "\u00e1/b"      # NFC applied
    for bad in ("", ".", "..", "../x", "a/", "a//", "\x00", "a\x01b",
                "a/b/"):
        with pytest.raises(PathError):
            normalize_repo_path(bad)


def test_patterns_conflict_examples():
    assert patterns_conflict("a/**", "a/b/**")
    assert patterns_conflict("**", "a/b")
    assert not patterns_conflict("A/**", "B/**")
    assert not patterns_conflict("Wave3b*", "Wave3c*")     # disjoint prefixes
    assert patterns_conflict("Wave3b*", "Wave3*")          # prefix-of


# -------------------------------------------------------- scan helpers ----

def test_scan_added_lines_only_plus_lines():
    hits = scan_added_lines("+++ b/A/F.cs\n+password: \"leakleak123\"\n"
                            " password: \"leakleak123\"\n")
    assert len(hits["secret"]) == 1
    hits2 = scan_added_lines("-password: \"leakleak123\"\n")
    assert hits2["secret"] == []


def test_scan_type_definitions():
    hits = scan_added_lines("+++ b/A/F.cs\n+public class Widget\n"
                            "+++ b/A/G.cs\n+internal sealed record Widget\n")
    assert hits["types"]["Widget"] == {"A/F.cs", "A/G.cs"}


# ---------------------------------------------------------- resolver ------

def test_resolve_representative_paths_on_v1_registry(reg_v1):
    """frozen hit, lane hit, integration hit, architecture hit, generated,
    lead-** fallback for a path nothing else owns."""
    trie = build_trie(reg_v1)

    def pc(p):
        return resolve_path(p, reg_v1, trie)

    r = pc("Application/Abstractions/IAi.cs")
    assert r.policy_class == PolicyClass.FROZEN and r.frozen
    r = pc("Application/Intelligence/Foo.cs")
    assert r.policy_class == PolicyClass.LANE
    assert "w3c-lane03" in r.owners_all and r.conflict_count == 0
    r = pc("MauiProgram.cs")
    assert r.policy_class == PolicyClass.INTEGRATION
    r = pc(".github/OWNERSHIP.yaml")
    assert r.policy_class == PolicyClass.ARCHITECTURE
    r = pc("Resources/Styles/Dark.xaml")
    assert r.policy_class == PolicyClass.INTEGRATION
    r = pc("docs/agents/anything.md")
    assert r.policy_class == PolicyClass.INTEGRATION
    r = pc("Random/Unowned/File.txt")
    # v1 lead '**' means nothing is UNOWNED on the real registry: fallback is LEAD
    assert r.policy_class == PolicyClass.LEAD and r.owners_all == ["lead"]
    r = pc("obj/Release/art.dll")
    assert r.policy_class == PolicyClass.GENERATED
    r = pc("obj/Release/art.dll")
    assert "generated:obj/**" in r.matched_rules


def test_resolve_case_insensitive_protected_hit(reg_v1):
    trie = build_trie(reg_v1)
    r = resolve_path("APPLICATION/ABSTRACTIONS/IAI.CS", reg_v1, trie)
    assert r.policy_class == PolicyClass.FROZEN
    assert any(rule.endswith("#ci") for rule in r.matched_rules)


def test_resolve_unowned_only_without_lead_catchall(lane_registry):
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "lane-a", "agent": "agent-a", "branch": "lane/a-*",
         "owns": ["Alpha/**"]}]}, name="nolead.yaml")
    r = resolve_path("Zeta/F.cs", reg, build_trie(reg))
    assert r.policy_class == PolicyClass.UNOWNED
    assert r.owners_all == [] and r.best_claim is None


def test_resolve_exclusive_conflict_count(lane_registry):
    """active-vs-paused overlap loads with a warning; resolution still counts
    distinct exclusive claimants C=2 on the shared path."""
    reg = lane_registry({"version": 2, "lanes": [
        {"id": "l1", "agent": "a1", "branch": "b/1", "owns": ["Dup/**"]},
        {"id": "l2", "agent": "a2", "branch": "b/2", "owns": ["Dup/**"],
         "status": "paused"}]}, name="c2.yaml")
    r = resolve_path("Dup/F.cs", reg, build_trie(reg))
    assert r.conflict_count == 2
    assert r.conflicting_owners == ["l1", "l2"]


def test_specificity_ordering_exact_gt_deep_dir_gt_root_dir_gt_universal():
    s = lambda p: Matcher(p).specificity()
    assert s("A/B/C.cs") == 46 > s("A/B/**") == 30 > s("A/**") == 15 > s("**") == 0
    assert s("A/B/*.cs") == 35 < s("A/B/C.cs")            # wildcard loses to exact
    assert s("A/B/**/*.cs") > s("A/**/*.cs")


def test_best_claim_prefers_specificity_within_family(reg_v1):
    """Application/Intelligence/Foo.cs is claimed by both w3c-lane03's
    pattern and lead's '**'; the lane claim (same-ish family, higher S) wins
    precedence over the lead default."""
    trie = build_trie(reg_v1)
    r = resolve_path("Application/Intelligence/Foo.cs", reg_v1, trie)
    assert r.best_claim.rule_id == "lane:w3c-lane03:Application/Intelligence/**"


# ------------------------------------------------- trie completeness ------

BRUTE_PATHS = ["Application/Abstractions/IAi.cs", "Application/Intelligence/Foo.cs",
               "MauiProgram.cs", ".github/OWNERSHIP.yaml", "Resources/Styles/a.xaml",
               "Random/File.txt", "obj/x.dll", "wave3c-keys/k.txt",
               "docs/agents/WORKFLOW.md", "a", "Application", "Application/",
               "Domain/Enums/X.cs", "Tests/QA/adversary.cs", "bench/src/mod001/F.cs",
               "APPLICATION/ABSTRACTIONS/X.CS", "Infrastructure/Security/Gateway/K.cs"]


def test_trie_candidates_superset_of_brute_force(reg_v1):
    """The literal-prefix trie is an optimisation ONLY: every claim that a
    brute-force scan matches must be in candidates() — including the
    case-insensitive protected-family hits resolve_path relies on."""
    trie = build_trie(reg_v1)
    for path in BRUTE_PATHS:
        try:
            norm = normalize_repo_path(path)
        except PathError:
            continue
        candidates = {c.rule_id for c in trie.candidates(norm)}
        brute = {c.rule_id for c in reg_v1.claims
                 if c.matcher.match(norm) or
                 (c.kind in ("frozen", "architecture", "integration") and
                  c.matcher.match(norm, case_insensitive=True))}
        missing = brute - candidates
        assert not missing, f"trie dropped {missing} for {norm}"


def test_trie_completeness_random_paths(reg_v1):
    """Random walk over plausible segments; candidates must never under-report."""
    rng = random.Random(20260914)                     # fixed seed: deterministic
    trie = build_trie(reg_v1)
    segs = ["Application", "Abstractions", "Intelligence", "Domain", "Enums",
            "Resources", "Styles", "docs", "agents", "obj", "bin", "wave3c-keys",
            "Tests", "QA", "MauiProgram.cs", "Foo.cs", "a**b.cs", "X"]
    checked = 0
    for _ in range(400):
        path = "/".join(rng.choice(segs) for _ in range(rng.randint(1, 4)))
        try:
            norm = normalize_repo_path(path)
        except PathError:
            continue
        checked += 1
        candidates = {c.rule_id for c in trie.candidates(norm)}
        brute = {c.rule_id for c in reg_v1.claims
                 if c.matcher.match(norm) or
                 (c.kind in ("frozen", "architecture", "integration") and
                  c.matcher.match(norm, case_insensitive=True))}
        assert brute <= candidates, f"trie missed claims for {norm}"
    assert checked > 100, "seed produced too few valid paths"


def test_resolution_agrees_with_brute_force_decision_table(reg_v1):
    """resolve_path's policy_class equals a from-scratch family-max scan."""
    from governance.registry import FAMILY_PRIORITY
    trie = build_trie(reg_v1)
    for path in BRUTE_PATHS:
        try:
            norm = normalize_repo_path(path)
        except PathError:
            continue
        kinds = set()
        for c in reg_v1.claims:
            if c.matcher.match(norm) or (
                    c.kind in ("frozen", "architecture", "integration")
                    and c.matcher.match(norm, case_insensitive=True)):
                kinds.add(c.kind)
        expect = {
            "frozen": PolicyClass.FROZEN, "architecture": PolicyClass.ARCHITECTURE,
            "integration": PolicyClass.INTEGRATION, "lane": PolicyClass.LANE,
            "lead": PolicyClass.LEAD, "generated": PolicyClass.GENERATED,
        }
        winner = max(kinds, key=lambda k: FAMILY_PRIORITY.get(k, -1)) if kinds else None
        want = expect.get(winner, PolicyClass.UNOWNED)
        assert resolve_path(norm, reg_v1, trie).policy_class == want, path


# ------------------------------------------------------------ misc unit ---

def test_reason_severity_map_is_total_and_mutation_proof():
    for r in Reason:
        assert r in REASON_SEVERITY
        expected = Severity.HARD_YELLOW if r in YELLOW_REASONS else Severity.HARD_RED
        assert REASON_SEVERITY[r] == expected


def test_changed_file_eval_paths_and_unknown_default():
    ch = gite.ChangedFile(path="a/b.cs")
    assert ch.operation == Operation.UNKNOWN          # fail-closed default
    assert gite.ChangedFile(path="a/b.cs", old_path="a/old.cs",
                            operation=Operation.RENAME).eval_paths == ["a/old.cs",
                                                                       "a/b.cs"]
    assert gite.ChangedFile(path="a/b.cs",
                            operation=Operation.CREATE).eval_paths == ["a/b.cs"]


def test_live_registry_loads_valid_without_findings(reg_live):
    """The actual .github/OWNERSHIP.yaml in this worktree must load: policy
    may evolve (v1 -> v2 by a concurrent wave) but never break the gate."""
    assert reg_live.version in (1, 2)
    assert reg_live.validation_findings == []
    assert "lead" in reg_live.lanes and "w3c-lane03" in reg_live.lanes


def test_warnings_are_advisory_and_registry_predicate_holds(reg_v1):
    """The pinned v1 registry loads with 3 hygiene WARNINGS and zero findings;
    warnings must not affect any verdict (registry_valid stays proven)."""
    assert reg_v1.validation_findings == [] and len(reg_v1.validation_warnings) == 3
    gi = GovernanceInput(
        registry=reg_v1, changeset=cf.changeset(("Application/Intelligence/F.cs",
                                                 Operation.CREATE)),
        contract=cf.contract_for(reg_v1.lanes["w3c-lane03"]),
        meta={"mergeable": "clean", "author": "agent-c"},
        branch_evidence=cf.branch_evidence(), diff_text="")
    res = classify(gi)
    assert res.decision == Decision.GREEN
    assert res.green_predicates["registry_valid"] is True
    # warnings are advisory: never copied into the fired constraints
    assert res.hard_constraints == []
