# Wave 4 P1-F — proposed server CI job (APPLIED by the lead — note below)

> **APPLIED (lead, post-v1.3.0, this PR).** The `server-gates` job below landed in
> `.github/workflows/pr-gates.yml` with two honest deltas from the proposal:
> (1) the `needs`/receipt references use this workflow's actual job id `gate` (the check it
> publishes is named `controller-gate`), and the checkout ref carries the `|| inputs.pr`
> fallback so `workflow_dispatch` runs resolve the merge ref too; (2) the receipt-upload path
> is `${{ runner.temp }}/livora-wave4-gates` — the harness writes to `Path.GetTempPath()`,
> which on hosted runners IS the runner temp; the proposal's `docs/quality/wave4/**` glob
> would have uploaded the docs instead of the logs. Commands otherwise verbatim. Note:
> `pull_request` runs execute the workflow definition from the PR's MERGE commit, so this job
> is enforced on this very PR — which is exactly how the third delta below was caught.
> Side effect the lead intends: the split makes the client perf budgets
> deterministic in CI, which also removes the disclosed GateHarness flake-inheritance
> (Wave4Gate's client child mirrors Gate 3a/3b; see FLAKE-STORAGE-BUDGET.md).
>
> **Third delta, found by the first CI enforcement (PR #15):** the single "server tests" step
> went red on `Wave4GateTests.GateHarness` — the harness nests its own builds + full suites,
> and with xunit's parallel collections running against it on the 4-core runner, the nested
> `Wave4PerformanceBudget` Quality tests measured self-contention rather than the product
> (local reproduction on the exact merge ref `f30e37e`: full suite 427/428 with the harness the
> only red; harness-excluded 427/427; full-suite re-run 428/428 — contention, not code). Gate 2
> is therefore split like the client gate: **2a** runs the suite WITHOUT the harness, **2b**
> runs the harness ALONE and serialised. Every test still executes exactly once per job; no
> budget widened, no test removed, retried, or weakened.

**Original framing, kept for the record:** this file was a proposal, not an edit —
`.github/**` is outside the P1-F write scope (`.github/OWNERSHIP.yaml` row `w4-p1f-quality`);
applying it is a lead commit. Every command in it was executed from a clean checkout path at
`wave4/p1f/quality` HEAD before this file was written, and re-executed at `7723b23` during the
v1.3.0 verification battery (server 428/428, client 6 isolated + 1293 parallel, builds 0W/0E).

## What today's workflows do NOT cover

`pr-gates.yml` + `ci.yml` run the CLIENT suite and the MAUI builds; neither builds
`server/src/Livora.Server` nor runs `server/tests/Livora.Server.Tests`. The backend scaffold is
therefore gated only by lane discipline — which is precisely what a gate must never be. The Wave 4
contract's §3 three commands need a home in CI before Phase 2 lands twelve lanes on top of it.

## Proposed job (add to `.github/workflows/pr-gates.yml`; merge-ref checkout pattern copied from
## the existing `verify merge state` job so the same stale-baseline protection applies)

```yaml
  server-gates:
    name: server gates (build + server tests + client perf-isolated suite)
    needs: controller-gate
    if: needs.controller-gate.result == 'success' || needs.controller-gate.result == 'skipped'
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
        with:
          ref: refs/pull/${{ github.event.pull_request.number }}/merge
      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      # Gate 1 — backend compiles clean; server/Directory.Build.props sets
      # TreatWarningsAsErrors=true, so 0W is part of the verdict, not a nicety.
      - name: server build (warnings are errors)
        run: dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo

      # Gate 2 — the real-host contract suite (fixture = WebApplicationFactory, in-memory DB).
      - name: server tests
        run: dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q

      # Gate 3a — client timed budgets, ISOLATED and serialised.
      # WHY: StoragePerformanceBudgetTests reads process-wide GC counters and wall-clock budgets
      # while xunit runs every other class in parallel against them — ~19% of one-command full runs
      # go red without any code change (docs/quality/wave4/FLAKE-STORAGE-BUDGET.md, 26 measured
      # full-suite runs). The class ships [Trait("category","Wave3c-Perf")]; the split costs ~2 s.
      - name: client perf budgets (isolated, serial)
        run: >
          dotnet test Tests/LIVORA.Tests.csproj --nologo -v q
          --filter "category=Wave3c-Perf"
          -- RunConfiguration.MaxParallelThreads=1

      # Gate 3b — the remaining 1182 client tests in full parallel (measured: unaffected by 3a).
      - name: client tests (rest, parallel)
        run: dotnet test Tests/LIVORA.Tests.csproj --nologo -v q --filter "category!=Wave3c-Perf"

      # Upload receipts: the gate harness writes one log per gate under %TEMP%\livora-wave4-gates.
      - name: attach lane report artefacts (if any lane produced one)
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: server-gate-logs
          path: 'docs/quality/wave4/**'
          if-no-files-found: ignore
```

Notes for the lead, honest about what this job does and does not prove:

1. **Both client steps share one build** (the first builds, the second reuses — add `--no-build` to
   3b only if the runner cache proves it safe; measured locally the rebuild between them costs ~2 s).
2. **The isolation step is the documented stopgap.** The durable fix is either the split staying (fine
   — it is explicit and cheap) or a collection/serialisation attribute inside `Tests/Wave3c/**`
   (a lane-owned file; this lane may not edit it — see REQUESTS-P1F.md R-P1F-2).
3. **What is NOT proposed here deliberately:** MAUI workload install (already owned by the existing
   `App build` job — duplicating it doubles every PR's wall time), a server publish/deploy step
   (nothing deploys yet — see `Wave4TruthMatrix.md`: deployment is BLOCKED/NOT_IMPLEMENTED, and CI
   pretending otherwise would be the honesty tripwire's favourite subject), and any `dotnet ef`
   migration step (contract §4: only the lead generates migrations).
4. `dotnet test ... Quality` honesty tripwides are PART OF GATE 2 already — `SourceHonestyTests`
   runs the repo scans inside the server suite, so a violating lane diff reddens PRs with no extra
   CI surface. That is by design: one gate command, not a shrine of special jobs.
5. Expected wall time from measured local runs: build ~7 s warm / 2–4 min cold, server suite ~30 s,
   3a ~25 s with build, 3b ~15 s — well inside the existing jobs' envelope.
