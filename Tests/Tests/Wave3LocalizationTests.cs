using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Localization;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests;

/// <summary>In-memory stand-in for the MAUI Preferences-backed settings service.</summary>
internal sealed class StubSettings : ISettingsService
{
    public AppLanguage PreferredLanguage { get; set; } = AppLanguage.Persian;
    public bool LanguageExplicitlySet { get; set; } = true;
    public bool OnboardingCompleted { get; set; }
    public string? ProfileId { get; set; }
    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;
    public string? LastSeenVersion { get; set; }
    public bool DemoDataSeeded { get; set; }
}

/// <summary>
/// The Wave 3 localization/formatting contract exercised through the REAL
/// Infrastructure/Localization implementation (MAUI-free, so the plain-net10.0 test project
/// compiles it — see Tests/LIVORA.Tests.csproj). LocalizationService mutates process-global culture
/// state (AppResources.Culture, CultureInfo.Current*), so this class is serialized in its own
/// collection and restores Persian on dispose.
/// </summary>
[Collection("LocalizationState")]
public class LocalizationAndFormattingTests : IDisposable
{
    private static bool IsPersianDigit(char c) => c is >= '۰' and <= '۹';

    private readonly LocalizationService _svc;

    public LocalizationAndFormattingTests()
    {
        // NullLogger, not null!: T() logs before degrading on a bad format string.
        _svc = new LocalizationService(new StubSettings(), NullLogger<LocalizationService>.Instance);
    }

    public void Dispose() => _svc.SetLanguage(AppLanguage.Persian);

    private LocalizationService Fa()
    {
        _svc.SetLanguage(AppLanguage.Persian);
        return _svc;
    }

    private LocalizationService En()
    {
        _svc.SetLanguage(AppLanguage.English);
        return _svc;
    }

    private static LocalizationService Fresh(AppLanguage lang) =>
        new(new StubSettings { LanguageExplicitlySet = true, PreferredLanguage = lang },
            NullLogger<LocalizationService>.Instance);

    // ---- product default + live switching ---------------------------------

    [Fact]
    public void FirstLaunch_DefaultsToPersian_AndPersistsTheChoice()
    {
        var settings = new StubSettings { LanguageExplicitlySet = false };
        var svc = new LocalizationService(settings, NullLogger<LocalizationService>.Instance);

        Assert.Equal(AppLanguage.Persian, svc.CurrentLanguage);
        Assert.True(svc.IsRightToLeft, "Persian is the first-class language: RTL out of the box");
        Assert.True(settings.LanguageExplicitlySet, "the default must be recorded so a later switch sticks");
        Assert.Equal(AppLanguage.Persian, settings.PreferredLanguage);
    }

    [Fact]
    public void ExplicitPreference_IsRespected_AndEnglishIsLtr()
    {
        var svc = Fresh(AppLanguage.English);
        Assert.Equal(AppLanguage.English, svc.CurrentLanguage);
        Assert.False(svc.IsRightToLeft);
    }

