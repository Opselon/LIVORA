# LIVORA Wave 4 Phase 1 — Integration Matrix (lead execution order at merge)

**Author:** Agent 01 @ `e7579a3`. Seeds the merge plan for the six Phase-1 lanes; the lead owns the
integration branch (`wave4/integration`) and applies this order. Cross-references: architecture §3,
server contract §3 merge-time checks, capability map lane index, CONTRACT-P1 §8 git protocol.

## 0. Baselines

- Client gates: 1188 tests must stay green for EVERY lane merge (client code is only P1-D's, additive).
- Server gates: 21 tests + 0W0E build must stay green; lane merges grow the count (frozen tests untouched).
- All lanes fork `e7579a3`. Merge-base drift is YELLOW by gate design (`livora_gates.py` §3) — expected, not alarm.

## 1. Merge order (dependency-honest; each step's gate run is the proof the previous step was real)

| Step | Lane branch | Reason | Post-merge gate adds |
|---|---|---|---|
| 1 | `wave4/p1a/architecture` (this) | docs only — the law the rest are checked against | none (docs-only diff) |
| 2 | `wave4/p1b/platform` | persistence mechanics + `sync` module: everyone else's tables and the observability surface land first | client 1188; server ≥ 21 + P1-B's set |
| 3 | `wave4/p1c/identity` | needs step-2's persistence conventions; P1-D needs its endpoints | + auth/session/deletion/export tests |
| 4 | `wave4/p1e/engines` | intelligence+verification: pure engines + evidence model on the settled schema | + golden-engine + IDOR tests |
| 5 | `wave4/p1d/client-seam` | codes against §5c as REALized in steps 2-3 (reduces mock-vs-real ambiguity in its tests) | + client seam suite (Wave4ClientSeam) |
| 6 | `wave4/p1f/quality` | tripwires+release harness land LAST so they scan the merged tree, not a wish-tree | + honesty-gate suite |
| 7 | lead | `dotnet ef migrations add Wave4P1Schema` from merged contributions (ADR-0002), SQLite+PG apply check, full 3-gate run, P2 kickoff snapshot tag | all |

Rationale for non-obvious choices: P1-F after everyone (a gate before its subjects is decorative);
P1-D after P1-B/C so its fake-handler tests are anchored to endpoints that actually exist (contract
drift becomes visible at lane time, not merge time); P1-E before P1-D because the client's
verification/"connected" screens must not be written against endpoints no server code implements yet.

## 2. Rendezvous points (what breaks silently if two lanes interpret differently)

| Point | Binding text | Owner of the check |
|---|---|---|
| §5c route/DTO shapes | CONTRACT-P1 §5c + SERVER-CONTRACT-P1 §2; amendment = lead PR only | lead diff-checks P1-B/C/D tests against §5c names |
| Module keys | OWNERSHIP rows; duplicate ⇒ startup throw (`ModuleRegistry.cs:36-41`) | step gate: host start in tests |
| Table names / contribution freeze | ARCHITECTURE-P1 §3 (proposals frozen there) | lead at `Wave4P1Schema` generation |
| Claim vocabulary | CAPABILITY-MAP header + audit §D state-claim law | P1-F tripwires (step 6) |
| `migration-ready` lines | each lane's request file + report §REQUESTS | lead (server contract §3 table) |
| WAVE4-DI APPEND + marker regions | CLIENT-CONTRACT-P1 §6 + DI marker rules (STATUS-MATRIX §DI risk notes) | lead applies; `Wave3DiIntegrityTests` re-runs |
| resx/wave-keys | ADR-0007; lead merges `wave4-keys/*` into AppResources | lead + resx integrity tests |
| Dead config keys (`Ai:*`,`Payments:*`,`Sync:*`,`RefreshTokenLifetimeDays`) | audit §B10 — P1-B wires Sync, P1-C wires Identity; nobody redeclares | lead |

## 3. Conflict pre-judgement (likely file collisions and the pre-agreed winner)

- `server/src/Livora.Server.Infrastructure/Persistence/**`: P1-B owns `Persistence/` additions;
  P1-C/P1-E entity classes live in `Infrastructure/Identity/**` / `Infrastructure/Engines/**` (their
  OWNERSHIP rows) and touch the shared folder ONLY via contributions — a P1-C edit inside
  `Persistence/` is scope drift (gate: YELLOW→review).
- `server/tests/Livora.Server.Tests/Platform/**` is P1-B's row; P1-C/P1-E tests live in
  `Identity/**`, `Engines/**`, `Verification/**`. Nobody edits `Fixtures/**` (frozen).
- Both P1-B and P1-C may want an `AuditEvents` writer helper: it belongs to P1-B (observability row);
  P1-C files R-P1C-n and consumes it. Same for a shared `TimeProvider`: if needed, request it from the
  lead as a platform registration (not lane code) — deterministic clock tests depend on it.
- Client: only P1-D writes `Application/Cloud|Infrastructure/Cloud`. Phase-2 client lanes get their
  own `Wave4ClientSeam`-style test folders later.

## 4. Rollback doctrine

Squash-merge per lane ⇒ revert = `git revert <squash sha>`: clean unless steps 2-6 depend on the
reverted step — order guarantees dependencies point backwards only. `Wave4P1Schema` (step 7) is the
single migration; if a post-7 revert is needed, regenerate it from the surviving tree rather than
authoring a down-migration by hand (core `InitialCore` stays untouched throughout).

## 5. Phase-1 exit criteria (the P2 gate; all four must be true, evidenced, at the step-7 commit)

1. Three gates green at the merged tree (client 1188+…, server ≥ 21+…, 0W0E) — verbatim tails in the
   integration ledger entry.
2. `/api/v1/platform/capabilities` from the merged host lists platform+sync+identity+intelligence+
   verification with truthful states: `ok`(probed)/`unconfigured`/`degraded` only — zero invented keys
   (P1-F assert runs against the live host).
3. One signed-in device can: register→login→refresh-rotate→sync a batch→replay it idempotently→see
   conflict on concurrent edit→request deletion→export with `source` markers — each leg an executed
   test, each §5c-shaped.
4. Audit doc's §B table re-scored at merge SHA with no downgrade vs e7579a3, and the delta register
   updated (this file's step results appended by the lead, not by lanes).
