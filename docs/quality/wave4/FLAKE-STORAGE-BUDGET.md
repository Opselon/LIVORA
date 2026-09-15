# LIVORA Wave 4 — P1-F Flake Investigation: StoragePerformanceBudgetTests

**Lane:** P1-F (Agent 16, QA/release gate) · **Branch:** `wave4/p1f/quality` · **Base HEAD investigated:** `e7579a3`
**Machine:** Windows 11, 8 logical cores, .NET SDK 10.0.303, local NVMe, nothing else building during runs.
**Status of the owned file:** `Tests/Wave3c/Perf/StoragePerformanceBudgetTests.cs` is FROZEN for this lane —
everything below is evidence + a request to the lead, not an edit.

---

## 1. What was claimed and what was found

The brief describes one flake: `Allocation_100Loads_…` failed once under parallel load and passed on rerun.
**It is worse than reported, and the named test is not even the most frequent offender.** 53 full
`dotnet test Tests/LIVORA.Tests.csproj` runs were executed in isolated phases; the failure mode is a
family of two budget assertions inside the same class, and the client suite went red in **5 of 26
full-suite runs (~19%)** at this HEAD on this machine when it runs as one parallel command:

| Phase | Command shape | Iterations | Failures | Failing tests |
|---|---|---|---|---|
| A | full suite, class-filtered to `StoragePerformanceBudgetTests` only | 8 | **0** | — |
| B | full suite `dotnet test Tests/LIVORA.Tests.csproj` (fresh build) | 6 | 0 | — |
| C | full suite, `--no-build`, sequential back-to-back | 20 | **5** | 3× `SyncQueue_10000Enqueues_Under400ms` · 2× `Allocation_100Loads` |
| C2 | client suite WHILE a server-suite `dotnet test` competes for CPU | 6 | 0 | — |
| setup | first full run of the session | 1 | 1 | `SyncQueue_10000Enqueues_Under400ms` (919.9 ms vs 400 ms budget) |

Phase C detail: every iteration ran the full 1188; in all five failures exactly one test went red and
the other 1187 passed. No other class in the suite failed once in 41 full runs. The red is entirely
inside `Wave3c-Perf`.

Raw logs: `%TEMP%\livora-flake\{A_isolated,B_full,C_full,C2_client,C2_server}_*.log` + `progress.txt`,
`progressC.txt` (kept per the evidence rule; the numbers below are read back from these files, not
from memory). The B-vs-C difference is run-to-run luck of the parallel schedule (the contamination is
a lottery, see §2a), not a property of `--no-build`: across B+C, 5 reds in 26 full-suite runs.

## 2. Root cause (measured, not assumed)

**(a) `Allocation_100Loads` — process-wide GC attribution vs in-test parallelism.**
`GC.GetTotalAllocatedBytes(precise: true)` counts the WHOLE PROCESS. xunit parallelises test
*classes* within the assembly by default, so while this test measures its 100-load loop, other
classes (JsonFileStore round-trips, `MigrationRunner`, `ParallelWriters_20Way`'s own 20 `Task.Run`
writers, sync-queue churn) allocate in the same windows. The test defends itself with
best-of-three-windows, and the file's own comment records the contamination (12–53 MB). Measured here:

- class isolated (phase A): every window **exactly 12.73 MB**, all 8 iterations — the loop's own cost
  is deterministic and the 60 MB budget is a 4.7× margin over it. **The budget is correct.**
- full suite (phases B/C): windows range **15.5 → 134.5 MB**; best-of-3 saved 18 of 20 runs; the two
  that went red had best windows of **69.9 MB and 62.6 MB** — contamination exceeded the budget in
  *every* window of those runs, which the min-of-3 heuristic cannot survive. The failure is the
  measurement being wrong, not the code regressing: allocation per load is fixed by the JSON model
  (120 records ≈ 0.127 MB), it does not drift 5×.

**(b) `SyncQueue_10000Enqueues_Under400ms` — the same contention on the wall clock.**
Isolated enqueue times: 148–241 ms (phase A). In full-suite runs the same operation measured
167 → **920 ms**; the three phase-C failures landed at 454, 413 and 920 ms against the 400 ms budget.
The class contains a 20-writer × 6-round disk-contention test and a 500-record file migration; when
those run *concurrently* with the enqueue timing loop on 8 cores, IO + thread-pool queueing alone
doubles the measured time. The other 400 ms-headroom budgets in the file (`ParallelWriters`,
`Migration`, warm `HistoryStore`) never went red because their budgets are 6–15× their isolated cost
while these two sit at 1.5–4× — they are the thin edges of the same design, not new bugs.

