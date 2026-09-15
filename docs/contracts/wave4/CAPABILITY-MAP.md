# LIVORA Wave 4 — Capability Map (13 capabilities × lanes × truth states)

**Author:** Agent 01 @ `e7579a3`. The one-to-one mirror of `CapabilityKeys.All`
(`server/src/Livora.Server.Application/Authorization.cs:59-79`) — that file is the authority for key
names; this file is the authority for what each key MEANS, which module/lane owns it, what state
vocabulary it may report today, and what Phase 2 may not re-decide.

State vocabulary (frozen): the `connector_states.State` vocabulary at
(`server/src/Livora.Server.Infrastructure/Persistence/Entities.cs:103-104`, `State` default) —
`ok | unconfigured | permission_required | disconnected | degraded | unavailable | partial` —
maps 1:1 onto `DependencyState` (`ok`↔`Ok`, `unconfigured`↔`Unconfigured`, `degraded|disconnected|unavailable`↔`Degraded`,
plus `not_implemented`↔`NotImplemented` which has no connector row and no UI word).
UI words {connected, verified, paid, synced, live} obey the state-claim law
(`docs/audit/wave4/AUDIT-P1-BASELINE.md` §D): probe + persisted row, always.

> **Vocabulary gap (honest note, not a blocker):** `ConnectorState.State` accepts
> `permission_required` and `partial`, which have **no `DependencyState` equivalent**
> (`IFlivoraModule.cs:72-82` has exactly Ok/Degraded/Unconfigured/NotImplemented). Rule for every
> module: report `Degraded` in `ModuleHealth.State` and carry the precise connector word in
> `ModuleHealth.Capabilities["state"]` (the dictionary is per-contract, `IFlivoraModule.cs:89-93`);
> the client screen reads the capability dict for the fine-grained chip. Never widen
> `DependencyState` by guesswork — that is a lead amendment (ADR-0008).

| # | Capability key | Module / owner lane | Reports today (at e7579a3) | Truthful state at Phase-1 exit (target) | Phase-2 build (what may NOT be re-decided) |
|---|---|---|---|---|---|
| 1 | `platform` | `platform` / lead | REAL — capabilities+version+me endpoints, probed DB | REAL | composition root, envelope, correlation, auth stay frozen |
| 2 | `sync` | `sync` / P1-B | MISSING (no module; core `sync_operations` table + indexes REAL) | REAL on SQLite/PG: batch apply, idempotent replay, conflict outcomes, `/sync/changes` | revision model = per-entity current state + insert-only op log (ARCHITECTURE-P1 §5); client offline queue stays `Application/Sync/SyncQueue` bridged by P1-D |
| 3 | `identity` | `identity` / P1-C | PARTIAL — mint/validate/policies/401-envelope REAL+tested; no register/login/refresh endpoints | REAL email+password; Google leg **UNCONFIGURED** until a live id-token verify probe passes | §5c auth routes verbatim; refresh hash+rotation+family revocation; deletion = scheduled reversible; ephemeral signing key surfaces as `degraded` in capability detail |
| 4 | `intelligence` | `intelligence` / P1-E | MISSING server-side; client-side transport REAL but off-by-default, plain HTTP (audit A-17) | REAL deterministic interpretation service; AI leg UNCONFIGURED by owner posture | deterministic code owns truth, AI explains after validation (ARCHITECTURE-P1 §8); no CI live-LLM dependency |
| 5 | `verification` | `verification` / P1-E | MISSING | REAL rule-based evidence verification (`evidence_records`, soft delete) | "verified" word only from a persisted evidence row + code `verification_rule_unknown` when a rule name is bogus |
| 6 | `health` | client: `HealthConnectProvider`; server Phase-2 `health` import surface / P1-D + Phase-2 | PARTIAL — bridge SHELL honest `ApiNotBundled`; mock providers labelled REAL-as-mock | unchanged in P1 (owner: unbundled client pending); connector rows only from probe results | AndroidX Health Connect client addition flips the probe — nothing may light up UI before that (no `Platforms/**` edits this phase: frozen) |
| 7 | `calendar` | Phase-2 `calendar` module (owner decision: Google Calendar = approved real path) | MISSING | UNCONFIGURED posture (no client_id/secret exists); converter/verifier code real behind config | Google pattern (identity §6): code real, state `unconfigured` until probed; never "connected" from config presence |
| 8 | `screen_time` | Phase-2 (platform APIs) | MISSING | MISSING this phase; key reserved — a Phase-2 lane maps it through the same seam | no fake "screen time" from notifications counting; honest `permission_required` first |
| 9 | `nutrition` | Phase-2 | MISSING | MISSING; key reserved | deterministic food-log rules; AI may only annotate validated entries |
| 10 | `personalization` | Phase-2 | MISSING | MISSING; key reserved | server-authoritative tier/entitlement reads (`Authorization.cs:9-12`); client flags never authoritative |
| 11 | `marketplace` | Phase-2 (creator programs) | MISSING | MISSING; key reserved | `Policies.Creator` + approval rows server-side; `creator_not_approved` / `program_not_published` frozen codes |
| 12 | `community` | Phase-2 | MISSING | MISSING; key reserved | moderation via `Policies.Moderator`; report dedupe via `report_already_open`; blocked paths via `blocked_by_participant` |
| 13 | `commerce` | Phase-2 (payments) | MISSING | **BLOCKED (no merchant credentials)** — provider client + webhook signature verifier coded & double-tested, `payment_provider_unconfigured` until a provider-confirmed event | only a verified webhook may write a row that says `paid`; `webhook_signature_invalid`, `purchase_already_exists`, `refund_not_permissible` are the frozen verbs (ARCHITECTURE-P1 §12.3) |

## Lane → deliverable index (Phase 1 exit criteria, per lane)

- **P1-A (this lane):** docs only — audit truth, architecture, contracts, ADRs, integration matrix. No code.
- **P1-B:** `sync` module + `SyncEntityState` contribution + idempotency + observability; gate ≥ its
  own tests incl. replay/conflict matrix; files `migration-ready | …`.
- **P1-C:** `identity` module + §5c endpoints + refresh rotation/theft + deletion + export with
  `source` markers + Google verifier behind `Identity:Google:ClientId` (UNCONFIGURED posture).
- **P1-D:** `Application/Cloud` + `Infrastructure/Cloud` typed port, token store, offline-queue bridge,
  connector-state screen fed ONLY by `/capabilities` + `/auth/sessions`; WAVE4-DI APPEND; wave4-keys.
- **P1-E:** `intelligence` + `verification` modules, deterministic engines with golden tests, evidence model.
- **P1-F:** server CI job proposal (`docs/quality/wave4/`), release-gate harness, honesty tripwires,
  fixtures, perf-budget flake resolution (CONTRACT-P1 §3 known flake).

Retirement rule (`Authorization.cs:56-57`): a key retired here must be deleted from the constants AND
noted in an ADR before Phase 2 — an old client must never read a new meaning into an old key.
