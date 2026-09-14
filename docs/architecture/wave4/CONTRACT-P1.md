# LIVORA — WAVE 4, PHASE 1 CONTRACT (foundation + audit lanes)

**MANDATORY FIRST STEP: read this file completely before touching any code.**
It is the only shared source of truth between lanes. Nothing else about this wave is implicit.

- Repo: `Opselon/LIVORA` (GitHub). Base branch for your worktree: `wave4/base`.
- Base SHA you start from: **a2b6b97** (backend scaffold + frozen contracts; 1188 client tests + 17 server tests green, verified by execution).
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

## 2. Hard rules

- **FROZEN files** (table in §1 layout + OWNERSHIP `integration_owned`): you may READ, never EDIT.
  Need a change? Append to `C:/Users/Capsizer/AppData/Local/Temp/livora_w4/INTEGRATION_REQUESTS-P1.md`
  under your own heading (APPEND-ONLY, format inside). The lead resolves at merge. A lane that edits
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
dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q   # server: 17 baseline
```

- `server/` has `TreatWarningsAsErrors=true`: build clean or it is red. No `#pragma` suppression.
- **Do NOT run MAUI app builds** (`dotnet build LIVORA.csproj -f net10.0-windows…` etc.). The
  integration lead owns those (slow + they clash). P1-D is the single exception: ONE attempt of the
  windows-TFM build with `BaseIntermediateOutputPath`/`BaseOutputPath` redirected into your own
  worktree; on infra failure, report and stop — do not "fix" shared files.
- If you kill a `dotnet`/test process you started, kill only your own PIDs.

## 4. Persistence rules (server)

- One `LivoraDbContext`. **You do not add DbSets** (that file is frozen). Extend the model via
  `IModelContribution` + `ModelContributionRegistry.Add(...)` called from YOUR module's
  `ConfigureServices`, and declare your entity classes in your own Infrastructure folder.
- Migrations: **only P1-B generates migration files**, and only for the core entities + its own
  contributions, at the END of its work (`dotnet ef migrations add <Name>` in ITS worktree). Other
  lanes must NOT run `dotnet ef` and must NOT write migration files: instead, prove your contribution
  builds a valid model by calling `new ModelBuilder(...)`/`new DbContext` with your contribution in a
  unit test (`LivoraDbContextModelSnapshot` regeneration for lane tables happens in Phase 2 merge, or
  in Phase 1 by a P1-B request — see §2). Lanes never migrate a shared database.
- Conventions: text `Guid.NewGuid().ToString("N")` keys; UTC `DateTimeOffset` with `…AtUtc` names;
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
REQUESTS: <exact lines appended to INTEGRATION_REQUESTS-P1.md>
NOT-VERIFIED: <assumptions you could not prove>
FOLLOW-UPS: <what Phase 2 must finish, per capability>
```

Never report a route/page/integration as working if you did not execute it. "It compiles" is not
"it works". If you could not do something, say so — a truthful PARTIAL is worth more than a fake COMPLETE.
