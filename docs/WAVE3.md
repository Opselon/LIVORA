# LIVORA — Wave 3 decisions, and why the honesty rules are written the way they are

Written by lane 10 (tests + documentation). Everything here is derived from code that exists in this
tree; claims that could not be verified from source are marked **Unverified**. The merge protocol lives
in `LANES.md`; the merge checklist derived from it is `WAVE3-MERGE.md`.

---

## 1. What Wave 3 was actually for

Wave 2 made the brain: state → baselines → rules → plan → explanation, all deterministic and tested.
Wave 3 makes the app useful to a person who pays for it: log your own data, edit real goals and habits,
open an adaptive program, see a real chart, get reminders, check for updates in-app, and not look like
a phone app stretched across a desktop window.

The risk of that mission is obvious: every one of those features is a chance to lie. A "sync" that
never happened, a chart drawn from sample data with the sample label removed, a "you're up to date"
printed because the feed was unreachable, a reminder that was never granted. Wave 3 is therefore built
as **contracts first, implementations in parallel, honesty enforced by tests** — and this document
records why each seam looks the way it does.

---

## 2. Decisions worth defending

### 2.1 Frozen contracts, ten parallel lanes

`Application/Abstractions/IWave3Contracts.cs`, `Domain/Enums/Wave3Enums.cs` and the abstraction files
are read-only for every lane. Lanes implement services; nobody edits a signature. The composition root
stays the only place a concrete type is named, and each lane hands the orchestrator an `APPEND:` block
targeting a marker region (`// WAVE3-DI:`, `// WAVE3-APP:`, `// WAVE3-SHELL:`) instead of editing
shared files.

**Why**: ten agents editing `MauiProgram.cs` is ten merge conflicts and an arbitrary winner. Marker
regions turn the merge into a sequence of appends that can be diffed against a checklist
(`WAVE3-MERGE.md`) rather than reconstructed by hand.

**Cost**: a lane can write a beautifully tested service that never gets registered. That is exactly
what `Wave3DiIntegrityTests` watches for — see §4.

### 2.2 The contracts are MAUI-free by construction

`IThemeService` returns `AppThemeKind` (a domain enum) rather than MAUI's `AppTheme`;
`IUpdateService`/`IReminderService`/`IManualEntryService` speak `Task`, records and domain enums.
`IWave3Contracts.cs` says so in a comment, and the theme test asserts that no `IThemeService` property
type lives in a `*Maui*` namespace.

**Why**: the test project targets plain `net10.0` and links `Application/**` directly. One MAUI type in
a contract and the whole pure-layer test strategy dies — either by not compiling or by dragging
`Microsoft.Maui.Controls` into a "pure" layer. This is the cheapest possible guarantee that the
honesty invariants stay testable forever.

### 2.3 `AppVersion.Compare` returns `int?`, not `int`

Unparseable input yields **null** = "no comparison possible", and callers must surface that as a
failure state rather than 0 = equal.

**Why**: an `int` API forces the implementer to pick a number for "I don't know", and 0 (equal, i.e.
"up to date") is the path of least resistance. `null` makes the over-claim a compile-visible decision.
`AppVersionTests` pins all seven documented shapes plus every null/unparseable combination, including
the trap that matters in practice: `1.10` must beat `1.9` (a string compare says the opposite).

Also pinned, deliberately: `1.2.0-rc.1` == `1.2.0`. The parser discards the suffix, so pre-release
ordering is *not* modeled. Callers must present `IsPrerelease` separately instead of pretending the
number sorts tags.

### 2.4 Manual data is an overlay, not a source

The lane-02 design composes `ManualOverlayProvider(SampleHealthProvider, IManualEntryService)` and a
pure `ManualMerge.Overlay(day, record)`: manual values win **per field**, the merged fields become
`Origin = Manual`, `Quality = Complete`, `Confidence = 1.0`, and untouched fields keep the provider's
provenance.

**Why this shape**: it makes the honesty claim structural rather than behavioural. A single "provider"
that happens to prefer user input would have to remember which fields it invented. With an overlay,
provenance is computed per field from inputs that already carry it — and the test for it
(`MetricProvenance_TravelsFromProviderToState`) is about the *derivation*, not the writer.

`ManualEntryDraft.IsEmpty` ignores `Note` on purpose: a saved note with no numbers is not a
measurement, and treating it as one would let the Today nudge go quiet on a day with no data.

