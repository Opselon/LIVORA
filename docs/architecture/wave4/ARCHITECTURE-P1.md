# LIVORA Wave 4 — System Architecture (Phase-1 baseline, binding for Phase 2)

**Author:** Agent 01 (w4-p1a-architecture) @ `e7579a3`. **Status:** binding.
Phase-2 lanes build on this; re-deciding anything here requires a request in
`docs/architecture/wave4/INTEGRATION_REQUESTS-P1.md` and a lead ADR amendment — not a silent divergence.
Truth-gate classes and current verdicts: `docs/audit/wave4/AUDIT-P1-BASELINE.md`. Contracts:
`docs/architecture/wave4/CONTRACT-P1.md` §5c + `docs/contracts/wave4/CAPABILITY-MAP.md`.

---

## 1. One-paragraph shape

LIVORA stays **one MAUI client + one ASP.NET Core host + one SQLite/Postgres database**. The host is
a versioned modular monolith: `Program.cs` (frozen) composes platform middleware, then discovers
`IFlivoraModule` implementations by assembly scan — a lane adds a folder, never edits a shared file.
Schema is shared through `IModelContribution` (frozen before first model build). Cross-cutting truth —
auth, errors, correlation, capability reporting — is pre-wired and frozen, so the only way a feature
can be "connected" is by probing, persisted into `connector_states`, and reported through
`/api/v1/platform/capabilities`. There are no microservices, no event bus, no speculative abstraction.

## 2. Component diagram (text; boxes exist today unless marked PHASE-2)

```
                     ┌────────────────────────────── MAUI client (LIVORA.csproj) ─────────────────────────────┐
                     │ Presentation (EN/FA, RTL)                                                            │
                     │   ▼                                                                                  │
                     │ Application/  ── rules, planning, state, sync queue (Application/Sync/SyncQueue.cs) │
                     │ Infrastructure/                                                                    │
                     │   ├─ Security: ConsentStore, LocalPasscodeService, SecureStorage(DPAPI),           │
                     │   │            GatewayConfigService + GatewayKeyStore (obfuscation, off-by-default)│
                     │   ├─ IntelligenceProviders/Wave3c: OpenAiCompatibleChatProvider (plain HTTP,       │
                     │   │            built-in endpoint, USER-ENABLED) + SampleIntelligenceProvider      │
                     │   ├─ HealthProviders: HealthConnectProvider + Android bridge SHELL (ApiNotBundled) │
                     │   ├─ Cloud/ …                                        ◄── PHASE-1 P1-D fills this   │
                     │   │     LivoraApiPort (typed, camelCase, ProblemCodes→l10n)                        │
                     │   │     + token store (ISecureStorageService) + offline queue (reuses SyncQueue)   │
                     │   └─ Security/CloudAuthService + NoopSyncTransport ── DELETED by P1-D when the     │
                     │        real seam lights up (their doc-comment says deletion is the only path)      │
                     └───────────────────────────────┬──────────────────────────────────────────────────────┘
                                         HTTPS /api/v1 (JWT bearer, X-Correlation-Id, Idempotency-Key)
                     ┌───────────────────────────────▼──────────────────────────────────────────────────────┐
                     │ Livora.Server (single host)                                                          │
                     │  Platform (frozen): Correlation · Problems(+AuthEnvelope) · LivoraAuth               │
                     │  ModuleRegistry ── ConfigureServices(pre-Build) → Freeze() → MapEndpoints(post-Build)│
                     │  Modules: platform(REAL) · sync(P1-B) · identity(P1-C) · intelligence(P1-E)         │
                     │           verification(P1-E) · [marketplace/community/commerce/… Phase-2]          │
                     │  /healthz(+deep) · /api/v1/platform/capabilities (probed DB + live module reports)  │
                     ├──────────────────────────────────────────────────────────────────────────────────────┤
                     │ LivoraDbContext (one) = 5 core tables + frozen IModelContributions                   │
                     │   SQLite (local/CI, FK+WAL interceptor) │ Npgsql (deploy) — no provider branching    │
                     └───────────────────────────────┬──────────────────────────────────────────────────────┘
                                                     │ server-side verifier/converter clients (behind config)
                    Google OAuth id-token · Google Calendar API · payment provider (webhook + signature)
                         UNCONFIGURED (no client_id/secret)        BLOCKED (no merchant credentials)
```

## 3. Module ownership map (Phase 1)

