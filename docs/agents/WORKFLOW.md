# LIVORA multi-agent integration workflow

You are an autonomous agent working on this repo. Read this before touching anything.
Facts below are enforced by `pr-gates.yml` (the `controller-gate` check); violating
them does not "get caught later" — it gets your PR RED/BLOCKED.

## Prime rule
**Never push to `master`. Never merge your own PR.** You: branch → commit → PR →
gate handles the rest. The `Integrate` workflow is the only thing that moves master.

## Task states
`PLANNED → ASSIGNED → WORKING → READY_FOR_REVIEW → VERIFYING → READY_TO_MERGE →
MERGED → REVERIFIED`  (failure: `BLOCKED`, `RECOVERY_REQUIRED`, `REASSIGNED`)
- You move your task to READY_FOR_REVIEW by opening the PR (put the state line in it).
- VERIFYING/READY_TO_MERGE/MERGED/REVERIFIED are controller-set; never self-declare them.
- Agent-reported results are **claims**. Only controller runs are facts. The four
  epistemic classes: VERIFIED FACT / INFERENCE / AGENT CLAIM / UNVERIFIED RESULT.

## Before you start
1. `git fetch origin && git checkout -b agent/<wave>/<lane-slug> origin/master`
   (worktree strongly preferred: `git worktree add ../wt-lane origin/master`).
2. Confirm your lane exists in `.github/OWNERSHIP.yaml` (`lanes:`). No entry?
   Ask the coordinator to add it (a lead-only PR). You may ONLY edit paths in your
   lane's `owns:` globs.
3. Record the exact `Base-Commit` SHA — you must declare it in the PR.

## Hard scope rules
- `Application/Abstractions/**`, `Domain/Enums/**` = **FROZEN**. Edits = RED.
  Need a contract change? Write a proposal in your PR description; lead amends.
- `MauiProgram.cs`, `AppShell.xaml`, `Resources/Localization/AppResources*.resx`,
  `SettingsPage.xaml`, `SettingsViewModel.cs`, `Resources/Styles/**`,
  `*.csproj`, `.github/**`, `scripts/**`, `docs/agents/**` = **integration/lead-owned**.
  Do NOT edit. Deliver `APPEND:` blocks (exact code + anchor comment) in your PR
  body; the lead applies them on an `integration/*` branch PR.
  XAML anchors must be XML comments `<!-- ... -->` — never `{/* */}` (MAUIX2002).
- New localization strings → `wave-keys/<lane>.en.keys.xml` / `.fa.keys.xml` inside
  your owned dir (plain `<data>` XML, not .resx).
- No new NuGet packages. No secrets in code/logs/UI; the AI gateway key blob is
  owned by exactly one lane (see OWNERSHIP.yaml).

## The PR
Use the template (`.github/pull_request_template.md`); every `Key:` line is parsed.
One PR per lane. Draft PRs are skipped by the controller until ready.

### Governance contract (v2 engine — wave 5)
The gate now runs the layered governance engine
(`scripts/integration/governance/`, docs in
`docs/architecture/governance/GOVERNANCE_MODEL.md`). What changed for you:

- **Task identity is proven, not claimed.** `Task-Hash:` must carry the sha256
  (≥8 hex chars) of your lane's canonical task text from `OWNERSHIP.yaml`
  (`task` field, whitespace-collapsed + case-folded). Missing = RED. Changed
  task text under an unchanged lane id = TASK_DRIFT (YELLOW), or RED when the
  drift note mentions ownership/security/architecture responsibilities without
  lead re-authorization.
- **`Lane-Branch:` is required** and must match your lane's registered branch
  glob (e.g. `agent/w3c/lane03-*`). Branch spoofing = RED.
- **Lifecycle is enforced.** `merged`/`released`/`superseded`/`abandoned` lanes
  cannot deliver code (RED). `stalled` = YELLOW hold.
- **`Owned-Scope: **` is RED for lanes** — declare your lane's own globs.
- **Policy is read from the base commit**, so editing the registry inside your
  own PR can never re-authorize that PR; `.github/**` + `scripts/**` remain RED.
- Renames/deletes/copies are evaluated on BOTH sides of the rename; case
  variants and `..`-traversal of frozen/architecture paths are RED (paths are
  normalized formally — no substring matching anywhere).
- Every gate run publishes `governance-manifest.json` (per-file decisions,
  reason codes, hard constraints, GREEN predicates). Read the reason codes
  instead of guessing: `explain governance-manifest.json`.
- Self-check before pushing:
  `python3 scripts/integration/governance_local.py <your-branch> --base origin/master`
  (same engine, local git, no GitHub API).

## What the controller does on every push to your PR
1. `gate` job (ubuntu): re-derives changed files + mergeability from GitHub against
   **current** master, runs `scripts/integration/livora_gates.py`:
   - **RED** (conflict, frozen/architecture edit, unregistered scope, secret,
     brace-comment XAML, missing metadata) → PR blocked, diagnostics posted as a
     comment; fix and push — gate re-runs automatically.
   - **YELLOW** (shared-file/APPEND-heavy, scope drift, duplicate type, overlap
     group, >40 files, hollow DI, brush-on-color) → blocked from auto-merge, held
     for coordinator review (or manual override via Integrate dispatch).
   - **GREEN** → proceeds to build/tests.
2. `build-test` job (windows): checks out `refs/pull/N/merge` — your PR **on top of
   current master HEAD**, not your stale base — runs `dotnet test` (Release) AND
   `dotnet build LIVORA.csproj -f net10.0-windows...` (the app head, because the
   test project cannot see XAML/DI breaks; this caught real false-greens before).
3. `Integrate` (every 30 min + manual): merges the OLDEST PR whose gate is GREEN
   on its exact head SHA, squash, one per run (serialized).
4. `reverify` job: fresh checkout of the **new real master**, full tests + windows
   build; success → ledger `REVERIFIED`; failure → ledger `RECOVERY_REQUIRED` +
   an issue titled `RECOVERY_REQUIRED:` — **freeze all merges** until a coordinator
   clears it.

## If master moves under you
The gate always tests merge-state, so a stale base is not automatically fatal —
but if your `Base-Commit` is far behind or files shifted, rebase onto current
master yourself (your branch only; never force-push anyone else's branch).

## Merging another wave's outputs (lead/merger lanes)
Merge order is FIFO by PR creation among GREENs. If wave3b and wave3c both deliver
the same product area (see `overlap_groups` in OWNERSHIP.yaml), the coordinator
must hand-pick the merge order with `Integrate → run workflow → pr=<N>` before the
heartbeat reaches the loser.

## Emergency
- Your lane is compromised (wrong scope, secret written): close the PR, tell the
  coordinator, re-branch from fresh master. Do not "fix by force-push" shared refs.
- Master red: nobody merges (auto or manual) until the RECOVERY_REQUIRED issue is
  closed by the coordinator.
