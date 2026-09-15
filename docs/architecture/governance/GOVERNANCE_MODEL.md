# LIVORA Governance Model

Normative reference for the governance engine at `scripts/integration/governance/`
and the `livora_gates.py` CLI. This document describes **what the code does** and
**why it is shaped that way**. Where this text and the source disagree, the source
is authoritative; fix this file.

Companion documents:
- `THREAT_MODEL.md` — adversarial analysis of the same design.
- `MIGRATION.md` — registry schema v1 → v2.
- `workflow-integration-notes.md` — CI adoption/rollback of the v2 gate.
- `.github/OWNERSHIP.yaml` — the policy instance itself.

---

## 1. Layers

The engine is a strict pipeline; each layer only consumes the layer above it
(`engine.py` module docstring):

| Layer | Contents | Module | Failure mode |
|---|---|---|---|
| **Policy** | `OWNERSHIP.yaml` registry: families (frozen / architecture_owned / integration_owned / generated), lanes, claims, overlap groups, global policy knobs | `registry.py` | invalid registry → RED before anything else (`REGISTRY_INVALID`, `REGISTRY_CORRUPTION`) |
| **State** | lanes, lifecycle statuses, task revisions and hashes | `registry.py`, `model.py` | unknown lane / bad lifecycle → RED |
| **Evidence** | git-derived changeset (with operations + renames), branch topology (merge-base, behind/ahead, force-push), diff text, mergeability | `git_evidence.py`, `livora_gates.py` | missing/broken evidence → explicit gaps, fail-closed (`MISSING_EVIDENCE`) |
| **Decision** | fold of hard constraints + GREEN predicates → GREEN / YELLOW / RED + stable JSON manifest | `engine.py`, `report.py` | predicate failure without any fired constraint → RED (engine inconsistency fails closed) |

The registry is **an untrusted input**: policy is validated on load, and the
decision on any PR is conditioned on that validation (`stage 0` in `classify()`).

## 2. Decision algebra

### 2.1 The fold

There is exactly one severity fold in the system — `fold_decisions()`:

```
any HARD_RED constraint      -> RED
else any HARD_YELLOW        -> YELLOW
else                        -> GREEN (candidate)
```

Applied per file (`FileDecision.decision`) and over the union of PR-level and
per-file constraints in stage 7 of `classify()`.

### 2.2 GREEN is proven, not granted

If no constraint fired, GREEN still requires **all predicates** in
`compute_predicates()` to be true (14 in the current source):

1. `registry_valid`
2. `governance_metadata_valid`
3. `lane_registered`
4. `ownership_valid`
5. `task_valid`
6. `branch_valid`
7. `no_contract_violation`
8. `no_exclusive_conflict`
9. `no_unclassified_file`
10. `no_stale_base_violation`
11. `no_identity_mismatch`
12. `changeset_nonempty`
13. `mergeability_clean`
14. `test_gates_declared`