| Module key | Route segment | Lane | Table space (entity → table, via contribution) | CapabilityKeys |
|---|---|---|---|---|
| `platform` | `/api/v1/platform/*` | lead (REAL, frozen) | core tables only | `platform` |
| `sync` | `/api/v1/sync/*` | P1-B | uses core `sync_operations`; adds `SyncEntityState` → `sync_entity_states` (revision current-state, so `/sync/changes` doesn't scan the log) | `sync` |
| `identity` | `/api/v1/auth/*`, `/api/v1/account/*` | P1-C | uses core `users`,`auth_sessions`,`audit_events`; adds `RefreshTokenReuseSignal`→`auth_reuse_signals`, `DeletionRequest`→`deletion_requests` | `identity` |
| `intelligence` | `/api/v1/intelligence/*` | P1-E | adds `IntelligenceRun`→`intelligence_runs` (validated AI output audit, no raw health payloads) | `intelligence` |
| `verification` | `/api/v1/verification/*` | P1-E | adds `EvidenceRecord`→`evidence_records` (soft-delete; the durable proof rows behind "verified") | `verification` |
| (client seam) | — | P1-D | no server tables; `Application/Cloud/**` + `Infrastructure/Cloud/**` | consumes all |
| (quality) | — | P1-F | no tables; gates/harness/tripwires | guards all |

Table names above are the LANES' proposals frozen by this document, so contribution collisions and the
lead's single `Wave4P1Schema` migration are predictable. **No lane runs `dotnet ef` or writes
migration files** (CONTRACT-P1 §4); each lane files `migration-ready | <entity types>` instead.
P1-B/P1-C/P1-E: if your final entity list differs, that IS a request (R-P1x-n), not a divergence.

## 4. Request lifecycle (one honest request, end to end)

1. Client: typed port (`P1-D`) builds request — bearer token from secure storage, `X-Correlation-Id`
   optional (server mints if absent), `Idempotency-Key` on every mutating call.
2. `CorrelationMiddleware` → `AuthEnvelopeMiddleware`→ JWT validation (`LivoraAuth`, strict: issuer,
   audience, lifetime, single key) → `RequireAuthorization(Policies.X)`.
3. Handler: reads identity ONLY via `ctx.User.UserId()/SessionId()/RolesOf()`; **compares row owner id**
   (policy never proves ownership — IDOR gate, one test per lane); answers via `Problems.Of` or DTO.
4. Anything thrown → 500 + `internal_error` + correlation id, exception text to log only.
5. Failure mapping is by `code`, never prose: client localises `ProblemCodes` (en+fa), renders the
   missing thing per product law 3 (safe degradation).

## 5. Sync protocol (the one that makes offline honest)

- Client is source of truth for *intent*; server is source of truth for *state*. Mutations go through
  `POST /api/v1/sync/batch` (contract §5c): per-op `operationId` unique per user
  (`IX_sync_operations_UserId_OperationId` already enforced — `LivoraDbContext.cs:100`),
  `baseRevision` optimistic compare, outcomes `applied|conflict|rejected|duplicate`.
- Replay of a batch with the same `Idempotency-Key` returns the ORIGINAL result; different body under
  the same key = 409 `idempotency_key_reuse_mismatch` (code frozen in `ApiProblem.cs:22`).
- `/sync/changes?since=` reads the current-state table P1-B contributes (§3) — the operation log stays
  insert-only history, never a read model.
- Until the real transport lands (P1-D), `NoopSyncTransport` keeps records `Pending` — the queue never
  marks `Synced` on a fake success (its doc-comment is the contract).

## 6. Identity & sessions (server-authoritative)

- Register/login/refresh/logout per §5c; refresh tokens stored as SHA-256 hash only
  (`AuthSession.RefreshTokenHash`, unique index); rotation is mandatory and reuse of a rotated-out
  token revokes the whole session family + writes `audit_events` theft signal (`RevokedReason`).
- Account delete = scheduled, reversible-while-pending (`DeletionRequestedAtUtc`, code
  `deletion_pending`), executed later by operator path; export = §5c shape with per-section
  `"source": "server"|"not_implemented"` — empty is honest, fabricated fixtures are gate-RED.
- Google path: `AccessTokenMint` stays the only mint; `Identity:Google:ClientId` empty ⇒ 503
  `provider_unconfigured` (never a fallback "demo login"). Verifier is real code behind config —
  flip to `ok` in capability report ONLY after a live verification probe succeeds (truth-gate law, audit §D).

## 7. Truthfulness spine (how "connected" becomes a fact)

```
config present ──► NOT a state (never enough)
probe/executed call ──► DependencyState ──► ModuleHealth.Report() (live, per request)
                     └─► connector_states row (per user, per provider; state vocabulary in Entities.cs:103-110)
                          └─► /api/v1/platform/capabilities ──► client connector screen (P1-D) renders exactly it
```
`ModuleRegistry` downgrades a module that failed endpoint mapping — it can never report `Ok`
(`ModuleRegistry.cs:112-120`). P1-F's tripwires enforce the rest (CONTRACT-P1 §7).

## 8. Deterministic engines vs AI (boundary is code, not vibes)

- Server intelligence (P1-E): entitlement, verification state, scheduling constraints, payment state
  are computed by deterministic code with golden tests. `intelligence` module may *explain* state;
  every output passes a validator before it is persisted, and an AI run that fails validation answers
  `ai_output_rejected` — never half-applies.
- Client keeps Wave-3c posture (product law 2): rules engine owns recommendations; the OpenAI-compatible
  provider stays user-enabled, plain HTTP, off-by-default — unchanged this phase by owner decision.

## 9. Client seam (P1-D) — what replaces the stubs

`Application/Cloud/` = ports + DTO mirrors of §5c (MAUI-free, compiles into Tests). `Infrastructure/Cloud/`
= `HttpClient` impl (`IHttpClientFactory`-free: a typed `HttpClient` with injectable `HttpMessageHandler`
for tests — same shape the Wave3c chat provider uses so the pattern is proven), token store on
`ISecureStorageService`, offline queue bridging `Application/Sync/SyncQueue` ↔ `/sync/batch`.
DI delivery = `// WAVE4-DI:` APPEND block; strings = `wave4-keys/lane-p1d.{en,fa}.keys.xml`.
Deleting `CloudAuthService`/`NoopSyncTransport` registrations is the ONLY way the client may show a
connected account state; their file comments say so, and `Wave3DiIntegrityTests` marker-counting rules
apply to the new region (never paste a copy of an existing marker line).

## 10. Persistence conventions (shared model, no collisions)

Text-GUID "N" PKs · `DateTimeOffset` UTC `…AtUtc` · explicit `HasMaxLength` · unique indexes where
idempotency matters · soft delete (`DeletedAtUtc`) for evidence/audit · no secrets/PII in audit
metadata · contributions configure ONLY own types (a collision with a core type fails the model —
intended tripwire). One schema truth: merged contributions → lead's single `Wave4P1Schema` migration.

## 11. Bilingual & a11y law for every surface Phase 2 adds

Server-authored strings are codes/keys, never prose (client localises). Every new client string ships
en+fa through the wave-keys XML flow; RTL via existing `FlowDirection` plumbing; Jalali/Persian digits
via `IFormatService`. There is no English-only path.

## 12. Phase-2 boundaries this doc settles (do not re-decide)

1. Phase-2 marketplace/community/commerce/personalization/nutrition/calendar/screentime modules attach
   through the SAME seam + contribution path as Phase 1 — new keys, new folders, no shared-file edits.
2. Entitlement/creator/moderation decisions read server tables; a client-sent `tier`/`isCreator` flag
   is never authoritative (frozen in `Authorization.cs:9-12`).
3. Payments stay `blocked(no merchant credentials)` until the owner supplies credentials; the webhook
   receiver + signature verifier are real code exercised by a labelled test double in CI — the double
   is MOCK, the signature path is REAL, and only a provider-confirmed transaction can write a
   `connector_states`/purchase row that says `paid`.
4. Health/Calendar/ScreenTime connectors on the server follow the Google pattern: real converter code,
   `unconfigured` until probed. Health Connect on Android stays the client's local path (Phase-1
   unbundled-client posture unchanged; a Wave-4 lane may add the AndroidX client — that flips the
   bridge probe, nothing else).
