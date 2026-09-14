# LIVORA Wave 4 — Client Cloud Contract (P1-D + every Phase-2 client lane)

**Author:** Agent 01 @ `e7579a3`. Binds the client side of the §5c rendezvous
(`docs/architecture/wave4/CONTRACT-P1.md`). Server authority: `docs/contracts/wave4/SERVER-CONTRACT-P1.md`.

## 1. Seam layout (MAUI-free by construction — it compiles into Tests/LIVORA.Tests.csproj today)

```
Application/Cloud/            contracts + DTO mirrors (interfaces, records, enums only)
  ILivoraApiPort.cs             one typed method per §5c endpoint; no HttpClient here
  CloudDtos.cs                  camelCase mirrors of §5c bodies (records, init-only)
  ICloudTokenStore.cs           access/refresh token custody abstraction
  CloudErrorMap.cs              ProblemCodes -> localization keys (pure dictionary, unit-tested)
Infrastructure/Cloud/
  LivoraHttpClient.cs           ILivoraApiPort impl; HttpClient with injectable HttpMessageHandler
                                (same injectable-handler shape proven by Wave3c OpenAiCompatibleChatProvider.cs:48,183-187)
  SecureCloudTokenStore.cs      ICloudTokenStore over ISecureStorageService (never LocalJsonStore)
Tests/Tests/Wave4ClientSeam/    fake-handler tests: every endpoint, every error code, replay, expiry
wave4-keys/lane-p1d.{en,fa}.keys.xml
```
`Tests/LIVORA.Tests.csproj:50` already includes `Infrastructure\Cloud\*.cs` (lead pre-wire `3710385`);
`Application\**` is wildcarded at `:16`. **P1-D edits no csproj.**

## 2. Auth custody rules

- Access token in memory; refresh token only via `ICloudTokenStore` → `ISecureStorageService`
  (DPAPI/platform keystore path, `Infrastructure/Security/SecureStorageService.cs`). Plaintext token
  never in a log, never in `LocalJsonStore`, never in an audit/AI-context payload.
- 401 with code `token_expired`/`token_revoked` ⇒ single refresh attempt per request (in-flight join),
  replay once; a second 401 ⇒ signed-out state + honest reason key. No silent infinite retry.
