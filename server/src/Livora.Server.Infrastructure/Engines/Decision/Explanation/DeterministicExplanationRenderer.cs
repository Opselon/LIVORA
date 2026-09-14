namespace Livora.Server.Infrastructure.Engines.Decision.Explanation;

/// <summary>
/// PURPOSE: the AI-free explanation port. Every number a decision quotes is rendered into a
///          sentence by a deterministic template table — the SAME keys the client localizes,
///          shipped server-side in en + fa so the server can answer text without the client.
///          No AI call exists on this path (Wave 4 product law: AI explains, engines decide;
///          and the human rule for this wave: no live-LLM dependency in CI). A caller asking
///          for the AI explainer gets an honest 503 code=ai_unavailable, never a disguised fallback.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Pure: key+args in, text out, no IO.
/// INVARIANTS:
///   - an unknown key renders "[missing:key]" — visible, greppable, never silently invented prose
///   - fa templates are the same shape with Persian digits via PersianDigits() (RTL-safe: no
///     bidi-sensitive concatenation — numbers are formatted BEFORE embedding)
///   - generatedBy is always "deterministic_template": the output says how it was made
/// </summary>
public static class DeterministicExplanationRenderer
{
    public const string GeneratedBy = "deterministic_template";
    public const string MissingPrefix = "[missing:";

