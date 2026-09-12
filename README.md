# LIVORA

**Personal life & performance OS.** Not a chatbot. LIVORA runs a closed loop:

> **data → state → intelligence → recommendation → plan → adaptation**

It ingests daily health signals (mock for now), derives *your* state against *your own* baseline
(not population averages), fires deterministic rules, adjusts today's plan, and explains every
change — in English or Persian, with full right-to-left support.

- .NET 10, .NET MAUI, single project, multi-target: Android / iOS / Mac Catalyst / Windows
- Root namespace `LIVORA`, app id `com.livora.app`, version 1.1 (build 2)
- Clean architecture: `Domain → Application → Infrastructure → Presentation`, DI composed in `MauiProgram.cs`
- Persian (`fa`) is a first-class, first-launch default language — the app is RTL out of the box

---

## 1. Data flow

```
SampleHealthProvider            (Application/HealthData — deterministic mock, DataPoints
   │                             with origin/quality/confidence; HR/HRV honestly null)
   ▼
DataNormalizer                  (sanity ranges, Missing/Invalid clamping, staleness windows:
   │                             sleep 2d, activity 1d, recovery 2d, wellness 3d)
   ▼
DailyHistoryStore               (Infrastructure/Persistence — rolling JSON history,
   │                             24-day backfill on first run, 120-day cap)
   ▼
BaselineService ──► UserStateService   (28-day rolling personal baselines, confidence-gated;
   │                TrendService        half-vs-half trends, ≥5 samples only)
   ▼
PersonalState                   (Domain/Models/State — derived, typed snapshot; the ONLY
   │                             thing rules and UI consume; raw provider data stops above)
   ▼
RuleEngine                      (7 deterministic rules → structured RuleResults)
   ▼
RecommendationService + DailyPlanService   (≤3 visible recs; plan skeleton + rule adaptations)
   ▼
IntelligenceOrchestrator  ◄──── SampleIntelligenceProvider (IIntelligenceProvider — mock,
   │                             rule-driven phrasing; a real LLM can swap in later)
   ▼
Today UI (TodayViewModel → TodayPage)      (everything rendered is a localization key + args)
```

Side branches: `WeeklySummaryService` reads history + trends for the weekly review modal;
`ProgramAdapter` adapts bootcamp days through the *same* `RuleEngine`, so recommendations and
program adaptation can never disagree.

## 2. Layers

Dependency direction: **Presentation → Application → Domain**; **Infrastructure → Application → Domain**.
`MauiProgram.cs` is the composition root — the only place concrete implementations are named.

| Layer | Rule |
|---|---|
| `Domain/` | Pure C#. Models + enums + constants. No MAUI, no IO, no services. Knows *what things are*. |
| `Application/` | Pure C#. Contracts (`Abstractions/`) + engines (State, Rules, Planning, Insights, HealthData, Context). No platform APIs. Knows *how decisions are made*. This layer is what the xunit project compiles directly. |
| `Infrastructure/` | Platform adapters: persistence (JSON files), localization (resx/culture/fonts), settings (MAUI Preferences), security (permissions/privacy), intelligence providers. Implements Application contracts. |
| `Presentation/` | MAUI views, ViewModels by feature, converters, `BaseContentPage`, `Theme.cs`. Knows *how to show*, never *what to decide*. |

### File tree (source only)

