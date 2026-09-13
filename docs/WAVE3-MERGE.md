# LIVORA — Wave 3 merge checklist (lane 10 → orchestrator)

Derived from `docs/LANES.md` §0–§5. This is the artifact to diff the actual merge against: every line
the lanes were told to hand over is listed, with the file and marker it belongs to. Lane 10 never
writes shared files — anything below marked **(APPEND)** arrives in a lane's report block.

Verification gates at the end (§7) are the commands that prove the merge; each one names the test that
catches the mistake.

---

## 1. Marker regions that exist in the base

| File | Marker (open / close) | Today's content | Receives |
|---|---|---|---|
| `MauiProgram.cs` | `// WAVE3-DI:` … `// WAVE3-DI-END` | comment only | every service/VM registration (§2) + `builder.UseLocalNotification()` (lane 09) |
| `App.xaml.cs` | `// WAVE3-APP:` … `// WAVE3-APP-END` | comment only, inside `CreateWindow` | Windows window sizing, `IThemeService.Apply()`, `RequestedThemeChanged` (lane 04) |
| `AppShell.xaml.cs` | `// WAVE3-SHELL:` … `// WAVE3-SHELL-END` | comment only | `Routing.RegisterRoute` lines (§3) |

Rules the tests enforce: each marker appears **exactly once** as its own line (the prose inside the
region repeats the marker text, so a merge script that counts substring occurrences will over-count);
the DI region stays above `builder.Build()` and never contains `ServiceHelper.Initialize`.
→ `Wave3DiIntegrityTests.MarkerRegion_*`

Not a marker region, but merged the same way: `Presentation/Views/Profile/**` hosts lane 01's
`UpdateBannerViewModel`, and `WeeklySummaryPage.xaml` hosts lane 08's `WeekProgressView` via an
`APPEND:` block (that page belongs to no lane).

## 2. DI lines to apply — one block per lane, each exactly once

Order does not matter; **duplicates do**. MS.DI lets the last registration win silently, so a block
applied twice is a behavior change with no build error.

| Lane | Registration | Kind |
|---|---|---|
| 01 | `IUpdateService` → `UpdateService` | singleton |
| 01 | `GitHubReleaseFeed` | singleton |
| 01 | `UpdateViewModel`, `UpdateBannerViewModel` | transient |
| 02 | `IManualEntryService` → `ManualEntryStore` | singleton |
| 02 | **replace** the existing `IDataProvider` registration (`SampleHealthProvider`) with `ManualOverlayProvider` | singleton |
| 03 | `LogPage`, `LogViewModel`, `LogEntryPage`, `LogEntryViewModel` | pages transient/VM transient per lane block |
| 05 | `IThemeService` → `ThemeService` | singleton |
| 06 | `SettingsPage`, `SettingsViewModel` | transient |
| 07 | `GoalEditorPage`, `GoalEditorViewModel`, `HabitEditorPage`, `HabitEditorViewModel` | transient |
| 08 | `BootcampDetailPage`, `BootcampDetailViewModel`, `ProgramsViewModel` (if the lane re-declares it) | transient |
| 09 | `IReminderService` → `LocalReminderService` | singleton |
| 09 | `builder.UseLocalNotification();` | on the `MauiAppBuilder`, **before** `Build()` |
| 09 | `RemindersPage`, `RemindersViewModel`, `Application/Reminders/ReminderEngine`, `SnoozeStore` (app + infra) | per lane block |

Lane 02's replacement is the one to eyeball: the base currently has
`AddSingleton<IDataProvider>(sp => sp.GetRequiredService<SampleHealthProvider>())`. After the merge the
`IDataProvider` line must resolve the overlay and `SampleHealthProvider` must still be registered (the
overlay takes it as a dependency).

→ Checks: `NoServiceInterface_IsRegisteredTwiceInMauiProgram`,
`EveryConcreteTypeNamedInMauiProgram_ExistsSomInTheDocument`,
`Wave3Contracts_AreNotRegisteredYetInThisCopy_WhenTheirLanesHavenotLanded` (asserts the region is
either fully pending or fully merged — a half-merged region fails on purpose),
`PureServiceGraph_ResolvesFromARealServiceProvider`.

