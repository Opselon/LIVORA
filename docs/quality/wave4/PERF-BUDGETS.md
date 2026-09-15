# Wave 4 P1-F — Performance budgets (P1-F scope: platform surface + gate discipline)

**Every number below is read from an executed run on 2026-09-14, machine Windows 11 / 8 logical
cores / SDK 10.0.303, logs in the test-output of
`dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --filter FullyQualifiedName~PerformanceBudget`.
They are NOT production numbers and never will be from this harness:** in-process TestServer has no
network stack, no TLS, no disk contention, no cold JIT on a shared runner, and an in-memory provider.
They answer exactly one question: *does the code path have an algorithmic blow-up, and can we
regression-trip it?* Capacity planning requires a deployed target — which does not exist yet
(`Wave4TruthMatrix.md`, deployment row). Don't quote these numbers to anyone as "API latency".

## Measured (this HEAD)

| Surface | p50 | p95 | p99 | max | budget | n | status |
|---|---|---|---|---|---|---|---|
| GET /healthz | 0.13 ms | 0.20 ms | 0.28 ms | 0.49 ms | p95 < 50 ms | 200 | PASS (250× headroom) |
| GET /api/v1/platform/capabilities | 2.28 ms | 7.74 ms | 20.48 ms | 21.99 ms | p95 < 50 ms | 200 | PASS (6.5× headroom) |
| POST /api/v1/sync/batch (50 ops) | 38 ms | 56 ms | ~60 ms | 58-70 ms | p95 < 150 ms | 30-50 | **PASS, 2.7× headroom — R3 UPDATE: endpoint landed (P1-B), budget now MEASURED not asserted-absent.** Same code, contended windows: full-suite p95 235-470 ms, sibling-lane-saturating box 630-800 ms (1-op witness 540 ms) — disk-queue contention (fsync/WAL), not algorithmic; the test's retry + same-bottleneck witness + 15× scaling guard + hang ceilings encode that honestly (class header). CI's serial step (gate 3a shape) is where the tight 150 ms is enforced |

Method (as coded in `Quality/Wave4PerformanceBudgetTests.cs`, serialised with the gate harness via
`Wave4SerialCollection`): 10 warm-up requests, then 200 SEQUENTIAL requests sampled with
`Stopwatch`; only successful responses count as samples; the p95 is nearest-rank over the 200.

The capabilities endpoint is the interesting row: it runs a live EF `CanConnectAsync` probe + a
module-report walk per request, so its p99 tail (20 ms) is the honest price of "no cached health
lies" — acceptable at this volume; revisit when a Phase-2 module adds per-request external HTTP
probes (a provider with 200 ms RTT must not be probed inline per /capabilities request — the fix at
that point is a bounded probe cache with an age shown in the response, NOT a lie).

## Client-suite budgets (inherited, this lane observes only)

`Tests/Wave3c/Perf/StoragePerformanceBudgetTests.cs` (client, FROZEN for P1-F) carries six budgets;
its own `PERF` output lines print the measured values every run. At this HEAD the *values* are sound
(365-record warm load 1.6–4.4 ms vs 150 ms; projection 0.33 ms vs 300 ms; migration 8–50 ms vs
3000 ms; idempotent re-run < 5 ms) but two of them — `Allocation_100Loads` (60 MB) and
`SyncQueue_10000Enqueues` (400 ms) — are measured with process-wide counters / wall clocks while the
rest of the 1188-test assembly runs in parallel around them, which fails the class on ~19% of
one-command runs with zero code change. Full investigation, evidence and the proven two-step CI
split: **`FLAKE-STORAGE-BUDGET.md`**.

## Gate discipline (what "budget" means as a release rule)

1. A budget may only be widened with a measured justification in this file (numbers before/after,
   linked run). Never to make red green.
2. The CI shape must isolate timed budgets from parallel contamination (`CI-SERVER-JOB.md` gate 3a)
   — a budget measured in a CPU fight measures the fight.
3. Latency/throughput assertions live in serial collections; the gate harness itself must not run
   concurrently with them (single-flight + `wave4-serial`).
4. GC-mode tuning was investigated and is explicitly NOT the fix for the known flake (workstation
   vs server GC changes collection counts, not the attribution problem — see §2d of the flake doc).
