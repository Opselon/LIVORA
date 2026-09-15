namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: the single numeric spine of the deterministic decision pipeline. Every constant here
///          RESTATS a client-side rule (Application/Rules/RuleEngine.cs, Application/State/*,
///          Application/Planning/Adaptive/PlanAdaptationEngine.cs, Application/Insights/WeeklySummaryService.cs,
///          Application/Patterns/PatternEngine.cs) so server and client cannot drift silently:
///          the semantic-parity test (Engines/SemanticParityTests.cs) reads BOTH source files and
///          compares these names and values verbatim. Change one side without the other => red gate.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Pure: no EF, no HTTP, no clock, no AI.
/// INVARIANTS:
///   - constants are named, never inlined at use sites (a magic 0.55 in a condition is how drift starts)
///   - all functions are pure and total: they return an honest "unknown/insufficient" instead of
///     guessing when their inputs are not there (product law: say nothing rather than fabricate)
/// </summary>
public static class NumericRules
{
    // ============================ personal baseline ============================
    // Mirror of LIVORA.Application.State.BaselineService + Baseline.FromSamples.
    public const int BaselineWindowDays = 28;        // rolling window of the user's OWN history
    public const int NoneBelowSamples = 3;           // < 3 samples  -> None (too few to know anything)
    public const int LowBelowSamples = 7;            // < 7          -> Low (early learning)
    public const int MediumBelowSamples = 14;        // < 14         -> Medium, else High

    // ================================ trend ===================================
    // Mirror of TrendService: first-half mean vs second-half mean with a noise band.
    public const int TrendMinSamples = 5;            // fewer => InsufficientData (no fake conclusions)
    public const double TrendBandFraction = 0.06;    // 6% swing = still "stable"

    // ============================ level / badness =============================
    public const double LevelBand = 0.12;            // ±12% of personal baseline is noise, not signal

    // ============================== rule engine ===============================
    // Mirror of RuleEngine thresholds (names kept for the parity test).
    public const double SleepDeficitHours = 1.5;     // below personal baseline by this => sleep debt
    public const double RecoveryBelow = 0.55;        // absolute floor for "low recovery"
    public const double StressAbove = 0.65;          // absolute ceiling for "high stress"
    public const double ActivityDeficitFraction = 0.45; // steps < 55% of baseline => deficit
    public const double StaleDaysAllowance = 2;      // feed older than this => stale handling
    public const double SleepDeficitHighHours = 2.5; // deficit >= this => High priority (client rule)
    public const double StressHighAbove = 0.8;       // stress above this => High priority
    public const double RecoveryBaselineDrop = -0.18;// relative deviation under this => low recovery
    public const double MomentumRecoveryFloor = 0.65;// >= this counts as good for momentum
    public const double MomentumStressCeiling = 0.5; // < this counts as calm for momentum
    public const int HabitRiskHour = 18;             // nothing logged by 18:00 => at-risk
    public const int HabitRiskStreak = 3;            // streak >= 3 days to be worth protecting

    // rule confidences (client values, verbatim)
    public const double ConfSleepDebtHigh = 0.9;
    public const double ConfSleepDebtMedium = 0.8;
    public const double ConfSleepDebtLow = 0.6;
    public const double ConfSleepDebtReduce = 0.85;
    public const double ConfLowRecovery = 0.8;
    public const double ConfHighStress = 0.75;
    public const double ConfHighStressScreens = 0.7;
    public const double ConfActivityDeficit = 0.75;
    public const double ConfMomentum = 0.7;
    public const double ConfHabitAtRisk = 0.85;
    public const double StaleConfidence = 0.3;       // PlanAdaptationEngine.StaleConfidence

    // ========================= plan adaptation (fusion) =======================
    // Mirror of PlanAdaptationEngine constants.
    public static readonly IReadOnlyList<int> RecoveryLadderMinutes = new[] { 45, 30, 20 };
    public const int ShrinkFloorMinutes = 15;
    public const int RecoveryItemMinutes = 15;
    public const int WindDownMinutes = 20;
    public const double SleepDeviationTrigger = -0.15;       // "at or below" -15% vs baseline
    public const double HighActivityDeviationTrigger = 0.25; // "at least" +25% above baseline
    public const int StaleDaysTrigger = 2;
    public const int DeadlineRiskDays = 30;
    public const double DeadlineRiskFractionCeiling = 0.5;
    public const double TotalMinutesCapFactor = 1.2;         // committed minutes <= 1.2x base total
    public const int FocusClampMorningMinutes = 7 * 60;      // focus never shifts before 07:00
    public const int FocusShiftMinutes = 120;                // sleep-low shifts earliest focus 2h earlier