## 3. Shell routes — seven names, frozen in LANES.md §4

| Route | Page (all in namespace `LIVORA.Presentation.Views`) | Lane |
|---|---|---|
| `updates` | `UpdatePage` | 01 |
| `log-entry` | `LogEntryPage` | 03 |
| `settings` | `SettingsPage` | 06 |
| `goal-editor` | `GoalEditorPage` | 07 |
| `habit-editor` | `HabitEditorPage` | 07 |
| `bootcamp-detail` | `BootcampDetailPage` | 08 |
| `reminders` | `RemindersPage` | 09 |

```csharp
Routing.RegisterRoute("updates", typeof(LIVORA.Presentation.Views.UpdatePage));
// …one per row, inside // WAVE3-SHELL:
```

Both directions must hold: a route registered for a page that did not land is a runtime navigation
failure with no build error; a page landed without its route breaks lane 09's defensive Today
quick-actions ("not available yet" forever). Query-arg reachability (`goal-editor?mode=new`,
`log-entry?date=…`) is a per-lane responsibility.

## 4. Tab / shell chrome

- Lane 04 adds the **6th tab `Log`** between Health and Goals (`Resources/Icons/tab_log.svg`,
  `ContentTemplate` → `views:LogPage`, `Route="Log"`, title key `Tab.Log`).
  `AppShell.xaml.cs::RefreshTitles` must gain `LogItem.Title = loc["Tab.Log"];` — today it re-resolves
  exactly five titles, so a sixth tab that nobody re-titles shows the wrong language after a switch.
- `Tab.Log` does **not** exist in either resx yet (verified: `Tab.*` = 6 keys, no `Log`). Lane 04 must
  emit it in `KEYS-EN`/`KEYS-FA` or the tab label renders as `[Tab.Log]`.

## 5. File sets that must stay disjoint (ownership audit)

Merge order matters only where a lane edits a file another lane owns. The protocol makes the sets
disjoint; this table is the check that they stayed that way.

| Path | Owner | Anyone else may… |
|---|---|---|
| `Infrastructure/Updates/**`, `Presentation/{Views,ViewModels}/Updates/**` | 01 | reference types only |
| `Infrastructure/Persistence/ManualEntryStore.cs`, `Application/HealthData/{ManualMerge,ManualOverlayProvider}.cs`, `Domain/Models/Health/ManualEntryRecord.cs` | 02 | — |
| `Presentation/{Views,ViewModels}/Log/**`, `Application/HealthData/LogEntryRules.cs` | 03 | route to `log-entry` |
| `App.xaml`, `App.xaml.cs`, `AppShell.xaml`, `AppShell.xaml.cs`, `Presentation/Responsive/**` | 04 | `APPEND:` only |
| `Resources/Styles/LivoraColors.xaml`, `LivoraStyles.xaml`, `Infrastructure/Settings/ThemeService.cs`, `Presentation/Components/{SegmentedControlView,StatChip,ProgressRing,PressableBorder}.*`, `Resources/{Icons,Splash,AppIcon}/**` | 05 | read tokens (§2 of LANES.md) |
| `Presentation/{Views,ViewModels}/{Profile,Settings}/**`, `Presentation/Views/BaseContentPage.cs` | 06 | lane 04's hook is an `APPEND:` applied **after** 06 |
| `Presentation/{Views,ViewModels}/{Goals,Editor}/**`, `Application/Planning/GoalEditorRules.cs` | 07 | — |
| `Presentation/{Views,ViewModels}/{Programs,Bootcamps}/**`, `Application/Planning/BootcampProgress.cs`, `Application/Discovery/**` | 08 | `WeeklySummaryPage.xaml` via `APPEND:` |
| `Application/Reminders/**`, `Infrastructure/Notifications/**`, `Presentation/{Views,ViewModels}/{Today,Review,Reminders}/**`, `Presentation/Components/{EmptyStateView,SearchBarView,FilterChipsView}.*`, both `SnoozeStore.cs` | 09 | `WeeklySummaryPage.xaml` is nobody's — `APPEND:` |
| `Tests/**`, `README.md`, `docs/**` (except `LANES.md`) | 10 | request tests via `NOTES:` |
| `Domain/**`, `Application/Abstractions/*`, `Application/{State,Insights,Rules}/**`, `Application/HealthData/{DataNormalizer,SampleHealthProvider}.cs`, `Resources/Localization/*.resx`, `Infrastructure/Localization/*`, `Infrastructure/Persistence/{JsonFileStore,DailyHistoryStore}.cs`, `Infrastructure/Settings/PreferencesSettingsService.cs`, `Presentation/{ObservableObject,Theme,Components/TrExtension}.cs`, `LIVORA.csproj`, `global.json`, `Platforms/**`, `.github/**`, `docs/LANES.md` | **frozen** | report needs, never edit |

