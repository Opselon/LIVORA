using System.Globalization;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Application.Planning;

/// <summary>Why the editor refuses to save. Values are stable machine codes; the UI resolves a
/// localization key from them (via a dedicated key mapper in the ViewModel) and never prints this
/// name to the user.</summary>
public enum GoalEditIssue
{
    None,
    /// <summary>Name missing after trimming.</summary>
    NameEmpty,
    /// <summary>Name longer than <see cref="GoalEditorRules.MaxNameLength"/>.</summary>
    NameTooLong,
    /// <summary>Another live goal already uses this name (warning, not a blocker).</summary>
    NameDuplicate,
    /// <summary>Target is missing or not a finite, positive number.</summary>
    TargetNotNumeric,
    /// <summary>Target is not a whole number while the unit counts whole things (sessions/steps).</summary>
    TargetNotWhole,
    /// <summary>Target outside the sane range for its unit.</summary>
    TargetOutOfRange,
    /// <summary>Deadline is in the past.</summary>
    DeadlinePast,
    /// <summary>Measurement wants a metric but no metric key was chosen.</summary>
    MetricKeyMissing,
}

public enum HabitEditIssue
{
    None,
    NameEmpty,
    NameTooLong,
    /// <summary>Another live habit already uses this name (warning, not a blocker).</summary>
    NameDuplicate,
    /// <summary>Times-per-week outside 1..<see cref="GoalEditorRules.MaxTimesPerWeek"/>.</summary>
    TimesPerWeekOutOfRange,
    /// <summary>A fixed cadence (Daily / Weekdays) contradicts the typed times-per-week.</summary>
    TimesPerWeekBelowFrequency,
}

/// <summary>One message per issue; IsValid only counts blocking issues (see OrderedIssues). The
/// duplicate-name issue is advisory, so IsValid can be true while Issues is non-empty.</summary>
public sealed record GoalEditValidation(bool IsValid, IReadOnlyList<GoalEditIssue> Issues)
{
    public static readonly GoalEditValidation Valid = new(true, Array.Empty<GoalEditIssue>());
    public bool HasDuplicateNameWarning => Issues.Contains(GoalEditIssue.NameDuplicate);
    public bool BlocksSave => !IsValid;
}

public sealed record HabitEditValidation(bool IsValid, IReadOnlyList<HabitEditIssue> Issues)
{
    public static readonly HabitEditValidation Valid = new(true, Array.Empty<HabitEditIssue>());
    public bool HasDuplicateNameWarning => Issues.Contains(HabitEditIssue.NameDuplicate);
    public bool BlocksSave => !IsValid;
}

/// <summary>
/// Wave 3 (lane 07): the goal/habit editor's decision logic, kept pure and MAUI-free so the plain
/// net10.0 test project compiles and pins it down.
///
/// Layer rules honoured here:
/// - No localization lookups. Codes out, the Presentation layer resolves keys — the Application
///   layer may only emit keys and arguments (Wave 2 honesty rule, now asserted by the suite).
/// - No formatting. Localized digits are IFormatService's job, so numeric bounds are returned as
///   numbers for the VM to format.
/// - No persistence. The store stays behind IRepository / IHistoryRepository.
/// </summary>
public static class GoalEditorRules
{
    public const int MaxNameLength = 80;
    /// <summary>Upper bound on a per-period session/step target — generous, but stops fat-finger
    /// values like 999999 sessions from shipping as a "goal".</summary>
    public const int MaxSessionsPerPeriod = 100;
    public const int MaxStepsPerPeriod = 200_000;
    public const double MaxHoursPerPeriod = 168;   // one week of hours
    public const int MaxTimesPerWeek = 7;
    /// <summary>Progress a manual bump adds at once.</summary>
    public const double ManualBumpStep = 1;

    // ---- Name ------------------------------------------------------------

    /// <summary>Names are trimmed here so what is validated is exactly what is stored. Persian
    /// names routinely contain ZWNJ and other invisible joiners, so "nothing visible left" is
    /// tested with letter/digit check marks rather than a plain IsWhiteSpace.</summary>
    public static string CleanName(string? raw) => (raw ?? string.Empty).Trim();

