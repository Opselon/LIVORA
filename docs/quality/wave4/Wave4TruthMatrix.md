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
| 1 | platform (`platform`) | P1-B (02) | **REAL** (scaffold) | `/healthz`, `/api/v1/platform/{capabilities,version,me}` served by a live `WebApplicationFactory` host AND a real Kestrel boot on a throwaway file DB (curl receipts in `WAVE4-P1-GATE.md` E3–E5, 14 Sep); measured p95 0.13–0.20 ms (healthz) / ~8 ms (capabilities) — `PERF` lines of `Wave4PerformanceBudgetTests` | `PlatformScaffoldTests` (7), `Wave4PerformanceBudgetTests.Healthz/Capabilities` | Deep health is not wired to any real external provider yet; deployment story = none (row 14) |
| 2 | sync (`sync`) | P1-B | **REAL (endpoints) / PARTIAL (protocol docs)** — was NOT_IMPLEMENTED pre-P1-B | `POST /api/v1/sync/batch` + `GET /sync/{changes,operations}` live: anonymous 401 envelope, key-mandatory 400, byte-equal replay, revision conflict, per-user feed isolation — all asserted by `SecurityChecklistTests` S1/S2c/S4/S4b and `Wave4PerformanceBudgetTests.Sync_batch…` (p95 ~56 ms quiet-box vs 150 ms budget) at HEAD 1c7bd50; the LANE's own suite has 2 red tests (R-R3-4/5 in `requests/r3.md`) incl. the missing OpenAPI Idempotency-Key parameter | S2c/S4/S4b + sync budget + `Sync/*` (26 of 28 lane tests green) | OpenAPI parameter declaration missing (R-R3-4); device-side sync engine explicitly `not_implemented_in_this_lane` in the module's own capability report |
| 3 | identity (`identity`) | P1-C | **PARTIAL (routes live, lane's claimed tests absent)** | register/login/refresh/logout/sessions/account routes exist and behave: cross-account session revoke = 403 (S2a), export self-scoped against foreign `?user=` (S2b), session-row re-check gates every protected call (R3 probes through the real API) — but the lane header claims a `tests/…/Identity/` suite that DOES NOT EXIST (request R-R3-6); signing key is EPHEMERAL unless configured (capability report says `degraded`, honestly) | `AuthContractTests` (4) + this lane's S1/S1b/S2a/S2b | rotation/theft, lockout ladder, deletion lifecycle UNPINNED by tests until the lane lands its claimed suite |
| 4 | intelligence (`intelligence`) | P1-E | **FAIL at merge (shipped red)** | module live + capability report `selfcheck:ok` (boot curl, gate doc E5) and `/api/v1/intelligence/*` routes registered + auth-enforced (S1 sweep proves every one answers the 401 envelope) — but 26 of the lane's own Engines tests fail at HEAD 1c7bd50 (full list + causes: `requests/r3.md` R-R3-3) | `Engines/**` 26 failing / rest green | the lane must repair its suite before this row can read REAL/PARTIAL |
| 5 | verification (`verification`) | P1-E | **REAL (server slice)** | `/api/v1/verification/*` routes live, evidence ladder + refusal paths tested (`EvidenceModelTests`, `VerificationLadderTests` green in-suite); the §7 T2 word-rule now recognises the file's own `enum VerificationStatus` state machine (TRIPWIRES.md R3 §3) instead of flagging it | lane Verification tests + `PlantedViolationTests` | client-side rendering of the ladder is Wave-3c/Phase-2 scope |
| 6 | health (`health`) | client lanes (Wave 3b/3c) | **PARTIAL** (client only) | Health Connect provider + JSON store exist client-side (Wave 3b/3c suites, 1299 client tests green at HEAD 1c7bd50, `WAVE4-P1-GATE.md` E1); NO server health module or entities beyond the frozen core schema (S2h three-way absence) | `SqliteMigrationTests` prove `connector_states` table exists server-side | server-side health ingestion depends on row 2; provider truth must report `unconfigured` until a real device run |
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
| Honesty tripwires (contract §7) | **REAL** | `SourceHonestyTests` (15) + `PlantedViolationTests` (12) + `SourceTokenizerSelfTests` (18) + `SecurityChecklistTests` (17) + perf budgets (3) + gate tests (4) + persona catalogue (6): Quality scope 75/75 green at HEAD 1c7bd50+ (14 Sep, `WAVE4-P1-GATE.md` E2) after R3's documented scanner refinements (`TRIPWIRES.md` "R3 widenings") — planted fixtures still fire, real tree clean | heuristics, not a compiler: documented limits in `SourceTokenizer.cs` + `AnalyzerRefinements.cs` headers |
| Release-gate harness | **REAL (with documented self-reference policy)** | `Wave4GateTests.GateHarness…` executed the three §3 commands as children with on-disk receipts (`%TEMP%\livora-wave4-gates\20260914-175242\`): build PASS, client PASS (1299), server-tests FAIL reported + foreign failures enumerated, never self-hidden — policy in the test header, gate overall verdict in `WAVE4-P1-GATE.md` | client gate carries the narrow one-shot retry for the known flake (FLAKE-STORAGE-BUDGET.md); remove when the CI split lands |
| Server CI job | **REAL (applied; enforcing on PR #15)** | the `server-gates` job landed in `.github/workflows/pr-gates.yml` (lead PR after v1.3.0; deltas from the proposal recorded in the `CI-SERVER-JOB.md` header) | Gate 2 runs split 2a/2b after CI caught the harness self-nesting contention (third delta in `CI-SERVER-JOB.md`); receipts artifact path verified against the harness's `%TEMP%` writer |
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
