# LIVORA Wave 4 — Server Module Contract (P1 rendezvous, binding)

**Author:** Agent 01 @ `e7579a3`. This is the cross-module contract the Phase-1 lanes code against so
P1-B/C/D/E land together without talking. Route/DTO shapes are frozen in
`docs/architecture/wave4/CONTRACT-P1.md` §5c; ownership and table space in
`docs/architecture/wave4/ARCHITECTURE-P1.md` §3; capability meaning in
`docs/contracts/wave4/CAPABILITY-MAP.md`. Anything else a lane needs from a neighbour = request
`R-P1x-n`, not an assumption.

## 1. Universal module obligations (every module, or it is gate-RED)

1. Public non-abstract `IFlivoraModule` in `server/src/Livora.Server/Modules/<Feature>/`, key exactly
   the OWNERSHIP row's key; parameterless constructor (the scanner instantiates it —
   `ModuleRegistry.cs:24-27`).
2. Services in `ConfigureServices(ModuleSeed)` ONLY (including `ModelContributionRegistry.Add`);
   routes in `MapEndpoints(FlivoraEndpointContext)` ONLY, via `ctx.MapVersionedGroup("<segment>")`.
3. `Report()` is live per call and probe-backed. `DependencyState.Ok` with no probe executed = tripwire
   (CONTRACT-P1 §7). Report `Detail` carries no secret, no PII, no exception message (`ModuleRegistry.cs:108-109`
   precedent: exception TYPE only).
4. Register `Policies` names only from the frozen constants; compare row ownership in the handler
   (ADR-0003). `async Task` handlers with `CancellationToken`; no `.Result`/`.Wait()`.
5. Errors via `Problems.Of(ctx, ProblemCodes.X, detail)` — a missing constant is a request, not a
   literal (ADR-0007). Lists use `PageRequest`/`PagedResult<T>`.
6. Write an `AuditEvent` for every state-changing action that a user could later dispute (login,
   revoke, connect, delete-request, verification decision, payment-confirmed). Metadata = ids/enums/counts.
7. Own test set includes: happy path, anonymous 401 envelope, wrong-owner 403 (IDOR), and — for any
   provider leg — the `unconfigured` answer when config is absent (ADR-0004).

## 2. Module contracts

### 2.1 `sync` (P1-B)
- **PROVIDES:** `POST /api/v1/sync/batch`, `GET /api/v1/sync/changes` (§5c verbatim), plus
  observability surfaces the lead wires into `/healthz?deep=1` reporting.
- **CONSUMES:** `livora.uid` claim (P1-C semantics already frozen in `LivoraAuth`), core
  `sync_operations`, its own `sync_entity_states` contribution, `Sync:*` config keys
  (`appsettings.json`: `MaxOperationsPerBatch=200`, `MaxPayloadBytes=60000` — nothing reads them yet;
  P1-B owns that wiring, see B10 in the audit).
- **INVARIANTS:** idempotent replay returns original result body; oversized batch ⇒ `sync_too_large`;
  revision mismatch ⇒ outcome `conflict` + `ConflictDetail`, never silent win; op log insert-only.
- **DEFER TO LEAD:** `migration-ready | SyncEntityState`.

### 2.2 `identity` (P1-C)
- **PROVIDES:** `/api/v1/auth/*` + `/api/v1/account/*` (§5c verbatim); the user-facing semantics of
  `livora.uid`/`livora.sid`; registration of any `identity` capability detail the platform surfaces.
- **CONSUMES:** frozen `LivoraSigningKey` + `AccessTokenMint` (must NOT build its own
  `TokenValidationParameters` — `LivoraAuth.cs:36-37`); `Identity:*` config including the two
  currently-dead keys `AccessTokenLifetimeMinutes`/`RefreshTokenLifetimeDays` (P1-C wires them;
  mint already accepts `lifetimeMinutes`).
- **INVARIANTS:** identical 401 body for wrong-email vs wrong-password; refresh rotation + family
  revocation on reuse (`RevokedReason` set, audit theft row); `account_locked` on threshold,
  `rate_limited` before it; export sections carry `source` markers; Google leg 503
  `provider_unconfigured` while `Identity:Google:ClientId` is empty and flips only on a live probe.
- **DEFER TO LEAD:** `migration-ready | <its entity types>`; capability report must surface
  `LivoraSigningKey.Ephemeral == true` as `degraded`, never `ok` (CONTRACT-P1 §5b).

### 2.3 `intelligence` + `verification` (P1-E)
- **PROVIDES:** deterministic engine endpoints under `/api/v1/intelligence/*` and
  `/api/v1/verification/*`; evidence model (`evidence_records`) that is the only lawful source of the
  word "verified"; validated-outputs-only persistence.
- **CONSUMES:** `Policies.SignedIn`/`Entitled`; core `audit_events`; `Ai:*` config for an optional
  server-side gateway leg (owner posture: stays UNCONFIGURED this phase — server code must not add a
  live-LLM CI dependency; if a leg calls a model, output passes the validator or
  `ai_output_rejected`, and the run row records provenance).
- **DEFER TO LEAD:** golden-test list for each engine rule (P1-F wires them into the release gate).

### 2.4 Client cloud seam (P1-D) — server-side counterpart expectations
- P1-D codes against §5c exactly; it must not require a field §5c does not define. If P1-B/C need an
  extra response field, THAT is a §5c amendment request routed through the lead (frozen-contract path).
- `/api/v1/platform/capabilities` is the only source the connector screen may read for provider state;
  `/api/v1/auth/sessions` is the only source for "signed-in devices".

## 3. Merge-time rendezvous (what the lead checks across the four server slices)

| Check | Why it can fail silently |
|---|---|
| Module keys unique across merged tree | duplicate key throws only at first real host start |
| `migration-ready` lines ⊇ every contributed entity type | an absent line = lane built its slice with `EnsureCreated` only |
| `Wave4P1Schema` applies on BOTH SQLite and Postgres | CI only exercises SQLite (ADR-0002 note) |
| No lane added a `Policies`/`ProblemCodes`/`CapabilityKeys` literal | constants are frozen; drift shows at client render time |
| Every provider leg answers `unconfigured` with clean config | otherwise a fake "connected" ships |
| Server test count strictly increases; 21 baseline tests untouched | frozen fixtures (`Fixtures/**`) are integration-owned |
