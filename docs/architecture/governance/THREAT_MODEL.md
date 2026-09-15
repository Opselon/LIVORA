# LIVORA Governance Threat Model

Scope: the governance engine (`scripts/integration/governance/`), its CLI
(`scripts/integration/livora_gates.py`, `scripts/integration/governance_local.py`)
and the CI path that consumes it (`.github/workflows/pr-gates-v2.yml` →
`report` job `roll-up status` → `Integrate` merges the oldest GREEN PR).

Out of scope: MAUI application security, the AI gateway key blob, physical/API
compromise of GitHub itself (assumed: an attacker can be an *authenticated
contributor* with PR rights).

Method: enumerate what a lane agent can control, map each vector to the control
that exists **in the source today**, and state the residual risk honestly —
including the two places where the control is documented but not wired.

---

## 0. FIXED CRITICAL — the gate classified against the PR's own policy file

**Severity: critical (policy privilege escalation). Status: CLOSED in this PR
(`pr-gates-v2.yml` now exports the registry from the merge-base commit and all
policy reads — drift detection, `validate-registry`, `classify` — use that
BASE copy; the PR-head copy is never loaded). Kept here as the canonical
attack description and regression rationale.**

### The gap

`.github/workflows/pr-gates-v2.yml` defines at workflow level:

```yaml
env:
  REGISTRY: .github/OWNERSHIP.yaml
```

`actions/checkout@v4` (no `ref:`) checks out the **merge ref of the PR**, i.e. the
PR head's tree. Both `validate-registry` and `classify` are then run with
`--registry "$REGISTRY"`, so **the policy that decides the verdict is read from the
attacker's own commit**. The same step reads frozen/architecture patterns from the
PR-head file to compute `architecture_changed_after_fork`, so the drift check is
self-referential too.

### Why the obvious defence does not save it

The intuition is "a lane cannot edit the registry, because `.github/**` is
`architecture_owned` → RED". True only if the classification uses the *base*
registry. Evaluate the PR against the registry **it modifies** and the file is no
longer architecture-owned:

1. PR head edits `.github/OWNERSHIP.yaml`: deletes `architecture_owned` and adds
   lane `w9-evil` with `owns: ["**"]`, `agent: <the attacker's own Agent-Id>`,
   `status: active`, plus a `policy` block with a large
   `auto_merge_max_changed_files`.
2. `validate-registry` passes — the rogue registry is schema-valid.
3. Stage 4 classifies `.github/OWNERSHIP.yaml` as `PolicyClass.LANE` whose active
   owner is the submitting lane → **no** `ARCHITECTURE_FILE_TOUCHED_BY_LANE`, no
   `UNKNOWN_FILE_OWNER`, no `LEAD_AMENDMENT`.
4. Identity checks pass (`Agent-Id` equals the rogue lane's `agent`; the actor is
   unbound in the rogue `agent_bindings`).
5. With clean mergeability, `Task-Hash` matching the rogue lane's own
   `canonical_task_hash` text, and topology within `stale_commits_threshold`, every
   predicate is provable → **GREEN**, and `Integrate` auto-merges the oldest GREEN
   PR. Policy is now attacker-controlled.

The engine is not at fault: it is a pure function of `(registry, evidence)`. The
wiring supplies the wrong registry. `manifest.registry_sha256` does record the
rogue bytes — but nothing compares it to the base registry's SHA, so the audit
trail only helps after the fact.

### REQUIRED remediation (exact, and mandatory before v2 is enabled)

Pin the policy to the PR's base commit and use it for **every** governance step.
Add after the "Establish base / head / merge-base topology" step, and change the
two `--registry "$REGISTRY"` call sites (and the inline meta.json step) to
`--registry "$POLICY_REGISTRY"` / `os.environ["POLICY_REGISTRY"]`:

