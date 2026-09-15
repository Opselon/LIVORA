# LIVORA — WAVE 4, PHASE 1 CONTRACT (foundation + audit lanes)

**MANDATORY FIRST STEP: read this file completely before touching any code.**
It is the only shared source of truth between lanes. Nothing else about this wave is implicit.

- Repo: `Opselon/LIVORA` (GitHub). Base branch for your worktree: `wave4/base`.
- Base SHA you start from: **e7579a3** (backend scaffold + frozen contracts + pre-wired auth;
  1188 client tests + 21 server tests green, re-verified by execution at this SHA — see
  `docs/audit/wave4/AUDIT-P1-BASELINE.md` §C).
- Your worktree path and branch are given in your brief. Never work in another worktree.
- Phase 2 (10 build lanes) will implement the 13 Wave-4 capabilities on top of what YOU land.
  Your job: the audit truth, the platform mechanics, the seams, and the quality gates — not the features.

---

## 0. Product law (from the Wave 4 brief — non-negotiable, applies to every line you write)

1. **Truth over appearance.** `REAL` means *executed and verified by you, in this lane*. Unconfigured =
   `unconfigured`, never "connected". No fake values, syncs, verifications, payments, review counts,
   or AI answers. No fabricated analytics; every number is real, deterministic-mock (labelled), or computed.
2. **AI is not the source of truth.** Deterministic code owns entitlements, verification state, payment
   state, auth, permissions, data validity, scheduling constraints. AI explains/summarises/proposes and
   its output is validated before application.
3. **Safe degradation.** Provider failure, offline, denied permission, AI timeout, unconfigured payment
   ⇒ the product still works and states what is missing. Nothing may hard-crash on a missing dependency.
4. **Bilingual by default.** Any client string you add ships in BOTH `en` and `fa`, RTL-safe.
5. **Privacy.** No raw health values, tokens, or payment data in logs, audit metadata, AI context, or
   error bodies. Correlation ids yes; content no.
6. **No overengineering.** One DB, one host, no microservices/event-bus/speculative abstraction.
   "Ceremonial interfaces" and empty repositories are explicitly forbidden.
7. **Inspect before you audit.** Rule A: every claim you classify must cite `file:line` or a command you ran.

---

## 1. Layout

```
LIVORA/                              MAUI client (existing; 1188 tests must stay green)
  Application/ Infrastructure/ …     client layers (MAUI-free code is unit-testable as-is)
  server/                            Wave 4 backend (P0 scaffold EXISTS and is green)
    src/Livora.Server/                    host + Modules/<Feature>/  <- feature lanes write HERE
    src/Livora.Server.Application/        pure contracts (FROZEN)
    src/Livora.Server.Infrastructure/     DbContext, persistence, adapters
    tests/Livora.Server.Tests/            real-host + pure tests (fixtures FROZEN)
  docs/{architecture,contracts,decisions,audit,quality}/wave4/
```

Read these scaffold files first (they define your world):
- `server/src/Livora.Server/Modules/IFlivoraModule.cs` — the module seam + `DependencyState`
- `server/src/Livora.Server/Modules/ModuleRegistry.cs` — two-phase lifecycle, failure containment
- `server/src/Livora.Server/Program.cs` — composition root (FROZEN: never edit)
- `server/src/Livora.Server/Platform/Problems.cs` + `Application/ApiProblem.cs` — error envelope + codes
- `server/src/Livora.Server.Application/Authorization.cs` — `Policies`, `Roles`, `CapabilityKeys`
- `server/src/Livora.Server.Application/Paging.cs` — `PageRequest`/`PagedResult<T>`
- `server/src/Livora.Server.Infrastructure/Persistence/*` — DbContext, entities, provider switch, interceptor, `IModelContribution`
- `server/tests/Livora.Server.Tests/Fixtures/LivoraWebFixture.cs` — the only sanctioned API test host
- `.github/OWNERSHIP.yaml` — your lane row = your legal write scope (`w4-p1*-*` entries)

Binding Wave-4 docs written at this baseline (read them before designing anything):
`docs/audit/wave4/AUDIT-P1-BASELINE.md` (truth + state-claim law),
`docs/architecture/wave4/ARCHITECTURE-P1.md` (module/table ownership, sync+identity protocol),
`docs/contracts/wave4/CAPABILITY-MAP.md` (13 capability keys), `docs/contracts/wave4/SERVER-CONTRACT-P1.md`,
`docs/contracts/wave4/CLIENT-CONTRACT-P1.md`, `docs/decisions/wave4/ADR-INDEX.md` (0001-0008),
`docs/integration/wave4/INTEGRATION-MATRIX-P1.md` (merge order). Re-deciding any of them = a request.