    // ---- en templates -----------------------------------------------------------------
    private static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        ["Rule.Reason.SleepBelowBaseline"] = "Sleep is {0} hours below your own baseline.",
        ["Rule.Reason.RecoveryBelowBaseline"] = "Recovery is {0}% below your usual level.",
        ["Rule.Reason.StressAboveUsual"] = "Stress is running above your usual level ({0}).",
        ["Rule.Reason.StepsBelowBaseline"] = "Steps are below your personal baseline ({0} vs {1}).",
        ["Rule.Reason.AllNearBaseline"] = "Everything is close to your normal — keep the routine.",
        ["Rule.Reason.HabitStreakAtRisk"] = "Your streak on '{0}' ({1} days) is at risk today.",
        ["Rule.Reason.SleepDataStale"] = "Sleep data is {0} days old — treating it as stale, not fresh.",
        ["Rule.Reason.MeetingLoadHigh"] = "Meetings take {0} minutes today.",
        ["Rule.Reason.ScreenTimeHigh"] = "Screen time is {0} minutes today.",
        ["Plan.Evidence.RecoveryBelowBaseline"] = "Recovery is {0}% below your usual, so the training block is adjusted.",
        ["Plan.Change.MoveWorkout"] = "The workout moves to a free slot ({0} minutes later, {1} min kept).",
        ["Plan.Change.ShrinkExercise"] = "The workout shortens from {0} to {1} minutes ({2}% below your usual recovery).",
        ["Plan.Change.SkipWorkout"] = "The workout is skipped ({0} min planned; recovery is {1}% below usual).",
        ["Fusion.ProtectOneFocusBlock"] = "Protect ONE {1}-minute focus block — meetings already take {0} minutes.",
        ["Fusion.NoExtraDemandToday"] = "Add no extra demanding task today (meetings: {0} min).",
        ["Fusion.HydrateAfterDepletedTraining"] = "Keep hydration up — the day is training-reduced, not training-free.",
        ["Rec.ShortWalk"] = "Take a 20-minute walk.",
        ["Rec.EarlierBedtime"] = "Go to bed about 30 minutes earlier tonight.",
        ["Rec.TakeBreak"] = "Take a 10-minute break away from screens.",
        ["Rec.CompleteHabit"] = "Complete '{0}' to keep the {1}-day streak.",
        ["Rec.KeepRoutine"] = "Keep today's routine — nothing needs correcting.",
        ["Weekly.Up.Sleep"] = "Sleep trended up this week.",
        ["Weekly.Up.Activity"] = "Activity trended up this week.",
        ["Weekly.Up.Recovery"] = "Recovery trended up this week.",
        ["Weekly.Up.Stress"] = "Stress trended down this week.",
        ["Weekly.Down.Sleep"] = "Sleep trended down this week.",
        ["Weekly.Down.Activity"] = "Activity trended down this week.",
        ["Weekly.Down.Recovery"] = "Recovery trended down this week.",
        ["Weekly.Down.Stress"] = "Stress trended up this week.",
        ["Weekly.Focus.Stress"] = "Next week, one small stress lever.",
        ["Weekly.Focus.Sleep"] = "Next week, protect sleep first.",
        ["Weekly.Focus.Momentum"] = "Keep the momentum going ({0}-day best streak).",
        ["Weekly.Focus.OneHabit"] = "Pick ONE habit to make reliable.",
        ["Weekly.NoData"] = "Not enough history this week to say anything honest.",
        ["pattern.observed.late-sleep"] = "Late nights recur.",
        ["pattern.evidence.late-nights"] = "{0} of the last {1} nights were more than {2} min past your usual bedtime.",
        ["pattern.observed.weekday-dip"] = "One weekday dips.",
        ["pattern.evidence.weekday-dip"] = "Weekday {0} averages {1}% fewer steps than your other days ({2} samples).",
        ["pattern.observed.poor-sleep-focus"] = "Focus tends to be lower the day after poor sleep.",
        ["pattern.evidence.poor-sleep-focus"] = "In {0}% of {1} poor-sleep nights, next-day focus sat below your median ({2} times). Co-occurrence, not a cause.",
        ["pattern.observed.goal-stagnant"] = "A goal hasn't moved.",
        ["pattern.evidence.goal-stagnant"] = "Progress was flat for {0} days with {1} days to the deadline.",
    };

    // ---- fa templates (same keys; Persian digits applied to the numeric args) ----------
    private static readonly Dictionary<string, string> Fa = new(StringComparer.Ordinal)
    {
        ["Rule.Reason.SleepBelowBaseline"] = "خواب {0} ساعت کمتر از حالت معمول شماست.",
        ["Rule.Reason.RecoveryBelowBaseline"] = "ریکاوری {0}٪ پایین‌تر از سطح معمول شماست.",
        ["Rule.Reason.StressAboveUsual"] = "استرس از حد معمول شما بالاتر است ({0}).",
        ["Rule.Reason.StepsBelowBaseline"] = "گام‌ها از حالت معمول شما کمتر است ({0} در برابر {1}).",
        ["Rule.Reason.AllNearBaseline"] = "همه‌چیز نزدیک به حالت عادی شماست؛ روتین را نگه دارید.",
        ["Rule.Reason.HabitStreakAtRisk"] = "زنجیرهٔ «{0}» ({1} روزه) امروز در خطر است.",
        ["Rule.Reason.SleepDataStale"] = "دادهٔ خواب {0} روز قدیمی است؛ آن را تازه فرض نمی‌کنیم.",
        ["Rule.Reason.MeetingLoadHigh"] = "جلسات امروز {0} دقیقه وقت می‌گیرند.",
        ["Rule.Reason.ScreenTimeHigh"] = "زمان صفحهٔ نمایش امروز {0} دقیقه است.",
        ["Plan.Evidence.RecoveryBelowBaseline"] = "ریکاوری {0}٪ پایین‌تر از معمول است، پس تمرین تنظیم شد.",
        ["Plan.Change.MoveWorkout"] = "تمرین به یک ساعت آزاد منتقل شد ({0} دقیقه بعدتر، {1} دقیقه حفظ شد).",
        ["Plan.Change.ShrinkExercise"] = "تمرین از {0} به {1} دقیقه کوتاه شد ({2}٪ پایین‌تر از ریکاوری معمول).",
        ["Plan.Change.SkipWorkout"] = "تمرین حذف شد ({0} دقیقه برنامه داشت؛ ریکاوری {1}٪ پایین‌تر از معمول).",
        ["Fusion.ProtectOneFocusBlock"] = "از یک بلوک {1} دقیقه‌ای تمرکز محافظت کنید — جلسات {0} دقیقه هستند.",
        ["Fusion.NoExtraDemandToday"] = "امروز کار demanding اضافه‌ای روی برنامه نگذارید (جلسات: {0} دقیقه).",
        ["Fusion.HydrateAfterDepletedTraining"] = "آب کافی بنوشید؛ امروز تمرین کم‌شدت است، نه بی‌حرکت.",
        ["Rec.ShortWalk"] = "یک پیاده‌روی ۲۰ دقیقه‌ای داشته باشید.",
        ["Rec.EarlierBedtime"] = "امروز حدود ۳۰ دقیقه زودتر بخوابید.",
        ["Rec.TakeBreak"] = "یک استراحت ۱۰ دقیقه‌ای دور از صفحه‌نمایش.",
        ["Rec.CompleteHabit"] = "«{0}» را انجام دهید تا زنجیرهٔ {1} روزه نشکند.",
        ["Rec.KeepRoutine"] = "روتین امروز را نگه دارید؛ چیزی نیاز به اصلاح ندارد.",
        ["Weekly.Up.Sleep"] = "خواب این هفته روند صعودی داشت.",
        ["Weekly.Up.Activity"] = "فعالیت این هفته روند صعودی داشت.",
        ["Weekly.Up.Recovery"] = "ریکاوری این هفته روند صعودی داشت.",
        ["Weekly.Up.Stress"] = "استرس این هفته روند نزولی داشت.",
        ["Weekly.Down.Sleep"] = "خواب این هفته روند نزولی داشت.",
        ["Weekly.Down.Activity"] = "فعالیت این هفته روند نزولی داشت.",
        ["Weekly.Down.Recovery"] = "ریکاوری این هفته روند نزولی داشت.",
        ["Weekly.Down.Stress"] = "استرس این هفته روند صعودی داشت.",
        ["Weekly.Focus.Stress"] = "هفتهٔ آینده یک اهرم کوچک استرس را امتحان کنید.",
        ["Weekly.Focus.Sleep"] = "هفتهٔ آینده اول از خواب محافظت کنید.",
        ["Weekly.Focus.Momentum"] = "همین روند را نگه دارید (بهترین زنجیره: {0} روز).",
        ["Weekly.Focus.OneHabit"] = "یک عادت را انتخاب کنید و قابل‌اطمینان بسازید.",
        ["Weekly.NoData"] = "تاریخچهٔ این هفته برای یک نتیجهٔ صادقانه کافی نیست.",
        ["pattern.observed.late-sleep"] = "شب‌های دیر‌خوابی تکرار می‌شوند.",
        ["pattern.evidence.late-nights"] = "{0} از {1} شب گذشته بیش از {2} دقیقه دیرتر از معمول خوابیدید.",
        ["pattern.observed.weekday-dip"] = "یک روز هفته افت دارد.",
        ["pattern.evidence.weekday-dip"] = "روز هفتهٔ {0} به‌طور متوسط {1}٪ گام کمتر از بقیهٔ روزها دارد ({2} نمونه).",
        ["pattern.observed.poor-sleep-focus"] = "بعد از خواب بد، تمرکز روز بعد معمولاً پایین‌تر است.",
        ["pattern.evidence.poor-sleep-focus"] = "در {0}٪ از {1} شبِ خواب بد، تمرکز روز بعد زیر میانگین بود ({2} بار). این هم‌رخداد است، نه علت.",
        ["pattern.observed.goal-stagnant"] = "یک هدف تکان نخورد.",
        ["pattern.evidence.goal-stagnant"] = "پیشرفت {0} روز ثابت ماند در حالی که {1} روز تا مهلت باقی است.",
    };

    public static string Render(string key, IReadOnlyList<object> args, string locale = "en")
    {
        var table = string.Equals(locale, "fa", StringComparison.OrdinalIgnoreCase) ? Fa : En;
        if (!table.TryGetValue(key, out var template))
            return MissingPrefix + key + "]";

        var formatted = args.Select(a => Format(a, locale)).ToArray();
        try { return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, formatted); }
        catch (FormatException) { return MissingPrefix + key + "]"; } // arity mismatch is a bug — say so visibly
    }

    public static bool Knows(string key, string locale = "en") =>
        (string.Equals(locale, "fa", StringComparison.OrdinalIgnoreCase) ? Fa : En).ContainsKey(key);

    public static IReadOnlyList<string> KnownKeys =>
        En.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>Persian digits for fa (client IFormatService convention, restated pure).</summary>
    public static string PersianDigits(string latinDigits) =>
        latinDigits
            .Replace('0', '۰').Replace('1', '۱').Replace('2', '۲').Replace('3', '۳').Replace('4', '۴')
            .Replace('5', '۵').Replace('6', '۶').Replace('7', '۷').Replace('8', '۸').Replace('9', '۹');

    private static string Format(object arg, string locale)
    {
        var s = arg switch
        {
            double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            float f => f.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => arg?.ToString() ?? string.Empty,
        };
        return string.Equals(locale, "fa", StringComparison.OrdinalIgnoreCase) ? PersianDigits(s) : s;
    }
}
