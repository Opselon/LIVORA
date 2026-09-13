using System.Globalization;
using LIVORA.Application.Abstractions;

namespace LIVORA.Presentation;

// =============================================================================
// Lane 03 — every sentence the Log feature shows, resolved in exactly ONE place.
//
// LogViewModel (the tab: header, skeleton, empty state, chart caption) and
// LogEntryViewModel (the editor: sections, hints, validation, delete) are two
// types, so each raises its own localized strings. When the same key surfaced
// twice, the two classes had already drifted apart. This static resolver owns
// the keys, the fallbacks and the argument counts; a VM exposes a property
// that calls it. Both sides are therefore right by construction.
//
// Fallback rule: an empty translation collapses to the English fallback passed
// in. A UI must never show a raw key. Validation messages follow the same rule
// (LogEntryRules emits the key, Validation() below resolves it) — one key per
// reason, in both languages, and Application/ never holds prose at all.
// =============================================================================

/// <summary>
/// Which surface a heading belongs to. The wording differs on purpose: the pushed page opens on
/// one day's report (the day's own long date is its title), while the tab hosts the editor inline
/// and needs a section heading that does not compete with the tab title above it.
/// </summary>
public enum LogHeadingStyle { Page, Tab }

/// <summary>Shared localized strings for the Log feature (tab + pushed editor).</summary>
public static class LogUiStrings
{
    private static ILocalizationService? Loc => ServiceHelper.TryGet<ILocalizationService>();

    /// <summary>Resolves one key and always returns displayable text (never blank, never the key).</summary>
    private static string T(string key, string fallbackEnglish)
    {
        var loc = Loc;
        if (loc is null) return fallbackEnglish;
        var v = loc[key];
        // LocalizationService answers "[Key]" for an unregistered key — that is a build-time bug
        // report, not user-facing text, so it degrades to the fallback here too.
        if (string.IsNullOrWhiteSpace(v) || v == "[" + key + "]") return fallbackEnglish;
        return v;
    }

    /// <summary>
    /// "{0} nights with data · {1} logged by you · {2} sample". The chart's honesty line: the three
    /// numbers add up ({1} + {2} == {0}), nights with no value are simply absent, and sample data
    /// never borrows the word "logged by you".
    /// </summary>
    public static string Caption(int nights, int manual, int sample, IFormatService format)
        => T("Log.Chart.Caption", "{0} nights with data · {1} logged by you · {2} sample")
            .Trim()
            .Replace("{0}", format.Number(nights))
            .Replace("{1}", format.Number(manual))
            .Replace("{2}", format.Number(sample));

    /// <summary>Compact axis tick: "{0}h" / "{0} ساعت" — short enough to fit the chart gutter.</summary>
    public static string AxisHour(int hours, IFormatService format)
        => T("Log.Chart.AxisHour", "{0}h").Replace("{0}", format.Number(hours));

    /// <summary>"Your normal: {0}" — shown only when the dashed baseline line is actually drawn.</summary>
    public static string Baseline(string duration)
        => T("Log.Chart.Baseline", "Your normal: {0}").Replace("{0}", duration);

    /// <summary>The day-heading wording for the pushed page or the inline tab section.</summary>
    public static string CheckInTitle(LogHeadingStyle style)
        => style == LogHeadingStyle.Page
            ? T("Log.CheckIn.Title", "Daily check-in")
            : T("Log.CheckIn.Section", "Log a day");

    /// <summary>Page title of the Log tab.</summary>
    public static string Title => T("Log.Title", "Log");

    /// <summary>Subtitle of the Log tab (states what the tab is, not what it promises).</summary>
    public static string Subtitle => T("Log.Subtitle", "Your own numbers, one day at a time");

    /// <summary>"{0} days logged" counter chip.</summary>
    public static string EntryCount(int days, IFormatService format)
        => T("Log.EntryCount", "{0} days logged").Replace("{0}", format.Number(days));