Correction against the design brief: the source defines **14 predicates**, not 13 —
`compute_predicates()` in `engine.py` returns the 14 keys listed above (verified by
parsing the function's return dict). This document follows the source.

If zero constraints fired but any predicate is false, the engine adds
`UNKNOWN_RULE` (HARD_RED, from the reason-code severity table) with the message
"…engine inconsistency fails closed" and returns RED. Absence of findings is never
proof of safety: each predicate names the evidence it requires.

### 2.3 Reason codes and severities

`Reason` (`model.py`) is a closed machine enum; `REASON_SEVERITY` is an explicit
total map — every reason is HARD_RED unless it appears in `YELLOW_REASONS`
(19 codes: e.g. `LEAD_AMENDMENT`, `CROSS_LANE_IMPACT`, `OVERSIZED_CHANGE`,
`STALE_BASE`, `ARCHITECTURE_STALE_BASE`, `TASK_DRIFT_MEDIUM`, `SCOPE_DRIFT`,
`GENERATED_FILE_COMMITTED`, `LANE_STALE`, `MERGEABILITY_UNKNOWN`,
`SOFT_TRIPWIRE_PATTERN`, `BASE_METADATA_MISMATCH`, `EMPTY_CHANGESET`,
`WEAK_TASK_RELEVANCE`, …). There is no substring/prefix magic, so adding a reason
cannot silently change a verdict. The map is built once from the enum and cannot
mutate at runtime.

The enum also carries codes that the v1 engine never *fires* (they exist for
explanatory completeness or future stages and would classify RED if emitted:
`UNKNOWN_TASK`, `PATH_TRAVERSAL`, `PATH_DUPLICATE_ENTRY`, `TEST_EVIDENCE_MISSING`,
`LIFECYCLE_TRANSITION_INVALID`, `REGISTRY_OVERLAP_WARNING`, `REGISTRY_HYGIENE`,
`LIFECYCLE_CHANGED` has only a severity entry, no emitter). Enum membership is not
behavior; the emitters in `classify()` are the behavior.

### 2.4 Fail-closed vocabulary

Every situation the engine cannot decide is a constraint, never a pass:

| Cause | Reason code | Severity |
|---|---|---|
| lane id not in registry | `UNKNOWN_LANE` | RED |
| path matches no claim at all (`PolicyClass.UNOWNED`) | `UNKNOWN_FILE_OWNER` | RED |
| lane-class path where the submitting lane is not an active owner | `UNKNOWN_FILE_OWNER` | RED |
| path covered only by the lead `'**'` default claim | `LEAD_AMENDMENT` | YELLOW (human review; never auto-GREEN) |
| unparseable git path | `MALFORMED_PATH` | RED |
| case-variant duplicates in one changeset | `PATH_CASE_COLLISION` | RED |
| backslashes in a diff path | `PATH_BACKSLASH_ALIAS` | RED |
| flattened `changed.txt` without operations (no `changed.json`, no local git) | `UNKNOWN_OPERATION` | RED |
| no branch evidence supplied | `MISSING_EVIDENCE` | RED |
| registry file unreadable / not a mapping / YAML parse failure | `REGISTRY_INVALID` / `REGISTRY_CORRUPTION` | RED |
| PyYAML not installed (no reduced-fidelity fallback) | `REGISTRY_INVALID` | RED |
| any unexpected CLI exception | `PARSER_FAILURE` | exit 2 |
| empty changeset | `EMPTY_CHANGESET` | YELLOW (a zero-file PR cannot be GREEN) |
| mergeability unknown | `MERGEABILITY_UNKNOWN` | YELLOW; `dirty` → `MISSING_EVIDENCE` RED |

Note the asymmetry the brief phrased loosely: lead-class-only paths are YELLOW
`LEAD_AMENDMENT`, not RED; a matching-nothing path is RED; the brief's wording
("UNKNOWN_FILE_OWNER… VERIFY") is resolved as: `UNKNOWN_FILE_OWNER` fires for
UNOWNED and for lane-class-without-submitter-claim, both RED.

## 3. Ownership: precedence and specificity

### 3.1 Rule families and priorities

`FAMILY_PRIORITY` (`registry.py`) — higher wins:

| family | priority |
|---|---|
| `frozen` | 900 |
| `architecture` | 800 |
| `lane_exclusive` | 700 |
| `integration` | 600 |
| `lane` | 500 |
| `inherited` | 400 |
| `generated` | 300 |
| `lead_default` | 200 |
| `default` | 0 |

`inherited`/`default` are reserved vocabulary (no v1 emitter constructs claims
with those kinds). The lead lane's `'**'` claim gets `lead_default` priority and
is treated as a non-exclusive catch-all (`exclusive=False` is forced for `lead`);
lane-class paths covered only by it produce `LEAD_AMENDMENT`.

### 3.2 Deterministic total order (YAML order never matters)

The winning claim among all matches is `sorted` by the key
(`resolver.resolve_path()`):

```
(-family_priority, -specificity, -len(literal_prefix), rule_id)
```

i.e. family priority desc → specificity desc → literal-prefix length desc →
`rule_id` lexicographic asc. `rule_id` is stable
(`kind:subject:pattern` for lane/frozen/architecture/integration/generated claims),
so the order is a **total order** — file order in the registry can never change a
verdict (property-tested in `Tests/Governance/test_properties.py`,
`test_rule_order_independence`).

### 3.3 Specificity math

`Matcher.specificity()` = **S = 10·P + 5·D + E** where, over the pattern's
segments **excluding `**` segments**:

- P = count of literal (wildcard-free) segments,
- D = number of concrete segments (depth; `**` adds reach, not specificity),
- E = 1 iff the pattern is an exact path (no wildcard at all).

Worked values: `'**'` → 0; `'dir/**'` → 15; `'Application/Abstractions/**'` → 30;
`'Tests/Tests/Wave3b*'` → 35; `'Application/Rules/RuleEngine.cs'` → 46.
So an exact file rule beats a `dir/**` rule beats `'**'`, and the family layer
always outranks specificity (frozen exact file < frozen dir beats any lane rule).

### 3.4 Policy class of a file

`classify()` assigns one `PolicyClass` per file by family precedence of the
matched families:
`FROZEN > ARCHITECTURE > INTEGRATION > LANE > GENERATED > LEAD > UNOWNED`.
`generated` is only assigned when **no** frozen/architecture/integration/lane
family matched; `fd.generated` is set the same way.

Per-class outcomes (stage 4):

| class | submitter = lead | submitter = lane |
|---|---|---|
| FROZEN | `LEAD_AMENDMENT` (YELLOW — human review, never auto-GREEN) | `FROZEN_CONTRACT_TOUCHED` RED |
| ARCHITECTURE | `LEAD_AMENDMENT` YELLOW | `ARCHITECTURE_FILE_TOUCHED_BY_LANE` RED |
| INTEGRATION | (no finding — lead applies) | `INTEGRATION_FILE_CHANGED` YELLOW (lane may PROPOSE an APPEND block; lead applies) |
| LANE | — | RED `UNKNOWN_FILE_OWNER` if submitting lane ∉ active owners; else no ownership finding |
| LEAD (`'**'` only) | `LEAD_AMENDMENT` YELLOW | `LEAD_AMENDMENT` YELLOW |
| GENERATED | `GENERATED_FILE_COMMITTED` YELLOW (generated output must not be committed; if it must be, govern it as a normal owned path) | same |
| UNOWNED | `UNKNOWN_FILE_OWNER` RED | `UNKNOWN_FILE_OWNER` RED |

Exclusive-claim conflicts: C = number of **distinct lanes** with exclusive claims
matching the path. C > 1 with the lanes sharing an `overlap_groups` entry →
`CROSS_LANE_IMPACT` YELLOW (explicit policy: lead arbitrates). C > 1 otherwise →
`OWNERSHIP_CONFLICT` RED (ambiguous ownership).

## 4. Path formalization

### 4.1 Normalization — `paths.normalize_repo_path`

Deterministic canonical repo-relative POSIX path or `PathError` (which is a
governance RED, never a pass-through):

- reject non-string, empty, NUL byte, any Unicode category `Cc` control char;
- **NFC** Unicode normalization (defeats look-alike/homoglyph renames);
- `\` → `/`; leading `./` and `/` stripped; `.` segments dropped; `//` collapsed;
- `..` resolved **inside** the repository; escaping the root is `PathError`;
- trailing `/` rejected;
- case is **preserved** (case-collisions are a separate detection, §4.3).

Normalization is idempotent and never crashes on control-free text (property
tests). Renames evaluate **both** `old_path` and `path` (`ChangedFile.eval_paths`)
— a rename out of or into a protected family is caught on each side.

### 4.2 Glob semantics — `validate_pattern` + `Matcher`

- `**` matches zero or more segments and **must occupy a whole segment**
  (`a**b` is a `PatternError`);
- `dir/**` also matches `dir` itself — the registry convention (owning a
  directory, not only its contents);
- `*` and `?` never cross `/`; `[...]` character classes supported, `[!...]`
  negated;
- a pattern may not start with `/` or contain `..` (no root traversal);
- `'**'` alone is the universal pattern (`is_universal`) — rejected when it
  appears in a lane's `Owned-Scope:` contract line
  (`DECLARED_SCOPE_TOO_BROAD`, YELLOW: it would self-disarm the scope-drift check).

Protected-family case bypass is defeated by a **case-insensitive second pass
only for `frozen` / `architecture` / `integration` kinds**
(`resolver.resolve_path`: `c.kind in ("frozen","architecture","integration")`),
matched with a `#ci` suffix on the rule id. Lane/lead/generated claims stay
case-sensitive, matching Git on Linux CI. (The brief says "frozen/architecture";
the source also includes `integration` — source wins, noted in the report.)

### 4.3 Conflict decidability — `patterns_conflict`

Used by registry self-validation to find two exclusive claims that can match a
common path. Segment-wise `segs_conflict` decides the decidable cases exactly:

- literal vs literal: equal (casefolded) or not;
- literal vs pattern: exact regex membership;
- two simple prefix-globs: intersect **iff** one literal prefix is a prefix of
  the other — so `Wave3b*` vs `Wave3c*` is **provably disjoint** (verified:
  `patterns_conflict → False`);
- wildcard vs wildcard otherwise: conservative `True` — over-report, never
  under-report, because a missed exclusive conflict is worse than a false one.

`**` segments branch the walk (memoized). Direction of conservatism: a
**registry-level** conflict between two *ACTIVE/PLANNED* lanes is a finding
(RED at v2 / warning at v1); between historical lanes it is always a warning
("inert unless both reactivate").

## 5. Task identity and drift

- `task_id` (= lane id) is **immutable** — identity never changes; content does.
- `task_hash = sha256(canonical_task_text)` where canonical = strip, collapse all
  whitespace runs to one space, casefold (`registry.canonical_task_hash`). The
  registry computes it at load; the PR must declare `Task-Hash:`.
- comparison is prefix-tolerant (`lane.task_hash.startswith(declared)`, ≥8 hex
  chars per the PR template) — a truncated paste stays valid, a wrong hash does not.
- declared hash ≠ registry hash → drift, unless `Task-Drift-Ack:` text indicates
  ownership/security/architecture/contract/frozen (or the word "high") — then it
  is `TASK_DRIFT_HIGH` **RED** (explicit lead re-authorization required);
  otherwise `TASK_DRIFT_MEDIUM` **YELLOW**. No hash at all →
  `CONTRACT_FIELD_MISSING` RED ("unknown fails closed").
- `Task-Revision:` (int, registry default 1) must equal the registry's
  `task_revision`, else `TASK_REVISION_UNKNOWN` RED.
- required contract lines per PR body: `Task-Id`, `Agent-Id`, `Wave`,
  `Base-Commit`, `Tests-Run` (`REQUIRED_CONTRACT_FIELDS`), plus `Task-Hash` and
  `Task-Revision` checked in the drift stage. Missing any → `CONTRACT_FIELD_MISSING`.

## 6. Lane lifecycle

Enum: `planned | active | paused | stalled | superseded | merged | released | abandoned`.

`TRANSITIONS` (`model.py`) — the legal directed graph:

| from | to |
|---|---|
| planned | active, abandoned |
| active | paused, stalled, merged, abandoned, superseded |
| paused | active, stalled, abandoned, superseded |
| stalled | active, abandoned, superseded, merged |
| merged | released |
| released / superseded / abandoned | ∅ (terminal) |

`merged → active` is deliberately **absent**: reopening work means a new lane id.
(Enforcement note: the engine does not currently emit
`LIFECYCLE_TRANSITION_INVALID` — the transition table is normative data for the
coordinator, not yet a gate. Documented gap, see §10.)

Status sets:
- `NON_DELIVERING = {released, superseded, abandoned, merged}` → any PR by such a
  lane is RED `LANE_NOT_DELIVERING` ("merged lanes need a re-open/new lane id");
- `CLAIM_BEARING = {planned, active, paused, stalled, merged}` — a merged lane
  **keeps its claims** so no other lane can silently take its paths, while being
  unable to deliver. "Active owners" for per-file purposes = delivering
  `{planned, active, paused, stalled}` ∪ claim-bearing on relevance side
  (`resolve_path`: `owners_active` from delivering; `relevance_lane_ids` from
  claim-bearing).
- `stalled` → `LANE_STALE` YELLOW (coordinator review required).
- `expiry` is stored on claims and **not enforced** by the engine in v1 (data for
  the coordinator; see §10).

## 7. Branch evidence and staleness

`git_evidence.branch_evidence(repo, lane_branch, base_branch, declared_base,
frozen_prefixes)` — pure git-topology, **no wall-clock**:

- head/base resolution local refs → `origin/*` refs; failures become gaps
  (`NO_BRANCH_DECLARED`, `BRANCH_NOT_FOUND`, `BASE_BRANCH_NOT_FOUND`,
  `NO_MERGE_BASE`) → each gap is a `MISSING_EVIDENCE` RED;
- `commits_behind = rev-list --count merge_base..base_sha`,
  `commits_ahead = rev-list --count merge_base..head_sha`;
- lane branch == base branch → `BRANCH_IS_BASE` RED;
- **force-push detection**: declared `Base-Commit` must be an ancestor-equal of the
  merge-base (either SHA prefixes the other); otherwise `force_push_suspect` →
  `BRANCH_UNSAFE_REWRITE` RED; declared base not in repo → gap;
- **architecture staleness**: if any path changed on master between merge-base and
  base matches a frozen/architecture literal-prefix,
  `architecture_changed_after_fork=True` → `ARCHITECTURE_STALE_BASE` YELLOW,
  checked **before** plain count staleness so it outranks it ("architecture
  staleness outranks time");
- plain staleness: `commits_behind >= policy.stale_commits_threshold` (default 25)
  → `STALE_BASE` YELLOW;
- declared base ≠ merge-base (no rewrite, just stale metadata) →
  `BASE_METADATA_MISMATCH` YELLOW.

**Wiring honesty**: `git_evidence.detect_binary()` exists (numstat `-`/`-`
columns) but is **not called** by the CLI, so `GovernanceInput.binary_paths`
stays at its empty default and the binary term of change-magnitude is 0 on the
CI path (the workflow's `changed.json` writes `"binary": false` for every entry).
Rich operation evidence (create/modify/delete/rename/copy) requires `changed.json`
or a local git checkout; the flattened `changed.txt` fallback yields
`UNKNOWN_OPERATION` RED by design.

## 8. Relevance and risk (explanatory layer)

### 8.1 Relevance levels

`engine.relevance_level(path, submitter_owns, lane_modules)` returns **3, 2 or 0**
— levels 4 and 1 exist in the 0..4 range of the docstring but no emitter produces
them in v1:

| level | condition |
|---|---|
| 3 | submitter holds an active claim on the file (or is acting as lead) |
| 2 | the file's **top-level module** (`path.split("/")[0]`) is a module some claim of the submitting lane touches (excluding universal `'**'` claims) |
| 0 | none of the above |

GREEN requires relevance ≥ 3 per file; `WEAK_TASK_RELEVANCE` (YELLOW) fires for
level ≤ 0 on files that are not themselves FROZEN/ARCHITECTURE/INTEGRATION-class
(those classes already carry their own findings). Level 2 (module adjacency) has
no explicit finding: in practice a level-2 file matched no active claim of the
submitter, so it can only reach a verdict through another finding (typically
`LEAD_AMENDMENT` YELLOW or `UNKNOWN_FILE_OWNER` RED) — the only path to a clean
fold is level 3. Level is reported in
`per_file_decisions[].relevance_level`. (The brief lists "top-level module
adjacency = 2"; exact semantics per source above.)

### 8.2 Risk factors

`RiskFactors` A/O/C/S/T/B/D/X ∈ [0,1], per file from stage 4, PR-level = per-file
mean (rounded to 3 decimals). **Explanatory only** — a number can never cancel a
hard constraint, and no threshold on any factor changes a verdict.

### 8.3 Change magnitude

`change_magnitude(F files, A additions, D deletions, R renames, B binary)`:

```
CM = 0.5·min(F,100)/100
   + 0.2·min(log10(1+A+D), 4)/4
   + 0.2·min(R,20)/20
   + 0.1·min(B,20)/20          (bounded to [0,1], rounded to 4 decimals)
```

Verified example: `CM(40 files, 500+, 100−, 2 renames, 1 binary) = 0.3639`;
`CM(1,10,0,0,0) = 0.0571`. Log-scaled line counts make 10× growth one step, not
ten. CM contributes to **no** verdict: the size gate is the policy threshold
`len(changeset) > policy.auto_merge_max_changed_files` (default 40) →
`OVERSIZED_CHANGE` YELLOW. Size never produces RED by itself.

### 8.4 No fake precision

The design anti-pattern this section exists to refuse: a legacy review culture of
printing a composite risk score like **73.4821** — a six-digit number that looks
measured but is a weighted sum of categorical guesses, silently thresholds
decisions nobody can defend. The LIVORA engine keeps numbers **out** of the
decision path entirely (module docstring: "Numbers… are EXPLANATORY ONLY… No fake
precision."). The manifest therefore reports integers (behind/ahead), booleans
(predicates), and enums (reason codes) as the *authoritative* fields;
`risk_factors` and `change_magnitude` are included for humans and downstream
analytics that must not feed them back into gating. If you are tempted to add
`decision = score > 0.7348` anywhere, stop: encode the actual predicate and give
it a reason code instead.

## 9. Determinism contract

For fixed `(registry bytes, evidence files, --as-of timestamp)`:

1. `f(input) = decision` — the engine never reads the clock
   (`timestamp: str = "FROZEN"`, "never read the clock"; CI passes
   `--as-of "$GATE_AS_OF"` = run start time; `git_evidence.current_time_iso()`
   is explicitly marked "NOT used by classification").
2. All JSON output is stable: `report.dump_json` uses `sort_keys=True`; manifest
   lists are sorted (`per_file_decisions` by path, `hard_constraints` by
   (reason, path, lane), `reason_codes` deduped+sorted, `matched_rules`, owners);
   YAML is loaded with `yaml.safe_load` only, PyYAML as the single parser (a
   second implementation could classify differently per host → RED fail-closed
   via `REGISTRY_INVALID` when absent).
3. Iteration is over sorted sets or stable lists; regex compilation per rule is
   done once at load (`Matcher.__slots__`); the trie (`resolver.Trie`) indexes
   claims by literal prefix so per-file matching is ~O(D+k) and candidate order
   is irrelevant (deduped by `rule_id`, results sorted before output).
4. Byte-stability: re-running classify on the same head with the same `--as-of`
   yields a byte-identical manifest (this is the acceptance property the
   rollback procedure in `workflow-integration-notes.md` relies on).

## 10. Manifest schema (`report.build_manifest`)

Top-level fields (JSON, sorted keys):

| field | meaning |
|---|---|
| `manifest_version` | schema pin (currently `1`) |
| `repository`, `pr` | repo slug, PR number |
| `base_sha`, `head_sha`, `merge_base` | topology SHAs (evidence-bound) |
| `lane_id`, `agent_id`, `task_id` | identity as parsed from the PR contract |
| `task_revision`, `task_hash`, `declared_task_hash` | registry truth vs declared proof (drift is visible without recomputation) |
| `registry_version`, `registry_sha256` | policy pin — the registry file's own sha256 |
| `engine` | `"livora-governance/1"` |
| `timestamp` | frozen `--as-of` value |
| `branch_evidence` | full `BranchEvidence` block or `null` (behind/ahead, force_push_suspect, gaps sorted, `architecture_changed_after_fork`) |
| `changed_files`, `change_magnitude` | counts + explanatory metric |
| `per_file_decisions` | per file: `path, operation, old_path, new_path, policy_class, owners, active_owners, conflicting_owners, matched_rules, relevance_level, decision, generated, hard_constraints[], risk{A..X}` |
| `conflicts` | sorted reason codes starting with `OWNERSHIP` among PR-level constraints |
| `hard_constraints` | PR-level list sorted by (reason, path, lane); each `{reason, severity, message, path, rule_id, lane_id}` |
| `green_predicates` | `{name: bool}` sorted — the proof ledger |
| `risk_factors` | PR-mean `A O C S T B D X` |
| `task_drift` | `NONE` / `MEDIUM` / `HIGH` |
| `reason_codes` | sorted unique codes across PR + per-file constraints |
| `final_decision` | `GREEN` / `YELLOW` / `RED` |

CLI exit codes are the stable CI contract: `classify`/`inspect-pr` 0=GREEN
1=YELLOW 2=RED; `validate-registry` 0=valid 2=invalid; `audit`/`benchmark` 0
(findings are data, not exit codes).

## 11. Intentionally NOT attempted in v1

Deliberate scope boundaries — each is a known, accepted limit, not an oversight:

1. **No compiler/symbol dependency graph.** Dependency fan-out (D) and cross-lane
   impact are inferred from paths and `overlap_groups` only. A lane editing a
   file whose *consumers* live in another lane's territory is invisible unless an
   overlap group declares it. Building a C# symbol graph is future work.
2. **No wall-clock staleness.** Time never enters the decision (topology does);
   `expiry` fields and `stale_base_seconds` (v1 remnant) are inert data.
3. **No lifecycle-transition enforcement.** `TRANSITIONS` is normative prose-to-
   data for humans; the gate only checks current status semantics (delivering /
   claim-bearing), not the legality of a registry diff.
4. **Binary detection needs rich evidence.** `binary_paths` is only populated from
   git numstat (local git / future wiring); CI `changed.json` currently marks
   everything `binary: false`, so the B term of CM is conservative-zero in CI.
5. **No symlink-mode reasoning.** The engine matches the *path text* git reports;
   see THREAT_MODEL.md for the residual-risk analysis and detection proposal.
6. **No cross-PR scheduling fairness** — that is `Integrate`'s FIFO heartbeat,
   outside governance.
7. **v1 legacy tolerance** — hygiene issues in a `version: 1` registry load as
   warnings (documented compromise; see MIGRATION.md).
8. **The PR body is untrusted input by design** — every claim in it (Base-Commit,
   Owned-Scope, Tests-Run) is only checked against git/GitHub-derived evidence;
   nothing in the body is ever taken as proof of a passing test.