## 2. Hard rules

- **FROZEN files** (table in §1 layout + OWNERSHIP `integration_owned`): you may READ, never EDIT.
  Need a change? Write YOUR OWN file `docs/architecture/wave4/requests/<lane-id>.md` (one file per
  lane, so six lanes never fight over one ledger). Format per request:
  `R-<lane>-<n> | target file | what you need | why | blocking? yes/no`. Lead resolves at merge. A lane that edits
  a frozen file is reverted in full.
- **Module keys** are fixed per lane (OWNERSHIP rows): `sync`, `identity`, `intelligence`, `verification`.
  A duplicate key throws at startup on purpose.
- **Ownership**: only your `owns:` patterns. Cross-lane calls happen via frozen contracts, not via a
  neighbour's in-flight class. If a contract you need doesn't exist, create it *inside your own folder*
  and file a request to promote it.
- Never `git push`; never rebase/merge master or another lane; the lead does all integration.

## 3. Gates (run all three; paste verbatim tails in your report)

```bash
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q                      # client: 1188 baseline
dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo # backend: 0W 0E (warnings=errors)
dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q   # server: 21 baseline
```

- `server/` has `TreatWarningsAsErrors=true`: build clean or it is red. No `#pragma` suppression.
- **Do NOT run MAUI app builds** (`dotnet build LIVORA.csproj -f net10.0-windows…` etc.). The
  integration lead owns those (slow + they clash). P1-D is the single exception: ONE attempt of the
  windows-TFM build with `BaseIntermediateOutputPath`/`BaseOutputPath` redirected into your own
  worktree; on infra failure, report and stop — do not "fix" shared files.
- If you kill a `dotnet`/test process you started, kill only your own PIDs.
- **Known flake (P1-F owns it):** `LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests` failed once
  under parallel load at base and passed on rerun. Investigate and tighten or document it; do not
  delete the budget. Every other gate must be deterministic.
- External providers in this wave, decided by the human: **Google OAuth + Google Calendar are the
  approved real paths, but no client_id/secret exists yet** — implement the real verifier/converter
  code behind config and report `unconfigured` until the secret arrives (then flip to real by probe).
  Payments: no provider token exists ⇒ real provider client + signature-verification path coded,
  test double driving the flow, status `BLOCKED (no merchant credentials)`. The repo's existing AI
  gateway stays as Wave 3c shipped it; do not add new live-LLM dependencies to CI.

## 4. Persistence rules (server)

- One `LivoraDbContext`. **You do not add DbSets** (that file is frozen). Extend the model via
  `IModelContribution` + `ModelContributionRegistry.Add(...)` called from YOUR module's
  `ConfigureServices`, and declare your entity classes in your own Infrastructure folder.
- Migrations: **no lane runs `dotnet ef` or writes migration files.** The lead generates ONE
  `Wave4P1Schema` migration at integration time from the merged contributions. In Phase 1 you
  prove your slice works by building the model with your contribution and calling
  `EnsureCreated()` on an isolated temp SQLite file (unit test), then file a request line
  `migration-ready | <entity types>` so the lead can verify your types appear in the model.
  Lanes never migrate a shared database.- Conventions: text `Guid.NewGuid().ToString("N")` keys; UTC `DateTimeOffset` with `…AtUtc` names;
  explicit `HasMaxLength`; unique indexes where idempotency matters; no secrets/PII in audit metadata
  columns; soft delete (`DeletedAtUtc`) for evidence/audit rows, hard delete only via the documented
  account-deletion path.
- SQLite = local/CI default (FKs + WAL come from `SqliteConnectionInterceptor`); Npgsql = deploy.
  Never branch behaviour on provider above the persistence layer.

## 5. HTTP API rules (server)

- Versioned prefix only: `ctx.MapVersionedGroup("<segment>")` → `/api/v1/<segment>/…`.
- Errors: `Problems.Of(ctx, ProblemCodes.X, "short human detail")`. Codes come from
  `Application/ApiProblem.cs` constants; a missing constant = a request in §2 file, not a literal.
