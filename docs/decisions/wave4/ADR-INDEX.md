# LIVORA Wave 4 — ADR Register (Phase 1)

Index: `ADR-0001` … `ADR-0008`. Statuses: ACCEPTED = decided at `e7579a3`, binding for Phase 1+2.
Superseding an ADR requires a lead PR referencing this folder (lanes file a request instead).

| ADR | Title | Status |
|---|---|---|
| 0001 | Modular monolith: one client, one host, one database | ACCEPTED |
| 0002 | Shared schema by contribution; ONE lead-generated migration | ACCEPTED |
| 0003 | Auth is platform-owned; ownership is handler-verified | ACCEPTED |
| 0004 | Provider truth posture: Google approved-unconfigured, payments blocked, AI as-is | ACCEPTED |
| 0005 | The state-claim law: connected/verified/paid/synced require probe + persisted row | ACCEPTED |
| 0006 | Sync: client owns intent, server owns state; idempotent replay, revision conflict | ACCEPTED |
| 0007 | Machine codes over prose; bilingual en/fa is a gate, not a nicety | ACCEPTED |
| 0008 | Request-ledger governance: frozen files change only by lead amendment | ACCEPTED |

---

## ADR-0001 — Modular monolith: one client, one host, one database

**Context.** 16 lanes must build one product without a distributed-systems tax. Product law 6 forbids
microservices/event-bus/speculative abstraction. The scaffold proves the alternative runs:
`FlivoraModuleScanner` discovers modules by assembly scan (`Modules/ModuleRegistry.cs:20-44`) and
`Program.cs` (frozen) never needs a lane's edit.
**Decision.** One MAUI client, one `Livora.Server` host, one `LivoraDbContext` (SQLite local/CI,
Npgsql deploy). Features are folders (`Modules/<Feature>/`), not services. Cross-feature calls go
through frozen contracts or the shared DB, never through a neighbour's in-flight class.
**Consequences.** Startup failures degrade one module, never the host (`ModuleRegistry.cs:83-96`);
deployment is one process; the price is discipline: duplicate module keys throw on purpose
(`ModuleRegistry.cs:36-41`), and shared-schema collisions are caught by the model itself.
**Verified by:** executed at e7579a3 — 21/21 server tests incl. module-containment tests green.

## ADR-0002 — Shared schema by contribution; ONE lead-generated migration

**Context.** Five lanes need tables in one database. Concurrent `dotnet ef` migration files from lanes
would conflict irreparably, and EF caches the model per provider, so late registration is
nondeterministic (that is why `Freeze()` exists: `Program.cs:43-45`, commit `c3ed577`).
**Decision.** Lanes extend the model ONLY via `IModelContribution` registered from their module's
`ConfigureServices`; entity classes live in the lane's own Infrastructure folder. NO lane runs
`dotnet ef` or writes migration files. The lead generates ONE `Wave4P1Schema` migration from the
merged contributions at integration time; lanes prove their slice with `EnsureCreated()` on an
isolated temp SQLite file and file `migration-ready | <entity types>`.
**Consequences.** One schema truth, reviewable as one diff; the frozen `InitialCore` migration
(`ModelSnapshots/20260914000911_InitialCore.cs`) stays untouched. Integration-time duty: the single
migration must apply on BOTH providers — note the filtered unique index at `LivoraDbContext.cs:47`
(`HasFilter("\"GoogleSubject\" IS NOT NULL")`, valid partial-index SQL on SQLite and Postgres).
**Verified by:** `SqliteMigrationTests` (5 tests) green at e7579a3; freeze semantics exist in code
(`LivoraPersistenceExtensions.cs:33-50`).

## ADR-0003 — Auth is platform-owned; ownership is handler-verified

**Context.** If each lane wires its own token plumbing, 16 implementations drift and half of them are
wrong in the dark. If a policy string becomes proof of ownership, every endpoint is silently IDOR-vulnerable.
**Decision.** The lead pre-wired the auth surface once and froze it (`Platform/LivoraAuth.cs`: strict
JWT validation, claim contract `livora.uid`/`livora.sid`/roles, `AccessTokenMint`, all `Policies.All`,
`AuthEnvelopeMiddleware` for 401/403). Handlers read identity only via `LivoraPrincipal` extensions and
ALWAYS compare the row's owner id — `Policies.OwnsResource` is an intent marker, not a proof. An IDOR
test is part of every lane's own test set. With no configured key, the host runs on an *ephemeral*
signing key and must report it as degraded/unconfigured, never as production-ready
(`LivoraAuth.cs:108-110`, CONTRACT-P1 §5b).
**Consequences.** No bespoke auth in any lane; revocation semantics live in `auth_sessions` (P1-C),
not in the token. Dev ergonomics stay perfect (host boots keyless, tests mint with the same key).
**Verified by:** 4 executed auth-contract tests incl. tampered-token rejection and `/platform/me` claims.

## ADR-0004 — Provider truth posture (owner decision, recorded verbatim)

**Context.** The human owner decided: Google OAuth + Google Calendar are the approved real paths, but
**no client_id/secret exists yet**; payments have **no merchant credentials**; the existing AI gateway
(plain HTTP built-in endpoint, off by default) **stays as-is this phase**.
**Decision.** Every such surface ships as REAL code behind config with an honest state:
- Google (identity `POST /auth/google`, calendar converter): implemented per contract; while
  `Identity:Google:ClientId` is empty it answers 503 `provider_unconfigured`. It flips to `ok` in the
  capability report ONLY after a live verification probe succeeds — config presence is never a state.
