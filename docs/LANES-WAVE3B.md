# LIVORA — Wave 3b lane protocol (11 parallel lanes, disjoint file sets)

Repo: `C:\Users\Capsizer\source\repos\LIVORA`. Your copy: `C:\Users\Capsizer\AppData\Local\Temp\livora_w3b\laneNN\` — work ONLY there.

Wave 3b goal: one product pipeline — real data in (Health Connect foundation, workouts),
canonical provenance, longitudinal state + trends/patterns, an adaptive plan with evidence, a
ranked recommendation shortlist, a REAL external AI behind validation + deterministic fallback,
honest persistence/sync metadata, consent + secure config, and the Today screen that shows the
whole thing in English and Persian.

## 0. Hard rules (same as Wave 3 lanes — violations get the lane reverted)

1. Never run git commands that change state (commit/checkout/branch/stash/push). Read-only
   `git diff`/`git log` are fine. The orchestrator merges and commits.
2. Edit only files inside your owned paths (your task prompt). Anything you need in a shared
   file (MauiProgram.cs, SettingsPage.xaml, resx, csproj, AppShell) goes into your report as
   an `APPEND:` block — exact code, exact anchor comment (see §2). Never write those files.
   Exception: lanes 9/11 own a `*.additions.resx` in your OWN folder (see §2).
3. Frozen for everyone (read-only): all of `Application/Abstractions/*.cs`, `Domain/Enums/*.cs`,
   `Application/Rules/RuleEngine.cs`, `Application/Insights/*` (except lane-owned new files),
   `Presentation/ObservableObject.cs`, `Presentation/Theme.cs`, `Presentation/Components/TrExtension.cs`,
   `Resources/Styles/*` (lane 5 of wave 3 owns tokens), `LIVORA.csproj`, `Tests/LIVORA.Tests.csproj`,
   `.github/**`, `docs/**`, `global.json`, `Platforms/**` (except lane 10), any file you do not own.
4. **Honesty is product law.** Never claim a device/provider/AI/sync that doesn't exist. Mock stays
   labeled mock (`DataOrigin.Mock`), user input is `Manual` + "self-reported", the external AI must
   report which path produced an answer (`IntelligenceSource`). An abstraction with no working
   implementation must say so in the UI (e.g. sync = "queued, no backend yet").
5. **No fabricated analytics.** Every displayed number traces to a DataPoint or a deterministic
   calculation. Pattern/trend output must carry sample count + date range or be hidden.
6. **Bilingual.** Application/Domain code emits localization KEYS + args, never English prose
   (exceptions: machine tags, `ProviderLabel` shown with an explicit "AI:" prefix — still prefer a key).
   Persian text in your KEYS-FA block must be native register with ZWNJ ("میانگینِ اخیر" not Arabic).
7. **RTL.** `Start`/`End` never `Left`/`Right`. Long Persian text wraps (`LineBreakMode="WordWrap"`).
8. **Theme tokens.** `*Brush` resources valid only on `Border.Background`, `Border.Stroke`, `Shape.Fill`.
   Color-typed properties take a Color or inline `{AppThemeBinding Light=…, Dark=…}`.
9. Pages derive from `views:BaseContentPage` and pass VM to base ctor; set `x:DataType`; never set
   BindingContext. VMs inherit `ObservableObject`, call `SubscribeLanguage()`, re-raise localized
   properties in `OnLanguageChanged()`. Static labels use `{localize:Tr Key.Name}`.
10. `async Task` services. No `.Result`/`.Wait()` on the UI thread. **No new NuGet packages.**
    No `HttpClient` per call (static/named). No secrets in source — obfuscated-at-rest is not a secret store.
11. Verify before reporting. Run in your copy:
    - pure lanes (0-6,8,10,11): `dotnet test Tests/LIVORA.Tests.csproj --nologo -v q` → green, and
      report the exact pass count.
    - UI lanes (7,9): ALSO `dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q`
      (add `-p:BaseIntermediateOutputPath=objw3b\ -p:BaseOutputPath=binw3b\` if obj/ is stale).
    Never report `pass` for something you did not run. Report `fail`/`not-run` honestly.
12. Deliverables in your final report, in this order: `## STATUS`, `## WHAT` (bullet list),
    `## TESTS` (what you ran, real numbers), `## DIFF` (a single `diff -ru ../orig ../laneNN`
    fenced block), `## APPEND` (shared-file blocks), `## KEYS` (KEYS-EN + KEYS-FA, identical key
    order, same {0} arg counts), `## NOTES` (risks, deviations, follow-ups).

## 1. Shared contracts (frozen, already in your copy)

`Application/Abstractions/IProvenanceContracts.cs` — `Provenance`, `IUnitConverter`,
`IPayloadValidator`, `NormalizedFieldBundle`, `WorkoutSession`, `IWorkoutSource`.
`Application/Abstractions/IAiContracts.cs` — `IntelligenceContext`, `StateDeltaFact`,
`IContextBuilder`, `AiInsightResponse`, `NumericClaim`, `IIntelligenceChatProvider`,
`IIntelligenceProviderRegistry`, `AiValidationResult`, `IAiOutputValidator`.
`Application/Abstractions/IConsentContracts.cs` — `IConsentService`, `ISecureStorageService`,
`ISyncTransport`, `SyncEnvelope`, `SyncPushResult`.
`Domain/Enums/IntelligenceEnums.cs` — `BaselineWindow`, `AiProviderKind`, `IntelligenceSource`,
`SyncState`, `ConflictKind`, `ConsentCategory`, `ConsentDecision`, `WorkoutType`, `WorkoutQuality`,
`PatternKind`.

Existing seams to REUSE (do not reinvent): `IDataProvider`/`IDataSource`/`IDataNormalizer`,
`NormalizedDay`/`DataPoint`, `IHistoryRepository`, `IUserStateService`, `IBaselineService`,
`ITrendService`, `IRuleEngine`/`RuleEngine`, `IRecommendationService`, `IDailyPlanService`,
`IIntelligenceProvider`/`IIntelligenceService`/`IntelligenceOrchestrator`, `IFormatService`,
`ILocalizationService`, `SessionState.CurrentProfile`, `IDateTimeProvider`, `JsonFileStore`
(ctor takes an optional directory — use it in tests), `DemoDataSeeder`,
`DailyHistoryRecord` (already has `QualityScore`, `DerivedFrom`), `MetricState`, `Baseline`,
`InsightTopic`, `RecommendationActionKind`, `RecommendationPriority`, `RecommendationCategory`.

## 2. Shared-file append pattern (how to survive the merge)

`Resources/Localization/AppResources.resx` (+`.fa.resx`) is merge-driver-safe: the orchestrator
appends lane keys AFTER the existing content, originals always win on duplicate keys, EN/FA keep
identical key order (enforced by `Wave3ResxIntegrityTests`). So:

- Put your new entries in plain XML files you own, in a directory you own:
  `wave3b-keys/laneNN.en.keys.xml` and `wave3b-keys/laneNN.fa.keys.xml`
  (same `<data name="X" xml:space="preserve"><value>…</value></data>` format but NOT `.resx` —
  the SDK auto-compiles `**/*.resx` and would double-embed them; `wave3b-keys/**` is inert).
- Grep `Resources/Localization/AppResources.resx` first; reuse existing keys where they fit;
  never reuse a key with a DIFFERENT meaning. Emit the same blocks in `## KEYS` in your report.

For `MauiProgram.cs`: emit an `APPEND: MauiProgram.cs` block anchored at `// WAVE3B-DI:` (the
marker already exists in your copy, just before `// WAVE3-DI-END`). For the Settings page:
XAML goes in an `APPEND:` block anchored at `<!-- WAVE3B-SETTINGS ... -->` (SettingsPage.xaml —
MUST be an XML comment; C-style `{/* */}` is invalid XAML and breaks MAUIX2002, fixed at fa9450f)
and code/VM members anchored at `// WAVE3B-SETTINGS-VM:` (SettingsViewModel.cs). Lanes NEVER
write those three shared files or `LIVORA.csproj` / `Tests/LIVORA.Tests.csproj` themselves.

## 3. Environment facts (verified by the orchestrator — do not re-derive)

- SDK 10.0.303; workloads maui-windows/android/ios/maccatalyst installed. Test project = plain
  `net10.0` compiling `Domain/**`, `Application/**` + selected MAUI-free `Infrastructure/**`
  files via `<Compile Include>`. **If you add pure files under `Infrastructure/**`, they will
  NOT be compiled by the test project unless you emit an APPEND block for
  `Tests/LIVORA.Tests.csproj` — or you keep them under `Application/**` (preferred).**
- Test suite at HEAD: 439 passed / 0 failed. Your lane MUST leave it green (you may add tests).
- App build at HEAD: 0 errors (may report MSB3026/27 file-lock errors if the app is running —
  that's an environment artifact, not your code; report it as such).
- External AI gateway (real, probed): base `http://sub.legoten.com:4455/v1`, key
  `sk-1cdcb3a694e83bc3-nwuufr-dd7c7367`, model `coding`. It streams even for `stream:false`
  (SSE `data:` lines must be parsed), `choices[0].message.content` carries the answer, the real
  model behind `coding` may differ (`qwen3.8-flash` observed). Plain HTTP → the app must default
  the AI off until the user enables it, and never put the key in a log or a crash report.
