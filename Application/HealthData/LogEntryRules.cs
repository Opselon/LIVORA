using System.Globalization;
using LIVORA.Application.Abstractions;

namespace LIVORA.Application.HealthData;

// =============================================================================
// Daily check-in rules (lane 03) — pure validation, defaults and clamping for
// ManualEntryDraft. Deliberately MAUI-free and side-effect-free so the plain
// test project compiles it directly (lane 10: tests/LogEntryRulesTests.cs).
//
// The UI clamps input live; this is the last gate before anything is persisted.
// Output never contains prose: validation results are localization KEYS.
// =============================================================================

/// <summary>Outcome of <see cref="LogEntryRules.Validate"/> — machine state + key, never a sentence.</summary>
public enum LogEntryValidity
{
    /// <summary>Nothing entered and nothing stored — a silent no-op is correct here.</summary>
    NothingToSave,
    /// <summary>One or more fields outside their allowed range — show <see cref="LogEntryValidation.MessageKey"/>.</summary>
    Invalid,
    /// <summary>Within range, safe to persist.</summary>
    Valid,
}

/// <summary>Validation verdict for one draft. MessageKey resolves in the UI layer only.</summary>
public sealed record LogEntryValidation(LogEntryValidity Status, string? MessageKey = null);

/// <summary>Clamped, locale-independent field bounds for the check-in editor.</summary>
public static class LogEntryLimits
{
    /// <summary>Sleep is minutes-per-night: 0..24h.</summary>
    public const int MaxSleepMinutes = 24 * 60;
    /// <summary>A day cannot hold more steps than this stays plausible (product rule, not physics).</summary>
    public const int MaxSteps = 100_000;
    /// <summary>Minutes in a day.</summary>
    public const int MaxActiveMinutes = 1_440;
    /// <summary>Slider values are normalized 0..1 fractions.</summary>
    public const double MinFraction = 0d;
    public const double MaxFraction = 1d;
    /// <summary>How far back a check-in may date (clock drift / vacation catch-up grace).</summary>
    public const int MaxAgeDays = 60;
    /// <summary>Future dates are never loggable — you can't report a night that hasn't happened.</summary>
    public const int MaxFutureDays = 0;
    /// <summary>Free-text note cap (kept short; notes sync everywhere).</summary>
    public const int MaxNoteLength = 240;
}

/// <summary>
/// The single source of truth for what a manual check-in may contain. Every method is pure:
/// same input → same output, no clock reads from callers (dates come in as arguments), no IO.
/// </summary>
public static class LogEntryRules
{
    /// <summary>
    /// Validates a draft against the limits above plus the past-date window.
    /// <paramref name="today"/> is injected so tests (and the app's IDateTimeProvider) control the clock.
    /// Order is deliberate: emptiness first (no-op beats error), then date, then fields — the user
    /// sees the first problem that actually blocks saving.
    /// </summary>
    public static LogEntryValidation Validate(ManualEntryDraft draft, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var day = today.Date;

        if (draft.IsEmpty)
        {
            // A note alone is not a data point and nothing was ever stored for this day → no-op.
            // If an entry with real values exists, clearing every value must still be saveable
            // (the store upserts the emptied draft), so callers pass that state in via HasStoredEntry.
            return new LogEntryValidation(LogEntryValidity.NothingToSave);
        }

        return ValidateFields(draft, day);
    }

    /// <summary>
    /// Same gate as <see cref="Validate"/> but aware that an entry already exists for the day
    /// (e.g. the user cleared every value of a stored entry — that is a real edit worth saving).
    /// </summary>
    public static LogEntryValidation Validate(ManualEntryDraft draft, DateTime today, bool hasStoredEntry)
    {
        if (draft is not null && draft.IsEmpty && !hasStoredEntry)
            return new LogEntryValidation(LogEntryValidity.NothingToSave);
        return Validate(draft!, today);
    }

    private static LogEntryValidation ValidateFields(ManualEntryDraft draft, DateTime day)
    {
        var d = draft.Date.Date;
        if (d > day) return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.FutureDate");
        if ((day - d).TotalDays > LogEntryLimits.MaxAgeDays)
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.DateTooOld");

        if (OutOfRange(draft.SleepMinutes, 0, LogEntryLimits.MaxSleepMinutes))
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.SleepRange");
        if (OutOfRange(draft.Steps, 0, LogEntryLimits.MaxSteps))
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.StepsRange");
        if (OutOfRange(draft.ActiveMinutes, 0, LogEntryLimits.MaxActiveMinutes))
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.ActiveRange");
        if (FractionInvalid(draft.SleepQuality) || FractionInvalid(draft.Mood) ||
            FractionInvalid(draft.Energy) || FractionInvalid(draft.Stress))
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.SliderRange");
        if (draft.Note is { Length: > LogEntryLimits.MaxNoteLength })
            return new LogEntryValidation(LogEntryValidity.Invalid, "Log.Error.NoteTooLong");

        return new LogEntryValidation(LogEntryValidity.Valid);
    }

