# Wave 3c — Integration Plan

Base: `origin/master` @ `aed6f7763b7042440478d533f8e2d01e8d68b45d` (439/439 tests green locally,
CI green on this SHA: run 34762593846, 2026-09-13T14:24:58Z, all three jobs success).

Integration worktree: `C:/Users/Capsizer/AppData/Local/Temp/livora_w3c/int_wt` (branch `wave3c/integration`).
Lane patches live in `C:/Users/Capsizer/AppData/Local/Temp/livora_w3c/` as `laneNN.patch`.
Lane reports are the `## APPEND` / `## KEYS` blocks each lane emits at report time.
**As of this plan's writing (verified by `diff -rq origNN laneNN`): no lane has produced a patch yet —
all seven lane copies are still pristine.** Patch sizes/paths below are the expected artifacts.

## 1. Lane map

| Lane | Theme | Owned paths (created/edited by the lane) | Patch | APPEND anchors it may emit |
|---|---|---|---|---|
| 01 | security / catalog | `Infrastructure/Security/**` (consent, secure config, local-account), implementation of `ILocalDataCatalogService` + data-portability adapters under `Infrastructure/Persistence/**` or `Infrastructure/Security/**`, `Tests/Wave3c/lane01/**` | `lane01.patch` | `// WAVE3B-DI:` (catalog/consent/security regs), `<!-- WAVE3B-SETTINGS` (privacy/account rows), `// WAVE3B-SETTINGS-VM:`, `wave3c-keys/lane01.{en,fa}.keys.xml` |
| 02 | AI | `Infrastructure/AiProviders/**` (gateway transport, SSE parse, validation), single obfuscated key blob, consent-gated registration wiring, `Tests/Wave3c/lane02/**` | `lane02.patch` | `// WAVE3B-DI:` (IAiTransport + fallback), `<!-- WAVE3B-SETTINGS` (AI status row incl. plain-HTTP warning), `// WAVE3B-SETTINGS-VM:`, `wave3c-keys/lane02.{en,fa}.keys.xml` |
| 03 | normalization | `Application/HealthData/**` (normalizer extensions), `Domain/Models/Health/**` additive, `Tests/Wave3c/lane03/**` | `lane03.patch` | `// WAVE3B-DI:` only if a new seam is added; `wave3c-keys/lane03.{en,fa}.keys.xml` |
| 04 | state / patterns | `Application/State/**`, `Application/Patterns/**` (deviation-from-baseline descriptors, no diagnoses), `Tests/Wave3c/lane04/**` | `lane04.patch` | `// WAVE3B-DI:`, `wave3c-keys/lane04.{en,fa}.keys.xml` |
| 05 | plan / recommendations | `Application/Planning/**` (RecommendationService/ProgramAdapter extensions), `Tests/Wave3c/lane05/**` | `lane05.patch` | `// WAVE3B-DI:` if new services, `wave3c-keys/lane05.{en,fa}.keys.xml` |
| 06 | persistence / perf | `Infrastructure/Persistence/**` (store perf, metadata, file layouts), `Tests/Wave3c/lane06/**` | `lane06.patch` | `// WAVE3B-DI:`, **risk: overlaps lane 01 catalog — see §5**, `wave3c-keys/lane06.{en,fa}.keys.xml` |
| 07 | UI | `Presentation/Views/**`, `Presentation/ViewModels/**`, `Presentation/Components/**`, `Presentation/Responsive/**`, `Tests/Wave3c/lane07/**` | `lane07.patch` | `// WAVE3B-DI:` (VM/page registrations), `// WAVE3-SHELL:` (routes), `<!-- WAVE3B-SETTINGS`, `// WAVE3B-SETTINGS-VM:`, `wave3c-keys/lane07.{en,fa}.keys.xml` |

Frozen for all lanes (already at master, never in a lane patch): `Application/Abstractions/*.cs`
(contracts incl. `IGatewayConfigContracts.cs`, `IAccountsAndDataContracts.cs`),
`Domain/Enums/IntelligenceEnums.cs`, `Tests/LIVORA.Tests.csproj`, `.github/**`, `docs/**`,
`Resources/Styles/*`, `LIVORA.csproj` (no new NuGet packages — a lane patch touching these is a reject,
not a conflict).

Anchor inventory at base (verified by grep, `MauiProgram.cs` / `SettingsPage.xaml` /
`SettingsViewModel.cs` / `AppShell.xaml.cs`):
- `MauiProgram.cs:99  // WAVE3-DI:` … `:187 // WAVE3-DI-END` — the **outer** Wave 3 region.
- `MauiProgram.cs:183 // WAVE3B-DI:` … `:185 // WAVE3B-DI-END` — nested **inside** the outer region;
  this is where wave 3c lane APPEND blocks land (per WAVE3C-BRIEF §12).
- `Presentation/Views/Settings/SettingsPage.xaml:265 <!-- WAVE3B-SETTINGS ... -->`
- `Presentation/ViewModels/Settings/SettingsViewModel.cs:19 // WAVE3B-SETTINGS-VM:` … `:20 -END`
- `AppShell.xaml.cs:23 // WAVE3-SHELL:` … `:26 -END`

## 2. Merge order

```
03 (normalization) -> 04 (state/patterns) -> 05 (plan/rec) -> 02 (AI) -> 01 (security/catalog) -> 06 (persistence/perf) -> 07 (UI)
```

Rationale: pure-domain layers first (each is consumed by the next), AI before security/consent only in
the sense that lane 02's transport lands before lane 01's consent UI needs something to gate;
UI last so it sees final VM/service shapes; persistence/perf second-to-last because it may rewrite
store internals that lane 01's catalog then indexes.