```yaml
      # POLICY MUST COME FROM BASE, NEVER FROM THE PR. A PR is not allowed to
      # author the rules that judge it. Workflow-level REGISTRY is intentionally
      # replaced here (a distinct key avoids env-context shadowing ambiguity).
      - name: Pin governance policy to the base commit
        env:
          BASE_SHA: ${{ env.BASE_SHA }}
        run: |
          set -euo pipefail
          mkdir -p policy-base
          git show "${BASE_SHA}:.github/OWNERSHIP.yaml" > policy-base/OWNERSHIP.yaml
          test -s policy-base/OWNERSHIP.yaml
          echo "POLICY_REGISTRY=policy-base/OWNERSHIP.yaml" >> "$GITHUB_ENV"
```

`BASE_SHA` is `baseRefOid` from `gh pr view` (GitHub-authenticated, not agent-
declared), so `git show <sha>:<path>` cannot be steered by the PR. Belt-and-braces
variants: fetch the same blob via `gh api repos/{owner}/{repo}/contents/.github/OWNERSHIP.yaml?ref=$BASE_SHA`,
and add an audit step that fails when
`sha256sum .github/OWNERSHIP.yaml` (PR head) ≠ `registry_sha256` in the manifest —
i.e. *detect* registry edits loudly instead of merely ignoring them.

Until this lands, treat v2 as **not safe to enable on a repo where non-lead agents
can open PRs**; `workflow-integration-notes.md` §"Known blockers" is where the lead
tracks it. Also keep the v1 rule that only the lead's PR may touch
`.github/OWNERSHIP.yaml` as a *process* control while the technical one is missing.

---

## 1. Attacker capabilities

| # | Capability | Assumed |
|---|---|---|
| C1 | Full control of a lane branch: file contents, paths, commit messages, force-push | yes |
| C2 | Full control of the PR body: every `Key:` line, including `Task-Id`, `Agent-Id`, `Base-Commit`, `Owned-Scope`, `Task-Hash`, `Task-Drift-Ack`, `Tests-Run` | yes |
| C3 | Authenticated GitHub actor with PR rights on the repo (its login is visible in `pr.json`) | yes |
| C4 | Ability to open many PRs, and to time them against the `Integrate` heartbeat | yes |
| C5 | Editing `.github/OWNERSHIP.yaml` directly on `master` | no (lead-only, protected) |
| C6 | Editing `.github/OWNERSHIP.yaml` inside a PR | yes as an *action* (the question is whether the gate notices — see §0) |
| C7 | Reading the gate's source and manifests | yes (repo-visible) — the model is not secret |
| C8 | GitHub runner compromise, or supply-chain control of the workflow's `permissions:` | no |
| C9 | Control of the merge-base / topology GitHub reports | no |

## 2. Vectors, controls, residual risk