    [Fact]
    public void SetLanguage_PersistsAndRaisesLanguageChangedOnce()
    {
        var settings = new StubSettings();
        var svc = new LocalizationService(settings, NullLogger<LocalizationService>.Instance);
        var raised = 0;
        svc.LanguageChanged += () => raised++;

        svc.SetLanguage(AppLanguage.English);
        Assert.Equal(AppLanguage.English, settings.PreferredLanguage);
        Assert.Equal(1, raised);

        // Idempotent: the same language again must not re-raise (no pointless re-render storm).
        svc.SetLanguage(AppLanguage.English);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SetLanguage_RaisesPropertyChanged_ForBindingTree()
    {
        var svc = Fresh(AppLanguage.Persian);
        string? changed = null;
        svc.PropertyChanged += (_, e) => changed = e.PropertyName;
        svc.SetLanguage(AppLanguage.English);
        Assert.NotNull(changed);
    }

    [Fact]
    public void CultureName_FollowsLanguage()
    {
        Assert.Equal("fa-IR", Fa().CultureName);
        Assert.Equal("en-US", En().CultureName);
    }

    [Fact]
    public void RtlFollowsTheChosenLanguage_NotTheIcuData()
    {
        // Documented invariant: RTL is a product decision, not an ICU-data question.
        var svc = Fa();
        Assert.True(svc.IsRightToLeft);
        Assert.Equal(AppLanguage.Persian, svc.CurrentLanguage);
    }

    [Fact]
    public void LanguageHook_IsNotifiedOnSwitch()
    {
        // The TrExtension/Live labels ride on this static hook; a silent service break is a
        // UI-freeze-in-Persian bug, so pin it directly.
        var called = 0;
        Action handler = () => called++;
        LanguageHook.Subscribe(handler);
        var svc = Fresh(AppLanguage.Persian);
        svc.SetLanguage(AppLanguage.English);
        Assert.Equal(1, called);
        GC.KeepAlive(handler);
    }

    // ---- key lookup + the [missing] fallback ------------------------------

    [Fact]
    public void KnownKey_ResolvesInBothLanguages()
    {
        Assert.Equal("Save", En()["Common.Save"]);
        var faSave = Fa()["Common.Save"];
        Assert.NotEqual("Save", faSave);
        Assert.True(Wave3Harness.HasPersianCodepoint(faSave), $"Common.Save must be Persian, got {faSave}");
    }

    [Fact]
    public void MissingKey_RendersBracketedKey_NotEmpty_NotThrow()
    {
        const string key = "Lane99.Key.NobodyWrote";
        Assert.Equal($"[{key}]", Fa()[key]);
        Assert.Equal($"[{key}]", En()[key]);
        Assert.Equal($"[{key}]", Fa().T(key));
    }

    [Fact]
    public void MissingKey_FallbackSurvivesALanguageSwitch()
    {
        var svc = En();
        Assert.Equal("[Not.A.Key.At.All]", svc["Not.A.Key.At.All"]);
        svc.SetLanguage(AppLanguage.Persian);
        Assert.Equal("[Not.A.Key.At.All]", svc["Not.A.Key.At.All"]);
    }

    [Fact]
    public void EveryShippedKey_ResolvesToNonEmptyText()
    {
        // Guards the invisible-label failure mode: an empty <value> in resx would render nothing
        // on screen and never show the [key] breadcrumb.
        var pair = Wave3Harness.ReadBoth();
        if (pair is null) return;                       // resx not next to the sources — skip
        foreach (var key in pair.En.Keys)
        {
            Assert.False(pair.Fa.TryGetValue(key, out var fa) && string.IsNullOrWhiteSpace(fa),
                $"FA value is empty for {key}");
        }
    }

    [Fact]
    public void T_AppliesFormatArgs_InTheActiveCulture()
    {
        // Raw T() substitutes args verbatim — Persian DIGITS are Duration()'s job (it localizes
        // before formatting), so passing ints here yields ASCII digits on purpose. Pinned because a
        // "helpful" T() that digit-localized args would double-convert inside Duration().
        var fa = Fa().T("Format.DurationFullFa", 7, 40);
        Assert.Contains("ساعت", fa);
        Assert.Contains("دقیقه", fa);
        Assert.DoesNotContain("{", fa);
        Assert.Equal("7 ساعت و 40 دقیقه", fa);
    }

    [Fact]
    public void T_EnglishTemplate_FormatsWithoutLocalizingDigits()
        => Assert.Equal("7h 40m", En().T("Format.DurationFullFa", 7, 40));

    [Fact]
    public void T_WithNoArgs_ReturnsTheRawTemplate()
    {
        var template = Fa().T("Format.DurationMinutesFa");
        Assert.Contains("{0}", template);       // no substitution happened
        Assert.Contains("دقیقه", template);
    }

    [Fact]
    public void T_TooFewArgs_DoesNotThrow_DegradesToTheTemplate()
    {
        // Insight.Reason.SleepHours needs {0} and {1}; string.Format would throw
        // FormatException. The service must swallow it and show the template instead — a broken
        // format string may never take the Today page down.
        var text = Fa().T("Insight.Reason.SleepHours", 430);
        Assert.Contains("{1}", text);
    }

    // ---- Persian digits / separators / percent ---------------------------

    [Theory]
    [InlineData(0L, "۰")]
    [InlineData(7L, "۷")]
    public void Number_SinglePersianDigits(long value, string expected)
        => Assert.Equal(expected, Fa().Number(value));

    [Fact]
    public void Number_ThousandsGroupingIsLocalized()
    {
        // Separator glyph is ICU-version dependent ("," vs "٬"), so assert the invariants:
        // every digit Persian, the right digits in order, exactly two separators in a 7-digit number.
        var fa = Fa().Number(1234567);
        Assert.All(fa.Where(char.IsDigit), c => Assert.True(IsPersianDigit(c), "no ASCII digits in fa output"));
        Assert.Equal("۱۲۳۴۵۶۷", new string(fa.Where(IsPersianDigit).ToArray()));
        Assert.Equal(2, fa.Count(c => c == ',' || c == '٬'));
        Assert.Equal("1,234,567", En().Number(1234567));
    }

    [Fact]
    public void Number_NegativeValue_KeepsItsSign()
    {
        Assert.Contains("-", Fa().Number(-250));
        Assert.Equal("-250", En().Number(-250));
    }

    [Theory]
    [InlineData(0.5, "٪۵۰")]
    [InlineData(1.0, "٪۱۰۰")]
    [InlineData(0.0, "٪۰")]
    public void Percent_PersianDigitsWithPersianSign(double fraction, string expected)
        => Assert.Equal(expected, Fa().Percent(fraction));

    [Fact]
    public void Percent_LatinUsesTrailingSign()
    {
        Assert.Equal("50%", En().Percent(0.5));
        Assert.Equal("100%", En().Percent(1));
        Assert.Equal("0%", En().Percent(0));
    }

    [Theory]
    [InlineData(-0.4, "0%")]
    [InlineData(3.0, "100%")]
    public void Percent_IsClampedToItsDocumentedDomain(double outOfRange, string expected)
    {
        // Contract: "0..1 fraction as percent". Out-of-range input must not print -40% or 300%.
        Assert.Equal(expected, En().Percent(outOfRange));
    }

    [Theory]
    [InlineData(0.424, "42%")]
    [InlineData(0.425, "42%")]     // midpoint: Math.Round is banker's — pinned, not accidental
    [InlineData(0.4251, "43%")]
    public void Percent_RoundsToWholePoints(double fraction, string expected)
        => Assert.Equal(expected, En().Percent(fraction));

    // ---- durations --------------------------------------------------------

    [Theory]
    [InlineData(7, 40, "7h 40m")]
    [InlineData(1, 0, "1h")]
    [InlineData(0, 40, "0h 40m")]
    [InlineData(2, 90, "3h 30m")]
    public void Duration_EnglishShape(double h, double m, string expected)
        => Assert.Equal(expected, En().Duration(h, m));

    [Theory]
    [InlineData(465, "۷ ساعت و ۴۵ دقیقه")]
    [InlineData(60, "۱ ساعت و ۰ دقیقه")]
    [InlineData(45, "۴۵ دقیقه")]
    [InlineData(0, "۰ دقیقه")]
    [InlineData(2, "۲ دقیقه")]
    public void DurationFromMinutes_PersianUsesTheFaTemplates(int minutes, string expected)
        => Assert.Equal(expected, Fa().DurationFromMinutes(minutes));

    [Fact]
    public void DurationFromMinutes_LessThanAnHourStillNamesHours_InEnglish()
    {
        Assert.Equal("0h 45m", En().DurationFromMinutes(45));
        Assert.Equal("2h 5m", En().DurationFromMinutes(125));
    }

    [Fact]
    public void Duration_PersianHasNoLatinDigitsAnywhere()
    {
        foreach (var m in new[] { 0, 5, 40, 95, 600, 1439 })
        {
            var fa = Fa().DurationFromMinutes(m);
            Assert.DoesNotContain(fa, c => char.IsAsciiDigit(c));
            Assert.True(Wave3Harness.HasPersianCodepoint(fa));
        }
    }

    // ---- clocks, dates ----------------------------------------------------

    [Theory]
    [InlineData(23, 30, "23:30")]
    [InlineData(0, 0, "00:00")]
    [InlineData(7, 5, "07:05")]
    public void Time_IsTwentyFourHourPadded(int h, int m, string expected)
        => Assert.Equal(expected, En().Time(new TimeSpan(h, m, 0)));

    [Fact]
    public void Time_PersianDigitsOnly()
        => Assert.Equal("۰۷:۰۵", Fa().Time(new TimeSpan(7, 5, 0)));

    [Fact]
    public void Time_PastMidnoonWrapsRatherThanThrowing()
        => Assert.Equal("01:00", En().Time(TimeSpan.FromHours(25)));

    [Fact]
    public void LongDate_EnglishIsWeekdayMonthDay()
        => Assert.Equal("Friday, September 11", En().LongDate(new DateTime(2026, 9, 11)));

    [Fact]
    public void LongDate_PersianIsJalaliWithPersianDigits()
    {
        var text = Fa().LongDate(new DateTime(2026, 9, 11));
        Assert.DoesNotContain(text, c => char.IsAsciiDigit(c));
        // 11 Sep 2026 == 20 Shahrivar 1405 in the Persian calendar.
        Assert.Contains("۲۰", text);
        Assert.Contains("شهریور", text);
    }

    [Fact]
    public void ShortDate_BothLanguages_AreCompactAndNonEmpty()
    {
        Assert.Equal("Sep 11", En().ShortDate(new DateTime(2026, 9, 11)));
        var fa = Fa().ShortDate(new DateTime(2026, 9, 11));
        Assert.False(string.IsNullOrWhiteSpace(fa));
        Assert.DoesNotContain(fa, c => char.IsAsciiDigit(c));
    }

    // ---- fonts ------------------------------------------------------------

    [Theory]
    [InlineData(AppLanguage.Persian, FontFamilies.PersianBody, FontFamilies.PersianMedium, FontFamilies.PersianBold)]
    [InlineData(AppLanguage.English, FontFamilies.LatinBody, FontFamilies.LatinMedium, FontFamilies.LatinBold)]
    public void Fonts_FollowTheActiveLanguage(AppLanguage lang, string body, string medium, string bold)
    {
        var svc = Fresh(lang);
        Assert.Equal(body, svc.BodyFontFamily);
        Assert.Equal(medium, svc.MediumFontFamily);
        Assert.Equal(bold, svc.BoldFontFamily);
    }

    [Fact]
    public void PersianFontIsVazirmatn_SoPersianNeverFallsBackToALatinOnlyFace()
    {
        Assert.Contains("Vazirmatn", FontFamilies.PersianBody);
        Assert.Contains("Vazirmatn", FontFamilies.PersianBold);
        Assert.Contains("OpenSans", FontFamilies.LatinBody);
    }

    // ---- CultureBootstrap -------------------------------------------------

    [Fact]
    public void CultureBootstrap_ResolvesBothCultures_WithoutThrowing()
    {
        // A throwing static initializer would take the whole app down on first touch on
        // trimmed-ICU platforms; touching the properties at all is the regression guard.
        Assert.NotNull(CultureBootstrap.English);
        Assert.NotNull(CultureBootstrap.Persian);
    }

    [Fact]
    public void CultureBootstrap_FallbackChainOnlyEverYieldsTheDocumentedCultures()
    {
        // Persian: fa-IR (1065) -> neutral fa (41) -> invariant (127). Anything else is a bug.
        Assert.Contains(CultureBootstrap.Persian.LCID, new[] { 1065, 41, 127 });
        Assert.Contains(CultureBootstrap.English.LCID, new[] { 1033, 9, 127 });
    }

    [Fact]
    public void NativeDigits_AreTenSequentialGlyphsEndingInNine()
    {
        var digits = CultureBootstrap.Persian.NumberFormat.NativeDigits;
        Assert.Equal(10, digits.Length);
        Assert.True(digits[9] is "۹" or "9", $"unexpected nine glyph {digits[9]}");
    }

    // ---- Wave 3 contract surface (no implementations in this copy) ---------

    [Fact]
    public void UpdateInfo_Defaults_AreHonestUnknown_NotUpToDate()
    {
        var info = new UpdateInfo { CurrentVersion = "1.1" };
        Assert.Equal(UpdateCheckStatus.Unknown, info.Status);
        Assert.Null(info.LatestVersion);
        Assert.False(info.IsNewerThanFeed);
        Assert.False(info.FromCache);
        Assert.Empty(info.Notes);
        Assert.Empty(info.DownloadLinks);
        Assert.Null(info.CheckedAtUtc);
    }

    [Fact]
    public void UpdateInfo_NewerThanFeed_IsADistinctStateFromUpToDate()
    {
        // A CI/dev build ahead of the feed must never be reported as "you are current".
        var info = new UpdateInfo
        {
            CurrentVersion = "99.0.0-rc.7",
            LatestVersion = "1.1",
            Status = UpdateCheckStatus.NewerThanFeed,
            IsNewerThanFeed = true,
        };
        Assert.NotEqual(UpdateCheckStatus.UpToDate, info.Status);
        Assert.True(info.IsNewerThanFeed);
    }

    [Fact]
    public void UpdateInfo_CachedAnswerCarriesItsProvenance()
    {
        var info = new UpdateInfo
        {
            CurrentVersion = "1.1",
            LatestVersion = "1.2",
            Status = UpdateCheckStatus.UpdateAvailable,
            FromCache = true,
            CheckedAtUtc = new DateTime(2026, 9, 12, 8, 0, 0, DateTimeKind.Utc),
        };
        Assert.True(info.FromCache);
        Assert.NotNull(info.CheckedAtUtc);
        Assert.Equal(DateTimeKind.Utc, info.CheckedAtUtc.Value.Kind);
    }

    [Fact]
    public void UpdateCheckStatus_KeepsItsPersistedNumericOrder()
    {
        // These ints land in update_feed.json; renumbering silently corrupts old caches.
        Assert.Equal(0, (int)UpdateCheckStatus.Unknown);
        Assert.Equal(1, (int)UpdateCheckStatus.UpdateAvailable);
        Assert.Equal(2, (int)UpdateCheckStatus.UpToDate);
        Assert.Equal(3, (int)UpdateCheckStatus.NewerThanFeed);
        Assert.Equal(4, (int)UpdateCheckStatus.NoConnection);
        Assert.Equal(5, (int)UpdateCheckStatus.RateLimited);
        Assert.Equal(6, (int)UpdateCheckStatus.Error);
        Assert.Equal(7, (int)UpdateCheckStatus.Disabled);
    }

    [Fact]
    public void NotificationGrantState_NeverDefaultsToAllowed()
    {
        Assert.Equal(NotificationGrantState.Unknown, default);
        Assert.Equal(1, (int)NotificationGrantState.Allowed);
        Assert.Equal(5, (int)NotificationGrantState.Unsupported);
    }

    [Fact]
    public void ReminderSetting_DefaultsAreTheDocumentedValues()
    {
        var r = new ReminderSetting { Id = "r1", Kind = "log", TextKey = "Reminders.Log" };
        Assert.True(r.Enabled);                    // documented default
        Assert.Equal(0b1111111, r.DaysMask);       // all seven days, bit0 = Sunday
        Assert.Equal(new TimeSpan(20, 0, 0), r.TimeOfDay);
        Assert.Null(r.TargetId);
        Assert.Empty(r.TextArgs);
    }

    [Fact]
    public void ReminderSetting_DaysMaskBit0IsSunday()
    {
        // The mask contract: bit0 = Sunday (DayOfWeek order). Tuesday-only = 1 << 2.
        var tuesday = new ReminderSetting
        {
            Id = "t", Kind = "habit", TextKey = "K", DaysMask = 1 << (int)DayOfWeek.Tuesday,
        };
        Assert.Equal(4, tuesday.DaysMask);
        Assert.NotEqual(0, tuesday.DaysMask & (1 << (int)DayOfWeek.Tuesday));
        Assert.Equal(0, tuesday.DaysMask & (1 << (int)DayOfWeek.Saturday));
    }

    [Fact]
    public void ManualEntryDraft_IsEmptyOnlyWhenNoMetricWasEntered()
    {
        Assert.True(new ManualEntryDraft { Date = new DateTime(2026, 9, 11) }.IsEmpty);
        Assert.False(new ManualEntryDraft { Date = new DateTime(2026, 9, 11), Steps = 0 }.IsEmpty);
        Assert.False(new ManualEntryDraft { Date = new DateTime(2026, 9, 11), Mood = 0 }.IsEmpty);
        // A note alone is not data: IsEmpty must ignore it, or "saved" becomes a lie.
        Assert.True(new ManualEntryDraft { Date = new DateTime(2026, 9, 11), Note = "meh" }.IsEmpty);
    }

    [Fact]
    public void ManualEntryDraft_SavedAtUtcIsOwnedByTheStore()
    {
        var draft = new ManualEntryDraft { Date = new DateTime(2026, 9, 11), SleepMinutes = 430 };
        Assert.Null(draft.SavedAtUtc);
        Assert.Equal(430, draft.SleepMinutes);
        Assert.Null(draft.Steps);      // per-metric nulls: "not provided", never 0
    }

    [Fact]
    public void ThemeMode_SystemIsTheZeroValue_AndTheContractIsPlatformFree()
    {
        Assert.Equal(0, (int)ThemeMode.System);
        Assert.Equal(1, (int)ThemeMode.Light);
        Assert.Equal(2, (int)ThemeMode.Dark);
        Assert.Equal(0, (int)AppThemeKind.Light);
        Assert.Equal(1, (int)AppThemeKind.Dark);

        // IThemeService must speak domain enums (Microsoft.Maui types would break this project).
        foreach (var prop in typeof(IThemeService).GetProperties())
            Assert.DoesNotContain("Maui", prop.PropertyType.Namespace ?? "");
    }

    [Fact]
    public void IThemeService_MembersMatchTheFrozenContract()
    {
        var names = typeof(IThemeService)
            .GetMembers()
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_") && !n.StartsWith("set_") && !n.StartsWith("add_") && !n.StartsWith("remove_"))
            .Distinct()
            .ToList();
        Assert.Equal(new[] { "Apply", "IsDark", "Mode", "ResolvedTheme", "SetMode", "ThemeChanged" },
            names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void SettingsService_Wave3Members_ExistAndAreNullableFriendly()
    {
        var props = typeof(ISettingsService).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("ThemeMode", props);
        Assert.Contains("LastSeenVersion", props);
        Assert.Contains("LanguageExplicitlySet", props);

        var svc = Fresh(AppLanguage.Persian);
        Assert.Equal(AppLanguage.Persian, svc.CurrentLanguage);
    }
}