    /// <summary>Chart card heading.</summary>
    public static string ChartTitle => T("Log.Chart.Title", "Last 14 nights");

    /// <summary>Empty/recent-list headings.</summary>
    public static string RecentTitle => T("Log.Recent.Title", "Recent check-ins");

    /// <summary>Recent list is empty — states the fact, not a promise.</summary>
    public static string EmptyText => T("Log.Recent.Empty", "No check-ins yet");

    /// <summary>Second line of the empty state: what to do about it.</summary>
    public static string EmptySub
        => T("Log.Recent.EmptySub", "Log a night in the form above and it will show up here.");

    /// <summary>Chart card has no sleep values at all in the window.</summary>
    public static string ChartNoData
        => T("Log.Chart.NoData", "No sleep values yet — log a night to start the chart.");

    /// <summary>Painted by the Skia view when it has nothing to draw or its drawing faulted.</summary>
    public static string ChartPlaceholder
        => T("Log.Chart.Placeholder", "Chart unavailable");

    /// <summary>Legend entry for hollow + hatched bars.</summary>
    public static string LegendManual => T("Log.Chart.LegendManual", "You logged it");

    /// <summary>Legend entry for soft-filled bars (sample data).</summary>
    public static string LegendMock => T("Log.Chart.LegendMock", "Sample data");

    /// <summary>Legend entry for the dashed personal-normal line.</summary>
    public static string LegendBaseline => T("Log.Chart.LegendBaseline", "Your normal");

    /// <summary>Product law: user input is self-reported, never measured.</summary>
    public static string HonestyNote
        => T("Log.HonestyNote", "You entered these values yourself. LIVORA cannot verify them — they are a self-report, not a measurement.");

    /// <summary>Empty-field placeholders (examples, not implied defaults).</summary>
    public static string HintSleep => T("Log.Hint.Sleep", "e.g. 7");

    /// <summary>Minutes cell placeholder.</summary>
    public static string HintMinutes => T("Log.Hint.Minutes", "e.g. 30");

    /// <summary>Steps cell placeholder.</summary>
    public static string HintSteps => T("Log.Hint.Steps", "e.g. 8500");

    /// <summary>Active-minutes cell placeholder.</summary>
    public static string HintActive => T("Log.Hint.Active", "e.g. 45");

    /// <summary>Row action + confirmation chrome.</summary>
    public static string DeleteEntry => T("Log.DeleteEntry", "Delete");

    /// <summary>Confirm-delete title.</summary>
    public static string DeleteConfirmTitle => T("Log.DeleteConfirm.Title", "Delete this check-in?");

    /// <summary>Confirm-delete body for one day.</summary>
    public static string DeleteConfirmBody(string date)
        => T("Log.DeleteConfirm.Body", "The values you logged for {0} will be removed from this device.").Replace("{0}", date);

    /// <summary>Inline confirmation after a save.</summary>
    public static string Saved(string date)
        => T("Log.Saved", "Saved your check-in for {0}.").Replace("{0}", date);

    /// <summary>Inline confirmation after a delete.</summary>
    public static string Deleted(string date)
        => T("Log.Deleted", "Removed your check-in for {0}.").Replace("{0}", date);

    /// <summary>Honest failure of a save — never dressed up as success.</summary>
    public static string SaveFailed
        => T("Log.SaveFailed", "Couldn't save. Your values are still on screen — try again.");

    /// <summary>Honest failure of a delete.</summary>
    public static string DeleteFailed
        => T("Log.DeleteFailed", "Couldn't delete this check-in. Try again.");

    /// <summary>The selected day already holds stored values (edit/remove affordance).</summary>
    public static string ExistingEntry
        => T("Log.ExistingEntry", "You already logged this day. Your saved values are loaded — edit them or remove the entry.");

