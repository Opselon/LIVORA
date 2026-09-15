# LIVORA Wave 4 — Audit Truth at the Phase-1 Baseline

**Auditor:** Agent 01 (w4-p1a-architecture). **Audited tree:** `wave4/p1a/architecture` @ `e7579a3`
(fork point `e501688` = master after Wave 3c completion). Clean worktree; nothing in this file is
carried over from a lane copy. Every row cites `file:line` or a command executed by me in this
worktree (Rule A). Verdicts use the truth-gate classes defined in §D.

> This document supersedes `docs/wave3c/STATUS-MATRIX.md` as the statement of truth for what exists
> **now**. The Wave 3c matrix is still the historical record; §A is its re-check plus deltas.

---

## A. Wave 3c matrix re-check at e7579a3 (rows 1–20)

| # | Claim | Re-check at e7579a3 (evidence) | Wave 3c verdict | Current verdict |
|---|---|---|---|---|
| 1 | 5-tab UI, onboarding, theme tokens | `AppShell.xaml` 5× `ShellContent`; `AppShell.xaml.cs:34` `AddLogTab`; `App.xaml.cs:28-29` onboarding switch; `Resources/Styles/LivoraColors.xaml` 101 `x:Key` | VERIFIED | **VERIFIED** (code-level) |
| 2 | EN/FA RTL live switch + Jalali digits | `App.xaml.cs:32,38,121` FlowDirection; localization suites inside the 1188-test run (executed green, §C) | VERIFIED | **VERIFIED** |
| 3 | State engine → PersonalState | `Application/State/UserStateService.cs:76` `GetStateAsync` | VERIFIED | **VERIFIED** |
| 4 | Rules + rec cap + plan adaptation | `Application/Planning/RecommendationService.cs:35-36` `Take(1)`+`Take(2)` | VERIFIED | **VERIFIED** |
| 5 | Weekly review refuses <3 days | `Application/Insights/WeeklySummaryService.cs:44` `Count < 3 → null` | VERIFIED | **VERIFIED** |
| 6 | Privacy inventory + delete-all | `Infrastructure/Security/PrivacyService.cs:120` `DeleteAllLocalDataAsync` | VERIFIED (catalog-slot caveat) | **VERIFIED** — caveat RESOLVED: `Infrastructure/Persistence/LocalDataCatalog.cs` + `Infrastructure/Persistence/Wave3b/LocalDataCatalogService.cs` now exist |
| 7 | AppVersion compare | `Application/Abstractions/IWave3Contracts.cs:65` | VERIFIED | **VERIFIED** |
| 8 | Wave-3 contracts as contracts | `Application/Abstractions/` 18 contract files, MAUI-free | VERIFIED | **VERIFIED** |
| 9 | Health source mock label | `Application/HealthData/SampleHealthProvider.cs:13`, `ManualOverlayProvider.cs:26` | VERIFIED | **VERIFIED** |
| 10 | Intelligence mock label | `Infrastructure/IntelligenceProviders/SampleIntelligenceProvider.cs:14` | VERIFIED | **VERIFIED** |
| 11 | Manual entry store/overlay | `Application/HealthData/ManualOverlayProvider.cs:26` composed as the `IDataProvider` | VERIFIED | **VERIFIED** |
| 12 | Update feed + UpdateService | `MauiProgram.cs:107-111` registrations | VERIFIED (live fetch manual) | **VERIFIED** (same asterisk) |
| 13 | Reminders / notifications | `MauiProgram.cs:131-147` (`Plugin.LocalNotification`); `ReminderEngine` still has no direct unit test | VERIFIED (code) / engine PENDING tests | **unchanged** |
| 14 | ThemeService | `Infrastructure/Settings/ThemeService.cs:30` | VERIFIED | **VERIFIED** |
| 15 | Log tab / editors / chart / responsive | `Presentation/Views/Log/SleepTrendChart.cs:31` (SkiaSharp 4.152, `LIVORA.csproj:66-67`); routes `MauiProgram.cs:178-185,350-352` | VERIFIED (code), runtime PENDING | **unchanged** |
| 16 | Real health providers + OAuth | `grep -rniE 'healthconnect|garmin|fitbit|oauth'` → only enum slots/machine tags (`Application/Activities/WorkoutNormalizer.cs:296-300`) **plus** an Android Health Connect bridge SHELL: `Infrastructure/HealthProviders/HealthConnectAndroidBridge.cs:81` returns `ApiNotBundled`, `:147` `PendingCategory => ApiNotBundled`; gated `#if ANDROID` at `HealthConnectProvider.cs:124-130` | PENDING | **PARTIAL** — shell + honest probe real; AndroidX client, Apple/Garmin/Fitbit, OAuth all still MISSING |
| 17 | Real LLM interpretation | **Advanced since 3c matrix:** real OpenAI-compatible HTTP transport merged — `Infrastructure/IntelligenceProviders/Wave3c/OpenAiCompatibleChatProvider.cs:110-127` (gated on `Usable(config) && Enabled == true`); built-in endpoint is **plain HTTP** `http://sub.legoten.com:4455/v1` (`EmbeddedGatewayConfigService.cs:23`), key is an obfuscated built-in blob (`EmbeddedGatewayConfigService.cs:114` `DecodeBuiltInKey`, self-documented "obfuscation != encryption" `GatewayKeyStore.cs:10-20`), **off by default, fail-closed** (`EmbeddedGatewayConfigService.cs:101-110`) | PENDING | **PARTIAL** — transport REAL, live answer path UNCONFIGURED-by-default, never probed in CI |
| 18 | Cloud sync / accounts | Client: `CloudAuthService.cs:36` `IsBackendConfigured => false` (hardcoded), `NoopSyncTransport.cs:39` `IsConfigured => false` — honest stubs. Server scaffold now exists (§B) but has ZERO identity/sync feature endpoints. Client slot text still at `Application/Abstractions/IAccountsAndDataContracts.cs:19` | PENDING (by design) | **PENDING** — P1-B/C/D deliver it |
| 19 | Store packaging | `LIVORA.csproj:24` `WindowsPackageType=None`; `release.yml:109-143` APK/AAB only; no MSIX/Play-store signing | REFUTED | **REFUTED (holds)** |
| 20 | App-launch smoke automation | `grep -rniE 'appium|winappdriver|smoke' .github/workflows` → no hits | REFUTED | **REFUTED (holds)** |