- Payments: the provider client + webhook signature verifier are coded and exercised by a labelled
  test double; status stays `BLOCKED (no merchant credentials)`; only a provider-confirmed webhook may
  ever produce a `paid` row. The double is MOCK, the signature path is REAL — say both, never blend.
- AI gateway: no change to the Wave 3c posture (`EmbeddedGatewayConfigService.cs:23,101-110` —
  built-in plain-HTTP endpoint, obfuscated built-in key that is explicitly *not* encryption,
  off-by-default, fail-closed); no new live-LLM dependency in CI; deterministic engines remain the
  server's truth source (ADR-0005, ARCHITECTURE-P1 §8).
**Consequences.** Nothing in Wave 4 can lie about being connected; the moment secrets arrive, the flip
is a config change + probe, not a rewrite. **Verified by:** grep at e7579a3 shows empty credentials
(`appsettings.json`) and no payment/google code on the server — recorded as MISSING/UNCONFIGURED in
the audit, not claimed.

## ADR-0005 — The state-claim law

**Context.** Wave 3c's whole honesty apparatus exists because "connected" on a UI is cheap and a lie is
expensive. The mechanism must be a law with a table behind it, not a value statement.
**Decision.** A word from {connected, verified, paid, synced, live} is admissible only with (a) a
probe/executed call and (b) a persisted `connector_states` (or evidence/audit) row carrying it. The
enum-to-vocabulary mapping and the spine diagram are in `docs/contracts/wave4/CAPABILITY-MAP.md`;
`ModuleRegistry` structurally prevents a mapping-failed module from claiming `Ok`
(`ModuleRegistry.cs:112-120`); P1-F tripwires make an unprobed `DependencyState.Ok` and unlabelled
mocks gate-RED (CONTRACT-P1 §7).
**Consequences.** Reports degrade honestly (offline ⇒ `degraded`/`permission_required`, never blank);
every "connected" in the product is joinable to a row and a probe. **Verified by:** capability tests
(`Capabilities_exposes_no_fabricated_feature_keys` green) + the DB probe path (`DatabaseProbe`).

## ADR-0006 — Sync: client owns intent, server owns state

**Context.** A local-first app with an offline queue and a multi-device server cannot double-apply
retries or resolve conflicts by clock. The core table with the unique idempotency index already exists
(`Entities.cs:119-141`, `LivoraDbContext.cs:98-100`); the client queue already refuses fake success
(`NoopSyncTransport.cs:13-25` + `Application/Sync/SyncQueue.cs`).
**Decision.** `POST /api/v1/sync/batch` (CONTRACT-P1 §5c): per-op `operationId` unique per user;
`Idempotency-Key` replay returns the ORIGINAL result; same key + different body ⇒ 409
`idempotency_key_reuse_mismatch`; `baseRevision` mismatch ⇒ `conflict` outcome, never silent overwrite.
`/sync/changes?since=` reads a current-state table (`sync_entity_states`, P1-B contribution —
ARCHITECTURE-P1 §3), so the op log stays insert-only history. Client keeps `SyncQueue` as the journal;
P1-D only bridges it to the real transport, and deleting `NoopSyncTransport`'s registration remains the
only path by which any record may become `Synced`.
**Consequences.** Retry-safe, device-revocable (ops carry `SessionId`), conflict-visible.
**Verified by:** idempotency index test green at e7579a3; protocol itself lands with P1-B/P1-D.

## ADR-0007 — Machine codes over prose; bilingual is a gate

**Context.** The client must render correct localised errors without parsing English; Persian (fa) is
a first-class, first-launch default, RTL-critical. Frozen `ProblemCodes` exist exactly so
(`Application/ApiProblem.cs:15-66`); the client's en/fa suites + resx-integrity tests hold the line.
**Decision.** Server errors are always the envelope with a stable `code` (adding a code = lead
amendment; a lane missing a code files a request — never a literal). The client localises by code
(mapping table in `docs/contracts/wave4/CLIENT-CONTRACT-P1.md`); every new client string ships en+fa
via `wave4-keys/` fragments — editing `AppResources*.resx` directly is frozen-file territory.
**Consequences.** English-only additions are gate-RED by tripwire; codes are a permanent public API.
**Verified by:** 1188 client tests incl. `Wave3ResxIntegrityTests`+honesty suites green at e7579a3.

## ADR-0008 — Request-ledger governance

**Context.** Everything shared is frozen (`integration_owned`/`frozen`/`architecture_owned` in
`.github/OWNERSHIP.yaml`, enforced by `scripts/integration/livora_gates.py` path classification:
lane edit on frozen/arch/integration path ⇒ RED; lead edit ⇒ YELLOW human review).
**Decision.** Lanes never edit frozen files or the composition root; they write
`docs/architecture/wave4/requests/<lane-id>.md` (one file per lane) with
`R-<lane>-<n> | target file | what you need | why | blocking? yes/no` lines. The lead resolves at
merge and records verdicts in the shared ledger's P1-* sections / LEAD VERDICTS. Capability-key and
contract amendments follow the same path (lead PR only, `Authorization.cs:56-57` retirement rule).
**Consequences.** Six lanes cannot corrupt each other or the platform; every requested change is
auditable text. **Verified by:** gate script scope classification read at e7579a3 (`:302-331`).