    // =========================== weekly review ================================
    // Mirror of WeeklySummaryService: the refusal gate and the window.
    public const int WeeklyMinDays = 3;              // < 3 days of history => say nothing (null)
    public const int WeeklyWindowDays = 7;

    // =========================== pattern engine ===============================
    // Mirror of PatternEngine gates (subset the server ports; names kept verbatim).
    public const int MinHistoryDays = 21;
    public const int GateLateSleepNights = 7;
    public const int LateSleepNightsToFire = 3;
    public const double LateSleepThresholdMinutes = 60;
    public const int GateWeekdaySamples = 4;
    public const double WeekdayDipThreshold = -0.20;
    public const int GateFocusPairs = 8;
    public const double PoorSleepDeviation = -0.15;
    public const double FocusCoOccurrenceToFire = 0.50;
    public const int GateStagnationDays = 10;
    public const int StagnationDeadlineWindowDays = 30;
    public const int GateCouplingPairs = 14;
    public const double CouplingMinMagnitude = 0.30;

    // ==================== baseline windows (Wave3b BaselineEngine) ============
    public const int GateWindowDays7 = 4;
    public const int GateWindowDays14 = 8;
    public const int GateWindowDays30 = 16;

    // ===================== server-side fusion additions =======================
    // These are NEW server constants (the client has no calendar/screen-time ingestion yet);
    // they are listed as declared deviations in docs/architecture/wave4/requests/p1e-engines.md.
    public const int MeetingLoadHighMinutes = 240;   // >= 4h of meetings => protect ONE focus block
    public const int ScreenTimeHighMinutes = 270;    // >= 4.5h screen => wind-down matters tonight
    public const int FusedWalkMinutes = 20;          // the coherent plan asks for a 20-min walk
    public const int FocusedBlockMinutes = 50;       // one deep-work block
    public const int MaxHighPriorityRecommendations = 1; // budget: 1 high + 2 medium/low, never 17
    public const int MaxTotalRecommendations = 3;
    public const int ConflictSlotStepMinutes = 30;   // free-slot scan granularity (deterministic)
    public const int ConflictLastSlotStartMinutes = 20 * 60; // nothing moves past 20:00
    public const int MinWorkoutMinutesToKeep = 20;   // shorter than this -> skip beats a crumb