**A-tally:** 15 rows VERIFIED (1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 — with the 3c asterisks for
#13 engine tests and #15 runtime), 2 rows advanced PENDING→PARTIAL (16, 17), 1 row PENDING (18),
2 rows REFUTED and holding (19, 20).

---

## B. Wave 4 platform baseline at e7579a3

Class per §D. "REAL" = executed/verified by me here.

| # | Surface | Evidence | Verdict |
|---|---|---|---|
| B1 | Module seam (two-phase, key-unique, failure containment) | `server/src/Livora.Server/Modules/IFlivoraModule.cs:22-35`; `ModuleRegistry.cs:57-124`; exercised by `PlatformScaffoldTests` incl. throwing-module containment (executed green §C) | **REAL** |
| B2 | Versioned routing + honest 404 + correlation id | `IFlivoraModule.cs:61-68` prefix enforcement; `Program.cs:105-116`; `Platform/Correlation.cs:32,40-42` (sanitised `X-Correlation-Id`) | **REAL** |
| B3 | Problem envelope (RFC-9457 + `code`), code→status map | `Application/ApiProblem.cs:15-77`; `Platform/Problems.cs:24-63`; unknown code ⇒ 400 never 200 | **REAL** |
| B4 | Frozen auth surface: JWT mint/validate, `livora.uid`/`livora.sid`/roles claims, policy set, 401/403 envelope | `Platform/LivoraAuth.cs:41-42,47-88,123-157,178-203`; live proof `/api/v1/platform/me` `PlatformModule.cs:60-70` + 4 auth tests (executed §C) | **REAL** |
| B5 | Signing key posture: base64≥32 config, else ephemeral with honest `Ephemeral` flag | `LivoraAuth.cs:90-111`; no key in `server/src/Livora.Server/appsettings.json` → ephemeral is the default state | **UNCONFIGURED** (by design; never claim prod) |
| B6 | Core persistence: 5 tables (`users`,`auth_sessions`,`audit_events`,`connector_states`,`sync_operations`), text-GUID keys, unique idempotency/federated indexes | `Infrastructure/Persistence/Entities.cs:15-142`; `LivoraDbContext.cs:27-108`; migration `ModelSnapshots/20260914000911_InitialCore.cs:14-130` creates exactly these 5; FK/WAL via `SqliteConnectionInterceptor`; 5 migration tests green (§C) incl. unique `IX_users_GoogleSubject` filtered index | **REAL** |
| B7 | `IModelContribution` + freeze-before-build (shared schema for 16 lanes) | `LivoraPersistenceExtensions.cs:20-59`; `Program.cs:45` `Freeze()` after module services, before `Build` | **REAL** |
| B8 | Capability endpoint with a *probed* DB state | `PlatformModule.cs:30-48`; `DatabaseProbe.ProbeAsync` `:99-116` answers only from `CanConnectAsync`; tests `Capabilities_report_lists_the_platform_module_with_a_live_database_state` + `Capabilities_exposes_no_fabricated_feature_keys` (executed §C) | **REAL** |
| B9 | Feature modules: `Modules/` contains ONLY `Platform/` | `ls server/src/Livora.Server/Modules` → `IFlivoraModule.cs, ModuleRegistry.cs, Platform` | **MISSING** (sync/identity/intelligence/verification = P1-B/C/E deliver them) |
| B10 | Server config dead-keys: `Identity:RefreshTokenLifetimeDays`, `Identity:AccessTokenLifetimeMinutes`, `Ai:*`, `Payments:*`, `Sync:*` are read by NO server code yet | `grep -rn` over `server/src` → only doc-comment mentions in `LivoraAuth.cs:19-22`; `ResolveKey` reads only `TokenSigningKey/Issuer/Audience/AllowInsecureHttp`; mint lifetime is the 15-min constant `LivoraAuth.cs:176` | **UNCONFIGURED** — P1-C must wire the identity ones, P1-B the sync ones; lanes must NOT redeclare the keys |
| B11 | Google OAuth / Google Calendar | No `ClientId` anywhere non-empty (`appsettings.json Identity:Google.ClientId=""`); zero calendar/google client code in `server/src` | **UNCONFIGURED** (owner decision: approved real path; code behind config, report `unconfigured` until the credential arrives; then flip to real BY PROBE, not by config presence) |
| B12 | Payments | `appsettings.json Payments.Provider="unconfigured"`; the only payment-related artifact is the frozen code constants `ApiProblem.cs:49-56`; no provider client exists | **MISSING / BLOCKED (no merchant credentials)** — per owner decision: real client + webhook-signature path is *coded* in Phase 2, driven by a labelled test double, status never above `blocked` |
| B13 | AI gateway posture on the server | Server `Ai` config empty + `Enabled:false` (`appsettings.json`); no server code reads it; the *client-side* built-in gateway is plain HTTP and off by default (§A row 17). Owner decision: stays as-is this phase — no new live-LLM dependency in CI | **UNCONFIGURED** (deterministic server engines are the truth source; AI explains) |
| B14 | Client→cloud seam (P1-D target) | `Application/Cloud/` and `Infrastructure/Cloud/` do not exist; `Tests/LIVORA.Tests.csproj:50` already includes `Infrastructure\Cloud\*.cs` (lead pre-wire, commit 3710385) so the lane needs no csproj edit | **MISSING** (intended — P1-D builds here) |
| B15 | Gates at baseline | §C — executed | **REAL** |

**B-tally:** 15 claims — REAL 8 (B1,B2,B3,B4,B6,B7,B8,B15), UNCONFIGURED 4 (B5,B10,B11,B13),
MISSING 3 (B9,B12,B14), PARTIAL 0, BROKEN 0.

---

## C. Gate evidence (executed in this worktree at e7579a3, before any change by me)

```
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q
  Passed! - Failed: 0, Passed: 1188, Skipped: 0, Total: 1188  (exit 0)
dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo
  0 Warning(s) 0 Error(s)  (exit 0)
dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q
  Passed! - Failed: 0, Passed: 21, Skipped: 0, Total: 21  (exit 0)
```

Known flake (owned by P1-F): `Tests/Wave3c/Perf/StoragePerformanceBudgetTests` (trait
`Wave3c-Perf`) failed once under parallel load at base, passed on rerun; budget already
recalibrated once at `637be84` as a catastrophic-regression guard. Do not delete the budget.

### C.2 Live host probe (executed by me at e7579a3, not just tests)

Command: `dotnet run --no-build -c Debug --urls http://127.0.0.1:5199` from
`server/src/Livora.Server/` (fresh worktree, no `livora.db` on disk), then curl:

| Probe | Verbatim result | Proves |
|---|---|---|
| `GET /healthz` | `{"status":"ok","service":"livora-server","version":"1.0.0.0","correlationId":"254fc…"}` | liveness + correlation (REAL) |
| `GET /healthz?deep=1` | `…,"database":"sqlite","modules":[{"key":"platform","state":"ok",…}],"endpointMappingFailures":[]` | only the platform module exists — no fake feature keys (REAL) |
| `GET /api/v1/platform/capabilities` | `"database":{"key":"database","state":"degraded","detail":"configured but not reachable","capabilities":{"provider":"Sqlite"}}` | **the state-claim law works on the real host**: a configured-but-absent SQLite file reports `degraded`, never `ok` — config presence is not a state (B5/B8 confirmed end-to-end) |
| `GET /api/v1/platform/me` (no token) | `HTTP/1.1 401` + `{"type":"https://livora.app/problems/unauthenticated",…,"code":"unauthenticated","correlationId":"a0edf…"}` | auth envelope middleware live, not just unit-tested (B4 REAL) |
| `GET /api/v1/nope/nope` | `404` + `"code":"not_found"` envelope | honest 404 live (B2 REAL) |

Server process killed afterwards (PID 8296, `taskkill -PID 8296 -F`); no DB file was created;
`git status` verified clean before the docs-only commits that followed.

**Reproduced in this lane (docs-only tree, post-commit, so NOT caused by P1-A changes):**
`StoragePerformanceBudgetTests.SyncQueue_10000Enqueues_Under400ms_NoopDrainKeepsAllPending`
failed 1/1 in a full run (1187/1188), failed 1/3 in filtered runs (6 tests, same assembly), then
the full suite passed **3/3 consecutive at 1188/1188** on the same commit. The 400 ms enqueue
budget is the flaky leg; the noop-drain-pending invariant it also asserts has never failed.
P1-F: quarantine/relax the *time* assertion (or move it to a perf-tagged CI job), keep the
pending-invariant assertion sharp. Evidence class for this row: **REAL (flake reproduced), not a regression.**

---

## D. Truth-gate classes (definitions every Wave-4 claim must use)

- **REAL** — exists AND was executed/probed by the author of this doc in this tree (or covered by an executed test).
- **PARTIAL** — mechanism exists and is honest, but at least one promised leg is missing/unprobed.
- **UNCONFIGURED** — code + seam exist; an external credential/provider is absent; the surface must answer `unconfigured`, never `connected`.
- **MOCK** — a labelled double standing in for real behaviour (label is mandatory).
- **MISSING** — no code.
- **BROKEN** — code exists but does not do what it claims.
- **REFUTED** — a marketing/plan claim that measurement disproves.

**State-claim law (applies to every lane, every UI, every report):** a word from
{connected, verified, paid, synced, live} is admissible ONLY with (a) a probe result or executed
transaction behind it and (b) a persisted `connector_states`/audit row carrying it. Config presence
alone is never a state. An unprobed `DependencyState.Ok` is a gate-RED tripwire (CONTRACT-P1 §7).

---

## E. Delta register (what changed vs the Wave 3c matrix, with SHAs)

| Delta | SHA range | Audit consequence |
|---|---|---|
| Wave 3c lane code completed on master | `a47857b` (PR#9 squash; the original squash had missed untracked files) | rows 6/16/17 re-checked above |
| Server platform scaffold | `328325d` | B1–B8 became REAL |
| Frozen contracts (Policies/ApiProblem/Paging) | `a2b6b97` | rendezvous point for all lanes |
| Auth pre-wire + `/platform/me` + 4 gate tests (server 17→21) | `f17ae9f` | B4 REAL; lanes may protect routes from day one |
| Client test project pre-wired for `Infrastructure/Cloud` | `3710385` | B14 has zero csproj friction for P1-D |
| Contract v2 (requests-per-lane, no-lane-migrations, provider decisions) | `e7579a3` | this document |

## F. Non-verifiable-in-this-environment (honest gaps; NOT claimed real anywhere)

1. MAUI app builds (windows/android TFMs) — lead owns these; I did not run them (contract §3).
2. Runtime behaviour of the plain-HTTP built-in AI endpoint `sub.legoten.com:4455` — never probed by me; shipping posture is off-by-default, so no claim is made either way.
3. Android Health Connect on-device flow — bridge reports `ApiNotBundled` by construction (row 16).
4. GitHub Actions/CI execution of the gate script and the pr-gates workflow — branches are not pushed by lanes.
5. The "Wave 4 brief §N" numbering cited in CONTRACT-P1 comments (e.g. §21/§27/§54) refers to the human-issued brief, which is not a file in this repo — contract rules are cited by CONTRACT-P1 section instead in all Wave-4 docs.
