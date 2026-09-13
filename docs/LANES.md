# LIVORA — Wave 3 lane protocol (10 parallel implementers)

Repo: `C:\Users\Capsizer\source\repos\LIVORA` (.NET 10 MAUI, root ns `LIVORA`).
Each lane works in its own full copy of the repo under
`C:\Users\Capsizer\AppData\Local\Temp\livora_w3\laneNN\`.

## 0. Shared rules (hard — every lane)

1. **Your lane directory is the only place you may write.** Never touch the real repo or another
   lane. Never run `git` (no commit/branch/push) — the orchestrator merges and commits.
2. **Frozen files — read-only for you** (already updated by the orchestrator, identical in every
   copy): `Application/Abstractions/IWave3Contracts.cs`,
   `Application/Abstractions/ILocalizationService.cs`, `Application/Abstractions/IRepository.cs`,
   `Application/Abstractions/IStateContracts.cs`, `Domain/Enums/DomainEnums.cs`,
   `Domain/Enums/HealthDataEnums.cs`, `Domain/Enums/StateEnums.cs`, `Domain/Enums/PlanningEnums.cs`,
   `Domain/Enums/SecurityEnums.cs`, `LIVORA.csproj`, `Tests/LIVORA.Tests.csproj`,
   `Resources/Localization/AppResources.resx`, `Resources/Localization/AppResources.fa.resx`,
   `Resources/Localization/AppResources.cs`, `Resources/Styles/LivoraColors.xaml`,
   `Resources/Styles/LivoraStyles.xaml`, `Presentation/Components/*`, `Presentation/Theme.cs`,
   `Presentation/ObservableObject.cs`, `Presentation/Views/BaseContentPage.cs`, `global.json`,
   `.github/**`.
   **Exception:** lane 06 owns `Resources/Styles/LivoraColors.xaml`, `Resources/Styles/LivoraStyles.xaml`,
   `Presentation/Theme.cs` and `Presentation/Components/*` — everyone else treats them as frozen.
3. **Never add or edit anything in:** `App.xaml`, `App.xaml.cs`, `AppShell.xaml`, `AppShell.xaml.cs`,
   `MauiProgram.cs`, `Platforms/**`, `Domain/**` (except lane 03/09 owners noted in its section).
   Anything you need registered there goes into your patch as a `NEWFILE:` or `APPEND:` block
   (see §11) — the orchestrator applies it once in the merged tree.
4. **You may create files only inside your owned paths** (listed in your section). If you need a
   file another lane owns, do not create it — code against the contract and declare the need as an
   `// ORPHAN:` comment plus a line in your report.
5. **No new NuGet packages.** The app project already references: `Microsoft.Maui.Controls`,
   `Microsoft.Extensions.Logging.Debug`, `Plugin.LocalNotification`, `SkiaSharp`,
   `SkiaSharp.Views.Maui.Controls`, `SystemSecurityCryptor` — no, correction: only
   `Plugin.LocalNotification`, `SkiaSharp`, `SkiaSharp.Views.Maui.Controls` were added for Wave 3.
6. **Localize every user-facing string.** Use `{localize:Tr Key.Name}` in XAML (markup extension
   `TrExtension` in `LIVORA.Presentation.Components`, resolves at load **and** on live language
   change) or `L("Key")` / `Loc["Key"]` in ViewModels. Keys you invent must be appended to your
   resx blocks (§11), **EN and FA together**, following existing key style
   (`Feature.PascalCase.dot.sub`). No key may collide with the 331 existing ones — grep first.
   Never put prose in Domain/Application: emit keys + args.
7. **Honesty invariants (product law).** Never display a connected/real/AI claim that is not true.
   Mock data stays labeled; manual data is labeled self-reported; the update checker reports
   "couldn't check" instead of "up to date" when offline; notifications report the real grant state.
8. **RTL is free, don't fight it:** use `Start`/`End` (never `Left`/`Right`) for
   `HorizontalOptions`, `Grid.ColumnDefinitions` ordering, and `Margin`/`Padding` shorthand; set
   `FlowDirection="{Binding FlowDirection}"` only on pages that are not `BaseContentPage`
   descendants. Page content must not assume text length: `LineBreakMode="WordWrap"` + `MaxLines`
   on labels, `MinHeightRequest` on rows.
9. **Theme tokens only.** Colors come from `LivoraColors.xaml` resource keys (`Card`, `SoftCard`,
   `AccentCard`, `LDisplay`, `LHeading`, `LSubheading`, `LBody`, `LBodySecondary`, `LCaption`,
   `LMetric`, `LMetricLarge`, `PrimaryButton`, `SecondaryButton`, `ChipLabel`, `IconGlyph`,
   `ProgressTrack`, `SegmentText`; brushes `BgPrimaryBrush`, `SurfaceBrush`, `SurfaceAltBrush`,
   `OverlayBrush`, `AccentSoftBrush`, `Metric*SoftBrush`; and Color-typed `Accent`, `Positive`,
   `Caution`, `Negative`, `TextPrimary`, `TextSecondary`, `TextTertiary`, `Metric*`).
   **Brushes go on brush-typed properties only (`Border.Background`, `Border.Stroke`);
   Color-typed properties (`TextColor`, `BackgroundColor`, `ProgressColor`, `Shell.*Color`) must use
   a Color or an inline `{AppThemeBinding Light=…, Dark=…}`.** Violating that logs
   "Cannot convert SolidColorBrush to type Color" and crashes Android renderers.
10. **Verify before reporting.** Run:
    `dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q`
    (pass `-p:BaseIntermediateOutputPath=objw3\ -p:BaseOutputPath=binw3\` if `obj/` is stale).
    If you touched a file the test project compiles (Domain/Application/Infrastructure non-MAUI),
    also run `dotnet test Tests/LIVORA.Tests.csproj --nologo -v q`. Iterate until both are green.
    Report only what you actually observed. A lane that cannot build must say so and still deliver
    its patch.
11. **Output contract — this is how your work is merged.** Run, from your lane directory:
    ```bash
    git add -A >/dev/null 2>&1; git -c core.quotepath=false diff --cached --binary
    ```
    Put that unified diff (`diff --git` … blocks, binary-safe for fonts/png) in a fenced block in
    your final message, and end with these plain lines:
    `BUILD: ok|fail|not-run` — `TESTS: ok|fail|not-run` — `FILES: <count>` —
    `KEYS: <count added>` — `NOTES: <one line per thing the orchestrator must do or know>`.
    Keep prose under 25 lines. Do not paste file contents outside the diff.

## 1. Lane 01 — In-app updates

Owned paths: `Services/Updates/**` (create), `Presentation/ViewModels/Updates/**` (create),
`Presentation/Views/Updates/**` (create).

Build Wave 3's headline client feature: an honest update experience.

- `Services/Updates/GitHubReleaseFeed.cs` + `UpdateService.cs` implementing `IUpdateService`
  (contract in `IWave3Contracts.cs`). Fetch `https://api.github.com/repos/<owner>/<repo>/releases`
  with `HttpClient` (name the client, set a User-Agent — GitHub 403s without one), timeout 8s,
  no auth, no API keys. Config: owner/repo from constants in **your** file, read `AppInfo.Current`
  for the installed version. Map the newest release: `tag_name` (strip leading `v`), `name`,
  `body` (split into ≤5 note lines, strip markdown noise), `assets[].browser_download_url`,
  `html_url` → `DownloadLinks["all"]`, `prerelease`.
  Compare with `AppVersion.Compare` (pure, already in `IWave3Contracts.cs`).
- Offline/cache: persist the last successful answer via a JSON file
  (`FileSystem.AppDataDirectory/LIVORA/update_feed.json`) using your own tiny store — do **not**
  reuse `JsonFileStore`'s repository registrations. Respect a 6-hour minimum re-check interval
  unless `force: true`. Never report `UpToDate` from cache as if it were fresh (set `FromCache`).
- Statuses: `UpdateAvailable`, `UpToDate`, `NewerThanFeed`, `NoConnection`, `RateLimited`, `Error`,
  `Unknown`, `Disabled` — the UI text comes from `Update.*` keys.
- UI: `UpdatePage` (a pushed page, not a tab) with a big current/latest card, notes, and a
  "Download update" button that opens the store/page via `Browser.OpenAsync`. It must degrade
  gracefully when `DownloadLinks` has no entry for the current platform
  (`DevicePlatform` → key `android|ios|windows|macos`, fall back to `all`).
  Also expose a compact `UpdateBannerViewModel` used by Profile: idle → "Check for updates",
  spinner while checking, "Update available — 1.3" chip, muted "Couldn't check — retry" on failure.
- Windows-only nicety: `#if WINDOWS` detect whether an MSIX is installed (`AppInfo.Package` style
  check) and if so surface `UpdateStatus` through `Microsoft.Maui.ApplicationModel`
  (`Marketplace.Rate`-style API is not needed — keep it simple: if `AppInfo.Current.Package` is
  available use `Windows.ApplicationModel.Store.ReportingServices`? NO — do not use WinRT store
  APIs; just mark that path `Disabled` with a "installed from Microsoft Store" note when
  `AppInfo.Current.Package?.IsBundle ?? false` is not reliable. Prefer: skip MSIX, keep the feed path.)
- DI keys to hand to the orchestrator: `IUpdateService → UpdateService` (singleton),
  `UpdateFeedConfig` (singleton with owner/repo), `UpdatePage` + `UpdateViewModel` (transient).
- Tests: `Tests/Tests/UpdateVersionTests.cs` for `AppVersion.TryParse/Compare` (edge cases listed in
  the contract comment) and for the feed JSON parsing (parse from a string literal, no network).

## 2. Lane 02 — Manual health entry + 14-day chart

Owned paths: `Infrastructure/Persistence/ManualEntryStore.cs` (create),
`Presentation/ViewModels/Log/**`, `Presentation/Views/Log/**`, `Services/Charts/**` (create).

Give the client a reason to open the app every day: log sleep/steps/active minutes/mood/energy/
stress for today or a past day, and see a real chart.

- `ManualEntryStore` implements `IManualEntryService` (§IWave3Contracts). Storage: JSON via the
  existing `JsonFileStore` pattern — write your own `LoadObjectAsync`-style calls, file name
  `livora_manual_entries.json` (declare the constant in your file, not in `AppConstants`).
  Persist `UpdatedAt` and mark entries `DataOrigin.Manual`.
- Apply manual values to the pipeline by wrapping the provider: create
  `ManualOverlayProvider : IDataProvider` in `Application/HealthData/ManualOverlayProvider.cs`
  (you own that file) that composes `SampleHealthProvider` + `IManualEntryService` and overrides
  any field the user logged, setting `Origin = Manual`, `Quality = Complete`,
  `Confidence = 1.0` (self-reported, not device-grade — say so in the label, not by lying).
  Keep `SampleHealthProvider` untouched; the overlay is wired in `MauiProgram.cs` by the
  orchestrator (declare the exact registration lines as `APPEND:` in your patch).
- UI: `LogEntryPage` (pushed) with numeric inputs (sleep hours+minutes, steps, active minutes) and
  0..1 sliders/steppers for mood, energy, stress, sleep quality; date picker defaulting to today
  with a "yesterday" quick chip; validation (no negative, sane caps — 24h sleep, 100k steps);
  save → toast-free inline confirmation, and honest note: "self-reported, not measured".
  Add `SleepTrendChart` in `Services/Charts/`: a `SkiaSharp.Views.Maui.Controls.SKCanvasView`
  drawing a 14-day line/bar of sleep minutes with the personal baseline as a dashed line and
  per-day markers colored by origin (Manual = hollow/dotted). Must render correctly in RTL
  (x-axis reversed) and in dark theme. Also draw axis labels via `SKPaint` with the active
  language's font (`SKTypeface` from the embedded TTFs: `Vazirmatn-Regular.ttf` / `OpenSans-Regular.ttf`)
  and Persian digits when `ILocalizationService.IsRightToLeft`.
  If SkiaSharp proves awkward, keep `SKCanvasView` but simplify — do NOT fall back to hand-built
  BoxViews.
- Keep `HealthPage`/`HealthViewModel` untouched (lane 06's copy of them is being edited by lane 07
  — you must not touch them at all). Your entry point is reachable from the Log tab (lane 07 wires
  the tab; you only expose `LogEntryPage`).
- Tests: `Tests/Tests/ManualEntryTests.cs` — store round-trip through a temp dir, overlay
  precedence (manual beats mock), origin/quality honesty, validation bounds.

## 3. Lane 03 — Real goal/habit editor (CRUD)

Owned paths: `Presentation/ViewModels/Editor/**` (create), `Presentation/Views/Editor/**` (create),
`Domain/Models/Goals/GoalEditorDto.cs` (create if needed).

Today's "New goal" button invents "New goal 1" and "+1" bumps a counter. Replace that with a real
editor while leaving the existing pages alone.

- `GoalEditorPage` + `GoalEditorViewModel`: name (validated, ≤80 chars), description,
  category picker (all `GoalCategory` values, localized via `Enum.GoalCategory.*`),
  target value + unit (`GoalUnit`), period (`GoalPeriod`), optional deadline (date picker),
  measurement mode (`GoalMeasurement`), metric key for metric-measured goals.
  Edit mode loads by `Goal.Id` from `IRepository<Goal>`; create mode leaves progress 0.
  Delete = `IsArchived = true` (keep data) with an "archive" confirmation; hard delete only for
  goals the user created (declare this rule in code comments).
- `HabitEditorPage` + `HabitEditorViewModel`: name, `HabitFrequencyKind`, times-per-week when
  applicable, optional reminder time hook (`ReminderSetting` id — just persist `TimeSpan` fields
  `ReminderTime` … NO: do not edit `Habit` (Domain is frozen); instead pass the reminder through
  `ReminderSetting.TargetId` and store it via `IReminderService` — declare the DI keys you need).
  Habit list page (lane 07 builds it) will call `Shell.GoToAsync` with a parameter dictionary —
  so read your navigation args in the standard way
  (`QueryProperty` attributes or `Shell.Current.Navigation.PushAsync(new GoalEditorPage(...))`
  constructor args: prefer **constructor + DI scope resolve**, matching `BaseContentPage`'s
  "resolve VM from DI" pattern; keep both paths possible by exposing a public ctor taking an id).
- Keep the pure progress/status logic in `Domain` (already there: `Goal.Fraction`, `Status`,
  `Habit.CurrentStreak`). Do not re-implement it.
- Localization: `Editor.*` keys (incl. validation messages `Editor.Error.*`).
- Tests: `Tests/Tests/GoalEditorRulesTests.cs` for any pure rule you add (e.g. name validation,
  archive-vs-delete decision function) — put such rules in `Application/` as a small
  `GoalEditorRules` static class in `Application/Planning/GoalEditorRules.cs` (you own it).

## 4. Lane 04 — Bootcamp detail + adaptive day view

Owned paths: `Presentation/ViewModels/Programs/BootcampDetailViewModel.cs` (create),
`Presentation/Views/Programs/BootcampDetailPage.xaml(.cs)` (create),
`Application/Planning/BootcampProgress.cs` (create, pure).

Make Programs useful instead of a 3-button list.

- `BootcampProgress` (pure, Application layer): day-by-day completion math over `Bootcamp.Days`,
  streak within a program, "days remaining", projected finish date, and an honest
  `AdaptedDaysCount`. Cover it with `Tests/Tests/BootcampProgressTests.cs`.
- `BootcampDetailPage`: header card (title/desc from keys, category, difficulty, duration,
  creator), progress ring or bar with day math, a 3-column day calendar (`ItemsStackLayout` +
  `Grid` inside the template) showing completed / today / upcoming / adapted states, the current
  day's plan with the adaptation explanation from `ProgramAdapter` +
  `Today.PlanAdapted`-style keys, actions: enroll / leave / mark today done / jump to
  `LogEntryPage` if the day needs a logged value.
- Do not modify `ProgramsPage.xaml`/`ProgramsViewModel.cs` (lane 07 owns them); expose a public
  ctor taking a `Bootcamp` id, and add the DI keys to your patch as `APPEND:`.
- Reuse: `Card`/`SoftCard`/`AccentCard`, `StatusBadge`-equivalent Borders (see lane 06's
  `ChipLabel`), `L*` text styles. All copy through `Update`-free `Programs.*`/`Bootcamp.*` keys —
  new ones go in your resx block.

## 5. Lane 05 — Design system v2 + shell polish

Owned paths: `Resources/Styles/LivoraColors.xaml`, `Resources/Styles/LivoraStyles.xaml`,
`Presentation/Theme.cs`, `Presentation/Components/**`, `App.xaml`, `App.xaml.cs`,
`AppShell.xaml`, `AppShell.xaml.cs`, `MauiProgram.cs` (**only** the two
`// WAVE3-LANE05:` marker regions you were given), `Resources/Icons/**`,
`Resources/Splash/**`, `Resources/AppIcon/**`.

You are the visual system owner. Every other lane reuses your tokens, so finish first-ish and keep
names exactly as in §0.9 (they are already published — extend, don't rename).

- `TrExtension` already exists (frozen, do not touch) — verify it works in a page you own.
- Add tokens/styles: `ChipLabel`, `IconGlyph`, `SegmentText` (already added — refine spacing/size),
  new `Card` variants (`ElevatedCard`, `MetricCard`), `FieldLabel`, `InputField` (Entry +
  Editor styles with focus states), `SwitchStyle`, `SliderStyle`, `PickerStyle`, `TabBar` metrics,
  `LQuote` (insight body), `LTiny` (11px caption), focus/pressed visual states via
  `VisualStateManager` setters where supported, and a `Skeleton` style for loading states.
- Dark theme: audit every token for AA contrast in both modes (state the ratio you targeted,
  ≥4.5:1 for body text); add any missing `*Dark` mirrors. Fix the two hardcoded
  `SolidColorBrush x:Key="TextPrimaryBrush"`/`TextSecondaryBrush` if the palette needs them.
- Shell: `AppShell` grows to 6 tabs (`tab_log.svg` exists) — set
  `Shell.TabBarIsVisible` behavior, add a `FlyoutItem`-free design (keep tabs), set
  `Shell.NavBarIsVisible=False` (already), and add `Shell.TitleColor`/indicator tokens so the
  tab bar looks premium in RTL too. Keep the existing 5 routes and add `log` + `updates` routes
  registered by other lanes (do not reference their page types — the orchestrator wires routes;
  you may add `Routing.RegisterRoute` calls inside the `// WAVE3-LANE05:` marker region using
  string-based registration? NO: use `Routing.RegisterRoute("bootcamp-detail",
  typeof(Presentation.Views.BootcampDetailPage))` etc. inside the marker region ONLY if the type
  exists in your copy — it does not. So: leave route registration to the orchestrator and instead
  provide `Core/Navigation/INavigator` (create `Services/Navigation/ShellNavigator.cs`, you own
  it): `Task GoToAsync(string route, IReadOnlyDictionary<string,object>? args)`,
  `Task PushAsync(Page page)`, `Task PopAsync()`; VMs get it injected so lanes never call Shell
  directly.
- Icons: draw 3 more premium line SVGs if you find any lane needing them, and refresh
  `Resources/Splash/splash.svg` + `Resources/AppIcon/appiconfg.svg` to match the wordmark style
  (calm/premium; the accent `#2C5D53` is the brand color). Do not break `MauiIcon`/`MauiSplashScreen`.
- App shell polish in `App.xaml.cs`: register
  `Application.Current.RequestedThemeChanged` → `IThemeService`-independent re-flow (keep it
  null-safe), set default `Window` size on Windows (`window.Width = 1180; window.Height = 820;`
  guarded by `#if WINDOWS`) for a desktop-first feel, and `Microsoft.Maui.Controls`
  `PlatformDefaults`-style tweaks you can justify. **Keep everything the other lanes need**:
  `ApplyFlowDirection()`, onboarding-vs-shell window creation, `ServiceHelper.Initialize`.
- Report a short list of tokens/keys lanes can now use, as a `NOTES:` line.

## 6. Lane 06 — Responsive desktop layouts

Owned paths: `Presentation/Responsive/**` (create), `Presentation/Views/BaseContentPage.cs`
(you are the co-owner: append, do not remove behavior), `App.xaml.cs` (only a
`// WAVE3-LANE06:` marker region — see below), plus page-level edits to
`Presentation/Views/Today/TodayPage.xaml`, `Presentation/Views/Health/HealthPage.xaml` ONLY.

Desktop must not be a stretched phone.

- Create `Presentation/Responsive/AdaptiveLayout.cs`: a reusable MAUI control/helper exposing
  `Breakpoint` (`Narrow` < 700 effective px, `Medium` < 1000, `Wide` ≥ 1000) from
  `Page.Width` changes (use `SizeChanged` + weak events; no polling) and a
  `Grid`-column-count/`ItemsLayout` provider, plus a XAML-friendly attached property
  `Adaptive.ColumnSpan` / `Adaptive.Columns` so pages can declare layouts without code-behind.
  `BaseContentPage`: add `AdaptiveLayout.Attach(this)` wiring without breaking font/flow logic.
  (You own `BaseContentPage.cs`; `ObservableObject.cs` stays frozen — add your own partial
  `ObservableObject` extension if needed.)
- Today page: at `Wide`, render 2–3 columns (intelligence card + plan | metrics + habits |
  goals + recommendations) inside a `CollectionView`-free layout (use `Grid` + `IsVisible`
  per breakpoint or `ItemsStackLayout` variants) with the same bound properties — do NOT fork
  view models. At `Narrow`, the existing stack must look exactly as good.
- Health page: same treatment (chart column + metric column at Wide).
- Window chrome on Windows: use the `// WAVE3-LANE06:` marker region in `App.xaml.cs` to set
  `TitleBar`-adjacent niceties only if they don't need WinUI interop beyond what
  `Microsoft.Maui.Controls` exposes (`Window.Title`, `MinimumWidth`). Keep it trivial.
- `AppShell.xaml`: do not touch (lane 05 owns it). Instead, make sure each page's own content
  handles wide widths (max content width ~ 980 effective px, centered, generous spacing) so a
  4K window doesn't smear text.
- Verify with a manual resize test if you can run the app (`dotnet build` + launch the exe
  headlessly is not required); if not, say so in NOTES and rely on layout math.

## 7. Lane 07 — Client workflows + new Log tab + pages

Owned paths: `Presentation/Views/Log/**`, `Presentation/ViewModels/Log/**` (create),
`Presentation/Views/Goals/**`, `Presentation/Views/Programs/**`,
`Presentation/ViewModels/Goals/**`, `Presentation/ViewModels/Programs/**`,
`Presentation/Views/Profile/**`, `Presentation/ViewModels/Profile/**`,
`Presentation/Views/Health/**`, `Presentation/ViewModels/Health/**`,
`Presentation/Views/Today/**`, `Presentation/ViewModels/Today/**`,
`Presentation/Views/Onboarding/**`, `Presentation/ViewModels/Onboarding/**`,
`Presentation/Views/Review/**`, `Presentation/ViewModels/Review/**`, `AppShell.xaml`,
`AppShell.xaml.cs`, `MauiProgram.cs` (**only** the `// WAVE3-LANE07:` marker region).

Wire the wave's features into one coherent, genuinely useful client experience.

1. **New `Log` tab** (`ShellContent` route `Log`, `Icon="tab_log.svg"`, `views:LogPage`) — the
   daily check-in hub: today's manual-entry summary, a big "Log today" button → `LogEntryPage`,
   the 14-day `SleepTrendChart` (lane 02 exposes it as a view/VM you host), recent entries list,
   and an honest "sample data" note when no manual entry exists yet.
   Create `LogPage.xaml(.cs)` + `LogViewModel` (constructor-inject `IManualEntryService`,
   `IUserService`-style deps as needed; register in the marker region).
2. **Goals page**: replace the auto-named "New goal 1" flow with navigation to
   `GoalEditorPage` / `HabitEditorPage` (lane 03), habit list with per-day completion toggles,
   weekly ring, streak chip, empty states, undo for archive, filter chips (All / Goals / Habits).
3. **Programs page**: enroll/leave/complete-day moved into cards that navigate to
   `BootcampDetailPage` (lane 04), plus filter by category and a "your program adapted today"
   badge sourced from `Bootcamp.WasAdaptedToday`.
4. **Profile page**: add the update card (lane 01's `UpdateBannerViewModel`/`CheckAsync`) with
   "Check for updates" + "What's new" navigation to `UpdatePage`; theme mode segmented control
   (`ThemeMode` via `IThemeService`); reminders entry (lane 09's `RemindersPage`); privacy
   inventory extended with the new stores (`livora_manual_entries.json`, `update_feed.json`,
   `reminders.json`) — list them honestly with Manual/Device origin; version row + "about" text.
5. **Health page**: header action "Log" (push `LogEntryPage`), surface the manual-vs-mock origin
   per section, and a 7-day sparkline row (lane 02's chart if reusable, else simple bars).
6. **Today page**: pull-to-refresh (`RefreshView`), a "Log today" nudge when no manual entry,
   quick actions row (reminders/updates/review), and confirm the weekly review button navigates
   (`OpenWeeklyReview` already exists — make sure it pushes `WeeklySummaryPage`).
7. **Onboarding**: add a step that offers first-time notifications (lane 09 `RequestGrantAsync`)
   and explains data stays on device; keep it short.
8. **Shell**: tab titles refresh on language change (already), add the Log tab in the right order
   (Today, Health, Log, Goals, Programs, Profile) and register routes inside the marker region:
   `log-entry`, `goal-editor`, `habit-editor`, `bootcamp-detail`, `updates`, `reminders` using the
   page types created by lanes 01-04/09 — reference them by their documented namespaces
   (`LIVORA.Presentation.Views.LogEntryPage`, `...GoalEditorPage`, `...HabitEditorPage`,
   `LIVORA.Presentation.Views.BootcampDetailPage`, `...UpdatePage`, `...RemindersPage`).
   If a type is missing at your build time, comment the line with `// ORPHAN:` and note it.
9. Keep every string localized (your new keys in §11 resx blocks) and every binding live-updating
   on `OnLanguageChanged`.

## 8. Lane 08 — Search, filter, sort, insights UX, empty states

Owned paths: `Presentation/ViewModels/Discovery/**` (create),
`Presentation/Components/SearchBarView.xaml` (create as a `ContentView`), plus *template-level*
polish inside `Presentation/Views/Today/TodayPage.xaml` (recommendation cards only) and
`Resources/Styles/` — wait: styles are lane 05's. So: put your styles inline in your own XAML.

Make the app feel considered:

- `SearchBarView` (reusable: placeholder, clear button, `TextChanged` event, RTL-correct magnifier,
  `IconGlyph` style) + a `FilterChipsView` (single-select chip row bound to a
  `IReadOnlyList<FilterOption>` with `SelectedKey`), both `ContentView`s with `BindableProperty`s.
- `Discovery/FilterSortService.cs` (pure logic in `Application/Discovery/` — you own that file):
  predicates + comparers over goals/habits/bootcamps (by category, status, streak, days left,
  progress, "at risk"), with **locale-aware** text matching (case-insensitive, diacritic-insensitive,
  and matching Persian digits typed as Latin and vice versa — reuse
  `IFormatService`/`LocalizationService` behavior through `ILocalizationService`).
- Recommendation cards on Today: tappable → an explanation sheet (`FlyoutBase`/`DisplayAnimated`
  — use a `Border` overlay or `Shell` modal-free approach that works on all four heads) showing
  why (rule key), expected benefit, confidence bar, and "why this, why now" copy from the existing
  `ExplainKey`/`ExpectedBenefitKey`. Add an `ICommand` to dismiss/snooze a recommendation
  (persist snooze in a file you own: `livora_snoozed.json`).
- Empty/skeleton/error states: `EmptyStateView` `ContentView` (icon glyph + title + body + CTA,
  localized), used where lists are empty.
- Do not touch files owned by lanes 07 for Goals/Programs pages (they get the components through
  your NOTES).
- Tests: `Tests/Tests/DiscoveryFilterTests.cs` for the pure matcher/comparers (incl. Persian).

## 9. Lane 09 — Reminders + notifications + settings depth

Owned paths: `Services/Reminders/**` (create), `Presentation/Views/Settings/**`,
`Presentation/ViewModels/Settings/**` (create), `Application/Reminders/**` (create),
`Domain/Enums/ReminderEnums.cs` (create).

Real client value: LIVORA tells you at 18:00 that your morning-walk streak is at risk — and never
lies about whether it can.

- `Application/Reminders/ReminderEngine.cs` (pure): given `PersonalState`, goals, habits and
  `ReminderSetting`s, decide what to notify (habit-at-risk ≥18:00, sleep-debt wind-down,
  bootcamp day pending, "you haven't logged today"), dedupe per day (`lastFired` map persisted by
  the service layer), compute the exact localized `TextKey`/args. Unit-test it.
- `Services/Reminders/LocalReminderService.cs` implements `IReminderService` using
  `Plugin.LocalNotification` (v14.1.2 — `INotificationService`,
  `NotificationRequest` with `Schedule.NotifyTime`, `Permissions.Notifications`
  `AreNotificationsEnabledAsync`/`RequestPermissionsAsync`). Store settings + last-fired dates in
  `livora_reminders.json` (own tiny store, same pattern as lane 01). Android needs the platform
  init — `MauiProgram` line: `builder.UseLocalNotification();` (declare as `APPEND:` for the
  orchestrator; also declare the `Platforms/Android` + `Platforms/Windows` requirements you find
  in the package docs and mark them honestly as untested on device).
- `RemindersPage` + `RemindersViewModel`: list of built-in reminder kinds with enable switch,
  time picker, repeat-days chips, a grant-state banner
  (`NotificationGrantState` → localized: allowed / blocked / not requested / system-managed) and a
  "grant notifications" button that reflects the real result — never a fake success.
  Plus app settings depth here: theme mode segmented control, language segmented control (reuse
  the Profile pattern), data-source note, and an "about/updates" link to `UpdatePage`.
- Tests: `Tests/Tests/ReminderEngineTests.cs` (dedupe, threshold times, mask logic, key selection).

## 10. Lane 10 — Tests, docs, coverage

Owned paths: `Tests/**`, `README.md`, `docs/**`.

- Add the Wave 3 tests named in the other lanes' sections ONLY for code that already exists in
  `Application/`/`Domain/`/`Infrastructure/` in your copy — you will not see lane code. So instead:
  extend coverage of what IS here: `AppVersion.Compare/TryParse` (contract exists — test it hard),
  `DataNormalizer` edge cases, `BaselineService` wrap-around bedtime, `TrendService` insufficient
  data, `RuleEngine` each rule boundary (find the thresholds by reading the code),
  `RecommendationService` cap logic, `ProgramAdapter`, `WeeklySummaryService` null-below-3-days,
  `LocalizationService` key lookup + `[missing]` behavior + Persian digit/percent/duration
  formatting, `CultureBootstrap` fallback, `Goal`/`Habit` progress and streak math (incl. Saturday
  week start), `DemoDataSeeder` idempotency, and an honesty suite: mock-labeled strings must be
  reachable via keys, `NormalizedDay.Completeness()`, `MetricState.Level` dead band.
  Target +40 tests, all green.
- Localization integrity test: parse both `.resx` files in the test project (they are not compiled
  into the test project — read from disk relative to the repo root computed from
  `AppContext.BaseDirectory`, with a clear skip when not found) and assert equal key sets, no
  empty values, and that `fa` values actually contain Persian codepoints.
- `README.md`: rewrite §3 (Wave 1→2→3), §7 build/test, §8 status, §9 roadmap for Wave 3 — describe
  the new features, the 6 tabs, the update feed, notifications honesty, desktop layout, and the
  lane protocol. Keep the honesty table accurate; move "Real providers" to still-not-implemented.
- `docs/WAVE3.md`: architecture decisions (why `IUpdateService` is keyless, why manual overlay
  beats replacing the provider, the `TrExtension` mechanism, chart/RTL approach, what is
  deliberately still mock).

## 11. The resx append block format (lanes 01,02,03,04,07,08,09)

Your diff cannot edit the frozen resx files. Instead, after the diff, emit:

```
KEYS-EN
<data name="Update.Title" xml:space="preserve"><value>Updates</value></data>
...
KEYS-FA
<data name="Update.Title" xml:space="preserve"><value>بروزرسانی‌ها</value></data>
...
```

Rules: same indentation/style as existing entries, one line per key, EN and FA with **identical key
order and count**, `xml:space="preserve"`, arguments as `{0}`/`{1}` in both languages (Persian may
reorder indices but must use the same count), no English-only keys, no duplicate keys, and every key
you emit must appear in your own code. Persian must be real idiomatic Persian (native register,
"تو" form, ZWNJ where natural), not transliteration and not Arabic.