    public static bool NameLengthOk(string? name) => CleanName(name).Length <= MaxNameLength;

    /// <summary>True when the two names should be treated as the same goal name (case- and
    /// culture-insensitive, trimmed). Turkish/German case folding is deliberately avoided so a
    /// Persian name never folds into something the user did not type.</summary>
    public static bool IsSameName(string? a, string? b)
        => string.Equals(CleanName(a), CleanName(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsDuplicateName(string? name, IEnumerable<Goal> existingGoals, string? editingGoalId = null)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) return false;
        return existingGoals.Any(g =>
            g.Id != editingGoalId && IsSameName(g.Name, clean));
    }

    public static bool IsDuplicateHabitName(string? name, IEnumerable<Habit> existingHabits, string? editingHabitId = null)
    {
        var clean = CleanName(name);
        if (clean.Length == 0) return false;
        return existingHabits.Any(h => h.Id != editingHabitId && IsSameName(h.Name, clean));
    }

    // ---- Target ----------------------------------------------------------

    /// <summary>Parses user-typed numbers. Western and Persian/Arabic-Indic digit shapes are both
    /// accepted because the keyboard follows the OS, not the app language: a Persian UI on a Latin
    /// keyboard (and vice versa) must still save. Group separators come from the active culture,
    /// so "1,200" and "۱٬۲۰۰" both parse. Null = did not parse (never a silent 0).</summary>
    public static bool TryParseTarget(string? text, CultureInfo? culture, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var raw = text.Trim();

        var ci = CultureInfo.InvariantCulture;
        if (culture is not null)
        {
            try { ci = CultureInfo.GetCultureInfo(culture.Name); }
            catch (CultureNotFoundException) { /* fall back to invariant digits */ }
        }

        double sum = 0;
        bool any = false;
        foreach (var part in raw.Split('+'))
        {
            if (!double.TryParse(Normalize(part), NumberStyles.Float, ci, out var v)
                && !double.TryParse(Normalize(part), NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return false;
            if (!double.IsFinite(v)) return false;
            sum += v;
            any = true;
        }
        if (!any) return false;
        value = sum;
        return true;

        string Normalize(string s)
        {
            s = s.Trim();
            if (culture is not null)
            {
                var native = culture.NumberFormat.NativeDigits;
                if (native is { Length: 10 })
                {
                    var map = new char[s.Length];
                    for (int i = 0; i < s.Length; i++)
                    {
                        int d = NativeDigit(s[i], native);
                        map[i] = d >= 0 ? (char)('0' + d) : s[i];
                    }
                    s = new string(map);
                }
            }
            // Arabic-Indic shapes (the Arabic block) show up on many Arabic-keyboard layouts and are
            // not covered by fa-IR's native digits (which use the extended Perso-Arabic forms).
            var latin = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                latin[i] = c is >= '\u0660' and <= '\u0669' ? (char)('0' + (c - '\u0660')) : c;
            }
            return new string(latin);
        }

        static int NativeDigit(char c, string[] native)
        {
            for (int d = 0; d < native.Length; d++)
                if (native[d].Length > 0 && native[d][0] == c) return d;
            return -1;
        }
    }

    /// <summary>Units that only make sense as integers — half a session or 3.5 steps is not a thing.</summary>
    public static bool RequiresWholeNumber(GoalUnit unit) =>
        unit is GoalUnit.Sessions or GoalUnit.Steps;

    /// <summary>Sane numeric bounds per unit (the target is always per <see cref="Goal.Period"/>).
    /// Bounds are whole numbers so hint text can go through IFormatService.Number without rounding
    /// surprises; fractional targets stay valid (hours/days) except where the unit is discrete.</summary>
    public static (double Min, double Max) TargetBounds(GoalUnit unit) => unit switch
    {
        GoalUnit.Sessions => (1, MaxSessionsPerPeriod),
        GoalUnit.Steps => (100, MaxStepsPerPeriod),
        GoalUnit.Minutes => (1, MaxHoursPerPeriod * 60),
        GoalUnit.Hours => (1, MaxHoursPerPeriod),
        _ => (1, MaxSessionsPerPeriod),
    };

    /// <summary>Generic range hint: {0}=min {1}=max {2}=unit label (localized by the VM).</summary>
    public const string NumberBoundsHintKey = "Editor.Hint.Range";

    // ---- Measurement -----------------------------------------------------

    /// <summary>True when the goal is measured from health data instead of a manual counter.
    /// Anything metric-based needs a metric key.</summary>
    public static bool IsMetricMeasured(GoalMeasurement m) =>
        m is GoalMeasurement.DailyMetricAverage
           or GoalMeasurement.DailyMetricSum
           or GoalMeasurement.ThresholdDayCount;

    public static bool IsMetricMeasured(Goal? goal) =>
        goal is not null && IsMetricMeasured(goal.Measurement);

    /// <summary>True when progress can only be moved by a person tapping a counter. Metric-measured
    /// goals are computed from history, so the editor must not offer a fake "+1".</summary>
    public static bool AllowsManualProgress(Goal? goal) =>
        goal is not null && !IsMetricMeasured(goal);

    /// <summary>Every metric key the engine actually computes (mirrors State.Metrics constants;
    /// kept literal so the Application layer has no dependency on the state model's shape).
    /// Order is stable and UI-localized through <c>Editor.Metric.*</c> keys.</summary>
    public static IReadOnlyList<string> KnownMetricKeys { get; } = new[]
    {
        "sleep.minutes",
        "sleep.quality",
        "sleep.consistency",
        "sleep.bedtime",
        "activity.steps",
        "activity.minutes",
        "recovery.score",
        "recovery.rhr",
        "recovery.hrv",
        "wellness.stress",
        "wellness.mood",
        "wellness.energy",
        "focus.estimate",
    };

    public static bool IsKnownMetricKey(string? metricKey) =>
        !string.IsNullOrWhiteSpace(metricKey)
        && KnownMetricKeys.Any(k => string.Equals(k, metricKey, StringComparison.OrdinalIgnoreCase));

    public static string MetricKeyLocalizationKey(string? metricKey) => metricKey switch
    {
        "sleep.minutes" => "Editor.Metric.SleepMinutes",
        "sleep.quality" => "Editor.Metric.SleepQuality",
        "sleep.consistency" => "Editor.Metric.SleepConsistency",
        "sleep.bedtime" => "Editor.Metric.Bedtime",
        "activity.steps" => "Editor.Metric.Steps",
        "activity.minutes" => "Editor.Metric.ActiveMinutes",
        "recovery.score" => "Editor.Metric.Recovery",
        "recovery.rhr" => "Editor.Metric.RestingHeartRate",
        "recovery.hrv" => "Editor.Metric.Hrv",
        "wellness.stress" => "Editor.Metric.Stress",
        "wellness.mood" => "Editor.Metric.Mood",
        "wellness.energy" => "Editor.Metric.Energy",
        "focus.estimate" => "Editor.Metric.Focus",
        _ => "Editor.Metric.Custom",
    };

    /// <summary>Category the app should preselect for a metric, so a step goal reads as fitness.</summary>
    public static GoalCategory SuggestedCategoryForMetric(string? metricKey) => metricKey switch
    {
        "sleep.minutes" or "sleep.quality" or "sleep.consistency" or "sleep.bedtime" => GoalCategory.Sleep,
        "activity.steps" or "activity.minutes" => GoalCategory.Fitness,
        "focus.estimate" => GoalCategory.Focus,
        "wellness.stress" or "wellness.mood" or "wellness.energy" => GoalCategory.Mindfulness,
        _ => GoalCategory.Custom,
    };

    // ---- Suggestions from the profile ------------------------------------

    /// <summary>
    /// A ready-to-edit draft suggested from the focus areas picked in onboarding
    /// (<see cref="UserProfile.FocusAreas"/>). Values are concrete starting points, not guesses
    /// about the user's data, and the editor always shows them as editable fields. Unknown areas
    /// fall back to a plain custom goal rather than inventing a health target.
    /// </summary>
    public static Goal SuggestedDraft(UserProfile? profile) => SuggestedDraft(profile?.FocusAreas);

    public static Goal SuggestedDraft(IReadOnlyList<string>? focusAreas)
    {
        var area = (focusAreas is { Count: > 0 } ? focusAreas[0] : null)?.Trim().ToLowerInvariant();
        return area switch
        {
            "sleep" => new Goal
            {
                Category = GoalCategory.Sleep, Unit = GoalUnit.Hours, Period = GoalPeriod.Day,
                TargetValue = 7.5, FrequencyPerPeriod = 1,
            },
            "fitness" or "activity" => new Goal
            {
                Category = GoalCategory.Fitness, Unit = GoalUnit.Sessions, Period = GoalPeriod.Week,
                TargetValue = 3, FrequencyPerPeriod = 3,
            },
            "energy" => new Goal
            {
                Category = GoalCategory.Fitness, Unit = GoalUnit.Minutes, Period = GoalPeriod.Week,
                TargetValue = 150, FrequencyPerPeriod = 5,
            },
            "focus" => new Goal
            {
                Category = GoalCategory.Focus, Unit = GoalUnit.Sessions, Period = GoalPeriod.Week,
                TargetValue = 5, FrequencyPerPeriod = 5,
            },
            "stress" or "wellness" or "mindfulness" => new Goal
            {
                Category = GoalCategory.Mindfulness, Unit = GoalUnit.Sessions, Period = GoalPeriod.Week,
                TargetValue = 5, FrequencyPerPeriod = 5,
            },
            "learning" => new Goal
            {
                Category = GoalCategory.Learning, Unit = GoalUnit.Minutes, Period = GoalPeriod.Week,
                TargetValue = 90, FrequencyPerPeriod = 3,
            },
            "nutrition" => new Goal
            {
                Category = GoalCategory.Nutrition, Unit = GoalUnit.Sessions, Period = GoalPeriod.Week,
                TargetValue = 5, FrequencyPerPeriod = 5,
            },
            "screentime" => new Goal
            {
                Category = GoalCategory.ScreenTime, Unit = GoalUnit.Hours, Period = GoalPeriod.Day,
                TargetValue = 2, FrequencyPerPeriod = 1,
            },
            _ => new Goal
            {
                Category = GoalCategory.Custom, Unit = GoalUnit.Sessions, Period = GoalPeriod.Week,
                TargetValue = 3, FrequencyPerPeriod = 3,
            },
        };
    }

    public static Habit SuggestedHabitDraft(UserProfile? profile)
    {
        var goal = SuggestedDraft(profile);
        return new Habit
        {
            Frequency = goal.Category == GoalCategory.Sleep || goal.Category == GoalCategory.Mindfulness
                ? HabitFrequencyKind.Daily
                : HabitFrequencyKind.TimesPerWeek,
            TimesPerWeek = Math.Clamp(goal.FrequencyPerPeriod, 1, MaxTimesPerWeek),
        };
    }

    // ---- Archive vs delete ----------------------------------------------

    /// <summary>
    /// Delete is reserved for something that was never started: a goal that never moved keeps
    /// nothing worth logging, so it is really deleted. Anything with progress (or a measurement
    /// link to history) is archived instead — the user's effort stays in history and the weekly
    /// review keeps counting it, which is why the archive decision is a rule and not a checkbox.
    /// </summary>
    public static bool ShouldHardDelete(Goal? goal) =>
        goal is not null && goal.ProgressValue <= 0 && !IsMetricMeasured(goal);

    /// <summary>The verb the UI should offer for this goal (key, not prose).</summary>
    public static string DeleteOrArchiveActionKey(Goal? goal) =>
        ShouldHardDelete(goal) ? "Common.Delete" : "Editor.ArchiveAction";

    public static string ConfirmBodyKey(Goal? goal) =>
        ShouldHardDelete(goal) ? "Editor.Confirm.Delete.Body" : "Editor.Confirm.Archive.Body";

    /// <summary>The undo key for the message shown right after the action.</summary>
    /// <summary>Message shown right after the action, with {0} = the goal's display name.</summary>
    public static string UndoKey(Goal? goal) =>
        ShouldHardDelete(goal) ? "Editor.Undo.Deleted" : "Editor.Undo.Archived";

    public static bool CanRestore(Goal? goal) => goal is not null && goal.IsArchived;

    /// <summary>Un-archive. Mutates the model on purpose: the repository is the caller's job so the
    /// rule stays testable without IO.</summary>
    public static void Restore(Goal goal) => goal.IsArchived = false;

    public static void Archive(Goal goal) => goal.IsArchived = true;

    // ---- Validation ------------------------------------------------------

    /// <summary>Full goal-editor validation. A duplicate name is a WARNING (it lands in Issues but
    /// keeps IsValid true) so the user can have two goals called "Walk"; everything else blocks.</summary>
    public static GoalEditValidation ValidateGoal(
        string? name,
        string? targetText,
        GoalUnit unit,
        GoalMeasurement measurement,
        string? metricKey,
        DateTime? deadline,
        DateTime today,
        IEnumerable<Goal>? existingGoals = null,
        string? editingGoalId = null,
        CultureInfo? culture = null)
    {
        var issues = new List<GoalEditIssue>();
        var clean = CleanName(name);

        if (clean.Length == 0) issues.Add(GoalEditIssue.NameEmpty);
        else if (clean.Length > MaxNameLength) issues.Add(GoalEditIssue.NameTooLong);
        else if (existingGoals is not null && IsDuplicateName(clean, existingGoals, editingGoalId))
            issues.Add(GoalEditIssue.NameDuplicate);

        bool numeric = TryParseTarget(targetText, culture, out var target);
        if (!numeric) issues.Add(GoalEditIssue.TargetNotNumeric);
        else
        {
            var (min, max) = TargetBounds(unit);
            if (RequiresWholeNumber(unit) && Math.Abs(target - Math.Round(target)) > 1e-9)
                issues.Add(GoalEditIssue.TargetNotWhole);
            if (target < min || target > max) issues.Add(GoalEditIssue.TargetOutOfRange);
        }

        if (deadline is { } d && d.Date < today.Date) issues.Add(GoalEditIssue.DeadlinePast);

        if (IsMetricMeasured(measurement) && !IsKnownMetricKey(metricKey))
            issues.Add(GoalEditIssue.MetricKeyMissing);

        bool blocking = issues.Exists(i => i != GoalEditIssue.NameDuplicate);
        return new GoalEditValidation(!blocking, issues);
    }

    public static HabitEditValidation ValidateHabit(
        string? name,
        HabitFrequencyKind frequency,
        string? timesText,
        CultureInfo? culture = null,
        IEnumerable<Habit>? existingHabits = null,
        string? editingHabitId = null)
    {
        var issues = new List<HabitEditIssue>();
        var clean = CleanName(name);

        if (clean.Length == 0) issues.Add(HabitEditIssue.NameEmpty);
        else if (clean.Length > MaxNameLength) issues.Add(HabitEditIssue.NameTooLong);
        else if (existingHabits is not null && IsDuplicateHabitName(clean, existingHabits, editingHabitId))
            issues.Add(HabitEditIssue.NameDuplicate);

        int expected = ExpectedTimesPerWeek(frequency);
        if (expected > 0)
        {
            // Daily / Weekdays carry their own cadence; the typed value must not contradict it.
            if (TryParseTarget(timesText, culture, out var t) && Math.Abs(t - expected) > 1e-9)
                issues.Add(HabitEditIssue.TimesPerWeekBelowFrequency);
        }
        else
        {
            if (!TryParseTarget(timesText, culture, out var times)
                || Math.Abs(times - Math.Round(times)) > 1e-9)
            {
                issues.Add(HabitEditIssue.TimesPerWeekOutOfRange);
            }
            else if (times < 1 || times > MaxTimesPerWeek)
            {
                issues.Add(HabitEditIssue.TimesPerWeekOutOfRange);
            }
        }

        bool blocking = issues.Exists(i => i != HabitEditIssue.NameDuplicate);
        return new HabitEditValidation(!blocking, issues);
    }

    /// <summary>Times-per-week implied by a cadence (0 = the user chooses it).</summary>
    public static int ExpectedTimesPerWeek(HabitFrequencyKind frequency) => frequency switch
    {
        HabitFrequencyKind.Daily => 7,
        HabitFrequencyKind.Weekdays => 5,
        _ => 0,
    };

    public static int ResolveTimesPerWeek(HabitFrequencyKind frequency, string? timesText, CultureInfo? culture = null)
    {
        int implied = ExpectedTimesPerWeek(frequency);
        if (implied > 0) return implied;
        if (TryParseTarget(timesText, culture, out var t) && t >= 1 && t <= MaxTimesPerWeek)
            return (int)Math.Round(t);
        return 3;
    }

    /// <summary>How often a habit is expected per week: the chosen cadence, clamped to real days.</summary>
    public static int WeeklyExpectation(Habit habit)
    {
        if (habit is null) return 0;
        int implied = ExpectedTimesPerWeek(habit.Frequency);
        int value = implied > 0 ? implied : habit.TimesPerWeek;
        return Math.Clamp(value, 1, MaxTimesPerWeek);
    }

    // ---- Reminder binding ------------------------------------------------

    /// <summary>Machine tag stored in ReminderSetting.Kind. Persisted as a string — never rename.</summary>
    public const string HabitReminderKind = "habit";
    /// <summary>Notification body key. The reminder engine/scheduler resolve it at display time.
    /// Prefixed Goals.* so it can never collide with lane 09's Reminders.* keys in the merge.</summary>
    public const string HabitReminderTextKey = "Goals.HabitReminderText";

    /// <summary>A habit reminder id is derived from the habit id so re-saving the same habit's
    /// reminder upserts one row instead of stacking duplicates.</summary>
    public static string HabitReminderId(string habitId) => "habit:" + habitId;

    // ---- Period / unit phrasing (keys + args only) ----------------------

    public static string PeriodSuffixKey(GoalPeriod period) =>
        period == GoalPeriod.Day ? "Goals.PerDay" : "Goals.PerWeek";

    public static string FrequencyLabelKey(HabitFrequencyKind frequency) => frequency switch
    {
        HabitFrequencyKind.Daily => "Enum.HabitFrequencyKind.Daily",
        HabitFrequencyKind.Weekdays => "Enum.HabitFrequencyKind.Weekdays",
        _ => "Enum.HabitFrequencyKind.TimesPerWeek",
    };

    public static string MeasurementLabelKey(GoalMeasurement measurement) => measurement switch
    {
        GoalMeasurement.ManualCounter => "Editor.Measurement.Manual",
        GoalMeasurement.DailyMetricAverage => "Editor.Measurement.Average",
        GoalMeasurement.DailyMetricSum => "Editor.Measurement.Sum",
        GoalMeasurement.ThresholdDayCount => "Editor.Measurement.Days",
        _ => "Editor.Measurement.Manual",
    };

    /// <summary>The issue codes in a stable display order, so the message list never re-shuffles
    /// under the user's thumb.</summary>
    public static IReadOnlyList<GoalEditIssue> OrderedIssues(GoalEditValidation v) =>
        v.Issues.OrderByDescending(i => i is GoalEditIssue.NameDuplicate ? 0 : 1).ToList();

    public static IReadOnlyList<HabitEditIssue> OrderedIssues(HabitEditValidation v) =>
        v.Issues.OrderByDescending(i => i is HabitEditIssue.NameDuplicate ? 0 : 1).ToList();

    /// <summary>The step used by the manual progress bump; clamped so a bump can never overshoot
    /// the target into a meaningless 137%.</summary>
    public static double NextManualProgress(Goal goal)
    {
        if (goal is null || !AllowsManualProgress(goal)) return goal?.ProgressValue ?? 0;
        double next = goal.ProgressValue + ManualBumpStep;
        return goal.TargetValue > 0 ? Math.Min(next, goal.TargetValue) : next;
    }
}