    /// <summary>The named numeric spine, exposed for the /rules catalog endpoint. Keys are the
    /// constant names, so the client can pin a threshold against drift without reading source.</summary>
    public static IReadOnlyDictionary<string, double> KnownConstants() => new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [nameof(SleepDeficitHours)] = SleepDeficitHours,
        [nameof(RecoveryBelow)] = RecoveryBelow,
        [nameof(StressAbove)] = StressAbove,
        [nameof(ActivityDeficitFraction)] = ActivityDeficitFraction,
        [nameof(StaleDaysAllowance)] = StaleDaysAllowance,
        [nameof(SleepDeficitHighHours)] = SleepDeficitHighHours,
        [nameof(StressHighAbove)] = StressHighAbove,
        [nameof(RecoveryBaselineDrop)] = RecoveryBaselineDrop,
        [nameof(MomentumRecoveryFloor)] = MomentumRecoveryFloor,
        [nameof(MomentumStressCeiling)] = MomentumStressCeiling,
        [nameof(TrendBandFraction)] = TrendBandFraction,
        [nameof(LevelBand)] = LevelBand,
        [nameof(SleepDeviationTrigger)] = SleepDeviationTrigger,
        [nameof(HighActivityDeviationTrigger)] = HighActivityDeviationTrigger,
        [nameof(TotalMinutesCapFactor)] = TotalMinutesCapFactor,
        [nameof(MeetingLoadHighMinutes)] = MeetingLoadHighMinutes,
        [nameof(ScreenTimeHighMinutes)] = ScreenTimeHighMinutes,
    };

    /// <summary>Sample-count learning curve, identical to Baseline.FromSamples.</summary>
    public static EngineBaselineConfidence ConfidenceForSampleCount(int samples) => samples switch
    {
        < NoneBelowSamples => EngineBaselineConfidence.None,
        < LowBelowSamples => EngineBaselineConfidence.Low,
        < MediumBelowSamples => EngineBaselineConfidence.Medium,
        _ => EngineBaselineConfidence.High,
    };

    /// <summary>Baseline mean/std/n over clean samples (NaN and negatives discarded, client rule).</summary>
    public static (double Mean, double StdDev, int N) MeanStdDev(IReadOnlyList<double> samples)
    {
        var clean = samples.Where(s => !double.IsNaN(s) && s >= 0).ToList();
        if (clean.Count == 0) return (double.NaN, double.NaN, 0);
        double mean = clean.Average();
        double variance = clean.Sum(x => (x - mean) * (x - mean)) / clean.Count;
        return (mean, Math.Sqrt(variance), clean.Count);
    }

    /// <summary>Trend = first-half mean vs second-half mean, 6% noise band, &lt;5 samples refuses.</summary>
    public static EngineTrend ClassifyTrend(IReadOnlyList<double> values, bool higherIsBetter)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count < TrendMinSamples) return EngineTrend.InsufficientData;

        int half = v.Count / 2;
        double first = v.Take(half).Average();
        double second = v.Skip(half).Average();
        double scale = Math.Max(Math.Abs(first), 1e-6);
        double change = (second - first) / scale;

        if (Math.Abs(change) < TrendBandFraction) return EngineTrend.Stable;
        bool improving = higherIsBetter ? change > 0 : change < 0;
        return improving ? EngineTrend.Improving : EngineTrend.Declining;
    }

    /// <summary>Relative deviation vs baseline; null when there is no usable baseline (never 0).</summary>
    public static double? RelativeDeviation(double value, double? baselineValue, EngineBaselineConfidence confidence)
    {
        if (baselineValue is null || baselineValue <= 0 || confidence == EngineBaselineConfidence.None)
            return null;
        var d = (value - baselineValue.Value) / baselineValue.Value;
        return double.IsNaN(d) ? null : d;
    }

    /// <summary>±12% band = Normal; missing signal = Unknown (never a fake level).</summary>
    public static EngineLevel LevelFor(double? relativeDeviation)
    {
        if (relativeDeviation is null) return EngineLevel.Unknown;
        var d = relativeDeviation.Value;
        return d > LevelBand ? EngineLevel.AboveBaseline : d < -LevelBand ? EngineLevel.BelowBaseline : EngineLevel.Normal;
    }

    /// <summary>Direction-aware severity: positive = worse than baseline regardless of polarity.</summary>
    public static double SignedBadness(double? relativeDeviation, bool higherIsBetter) =>
        relativeDeviation is null ? 0 : (higherIsBetter ? -relativeDeviation.Value : relativeDeviation.Value);

    /// <summary>Bedtimes near midnight break naive averaging: map to a post-noon scale (client rule).</summary>
    public static double WrapBedtime(double minutesOfDay) => minutesOfDay < 12 * 60 ? minutesOfDay + 1440 : minutesOfDay;

    /// <summary>Undo <see cref="WrapBedtime"/> averaging (one shared implementation, client parity).</summary>
    public static double UnwrapBedtime(double avg) => avg >= 1440 ? avg - 1440 : avg;

    /// <summary>Qualitative confidence of a baseline tier (PlanAdaptationEngine.ConfidenceOf parity).</summary>
    public static double ConfidenceOf(EngineBaselineConfidence c) => c switch
    {
        EngineBaselineConfidence.High => 0.9,
        EngineBaselineConfidence.Medium => 0.75,
        EngineBaselineConfidence.Low => 0.5,
        _ => 0.0,
    };

    /// <summary>Rounded signed-badness percent, the way adaptations cite it (client parity).</summary>
    public static int BadnessPercent(double signedBadness) =>
        (int)Math.Round(signedBadness * 100, MidpointRounding.AwayFromZero);
}
