# LIVORA Wave 4 — Phase 1 Truth Matrix (P1-F)

**Purpose:** the single honest status board for the 13 Wave-4 capabilities. Rule A of the contract
applies to every row: a claim cites `file:line`, a command, or a test that was EXECUTED, and
`REAL` means *executed and verified at this HEAD* — never "the code exists".
**Provenance:** seeded by Agent 16 (QA/release gate) from what Phase 1, lane P1-F can personally
prove at HEAD `e7579a3` + this lane's commits. Lanes P1-B…P1-E update their own rows as they land;
an unclaimed row stays at whatever Phase 1 actually proves, and most of them are NOT what the
feature list implies. That gap IS the point of the document.

**Vocabulary (mirrors `DependencyState` + the report format):**
- `REAL` — executed and verified in this repo at this HEAD (cite in Evidence)
- `PARTIAL` — a real, tested slice exists; the capability's full contract does not
- `MOCK` — deterministic labelled double only; no external provider is reached or claimed
- `BLOCKED` — code path exists or is designed, but an external dependency (credentials, config) gates it
- `NOT_IMPLEMENTED` — nothing exists; the API says so (honest 404 / `not_implemented` state), no fake surface

**Test column** = tests this lane can run right now (`dotnet test server/tests/Livora.Server.Tests/…`).

| # | Capability (key) | Owner | Status | Evidence (executed) | Tests guarding it | Limitations / what Phase 2 must finish |
|---|---|---|---|---|---|---|
| 1 | platform (`platform`) | P1-B (02) | **REAL** (scaffold) | `/healthz`, `/api/v1/platform/{capabilities,version,me}` served by a live `WebApplicationFactory` host; measured p95 0.20 ms (healthz) / 7.74 ms (capabilities) — `PERF` lines of `Wave4PerformanceBudgetTests`, 14 Sep 2026 | `PlatformScaffoldTests` (7), `Wave4PerformanceBudgetTests.Healthz/Capabilities` | Deep health is not wired to any real external provider yet; deployment story = none (row 14) |
| 2 | sync (`sync`) | P1-B | **NOT_IMPLEMENTED** (server) | `POST /api/v1/sync/batch` answers the scaffold's honest `not_found` envelope and `/openapi/v1.json` advertises no sync path — both asserted by `Wave4PerformanceBudgetTests.Sync_batch_…`, which prints `NOT_IMPLEMENTED` instead of faking a number | that test + `Tripwire6` versioning gate | Full §5c batch/changes protocol, `Idempotency-Key` replay semantics, revision model. Client-side queue + metadata already exist from Wave 3c (server-side counterpart missing) |
| 3 | identity (`identity`) | P1-C | **PARTIAL** (seam only) | JWT bearer + all `Policies.All` registered and exercised: `AuthContractTests` prove anonymous → `unauthenticated` envelope, tampered token → rejected, valid token → `livora.uid/sid/roles` claims (`server/tests/.../Platform/AuthContractTests.cs`). NO register/login/refresh/logout/sessions endpoints exist at this HEAD | `AuthContractTests` (4) | §5c auth endpoint set, refresh rotation + theft revocation, Google sign-in (row 7 gating), account delete/export. Signing key is EPHEMERAL unless `Identity:TokenSigningKey` is configured (LivoraAuth.cs:96-110) — no such config exists in-repo |
| 4 | intelligence (`intelligence`) | P1-E | **NOT_IMPLEMENTED** (server) | `Modules/` contains only `Platform` (enumerated: `server/src/Livora.Server/Modules/` = IFlivoraModule.cs, ModuleRegistry.cs, Platform/). Client-side Wave 3c orchestration exists but is out of scope of this server gate | none yet (nothing to test) | Model/agent surface, `ai_unavailable`/`ai_output_rejected` paths, context budget enforcement. Wave 3c AI gateway stays as shipped; no new live-LLM dependency in CI (contract §3) |
| 5 | verification (`verification`) | P1-E | **NOT_IMPLEMENTED** | no module, no engine wired, `verification_rule_unknown` has no caller (grep of server/src) | none | deterministic rule engine + evidence model; AI may explain, never decide (product law 2) |
| 6 | health (`health`) | client lanes (Wave 3b/3c) | **PARTIAL** (client only) | Health Connect provider + JSON store exist client-side (Wave 3b/3c suites, 1188 client tests green); NO server health module or entities beyond the frozen core schema | `SqliteMigrationTests` prove `connector_states` table exists server-side | server-side health ingestion depends on row 2; provider truth must report `unconfigured` until a real device run |
| 7 | calendar (`calendar`) | P1-B/04 | **BLOCKED** (no credentials) | `Identity:Google:ClientId`/`ClientSecret` are empty strings in `server/src/Livora.Server/appsettings.json` — the honest shape; a tripwire-verified absence, not a bug | `Tripwire2` (no secret literals) + `Scope_sanity` | Real Google Calendar verifier/converter behind config; must report `unconfigured` until the secret arrives, then flip by probe (contract §3 decision) |
| 8 | screen_time (`screen_time`) | P1-E | **NOT_IMPLEMENTED** | no code under any name (server grep) | none | provider + normalisation + bilingual rendering |
| 9 | nutrition (`nutrition`) | P1-E | **NOT_IMPLEMENTED** | no code | none | as row 8 |
| 10 | personalization (`personalization`) | P1-E | **NOT_IMPLEMENTED** | no code | none | must derive from deterministic state, not from a model's guess |
| 11 | marketplace (`marketplace`) | P1-D/12 | **NOT_IMPLEMENTED** | no module; `Application/`/`Infrastructure/` have no marketplace code at this HEAD (directory listing) | none | catalog, listing, search surfaces |
| 12 | community (`community`) | P1-13 | **NOT_IMPLEMENTED** | no module | none | reports/moderation states, `report_already_open` etc. unexercised |
| 13 | commerce (`commerce`) | P1-14 | **BLOCKED** (no merchant credentials) | `Payments.Provider="unconfigured"`, `ApiKey=""`, `WebhookSigningSecret=""` in appsettings.json — the honest absence, verified by `Scope_sanity`+`Tripwire2`; no payment endpoint exists | none beyond the tripwires | real provider client + signature verification coded, test double drives the flow, status `BLOCKED` until a merchant token exists (contract §3) |