### 2.5 Updates: five states where a naive design has two

`UpdateCheckStatus` separates `UpToDate`, `NewerThanFeed`, `NoConnection`, `RateLimited`, `Error`,
`Disabled`, `Unknown`, and `UpdateAvailable`; `UpdateInfo.FromCache`/`CheckedAtUtc` carry the answer's
provenance; `LatestVersion` stays null unless the feed actually answered.

**Why**: "no update available" is the single most useful sentence in the app and the easiest to
produce falsely. Collapsing unreachable-feed into "up to date" would make the app lie exactly when the
user is offline — which, for an app about checking a server, is often. `NewerThanFeed` exists because
CI builds (`99.0.0-rc.7`) would otherwise render as "you're current", which is both false and
confusing to the developer who filed the bug.

### 2.6 Notifications: `Unknown` is the default, `Allowed` must be earned

`NotificationGrantState` defaults to `Unknown` (`default(NotificationGrantState) != Allowed` is a
pinned test), and lane 09 must map the real platform answer.

**Why**: the failure mode of reminder features is not a crash, it is a user trusting a schedule that
was never armed. Making "assume allowed" impossible at the type level is cheaper than reviewing every
call site.

### 2.7 Persian stays the default, and RTL is not a formatting question

`LocalizationService` still defaults first launch to Persian and persists that choice. RTL is derived
from the *chosen language*; `CultureBootstrap` may degrade *formatting* to `fa` or invariant on a
trimmed-ICU device, and the layout still mirrors.

**Why**: this split was a real bug class in Wave 2. Conflating "we have no ICU data" with "we are
English" would flip an RTL user's interface. The tests assert both halves: `CultureName` follows the
resolved culture, `IsRightToLeft` follows the language.

### 2.8 Adaptation may only reduce load

`DailyPlanService` can halve a session, drop a focus block, add a walk/wind-down/recovery item. It can
never increase a planned workout, and every mutation records `AdaptedByRule` plus a key in
`AdaptationRuleKeys`; `AdaptationReasonKey` says `Plan.Adapted.Multiple` when several rules moved
things rather than picking one to blame.

**Why**: an app that quietly adds exercise on a bad-recovery day is worse than one that does nothing,
and an explanation that names one rule out of three is a fabrication. Both are pinned
(`ScaleDown_NeverInventsMinutes_AboveTheBase`, `AdaptedItem_AlwaysNamesItsRule`).

### 2.9 Dead bands and sample gates are product decisions written as numbers

`MetricState.Level` uses ±12%; `TrendService` needs ≥5 samples and a 6% band; baselines gate at
3/7/14 samples; the weekly review refuses below 3 closed days. `Wave3ThresholdBoundaryTests` pins each
one *on both sides of the boundary*, including the exclusive/inclusive details
(`|change| < band` means exactly 6% counts as a move; `> 0.65` means 0.65 does not fire).

**Why**: these constants are the difference between "you slept a bit less than usual" and a user
believing they are in trouble. Pinning the boundary turns every future retune into a deliberate act
with a failing test attached.

### 2.10 Localization is a typed contract, not a text dump

Wave 3 added three mechanical layers so "localize every user-facing string" stops being a review
comment:

1. `ApplicationPurityTests` scans `Application/**` and `Domain/**` for **sentence-shaped string
   literals** (three consecutive words). Zero today; any prose that lands fails CI.
2. `Wave3EmissionCatalog` hand-lists every `(key, arg-count)` pair the engines can emit. It is the
   contract between C# and the resx files, and its own test fails if a key is missing from either
   language or if a template needs an argument the engine never passes.
3. `ResxIntegrityTests` reads both resx files **from disk** and asserts key-set parity, no empty
   values, placeholder-index parity per key, contiguous placeholders, Persian (not Arabic)
   orthography (ی/ک/گ/چ/پ + ZWNJ, zero Arabic-only ي/ك/ة), and documented key prefixes.

**Why placeholder parity is the valuable one**: `LocalizationService.T` catches `FormatException` and
returns the raw template. So a Persian string that dropped `{1}` renders as `…{1}…` to a real user,
without a crash, without a log line, forever. Only a source-of-truth check on the resx files sees it.