5. The AI gateway stays off-by-default plain HTTP (owner decision) until a separate owner decision; no
   CI may depend on a live LLM; every AI artifact is labelled and validated (§8).
6. One host, one DB, offset-paged lists, `PageRequest`/`PagedResult` — a "we need a service per
   feature" proposal is out of scope law, not an ADR question.

## 13. Risk register (top 6, with the lane that must care)

| Risk | Who | Mitigation in this design |
|---|---|---|
| Model-contribution freeze ordering breaks a lane's schema | P1-B/C/E + lead | `Freeze()` timing is in frozen `Program.cs:45`; lanes register in `ConfigureServices` only; lead's single migration verifies all types |
| Sync current-state drift (log ≠ state) | P1-B | dedicated `sync_entity_states` table + §5 protocol test |
| IDOR via "policy = permission" confusion | every server lane | `Policies.OwnsResource` documented as intent marker; IDOR test per lane is contractual (CONTRACT-P1 §5b) |
| Refresh-token theft | P1-C | rotation + family revocation + `auth_reuse_signals` row + audit event |
| Fake-connected UI regressions | P1-D/P1-F | §7 spine + tripwires; deletion of stubs is the only unlock |
| Secret creep via built-in gateway blob | P1-F | existing scanner + honesty gate; blob stays obfuscation-labelled, never called encrypted |
