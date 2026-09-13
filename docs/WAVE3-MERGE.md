# Wave 3 merge plan (orchestrator)

Base: `ff39382` (lane contracts + foundation). All 10 lanes cut from this commit, disjoint
file-sets per `docs/LANES.md` §3. Merge order below is deliberate: foundation → data → UI → docs.

| Order | Lane | Owns (primary) | Merge notes |
|---|---|---|---|
| 1 | 02 | ManualEntryStore, ManualMerge, ManualOverlayProvider, ManualEntryRecord | Pure data layer; everything else depends on manual values flowing through the pipeline |
| 2 | 05 | Styles/colors, ThemeService, SegmentedControlView/StatChip/ProgressRing/PressableBorder, icons, splash | Publishes tokens the UI lanes consume — must land before page lanes |
| 3 | 01 | Infrastructure/Updates, UpdatePage, UpdateViewModel, UpdateBannerViewModel | Independent; Profile references it |
| 4 | 04 | App.xaml(.cs), AppShell, Presentation/Responsive | APPEND into BaseContentPage lands after lane 06's version of that file |
| 5 | 03 | Log page/VM + SkiaSharp chart + LogEntryRules | Needs tab_log.svg (05) and ManualEntryStore (02) |
| 6 | 07 | Goals page rewrite + goal/habit editors + GoalEditorRules | |
| 7 | 08 | BootcampDetail, ProgramDiscovery, BootcampProgress, WeekProgress | APPEND into WeeklySummaryPage lands with lane 09's version |
| 8 | 06 | Profile + Settings + BaseContentPage | Reconcile BaseContentPage with lane 04's APPEND |
| 9 | 09 | Reminders (Plugin.LocalNotification), Today page, Review, shared components | Largest surface; reconcile Today + AppShell after |
| 10 | 10 | Tests/**, README.md, docs/** | Applied last; its test additions cover the merged tree |

## Mechanical steps

1. For each lane in order: `git -C <lane> add -A; git -C <lane> diff --cached --binary > laneNN.patch`
   then in the real repo `git apply --3way --index laneNN.patch` (binary-safe; `--3way` only where
   files legitimately overlap). If a lane patch fails to apply cleanly, fall back to
   file-by-file `git checkout <lane> -- <path>` for files only that lane owns, and hand-merge the
   shared files (App.xaml.cs, AppShell.xaml.cs, MauiProgram.cs, BaseContentPage.cs, Today/Profile
   pages, both resx files).
2. Apply every lane's `APPEND:` blocks into the four marker regions (WAVE3-DI / WAVE3-SHELL /
   WAVE3-APP) and the two resx files (KEYS-EN → AppResources.resx, KEYS-FA → AppResources.fa.resx)
   before the first build.
3. Build windows head → fix compile/binding errors myself (never re-run a lane for a one-line seam).
4. Build android head (Release if Debug hits the emulator file-lock hazard).
5. `dotnet test Tests/LIVORA.Tests.csproj` — must be green including the new Wave 3 tests.
6. Launch the Windows build, walk every tab in EN and FA, screenshot both.
7. Commit per lane-group or one reviewable commit, then push to master.

## Known seam risks (watch these during merge)

- `Presentation/Views/BaseContentPage.cs`: lane 06 edits, lane 04 appends a one-liner.
- `AppShell.xaml`/`.cs`: lane 04 owns; lanes 01/03/06/07/08/09 hand route registrations as APPEND.
- `MauiProgram.cs`: marker only; every lane appends DI lines. Deduplicate (e.g. two lanes
  registering `IThemeService`).
- `Resources/Localization/AppResources*.resx`: key collisions between lanes — de-dupe before
  build; a duplicate `<data name>` fails the resx compile loudly, which is what we want.
- `Presentation/Views/Today/TodayPage.xaml`: lane 09 owns; lane 01/03 nudge/banner references.
- `Presentation/ViewModels/Profile/ProfileViewModel.cs`: lane 06 owns; lane 01's banner VM.
- Lane 04's AppShell Log tab references `LogPage` (lane 03) — expected to fail in isolation, fine
  after merge.
- Another agent session may commit to `master` concurrently: always `git fetch` + re-inspect
  `git log origin/master` before committing, and never `push --force`.