    /// <summary>
    /// Resolves a LogEntryRules validation key to a sentence. The rules layer emits a KEY only
    /// (Application never holds prose); the English safety net lives here so a missing translation
    /// degrades to readable text instead of a raw key.
    /// </summary>
    public static string Validation(string key) => T(key, EnglishFallback(key));

    /// <summary>Only used when the resx has no value yet — the real strings ship in both languages.</summary>
    private static string EnglishFallback(string key) => key switch
    {
        "Log.Error.FutureDate" => "You can only log a day that has already happened.",
        "Log.Error.DateTooOld" => "Only the last 60 days can be logged.",
        "Log.Error.SleepRange" => "Sleep must be between 0 and 24 hours.",
        "Log.Error.StepsRange" => "Steps must be between 0 and 100,000.",
        "Log.Error.ActiveRange" => "Active minutes must be between 0 and 1,440.",
        "Log.Error.SliderRange" => "Ratings must stay between 0 and 100%.",
        "Log.Error.NoteTooLong" => "Keep the note under 240 characters.",
        "Log.Error.NothingToSave" => "Nothing to save yet; enter at least one value.",
        _ => key,
    };

    /// <summary>
    /// Day chip for a row: Today / Yesterday / "{0} days ago". The relative word wins over the
    /// date so a list of recent nights reads in the order a person thinks about them.
    /// </summary>
    public static string DayLabel(int daysAgo, IFormatService format)
        => daysAgo switch
        {
            0 => T("Log.Today", "Today"),
            1 => T("Log.Yesterday", "Yesterday"),
            _ => T("Log.NDaysAgo", "{0} days ago").Replace("{0}", format.Number(daysAgo)),
        };

    /// <summary>Sleep cell for a row that carries no sleep value (null-safe on purpose).</summary>
    public static string NotSet => T("Log.NotSet", "not set");

    /// <summary>CTA in the empty state — hand the user back to the editor above it.</summary>
    public static string NewEntry => T("Log.NewEntry", "Log today");

    /// <summary>Back affordance on the pushed editor page.</summary>
    public static string BackToLog => T("Log.BackToLog", "Close");

    // ---- Editor labels (owned here so both surfaces share one wording) --------

    public static string DateLabel => T("Log.Date", "Date");
    public static string SectionSleep => T("Log.Section.Sleep", "Sleep");
    public static string SectionActivity => T("Log.Section.Activity", "Activity");
    public static string SectionFeelings => T("Log.Section.Feelings", "How you feel");
    public static string Hours => T("Log.Hours", "Hours");
    public static string Minutes => T("Log.Minutes", "Minutes");
    public static string Steps => T("Log.Steps", "Steps");
    public static string ActiveMinutes => T("Log.ActiveMinutes", "Active minutes");
    public static string SleepQuality => T("Log.SleepQuality", "Sleep quality");
    public static string Mood => T("Log.Mood", "Mood");
    public static string Energy => T("Log.Energy", "Energy");
    public static string Stress => T("Log.Stress", "Stress");
    public static string NoteLabel => T("Log.Note.Label", "Note");
    public static string OptionalHint => T("Log.OptionalHint", "Optional — anything worth remembering");
    public static string Save => T("Log.Save", "Save check-in");
    public static string Clear => T("Log.Clear", "Clear");

    /// <summary>"Nothing to save" — shown when every value is still blank.</summary>
    public static string ErrorNothingToSave
        => T("Log.Error.NothingToSave", "Nothing to save yet; enter at least one value.");

    /// <summary>Converts stored minutes into the two editor cells (blank when no value).</summary>
    public static (string Hours, string Minutes) SleepCells(int? totalMinutes)
        => totalMinutes is { } m
            ? ((m / 60).ToString(CultureInfo.InvariantCulture), (m % 60).ToString(CultureInfo.InvariantCulture))
            : (string.Empty, string.Empty);

    /// <summary>Text cell for a nullable integer editor field (blank = "not provided").</summary>
    public static string IntCell(int? value)
        => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
