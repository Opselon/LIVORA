# LIVORA — Wave 3 lane protocol (10 parallel lanes, disjoint file sets)

Repo: `C:\Users\Capsizer\source\repos\LIVORA`. Your copy:
`C:\Users\Capsizer\AppData\Local\Temp\livora_w3\laneNN\` — work ONLY there.

Wave 3 goal: make the app genuinely useful to a client — log your own data, edit real goals and
habits, open an adaptive program, see a real chart, get reminders, check for updates in-app,
look right on a desktop window — while keeping clean architecture, DDD boundaries, SOLID seams,
bilingual EN/FA with real RTL, and the honesty rules (never present mock data as real).

## 0. Hard rules

1. Never run git commands that change state (commit/checkout/branch/stash/push). Read-only
   `git diff`/`git log` are fine. The orchestrator merges and commits.
2. **Edit only files inside your owned paths.** Anything you need in a shared file goes into your
   report as an `APPEND:` block (§5) — exact code, exact target marker. Never write those files.
3. Frozen for everyone (read-only): `Domain/**` except where your lane explicitly owns a file,
   `Application/Abstractions/IWave3Contracts.cs`, `Application/Abstractions/ILocalizationService.cs`,
   `Application/Abstractions/IRepository.cs`, `Application/Abstractions/ISettingsService.cs`,
   `Application/Abstractions/IStateContracts.cs`, `Application/Abstractions/IIntelligenceContracts.cs`,
   `Application/Abstractions/IDataProvider.cs`, `Application/Abstractions/IFormatService.cs`,
   `Application/Abstractions/ISecurityContracts.cs`, `Application/Abstractions/IDataSource.cs`,
   `Application/Abstractions/IIntelligenceService.cs`, `Application/Rules/RuleEngine.cs`,
   `Application/State/**`, `Application/Insights/**`, `Application/HealthData/SampleHealthProvider.cs`,
   `Application/HealthData/DataNormalizer.cs`,
   `Domain/Enums/Wave3Enums.cs`, `Resources/Localization/AppResources*.resx`,
   `Resources/Localization/AppResources.cs`, `Presentation/ObservableObject.cs`,
   `Presentation/Theme.cs`, `Presentation/Components/TrExtension.cs`, `global.json`, `LIVORA.csproj`,
   `Tests/**` (lane 10 only), `.github/**`, `Platforms/**`, `Infrastructure/Localization/*`,
   `Infrastructure/Persistence/JsonFileStore.cs`, `Infrastructure/Persistence/DailyHistoryStore.cs`,
   `Infrastructure/Settings/PreferencesSettingsService.cs`, `docs/LANES.md`.
4. **Localize every user-facing string.** XAML: `{localize:Tr Key.Name}` with
   `xmlns:localize="clr-namespace:LIVORA.Presentation.Components"` (this extension re-resolves on
   live language change — prefer it over `{Binding SomeText}` for static labels; both are allowed).
   C#: `L("Key")` / `L("Key", args)` from `ObservableObject`, or
   `ServiceHelper.Get<ILocalizationService>()`. Keys you invent must be emitted in your
   `KEYS-EN`/`KEYS-FA` block, identical key order and count, real idiomatic Persian (native
   register, ZWNJ, not Arabic), same `{0}`/`{1}` argument counts in both languages.
   Grep the existing keys first — no collisions. Application/Domain code may only emit keys+args.
5. **Honesty (product law).** Never claim a connection, a measurement, an AI model, a delivered
   notification, or an up-to-date version that isn't true. Mock stays labeled mock; user input is
   labeled self-reported; a failed check is reported as a failure, not as "up to date".
6. **RTL.** `Start`/`End`, never `Left`/`Right`. Wrap long Persian text (`LineBreakMode="WordWrap"`
   + sensible `MaxLines`); never truncate a sentence. Grids must read correctly mirrored.
7. **Theme tokens only** (§2). `*Brush` resources are valid **only** on `Border.Background`,
   `Border.Stroke`, `Shape.Fill`. Color-typed properties (`TextColor`, `BackgroundColor`,
   `ProgressColor`, `PlaceholderColor`, `Shell.*Color`) take a Color or an inline
   `{AppThemeBinding Light=…, Dark=…}`. A brush on a Color property logs
   "Cannot convert SolidColorBrush to type Color" and crashes Android renderers.
8. Pages derive from `views:BaseContentPage` and pass their VM to the base ctor
   (`public FooPage(FooViewModel vm) : base(vm)`), set `x:DataType`, and do not set BindingContext.
   VMs inherit `ObservableObject`, call `SubscribeLanguage()` in the ctor, and re-raise every
   localized property in `protected override void OnLanguageChanged()`.
9. `async Task` services; `Command`/`Command<T>` in VMs; no `.Result`/`.Wait()` on the UI thread;
   no new NuGet packages; no reflection-based service location beyond the existing `ServiceHelper`.
10. **Verify before reporting.** `dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0
    --nologo -v q` must be green in your lane (use
    `-p:BaseIntermediateOutputPath=objw3\ -p:BaseOutputPath=binw3\` if `obj/` is stale). If you
    compile into the test project (`Application/**`, `Domain/**`, MAUI-free `Infrastructure/**`),
    also run `dotnet test Tests/LIVORA.Tests.csproj --nologo -v q`. Never claim a result you did not
    observe: report `fail`/`not-run` honestly.

## 1. Contracts already in your copy (do not edit)

`Application/Abstractions/IWave3Contracts.cs`:
- `IUpdateService` (`CheckAsync(force)`, `PeekCachedAsync()`, `IsFeedConfigured`) + `UpdateInfo`
  (Status, CurrentVersion, LatestVersion, Notes, DownloadLinks, ReleaseUrl, IsNewerThanFeed,
  CheckedAtUtc, ErrorDetail, FromCache) + pure `AppVersion.TryParse/Compare`.
- `IManualEntryService` + `ManualEntryDraft` (nullable per-metric values, `IsEmpty`).
- `IReminderService` + `ReminderSetting` (`Kind`, `TargetId`, `TimeOfDay`, `DaysMask`, `TextKey`)
  + `NotificationGrantState`.
- `IThemeService` (`Mode`, `ResolvedTheme`, `IsDark`, `SetMode`, `ThemeChanged`, `Apply`).

`Application/Abstractions/ISettingsService.cs` — Wave 3 added `ThemeMode` and `LastSeenVersion`
(both implemented by `PreferencesSettingsService` in your copy).
`Domain/Enums/Wave3Enums.cs` — `UpdateCheckStatus`, `NotificationGrantState`, `ThemeMode`.
`Domain/Enums/DomainEnums.cs` — `GoalMeasurement` + `HabitFrequencyKind` already exist.
`Presentation/Components/TrExtension.cs` — the `{localize:Tr …}` markup extension (verified builds).
`MauiProgram.cs` / `App.xaml.cs` / `AppShell.xaml.cs` carry marker comments where your `APPEND:`
blocks land: `// WAVE3-DI:`, `// WAVE3-APP:`, `// WAVE3-SHELL:`.

Existing seams to reuse instead of reinventing: `IDataProvider`, `IDataNormalizer`,
`IHistoryRepository`, `IUserStateService`, `IBaselineService`, `ITrendService`, `IRuleEngine`,
`IRecommendationService`, `IDailyPlanService`, `IIntelligenceProvider`, `IIntelligenceService`,
`IWeeklySummaryService`, `IPrivacyService`, `IPermissionService`, `IFormatService`
(`LongDate`, `ShortDate`, `Time`, `Duration`, `DurationFromMinutes`, `Number`, `Percent`),
`SessionState.CurrentProfile`, `IDateTimeProvider`, `JsonFileStore` (ctor takes an optional
directory override — use that in tests), `DemoDataSeeder.CreateBootcampCatalog(l)`,
`ItemsStackLayout`, `ColorByKey`/`IsNotEmpty`/`NotConverter`, `TappableFeedback`.

## 2. Tokens (lane 05 owns these files; everyone else reads)

Color keys: `Accent`, `AccentSoft`, `AccentDeep`, `BgPrimary`, `Surface`, `SurfaceAlt`, `Overlay`,
`TextPrimary`, `TextSecondary`, `TextTertiary`, `TextOnAccent`, `MetricSleep|MetricActivity|MetricRecovery|MetricWellness`
(+ `*Soft`), `Positive`, `Caution`, `Negative` (+ `*Dark` mirrors).
Brush keys: `BgPrimaryBrush`, `SurfaceBrush`, `SurfaceAltBrush`, `OverlayBrush`, `AccentSoftBrush`,
`Metric*SoftBrush`, `AccentBrush`, `Metric*Brush`, `PositiveBrush`, `CautionBrush`, `NegativeBrush`,
`TextPrimaryBrush`, `TextSecondaryBrush`, `TextTertiaryBrush`.
Styles: `LDisplay`, `LHeading`, `LSubheading`, `LBody`, `LBodySecondary`, `LCaption`, `LTiny`,
`LQuote`, `LMetric`, `LMetricLarge`, `LButton`, `ChipLabel`, `IconGlyph`, `FieldLabel`,
`InputField`, `Card`, `SoftCard`, `AccentCard`, `ElevatedCard`, `MetricCard`, `PrimaryButton`,
`SecondaryButton`, `GhostButton`, `IconButton`, `ProgressTrack`, `SegmentText`, `Skeleton`,
plus `SwitchStyle`, `SliderStyle`, `PickerStyle`, `EditorStyle`, `DatePickerStyle`, `TimePickerStyle`.
Geometry: `CardRadius`/`CardCornerRadius` 20, `ChipRadius`/`ChipCornerRadius` 14, `PagePadding` 20,
`CardSpacing` 14, `CardPadding` 18, `PageThickness` 20,12.
Anything missing from that list is lane 05's job — if you need a token that isn't there yet,
define it **inline** in your own XAML and note it in `NOTES:` (do not edit the dictionaries).
Fonts are applied by `BaseContentPage` from `ObservableObject.AppFont`; never hardcode `FontFamily`
except `views:BaseContentPage.FontFamilyOverride="True"` (brand wordmark).

## 3. Lanes

### Lane 01 — In-app update experience (the client-visible flagship)
Own: `Infrastructure/Updates/**`, `Presentation/ViewModels/Updates/**`,
`Presentation/Views/Updates/**`.
- `Infrastructure/Updates/GitHubReleaseFeed.cs`: fetch
  `https://api.nuget.org`-free, plain `https://api.github.com/repos/Opselon/LIVORA/releases?per_page=10`
  with `HttpClient` (named/`static` client, `User-Agent: LIVORA-app`, 8s timeout, no token, no keys),
  parse with `System.Text.Json` into `ReleaseEntry` (tag_name, name, body, html_url, prerelease,
  published_at, `assets[].browser_download_url`), and map to `UpdateInfo` using `AppVersion.Compare`.
  Never invent a version: on any failure return the status (`NoConnection`/`RateLimited`/`Error`) with
  `LatestVersion = null`.
- `Infrastructure/Updates/UpdateService.cs : IUpdateService`: 6-hour re-check throttle, cached answer
  in `update_feed.json` (own writer under `FileSystem.AppDataDirectory/LIVORA`, `FromCache = true`
  when served from it), `PeekCachedAsync`, and `IsFeedConfigured`. Persist `last_successful_check_utc`.
- `Presentation/Views/Updates/UpdatePage.xaml` + `UpdateViewModel`: current vs latest card, release
  notes list, platform-correct "Download update" (`android|ios|windows|macos`, falling back to
  `all` → `ReleaseUrl`) opened with `Browser.OpenAsync`, honest status chip per
  `UpdateCheckStatus`, manual "Check again" (force), "installed from source" note when
  `NewerThanFeed`, and a first-run-after-upgrade "What's new" entry point that records
  `ISettingsService.LastSeenVersion`.
- `Presentation/ViewModels/Updates/UpdateBannerViewModel.cs`: small reusable banner VM (idle /
  checking / update available / up to date / couldn't check) that lane 06's Profile page hosts.
- `APPEND:` DI marker: `IUpdateService`→`UpdateService` singleton, `GitHubReleaseFeed` singleton,
  `UpdateViewModel` transient, `UpdateBannerViewModel` transient. Shell marker:
  `Routing.RegisterRoute("updates", typeof(LIVORA.Presentation.Views.UpdatePage));`

### Lane 02 — Manual entry pipeline (persistence + data layer, no UI)
Own: `Infrastructure/Persistence/ManualEntryStore.cs`, `Application/HealthData/ManualMerge.cs`,
`Application/HealthData/ManualOverlayProvider.cs`, `Domain/Models/Health/ManualEntryRecord.cs`.
- `ManualEntryStore : IManualEntryService` — JSON file `livora_manual_entries.json` through
  `JsonFileStore` (inject it; ctor accepts an optional directory override so tests can isolate),
  upsert-by-date, range query, `CountEntriesAsync`, corrupt-file-safe, records
  `SavedAtUtc` + `DataOrigin.Manual`.
- `Domain/Models/Health/ManualEntryRecord.cs`: the persisted shape (date + nullable values + note +
  saved-at). Pure data, no formatting.
- `ManualMerge.Overlay(Domain.Models.Health.NormalizedDay day, ManualEntryRecord? record)` — pure,
  static, no IO: manual values win per field, `Origin = Manual`, `Quality = Complete`,
  `Confidence = 1.0`; untouched fields keep the provider's provenance.
- `ManualOverlayProvider : IDataProvider` — composes `SampleHealthProvider` + `IManualEntryService`
  (both injected) and applies `ManualMerge`; advertises only capabilities it can really honor, and
  its `Origin` reports `Manual` when an override applied for that day, else the underlying origin.
- Everything must stay MAUI-free so the test project compiles it: no `FileSystem`, no
  `Microsoft.Maui.*` in the two `Application/` files (`JsonFileStore`'s MAUI dependency lives in the
  default ctor path only — pass the store in).
- `APPEND:` DI marker: `IManualEntryService`→`ManualEntryStore` singleton; replace the existing
  `IDataProvider` registration so it resolves to `ManualOverlayProvider` (give the exact lines).
  Tell lane 10 which test files to write (round-trip in a temp dir, precedence, origin honesty,
  `ManualMerge` bounds).

### Lane 03 — Daily check-in UI: Log tab + real chart
Own: `Presentation/Views/Log/**`, `Presentation/ViewModels/Log/**`,
`Presentation/ViewModels/Log/LogEntryViewModel.cs`, `Application/HealthData/LogEntryRules.cs`.
- `LogPage` (tab content) + `LogViewModel`: date selector (Today/Yesterday chips + `DatePicker`),
  inputs for sleep hours + minutes, steps, active minutes, and 0..1 sliders for sleep quality,
  mood, energy, stress; save through `IManualEntryService`; inline localized confirmation;
  "self-reported, not measured" honesty note; recent-entries list with tap-to-edit and delete;
  proper empty state; refresh when the language changes.
- `LogEntryPage` (pushed, route `log-entry`) — same editor focused on one day, reachable from
  Health/Today, so both flows share one implementation (extract the editor into a `ContentView`
  under `Presentation/Views/Log/`).
- `Application/HealthData/LogEntryRules.cs` — pure validation + defaults + clamping (≤24h sleep,
  0..100000 steps, 0..1440 active minutes, sliders within 0..1, past dates only within 60 days,
  "no values entered" detection). MAUI-free, so it is testable: tell lane 10 the file name.
- `SleepTrendChart` — a real 14-day chart using
  `SkiaSharp.Views.Maui.Controls.SKCanvasView` (package already referenced): bars/line of sleep
  minutes, dashed personal-baseline line from `IBaselineService`, per-day marker styled by origin
  (Manual = hollow/hatched, Mock = soft fill), axis labels drawn with the active language's font
  (`SKTypeface.FromStream` over the embedded `Vazirmatn-Regular.ttf` / `OpenSans-Regular.ttf`) and
  Persian digits when RTL, RTL-reversed x-axis, dark-theme aware colors. Wrap the paint in
  try/catch and render a token-styled placeholder on failure — never throw from the paint handler.
- `APPEND:` DI + shell marker lines (`LogPage`, `LogViewModel`, `LogEntryPage`, `LogEntryViewModel`,
  route `log-entry`).

### Lane 04 — Shell, tab order, responsive desktop layouts
Own: `App.xaml`, `App.xaml.cs`, `AppShell.xaml`, `AppShell.xaml.cs`, `Presentation/Responsive/**`.
- Add the 6th tab **Log** between Health and Goals (`Resources/Icons/tab_log.svg`,
  `ContentTemplate` → `views:LogPage`, `Route="Log"`, title key `Tab.Log`) and keep every tab title
  re-resolved on language change.
- `Presentation/Responsive/Breakpoint.cs` + `AdaptiveLayout.cs`: attached properties
  `AdaptiveLayout.Columns="1,2,3"` (per breakpoint) and `AdaptiveLayout.MaxContentWidth="980"`,
  driven by `Page.SizeChanged` (no timers, no polling, no thrash — recompute only on real width
  bucket changes). Breakpoints: Narrow `<700`, Medium `<1000`, Wide `>=700…1000/<1000` — pick and
  document exact effective-px values; expose `Breakpoint` as a bindable/attached value pages can key
  `IsVisible`/`Grid.ColumnSpan` off.
- In `BaseContentPage`… it is **not yours** (lane 06 edits it). Instead ship `AdaptiveLayout` as a
  pure helper + a `ResponsiveGrid` layout class other pages can use, and hand the orchestrator an
  `APPEND:` snippet for the one-line hook into `BaseContentPage` (it will land after lane 06's
  changes; write it as a small addition to the ctor, not a rewrite).
- `App.xaml.cs`: inside the existing Wave 3 marker region add Windows window sizing
  (`#if WINDOWS`, min ~1100×800, no fixed size), `RequestedThemeChanged` → notify `IThemeService`
  (null-safe, `ServiceHelper.TryGet`), and keep every existing behavior (onboarding-vs-shell window
  creation, `ApplyFlowDirection`, the new global error safety net) intact.
- `AppShell.xaml.cs`: `// WAVE3-SHELL:` region — leave it to lanes (the orchestrator merges their
  `Routing.RegisterRoute` lines); do not register routes for pages you don't own.
- Verify with a wide-window reasoning pass: at 1200px the Today/Health pages must not stretch text
  to full width. Report the exact hook you need in `BaseContentPage` as `APPEND:`.

### Lane 05 — Design system v2, theme service, icons, wordmark
Own: `Resources/Styles/LivoraColors.xaml`, `Resources/Styles/LivoraStyles.xaml`,
`Infrastructure/Settings/ThemeService.cs`, `Presentation/Components/SegmentedControlView.xaml(.cs)`,
`Presentation/Components/StatChip.xaml(.cs)`, `Presentation/Components/ProgressRing.cs`,
`Presentation/Components/PressableBorder.cs`, `Resources/Icons/**` (add),
`Resources/Splash/splash.svg`, `Resources/AppIcon/appiconfg.svg`,
`Resources/AppIcon/appicon.svg`.
- `ThemeService : IThemeService` (persists `ThemeMode` via `ISettingsService`, resolves System
  against `Application.Current.RequestedTheme`, `Apply()` sets `UserAppTheme`, re-raises on OS
  change). Declare its DI line as `APPEND:`.
- Styles: add every §2 token still missing, correct light/dark bindings, focus/pressed states via
  `VisualStateManager` where it works cross-platform, and a `Skeleton` style. Keep existing keys
  and their meaning stable (lanes 01-09 build against them); refine sizes/line-heights so
  Vazirmatn and OpenSans both look intentional (cap-height differences are real).
- Audit the two dictionaries for brush-on-color misuse and fix it; add
  `TextPrimaryBrush`/`TextSecondaryBrush`/`TextTertiaryBrush`.
- `ProgressRing` (pure MAUI `Arc`/`Circle` shapes, no Skia) + `StatChip` + `SegmentedControlView`
  (used by lanes 01/03/06/09 for theme + language + filters; RTL-correct) + `PressableBorder`
  (tap + press feedback, `Command`+`CommandParameter`; complement `TappableFeedback`, don't fight it).
- Icons: draw missing glyphs as SVG (calm, 1.75 stroke, brand `#2C5D53`): bell, download, refresh,
  close, check-circle, alert, sparkle, chart, moon, sun, system, chevron-right/left, plus, pencil,
  trash, calendar, clock, footprint, heart, book. They become `MauiImage` automatically (the csproj
  globs `Resources/Icons/*`). Keep the 5 existing tab icons' weight; make sure `tab_log.svg`
  (lane 04's tab) exists — it is in the repo already, verify and improve it if thin.
- Refresh splash + appicon foreground for a premium first-launch impression.

### Lane 06 — Profile + Settings pages, privacy, connection center, page base
Own: `Presentation/Views/Profile/**`, `Presentation/ViewModels/Profile/**`,
`Presentation/Views/Settings/**`, `Presentation/ViewModels/Settings/**`,
`Presentation/Views/BaseContentPage.cs`.
- `BaseContentPage`: keep the font walk + FlowDirection sync, and add the one-line
  `AdaptiveLayout` hook lane 04 requests (apply `MaxContentWidth` centering on wide windows) plus
  a safe `SetLoading(bool)`-style helper VMs can use. Do not remove behavior.
- Rework `ProfilePage` into a real account hub: initials avatar, name edit, primary-goal chips,
  activity level, schedule (bedtime/wake), language segmented control (live, persisted), theme mode,
  reminders entry, settings entry, updates card (lane 01's `UpdateBannerViewModel` + route
  `updates`), connection center (mock active / not connected — keep the honesty),
  data & privacy inventory extended with the Wave 3 stores (`livora_manual_entries.json`,
  `update_feed.json`, `livora_reminders.json`, `livora_snoozed.json`) labeled by real origin
  (Manual/Mock) and location (Device), and **Delete all local data** wired so the new stores are
  cleared too (via `IPrivacyService` + `APPEND:` for the extra deletions if the abstraction can't
  reach them).
- New `SettingsPage` + `SettingsViewModel`: notification grant state with a real
  `IReminderService.RequestGrantAsync` button that reports exactly what the OS said
  (`AppInfo.ShowSettingsUI()` when denied), theme mode, language, "check for updates",
  reminders shortcut, sample-data disclosure, about/version (`AppInfo.Current.VersionString`)
  and a "What's new" replay.
- Every row ≥44dp touch target, `SemanticProperties.Description` on interactive elements, no
  hardcoded strings, both languages must look deliberate.
- `APPEND:` DI + shell markers for `SettingsPage`/`SettingsViewModel` (route `settings`).

### Lane 07 — Goals & habits: real editors
Own: `Presentation/Views/Goals/**`, `Presentation/ViewModels/Goals/**`,
`Presentation/Views/Editor/**`, `Presentation/ViewModels/Editor/**`,
`Application/Planning/GoalEditorRules.cs`.
- `Application/Planning/GoalEditorRules.cs` (MAUI-free, testable): name validation (1..80,
  trimmed, duplicate-name warning), numeric target validation, archive-vs-delete decision,
  defaults suggested from `UserProfile.FocusAreas`, and a "is this goal metric-measured" helper.
- `GoalsPage`: replace the fake "New goal 1 / +1 / Delete" flow with navigation to the editors
  (`Shell.GoToAsync("goal-editor?mode=new")` style — the routes come from `APPEND:`), a
  Goals/Habits segmented filter, per-habit today toggle, streak chips, weekly progress, real empty
  states, and archive with undo (uses `GoalEditorRules`, no re-seeding of persisted data).
- `GoalEditorPage` + VM: name, description, category, target value + `GoalUnit` + `GoalPeriod`,
  optional deadline, `GoalMeasurement` + metric key, save, archive; edit mode loads by id from
  `IRepository<Goal>`; validation messages localized.
- `HabitEditorPage` + VM: name, `HabitFrequencyKind`, times-per-week, and a reminder time that
  upserts a `ReminderSetting` (`Kind = "habit"`, `TargetId = habit.Id`) through `IReminderService`.
- Both editors must be reachable by ctor (DI) *and* by route (query args) so lane 04's shell works.
- `APPEND:` DI + shell markers for the 4 types (routes `goal-editor`, `habit-editor`).

### Lane 08 — Programs/Bootcamps, discovery, week progress
Own: `Presentation/Views/Programs/**`, `Presentation/ViewModels/Programs/**`,
`Presentation/Views/Bootcamps/**`, `Presentation/ViewModels/Bootcamps/**`,
`Application/Planning/BootcampProgress.cs`, `Application/Discovery/**`.
- `Application/Planning/BootcampProgress.cs` (pure, MAUI-free): completed/today/upcoming day math,
  in-program streak, days remaining, projected finish date, adapted-day count, completion fraction
  from `Days[].IsCompleted` (not just `CurrentDay`), next milestone. Tell lane 10 to cover it.
- `Application/Discovery/ProgramDiscovery.cs` (pure): recommend programs from
  `UserProfile.FocusAreas` + current state (deterministic, explainable — return keys, never prose).
- `Application/Discovery/WeekProgress.cs` (pure): weekly habit/goal/bootcamp bars from
  `IHistoryRepository` + habits + goals, with `InsufficientData` honesty below 3 days.
- `BootcampDetailPage` + VM (route `bootcamp-detail`): header (title/desc from keys, category,
  difficulty, duration, creator `LIVORA`), progress, a day calendar with completed/today/adapted/
  upcoming states, today's plan card with the adaptation explanation (`ProgramAdapter`,
  `Bootcamp.AdaptationRuleKeys`), enroll / leave / mark-day-done actions, and an honest
  "sample program" label.
- `ProgramsPage`: category filter chips + search over localized titles, enrolled-first ordering,
  per-card progress, "adapted today" badge, empty state, and a "recommended for you" rail from
  `ProgramDiscovery`.
- `WeekProgressView` (`ContentView`, used by the Review page — hand the orchestrator an `APPEND:`
  snippet for `WeeklySummaryPage.xaml` instead of editing it; lane 09 owns that page's VM only, and
  the page file belongs to no lane, so `APPEND:` is required).
- `APPEND:` DI + shell markers.

### Lane 09 — Reminders, Today page, Weekly review, shared state
Own: `Application/Reminders/**`, `Infrastructure/Notifications/**`,
`Presentation/Views/Today/**`, `Presentation/ViewModels/Today/**`,
`Presentation/Views/Review/**`, `Presentation/ViewModels/Review/**`,
`Application/Insights/SnoozeStore.cs` (pure contract) + `Infrastructure/Notifications/SnoozeStore.cs`,
`Presentation/Views/Reminders/**`, `Presentation/ViewModels/Reminders/**`,
`Presentation/Components/EmptyStateView.xaml(.cs)`, `Presentation/Components/SearchBarView.xaml(.cs)`,
`Presentation/Components/FilterChipsView.xaml(.cs)`.
- `Application/Reminders/ReminderEngine.cs` (pure, testable): from state/goals/habits/bootcamps +
  `ReminderSetting` list + already-fired map → what to notify, with per-day dedupe; mirrors
  `RuleEngine.HabitAtRisk` semantics (streak ≥3, nothing logged after 18:00), wind-down when sleep
  debt is High, "haven't logged today" after 21:00 (uses `IManualEntryService` counts), bootcamp day
  pending. Output = keys + args only.
- `Infrastructure/Notifications/LocalReminderService.cs : IReminderService` on
  `Plugin.LocalNotification` (14.1.2): settings + fired-map in `livora_reminders.json`,
  `SyncAsync` schedules enabled reminders (`Schedule.NotifyTime`, repeating per `DaysMask`),
  `GetGrantStateAsync`/`RequestGrantAsync` map the real platform answer to `NotificationGrantState`
  (never report Allowed unless the API said so), `ClearAllAsync` cancels, `AddReceivedCallback`
  wiring so tapping a reminder opens Today. Declare `builder.UseLocalNotification();` as `APPEND:`
  for `MauiProgram.cs`, and list every Android/Windows manifest or permission requirement you could
  NOT verify on a device in `NOTES:` — do not claim device verification.
- `RemindersPage` + VM (route `reminders`: list of built-in reminder kinds with switch, `TimePicker`,
  weekday chips, grant banner with the real state, "Grant"/"Open system settings", and a
  "send test reminder (+1 min)" that says exactly what happened.
- Today page: `RefreshView` pull-to-refresh, "Log today" nudge when no manual entry exists (route
  `log-entry`), a quick-actions row (check-in, reminders, updates, weekly review) that must
  navigate via `Shell.GoToAsync` routes declared by other lanes (code defensively: catch route
  failures and show the honest "not available yet" state), tappable recommendation cards that
  expand why/benefit/confidence (`ExplainKey`, `ExpectedBenefitKey`) with snooze persisted in
  `livora_snoozed.json`, skeleton/loading polish, and the existing honesty note.
- Weekly review: finish issue #1 properly — `WeeklySummaryPage` reachable from Today **and**
  Profile, week navigation (this week / last week), `WeekProgress`-style bars rendered from the
  VM (lane 08's `APPEND:` provides the view; you host it), empty/insufficient-data state.
- `EmptyStateView` / `SearchBarView` / `FilterChipsView` `ContentView`s with `BindableProperty`
  APIs exactly as §2 names them (lanes 07/08 code against those names in parallel; if a type is
  missing in your copy, `// ORPHAN:` it and keep your build green).
- `APPEND:` DI + shell markers for everything above.

### Lane 10 — Tests + documentation (and the merge gate helper)
Own: `Tests/**`, `README.md`, `docs/**` (except `docs/LANES.md`).
- The suite is 117 green — never break it. Add ≥45 tests covering:
  `AppVersion` parse/compare (all edge cases in the contract comment), localization lookup +
  `[missing]` + Persian digits/percent/duration + `CultureBootstrap` fallback, `DataNormalizer`
  sanity/staleness, `BaselineService` incl. wrap-around bedtime + confidence gates, `TrendService`
  insufficient data, every `RuleEngine` rule boundary, `RecommendationService` cap + dedupe,
  `DailyPlanService` adaptation grammar, `ProgramAdapter`, `WeeklySummaryService` null-below-3,
  `Goal`/`Habit` math (Saturday week start, streaks), `DemoDataSeeder` idempotency, honesty suite
  (every mock-labeled key exists in both resx; `NormalizedDay.Completeness`; `MetricState.Level`
  dead band; no prose in Application outputs — assert keys), resx integrity (EN/FA key sets equal,
  no empty values, FA contains Persian codepoints — locate the files from `AppContext.BaseDirectory`
  and skip cleanly when absent), DI smoke test that every Wave 3 contract is resolvable **only**
  if it can run without MAUI (otherwise skip and say why).
- Add the tests other lanes ask for in their `NOTES:`/report (they will name pure files under
  `Application/`): that is where the lane-specific coverage lands, because only you may edit
  `Tests/**`.
- You may add `<Compile Include>` lines to `Tests/LIVORA.Tests.csproj` (you own it) for pure files
  outside the current globs, e.g. `Infrastructure/Notifications/*` pure helpers — but never include
  anything that needs a MAUI head.
- Rewrite `README.md` for Wave 3 (data flow incl. manual overlay, layers, 6 tabs, update feed,
  notification honesty, charts, desktop breakpoints, build/test commands, status table with
  real/mock/not-implemented) and write `docs/WAVE3.md` (decisions + honesty rationale). Anything you
  cannot verify from the code goes under "Unverified".
- Also produce `docs/WAVE3-MERGE.md`: a checklist you derive from the lane protocol (which markers
  exist, which DI lines each lane needs) so the orchestrator's merge can be diffed against it.

## 4. Cross-lane names you MUST use exactly (they are being written in parallel)

- VM/service type names: `UpdateViewModel`, `UpdateBannerViewModel`, `UpdateService`,
  `GitHubReleaseFeed`, `ManualEntryStore`, `ManualOverlayProvider`, `LogViewModel`,
  `LogEntryViewModel`, `LogPage`, `LogEntryPage`, `GoalEditorViewModel`, `HabitEditorViewModel`,
  `GoalEditorPage`, `HabitEditorPage`, `BootcampDetailViewModel`, `BootcampDetailPage`,
  `ProgramsViewModel`, `GoalsViewModel`, `ProfileViewModel`, `SettingsViewModel`, `SettingsPage`,
  `RemindersViewModel`, `RemindersPage`, `TodayViewModel`, `LocalReminderService`, `ThemeService`,
  `ReminderEngine`, `BootcampProgress`, `GoalEditorRules`, `LogEntryRules`, `ProgramDiscovery`,
  `WeekProgress`, `ManualMerge`.
- Page namespaces: `LIVORA.Presentation.Views` (existing convention — every page lives in it).
- Routes: `updates`, `settings`, `reminders`, `log-entry`, `goal-editor`, `habit-editor`,
  `bootcamp-detail`.
- Components: `EmptyStateView`, `SearchBarView`, `FilterChipsView`, `SegmentedControlView`,
  `StatChip`, `ProgressRing`, `PressableBorder`, `AdaptiveLayout`, `ResponsiveGrid` (namespaces
  `LIVORA.Presentation.Components` / `LIVORA.Presentation.Responsive`).
- Localization key prefixes: `Update.*`, `Log.*`, `Editor.*`, `Bootcamp.*` (extend), `Programs.*`,
  `Goals.*`, `Settings.*`, `Reminders.*`, `Profile.*`, `Today.*`, `Health.*`, `Theme.*`,
  `Notification.*`, `Privacy.*`, `Common.*`, `Enum.*`, `Format.*`.

## 5. Report format (all lanes)

From your lane directory:

```bash
git add -A >/dev/null 2>&1
git -c core.quotepath=false diff --cached --binary
```

Then in your final message, in this order:
1. One fenced ```diff block with the complete diff (binary-safe — do not split or summarize it).
2. `KEYS-EN` and `KEYS-FA` blocks: raw `<data name="…" xml:space="preserve"><value>…</value></data>`
   lines, one per key, same order in both, no resx wrapper.
3. `APPEND <target-file> <marker>` blocks with the exact code to insert.
4. Stats: `BUILD: ok|fail|not-run`, `TESTS: ok|fail|not-run`, `FILES: n`, `KEYS: n`.
5. `NOTES:` one line each — orphans, unverified platform claims, tokens you need from lane 05,
   tests you want lane 10 to write, files you expect to conflict.
Prose under 20 lines. Never paste a full file outside the diff.