```
LIVORA/
├── global.json                     # pins SDK 10.0.303 (rollForward: latestFeature)
├── LIVORA.csproj                   # multi-target MAUI app; Tests excluded
├── LIVORA.slnx                     # app + test project
├── MauiProgram.cs                  # composition root: all DI registrations
├── App.xaml(.cs)                   # resource dictionaries; window + FlowDirection bootstrap
├── AppShell.xaml(.cs)              # 5 tabs: Today / Health / Goals / Programs / Profile
│
├── Domain/                         # pure — no dependencies
│   ├── Constants/AppConstants.cs   # file names, targets (7.5h sleep, 8000 steps, 30 active min)
│   ├── Enums/                      # Domain, HealthData (DataOrigin/Quality/Capabilities),
│   │                               # State (BaselineConfidence/TrendDirection/StateLevel),
│   │                               # Planning (priority ladder, action kinds), Security
│   └── Models/
│       ├── Health/DataPoint.cs     # DataPoint + NormalizedDay (raw-measurement contract)
│       ├── State/PersonalState.cs  # MetricState, Baseline, domain states, Metrics keys
│       ├── History/DailyHistoryRecord.cs  # + WeeklySummary
│       ├── Planning/DailyPlan.cs   # PlanItem, DailyPlan, RuleResult
│       ├── Goals/Goal.cs           # Goal + Habit (streaks, completion log)
│       ├── Programs/Bootcamp.cs    # adaptive program + DailyInsight + Recommendation
│       └── UserProfile.cs
│
├── Application/                    # pure — contracts + engines
│   ├── Abstractions/               # IDataProvider, IDataNormalizer, IRepository,
│   │                               # IStateContracts (IBaselineService/IUserStateService/
│   │                               # ITrendService/IHistoryRepository), IIntelligenceContracts
│   │                               # (IRuleEngine/IRecommendationService/IDailyPlanService/
│   │                               # IIntelligenceProvider), IIntelligenceService,
│   │                               # ILocalizationService, IFormatService, ISettingsService,
│   │                               # ISecurityContracts, IDataSource
│   ├── Context/SessionState.cs     # active UserProfile for the session
│   ├── HealthData/                 # SampleHealthProvider, DataNormalizer
│   ├── State/                      # BaselineService, UserStateService, TrendService
│   ├── Rules/RuleEngine.cs         # the deterministic safety layer
│   ├── Planning/                   # RecommendationService, DailyPlanService, ProgramAdapter
│   └── Insights/                   # IntelligenceOrchestrator, WeeklySummaryService
│
├── Infrastructure/
│   ├── Persistence/                # JsonFileStore + JsonRepository, DailyHistoryStore,
│   │                               # DemoDataSeeder (first-launch sample content)
│   ├── Localization/               # LocalizationService (+IFormatService), FontFamilies,
│   │                               # LanguageHook
│   ├── Settings/                   # PreferencesSettingsService + CultureBootstrap
│   ├── Security/                   # PermissionService (never prompts yet), PrivacyService
│   └── IntelligenceProviders/      # SampleIntelligenceProvider (rule-driven mock "AI")
│
├── Presentation/
│   ├── Theme.cs                    # single source of code-side colors (mirrors XAML tokens)
│   ├── ObservableObject.cs         # VM base: localization glue, FlowDirection/font re-raise
│   ├── ViewModels/<feature>/       # Today, Health, Goals, Programs, Profile, Review, Onboarding
│   ├── Views/<feature>/            # pages (all derive BaseContentPage) + WeeklySummary modal
│   └── Components/                 # ItemsStackLayout, ColorByKeyConverter, IsNotEmptyConverter
│
├── Resources/
│   ├── Localization/               # AppResources.resx (en) + AppResources.fa.resx (fa),
│   │                               # 325 keys each, hand-written accessor (no designer)
│   ├── Styles/                     # LivoraColors.xaml, LivoraStyles.xaml
│   └── Fonts/                      # OpenSans (Latin), Vazirmatn (Persian, SIL OFL 1.1 —
│                                   # license in Resources/Fonts/OFL-Vazirmatn.txt)
├── Platforms/                      # Android / iOS / MacCatalyst / Windows entry points
└── Tests/                          # xunit project (see §6)
```

## 3. Wave 1 → Wave 2

**Wave 1** shipped the shell as a single `LIVORA.Core` + app project: onboarding, five-tab UI,
bilingual EN/FA with RTL, local preferences, mock data source, bootcamp catalog. No state model,
no rules, no tests.

**Wave 2** (this codebase) restructured into four layers and added the intelligence core:

- **Raw-vs-derived split**: `DataPoint` (a measurement with provenance: `DataOrigin`,
  `DataQuality` = Missing/Complete/Estimated/Stale/Invalid, `Confidence` 0..1) vs `MetricState`
  (value *plus* deviation from the user's **personal** baseline). Nothing downstream sees raw
  provider data.
- **Baselines**: 28-day rolling window per metric (`Baseline.FromSamples`), mean + stddev,
  confidence gates by sample count: **<3 days → None, <7 → Low, <14 → Medium, ≥14 → High**.
  Bedtime averaging is wrap-around–safe (23:50 vs 00:40).
- **Derived state**: `PersonalState` composes Sleep / Activity / Recovery / Wellness / Focus
  domains + habit & goal snapshots; `Level` uses a ±12% dead band around personal baseline
  ("small deltas are noise, not signal"); overall `Confidence = min(domain confidences)`.
  Focus is honestly `Quality = Estimated, IsDerived = true` — never presented as measured.
- **Trends**: first-half vs second-half mean, 6% noise band, **<5 samples → InsufficientData**
  (no fake conclusions from tiny windows).
- **Rules engine** (deterministic, same input → same output), 7 rules:
  `SleepDebt` (≥1.5h below personal sleep baseline; High if ≥2.5h),
  `LowRecovery` (<0.55 absolute or >18% below baseline),
  `HighStress` (>0.65; High if >0.8),
  `ActivityDeficit` (steps <55% of baseline, gated on recovery),
  `PositiveMomentum` (all near baseline — a positive rule so good days don't feel like
  interventions),
  `HabitAtRisk` (streak ≥3, nothing logged after 18:00),
  `StaleSleep` (feed >2 days old → intelligence must not pretend it's fresh).
- **Planning**: `RecommendationService` + `DailyPlanService` (below), plus `ProgramAdapter`
  replacing Wave 1's inline bootcamp tweaks.
- **History & look-back**: `DailyHistoryStore` (backfilled, capped) + `WeeklySummaryService`
  (week-vs-prior-week trends, improvement/decline keys, one focus for next week,
  sample-size-aware confidence; returns `null` below 3 days — refuses to summarize nothing).
- **Platform honesty services**: `IPermissionService`, `IPrivacyService`, connection center.
- **xunit test suite** for the pure layers (100+ tests, green as of this writing).

## 4. Bilingual / RTL system

- **Contract**: `ILocalizationService` (Application) — indexer + `T(key, args)`, `IsRightToLeft`,
  `FormatCulture`, per-language font properties, `LanguageChanged` event. Implemented by
  `LocalizationService` (Infrastructure), which also implements `IFormatService`.
- **Strings**: `Resources/Localization/AppResources.resx` (English neutral) + `AppResources.fa.resx`
  (Persian), 325 matching keys, resolved through a hand-written `AppResources` accessor so plain
  `dotnet build` works without VS designers. Missing keys render as `[Key]` for fast detection.
- **First launch defaults to Persian** (product decision; `LanguageExplicitlySet` guard), switchable
  anytime **without restart**: `ApplyLanguage` → `LanguageHook.NotifyLanguageChanged` → every
  ViewModel re-raises; `App.ApplyFlowDirection()` flips Shell + open pages.
- **RTL propagation**: set on the root page in `App.CreateWindow` (Shell chrome needs it at
  creation), re-applied app-wide on language change, and mirrored per page by
  `BaseContentPage` (binds `FlowDirection` from its ViewModel).
- **Fonts**: per-language family resolution (`FontFamilies`): Latin → OpenSans, Persian →
  **Vazirmatn** (SIL OFL 1.1, full Arabic-script coverage — Persian text never falls back to a
  Latin-only font). `BaseContentPage.ApplyFont` walks the visual tree on load/language change;
  `FontFamilyOverride="True"` exempts a label (e.g. the brand wordmark).
- **Locale formatting** (`IFormatService`): dates via `fa-IR` culture — whose default calendar
  is Persian (**Jalali**: "پنجشنبه ۲۰ شهریور ۱۴۰۵") — Persian native digits (۰-۹), `٪` percent,
  locale-aware durations ("7h 40m" ↔ "۷ ساعت و ۴۰ دقیقه"). `CultureBootstrap` registers cultures
  with an invariant fallback for platforms where ICU `fa-IR` data is trimmed (Android/iOS
  sometimes are) — formatting never crashes.
- **Hard rule**: the engines never emit prose. Every recommendation, insight, reason, plan item
  and weekly bullet is a **localization key + format args** (`TextKey`, `ExplainKey`,
  `SummaryKey/BodyArgs`, `TitleKey`, `FocusKey`…). Translation, numbers and calendar live only in
  the presentation layer.

## 5. Intelligence model & the AI boundary

- **Determinism first**: `RuleEngine` → `RecommendationService`/`DailyPlanService` produce
  structured facts. Same state in → same output out. The whole pipeline is testable and offline.
- **Recommendation caps** (anti-overwhelm): rule outputs are deduped per action, then
  **max 1 High + 2 lower-priority = 3 visible**; a perfectly balanced day gets one gentle
  `KeepRoutine` anchor instead of an empty panel.
- **Plan adaptation grammar** (rules mutate the plan through tiny structured strings,
  applied by `DailyPlanService.Apply`): `exercise:*0.5`, `bedtime:-30min`, `focus:-1block`,
  `recovery:+15min`, `walk:+15min`, `winddown:+30min`, `habit:<id>:prompt`.
  Every touched `PlanItem` records `AdaptedByRule`; `DailyPlan.AdaptationRuleKeys` lists all
  firing rules; `AdaptationReasonKey(plan)` maps them to one explanation key per state.
- **Explainability**: every `Recommendation` carries `ExplainKey`+`ExplainArgs` (why),
  `ExpectedBenefitKey` (what for), `Confidence`, and `ProducedByRule` (traceability for UI and
  tests). The UI can always answer "why is this here?" with a real rule key, not vibes.
- **The AI boundary**: `IIntelligenceProvider` is a *swappable phrasing layer*. It receives the
  deterministic state + recommendations and may only return `InsightInterpretation` — headline/body
  **keys + args** + priority/confidence; any `SuggestedRecommendationOverrides` must pass
  rule-engine validation before applying. It can never invent measurements or facts.
  Today's implementation, `SampleIntelligenceProvider`, is rule-driven (maps top rule →
  `InsightTopic` → keys, dampens confidence by state confidence, forces a "balanced day" when
  the sleep feed is stale) and **makes zero network calls**. A future OpenAI/local-LLM provider
  replaces it behind the same interface; the UI never knows which is active.
- **Wave 2 extras**: habit "best completion window" from the time-of-day histogram (needs ≥5
  logged completions to claim a pattern); focus blocks scale down under high stress.

## 6. Honesty rules (product invariants)

1. **Sample data is labeled everywhere**: "sample data" note on Today
   (`Today.SampleDataNote`), `Intelligence.MockBadge`, Health source badge
   (`Health.SourceMock`), connection center shows `Mock active / Not connected` — never a fake
   "Connected". `DailyInsight.Source = Mock`.
2. **No fabricated device signals**: `SampleHealthProvider` honestly omits resting HR and HRV
   (`null`) — its advertised `DataSourceCapabilities` exclude them. The mock "provides
   everything except device-grade HR/HRV".
3. **Confidence is always surfaced**: baseline confidence gates (None/Low/Medium/High), trend
   `InsufficientData`, ±12% noise band, state `Confidence` + `DataCompleteness` shown on Today.
   Stale feeds downgrade insight topics instead of pretending freshness.
4. **No permission prompts for unused features**: `PermissionService.RequestAsync` never asks —
   Health/Activity/Calendar report `UnavailableInPhase` rather than collecting trust we can't
   spend yet (explicit "trust debt" rule in code).
5. **Local-only, deletable**: everything lives in JSON files under `FileSystem.AppDataDirectory/LIVORA/`
   (`livora_goals/habits/bootcamps/profile/history.json`) + MAUI Preferences. No network, no
   accounts, no telemetry. The Profile page's privacy inventory lists each stored category with
   its origin (Manual/Mock) and location (Device), and **Delete all local data** removes every
   file.

## 7. Build & test

Prerequisite: SDK pinned by `global.json` to **10.0.303** (`rollForward: latestFeature`).
That pin exists because the installed MAUI workloads are 10.x (`dotnet workload list`:
android 36.1.43, ios/maccatalyst 26.5.x, maui-windows 10.0.20 — all under 10.0.100 manifests);
11-preview workloads lack the 10.x packs.

```bash
# Windows (unpackaged dev build — WindowsPackageType=None)
dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0

# Android
dotnet build LIVORA.csproj -f net10.0-android            # Debug — see caveat below
dotnet build LIVORA.csproj -c Release -f net10.0-android # Release (the wave-verified path)

# iOS / Mac Catalyst (require a paired Mac, standard MAUI constraint)
dotnet build LIVORA.csproj -f net10.0-ios
dotnet build LIVORA.csproj -f net10.0-maccatalyst

# Unit tests (pure Domain + Application layers)
dotnet test Tests/LIVORA.Tests.csproj
```

Caveats:
- **Android Debug builds can fail with file-lock errors in `obj/`** when an emulator or
  hot-reload session holds them; `dotnet build-server shutdown` / closing the emulator clears
  it, and Release Android avoids the hazard (no shared hot-reload outputs).
- XAML compiles via **source generation** (`MauiXamlInflator=SourceGen`): a mistyped property
  is a hard build error (MAUIX2002) instead of a runtime surprise. Note that MAUI `Label` has
  `HorizontalTextAlignment`/`VerticalTextAlignment`, not `TextAlignment` — a few views in the
  current working tree still use the bare name and fail all four TFMs until renamed.
- The test project (`Tests/LIVORA.Tests.csproj`) targets plain `net10.0` and **links the
  Domain + Application `.cs` files directly** — no MAUI reference, no mocking framework
  (hand-written `InMemoryRepo` fakes). Test source is excluded from the app via
  `<Compile Remove="Tests\**" />` in `LIVORA.csproj`.

## 8. Status (Wave 2)

| Area | Status |
|---|---|
| 5-tab UI, onboarding (name/activity/schedule/language), theme, light/dark tokens | **Done** (Wave 1) |
| Bilingual EN/FA, full RTL, live language switch, per-language fonts, Jalali/locale formatting | **Done** (Wave 1, hardened Wave 2) |
| Settings persistence (MAUI Preferences), profile + goals/habits/programs JSON repositories | **Done** |
| State engine: DataPoint/NormalizedDay, normalizer sanity+staleness, daily history (backfill, cap) | **Done** (Wave 2) |
| Personal baselines (28d, confidence gates), trends, PersonalState derivation incl. derived Focus | **Done** (Wave 2) |
| Rule engine (7 rules), recommendations with caps, daily plan + adaptation grammar, explainability | **Done** (Wave 2) |
| Weekly review, program adapter, connection center, privacy inventory + delete-all, permission abstraction | **Done** (Wave 2) |
| xunit suite for Domain + Application (100+ tests, `net10.0`, green) | **Done** |
| Health data source | **Mock** — `SampleHealthProvider` only (deterministic, labeled) |
| Intelligence provider | **Mock** — `SampleIntelligenceProvider` (rule-driven, offline) |
| Real providers (Apple Health, Health Connect, Garmin, Fitbit…) incl. OAuth | **Not implemented** |
| Notifications | **Not implemented** |
| Marketplace / community / creator programs | **Not implemented** |
| Cloud sync / accounts | **Not implemented** |
| Real LLM interpretation behind `IIntelligenceProvider` | **Not implemented** |
| Store packaging (Windows MSIX etc.) | **Not implemented** (Wave 3 task, per csproj comment) |

## 9. Roadmap (next waves)

- **Wave 3**: first real data provider (Health Connect / Apple Health) behind `IDataProvider`,
  real permission prompts now that integrations exist, notifications, Windows Store packaging.
- **Wave 4**: real `IIntelligenceProvider` (local or cloud LLM) — phrasing only, still bounded by
  the rule-engine facts and validation; provider-failure fallback to the deterministic text.
- **Wave 5**: goal measurement from real metrics (`DailyMetricAverage/Sum/ThresholdDayCount`),
  program marketplace, cross-device sync (with an explicit, honest privacy posture).
