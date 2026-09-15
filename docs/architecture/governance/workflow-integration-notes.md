# Workflow integration notes — PR Gates v2 (governance engine)

Audience: the integration lead merging wave5/governance, and whoever operates
`.github/workflows/`. This file describes how the new governance gate
(`pr-gates-v2.yml` + `scripts/integration/governance/` + `scripts/integration/livora_gates.py`)
adopts or replaces the v1 gate (`pr-gates.yml`), and the exact switch/rollback
procedure. It changes nothing itself — v1 stays untouched until the lead merges.

## What v2 is

- Same triggers as v1: `pull_request` (opened/synchronize/reopened/ready_for_review
  → master) + `workflow_dispatch {pr}`.
- Same job skeleton: `gate` → `build-test` → `report`. **The job names are
  load-bearing and must not be renamed**: `integrate.yml` resolves the merge
  authority by check-run name `roll-up status`, which is the *job name* of
  `report` (GitHub publishes a job's check-run under its `name:`). Keeping the
  three names identical keeps integrate.yml untouched and working.
- The gate job builds the engine's evidence dir (`gate-inputs/`):
  `pr.json` → `meta.json` (incl. `merge_base`, `commits_behind/ahead`,
  `architecture_changed_after_fork`), `changed.json` (git `--name-status -M -C`,
  the engine's preferred rich evidence with real operations), `changed.txt`
  (legacy fallback list), `additions.diff` (`gh pr diff --patch`, git fallback
  for >20k-line PRs).
- `validate-registry` runs as its **own step** and fails the job with the
  engine's constraint list when `.github/OWNERSHIP.yaml` itself is broken — a
  corrupt registry must never masquerade as a lane problem.
- `classify` runs with `--no-git --json --manifest-out governance-manifest.json
  --as-of <utc-iso>`, exit 0→GREEN / 1→YELLOW / 2→RED; the manifest is uploaded
  as artifact `governance-manifest-PR<n>` and the reason codes are posted as a PR
  comment. RED hard-stops the gate job; YELLOW proceeds so build results still
  reach the coordinator, and `report` holds YELLOW (exit 1 + warning).
- PyYAML is installed in both the gate job and the governance test step: the
  registry loader is fail-closed (`REGISTRY_DEPENDENCY_MISSING` → RED) without it.

## The single roll-up authority problem

Both workflows define a job named `report` with name `roll-up status`. If both
were active on the same PR head, `integrate.yml`'s lookup —

> `gh api .../commits/$HEAD/check-runs | select(.name=="roll-up status") | sort_by(.startedAt) | last`

— picks the *newest* run, so the merge decision would silently depend on which
workflow happened to finish last. That is an authority race, and governance
engines must not race. Two options:

1. **Pinned check name (requires editing integrate.yml — preferred end-state).**
   Have integrate.yml select on a workflow-unique check name (e.g.
   `roll-up status v2` for v2 runs) or filter by `check_suite.app`/workflow name.
   Deferred to the lead because integrate.yml is out of this lane's file scope.
2. **One workflow enabled at a time (what this lane implements).** Until the pin
   lands, exactly one of `pr-gates.yml` / `pr-gates-v2.yml` may be active. v2
   therefore keeps the `report`/`roll-up status` naming and the gate job name
   `controller-gate-v2` (v1's is `controller-gate`), which makes the pair of
   active workflows auditable at a glance in the PR checks list.

## Switch procedure: v1 → v2

Do this on master via a lead-only PR (both files are `architecture_owned`):

1. Merge the wave5/governance code PR (engine + CLI + v2 workflow + docs). With
   both workflow files on master, **v1 and v2 would both fire** — so the merge PR
   that introduces v2 must land it *inert*: keep only one active by disabling the
   other in the same commit (step 2). Nothing else changes; branches without a
   registered lane simply get YELLOW/RED from v2 exactly as v1 would RED them.
2. Disable v1 (pick one, same PR):
   - comment out / delete `pr-gates.yml`, **or** (reversible, keeps history)
     add to v1's `on:` block a guard, simplest being to flip its workflow
     `concurrency` off is not enough — recommended: rename `pr-gates.yml` →
     `pr-gates.yml.disabled` is NOT enough either (Actions re-scans `.yml` only,
     so renaming to any non-`.yaml/.yml` extension *does* deactivate it).
   - Chosen convention: move the disabled file to
     `.github/workflows/disabled/pr-gates.yml` (Actions does not load workflows
     in subdirectories; the file stays in the repo as the rollback source).
3. Open a canary PR from a scratch branch and confirm, in order:
   - `controller-gate-v2` check appears and its job summary shows the manifest
     verdict; artifact `governance-manifest-PR<n>` downloads;
   - deliberately break `.github/OWNERSHIP.yaml` in the canary → the
     `Validate governance registry` step fails the gate job with the constraint
     list (this proves fail-closed registry handling);
   - lane-scope RED case (touch `Application/Abstractions/**`) → hard stop;
   - YELLOW case (unregistered branch) → build-test still runs, `roll-up status`
     = failure with the warning annotation;
   - GREEN case → `roll-up status` = success, and the next `Integrate` heartbeat
     merges it. **The canary's GREEN merge is the acceptance test for the
     integrate.yml contract**, since integrate.yml was not modified.
4. Record the switch in `docs/agents/ledger.json` (integration commit).

## Rollback procedure: v2 → v1

1. Lead-only PR: move `.github/workflows/disabled/pr-gates.yml` back into
   `.github/workflows/`, and move `pr-gates-v2.yml` into `disabled/`. The two
   files never sit active simultaneously.
2. No branch protection or integrate.yml change is needed either way, because
   neither procedure renames the `report` job.
3. If a PR was mid-flight during rollback, re-run whichever workflow is active
   via `workflow_dispatch {pr}` — verdicts are pure functions of (registry@sha,
   evidence), so a re-run on the same head is deterministic given `--as-of`
   (CI passes the run start time; keep that if you need manifests byte-stable).

## What must change in pr-gates.yml itself when v2 is adopted

Nothing is edited in this lane, but the adoption diff to `pr-gates.yml` (if the
lead prefers a single workflow file over the disabled/ pattern) is exactly:

- `name: PR Gates` → `PR Gates v2`; keep `jobs:` keys `gate`/`build-test`/`report`
  and job `name:`s (`controller-gate`, `verify merge state …`, `roll-up status`).
- Add `pip3 install pyyaml` before any gate step (v1's inline python used stdlib
  only; v2's engine refuses to parse the registry without PyYAML — fail-closed).
- Replace the bare `python scripts/integration/livora_gates.py` invocation
  (v1 ran the legacy stdin contract) with the v2 sequence: evidence dir →
  `validate-registry` → `classify --input-dir gate-inputs --no-git --json
  --manifest-out governance-manifest.json --as-of "$GATE_AS_OF"`.
- Extend meta.json with `merge_base`, `commits_behind`, `commits_ahead`,
  `architecture_changed_after_fork` and emit `changed.json` (v2 does this; the
  inline python is copy-paste ready from `pr-gates-v2.yml`).
- v1 posts the verdict comment inside the classify step; v2 separates it so a
  comment failure (`|| true`) can never mask a classification result.
- Drop `potentialMergeCommit` from `gh pr view --json` (v1 collected
  `merge_sha`; v2's engine consumes `merge_base` topology instead — the
  potential-merge object is not evidence of anything the engine checks).
- Map `mergeable`: GitHub returns `MERGEABLE|CONFLICTING|UNKNOWN`; the engine
  expects `clean|dirty|unknown`. v2 maps it explicitly (anything unmapped →
  `unknown` → engine raises `MERGEABILITY_UNKNOWN`, never a guess). v1's
  `.lower()` was only correct because v1's script did not consume it.

## Local self-check for lanes

`python3 scripts/integration/governance_local.py <branch-or-ref> [--base master]`
derives the identical evidence files from a local checkout into `./gate-inputs/`
(no GitHub API) and runs the same classify command, propagating 0/1/2. Run it
before pushing. Stdlib-only, argument-array git calls, fail-closed on any
missing ref/registry.

## Known blockers (verify before enabling v2)

1. **`Tests/Governance` does not exist yet** on wave5/governance as of this
   writing — the `build-test` step `python3 -m pytest Tests/Governance -q` will
   fail (pytest exit 4), holding every PR at roll-up. The suite lands with the
   engine's test lane; the step is written to the agreed target path. Do not
   enable v2 until the directory exists on the merge candidate.
2. Registry `version: 2` (with `policy.base_branch`, `agent_bindings`,
   `generated:`, per-lane `task_id`/`task_revision`) is an uncommitted change in
   this worktree; v2's stricter validation expects it. `validate-registry`
   passes locally against the worktree copy — it must be committed by the lead
   in the same integration PR.
3. v2 hard-requires the *new* engine layout (`scripts/integration/governance/`);
   if the CLI regresses to the legacy single-file contract, the workflow's
   `--input-dir/--manifest-out/--as-of` flags stop existing and classify exits 2
   (usage error → RED). Acceptable: it fails closed, loudly, at the gate.
