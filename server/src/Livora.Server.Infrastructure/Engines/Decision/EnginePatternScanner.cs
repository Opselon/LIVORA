namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: server restatement of Application/Patterns/PatternEngine.cs (the detectors the
///          intelligence surface needs server-side): global sample gate + LateSleepRecurring,
///          WeekdayActivityDip, FocusAfterPoorSleep, GoalStagnation. Confidence-aware, revisable
///          (re-scan overwrites by kind), DELETABLE (the store supports delete — a pattern the user
///          rejects must be removable, product law).
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Pure: no IO, no clock, no AI.
/// INVARIANTS (client parity, pinned by SemanticParityTests + PatternEngineTests):
///   - HARD RULE: findings describe CO-OCCURRENCE and deviation from the user's OWN baseline.
///     They never claim causation and are never a diagnosis — no psychological labels are invented:
///     the kind strings are the client PatternKind names, verbatim.
///   - evidence keys start with "pattern.observed." or "pattern.evidence." — enforced by test sweep
///   - below <see cref="NumericRules.MinHistoryDays"/> distinct days EVERY kind is refused explicitly
///     (refusal is data; the UI can then say "learning" instead of pretending absence)
///   - confidence = effect × sample ratio, hard-capped at 0.95 — patterns are never as sure as
///     measurements (ConfidenceFrom parity)
/// </summary>
public static class EnginePatternScanner
{
    public const string KindLateSleep = "LateSleepRecurring";
    public const string KindWeekdayDip = "WeekdayActivityDip";
    public const string KindFocusAfterPoorSleep = "FocusAfterPoorSleep";
    public const string KindGoalStagnation = "GoalStagnation";

    public static readonly IReadOnlyList<string> AllKinds =
        [KindLateSleep, KindWeekdayDip, KindFocusAfterPoorSleep, KindGoalStagnation];

    public static PatternScanOutput Scan(DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var asOf = input.AsOfUtc.Date;
        var history = input.History
            .Where(r => r.DateUtc.Date <= asOf && r.DateUtc.Date > asOf.AddDays(-NumericRules.BaselineWindowDays))
            .OrderBy(r => r.DateUtc.Date)
            .ToList();
        int distinct = history.Select(r => r.DateUtc.Date).Distinct().Count();
        if (distinct < NumericRules.MinHistoryDays)
            return new PatternScanOutput(Array.Empty<PatternFindingView>(), AllKinds, distinct);

        var findings = new List<PatternFindingView>();
        var insufficient = new List<string>();

        TryLateSleep(history, asOf, findings, insufficient);
        TryWeekdayDip(history, asOf, findings, insufficient);
        TryFocusAfterPoorSleep(history, asOf, findings, insufficient);
        TryGoalStagnation(input, asOf, findings, insufficient);

        return new PatternScanOutput(
            findings.OrderByDescending(f => f.Confidence)
                    .ThenBy(f => f.Kind, StringComparer.Ordinal)
                    .ToList(),
            insufficient, distinct);
    }

    // ---- 1) LateSleepRecurring: >=3 late nights in the last 7 (baseline + 60 min) --------------
    private static void TryLateSleep(List<EngineDayRecord> history, DateTime asOf,
        List<PatternFindingView> findings, List<string> insufficient)
    {
        // Bedtime baseline over 14 days needs >= 8 clean samples (client BaselineEngine gate).
        var bedtimes = history
            .Where(r => r.BedtimeMinutesOfDay is > 0)
            .Select(r => NumericRules.WrapBedtime(r.BedtimeMinutesOfDay!.Value))
            .ToList();
        if (bedtimes.Count < NumericRules.GateWindowDays14) { insufficient.Add(KindLateSleep); return; }
        double basePostNoon = bedtimes.Average();

        var lastSeven = history
            .Where(r => r.DateUtc.Date > asOf.AddDays(-NumericRules.GateLateSleepNights) && r.DateUtc.Date <= asOf)
            .Where(r => r.BedtimeMinutesOfDay is > 0)
            .ToList();
        if (lastSeven.Count < NumericRules.GateLateSleepNights) { insufficient.Add(KindLateSleep); return; }

        var late = lastSeven
            .Where(r => NumericRules.WrapBedtime(r.BedtimeMinutesOfDay!.Value)
                        > basePostNoon + NumericRules.LateSleepThresholdMinutes)
            .ToList();
        if (late.Count < NumericRules.LateSleepNightsToFire) return;   // below the fire threshold is NOT a pattern

        findings.Add(new PatternFindingView(
            PatternId(late.Select(r => r.DateUtc).ToList(), KindLateSleep), KindLateSleep,
            ConfidenceFrom(late.Count / (double)NumericRules.GateLateSleepNights / 0.60, lastSeven.Count, NumericRules.GateLateSleepNights),
            lastSeven.Count, lastSeven[0].DateUtc.Date, lastSeven[^1].DateUtc.Date,
            EngineTrend.InsufficientData.ToString(),
            [
                new PatternEvidenceKeyView("pattern.observed.late-sleep", Array.Empty<object>()),
                new PatternEvidenceKeyView("pattern.evidence.late-nights",
                    [late.Count, lastSeven.Count, (int)NumericRules.LateSleepThresholdMinutes]),
            ],
            late.Select(r => $"fact:{EngineStateComputer.EngineMetrics.BedtimeMinutes}:{r.DateUtc:yyyy-MM-dd}").ToList()));
    }

    // ---- 2) WeekdayActivityDip: a weekday whose mean steps sit >=20% under the personal mean ----
    private static void TryWeekdayDip(List<EngineDayRecord> history, DateTime asOf,
        List<PatternFindingView> findings, List<string> insufficient)
    {
        var withSteps = history.Where(r => r.Steps is > 0 && r.DateUtc.Date <= asOf).ToList();
        if (withSteps.Count == 0) { insufficient.Add(KindWeekdayDip); return; }
        double overall = withSteps.Average(r => r.Steps!.Value);
        if (overall <= 0) { insufficient.Add(KindWeekdayDip); return; }

        var groups = withSteps
            .GroupBy(r => (int)r.DateUtc.DayOfWeek)
            .Select(g => (Weekday: g.Key, N: g.Count(), Mean: g.Average(r => r.Steps!.Value),
                          First: g.Min(r => r.DateUtc.Date), Last: g.Max(r => r.DateUtc.Date)))
            .ToList();
        var qualifying = groups.Where(g => g.N >= NumericRules.GateWeekdaySamples).ToList();
        if (qualifying.Count == 0) { insufficient.Add(KindWeekdayDip); return; }

        var worst = qualifying.OrderBy(g => (g.Mean - overall) / overall).First();
        double dev = (worst.Mean - overall) / overall;
        if (dev > NumericRules.WeekdayDipThreshold) return;

        int pctBelow = (int)Math.Round(Math.Abs(dev) * 100, MidpointRounding.AwayFromZero);
        findings.Add(new PatternFindingView(
            PatternId(groups.Select(g => (DateTime?)g.First).Concat([worst.Last]).OfType<DateTime>().ToList(), KindWeekdayDip),
            KindWeekdayDip,
            ConfidenceFrom(Math.Abs(dev) / 0.40, worst.N, NumericRules.GateWeekdaySamples),
            worst.N, worst.First, worst.Last,
            EngineTrend.InsufficientData.ToString(),
            [
                new PatternEvidenceKeyView("pattern.observed.weekday-dip", Array.Empty<object>()),
                new PatternEvidenceKeyView("pattern.evidence.weekday-dip", [worst.Weekday, pctBelow, worst.N]),
            ],
            [FactRangeKey(EngineStateComputer.EngineMetrics.Steps, worst.First, worst.Last)]));
    }

    // ---- 3) FocusAfterPoorSleep: co-occurrence, NOT causation (>=8 pairs, >=50%) ----------------
    private static void TryFocusAfterPoorSleep(List<EngineDayRecord> history, DateTime asOf,
        List<PatternFindingView> findings, List<string> insufficient)
    {
        var sleepSamples = history.Where(r => r.SleepMinutes is > 0).Select(r => r.SleepMinutes!.Value).ToList();
        if (sleepSamples.Count < NumericRules.GateWindowDays14) { insufficient.Add(KindFocusAfterPoorSleep); return; }
        double sleepBase = sleepSamples.Average();
        if (sleepBase <= 0) { insufficient.Add(KindFocusAfterPoorSleep); return; }

        var byDate = history.GroupBy(r => r.DateUtc.Date).ToDictionary(g => g.Key, g => g.Last());
        var dates = byDate.Keys.OrderBy(d => d).ToList();

        double? FocusOf(DateTime day) =>
            byDate.TryGetValue(day, out var r) && r.Energy is not null && r.Stress is not null && r.SleepQuality is not null
                ? Math.Clamp(0.45 * r.Energy.Value + 0.35 * (1 - r.Stress.Value) + 0.20 * r.SleepQuality.Value, 0, 1)
                : null;

        var focusSeries = dates.Select(FocusOf).OfType<double>().ToList();
        if (focusSeries.Count < NumericRules.GateFocusPairs) return;
        double focusMedian = Median(focusSeries);

        int pairs = 0, cooccur = 0;
        DateTime first = default, last = default;
        for (int i = 0; i + 1 < dates.Count; i++)
        {
            if ((dates[i + 1].Date - dates[i].Date).TotalDays != 1) continue;
            var night = byDate[dates[i]];
            if (night.SleepMinutes is null) continue;
            double sleepDev = (night.SleepMinutes.Value - sleepBase) / sleepBase;
            if (sleepDev >= NumericRules.PoorSleepDeviation) continue;   // not a poor-sleep night
            var nextFocus = FocusOf(dates[i + 1]);
            if (nextFocus is null) continue;                             // no next-day signal
            pairs++;
            if (first == default) first = dates[i];
            last = dates[i + 1];
            if (nextFocus.Value < focusMedian) cooccur++;
        }
        if (pairs < NumericRules.GateFocusPairs) { insufficient.Add(KindFocusAfterPoorSleep); return; }

        double rate = cooccur / (double)pairs;
        if (rate < NumericRules.FocusCoOccurrenceToFire) return;         // not stronger than chance

        int ratePct = (int)Math.Round(rate * 100, MidpointRounding.AwayFromZero);
        findings.Add(new PatternFindingView(
            PatternId([first, last], KindFocusAfterPoorSleep), KindFocusAfterPoorSleep,
            ConfidenceFrom(rate / 0.80, pairs, NumericRules.GateFocusPairs),
            pairs, first, last, EngineTrend.InsufficientData.ToString(),
            [
                new PatternEvidenceKeyView("pattern.observed.poor-sleep-focus", Array.Empty<object>()),
                new PatternEvidenceKeyView("pattern.evidence.poor-sleep-focus", [ratePct, pairs, cooccur]),
            ],
            [FactRangeKey(EngineStateComputer.EngineMetrics.SleepMinutes, first, last),
             FactRangeKey(EngineStateComputer.EngineMetrics.Energy, first, last)]));
    }

    // ---- 4) GoalStagnation: flat progress >= 10 days with a deadline inside 30 days -------------
    private static void TryGoalStagnation(DecisionInput input, DateTime asOf,
        List<PatternFindingView> findings, List<string> insufficient)
    {
        var samples = input.GoalProgress
            .Where(s => s.DateUtc.Date <= asOf)
            .GroupBy(s => s.GoalId)
            .ToList();
        if (samples.Count == 0) { insufficient.Add(KindGoalStagnation); return; }

        foreach (var group in samples.OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var pts = group.OrderBy(s => s.DateUtc.Date).GroupBy(s => s.DateUtc.Date).Select(g => g.Last()).ToList();
            if (pts.Count < 2) continue;
            var last = pts[^1];
            if (last.DeadlineUtc is null) continue;
            int daysToDeadline = (int)(last.DeadlineUtc.Value.Date - asOf).TotalDays;
            if (daysToDeadline < 0 || daysToDeadline > NumericRules.StagnationDeadlineWindowDays) continue;

            double newestValue = last.ProgressValue;
            int flatFrom = pts.Count - 1;
            while (flatFrom > 0 && pts[flatFrom - 1].ProgressValue == newestValue) flatFrom--;
            int flatDays = (int)(pts[^1].DateUtc.Date - pts[flatFrom].DateUtc.Date).TotalDays + 1;
            if (flatDays < NumericRules.GateStagnationDays) continue;

            findings.Add(new PatternFindingView(
                PatternId([pts[flatFrom].DateUtc, last.DateUtc], KindGoalStagnation + group.Key), KindGoalStagnation,
                ConfidenceFrom(flatDays / 20.0, flatDays, NumericRules.GateStagnationDays),
                flatDays, pts[flatFrom].DateUtc.Date, last.DateUtc.Date,
                EngineTrend.InsufficientData.ToString(),
                [
                    new PatternEvidenceKeyView("pattern.observed.goal-stagnant", Array.Empty<object>()),
                    new PatternEvidenceKeyView("pattern.evidence.goal-stagnant", [flatDays, daysToDeadline]),
                ],
                [$"fact:goal-progress:{group.Key}:{pts[flatFrom].DateUtc:yyyy-MM-dd}..{last.DateUtc:yyyy-MM-dd}"]));
            return; // one stagnation report is enough noise for the UI (client parity)
        }
    }

    // ---- shared plumbing (client-parity helpers) ----------------------------------------------

    /// <summary>Confidence from effect × sample-gate ratio, hard-capped below certainty.</summary>
    public static double ConfidenceFrom(double effectScore, int sampleCount, int gate)
    {
        double effect = Math.Clamp(effectScore, 0, 1);
        double gateRatio = Math.Clamp(gate <= 0 ? 1 : sampleCount / (double)gate, 0, 1);
        return Math.Round(Math.Min(0.95, effect * gateRatio), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Stable pattern id: same kind + same date span => same id (re-scan REVISES in place;
    /// delete removes it permanently until its inputs change). No Guid randomness: determinism.</summary>
    private static string PatternId(IReadOnlyList<DateTime> datesSpanned, string kind)
    {
        if (datesSpanned.Count == 0) return $"pat:{kind}";
        var from = datesSpanned.Min().Date;
        var to = datesSpanned.Max().Date;
        return $"pat:{kind}:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";
    }

    private static string FactRangeKey(string metric, DateTime from, DateTime to) =>
        $"fact:{metric}:{from:yyyy-MM-dd}..{to:yyyy-MM-dd}";

    private static double Median(List<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var s = v.OrderBy(x => x).ToList();
        int mid = s.Count / 2;
        return s.Count % 2 == 1 ? s[mid] : (s[mid - 1] + s[mid]) / 2.0;
    }
}