## Cross-cutting rows (not capabilities; they gate releases the same way)

| Area | Status | Evidence | Limitation |
|---|---|---|---|
| Honesty tripwires (contract §7) | **REAL** | `SourceHonestyTests`: 5 source tripwires + OpenAPI versioning gate + proof-of-fire self-tests + scope sanity, all green; scanner pinned by 18 `SourceTokenizerSelfTests` cases; a companion planted-fixture lens (`PlantedViolationTests` + `Quality/TripwireFixtures/`) fires on committed violations — server suite total at this HEAD: 70/70 | heuristics, not a compiler: documented limits in `SourceTokenizer.cs` header (raw-string holes, ':' handling) |
| Release-gate harness | **REAL** | `Wave4GateTests` executed the three §3 commands as child processes: build PASS, server tests PASS (70 at this HEAD), client tests PASS; per-gate evidence logs written under `%TEMP%\livora-wave4-gates\` | client gate carries the narrow one-shot retry for the known flake (see FLAKE-STORAGE-BUDGET.md); remove when the CI split lands |
| Server CI job | **NOT_IMPLEMENTED → requested** | ready-to-apply job in `CI-SERVER-JOB.md`; `.github/**` is outside this lane's write scope | lead must apply it — until then the backend has NO CI gate at all |
| Client perf flake | **DIAGNOSED** | 26 full-suite runs measured: 5 reds (3× SyncQueue, 2× Allocation), 0/8 red when the class runs isolated; root cause = process-wide GC counters + intra-assembly parallelism | fix is a CI step split (proven), budgets unchanged; P1-F may not edit the frozen client file |
| Deployment / hosting | **NOT_IMPLEMENTED** | no Dockerfile, no IaC, no publish profile in the repo (file search) | production numbers do not exist yet: every latency figure in this lane is in-process TestServer, which is NOT a production number (stated in the test's own header and output line) |
| Secret management | **PARTIAL** | CI diff scanner exists (`scripts/integration/livora_gates.py` SECRET_PATTERNS) + this lane's whole-tree scanner (`Tripwire2`); `UserSecretsId` present, no key committed (verified by both) | `Identity:TokenSigningKey` has no production source yet — ephemeral key in every environment today |

## What Phase 1 PROVES, in one paragraph

The backend host boots, routes, correlates, degrades safely, answers RFC-9457 problems with stable
codes, enforces a versioned-only public surface, and can be lied to by nobody without a red test:
that is everything rows 1–3 + the cross-cutting rows claim. Everything marked NOT_IMPLEMENTED or
BLOCKED is exactly that — twelve of thirteen capabilities have no server-side existence yet, and the
two that do exist partially are gated on credentials that do not exist. No number in this document
was estimated; the perf values, test counts, and failure rates are read back from executed runs, and
this lane's own tests print their measurements so a future edit of this table can be checked against
them.