- Every list endpoint: `PageRequest` in, `PagedResult<T>` out. No bare arrays.
- Auth (until P1-C lands, code against the seam, not a guess):
  - `Policies.SignedIn` etc. via `RequireAuthorization(Policies.X)`
  - current user id: `ctx.User.FindFirst("livora.uid")?.Value` — P1-C guarantees this claim on access
    tokens, plus roles in `ClaimTypes.Role`. Handlers MUST still compare row ownership (IDOR gate, §16
    of the Wave 4 brief); a policy never proves ownership of a specific row.
- DTOs: records in your own `Modules/<Feature>/` folder (Phase 1) — snake_case JSON names are NOT
  used; the shared serializer option is camelCase (`Problems.Json`, `JsonSerializerDefaults.Web`).
- No synchronous `.Result`/`.Wait()` anywhere; `async Task` handlers with `CancellationToken`.

## 5b. Auth is ALREADY wired (read before writing any protected route)

The lead pre-wired `server/src/Livora.Server/Platform/LivoraAuth.cs` (frozen):

- `AddLivoraAuthentication` runs before module service registration; JWT bearer + all
  `Policies.All` are registered; `UseAuthentication`/`UseAuthorization` are in the pipeline.
- Signing key: config `Identity:TokenSigningKey` (base64 ≥ 32 bytes). **Absent ⇒ an ephemeral
  per-process key** and `LivoraSigningKey.Ephemeral == true`. Never commit a key. P1-C must surface
  `Ephemeral` in its capability report as `degraded`/`unconfigured`, never as production-ready.
- Access-token claims: `livora.uid` (account id), `livora.sid` (session id), `ClaimTypes.Role`.
  Read them ONLY via `ctx.User.UserId()`, `.SessionId()`, `.RolesOf()`, `.IsStaff()`.
- Mint a token: `AccessTokenMint.Create(key, userId, sessionId, roles)` (inject `LivoraSigningKey`).
- A protected route answers anonymous/forbidden with the shared envelope automatically
  (`AuthEnvelopeMiddleware` → code `unauthenticated` / `forbidden`) — no lane writes its own 401 body.
- `/api/v1/platform/me` is a live protected endpoint proving this contract (4 tests, all real).
- `Policies.OwnsResource` proves a token, NOT ownership: every handler must still compare the row's
  owner id against `ctx.User.UserId()`. An IDOR test is part of every lane's own test set.