Collision watch-list (files more than one lane was told to touch, by protocol):

1. `Presentation/Views/BaseContentPage.cs` — lane 06 edits it, lane 04 appends one hook line into it.
   Apply 06 first, then 04's snippet; the result must keep the font walk, FlowDirection sync, and add
   `MaxContentWidth` centering + a `SetLoading(bool)`-style helper. Nothing may be removed.
2. `MauiProgram.cs` — every lane appends; `IDataProvider` is *replaced* by 02 and *re-read* by 03/09.
3. `WeeklySummaryPage.xaml` — lane 08 provides the `WeekProgressView` snippet, lane 09 hosts it in its
   own VM. Two reports, one file: apply 08's view insert and 09's binding/VM change as one edit.
4. `LIVORA.csproj` — frozen; lanes 03/05/09 already assume `SkiaSharp.Views.Maui.Controls`,
   `Plugin.LocalNotification`, and the `Resources/Icons/*` glob exist (they do).
5. Both `.resx` files — every lane emits `KEYS-EN`/`KEYS-FA`; lane 10 may not edit them.
   Merge them **in one pass, identical key order in both files** (see §6).

## 6. Resource merge — the pass that breaks most silently

1. Concatenate every lane's `KEYS-EN` block into `AppResources.resx` and the matching `KEYS-FA` block
   into `AppResources.fa.resx`, **same order**, after the existing entries.
2. Grep for collisions before applying: no lane may redefine an existing key with different text, and
   two lanes may not define the same new key differently (LANES.md §0.4 says grep first; the merge
   proves it).
3. Check the invariants mechanically — this is what the suite does after the merge:
   - key sets equal, counts equal, no duplicates → `EnAndFa_KeySetsAreIdentical`, `NoDuplicateKeys…`
   - no empty values → `NoEmptyValuesInEitherFile`
   - FA carries Persian script (allowlist: `Onboarding.Language.English`, `Health.HRV`,
     `Plan.Item.Habit`) → `FaValues_ContainPersianCodepoints_ExceptDeliberateLatinLabels`
   - FA is Persian, not Arabic (no `ي ك ة`; enough `پ چ ژ گ` and ZWNJ)
     → `FaTranslations_UsePersianOrthography_NotArabicCodepoints`
   - `{0}`/`{1}` index sets identical per key, contiguous from zero
     → `PlaceholderIndices_*`
   - every key starts with a documented prefix → `Keys_AreNamespacedWithTheDocumentedPrefixes`.
     Wave 3's new prefixes are already allowlisted (`Update.`, `Log.`, `Editor.`, `Settings.`,
     `Reminders.`, `Theme.`, `Notification.`, `Source.`, `Habit.`, `Goal.`). A lane that invents a
     different prefix must be refused or the list extended **deliberately**.
   - every key the engines emit exists in both files with enough placeholders
     → `Wave3HonestyTests.EveryCataloguedKey_*` (extend `Wave3EmissionCatalog` when a lane adds an
     engine-emitted key — that is the intended way to register a new contract key).