| # | Vector | What the attacker wants | Control in the source today | Verdict | Residual risk |
|---|---|---|---|---|---|
| T1 | **Self-granting registry edit** (C6) | make my paths legal / make frozen non-frozen | `.github/**` is `architecture_owned` → RED *if judged with the base registry*; `validate-registry` + `classify` in CI are judged with the **PR-head** registry | **not mitigated in CI wiring** | **Critical** until §0's `POLICY_REGISTRY` pin ships. Defence-in-depth still valuable: `LEAD_AMENDMENT` on lead-only paths, `manifest.registry_sha256` for auditing |
| T2 | **Agent-Id spoofing** (C2): declare `Agent-Id` equal to the victim lane's registered agent, submit under `Task-Id` of a lane someone else registered | deliver code as another lane, or steal a lane's scope | (a) declared `Agent-Id` must equal `lane.agent` (`PR_AUTHOR_IDENTITY_MISMATCH` RED) — but both sides come from files the attacker can read, so this only catches sloppiness; (b) the real control is `policy.agent_bindings[actor]`: the *GitHub-authenticated* login from `pr.json` must be bound to the submitted lane id, else RED | mitigated **only for lanes listed in `agent_bindings`** | Any lane absent from `agent_bindings` is unbound: any contributor with PR rights may submit as it. LIVORA's registry binds only `opselon: lead`. Remediation: bind every active lane to a real actor, and treat unbound lanes as YELLOW rather than silent |
| T3 | **PR-body lies** — fake `Base-Commit`, `Owned-Scope: **`, `Tests-Run: 439/439` | look in-scope / look tested | `Base-Commit` is only ever compared to git-derived `merge_base`; `'**'` in Owned-Scope → `DECLARED_SCOPE_TOO_BROAD` (YELLOW) and the drift check self-disarms, so the *real* scope check is ownership, not the declaration; `Tests-Run` is required non-empty for `test_gates_declared` and is treated as a claim — the independent truth is `build-test` on `refs/pull/N/merge` | body is **never** trusted as evidence; the workflow comment in `pr-gates-v2.yml` says exactly that | `BASE_METADATA_MISMATCH` is YELLOW, so lying about the base costs a hold, not a block. `Owned-Scope` cannot *grant* anything (it can only be too broad or too narrow vs actual ownership) |
| T4 | **Path traversal / absolute paths** (`../../etc`, `/etc/passwd`, `..\..`) | write outside the repo | `normalize_repo_path`: `..` resolved inside the repo, escape = `PathError` → `MALFORMED_PATH` RED; NUL and every `Cc` control char rejected; trailing `/` rejected; leading `/` and `.` stripped | mitigated | A traversal-shaped path is *rejected*, which is the intended fail-closed outcome, so the residual is a self-inflicted RED for an honest lane, not an escape |
| T5 | **Case-variant bypass** (`application/abstractions/x.cs` on a case-insensitive checkout) | escape a protected glob | literal matching is case-sensitive (Git on Linux CI) **plus** a case-insensitive second pass restricted to `frozen` / `architecture` / `integration` kinds (`#ci` rule ids); `PATH_CASE_COLLISION` RED when one changeset carries two case variants of a path; Windows checkouts collide anyway | mitigated for protected families | Lane/lead/generated globs remain case-sensitive by design, so a case-variant of a *lane* claim is UNOWNED → RED (fails closed, not a bypass). On the shared Windows `build-test` runner, case-variant paths in the merge ref can still confuse the build |
| T6 | **Homoglyph / confusable directory** (`Application/Abstractionс/` with a Cyrillic `с`) | ship a file that *looks* like it is in a frozen directory | `normalize_repo_path` applies **NFC** — that canonicalizes decomposed vs precomposed codepoints (`e`+U+0301 vs `é`), not cross-script look-alikes; a confusable directory is simply a new path with no claim → `UNKNOWN_FILE_OWNER` RED unless the lane happens to own it | **partial** | If the confusable path *is* inside the lane's own claim it classifies clean, and the damage is human deception plus a shadow/duplicate type; the .cs compile glob picks it up, and `DUPLICATE_TYPE_SUSPECTED` (YELLOW) catches same-name redefinition. Proposal: reject non-ASCII in registry pattern literals; flag mixed-script path segments (UTS#39-style confusables scan) as a new reason code |
| T7 | **Backslash aliases** (`docs\x.md`) | a second spelling of a path that evades a glob | `normalize_repo_path` maps `\` → `/`; `git_evidence` re-normalizes every path git reports; a raw backslash surviving into `eval_paths` → `PATH_BACKSLASH_ALIAS` RED; `--raw -z` parsing means spaces/unicode in names never break tokenization | mitigated | Git on Linux never emits backslashes in tree entries, so the finding mostly fires on hand-crafted evidence files — correct fail-closed behaviour for a tampered local run |
| T8 | **Rename/landing games**: rename a frozen file into the lane, or a lane file onto a frozen path, or `T` type-change | move policy without an "edit" | renames/copies carry both endpoints; `ChangedFile.eval_paths = [old_path, path]` and stage 4 resolves **each** — so `FROZEN_CONTRACT_TOUCHED` fires on the departing side and on the landing side; `Operation.RENAME/DELETE` are first-class in the manifest | mitigated | Git rename detection is similarity-based (`-M -C --find-copies-harder`): a copy+delete with <50% similarity looks like an unrelated create+delete — still both classified, so the *ownership* conclusion is unchanged; only the `operation` label degrades |
| T9 | **Symlink / gitlink escape** | land content inside a protected path without naming it | **none**: `read_name_status_diff` parses the `--raw` status letter and ignores the mode fields, so a blob with mode `120000` (symlink) or `160000` (submodule) is classified as an ordinary create/modify **at the link's own path**; `T` (type change: file→symlink) maps to `MODIFY`. The engine never resolves link targets and never reads the working tree. What *is* covered: a rename whose new or old path is protected (§T8) and any case-variant of a protected prefix (§T5) | **not mitigated (residual)** | Realistic abuse is narrow but non-zero: create `Features/Mine/redirect` (mode 120000, blob content `../../Application/Rules/RuleEngine.cs`) inside your own claim, then have tooling or a human write through it — governance sees only a lane-owned path. Windows runners with `core.symlinks=false` materialize it as a text file, which limits CI impact but does not bound it. **Detection proposal (small, engine-local):** (1) capture the mode column in `read_name_status_diff` and emit a new RED reason `SYMLINK_INTRODUCED` for any `120000`/`160000` entry authored by a non-lead lane; (2) run the link target text (the blob *is* the target path) through `normalize_repo_path` and resolve it against the policy — a target that is frozen/architecture/integration, absolute, or escapes the repo root is RED; (3) in CI, scan the merge tree once (`git ls-tree -r --full-tree <head> \| awk '$1=="120000"\|\|$1=="160000"'`) and treat hits as RED. Until then this is a stated, accepted v1 limit (see GOVERNANCE_MODEL.md §11) |
| T10 | **Force-push / history rewrite** of the lane branch to hide a frozen edit or fake a base | discard evidence of earlier scope | engine has the check: declared `Base-Commit` not ancestor-equal to `merge_base` → `force_push_suspect` → `BRANCH_UNSAFE_REWRITE` RED | **wired gap on the CI path**: `pr-gates-v2.yml` classifies with `--no-git`, and `_branch_evidence_from_meta()` never sets `force_push_suspect` — so the RED cannot fire in CI. It fires only for callers that omit `--no-git` (and `governance_local.py` passes it too). What still bites: `BRANCH_IS_BASE` RED, `MISSING_EVIDENCE` on any topology gap, `BASE_METADATA_MISMATCH` YELLOW, `mergeability_clean` predicate | Medium. Fix: compute the ancestry in the workflow (`git merge-base --is-ancestor "$DECLARED_BASE" "$MERGE_BASE"`) and put a boolean in `meta.json`, or drop `--no-git` now that the job already has `fetch-depth: 0`. Note `branch_valid` on CI reduces to "GitHub reported a head SHA", which is still GitHub-authenticated |
| T11 | **Stale-lane resurrection**: revive a `merged`/`superseded`/`abandoned` lane (or grab a dead lane's paths) | take over territory without a lead PR | `NON_DELIVERING` statuses → `LANE_NOT_DELIVERING` RED for any PR from that lane; `CLAIM_BEARING` keeps `merged` in the set so a *dead* lane's exclusive claims still block a live lane (`UNKNOWN_FILE_OWNER`/`OWNERSHIP_CONFLICT`), which is precisely the anti-resurrection property at the ownership layer | mitigated **for delivery**, not for registry edits | `TRANSITIONS` forbids `merged → active` but the engine does not emit `LIFECYCLE_TRANSITION_INVALID` — reviving a status requires editing the registry (⇒ T1). Terminal statuses are enforced only by the transition table being documentation today |
| T12 | **Ownership squatting** — register a broad exclusive claim (`Application/**`) before a real lane does | block or absorb a neighbour | registry-level pairwise `patterns_conflict` scan over *exclusive lane* claims: two ACTIVE/PLANNED lanes with intersecting claims → finding (RED at v2, warning at v1); per-file `C>1` → `OWNERSHIP_CONFLICT` RED unless the lanes share an `overlap_groups` entry, which downgrades it to `CROSS_LANE_IMPACT` YELLOW ("one implementation must win", lead arbitrates); `'**'` outside the lead lane gets `DECLARED_SCOPE_TOO_BROAD`; the conflict predicate over-reports so overlaps are rarely missed | mitigated at v2 | v1 registries downgrade exactly these findings to warnings (documented compromise, see MIGRATION.md); `overlap_groups` is itself registry data (⇒ T1); a claim by a `paused`/`stalled` lane is inert-but-resurrectable |
| T13 | **Registry DoS** — a pathological `OWNERSHIP.yaml` (deep anchors/aliases, huge glob, thousands of lanes) | hang or OOM the gate | `yaml.safe_load` only, so no tag-based code execution; the literal-prefix trie bounds per-file matching; `benchmark` measures scaling to 500 lanes | mitigated against **code execution** | `safe_load` does **not** bound alias expansion (billion-laughs) and `_load_yaml` has no file-size cap. Impact is availability, not integrity: the job dies or classify exits 2 → fail-closed RED, never a pass. Proposal: cap registry bytes, reject `&`/`*` anchors outside comments |
| T14 | **YAML deserialization exploit** via registry tags (`!!python/object/apply: os.system`) | run code on the runner | `registry._load_yaml` uses `yaml.safe_load`; the workflow's inline helper also uses `yaml.safe_load`; the local runner deliberately uses a stdlib indentation scan *only* to derive a boolean hint, with the engine's loader authoritative | mitigated | PyYAML is installed with `pip3 install` at gate time without a hash pin — a supply-chain surface outside governance's own model; pin the version in a follow-up |
| T15 | **Command injection** through hostile paths, branch names, PR bodies | run shell on the runner | every git invocation is an argument array: `git_evidence._git(..., shell=False)`, `governance_local.git(...)` (`check=True`, tuple args), `livora_gates` `subprocess.run([...])`; no `shell=True` anywhere in the package; paths never reach a shell; PR body fields are regex-extracted into strings and only compared/printed | mitigated | The workflow's own shell steps interpolate `${BASE_SHA}`-style env values inside `<<'PY'` heredocs (quoted delimiter → no expansion) and use `set -euo pipefail`; residual lives in workflow shell code, not the engine |
| T16 | **Oversize / noise flooding** (10 000-file PR, or many tiny PRs) | exhaust reviewer attention, sneak a frozen line through | file-count cap → `OVERSIZED_CHANGE` YELLOW (never RED by size, deliberately — size is not an authority); empty changeset → `EMPTY_CHANGESET` YELLOW; per-file decisions are independent, so a big PR cannot hide an unclassified file (each file is resolved, and UNOWNED is RED) | mitigated | Yellow-hold fatigue is a human-process risk, not an engine one |
| T17 | **Bypass the check name** so `Integrate` sees a stale/foreign GREEN | merge without the gate | `roll-up status` is the *single* merge authority and the workflow pair (v1/v2) must never be active together; job names are load-bearing and documented; `integrate.yml` picks the newest run, hence the pin-check-name follow-up in `workflow-integration-notes.md` | mitigated by convention | While both workflows are enabled, the merge decision depends on which finishes last — an authority race the notes already list as blocking |
| T18 | **Secret exfiltration** (own or planted) and known-bad XAML/DI patterns | damage master even if scope-clean | 5 secret regexes on added diff lines → `SECRET_IN_DIFF` RED; hard tripwire `{/*` in `.xaml` → `TRIPWIRE_PATTERN` RED; soft tripwires hollow-DI / brush-on-color → YELLOW; new type defined in >1 file → `DUPLICATE_TYPE_SUSPECTED` YELLOW | mitigated | Scans are line-oriented on the *patch*, so a secret introduced via rename-similarity collapsing or a non-UTF8 diff (`errors="replace"`) can evade; deletion-only lines are not scanned by design |
| T19 | **Cross-lane spillover** (touch a neighbour's area without claiming it) | silently break another lane | paths: per-file ownership findings (T12); `overlap_groups`: a changeset spanning lanes of a group the submitter belongs to → `CROSS_LANE_IMPACT` YELLOW; PR-level scans are global, so content effects are caught regardless of scope | mitigated where declared | No compiler/symbol graph: D/X fan-out is heuristic path/overlap-group evidence only (v1 scope boundary); an undeclared shared consumer is invisible |

## 3. What "fail-closed" means operationally

1. **Unknowns are findings, not passes.** Every vocabulary gap has an explicit
   code: `UNKNOWN_LANE`, `UNKNOWN_FILE_OWNER`, `UNKNOWN_OPERATION`,
   `UNKNOWN_RULE`, `UNKNOWN_LIFECYCLE_STATE`, `MALFORMED_PATH`,
   `PATH_CASE_COLLISION`, `PATH_BACKSLASH_ALIAS`, `MISSING_EVIDENCE`,
   `MERGEABILITY_UNKNOWN`, `EMPTY_CHANGESET`, `REGISTRY_INVALID`,
   `REGISTRY_CORRUPTION`, `PARSER_FAILURE`. A path that matches nothing is RED; a
   file whose git operation cannot be determined is RED; absent evidence is RED.
2. **Absence of findings is not GREEN.** GREEN additionally requires all 14
   `compute_predicates()` to be proven (§GOVERNANCE_MODEL 2.2), and a failed
   predicate with no fired constraint is RED (`UNKNOWN_RULE`) — the engine
   distrusts its own reasoning.
3. **Policy is validated before it is used.** `validate-registry` runs as its own
   CI step and fails the job with the engine's constraint list, so a corrupt
   registry surfaces as a registry problem, never as a lane problem.
4. **Every crash is RED.** `livora_gates.main()` catches any unexpected exception
   and exits 2 with `PARSER_FAILURE`; the registry loader turns any parser escape
   into `REGISTRY_CORRUPTION`. A missing PyYAML is `REGISTRY_INVALID` — the loader
   refuses to fall back to a second YAML implementation, because a host-dependent
   parser would violate determinism.
5. **Numbers cannot rescue a PR.** `risk_factors` and `change_magnitude` are
   reported, never thresholded; only hard constraints and predicates move a verdict.
6. **Operational consequences to expect.** Fail-closed is deliberately
   lane-hostile: a fresh lane on a stale base, an unknown lifecycle word, a
   case-variant file name, a zero-file PR or an "unknown" mergeability all stop
   auto-merge. The cost lands on honest lanes (a hold, fixable in one push),
   which is the correct asymmetry: the alternative is paying it on `master`.
   The three states a lane can be in after a run: GREEN (integrate may merge),
   YELLOW (held for coordinator review; build results still reported),
   RED (hard stop, gate job fails, reason codes posted).

## 4. Threats to the model's own integrity

- **Docs drift from code.** These three documents describe a specific commit of
  `scripts/integration/governance/`. `Tests/Governance/test_properties.py` pins the
  load-bearing properties (normalization idempotence/crash-freedom, rule-order
  independence, per-file independence and order-freedom, protected-family case
  insensitivity, an exclusive conflict can never stay green, frozen violation
  dominance, fold monotonicity in the finding set, specificity ordering,
  task-hash determinism). If a change breaks one, the change is the bug or the
  docs must be re-derived from the source — never the reverse.
- **Prompt-vs-source divergences found while writing this model** (source wins;
  see the task report): 14 predicates (not 13); the case-insensitive second pass
  also covers `integration`; a lane-class path with a non-owner submitter is
  `UNKNOWN_FILE_OWNER` RED while lead-`'**'`-only paths are `LEAD_AMENDMENT`
  YELLOW; the force-push control is implemented but not wired on the `--no-git`
  CI path; `Claim.operations` and `Claim.expiry` are stored and never consulted;
  `PolicyClass.GENERATED` is assigned by family precedence but the engine reports
  generated-ness through `fd.generated` + `GENERATED_FILE_COMMITTED` YELLOW.
- **Single-point-of-trust honesty.** Governance's authority reduces ultimately to
  (a) the base commit's `OWNERSHIP.yaml` and (b) GitHub-authenticated facts
  (`baseRefOid`, `headRefOid`, `merge_base`, `mergeable`, `author.login`). The
  first is exactly what §0 protects; the second is why the workflow comment reads
  "derive from GitHub, never from agent claims".
