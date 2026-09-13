# LIVORA

**Personal life & performance OS.** Not a chatbot. LIVORA runs a closed loop:

> **data → state → intelligence → recommendation → plan → adaptation**

It ingests daily health signals (mock, plus **your own logged entries**), derives *your* state against
*your own* baseline (not population averages), fires deterministic rules, adjusts today's plan, and
explains every change — in English or Persian, with full right-to-left support.

- .NET 10, .NET MAUI, single project, multi-target: Android / iOS / Mac Catalyst / Windows
- Root namespace `LIVORA`, app id `com.livora.app`, version **1.1** (build 2)
- Clean architecture: `Domain → Application → Infrastructure → Presentation`, DI composed in `MauiProgram.cs`
- Persian (`fa`) is a first-class, **first-launch default** language — the app is RTL out of the box
- **Product law**: mock stays labeled mock, self-reported stays labeled self-reported, and a failed
  check is reported as a failure — never as "up to date"

Wave 3 (this document's scope) is the wave that tries to make LIVORA genuinely useful to a client:
log your own data, edit real goals and habits, open an adaptive program, see a real chart, get
reminders, check for updates in-app, look right in a desktop window. **Read §9 (Status) first** — it
separates what is shipping today from what only exists as a contract.

---

## 1. Data flow (Wave 3)

```
SampleHealthProvider              (Application/HealthData — deterministic mock; DataPoints carry
   │                              origin/quality/confidence; HR/HRV honestly null; seeded per user+date)
   ▼
[ManualOverlayProvider]           (lane 02 — composes the provider with the user's own logged values;
   │                              manual wins per field, origin flips to Manual. CONTRACT ONLY today:
   │                              IManualEntryService + ManualEntryDraft exist, the implementation is
   │                              still in flight — see §9)
   ▼
DataNormalizer                    (sanity ranges, Missing/Invalid, staleness windows:
   │                              sleep 2d, activity 1d, recovery 2d, wellness 3d)
   ▼
DailyHistoryStore                 (Infrastructure/Persistence — rolling JSON history, 24-day backfill
   │                              on first run, 120-day cap, writes only on real content change)
   ▼
BaselineService ──► UserStateService   (28-day rolling personal baselines, confidence-gated;
   │                TrendService        half-vs-half trends, ≥5 samples only)
   ▼
PersonalState                     (Domain/Models/State — derived, typed snapshot; the ONLY thing
   │                              rules and UI consume; raw provider data stops above it)
   ▼
RuleEngine                        (9 deterministic rule outputs from 7 rules → structured RuleResults)
   ▼
RecommendationService + DailyPlanService   (≤3 visible recs; plan skeleton + rule adaptations
   │                              through the adjustment grammar in §5)
   ▼
IntelligenceOrchestrator ◄──── SampleIntelligenceProvider (IIntelligenceProvider — rule-driven
   │                              phrasing, zero network calls; a real LLM can swap in behind it)
   ▼
Today / Health / Log / Goals / Programs / Profile (Presentation — renders localization keys + args)
```

Side branches:

- **Manual entry (Wave 3, in flight)** — the Log tab writes `ManualEntryDraft`s
  (`IManualEntryService`, `Application/Abstractions/IWave3Contracts.cs`). `ManualMerge.Overlay`
  (lane 02) merges them into a `NormalizedDay` per field: manual value wins, `Origin = Manual`,
  `Quality = Complete`, `Confidence = 1.0`; untouched fields keep the provider's provenance. User
  input is **self-reported**, and the UI must keep saying so.
- **WeeklySummaryService** reads history + trends for the review; returns `null` below 3 closed days
  (refuses to summarize nothing).
- **ProgramAdapter** adapts bootcamp days through the *same* `RuleEngine`, so a recommendation and
  the program can never disagree — and an adaptation with no rule behind it is impossible.
- **Updates (Wave 3, in flight)** — `IUpdateService` + `UpdateInfo` + pure `AppVersion`
  (`Application/Abstractions/IWave3Contracts.cs`); lane 01 supplies `GitHubReleaseFeed` over the
  public `api.github.com` release list, cached in `update_feed.json`.
- **Reminders (Wave 3, in flight)** — `IReminderService` + `ReminderSetting` +
  `NotificationGrantState`; lane 09 supplies `ReminderEngine` (pure) + a `Plugin.LocalNotification`
  adapter that reports exactly what the OS answered.

## 2. Layers

Dependency direction: **Presentation → Application → Domain**; **Infrastructure → Application → Domain**.
`MauiProgram.cs` is the composition root — the only place a concrete implementation is named.

| Layer | Rule |
|---|---|
| `Domain/` | Pure C#. Models + enums + constants. No MAUI, no IO, no services. Knows *what things are*. |
| `Application/` | Pure C#. Contracts (`Abstractions/`) + engines (State, Rules, Planning, Insights, HealthData, Context). No platform APIs. Knows *how decisions are made*. The test project compiles this directly. |
| `Infrastructure/` | Platform adapters: persistence (JSON), localization (resx/culture/fonts), settings (MAUI Preferences), security, intelligence providers, updates/notifications (Wave 3). Implements Application contracts. |
| `Presentation/` | MAUI views, ViewModels by feature, converters, `BaseContentPage`, `Theme.cs`. Knows *how to show*, never *what to decide*. |

### File tree (source only)

```
LIVORA/
├── global.json                     # pins the 10.x SDK (rollForward: latestFeature)
├── LIVORA.csproj                   # multi-target MAUI app; Tests excluded by wildcard
├── LIVORA.slnx                     # app + test project
├── MauiProgram.cs                  # composition root; carries the // WAVE3-DI: marker region
├── App.xaml(.cs)                   # resources; window + FlowDirection bootstrap; // WAVE3-APP:
├── AppShell.xaml(.cs)              # tabs; // WAVE3-SHELL: (lane route registrations land here)
│
├── Domain/                         # pure — no dependencies
│   ├── Constants/AppConstants.cs   # file names + targets (7.5h sleep, 8000 steps, 30 active min)
│   ├── Enums/                      # Domain, HealthData (DataOrigin/Quality/Capabilities),
│   │   │                           # State (BaselineConfidence/TrendDirection/StateLevel),
│   │   ├── PlanningEnums.cs        # priority ladder, action kinds, refresh modes
│   │   ├── SecurityEnums.cs        # AppPermission/PermissionState/GoalMeasurement
│   │   └── Wave3Enums.cs           # UpdateCheckStatus, NotificationGrantState, ThemeMode,
│   │                               # AppThemeKind (domain stand-in for MAUI's AppTheme)
│   └── Models/
│       ├── Health/DataPoint.cs     # DataPoint + NormalizedDay (raw-measurement contract)
│       ├── State/PersonalState.cs  # MetricState, Baseline, domain states, Metrics keys
│       ├── History/DailyHistoryRecord.cs  # + WeeklySummary
│       ├── Planning/DailyPlan.cs   # PlanItem, DailyPlan, RuleResult
│       ├── Goals/Goal.cs           # Goal + Habit (streaks, completion log, Saturday week start)
│       ├── Programs/Bootcamp.cs    # adaptive program + DailyInsight + Recommendation
│       └── UserProfile.cs
│
├── Application/                    # pure — contracts + engines
│   ├── Abstractions/               # IDataProvider, IDataNormalizer, IRepository,
│   │   │                           # IStateContracts, IIntelligenceContracts, IIntelligenceService,
│   │   │                           # ILocalizationService, IFormatService, ISecurityContracts,
│   │   │                           # IDataSource, ISettingsService (+ThemeMode, +LastSeenVersion)
│   │   └── IWave3Contracts.cs      # IUpdateService/UpdateInfo/AppVersion, IManualEntryService/
│   │                               # ManualEntryDraft, IReminderService/ReminderSetting, IThemeService
│   ├── Context/SessionState.cs     # active UserProfile for the session
│   ├── HealthData/                 # SampleHealthProvider, DataNormalizer
│   ├── State/                      # BaselineService, UserStateService, TrendService
│   ├── Rules/RuleEngine.cs         # the deterministic safety layer
│   ├── Planning/                   # RecommendationService **and** DailyPlanService (same file),
│   │   └── ProgramAdapter.cs       # bootcamp-day adaptation via the shared RuleEngine
│   └── Insights/                   # IntelligenceOrchestrator, WeeklySummaryService
│
├── Infrastructure/
│   ├── Persistence/                # JsonFileStore + JsonRepository, DailyHistoryStore,
│   │                               # DemoDataSeeder (first-launch sample content)
│   ├── Localization/               # LocalizationService (+IFormatService), FontFamilies,
│   │                               # LanguageHook, CultureBootstrap  — MAUI-free, and compiled
│   │                               # into the test project since Wave 3 (lane 10)
│   ├── Settings/                   # PreferencesSettingsService (MAUI Preferences)
│   │                               # ⚠ declares namespace LIVORA.Infrastructure.Localization
│   │                               #   — known folder/namespace drift, see docs/WAVE3.md debt
│   ├── Security/                   # PermissionService (never prompts yet), PrivacyService
│   ├── IntelligenceProviders/      # SampleIntelligenceProvider (rule-driven mock "AI")
│   ├── Updates/                    # (lane 01, in flight) GitHubReleaseFeed, UpdateService
│   └── Notifications/              # (lane 09, in flight) LocalReminderService, SnoozeStore
│
├── Presentation/
│   ├── Theme.cs                    # single source of code-side colors (mirrors XAML tokens)
│   ├── ObservableObject.cs         # VM base: localization glue, FlowDirection/font re-raise
│   ├── Components/                 # TrExtension ({localize:Tr Key}), ItemsStackLayout,
│   │                               # converters, TappableFeedback; Wave 3 adds StatChip,
│   │                               # ProgressRing, SegmentedControlView, PressableBorder,
│   │                               # EmptyStateView, SearchBarView, FilterChipsView
│   ├── Responsive/                 # (lane 04, in flight) Breakpoint, AdaptiveLayout, ResponsiveGrid
│   ├── ViewModels/<feature>/       # Today, Health, Goals, Programs, Profile, Review, Onboarding
│   │                               # + Updates, Log, Settings, Reminders, Editor, Bootcamps (W3)
│   └── Views/<feature>/            # pages (all derive BaseContentPage)
│
├── Resources/
│   ├── Localization/               # AppResources.resx (en) + AppResources.fa.resx (fa),
│   │                               # 333 keys each (verified by ResxIntegrityTests), hand-written
│   │                               # accessor — no designer, works from plain dotnet CLI
│   ├── Styles/                     # LivoraColors.xaml, LivoraStyles.xaml (lane 05 owns tokens)
│   ├── Icons/                      # tab_today/health/goals/programs/profile + tab_log (MauiImage)
│   └── Fonts/                      # OpenSans (Latin), Vazirmatn (Persian, SIL OFL 1.1 —
│                                   # license in Resources/Fonts/OFL-Vazirmatn.txt)
├── Platforms/                      # Android / iOS / MacCatalyst / Windows entry points
├── Tests/                          # xunit project (see §7) — plain net10.0, no MAUI reference
└── docs/
    ├── LANES.md                    # the 10-lane parallel build protocol (merge contract)
    ├── WAVE3.md                    # Wave 3 decisions + the honesty rationale
    └── WAVE3-MERGE.md              # merge checklist derived from LANES.md
```

## 3. Waves

**Wave 1** shipped the shell as one project: onboarding, five-tab UI, bilingual EN/FA with RTL, local
preferences, mock data source, bootcamp catalog. No state model, no rules, no tests.

**Wave 2** restructured into four layers and added the intelligence core: the raw-vs-derived split
(`DataPoint` vs `MetricState`), personal baselines, trends, `PersonalState`, the rule engine, planning,
history + weekly look-back, privacy services, and the xunit suite.

**Wave 3** (this wave) adds the client-facing features — manual logging, real goal/habit editors, a
real chart, reminders, in-app update checks, theme mode, desktop breakpoints — behind frozen contracts
(`Application/Abstractions/IWave3Contracts.cs`, `Domain/Enums/Wave3Enums.cs`) that ten lanes implement
in parallel. Wave 3 also doubled the test suite (117 → 438 cases) and made the localization stack
testable without a MAUI head. `docs/WAVE3.md` records the decisions and their reasoning;
`docs/WAVE3-MERGE.md` is the merge checklist.

## 4. Bilingual / RTL system

- **Contract**: `ILocalizationService` (Application) — indexer + `T(key, args)`, `IsRightToLeft`,
  `FormatCulture`, per-language font properties, `LanguageChanged`. Implemented by
  `LocalizationService` (Infrastructure), which also implements `IFormatService`.
- **Strings**: `Resources/Localization/AppResources.resx` (English neutral) + `AppResources.fa.resx`
  (Persian), **333 matching keys** resolved through a hand-written `AppResources` accessor so plain
  `dotnet build` works without VS designers. A missing key renders as `[Key.Name]` for fast
  detection. `Tests/Tests/Wave3ResxIntegrityTests.cs` enforces: identical key sets, no empty values,
  Persian codepoints in FA values, matching `{0}`/`{1}` placeholder sets per key, ZWNJ +
  Persian-only letters (not Arabic), and that every key carries a documented prefix.
- **First launch defaults to Persian** (product decision; `LanguageExplicitlySet` guard), switchable
  anytime **without restart**: `ApplyLanguage` → `LanguageHook.NotifyLanguageChanged` → every
  ViewModel re-raises; `App.ApplyFlowDirection()` flips Shell + open pages.
- **XAML**: `{localize:Tr Key.Name}` (`Presentation/Components/TrExtension.cs`) re-resolves on live
  language change; prefer it over `{Binding SomeText}` for static labels.
- **RTL propagation**: set on the root page in `App.CreateWindow` (Shell chrome needs it at creation),
  re-applied app-wide on language change, mirrored per page by `BaseContentPage`. Layout uses
  `Start`/`End`, never `Left`/`Right`.
- **Fonts**: per-language resolution (`FontFamilies`): Latin → OpenSans, Persian → **Vazirmatn**
  (SIL OFL 1.1, full Arabic-script coverage) so Persian text never falls back to a Latin-only face.
  `BaseContentPage.ApplyFont` walks the visual tree on load/language change;
  `FontFamilyOverride="True"` exempts a label (e.g. the brand wordmark).
- **Locale formatting** (`IFormatService`): dates through the `fa-IR` culture — whose default calendar
  is Persian (**Jalali**: `11 Sep 2026` → `جمعه ۲۰ شهریور`) — Persian native digits (۰-۹), `٪` percent
  **before** the number, durations from resx templates (`7h 40m` ↔ `۷ ساعت و ۴۵ دقیقه`).
  `CultureBootstrap` resolves cultures with a fallback chain (`fa-IR` → `fa` → invariant) so a
  trimmed-ICU device degrades formatting instead of crashing; **RTL follows the chosen language, not
  the ICU data**, so layout still mirrors on such a device.
- **Hard rule**: the engines never emit prose. Every recommendation, insight, reason, plan item and
  weekly bullet is a **localization key + format args** (`TextKey`, `ExplainKey`, `SummaryKey`/
  `BodyArgs`, `TitleKey`, `FocusKey`…). Translation, digits and calendar live only in presentation.
  `ApplicationPurityTests` enforces the rule mechanically (no sentence-shaped literals anywhere in
  `Application/**` or `Domain/**`) and `Wave3EmissionCatalog` cross-checks every key the engines can
  emit against both resource files.

## 5. Intelligence model & the AI boundary

- **Determinism first**: `RuleEngine` → `RecommendationService`/`DailyPlanService` produce structured
  facts. Same state in → same output out. The pipeline is fully testable and offline.
- **Rules** (`Application/Rules/RuleEngine.cs`, thresholds named for tests and docs):
  | Rule | Fires when | Priority | Plan adjustments |
  |---|---|---|---|
  | `Rule.SleepDebt` | baseline − sleep ≥ **1.5h** (High at ≥2.5h) | Medium/High | `bedtime:-30min` |
  | `Rule.SleepDebtReduceIntensity` | same condition | Medium | `exercise:*0.5` |
  | `Rule.LowRecoveryReduce` | recovery < **0.55** or >18% below baseline | Medium | `exercise:*0.5`, `recovery:+15min` |
  | `Rule.HighStress` | stress > **0.65** (High at >0.8) | Medium/High | `focus:-1block`, `recovery:+10min` |
  | `Rule.HighStressScreens` | same condition | Low | `winddown:+30min` |
  | `Rule.ActivityDeficit` | steps < **55%** of baseline **and** recovery ≥ 0.55 | Low | `walk:+15min` |
  | `Rule.PositiveMomentum` | sleep normal, recovery ≥0.65, stress <0.5 | Low | — |
  | `Rule.HabitAtRisk` | streak ≥3, nothing logged, **hour ≥ 18** | Medium | `habit:<id>:prompt` |
  | `Rule.StaleSleep` | sleep feed older than **2 days** | Optional | — (guard, never resizes) |

  Results are ordered by priority, ties broken by `RuleKey` (ordinal) — a stable, testable order.
- **Recommendation caps** (anti-overwhelm): rule outputs are deduped per action, then
  **max 1 High + 2 lower = 3 visible**; a perfectly balanced day gets one gentle `KeepRoutine`
  anchor instead of an empty panel.
- **Plan adaptation grammar** (rules mutate the plan through tiny structured strings, applied by
  `DailyPlanService.Apply` in `Application/Planning/RecommendationService.cs`): `exercise:*0.5`,
  `bedtime:-30min`, `focus:-1block`, `recovery:+15min`, `walk:+15min`, `winddown:+30min`,
  `habit:<id>:prompt`. Every touched `PlanItem` records `AdaptedByRule`; `DailyPlan.AdaptationRuleKeys`
  lists all firing rules; `AdaptationReasonKey(plan)` maps them to exactly one explanation key
  (`Plan.Adapted.Multiple` when several rules moved things — it never picks one at random).
  Adaptation only ever **reduces** load in this wave.
- **Explainability**: every `Recommendation` carries `ExplainKey`+`ExplainArgs` (why),
  `ExpectedBenefitKey` (what for), `Confidence`, and `ProducedByRule` (traceability). The UI can
  always answer "why is this here?" with a real rule key.
- **The AI boundary**: `IIntelligenceProvider` is a *swappable phrasing layer*. It receives the
  deterministic state + recommendations and may only return `InsightInterpretation` — headline/body
  **keys + args** + priority/confidence; any `SuggestedRecommendationOverrides` must pass rule-engine
  validation before applying. It can never invent measurements. Today's implementation,
  `SampleIntelligenceProvider`, is rule-driven (top rule → `InsightTopic` → keys, confidence damped by
  state confidence, forced "balanced day" when the sleep feed is stale) and **makes zero network
  calls**.
- **Habit analytics**: "best completion window" comes from the time-of-day histogram and needs ≥5
  logged completions to claim a pattern; focus blocks scale down under high stress.

## 6. Honesty rules (product invariants)

1. **Sample data is labeled everywhere**: `Today.SampleDataNote`, `Intelligence.MockBadge`,
   `Health.SourceMock`, connection center shows `Profile.Status.MockActive` / `NotConnected` — never a
   fake "Connected". `DailyInsight.Source = Mock`.
2. **User-entered data is labeled self-reported**: manual entries keep `DataOrigin.Manual` forever and
   are never presented as measured. `Origin` survives normalization and derivation
   (`UserStateServiceTests` pins it).
3. **No fabricated device signals**: `SampleHealthProvider` omits resting HR and HRV (`null`) and its
   advertised `DataSourceCapabilities` exclude them.
4. **Confidence is always surfaced**: baseline gates (<3 days None, <7 Low, <14 Medium, ≥14 High),
   trend `InsufficientData` below 5 samples, ±12% dead band on `MetricState.Level`, state
   `Confidence` + `DataCompleteness` on Today. Stale feeds downgrade the insight topic instead of
   pretending freshness, and never drive action rules (`Quality == Complete` required).
5. **A check that failed is a failure**: `UpdateCheckStatus.NoConnection`/`RateLimited`/`Error` are
   distinct from `UpToDate`; `AppVersion.Compare` returns **null** ("no comparison possible") for
   unparseable input instead of 0; a build newer than the feed is `NewerThanFeed`, not "up to date";
   a cached answer carries `FromCache = true`.
6. **A notification that wasn't granted is not delivered**: `NotificationGrantState` reports the real
   platform answer (`Unknown` is the default; `Allowed` must be earned from the API).
7. **No permission prompts for unused features**: `PermissionService.RequestAsync` never asks —
   Health/Activity/Calendar report `UnavailableInPhase` rather than collecting trust we can't spend.
8. **Local-only, deletable**: JSON files under `FileSystem.AppDataDirectory/LIVORA/`
   (`livora_goals/habits/bootcamps/profile/history.json`, plus the Wave 3
   `livora_manual_entries.json`, `update_feed.json`, `livora_reminders.json`,
   `livora_snoozed.json`) + MAUI Preferences. No network, no accounts, no telemetry. Profile's privacy
   inventory lists each category with its origin (Manual/Mock) and location (Device), and
   **Delete all local data** removes every file.
9. **The weekly review refuses to summarize nothing**: fewer than 3 closed days ⇒ `null`, and 3–6 days
   ⇒ `Confidence = Low` with every trend held at `InsufficientData`.

## 7. Build & test

Prerequisite: the SDK pinned by `global.json` (**10.x**, `rollForward: latestFeature`). The pin exists
because the installed MAUI workloads are 10.x; 11-preview workloads lack the 10.x packs.

```bash
# Unit tests — no MAUI workload needed, plain net10.0
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q

# Windows (unpackaged dev build — WindowsPackageType=None)
dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q

# Android
dotnet build LIVORA.csproj -f net10.0-android                 # Debug — see caveat below
dotnet build LIVORA.csproj -c Release -f net10.0-android       # Release (the wave-verified path)

# iOS / Mac Catalyst (require a paired Mac — standard MAUI constraint)
dotnet build LIVORA.csproj -f net10.0-ios
dotnet build LIVORA.csproj -f net10.0-maccatalyst
```

CI (`.github/workflows/ci.yml`) runs `dotnet test` on every push/PR to `master` plus Windows and
Android Release builds; `.github/workflows/release.yml` builds APK/AAB, an unpackaged win-x64 zip, an
iOS simulator app and a Catalyst app on a `v*` tag.

Caveats:
- **Android Debug builds can fail with file-lock errors in `obj/`** when an emulator or hot-reload
  session holds them; `dotnet build-server shutdown` clears it, and Release avoids the hazard.
- If `obj/` is stale, build into a scratch path:
  `-p:BaseIntermediateOutputPath=objw3/ -p:BaseOutputPath=binw3/`.
- XAML compiles via **source generation** (`MauiXamlInflator=SourceGen`): a mistyped property is a
  hard build error (MAUIX2002), not a runtime surprise.
- A `*Brush` resource is valid **only** on `Border.Background`, `Border.Stroke` and `Shape.Fill`.
  Color-typed properties (`TextColor`, `BackgroundColor`, `ProgressColor`, `Shell.*Color`) take a
  Color or an inline `{AppThemeBinding Light=…, Dark=…}`; a brush there logs
  "Cannot convert SolidColorBrush to type Color" and breaks Android rendering.

### The test project

`Tests/LIVORA.Tests.csproj` targets **plain `net10.0`** and links source directly — no MAUI
reference, no mocking framework (hand-written fakes in `Tests/Tests/TestFakes.cs`):

| Compiled in | Why |
|---|---|
| `Domain/**`, `Application/**` | pure layers |
| `Infrastructure/IntelligenceProviders/SampleIntelligenceProvider.cs` | depends only on Domain + Application |
| `Infrastructure/Localization/*.cs` (Wave 3) | MAUI-free: only `Application.Abstractions` + `Logging.Abstractions` |
| `Resources/Localization/AppResources.cs` + both `.resx` (Wave 3) | gives the test assembly a real resource manifest, so `[missing]` and culture behavior are tested end to end |
| `Infrastructure/Persistence/DemoDataSeeder.cs` (Wave 3) | talks to `IRepository<T>` + a `Func<string,string>` localizer — no IO |

Nothing that needs a MAUI head is compiled in, and that is the rule for adding more: `Services/**`,
`Presentation/**` and Infrastructure files touching `FileSystem`/`Preferences` stay out.

**438 test cases** (117 Wave 2 + 321 added in Wave 3 (lane 10)) across these files:

| File | Covers |
|---|---|
| `DomainModelTests`, `StateAndBaselineTests`, `NormalizerAndProviderTests`, `RuleAndRecommendationTests`, `PlanAndRuleHonestyTests`, `InsightsAndSummaryTests` | the Wave 2 suites (unchanged, still green) |
| `Wave3AppVersionTests` | every documented `AppVersion` shape, `1.9` vs `1.10`, `v1.2`, `1.2.3-rc.4`, and null ⇒ "no comparison possible" |
| `Wave3LocalizationTests` | real `LocalizationService`: Persian default, live switching, `[missing]`, Persian digits/percent/durations, Jalali dates, fonts, `CultureBootstrap`, and the Wave 3 contract records/enums |
| `Wave3ResxIntegrityTests` | EN/FA key parity, no empty values, Persian (not Arabic) script, placeholder parity + waivers, key prefixes, compiled-resource resolution |
| `Wave3HonestyTests` | every emitted key resolves in both languages; mock disclosures say "sample"/"نمونه"; `Completeness` accounting; the ±12% dead band; rule thresholds on their documented boundaries |
| `ApplicationPurityTests` | no prose-shaped literals in `Application/**`/`Domain/**`; every key-shaped literal is a real resource key |
| `Wave3UserStateTests` | `UserStateService` (previously untested): derived-Focus honesty, provenance, confidence, staleness, snapshot rules, cache/coalescing/reentrancy; `BaselineService` wrap-around + gates |
| `Wave3ProgramAndPlanTests` | `ProgramAdapter` (previously untested) and the plan grammar clauses the Wave 2 suite missed |
| `Wave3DomainAndSeedTests` | Saturday week start, streak/goal/bootcamp arithmetic, `DemoDataSeeder` one-shot seeding (deleted sample content stays deleted; catalog re-seed guarded by `TitleKey`), history/summary shapes |
| `Wave3ThresholdTests` | trend band, normalizer inclusive ranges, recommendation cap ordering, weekly <3-days gate |
| `Wave3DiIntegrityTests` | marker regions exist exactly once, no duplicate DI registrations, every registered concrete type exists, and a **real** `ServiceProvider` resolves the MAUI-free graph (singletons shared, one end-to-end honest cycle) |

## 8. Wave 3 feature map (what the client gets)

| Feature | Entry point | State |
|---|---|---|
| Log your own data | Log tab → `LogEntryPage` (route `log-entry`) | contract shipped, UI in flight (lanes 02/03) |
| Real goals/habits | Goals tab → `goal-editor` / `habit-editor` | contract exists, UI in flight (lane 07) |
| Adaptive program detail | Programs tab → `bootcamp-detail` | adaptation engine **done**; page in flight (lane 08) |
| Sleep trend chart | Log tab (`SkiaSharp` `SKCanvasView`) | in flight (lane 03) |
| Reminders | Profile → `reminders` | contract shipped, engine in flight (lane 09) |
| In-app update check | Profile card → `updates` | contract + `AppVersion` **done**; feed in flight (lane 01) |
| Theme mode | Profile/Settings (`ThemeMode`) | contract + persistence **done**; `ThemeService` in flight (lane 05) |
| Desktop layout | `AdaptiveLayout` / `ResponsiveGrid` breakpoints | in flight (lane 04) |
| Weekly review | Today + Profile → `WeeklySummaryPage` | service **done**; page wiring in flight (lanes 08/09) |

## 9. Status (real / mock / not implemented)

The three columns are the honest state of **this repository**, not of the plan:

| Area | Status | Evidence |
|---|---|---|
| 5-tab UI, onboarding, light/dark tokens | **Real** (Wave 1) | `AppShell.xaml`, `Presentation/Views/**` |
| Bilingual EN/FA, RTL, live language switch, Jalali + Persian digits/percent/durations | **Real** | 333 keys × 2, `LocalizationAndFormattingTests` (65 cases) |
| State engine: provider → normalizer → history → baselines → `PersonalState` | **Real** | `UserStateServiceTests`, `BaselineServiceWave3Tests` |
| Rule engine, recommendations (capped), daily plan + adaptation grammar, program adapter | **Real** | `Wave3ThresholdTests`, `ProgramAdapterTests`, `DailyPlanGrammarTests` |
| Weekly review logic (incl. the <3-day refusal) | **Real** | `WeeklySummaryServiceTests`, `Wave3ThresholdBoundaryTests` |
| Privacy inventory + delete-all-local-data | **Real** (device files only) | `Infrastructure/Security/PrivacyService.cs` |
| Version-compare logic for the update check | **Real** | `AppVersionTests` (37 cases) |
| Wave 3 contracts (updates, manual entry, reminders, theme) | **Real as contracts**; no implementations in this tree | `Application/Abstractions/IWave3Contracts.cs` |
| Health data source | **Mock** | `SampleHealthProvider` — deterministic, labeled, HR/HRV honestly absent |
| Intelligence provider | **Mock** (rule-driven phrasing) | `SampleIntelligenceProvider`, `DailyInsight.Source = Mock` |
| Manual entry store/overlay | **Not implemented here** (lane 02) | `IManualEntryService` exists; `ManualEntryStore`/`ManualOverlayProvider` are in flight |
| Update feed / `UpdateService` | **Not implemented here** (lane 01) | `IUpdateService` exists; no `Infrastructure/Updates/**` |
| Reminders / notifications | **Not implemented here** (lane 09) | `IReminderService` exists; no `Infrastructure/Notifications/**` |
| `ThemeService` | **Not implemented here** (lane 05) | `IThemeService` + persisted `ThemeMode` exist; no implementation |
| Log tab, goal/habit editors, bootcamp page, charts, responsive desktop layouts | **Not implemented here** (lanes 03/04/07/08) | see §8 |
| Real providers (Apple Health, Health Connect, Garmin, Fitbit…) + OAuth | **Not implemented** | no adapters |
| Real LLM interpretation | **Not implemented** | mock phrasing only |
| Cloud sync, accounts, telemetry | **Not implemented** (deliberate) | local-only by design |
| Store packaging (MSIX/Play/App Store) | **Not implemented** | `WindowsPackageType=None` (unpackaged dev build) |
| Application-launch / UI smoke test | **Not automated** | "the app starts" still rests on manual runs — see `docs/Wave3.md` debt |

## 10. Roadmap

- **Wave 3 (in flight)**: the §8 features land through the lane protocol; the merge checklist in
  `docs/WAVE3-MERGE.md` gates them, and lane 10's second pass adds tests for the pure helpers each
  lane ships (`LogEntryRules`, `GoalEditorRules`, `BootcampProgress`, `ProgramDiscovery`,
  `WeekProgress`, `ManualMerge`, `ReminderEngine`).
- **Wave 4**: first real data provider (Health Connect / Apple Health) behind `IDataProvider`, real
  permission prompts once integrations exist, real `IIntelligenceProvider` (phrasing only, still
  bounded by rule-engine facts, with honest failure fallback).
- **Wave 5**: goal measurement from real metrics (`DailyMetricAverage/Sum/ThresholdDayCount`),
  program marketplace, cross-device sync with an explicit privacy posture.