4. Pre-existing drift to fix in this pass (one line, both files, and delete the waiver + its "still
   needed?" test in `ResxIntegrityTests`):
   `Rec.AdvanceGoal` EN has no placeholder, FA uses `{0}`. Either make EN
   `Move "{0}" forward — 20 focused minutes` or drop `{0}` from FA.

## 7. Verification gates after the merge

```bash
# 1. tests (plain net10.0, no workloads) — must stay green
dotnet test Tests/LIVORA.Tests.csproj --nologo -v q

# 2. the app must still build (lane gate from LANES.md §0.10)
dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q

# 3. if obj/ is stale from parallel work
dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q \
  -p:BaseIntermediateOutputPath=objw3/ -p:BaseOutputPath=binw3/
```

Base state (lane 10, this copy): **438 passed / 0 failed** (117 Wave 2 cases unchanged) and the
Windows app build green with 0 warnings — both observed, not assumed.

Post-merge review pass:

- [ ] every §2 registration present exactly once; `IDataProvider` resolves `ManualOverlayProvider`
- [ ] every §3 route registered **and** its page file exists; every landed page has its route
- [ ] `Tab.Log` in both resx + `AppShell.RefreshTitles` re-resolves six titles
- [ ] §6 resx invariants pass (they are tests now, not advice)
- [ ] lane-requested tests added: pure helpers under `Application/` — `LogEntryRules` (03),
      `GoalEditorRules` (07), `BootcampProgress` / `ProgramDiscovery` / `WeekProgress` (08),
      `ManualMerge` + `ManualEntryStore` round-trip in a temp dir (02), `ReminderEngine` (09).
      Each is MAUI-free; add `<Compile Include>` lines in `Tests/LIVORA.Tests.csproj` (lane 10 owns it)
      and **nothing that needs a MAUI head**
- [ ] Wave 3 DI smoke coverage extended once implementations are head-free
      (`ThemeService` is the first candidate; see `docs/WAVE3.md` §3)
- [ ] honesty spot-check of new UI: mock labeled, self-reported labeled, failed check shown as failure,
      no `*Brush` on a Color-typed property, `Start`/`End` only, every new string a key
- [ ] README §9 status table updated: rows that move from "not implemented here" to real, and
      `KeyCount_IsAtLeastTheDocumentedBaseline`'s floor raised to the new key count

## 8. What lane 10 changed, for diffing

- `Tests/LIVORA.Tests.csproj`: +`Infrastructure/Localization/*.cs`,
  +`Resources/Localization/AppResources.cs`, +both `.resx` as `EmbeddedResource` with explicit
  `ManifestResourceName`, +`Infrastructure/Persistence/DemoDataSeeder.cs`,
  +`Microsoft.Extensions.Logging.Abstractions` 10.0.0, +`Microsoft.Extensions.DependencyInjection`
  10.0.0. **No** MAUI-dependent source added.
- New test files: `Wave3Harness.cs` (repo-root/resx discovery + `[Collection("LocalizationState")]`
  serialization for the culture-mutating suite), `Wave3AppVersionTests.cs`,
  `Wave3LocalizationTests.cs`, `Wave3ResxIntegrityTests.cs`, `Wave3HonestyTests.cs` (incl.
  `Wave3EmissionCatalog`), `ApplicationPurityTests.cs`, `Wave3UserStateTests.cs`,
  `Wave3ProgramAndPlanTests.cs`, `Wave3DomainAndSeedTests.cs`, `Wave3ThresholdTests.cs`,
  `Wave3DiIntegrityTests.cs`.
- `README.md` rewritten for Wave 3; `docs/WAVE3.md` (this file's companion) + this checklist added.
- The 117 pre-existing tests were **not** modified — no assertion was deleted or weakened. Two Wave 3
  findings are encoded as tests that pin current behavior rather than desired behavior
  (`EmptiedGoalStore_ReseedsEverything_KnownWave3Hazard`, the `Rec.AdvanceGoal` waiver) and are listed
  as debt in `docs/WAVE3.md` §5.