One asymmetry already exists in the shipped files (`Rec.AdvanceGoal` uses `{0}` in FA, nothing in EN).
It is a **pinned waiver** with its own "is this waiver still needed?" test — the mechanism cannot rot
silently. Fixing it is a one-line orchestrator edit, listed in `WAVE3-MERGE.md` §6.

---

## 3. What lane 10 made testable, and how

Before Wave 3 the test project compiled `Domain/**`, `Application/**` and one Infrastructure file. It
now also compiles:

- `Infrastructure/Localization/*.cs` — `LocalizationService`, `FontFamilies`, `LanguageHook`,
  `CultureBootstrap`. All MAUI-free (only `Application.Abstractions` +
  `Microsoft.Extensions.Logging.Abstractions`), so the *real* key lookup, `[missing]` fallback and
  Persian formatting are tested instead of a reimplementation.
- `Resources/Localization/AppResources.cs` + both `.resx` as `EmbeddedResource` with explicit
  `ManifestResourceName` — the test assembly gets its own resource manifest, so the
  "missing key → `[Key]`" path and culture resolution are exercised end to end, and disk-level
  integrity is checked separately.
- `Infrastructure/Persistence/DemoDataSeeder.cs` — `IRepository<T>` + `Func<string,string>` only, no
  IO.

Two new packages, both pure-abstraction and workload-free: `Microsoft.Extensions.Logging.Abstractions`
(a real `NullLogger<T>` beats `null!` in a service that logs) and `Microsoft.Extensions.DependencyInjection`
(so the DI smoke test builds an actual `ServiceProvider`).

Nothing that needs a MAUI head was added: no `Presentation/**`, no `Services/**`, no file touching
`FileSystem`/`Preferences`.

### The DI smoke test, and its honest limit

`Wave3DiIntegrityTests.PureServiceGraph_ResolvesFromARealServiceProvider` rebuilds the Wave 2/3 graph
with the production implementations and MAUI-free stand-ins where a real adapter needs a head
(`IHistoryRepository` → in-memory, `ISettingsService` → stub, `IDataProvider` → a Manual-reporting
wrapper around the mock provider). It asserts every contract resolves, singletons really are single,
`ILocalizationService` and `IFormatService` are the *same* object (the app's live language switch
depends on it), and one end-to-end cycle runs with keys and provenance intact.

The four Wave 3 contracts (`IUpdateService`, `IManualEntryService`, `IReminderService`,
`IThemeService`) **cannot** be resolved here: their implementations do not exist in this copy and will
need `HttpClient`+`FileSystem`, `FileSystem`, `Plugin.LocalNotification` and `Application.Current`
respectively. A `Skip`-free test states that gap
(`Wave3Contracts_CannotBeResolvedHere_BecauseTheirImplementationsNeedAMauiHead`) so it is visible in
the pass list rather than silently missing. After the merge, the orchestrator's second pass should add
a `PureGraph` registration for `ThemeService` (its only dependency is `ISettingsService`, once the
`Application.Current.RequestedTheme` read is injected behind a seam).

---

## 4. Merge-gate findings (lane 10's second-pass list)

- **Duplicate DI registrations are silent.** MS.DI lets the last one win, so a lane block applied
  twice, or `IDataProvider` re-registered after lane 02's overlay, resolves to the mock with no
  error. `NoServiceInterface_IsRegisteredTwiceInMauiProgram` catches the shape in `MauiProgram.cs`.
- **Marker regions carry prose that repeats the marker text.** Counting string occurrences is wrong;
  the tests count *line-anchored* markers. Whoever writes a merge script should do the same.
- **A lane block can be applied to the wrong region.** The tests assert the DI region stays above
  `builder.Build()` and never contains `ServiceHelper.Initialize`.
- **`Routing.RegisterRoute` for a page that did not land is a runtime navigation failure, not a
  build failure.** Every route in `WAVE3-MERGE.md` §4 must be checked against the page file existing.

---

## 5. Known debt — documented, not hidden

These are real defects or gaps in the current code that lane 10 could not fix inside its ownership
(`Tests/**`, `README.md`, `docs/**` only). Each is written as a passing test where possible, so fixing
the code makes the test's name stale in an obvious way.