## 3. Per-merge verification protocol (run for EACH lane, in order)

All commands from a **fresh worktree of the current master** (never the live checkout
`C:/Users/Capsizer/source/repos/LIVORA` — it is a foreign merge tree and its `obj/` goes stale-false-green):

```bash
git fetch origin
git -C <repo> worktree add <tmp>/int_<laneNN> -b integrate/wave3c-laneNN origin/master   # re-check origin/master RIGHT BEFORE this; a parallel orchestrator may have moved it
cd <tmp>/int_<laneNN>

# 1. apply
git apply --check ../../laneNN.patch        # dry run — if it fails, resolve per §4, never force
git apply            ../../laneNN.patch

# 2. pure-layer tests
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q        # expect 439+ passed, 0 failed (lane may ADD tests)

# 3. APPEND blocks (lane report ## APPEND)
#    - DI block(s) -> insert immediately after the `// WAVE3B-DI:` marker line (before -END)
#    - settings rows -> at `<!-- WAVE3B-SETTINGS` (XAML) / `// WAVE3B-SETTINGS-VM:` (VM)
#    - shell routes  -> at `// WAVE3-SHELL:`
#    - resx keys: merge wave3c-keys/laneNN.en.keys.xml + laneNN.fa.keys.xml into
#      Resources/Localization/AppResources.resx / .fa.resx — EXISTING-KEY-WINS dedup
#      (a colliding key keeps the master value; log the collision in the commit message),
#      never insert a duplicate <data name>. EN/FA parity is then enforced by
#      Tests/Tests/Wave3ResxIntegrityTests.cs (identical key sets, equal counts, {N} placeholder
#      parity, non-empty values, Persian-script check with allow-list).

# 4. after resx merge, re-run the pure tests (resx integrity reads from DISK):
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q

# 5. head builds
dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 -c Release --nologo -v q    # 0 errors required
dotnet build LIVORA.csproj -f net10.0-android -c Release --nologo -v q                # record the REAL outcome; ~5 min locally, 15 min allowance

# 6. commit + push
git add -A && git commit -m "merge(wave3c): lane NN — <summary> [verified: tests NNN/NNN, win-ok, android-ok]"
git push origin integrate/wave3c-laneNN   # then fast-forward master from the lane branch, or cherry the commit
```

If the Android build fails, the lane is NOT merged green: record the error lines verbatim in the
merge commit and either fix-forward within the lane's owned paths or revert the lane.
Do not build with redirected obj/bin unless needed; in git-bash the flags MUST be quoted:
`"-p:BaseIntermediateOutputPath=objw3c\\" "-p:BaseOutputPath=binw3c\\"` (unquoted trailing
backslashes merge two flags into one invalid dir — WAVE3C-BRIEF §31).

## 4. Conflict protocol

1. `git apply --check` fails → apply with 3-way (`git apply -3`); if still dirty, apply the patch
   file-by-file (`git apply --include=...`) from lane-clean ones to shared-file hunks.
2. Shared files (`MauiProgram.cs`, `SettingsPage.xaml`, `SettingsViewModel.cs`, `AppShell.xaml.cs`,
   both resx, `LIVORA.csproj` never): reconcile **minimally** — keep both sides' registrations when
   they serve different contracts; when two lanes register the SAME contract, the later lane in
   §2 order wins the registration line and the loser's line is deleted **with a note**.
3. Every reconciliation is written into the merge commit message: which file, which hunk, what was
   kept/dropped/moved, and why. **Never silently rewrite lane functionality** — a functional change
   goes back to the lane owner, not into the merge.
4. After any manual reconciliation, the full §3 protocol (tests + both builds) re-runs from the
   reconciled state, not from the pre-apply state.

## 5. Known hot spots (pre-declared)

- **LocalDataCatalog duplication (lanes 01 × 06).** Master already ships
  `Presentation/ViewModels/Profile/LocalDataFiles.cs` (UI-side file inventory) and a frozen
  contract `ILocalDataCatalogService` (`Application/Abstractions/IAccountsAndDataContracts.cs:56`).
  Lane 01 owns the catalog implementation; lane 06 owns persistence internals and may grow a
  near-identical file registry. On collision: lane 01's catalog is the interface-facing one
  (`ILocalDataCatalogService`), lane 06's becomes an internal detail the catalog consumes — record
  the choice in the commit message. `NoServiceInterface_IsRegisteredTwiceInMauiProgram` catches only
  same-interface double DI; type *duplication under two names* will not fail any test — reviewer check.
- **Nested DI markers.** `// WAVE3B-DI:` sits INSIDE `// WAVE3-DI:..// WAVE3-DI-END`
  (`MauiProgram.cs:183` within `99..187`). Wave3DiIntegrityTests counts line-anchored marker hits
  (=1 each) and asserts no `builder.Build()` inside the outer region — see STATUS-MATRIX §Risks.
- **resx volume:** 754 keys EN + 754 FA at base; 7 lanes appending fragments in parallel means
  merge-order key collisions are likely. Existing-key-wins + parity test is the arbiter.
- **SettingsPage.xaml anchor is an XAML comment** — a lane VM block pasted into the XAML anchor (or
  vice versa) is the exact mistake wave3b commit `fix(wave3b): remove literal brace-comment text
  from the SettingsPage` had to repair; check the anchor side before building.
- **Lane 02 and the key:** the gateway key may appear ONLY inside lane 02's single obfuscated blob.
  It must never land in logs, resx, tests, docs, or this repo's history. Rotation tracked in the
  wave3c gate issue.