- Client stores the server's `expiresAtUtc`; proactive refresh at ≤60s remaining is an optimization,
  never the correctness mechanism (the server's 401 is).
- Sign-out calls `POST /auth/logout` best-effort AND clears local custody regardless of the result
  (offline sign-out must still lock the account-gated UI).

## 3. Error → message contract (client law)

| Problem code (`Application/ApiProblem.cs`) | HTTP | Client behaviour | l10n key family |
|---|---|---|---|
| `validation_failed` (+Errors dict) | 400 | per-field inline errors | `Cloud.Err.Validation.<field>` fallback `Cloud.Err.Validation` |
| `unauthenticated` / `token_expired` / `token_revoked` | 401 | refresh path §2, then signed-out | `Cloud.Err.Auth.*` |
| `invalid_credentials` | 401 | single generic "email or password is wrong" (never echo which) | `Cloud.Err.Login.Generic` |
| `forbidden` / `entitlement_required` / `creator_not_approved` | 403 | feature-gate card, no toast storm | `Cloud.Err.Forbidden.*` |
| `account_locked` | 403 | locked notice | `Cloud.Err.AccountLocked` |
| `rate_limited` | 429 | retry-after from detail/`Retry-After`; queue holds | `Cloud.Err.RateLimited` |
| `provider_unconfigured` / `payment_provider_unconfigured` / `ai_unavailable` / `provider_unavailable` | 503 | honest "not configured/available" card — UI may NOT render connected | `Cloud.Err.Provider.Unconfigured` etc. |
| `permission_required` | 428 | permission-request flow | `Cloud.Err.Permission.*` |
| `version_conflict` | 409 | per-entity conflict UI from `/sync/batch` result body | `Cloud.Sync.Conflict` |
| `idempotency_key_reuse_mismatch` | 409 | BUG-level: log machine tag, keep queue, never claim synced | `Cloud.Sync.IdempotencyBug` |
| `email_already_registered` / `deletion_pending` / `purchase_already_exists` / … | 409 | inline notices | `Cloud.Err.<Code>` |
| `internal_error` | 500 | generic + show correlation id (copyable) | `Cloud.Err.Internal` |

Rules: localise by CODE only; message prose is server-debug, never user-facing. Every key ships en+fa
(ADR-0007). Persian strings carry RTL marks via existing localization suite expectations
(`Tests/Tests/Wave3LocalizationTests.cs`, 1188-test run green at e7579a3).

## 4. Offline / sync bridge (P1-D owns the bridge, P1-B owns its semantics)

- `Application/Sync/SyncQueue` stays the journal; bridge = drain loop → `POST /sync/batch` with
  `Idempotency-Key` = batch uuid and per-op `operationId`s carried unchanged.
- `duplicate`/`applied` outcomes settle queue entries; `conflict` surfaces a per-entity resolution UI
  (no auto-merge of user data); `rejected` keeps the entry with reason and stops its entity's requeue
  until user acts.
- Until `/sync/batch` exists (P1-B merge), the transport is `NoopSyncTransport` — records stay
  `Pending`, UI says "not connected". **Deleting that registration is the only path to a `Synced`
  word** (`NoopSyncTransport.cs:13-30`; ARCHITECTURE-P1 §9).
- Connectivity flaps must not produce retry storms: exponential backoff with jitter, ceiling 5 min,
  single in-flight batch per user.

## 5. Connector-state screen (capability truth, rendered)

Data = `GET /api/v1/platform/capabilities` (`CapabilitySnapshot` — `Modules/Platform/PlatformModule.cs:78-90`)
+ `GET /api/v1/auth/sessions`. Per-capability chip vocabulary (one-to-one, no invention):
`ok`→"Connected" (with `lastSyncAtUtc` freshness from connector row when present) ·
`unconfigured`→"Not set up" + action that explains what a real setup requires ·
`degraded`→"Connection problem" + `ConsecutiveFailures`/stale badge, retry action ·
`permission_required`→"Needs permission" (deep-links the OS permission, Android Health Connect case) ·
`not_implemented`→ hidden from users, visible in advanced diagnostics with machine key.
No chip may render from a client-side guess; the screen re-renders per capability fetch, never caches
`ok`. State-claim law: audit doc §D.

## 6. DI + settings delivery (frozen-file protocol)

- `MauiProgram.cs`: P1-D delivers a `// WAVE4-DI:` APPEND block in its report — registrations go
  inside the existing marker-region discipline (`Wave3DiIntegrityTests` counts markers; never paste a
  copy of an existing marker line, never close the region early).
- Settings page: anchor-comment protocol from wave3b (XML comment, not `{/* */}` — MAUIX2002 pitfall,
  commits `fa9450f`/`74ee8db`).
- New pages/routes: report-only until the lead applies the APPEND; no lane edits `AppShell.*`.

## 7. Test obligations for the lane (minimum set, all in `Tests/Tests/Wave4ClientSeam/`)

1. Port↔§5c shape tests per endpoint (fake handler asserts exact JSON names/values).
2. `CloudErrorMap` completeness: every code in `ProblemCodes` maps to a key that exists in BOTH
   `.keys.xml` files (fail on missing — this is the bilingual gate for the seam).
3. Refresh/rotation happy + reuse-revoked path (client ends signed-out, storage cleared).
4. Replay: same batch key ⇒ queue settles on the ORIGINAL result (no double `Synced`).
5. Offline: `IsConfigured=false` transport ⇒ entries remain `Pending`; UI text key = not-connected.
6. Token custody: assert no token string ever reaches `LocalJsonStore`/logs (capture sink).
