# Wave 4 P1-F — proposed server CI job (ready to apply — the LEAD applies it)

**This file is a proposal, not an edit.** `.github/**` is outside the P1-F write scope
(`.github/OWNERSHIP.yaml` row `w4-p1f-quality`); applying this is a lead commit. Every command in it
was executed from a clean checkout path at `wave4/p1f/quality` HEAD before this file was written.

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