Fixture helpers (use them, don't rebuild): `Fixture.MintAccessToken(userId, roles)`,
`Fixture.CreateAuthenticatedClient(userId, roles)`, `Fixture.ReadProblemAsync(res)`,
`AssertHasCorrelation(res)`.

## 5c. Frozen P1 API contract (the rendezvous point for P1-B/C/D)

These exact shapes are the contract. **P1-B and P1-C must implement them verbatim; P1-D codes
against them and must not invent extra required fields.** JSON is camelCase. All authenticated
routes require `Policies.SignedIn` and a `livora.uid` claim. Errors always use the shared envelope.

```
POST /api/v1/auth/register        {email, password, displayName?, locale?}
     201 -> {userId, accessToken, refreshToken, expiresAtUtc, sessionId}
     409 code=email_already_registered | 400 code=validation_failed (Errors dict per field)
POST /api/v1/auth/login           {email, password, deviceLabel?, platform?}
     200 -> {userId, accessToken, refreshToken, expiresAtUtc, sessionId}
     401 code=invalid_credentials (identical body whether the email or the password was wrong)
     429 code=rate_limited | 403 code=account_locked
POST /api/v1/auth/google          {idToken, deviceLabel?, platform?}
     200 -> same as login | 503 code=provider_unconfigured when Identity:Google:ClientId is empty
POST /api/v1/auth/refresh         {refreshToken}
     200 -> {userId, accessToken, refreshToken, expiresAtUtc, sessionId}   (token ROTATED)
     401 code=token_revoked | 401 code=token_expired
POST /api/v1/auth/logout          {}                       -> 204 (revokes THIS session only)
GET  /api/v1/auth/sessions        -> [{sessionId, deviceLabel, platform, createdAtUtc,
                                       lastUsedAtUtc, expiresAtUtc, isCurrent}]
DELETE /api/v1/auth/sessions/{id} -> 204 (403 forbidden if the session belongs to another account)
GET  /api/v1/account              -> {userId, email, displayName, locale, tier, status,
                                      createdAtUtc, lastLoginAtUtc}
POST /api/v1/account/delete-requests {}   -> {deletionRequestedAtUtc, scheduledForUtc,
                                             reversibleUntilUtc}   (or 409 code=deletion_pending)
DELETE /api/v1/account/delete-requests {}  -> 204 (cancels a pending request)
GET  /api/v1/account/export       -> {exportedAtUtc, profile, goals, habits, plans, history,
                                      connectedDataMetadata, purchases, communityContent}
                                      (no secrets, no tokens, no provider credentials)
POST /api/v1/sync/batch           header Idempotency-Key: <client uuid>; body
     {operations:[{operationId, entityType, entityId, kind, baseRevision, payload}]}
     200 -> {results:[{operationId, outcome:applied|conflict|rejected|duplicate,
              resultRevision, conflict?}], serverTimeUtc}
GET  /api/v1/sync/changes?since=<rev>&limit=  -> {changes:[...], latestRevision, hasMore}
```

Notes every lane must honour:
- `refreshToken` is returned ONCE, in plaintext, and stored only as a SHA-256 hash server-side.
- A reused/rotated-out refresh token revokes the whole session family and is logged as a theft signal.
- `/account/export` may return empty arrays for domains that do not exist yet in Phase 1 (goals,
  habits, plans, purchases, community). **Empty is honest; a fabricated fixture is not.** Mark each
  section's provenance with `"source": "server" | "not_implemented"` per top-level key.
- `Idempotency-Key` on `POST /sync/batch` must match the per-operation `operationId` set; a replay of
  the same key returns the ORIGINAL result body, and a different body under the same key answers
  409 `idempotency_key_reuse_mismatch`.

## 6. Client rules (P1-D only)

- MAUI-free code under `Application/Cloud/**` + `Infrastructure/Cloud/**` so it compiles into the
  existing test project (`Tests/LIVORA.Tests.csproj` includes `Application/**` by wildcard — new
  subfolders are picked up automatically; `Infrastructure` files must be added to the test csproj by
  REQUEST, since that file is frozen).
- Never edit `MauiProgram.cs`, `AppShell.*`, resx files, `LIVORA.csproj` (frozen). Registration is
  delivered as a `// WAVE4-DI:` APPEND block in your report; localization keys as
  `wave4-keys/lane-p1d.{en,fa}.keys.xml`.
- All UI-facing strings via `ILocalizationService` keys; `Tr` markup extension for XAML; Persian
  includes RTL direction and Jalali/Persian-digit formatting via existing `IFormatService`.
- No raw `HttpClient` in ViewModels/Pages — everything through your typed API port.

## 7. Honesty tripwires (P1-F writes them; everyone obeys)

Any of these in a lane's diff = gate RED: hardcoded secret patterns (existing scanner), a
`DependencyState.Ok` report that performs no probe, "connected"/"verified"/"paid" strings in code
without a state machine behind them, a mock not labelled as mock, English-only new UI strings.

## 8. Git protocol (each lane)

1. `git status` clean before you start; re-check before every commit.
2. Stage explicit paths: `git add <path> …` then `git diff --cached --name-only` MUST list only your
   owned paths — if it lists anything else, unstage it (`git restore --staged <path>`).
3. Conventional commits, small and frequent: `feat(wave4/p1c): …`, `test(wave4/p1f): …`, `docs(...)`.
4. Commit often (the lead harvests lane tips at merge; uncommitted work is treated as not existing).
5. NEVER `git push`, never `git worktree add`, never touch `../LIVORA` or another lane's folder.

## 9. Report format (your final message — the lead verifies against this, not against vibes)

```
LANE: <p1a..p1f>
STATUS: complete | partial | blocked
HEAD: <git rev-parse HEAD>
FILES: <path — one-line purpose>  (every file you created/changed)
GATES: <three §4 commands, verbatim last lines; failing gate stated plainly, no hiding>
SURFACE: <endpoints/keys/classes you registered that actually exist>
AUDIT: <(audit lanes) claims -> evidence file:line -> verdict REAL/PARTIAL/MOCK/BROKEN/MISSING>
REAL vs MOCK: <what is real, what is a labelled double, what is BLOCKED and on what external dependency>
REQUESTS: <exact lines written into docs/architecture/wave4/requests/<lane-id>.md>
NOT-VERIFIED: <assumptions you could not prove>
FOLLOW-UPS: <what Phase 2 must finish, per capability>
```

Never report a route/page/integration as working if you did not execute it. "It compiles" is not
"it works". If you could not do something, say so — a truthful PARTIAL is worth more than a fake COMPLETE.
