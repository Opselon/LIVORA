# Wave 3 merge — state as of 2026-09-13 (orchestrator working notes)

Base commit chain:
- `ff39382` lane contracts + foundation (Wave 3 enums, ISettingsService additions, TrExtension,
  weak LanguageHook, marker regions, tab_log.svg, docs/LANES.md) — builds 0W/0E, 117 tests green.
- `bf5edbd` orchestrator audit fixes applied BEFORE lanes merge (so no lane re-implements them):
  DailyHistoryStore → IDataProvider seam; Bootcamp phantom rule key removed; Rec.AdvanceGoal EN
  placeholder; seeder one-shot gate + idempotent catalog; Goal/Habit `NameKey`; Tab.Log both langs.

LANES DELIVERED: 09 (reminders/notifications + Today + Review + shared components, 22 files, build ok,
tests ok, patch at %LOCALAPPDATA%\Temp\lane09.diff — sha256 8fd54656…67e1d) and
10 (321 new tests → 438 total, README + docs/WAVE3.md + docs/WAVE3-MERGE.md, patch at lane10.diff).

LANES STILL RUNNING: 01 updates, 02 manual pipeline, 03 log UI+chart, 04 shell/responsive,
05 design system, 06 profile/settings/onboarding, 07 goals/habits editors, 08 programs/discovery.

## Tripwires lane 10 left that MUST be updated at merge time (they pin pre-fix behavior)

Lane 10 wrote two tests that deliberately assert the OLD behavior of code I have since fixed in
`bf5edbd`, plus one ctor it may call with the old signature:

1. `ResxIntegrityTests.KnownDriftWaivers` — waives `Rec.AdvanceGoal` EN missing `{0}`, and a
   companion test asserts the waiver is STILL needed. EN now has the placeholder → remove the
   waiver and flip the companion to assert no waiver is needed.
2. `Wave3DomainAndSeedTests.EmptiedGoalStore_ReseedsEverything_KnownWave3Hazard` — pins the
   re-seed hazard. Fixed by the `DemoDataSeeded` gate → rewrite as the positive guarantee
   (deleting every goal does NOT reseed; first launch still does).
3. `DemoDataSeeder` ctor gained an `ISettingsService` parameter (5th, before the localizer) —
   any lane-10 construction of it must pass one, and `MauiProgram.cs` already does.

## Merge order + collision watch-list (unchanged from LANES.md §3/§5)

Apply: 02 → 05 → 01 → 04 → 03 → 07 → 08 → 06 → 09 → 10.
Shared files needing hand-merge: `MauiProgram.cs` (WAVE3-DI: dedupe registrations, apply lane 02's
IDataProvider REPLACE), `AppShell.xaml`/`.cs` (04 owns; others' routes go in the marker),
`Presentation/Views/BaseContentPage.cs` (06's version, then 04's one-line hook),
`Presentation/Views/Today/TodayPage.xaml` (09's), `ProfilePage.xaml` (06's), both `.resx`
(one ordered pass, drop duplicate keys, keep identical EN/FA order), `WeeklySummaryPage.xaml`
(09's + 08's host snippet).

Lane 09 explicitly needs lane 02's `IManualEntryService` registration in the SAME merge (Today
nudge + log-reminder gate resolve it) — do not split them into separate builds.

## Known-good commands (run from repo root after each merge step)

    dotnet build LIVORA.csproj -f net10.0-windows10.0.19041.0 --nologo -v q
    dotnet test Tests/LIVORA.Tests.csproj --nologo -v q
    dotnet build LIVORA.csproj -c Release -f net10.0-android --nologo -v q   # Android Debug has a known emulator file-lock hazard
