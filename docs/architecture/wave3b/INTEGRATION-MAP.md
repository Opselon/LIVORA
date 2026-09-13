# LIVORA — Wave 3b integration map (Agent 01 · Integration Lead)

Date: 2026-09-13. Base: `18cfbc7` (master, pushed). This wave consolidates the 15 master-plan agents
into 11 executable lanes + orchestrator-owned QA; mapping below.

## Agent → lane map

| Master agent | Lane | Scope | Key owned paths |
|---|---|---|---|
| 01 Integration Lead | orchestrator | contracts, merge, docs, final report, QA scenarios | `Application/Abstractions/I{Provenance,Ai,Consent}Contracts.cs`, `Domain/Enums/IntelligenceEnums.cs`, `docs/` |
| 02 Health data | 02 | Health Connect foundation, capability discovery, permission abstraction | `Infrastructure/HealthProviders/**` |
| 03 Activity | 03 | WorkoutSession sources + adapter | `Application/Activities/**` |
| 04 Normalization | 04 | provenance stamping, unit converter, payload validation, quality | `Application/Normalization/**` |
| 05 State/baselines | 05 | 7/14/30 windows, sample-gates, state confidence | `Application/State/Wave3b/**` |
| 06 Patterns + 08 Recs | 06 | behavioral patterns w/ evidence, recommendation ranking/dedup | `Application/Patterns/**`, `Application/Planning/Wave3b/**` |
| 07 Adaptive plan | 07 | IPlanAdaptationEngine, adaptation evidence records | `Application/Planning/Adaptive/**` |
| 09 AI + 10 Safety | 08 | gateway provider, context builder, registry, validator, fallback | `Application/Intelligence/**`, `Infrastructure/IntelligenceProviders/**` |
| 11 Persistence | 09 | entity metadata (versions/sync state), sync queue abstraction | `Application/Sync/**`, `Infrastructure/Persistence/Wave3b/**` |
| 12 Privacy | 10 | consent store, secure config storage, redaction, data controls | `Infrastructure/Security/**` |
| 13 UX + 14 l10n | 13 | Today intelligence cards, AI/rule provenance chip, RTL/a11y | `Presentation/**` (Today + Settings appendices) |
| 15 QA | orchestrator | scenarios A–I as tests post-merge + release gate | `Tests/Tests/Wave3b*` |

## Dependency order (batches)

```
Batch 1 (data in + brain):  02 03 04 05 06 07 08 09 10
Batch 2 (surface):          13   (consumes 08's context/insight fields, 10's consent UI seams, 06's pattern keys)
Batch 3 (orchestrator):     merge + DI composition + QA scenario suite + app/CI verification
```

Parallel-capable because lanes own disjoint file sets (docs/LANES-WAVE3B.md); shared files are
touched only via APPEND blocks onto pre-planted anchors (`WAVE3B-DI:`, `WAVE3B-SETTINGS`,
`// WAVE3B-SETTINGS-VM:`), and resx keys arrive as `wave3b-keys/laneNN.{en,fa}.keys.xml`.

## Architecture invariants the merge must preserve

1. Data flows one way: provider → `NormalizedDay`(+`Provenance`) → history → `PersonalState`
   (windowed baselines) → rules → recommendations (ranked ≤5) → plan (adaptations with evidence)
   → Today. AI sits BESIDE this chain, only re-phrasing/annotating; validator gates every output.
2. `NormalizedDay`/`DataPoint` JSON stores stay append-only-compatible (new fields defaulted;
   no migration needed for existing files) — Rule 21.
3. The AI gateway key is obfuscated-at-rest, never plaintext in source/logs/UI. Honest label:
   obfuscation ≠ security boundary (documented in ADR 3b-04).
4. Sync = abstraction only until a backend exists; queue drains nowhere and UI says so.
5. Every UI string EN+FA; Application/Domain emit keys+args only.

## Risks

- Duplicate abstraction drift: lanes inventing parallel `IProvenance`/`Baseline` types → freeze
  §1 contracts (done) + `Wave3bContractPurity` tripwire test at merge.
- The running app locks `LIVORA.exe` → app builds can show MSB3026/27; treat as env artifact.
- Android-only Health Connect code can't be unit-tested on Windows net10.0 → bridge behind a
  fakeable `IHealthConnectBridge`; pure logic tested, platform shell compile-checked only.
- Test csproj auto-includes `Application/**`: any lane file there must be MAUI/IO-free or the
  suite breaks — caught by lane test runs before merge.