1. **`JsonFileStore` treats a corrupt file as EMPTY** (`Infrastructure/Persistence/JsonFileStore.cs`
   ~44–51 and the `LoadObjectAsync` catch). `catch { return new List<T>(); }` means a half-written
   `livora_goals.json` (killed mid-write, disk full, one bad edit) silently becomes "you have no
   goals" — and, through item 2, the app then re-seeds demo content over the user's world. The file is
   not renamed, not logged, not recoverable. **Fix**: keep the last good copy
   (`*.json.bak`) or refuse to load and surface an error state. Untestable here (MAUI `FileSystem` in
   the default ctor path); no fake coverage invented for it.
2. ~~**`DemoDataSeeder` re-seeds when the goal store is empty.**~~ **FIXED during Wave 3 merge**
   (`ISettingsService.DemoDataSeeded` one-shot flag; catalog seeding is additionally idempotent by
   `TitleKey` for installs that predate the flag, since every seeded `Bootcamp` carries a fresh
   `Guid` and upsert-by-id cannot collapse duplicates). `EmptiedGoalStore_DoesNotReseed_OnceSeededFlagIsSet`
   and `CatalogSeeding_IsIdempotentByTitleKey_ForInstallsWithoutTheFlag` now assert the fixed
   behavior. Side effect worth noting: this also contains item 1's worst case — a corrupted
   `livora_goals.json` reads as empty, but an empty store no longer re-seeds demo content over it.
3. **`CultureBootstrap`'s trimmed-ICU fallback cannot be exercised on this machine.** Windows here has
   full `fa-IR` data, so the `fa-IR → fa → invariant` chain is covered only by its shape (LCID ∈
   {1065, 41, 127}) and by the fact that touching the properties does not throw. The Android/iOS
   behavior is **Unverified**.
4. **No launch/UI smoke test exists.** The "application launches" line of the definition of done
   currently rests on manual runs; CI builds the app and runs the pure-layer tests, nothing in between.
   Anything requiring `MauiProgram.CreateMauiApp()` is out of reach of this project by design.
5. **Namespace/folder drift**: `Infrastructure/Settings/PreferencesSettingsService.cs` declares
   `namespace LIVORA.Infrastructure.Localization`. It compiles and resolves, but it misleads
   navigation and any future convention test. Cosmetic, cheap to fix, and currently the only such case.
6. **`NormalizedDay.Completeness()` samples 8 of 13 fields** — bedtime, wake and consistency are not in
   the denominator, so a day can read 100% complete with no bedtime at all. `UserStateService` also
   mixes the injected clock with `DateTime.Today` inside `SleepDaysSinceFresh`, which is why the state
   tests pin "today" to the real `DateTime.Today`. Both are correctness-adjacent to the honesty model
   and worth an explicit decision.
7. **`Rule.HighStressReduce`** appears in `Bootcamp.AdaptationRuleKeys` but no such rule exists in
   `RuleEngine` (the real ones are `Rule.HighStress` / `Rule.HighStressScreens`). Harmless today —
   the adapter filters on adjustments, not names — but it is a dead identifier a future lane could
   code against.
8. **README/status drift is a recurring failure mode**, and README claims are not executable. The
   key-count claim (was 325, actually 333) is now backed by
   `KeyCount_IsAtLeastTheDocumentedBaseline`, which converts the number in the docs into a floor the
   build enforces.
9. **Persian `Plan.Item.Habit` is literally `{0}`** (the habit's own name), which is right for a
   user-titled habit but means the plan row carries no context in either language. The Latin-only
   allowlist in `ResxIntegrityTests` names it explicitly, together with `Health.HRV` and
   `Onboarding.Language.English`, so the exception cannot quietly grow.

---

## 6. Unverified claims (do not repeat them as facts)

- Any Android/iOS/Windows **runtime** behavior of Wave 3 features: nothing in this wave was run on a
  device or in the built app. The MAUI Windows app build is green (0 warnings, 0 errors) and the test
  suite is green; that is the extent of the evidence.
- Whether lane 09's `Plugin.LocalNotification` scheduling and Android permission/manifest requirements
  actually deliver a notification — the plugin's device behavior cannot be observed from CI here.
- Whether the SkiaSharp chart renders Persian glyphs correctly on a trimmed-ICU device (font loading
  through `SKTypeface.FromStream` over embedded assets is a runtime path).
- Whether `api.github.com` rate limits behave as lane 01's `RateLimited` status assumes in the field.
- Jalali output beyond the one pinned date (`11 Sep 2026 → ۲۰ شهریور`, day-of-week `جمعه`): correct
  for that date on this machine's ICU version, not sampled across a year.
