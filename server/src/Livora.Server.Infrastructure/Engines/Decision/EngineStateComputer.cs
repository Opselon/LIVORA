namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: server-side restatement of the client state stack (Application/State/UserStateService.cs
///          + BaselineService.cs + TrendService.cs): raw day records -> personal baselines ->
///          derived metrics, every number wrapped in a <see cref="MetricFact"/> with provenance and
///          confidence. Pure: the caller injects the as-of instant; there is no clock, no IO, no AI.
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// CONSUMES: <see cref="DecisionInput"/> (day records + history).
/// PROVIDES: <see cref="StateSnapshot"/> for the rule evaluator and the fusion planner.
/// INVARIANTS (client parity, pinned by SemanticParityTests + EngineStateTests):
///   - baseline window 28 days of the user's OWN history; confidence ladder 3/7/14 samples
///   - a metric with no usable baseline has RelativeDeviation=null and Level=Unknown — never a
///     computed-looking number from nothing (BaselineEngine "refusal" honesty)
///   - focus is DERIVED (energy .45 + (1−stress) .35 + sleepQuality .20, UserStateService parity);
///     missing inputs yield no focus value rather than a silent mid-range guess
///   - overall confidence = usable-metric ratio (weakest-link parity with ComputeConfidence)
///   - every value in the snapshot answers "observed | inferred | user-provided | assumed"
/// </summary>
public static class EngineStateComputer
{
    /// <summary>Metric keys with the client's polarity (higher-is-better flags verbatim).</summary>
    public static readonly IReadOnlyDictionary<string, bool> Polarity =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [EngineMetrics.SleepMinutes] = true,
            [EngineMetrics.SleepQuality] = true,
            [EngineMetrics.SleepConsistency] = true,
            [EngineMetrics.BedtimeMinutes] = true,
            [EngineMetrics.Steps] = true,
            [EngineMetrics.ActiveMinutes] = true,
            [EngineMetrics.RecoveryScore] = true,
            [EngineMetrics.Stress] = false,
            [EngineMetrics.Mood] = true,
            [EngineMetrics.Energy] = true,
            [EngineMetrics.FocusEstimate] = true,
        };

    /// <summary>Expected daily readings for the completeness ratio — mirrors NormalizedDay.Completeness().</summary>
    public static readonly IReadOnlyList<string> CompletenessKeys =
        [
            EngineMetrics.SleepMinutes, EngineMetrics.SleepQuality, EngineMetrics.Steps,
            EngineMetrics.ActiveMinutes, EngineMetrics.RecoveryScore, EngineMetrics.Stress,
            EngineMetrics.Mood, EngineMetrics.Energy,
        ];

    public static StateSnapshot Compute(DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var facts = new List<MetricFact>();
        var metrics = new Dictionary<string, DerivedMetric>(StringComparer.Ordinal);
        var assumptions = new List<string>();

        var asOfDate = input.AsOfUtc.Date;
        // Client parity (BaselineService.GetBaselinesAsync): date <= today AND date > today-28.
        // Today's own row is excluded here because the caller supplies it separately as Today.
        var history = input.History
            .Where(r => r.DateUtc.Date <= asOfDate && r.DateUtc.Date > asOfDate.AddDays(-NumericRules.BaselineWindowDays))
            .OrderBy(r => r.DateUtc.Date)
            .ToList();

        // ---- 1) personal baselines: rolling 28-day window, confidence-gated (BaselineService parity)
        var baselines = new Dictionary<string, (double Value, int N, EngineBaselineConfidence Conf)>(StringComparer.Ordinal);
        foreach (var (key, selector) in BaselineSelectors())
        {
            var samples = history.Select(selector).Where(v => !double.IsNaN(v) && v >= 0).ToList();
            var (mean, _, n) = NumericRules.MeanStdDev(samples);
            var conf = NumericRules.ConfidenceForSampleCount(n);
            if (key == EngineMetrics.BedtimeMinutes && conf != EngineBaselineConfidence.None && n > 0)
            {
                // Bedtimes break naive averaging across midnight: wrap → mean → unwrap (client WrapAround).
                var wrapped = history.Select(selector)
                    .Where(v => !double.IsNaN(v) && v > 0)
                    .Select(NumericRules.WrapBedtime)
                    .ToList();
                mean = wrapped.Count == 0 ? double.NaN : wrapped.Average();
                n = wrapped.Count;
                conf = NumericRules.ConfidenceForSampleCount(n);
                if (conf != EngineBaselineConfidence.None) mean = NumericRules.UnwrapBedtime(mean);
            }
            baselines[key] = (mean, n, conf);
        }

        // ---- 2) today's readings -> derived metrics + facts
        var today = input.Today;
        foreach (var (key, get, higher) in TodaySelectors(today))
        {
            var value = get();
            var (baseVal, baseN, baseConf) = baselines.TryGetValue(key, out var b) ? b : (double.NaN, 0, EngineBaselineConfidence.None);
            var usableBase = baseConf != EngineBaselineConfidence.None && baseVal > 0 ? baseVal : (double?)null;
            var dev = value is null ? null : NumericRules.RelativeDeviation(value.Value, usableBase, baseConf);

            var quality = value is null ? EngineQuality.Missing : EngineQuality.Complete;
            var provenance = ProvenanceOf(today, key);
            if (provenance == EngineProvenance.Assumed)
                assumptions.Add($"today.{key}");

            var factId = $"fact:{Sanitize(key)}:today";
            string baselineFactId = "";
            if (value is not null)
                facts.Add(new MetricFact(factId, key, value.Value, UnitFor(key), provenance,
                    ConfidenceOfReading(provenance, quality), $"{key}@{asOfDate:yyyy-MM-dd}", asOfDate, quality));
            if (usableBase is not null)
            {
                // The baseline is itself a computed number — it gets its own fact row with its own
                // lineage (the window that earned it), so "below your baseline" cites BOTH sides.
                baselineFactId = $"fact:{Sanitize(key)}:baseline";
                facts.Add(new MetricFact(baselineFactId, key + ":baseline", usableBase.Value, UnitFor(key),
                    EngineProvenance.Inferred, NumericRules.ConfidenceOf(baseConf),
                    $"baseline:{key}:window={NumericRules.BaselineWindowDays}d:samples={baseN}", asOfDate));
            }

            metrics[key] = new DerivedMetric(
                MetricKey: key,
                Value: value,
                BaselineValue: usableBase,
                BaselineConfidence: baseConf,
                BaselineSamples: baseN,
                RelativeDeviation: dev,
                Level: quality == EngineQuality.Missing ? EngineLevel.Unknown : NumericRules.LevelFor(dev),
                HigherIsBetter: higher,
                Quality: quality,
                Provenance: provenance,
                Confidence: value is null ? 0 : ConfidenceOfReading(provenance, quality),
                FactId: factId)
            { BaselineFactId = baselineFactId };
        }

        // ---- 3) focus estimate — DERIVED from energy + stress + sleep quality (labeled honestly)
        {
            var energy = metrics[EngineMetrics.Energy].Value;
            var stress = metrics[EngineMetrics.Stress].Value;
            var sleepQuality = metrics[EngineMetrics.SleepQuality].Value;
            const double wEnergy = 0.45, wStress = 0.35, wSleep = 0.20; // UserStateService literal weights
            double? focus = energy is not null && stress is not null && sleepQuality is not null
                ? Math.Clamp(wEnergy * energy.Value + wStress * (1 - stress.Value) + wSleep * sleepQuality.Value, 0, 1)
                : null;

            var factId = "fact:focus.estimate:today";
            // Weakest-link: a derived value can never be more confident than the inputs it used.
            double derivedConfidence = Math.Min(
                Math.Min(metrics[EngineMetrics.Energy].Confidence, metrics[EngineMetrics.Stress].Confidence),
                metrics[EngineMetrics.SleepQuality].Confidence);
            if (focus is not null)
                facts.Add(new MetricFact(factId, EngineMetrics.FocusEstimate, focus.Value, "ratio",
                    EngineProvenance.Inferred, derivedConfidence,
                    "derived:energy+stress+sleepQuality", asOfDate, EngineQuality.Estimated)
                { DerivedFrom = [metrics[EngineMetrics.Energy].FactId, metrics[EngineMetrics.Stress].FactId, metrics[EngineMetrics.SleepQuality].FactId] });

            metrics[EngineMetrics.FocusEstimate] = new DerivedMetric(
                MetricKey: EngineMetrics.FocusEstimate,
                Value: focus,
                BaselineValue: null,                    // derived signal has no personal history — honest null
                BaselineConfidence: EngineBaselineConfidence.None,
                BaselineSamples: 0,
                RelativeDeviation: null,
                Level: EngineLevel.Unknown,
                HigherIsBetter: true,
                Quality: focus is null ? EngineQuality.Missing : EngineQuality.Estimated,
                Provenance: EngineProvenance.Inferred,
                Confidence: focus is null ? 0 : derivedConfidence,
                FactId: factId);
        }

        // ---- 4) freshness + completeness + confidence (UserStateService parity)
        // Freshness is deliberately measured over the FULL input history, not the 28-day baseline
        // window: "how old is the newest sleep reading the user has" answers a different question
        // than "what is their normal" (client parity: PersonalStateProjector.DaysSinceFreshData
        // at :261-269 scans every record, and the baseline window has no say in it). Reading it
        // off the windowed list made a 42-day-old feed report the 99 no-data sentinel — a lie
        // about whether any data exists at all.
        int daysSinceFreshSleep;
        if (metrics[EngineMetrics.SleepMinutes].Value is not null) daysSinceFreshSleep = 0;
        else
        {
            var sleepDates = input.History
                .Where(r => r.DateUtc.Date <= asOfDate && r.SleepMinutes is > 0)
                .Select(r => r.DateUtc.Date)
                .ToList();
            daysSinceFreshSleep = sleepDates.Count == 0
                ? 99                                              // no feed at all — the client's sentinel
                : Math.Max(0, (int)(asOfDate - sleepDates.Max()).TotalDays);
        }

        double completeness = CompletenessKeys.Count(k => metrics.TryGetValue(k, out var m) && m.Value is not null)
                              / (double)CompletenessKeys.Count;

        double usable = metrics.Values.Count(m => m.Quality == EngineQuality.Complete && m.BaselineConfidence >= EngineBaselineConfidence.Low);
        double confidence = Math.Clamp(usable / (double)Math.Max(metrics.Count - 1, 1), 0, 1);

        // ---- 5) context facts the rule layer reads directly from the input (calendar/screen/habits)
        //     They are OBSERVED (a calendar server reported them) or USER-PROVIDED (manual entries);
        //     a rule that quotes one of these ids must find it here — that is the never-fabricate chain.
        var contextFacts = new List<MetricFact>(facts);
        int meetingMinutes = EngineRuleEvaluator.MeetingMinutes(input);
        contextFacts.Add(new MetricFact(EngineRuleEvaluator.MeetingFactId, "calendar.meeting-minutes",
            meetingMinutes, "minutes", EngineProvenance.Observed, 0.9,
            $"calendar:{input.Calendar.Count(c => c.Kind == "meeting")} blocks", asOfDate));
        if (input.ScreenTimeMinutesToday is { } screen)
            contextFacts.Add(new MetricFact(EngineRuleEvaluator.ScreenTimeFactId, "screen.minutes",
                screen, "minutes", EngineProvenance.Observed, 0.85, "screen-time:today", asOfDate));
        contextFacts.Add(new MetricFact(EngineRuleEvaluator.StaleLedgerFactId, "feed.stale-days",
            daysSinceFreshSleep, "days", EngineProvenance.Inferred, 1.0,
            "derived:latest sleep.minutes record age", asOfDate));
        foreach (var h in input.Habits)
            contextFacts.Add(new MetricFact(EngineRuleEvaluator.InputFactId(h.HabitId), "habit.streak",
                h.Streak, "days", EngineProvenance.UserProvided, 0.9, $"habit:{h.HabitId}", asOfDate));

        return new StateSnapshot(asOfDate, metrics, contextFacts, completeness, confidence, daysSinceFreshSleep, assumptions);
    }

    // ---- metric key constants: the SAME dotted strings the client Metrics class uses -------------
    // (restated here because the server must not reference the client assembly; the parity test
    //  asserts every string equals the client Domain/Models/State/PersonalState.cs Metrics.* value.)
    public static class EngineMetrics
    {
        public const string SleepMinutes = "sleep.minutes";
        public const string SleepQuality = "sleep.quality";
        public const string SleepConsistency = "sleep.consistency";
        public const string BedtimeMinutes = "sleep.bedtime";
        public const string Steps = "activity.steps";
        public const string ActiveMinutes = "activity.minutes";
        public const string RecoveryScore = "recovery.score";
        public const string Stress = "wellness.stress";
        public const string Mood = "wellness.mood";
        public const string Energy = "wellness.energy";
        public const string FocusEstimate = "focus.estimate";
    }

    private static IEnumerable<(string Key, Func<EngineDayRecord, double> Selector)> BaselineSelectors()
    {
        yield return (EngineMetrics.SleepMinutes, r => r.SleepMinutes ?? double.NaN);
        yield return (EngineMetrics.SleepQuality, r => r.SleepQuality ?? double.NaN);
        yield return (EngineMetrics.SleepConsistency, r => r.SleepConsistency ?? double.NaN);
        yield return (EngineMetrics.BedtimeMinutes, r => r.BedtimeMinutesOfDay ?? double.NaN);
        yield return (EngineMetrics.Steps, r => r.Steps ?? double.NaN);
        yield return (EngineMetrics.ActiveMinutes, r => r.ActiveMinutes ?? double.NaN);
        yield return (EngineMetrics.RecoveryScore, r => r.RecoveryScore ?? double.NaN);
        yield return (EngineMetrics.Stress, r => r.Stress ?? double.NaN);
        yield return (EngineMetrics.Mood, r => r.Mood ?? double.NaN);
        yield return (EngineMetrics.Energy, r => r.Energy ?? double.NaN);
    }

    private static IEnumerable<(string Key, Func<double?> Get, bool HigherIsBetter)> TodaySelectors(EngineDayRecord d)
    {
        yield return (EngineMetrics.SleepMinutes, () => d.SleepMinutes, true);
        yield return (EngineMetrics.SleepQuality, () => d.SleepQuality, true);
        yield return (EngineMetrics.SleepConsistency, () => d.SleepConsistency, true);
        yield return (EngineMetrics.BedtimeMinutes, () => d.BedtimeMinutesOfDay, true);
        yield return (EngineMetrics.Steps, () => d.Steps, true);
        yield return (EngineMetrics.ActiveMinutes, () => d.ActiveMinutes, true);
        yield return (EngineMetrics.RecoveryScore, () => d.RecoveryScore, true);
        yield return (EngineMetrics.Stress, () => d.Stress, false);
        yield return (EngineMetrics.Mood, () => d.Mood, true);
        yield return (EngineMetrics.Energy, () => d.Energy, true);
    }

    /// <summary>Where a reading's provenance comes from. A day record tagged with no data at all
    /// (null value) never reaches here; presence + the record tag decide.</summary>
    private static EngineProvenance ProvenanceOf(EngineDayRecord today, string metricKey)
    {
        // Mood/Energy are only ever captured by check-ins today: a reading on those keys that
        // arrives tagged Observed is still the user's own report (self-knowledge), never a device
        // measurement — keeping the label honest prevents "observed mood" from existing.
        if (metricKey is EngineMetrics.Mood or EngineMetrics.Energy)
            return today.Provenance == EngineProvenance.Observed ? EngineProvenance.UserProvided : today.Provenance;
        return today.Provenance;
    }

    private static double ConfidenceOfReading(EngineProvenance p, EngineQuality q) => (p, q) switch
    {
        (_, EngineQuality.Missing) => 0,
        (EngineProvenance.Observed, _) => 0.95,
        (EngineProvenance.UserProvided, _) => 0.8,
        (EngineProvenance.Inferred, _) => 0.7,
        _ => 0.5, // assumed
    };

    private static string UnitFor(string metricKey) => metricKey switch
    {
        EngineMetrics.SleepMinutes or EngineMetrics.ActiveMinutes or EngineMetrics.BedtimeMinutes => "minutes",
        EngineMetrics.Steps => "steps",
        EngineMetrics.SleepQuality or EngineMetrics.SleepConsistency or EngineMetrics.RecoveryScore
            or EngineMetrics.Stress or EngineMetrics.Mood or EngineMetrics.Energy or EngineMetrics.FocusEstimate => "ratio",
        _ => "unit",
    };

    private static string Sanitize(string metricKey) => metricKey.Replace('.', '-');
}
