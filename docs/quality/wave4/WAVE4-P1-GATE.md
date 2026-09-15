# LIVORA Wave 4 — Phase 1 RELEASE GATE (WAVE4-P1-GATE)

**Executed by:** R3 (quality repair lane, inheriting P1-F's gate duty) · **Date:** 2026-09-14
**Worktree/HEAD:** `C:/Users/Capsizer/source/repos/LIVORA-w4/r3` @ branch `wave4/p1r/r3-quality`
(base `1c7bd50`, lane commits `80e141f…6dbfaa4`) · **Machine:** Windows 11, 8 logical cores, SDK 10.0.303, local NVMe.
**Rule A applies to every row:** the verdict cites a command THIS lane executed in THIS worktree, and
the verbatim output is pasted below the table. `PASS` = executed and verified at this HEAD. Nothing
here says "the code exists".

## Verdicts (per Wave-4 capability + gate cross-cuts)

| # | Capability | Verdict | Basis (executed — see E-rows below) |
|---|---|---|---|
| 1 | platform | **PASS** | E3/E4/E5: real host boots on a throwaway file DB, `/healthz` + `/api/v1/platform/capabilities` answer 200 with live probe data; E2: scaffold + perf tests green |
| 2 | sync (server endpoints) | **FAIL (lane's own tests red; surface itself verified live)** | E6: anonymous 401, key-mandatory 400, replay byte-equal, IDOR feed isolation, budget measured green — all by Quality probes (E2); but 2 of the LANE's tests are red: openapi-Idempotency parameter absent, and a malformed-JSON fixture of its own (E7 names them → requests R-R3-4/5) |
| 3 | identity | **PARTIAL PASS** | E6: register/login/session routes enforce §5c behaviour (S2a cross-account revoke = 403, S2b export self-scoped, S1/S1b envelope auth) — Quality probes green; the lane's own claimed `tests/…/Identity/` suite DOES NOT EXIST (R-R3-6) so its invariants (rotation/theft, lockout ladder) are unpinned |
| 4 | intelligence | **FAIL** | Module live (E5 reports `selfcheck:ok`) but the engines lane's own tests are 26-red at merge (E7, request R-R3-3) |
| 5 | verification | **PASS (server slice)** | E2: lane tests `EvidenceModelTests` + `VerificationLadderTests` + Verification module tests run green inside the suite; §7 T2 now sees its state machine honestly (TRIPWIRES.md R3 §3) |
| 6 | health (server) | **NOT-IMPLEMENTED** | E6 S2h: `/api/v1/health/records` 404 + no capability module + absent from OpenAPI (three-way honest absence) |
| 7 | calendar | **BLOCKED (no credentials)** | `Identity:Google:ClientId/ClientSecret` empty in appsettings.json (honest absence, Tripwire2 green); S2h proves no route; GoogleLogin path reports `unconfigured` |
| 8 | screen_time | **NOT-IMPLEMENTED** | no route, no module (S2h/OpenAPI sweep E6/E2; Tripwire6 scans every served path) |
| 9 | nutrition | **NOT-IMPLEMENTED** | S2h three-way absence |
| 10 | personalization | **NOT-IMPLEMENTED** (as a distinct capability; deterministic engines live under #4) | no surface in the OpenAPI enumeration (E2 S1 sweep enumerates the document) |
| 11 | marketplace | **NOT-IMPLEMENTED** | S2h three-way absence |
| 12 | community | **NOT-IMPLEMENTED** | S2h three-way absence |
| 13 | commerce | **BLOCKED (no merchant credentials)** | `Payments.Provider="unconfigured"`, empty `ApiKey`/`WebhookSigningSecret` (appsettings.json, verified by Tripwire2 scans + Scope_sanity — E2); S2h proves no purchase route |
| G1 | client suite | **PASS 1299/1299** | E1 verbatim (baseline grew 1188→1299 with the merged client lanes) |
| G2 | server suite | **FAIL 28/381 — 100% outside Quality/** (26 Engines + 2 Sync)** | E7 verbatim + enumerated class-by-class in requests/r3.md (R-R3-3/4/5); Quality/** alone is 75/75 (E2) |
| G3 | honesty tripwires (§7) | **PASS** | E2: SourceHonesty 15/15 incl. proof-of-fire self-tests, PlantedViolation 12/12 (planted fixtures fire; real tree clean), SecurityChecklist 17/17, PerformanceBudget 3/3; every scanner widening documented with its demanded evidence (TRIPWIRES.md "R3 widenings") |
| G4 | gate harness | **PASS (with documented self-reference policy)** | E8: three §3 commands executed as children with on-disk receipts; Wave4GateTests tolerates server-tests FAIL only when every failing test is outside Quality/** and enumerates them; own-scope failure = hard FAIL |
| G5 | storage perf flake | **DOCUMENTED, BUDGETS NOT DELETED** | FLAKE-STORAGE-BUDGET.md stands as written (26 measured runs, root cause, CI-split proven); `AllowKnownPerfRetry` kept until the split lands; E1 run passed without needing the retry |
| G6 | server CI job | **NOT-IMPLEMENTED (requested)** | CI-SERVER-JOB.md ready-to-apply; `.github/**` outside quality write scope (R-P1F-1 / R-R3-8) |
| G7 | deployment | **NOT-IMPLEMENTED** | no Dockerfile/IaC/publish profile in tree; every latency number here is in-process TestServer or single-host curl, never "production" |

**GATE OVERALL: RED — NOT SHIPPABLE.** Not because of this lane's scope (Quality 75/75 green, host
smoke clean, client 1299 green), but because the merged server suite carries 28 foreign red tests
(intelligence/engines lane 26 + sync lane 2) and the identity lane shipped zero of its
claimed invariant tests. The ten Quality failures assigned to R3 are each fixed and listed in
`requests/r3.md`'s cross-reference + TRIPWIRES.md.

## Verbatim evidence (every block executed in this worktree, 2026-09-14)

**E1 — client suite** (`dotnet test Tests/LIVORA.Tests.csproj --nologo -v q`, full output last lines):
```
Test run for C:\Users\Capsizer\source\repos\LIVORA-w4\r3\Tests\bin\Debug\net10.0\LIVORA.Tests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.

Passed!  - Failed:     0, Passed:  1299, Skipped:     0, Total:  1299, Duration: 3 s - LIVORA.Tests.dll (net10.0)
```

**E2 — Quality scope** (`dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo --no-build --filter "FullyQualifiedName~Livora.Server.Tests.Quality"`):
```
Passed!  - Failed:     0, Passed:    75, Skipped:     0, Total:    75, Duration: 3 m 20 s - Livora.Server.Tests.dll (net10.0)
```
(75 = SourceHonestyTests 15 + TripwireScannerSelfTests 18 + PlantedViolationTests 12 +
SecurityChecklistTests 17 + Wave4PerformanceBudgetTests 3 + Wave4GateTests 4 + PersonaFixtureCatalogue…;
includes the gate-harness child run inside, i.e. this green run itself executed E8.)

**E3 — host boot (throwaway file DB, EnsureCreatedOnStart)**:
```
$ ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5199 Database__EnsureCreatedOnStart=true Database__Provider=sqlite ConnectionStrings__Livora="Data Source=$TEMP/livora_w4/gate-throwaway.db" dotnet run --project server/src/Livora.Server/Livora.Server.csproj --no-launch-profile
info: Livora.Server[0]
      LIVORA server starting: modules=identity,intelligence,platform,sync,verification database=sqlite mappingFailures=0
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://127.0.0.1:5199
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
```
Single-host PID 7424 killed after the probes (`taskkill -PID 7424 -F` → `SUCCESS: The process with
PID 7424 has been terminated`); no other process touched; throwaway DB deleted afterwards.

**E4 — curl GET /healthz (anonymous):**
```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Server: Kestrel
X-Correlation-Id: 9be901e8b6e941c2a4d298bf7ecf05ab

{"status":"ok","service":"livora-server","version":"1.0.0.0","correlationId":"9be901e8b6e941c2a4d298bf7ecf05ab"}
```

**E5 — curl GET /api/v1/platform/capabilities (abridged to the module array; full body in
`%TEMP%/livora_w4/host_smoke2.txt`):**
```
HTTP/1.1 200 OK
X-Correlation-Id: 34b669742580484e957b7e006d5790a5

{"serverTimeUtc":"2026-09-14T18:00:21.9555529+00:00","apiVersion":"v1","modules":[
 {"key":"identity","state":"degraded","detail":"dev signing key (tokens die with the process) — set Identity:TokenSigningKey for real use", …},
 {"key":"intelligence","state":"ok","detail":"deterministic engines computed the self-check vector in-process", …},
 {"key":"platform","state":"ok","detail":"platform surface always available when the host is up","capabilities":{}},
 {"key":"sync","state":"degraded","detail":"batch/changes endpoints mapped; persistence contribution registered; …"},
 {"key":"verification","state":"ok","detail":"five distinct trust rungs + all refusal paths verified by in-process fixed vectors", …}],
 "database":{"key":"database","state":"ok","detail":"connected","capabilities":{"provider":"Sqlite"}},"endpointMappingFailures":[]}
```
(Honest `degraded` on identity = ephemeral signing key — the absence reported, not hidden.
`mappingFailures=[]` + `database.state=ok` PROVEN by live probe, not assumed.)

**E6 — curl POST /api/v1/sync/batch, ANONYMOUS:**
```
HTTP/1.1 401 Unauthorized
Content-Type: application/json
Server: Kestrel
WWW-Authenticate: Bearer
X-Correlation-Id: a2676827302343c98c39d6d107b25635

{"type":"https://livora.app/problems/unauthenticated","title":"unauthenticated","status":401,"detail":"A valid access token is required for this endpoint.","instance":"/api/v1/sync/batch","code":"unauthenticated","correlationId":"a2676827302343c98c39d6d107b25635","errors":null}
```
Authenticated behaviour rows (key-mandatory 400, replay byte-equality, IDOR 403/feed isolation,
honest-absent surfaces) are asserted live by SecurityChecklistTests S1–S5 in E2.

**E7 — full server suite** (`dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo`):
```
Failed!  - Failed:    28, Passed:   353, Skipped:     0, Total:   381, Duration: 2 m 43 s - Livora.Server.Tests.dll (net10.0)
```
All 28 failing tests are outside `Quality/**` — `Engines/**` ×26 (BaselineStatePattern 4,
CrossDomainPipeline 8, PrioritisationScheduleFeedback 8, WeeklyAndExplanation 2, EngineState 1,
FusionEngine 1, EnginePersistence 1, EnginesApi 1 — note: EnginesApi and EnginePersistence share
the EF-translation root cause) and `Sync/SyncApiContractTests` ×2; request lines R-R3-3/4/5 carry
the per-test evidence. Quality/** within the same run: 0 failed.

**E8 — gate harness receipts** (`%TEMP%\livora-wave4-gates\20260914-175242\`, written by
Wave4Gate child processes during the E2 Quality-scope run; each log's own header + last line):
```
$ dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo
exit 0                                                     → GATE server-build: Pass

$ dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q   (child, --no-build)
exit 1
Failed!  - Failed:    28, Passed:   353, Skipped:     0, Total:   381, Duration: 1 m 35 s
                                                           → GATE server-tests: Fail — all 28 outside
                                                             Quality/** (enumerated by Wave4GateTests
                                                             per the self-reference policy)

$ dotnet test Tests/LIVORA.Tests.csproj --nologo -v q
exit 0
Passed!  - Failed:     0, Passed:  1299, Skipped:     0, Total:  1299, Duration: 2 s
                                                           → GATE client-tests: Pass (no flake retry needed)
```

**Budget measurements backing G3 row (quiet windows, same command twice):**
```
PERF /healthz: p50 0.13 ms · p95 0.20 ms (n=200)                     → budget 50 ms  PASS
PERF /api/v1/platform/capabilities: p95 7.74-8 ms isolated (n=200)   → budget 50 ms  PASS
PERF POST /api/v1/sync/batch (50 ops): p50 ~38 · p95 ~56 ms (quiet)  → budget 150 ms PASS (2.7× margin)
PERF POST /api/v1/sync/batch (50 ops): p95 235-800 ms under full-suite/sibling-lane disk contention
  → disclosed re-measurement + same-bottleneck 1-op witness + 15× scaling guard + hang ceilings
    (policy + all numbers documented in Wave4PerformanceBudgetTests header; budget VALUES unchanged)
```

## What would flip this gate GREEN

1. P1-E fixes its 26 Engines tests (R-R3-3) — the UTC-ms convention conversion is the common root
   in at least the BaselineState/EngineState family.
2. P1-B fixes its 2 tests (R-R3-4/5) and lands the OpenAPI Idempotency-Key parameter.
3. P1-C lands (or deletes the claim to) its invariant suite (R-R3-6).
4. P1-D repairs the manifest comments + ghost-test claim (R-R3-1/2; quality certification works
   around both, but a release gate should ship valid XML).
5. Lead applies CI-SERVER-JOB.md (R-R3-8) so every future lane diff is gated where it lands.
Then re-run E1+E2+E7 at the new HEAD and re-issue this document from the receipts.
