# Wave 3c — Honest Status Matrix

Repo: `Opselon/LIVORA` @ `origin/master = aed6f7763b7042440478d533f8e2d01e8d68b45d` (fetched +
re-inspected 2026-09-13). Every row was checked **at this SHA in a clean worktree**
(`int_wt`), not in lane copies. "In-flight" = lives only in `livora_w3c/laneNN` copies, which are
currently still pristine (`diff -rq origNN laneNN` → zero differences for all 7 lanes; no
`laneNN.patch` files exist yet). Foreign WIP is NOT counted as real.

Base verification for this matrix: `dotnet test Tests/LIVORA.Tests.csproj --nologo -v q` →
**Passed! Failed: 0, Passed: 439, Skipped: 0, Total: 439** (net10.0, 978 ms).
Windows Release build: **0 Warning(s), 0 Error(s), 52.0s**. Android Release build: **0 Error(s),
5 warnings (all doc-comment CS1574/CS0419), 5:17**. `gh run list --branch master`: last 3 CI runs
(34762593846, 34761609032, 34760838574) all **success**.

## Matrix

| # | Claim | Proof command / test | Actual result at origin/master (aed6f77) | Verdict |
|---|---|---|---|---|
| 1 | 5-tab UI, onboarding, light/dark tokens | `grep -c ShellContent AppShell.xaml` → 5 (Today/Health/Goals/Programs/Profile) + `AddLogTab()` in `AppShell.xaml.cs:34` inserts Log after Health at runtime (route-positioned, not index); `grep OnboardingPage App.xaml.cs` → `:29` root-page switch on `settings.OnboardingCompleted`; `grep -c 'x:Key' Resources/Styles/LivoraColors.xaml` → 101 incl. named Dark mirrors + `AppThemeBinding` across pages | All present and compiled (win/android builds green). Tests: none asserts tab count — UI-shape rests on build + manual run | **VERIFIED** (code-level) |
| 2 | EN/FA RTL live switch + Jalali/Persian digits | `dotnet test --filter LocalizationAndFormattingTests` (class in `Tests/Tests/Wave3LocalizationTests.cs`, incl. `LongDate_PersianIsJalaliWithPersianDigits`, `CultureBootstrap_ResolvesBothCultures_WithoutThrowing`); `grep FlowDirection App.xaml.cs` → `CreateWindow:32` + `ApplyFlowDirection():216` re-applies on window/shell; `LocalizationService.LanguageChanged` + `ObservableObject.SubscribeLanguage` re-resolve every VM live; `LogEntryRules.cs:158` parses ۰-۹/٠-٩ input back to ASCII | All 439 tests pass incl. the full localization suite | **VERIFIED** |
| 3 | State engine provider → PersonalState | `grep PersonalState Application/State/UserStateService.cs` → `GetStateAsync(DataRefreshMode):76` returns derived `PersonalState`, `StateUpdated` event, in-flight join; tests `StateAndBaselineTests` + `Wave3DiIntegrityTests.ResolvedGraph_RunsOneHonestEndToEndCycle` (asserts `sleep.minutes` Origin=Manual through real container) | Passes at 439/439 | **VERIFIED** |
| 4 | Rules + recommendations cap + plan adaptation | `RecommendationService.cs:35-36` `Take(1)` High + `Take(2)` rest → cap 3, asserted by `RuleAndRecommendationTests.Caps_VisibleRecommendations` and `Assert.True(recs.Count <= 3)` in DI end-to-end; `ProgramAdapter.AdaptDay` + `PlanAndRuleHonestyTests` (`LowRecovery_ResizesExerciseThroughGrammar`, `AdaptedItem_AlwaysNamesItsRule`, `CalmDay_PlanIsNotAdapted`) | Passes | **VERIFIED** |
| 5 | Weekly review incl. under-3-days refusal | `grep 'Count < 3' Application/Insights/WeeklySummaryService.cs` → `:44 if (thisWeek.Count < 3) return null; // not enough to say anything honest`; tests `InsightsAndSummaryTests.FewerThanThreeClosedDays_SaysNothing`, `ThreeClosedDays_ReturnsSummary_ButLabelsConfidenceLow` | Passes | **VERIFIED** |
| 6 | Privacy inventory + delete-all | `grep DeleteAll Infrastructure/Security/PrivacyService.cs` → `:120 DeleteAllLocalDataAsync()` documented 6-file wipe; `Presentation/ViewModels/Profile/LocalDataFiles.cs:25` per-file inventory with origin labels; honesty suite `EveryMockDisclosureKey_ExistsInBothLanguages`. NOTE: frozen contract `ILocalDataCatalogService` (`IAccountsAndDataContracts.cs:56`) has **no implementation** at master — implementation is lane 01 in-flight | Wipe + inventory work today through PrivacyService/LocalDataFiles; catalog contract is a slot | **VERIFIED** (with catalog-slot caveat) |
| 7 | AppVersion compare | `grep 'class AppVersion' Application/Abstractions/IWave3Contracts.cs` → `:65`; `Wave3AppVersionTests` (TryParse shapes, overflow, prerelease; Compare ±1/0) | Passes | **VERIFIED** |
| 8 | Wave3 contracts as contracts | `ls Application/Abstractions/` → `IProvenance/IAi/IConsent/IPlan/IGatewayConfig/IAccountsAndDataContracts.cs` + `IntelligenceEnums.cs` all exist MAUI-free at master (the aed6f77 commit itself); `ApplicationPurityTests` keeps layers key-only; but implementations: `ICloudAuthService` — "CONTRACT SLOT ONLY. No backend ships in wave 3c", `ILocalDataCatalogService` — no impl, `IAiTransport` — no impl | Interfaces frozen & referenced; engines pending in lanes 01/02 | **VERIFIED** (they are contracts — honestly unimplemented where stated) |
| 9 | Health source mock label | `grep SourceType Application/Abstractions/IDataProvider.cs` + `Settings.SampleDataNote` disclosure row (`SettingsPage.xaml:229-235`); `SampleHealthProvider` is the only `IDataProvider` base (`ManualOverlayProvider` on top); tests `MockOriginLabel_NeverReadsLikeRealData`, `ProviderDay_IsLabeledMock_EveryFieldOfIt` | Passes | **VERIFIED** |
| 10 | Intelligence mock label | `SampleIntelligenceProvider` is the only `IIntelligenceProvider` at master; `IntelligenceSource.RulesOnly=0` etc. report path; tests `IntelligenceInterpretation_ReportsMockOrigin_AndNeverFabricatesOverrides`, `DailyInsight_SourceIsMock_AndCarriesOnlyKeys` | Passes | **VERIFIED** |
| 11 | Manual entry store/overlay | `ManualEntryStore.cs:33 : IManualEntryService`, `ManualOverlayProvider` registered as THE `IDataProvider` (`MauiProgram.cs:126`); backfill via `DailyHistoryStore`; DI end-to-end test proves Manual origin flows through pipeline | Code at master, tests pass | **VERIFIED** |
| 12 | Update feed + UpdateService | `Infrastructure/Updates/{GitHubReleaseFeed,ReleaseFeedParser,UpdateEvaluator,UpdateFeedCache,UpdateService}.cs`; registered `MauiProgram.cs:104-110`, route `updates:175`; `Wave3AppVersionTests` + `Wave3HonestyTests` cover evaluation | Code at master, tests pass | **VERIFIED** (live-feed fetch not covered by unit tests — HTTP path is manual/CI-adjacent) |
| 13 | Reminders / notifications | `LocalReminderService.cs:33 : IReminderService, IReminderLedger, IReminderEvaluator`; `Plugin.LocalNotification 14.1.2` in `LIVORA.csproj:65`; registered `MauiProgram.cs:129-150`, route `reminders:181`; tested scope is honest: `ReminderSetting` contract tests (`ReminderSetting_DefaultsAreTheDocumentedValues`, `DaysMaskBit0IsSunday`) — `ReminderEngine` static evaluator has NO direct unit test | Code compiles both heads; engine logic + actual OS notification delivery unverified by tests | **VERIFIED** (code) / engine + delivery **PENDING** test coverage |
| 14 | ThemeService | `ThemeService.cs:30 : IThemeService` (persists mode, maps AppThemeKind↔MAUI); registered `MauiProgram.cs:153`; `App.xaml.cs:72-78` applies on launch + change; `Wave3LocalizationTests.IThemeService_MembersMatchTheFrozenContract` | Passes | **VERIFIED** |
| 15 | Log tab / editors / charts / responsive | `LogPage`, `LogEntryPage`, `GoalEditorPage`, `HabitEditorPage`, `BootcampDetailPage` files exist + routes registered (`MauiProgram.cs:176-179`); `SleepTrendChart.cs` SkiaSharp 4.152; `Responsive/{ResponsiveGrid,Breakpoint,AdaptiveLayout}`; android build compiles all (`5 warnings` above are these files' doc comments) | Compiled both heads; rendering/layout NOT runtime-tested | **VERIFIED** (code) — visual/runtime **PENDING** (see #20) |
| 16 | Real providers (HealthConnect / Apple / Garmin / Fitbit + OAuth) | `grep -rni "healthconnect\|garmin\|fitbit\|oauth" --include=*.cs .` → only enum slots (`HealthDataEnums.cs:31-34` `DataOrigin.HealthConnect/Garmin/Fitbit`) + one doc-comment string `"healthconnect"`. No adapter class, no OAuth, no `Platforms/` provider code | Nothing implemented at master; not even in lane copies yet (all pristine) | **PENDING** |
| 17 | Real LLM interpretation | `grep -rni "openai\|llm\|completion" Infrastructure Application` → only `SampleIntelligenceProvider.cs:11` comment "a future OpenAI/local-LLM can swap in". No HTTP AI transport, no gateway client at master (contracts `IAiContracts.cs`/`IGatewayConfigContracts.cs` exist; SSE-parsing client is lane 02 in-flight, currently not written) | Mock-only today — honestly labeled as such | **PENDING** |
| 18 | Cloud sync / accounts | `IAccountsAndDataContracts.cs:19-20` — `CloudAccount = 2` marked "CONTRACT SLOT ONLY. No backend ships in wave 3c"; `ICloudAuthService` "returns NotAvailable"; zero cloud code | Contract + honest not-available path only | **PENDING** (by design this wave) |
| 19 | Store packaging (MSIX / APK / App Store) | `grep WindowsPackageType LIVORA.csproj` → `:24 <WindowsPackageType>None</WindowsPackageType>` (unpackaged dev build); `.github/workflows/release.yml` builds APK+AAB (unsigned-when-no-keystore, labeled) + win-unpackaged zip + iOS-sim + Catalyst ad-hoc to GitHub Releases. No MSIX manifest/publish, no Play/App-Store signing pipeline | GitHub Releases artifacts ≠ store packaging | **REFUTED** as "store-ready"; release.yml APK/AAB exists (still not store-submission-ready) |
| 20 | App-launch smoke automation | `grep -rni "smoke\|appium\|winappdriver" .github/workflows` → no hits; `ci.yml` = dotnet test + 2 head builds only; README §Honesty admits "'the app starts' still rests on manual runs" | No automation that launches the app on any platform | **REFUTED** (known debt) |

## Verdict tally

- VERIFIED: rows 1–15 (15 rows; 13/15 have an on-device/visual asterisk — code+tests at master are green).
- PENDING: rows 16, 17, 18 (3 rows).
- REFUTED: rows 19, 20 (2 rows — "store packaging" and "launch smoke" do not exist beyond artifacts/manual runs).

## DI marker-region risk notes (for the integrator)

- `MauiProgram.cs` layout at master: `// WAVE3-DI:` (L99) … nested `// WAVE3B-DI:` (L183) …
  `// WAVE3B-DI-END` (L185) … `// WAVE3-DI-END` (L187). Lane APPEND blocks go after the
  **WAVE3B-DI** line, i.e. INSIDE both regions.
- `Wave3DiIntegrityTests.MarkerRegion_ExistsExactlyOnce_AndSitsInsideItsHost` counts
  line-anchored (`^\s*`) hits of `// WAVE3-DI:` / `// WAVE3-DI-END` etc. = exactly 1. The prose at
  L63 mentioning "WAVE3-DI region below" does not match because it isn't at line start. **This test
  passes at aed6f77 (confirmed inside the 439/439 local run).** What breaks it: a lane block that
  pastes a copy of any marker line (including `WAVE3-DI-END`) into the WAVE3B region — the count
  goes 2 and the outer `Region()` regex truncates early, silently dropping registrations below.
- `MarkerRegion_Blocks_AreInFileOrderAndNotOverlapping` rejects `builder.Build()` /
  `ServiceHelper.Initialize` inside the outer region — lanes must not close the region before their
  content.
- `Wave3Contracts_AreNotRegisteredYetInThisCopy...` requires the pending set to be 0 or 4 (all four
  Wave-3 contracts are registered at master → passes); a *partial* re-registration by a lane fails —
  good tripwire.
- Real blind spot: the duplicate-registration scan is regex-level; two different concrete types
  registered for the same interface *with different type-name lengths* or a duplicated
  `LocalDataCatalog` under two names (§5 of INTEGRATION-PLAN) will not fail any test — reviewer check
  per merge.

## Post-integration verification (04436af + this commit)
- merged tree: dotnet test 1188/1188 green; net10.0-windows Release 0 Errors; net10.0-android Release 0 Errors (pre-existing doc warnings only).
- DI composed: security/AI/plan/sync/health/workout/UI lanes registered (single-owner rule honored; catalog arbitrated: lane-01 LocalDataCatalog owns ILocalDataCatalogService, lane-06 concrete service kept for meta/sync writers).
- Lock gate wired in App.CreateWindow (no-op without a passcode); ConnectionTester records probe verdicts through the single writer.
- Honest PENDING after this PR: Android Health Connect runtime (client unbundled), key rotation (historical exposure), store packaging, launch smoke, mobile pickers.