    private static bool OutOfRange(int? value, int min, int max) => value is { } v && (v < min || v > max);

    private static bool FractionInvalid(double? value) =>
        value is { } v && (double.IsNaN(v) || v < LogEntryLimits.MinFraction || v > LogEntryLimits.MaxFraction);

    /// <summary>Clamp every value into range and trim the note. Nulls stay null (not provided).</summary>
    public static ManualEntryDraft Sanitize(ManualEntryDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with
        {
            Date = draft.Date.Date,
            SleepMinutes = Clamp(draft.SleepMinutes, 0, LogEntryLimits.MaxSleepMinutes),
            Steps = Clamp(draft.Steps, 0, LogEntryLimits.MaxSteps),
            ActiveMinutes = Clamp(draft.ActiveMinutes, 0, LogEntryLimits.MaxActiveMinutes),
            SleepQuality = ClampFraction(draft.SleepQuality),
            Mood = ClampFraction(draft.Mood),
            Energy = ClampFraction(draft.Energy),
            Stress = ClampFraction(draft.Stress),
            Note = draft.Note?.Trim(),
        };
    }

    private static int? Clamp(int? value, int min, int max) =>
        value is { } v ? Math.Clamp(v, min, max) : null;

    private static double? ClampFraction(double? value) =>
        value is { } v && !double.IsNaN(v)
            ? Math.Clamp(v, LogEntryLimits.MinFraction, LogEntryLimits.MaxFraction)
            : null;

    // ---- Editor defaults (UI speaks nullable, editors speak text) -------------

    /// <summary>True when a nullable integer editor field holds no value.</summary>
    public static bool IsBlank(int? value) => value is null;

    /// <summary>
    /// Parses one editor field: blank → null ("not provided", NOT zero), non-integer → null.
    /// Accepts Persian/Arabic-Indic digits (users type them on native keyboards). Range
    /// enforcement is Validate/Sanitize's job so the error path stays a key, not a guess.
    /// </summary>
    public static int? ParseInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = NormalizeDigits(text.Trim());
        return int.TryParse(cleaned, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>Maps ۰-۹ (Persian) and ٠-٩ (Arabic-Indic) to ASCII digits; other chars pass through.</summary>
    public static string NormalizeDigits(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var chars = input.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= '۰' and <= '۹') chars[i] = (char)('0' + (c - '۰'));
            else if (c is >= '٠' and <= '٩') chars[i] = (char)('0' + (c - '٠'));
        }
        return new string(chars);
    }

    /// <summary>Hours + minutes editor cells → nullable total minutes. Both cells blank → null.</summary>
    public static int? SleepTotalMinutes(string? hoursText, string? minutesText)
    {
        bool blank = string.IsNullOrWhiteSpace(hoursText) && string.IsNullOrWhiteSpace(minutesText);
        if (blank) return null;
        int h = ParseInt(hoursText) ?? 0;
        int m = ParseInt(minutesText) ?? 0;
        return h * 60 + m;
    }

    /// <summary>True when no slider carries a value. The view uses it to hide a 0%-full bar.</summary>
    public static bool AllSlidersEmpty(double? sleepQuality, double? mood, double? energy, double? stress) =>
        sleepQuality is null && mood is null && energy is null && stress is null;

    /// <summary>
    /// A day is loggable when it is today or lies within the past-date window. Used to enable/disable
    /// the save affordance early (Validate remains the gate that actually blocks persistence).
    /// </summary>
    public static bool IsDateInWindow(DateTime date, DateTime today)
    {
        var d = date.Date;
        return d <= today.Date && (today.Date - d).TotalDays <= LogEntryLimits.MaxAgeDays;
    }

    /// <summary>
    /// Clamps a picked date to the loggable window (no future days, no day older than
    /// <see cref="LogEntryLimits.MaxAgeDays"/>). Pure date arithmetic — no messages, no clock reads.
    /// </summary>
    public static DateTime ClampDate(DateTime date, DateTime today)
    {
        var d = date.Date;
        var t = today.Date;
        if (d > t) return t;
        var oldest = t.AddDays(-LogEntryLimits.MaxAgeDays);
        return d < oldest ? oldest : d;
    }

    /// <summary>How many days ago a date is (0 = today); negative for future dates.</summary>
    public static int DaysAgo(DateTime date, DateTime today) => (today.Date - date.Date).Days;

    /// <summary>
    /// Suggested defaults for a fresh check-in of the given weekday: nothing is fabricated as a
    /// measurement — these are starting points for the user's own report (0 = an honest "nothing yet").
    /// </summary>
    public static ManualEntryDraft EmptyDraft(DateTime date) => new() { Date = date.Date };
}