**(c) Cross-process contention (server suite competing for CPU) is NOT the driver.** Phase C2 ran the
full client suite while a complete server-suite `dotnet test` was live, 6/6 green, best allocation
windows 20.5–58.4 MB — *inside* the budget and close to the no-contention distribution. One
`dotnet test` client run saturating its own testhost is what pollutes it, not a neighbouring process.

**(d) GC mode is not the knob.** `Tests/bin/Debug/net10.0/LIVORA.Tests.runtimeconfig.json` carries no
`ServerGarbageCollection`/`ConcurrentGarbageCollection` entry (checked); the testhost runs default
workstation concurrent GC. Switching to server GC would change collection *counts*, not the fact that
`GetTotalAllocatedBytes` attributes other threads' allocations to this window. No GC-mode change can
fix an attribution problem. Toggling TieredCompilation moves JIT warm-up timing only.

## 3. Recommendation (the request line, for the lead)

**Isolate, don't relax, don't tighten.** The budgets are honest measurements of a quiet process; the
CI command runs them inside a noisy one. The proven fix needs zero repo edits — split the gate step:

```bash
# step 1 — the timed class, alone and serialised (measured: 6/6 green, ~2 s)
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q --filter "category=Wave3c-Perf" \
  -- RunConfiguration.MaxParallelThreads=1
# step 2 — the remaining 1182 in full parallel (measured: 1182/1182 green, ~4 s)
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q --filter "category!=Wave3c-Perf"
```

Both commands were executed at HEAD `e7579a3` with this lane's changes present: **6 + 1182 = 1188,
zero failures, combined wall time ~6 s** — the split costs no meaningful time and removes the only
known flake from the release gate. The class already carries `[Trait("category", "Wave3c-Perf")]`,
so the filter is the file's own declared seam. (If the lead prefers ONE command, the equivalent is a
lead-side `[CollectionBehavior(DisableTestParallelization…)]`/collection pin inside `Tests/Wave3c/**`,
but that is an edit to a lane-owned frozen file while the CI split is zero-edit — hence the split.)

Follow-ups recorded as request lines in `docs/quality/wave4/REQUESTS-P1F.md`:
- **R-P1F: apply the two-step client gate in CI** (snippet in `CI-SERVER-JOB.md`), blocking: no
  (the harness's narrow retry below covers the interim).
- **Gate-harness stopgap:** `Wave4Gate` retries the client gate exactly ONCE and only when *every*
  failing test is in `StoragePerformanceBudgetTests` (a single unexpected name anywhere in the failure
  list → hard FAIL; policy pinned by `Wave4GateTests`). Remove this retry once the two-step CI split
  lands — keeping it after would hide a *real* regression in that class behind a coin flip.
- **Do NOT delete or widen the budgets** (the brief's instruction and this data agree): 60 MB is
  4.7× the measured isolated cost, 400 ms is ~1.7×; they are regression guards, not wishes.

## 4. Verdict for the release log

`flake_verdict = REAL FLAKE, NOT A CODE BUG: process-wide GC attribution + intra-assembly test
parallelism inflate two budgets in StoragePerformanceBudgetTests; the class failed 5 of 26
full-suite runs (~19%) as one command and 0 of 11 runs when isolated; fix = two-step CI gate
(proven), budget values stay as written.`

## 5. R3 status note (14 Sep, merged P1 tree)

Still the authoritative flake record — nothing here deleted or contradicted. Additions from the
merged tree: (a) the client gate ran 1299/1299 green without the retry firing in R3's runs
(`WAVE4-P1-GATE.md` E1/E8), the narrow `AllowKnownPerfRetry` stays in `Wave4Gate` until the
two-step split lands (R-P1F-1/R-R3-8 unchanged); (b) the SAME contention family now has a
server-side twin — `Quality/Wave4PerformanceBudgetTests.Sync_batch…` measures the landed
POST /sync/batch path and inflates 4-14x under the full-suite disk queue (per-fixture SQLite files
fsyncing in parallel, p95 56 ms isolated → 235-800 ms contended); its one-disclosed-retry +
same-bottleneck 1-op witness + 15x scaling-guard policy is documented in that class header and is
the server-side application of this document's law: **isolate or witness, never widen the budget.**
