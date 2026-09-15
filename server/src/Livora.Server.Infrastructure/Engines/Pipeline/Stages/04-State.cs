namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 4 — state. Turns "value + personal baseline" into a polarity-aware verdict per signal and
/// names the cross-domain conclusions the rest of the pipeline is allowed to reason over. Banding
/// happens in exactly one place, so the same day can never be "poor sleep" to one stage and "fine"
/// to another. Pure: no fields, no clock, no IO — every input is a parameter.
/// <para>
/// PORTED thresholds (attribution; the client is the single source of truth for these numbers —
/// this lane must not quietly fork them):
///  - ±12% personal-baseline noise band: Domain/Models/State/PersonalState.cs:26 (MetricState.Level)
///  - sleep deficit 1.5 h below baseline; 2.5 h escalates to High: Application/Rules/RuleEngine.cs:16, 41
///  - recovery floor 0.55 absolute / -18% relative: RuleEngine.cs:17, 64
///  - stress ceiling 0.65; 0.80 High: RuleEngine.cs:18, 90
///  - activity deficit 45% below own baseline: RuleEngine.cs:19, 110
///  - stale allowance 2 days: RuleEngine.cs:20
///  - a Low-confidence baseline may not drive a firm reshape:
///    Application/Planning/Adaptive/PlanAdaptationEngine.cs:74-76
/// NEW server gates (meeting/screen load is server-side cross-domain input with no client
/// equivalent; each pinned by a boundary test): MeetingLoadHighDeviation, ScreenLoadHighDeviation,
/// MeetingLoadAbsoluteMinutes, ScreenLoadAbsoluteMinutes.
/// </para>
/// </summary>
public static class StateStage
{
    public const string StageName = "state";

    /// <summary>Client parity: PersonalState.cs:26.</summary>
    public const double NoiseBand = EngineMath.LevelBand;   // 0.12

    public const double SleepDeficitHours = 1.5;            // RuleEngine.cs:16
    public const double SleepDeficitHighHours = 2.5;        // RuleEngine.cs:41
    public const double RecoveryBelow = 0.55;               // RuleEngine.cs:17
    public const double RecoveryRelativeBelow = -0.18;      // RuleEngine.cs:64
    public const double StressAbove = 0.65;                 // RuleEngine.cs:18
    public const double StressHigh = 0.80;                  // RuleEngine.cs:90
    public const double ActivityDeficitFraction = 0.45;     // RuleEngine.cs:19
    public const int StaleAllowanceDays = 2;                // RuleEngine.cs:20

    public const double MeetingLoadHighDeviation = 0.35;
    public const double MeetingLoadAbsoluteMinutes = 180;
    public const double ScreenLoadHighDeviation = 0.35;
    public const double ScreenLoadAbsoluteMinutes = 300;

    /// <summary>Baseline confidence needed before a conclusion may shape a plan firmly.</summary>
    public const BaselineConfidence FirmClaimConfidence = BaselineConfidence.Medium;

    public static StateSnapshot Derive(FactSet facts, QualityReport quality, BaselineResult baselines)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(quality);
        ArgumentNullException.ThrowIfNull(baselines);

        var signals = new Dictionary<string, DomainSignal>(StringComparer.Ordinal);
        var trail = new List<TrailEntry>();

        DomainSignal Signal(string key, bool higherIsBetter)
        {
            var fact = quality.Fact(key);
            var baseline = baselines.Find(key);
            double? value = fact?.Value;
            double? baseValue = baseline is { Usable: true } ? baseline.Value : null;
            double? deviation = baseValue is null ? null : baseline!.DeviationOf(value);

            SignalLevel level;
            string verdict;
            if (value is null)
            {
                level = SignalLevel.Unknown;
                verdict = "missing";
            }
            else if (deviation is null)
            {
                level = SignalLevel.NoBaseline;   // early learning: the value is real, the verdict is not
                verdict = "no_baseline";
            }
            else if (Math.Abs(deviation.Value) <= NoiseBand)
            {
                level = SignalLevel.Normal;
                verdict = "normal";
            }
            else if (higherIsBetter)
            {
                level = deviation > 0 ? SignalLevel.BetterThanUsual : SignalLevel.WorseThanUsual;
                verdict = deviation > 0 ? "better" : "worse";
            }
            else
            {
                level = deviation > 0 ? SignalLevel.High : SignalLevel.BetterThanUsual;
                verdict = deviation > 0 ? "worse" : "better";
            }

            var s = new DomainSignal(key, value, baseValue, deviation, level,
                fact?.Grade ?? EvidenceGrade.Unrated,
                baseline?.Confidence ?? BaselineConfidence.None,
                fact?.EvidenceId is null ? Array.Empty<string>() : [fact.EvidenceId],
                verdict);
            signals[key] = s;
            return s;
        }

        // ---- polarity-aware signals -------------------------------------------------------------
        var sleep = Signal(FactKeys.SleepMinutes, higherIsBetter: true);
        var recovery = Signal(FactKeys.RecoveryScore, higherIsBetter: true);
        var meetings = Signal(FactKeys.MeetingMinutes, higherIsBetter: false);
        var screen = Signal(FactKeys.ScreenMinutes, higherIsBetter: false);
        var steps = Signal(FactKeys.Steps, higherIsBetter: true);
        var stress = Signal(FactKeys.Stress, higherIsBetter: false);

        double? sleepDeficitHours = sleep.Value is { } sv && sleep.Baseline is { } sb
            ? (sb - sv) / 60.0 : null;

        // ---- named cross-domain conclusions ------------------------------------------------------
        var conclusions = new List<StateConclusion>();
        void Conclude(string key, string signalKey, bool fired, string whenTrue, string whenFalse,
            IReadOnlyList<string> factors, IReadOnlyList<string> from)
        {
            var verdict = fired ? "fired" : "clear";
            conclusions.Add(new StateConclusion(key, signalKey, verdict, fired ? whenTrue : whenFalse, factors, from));
            trail.Add(TrailEntry.Of(StageName, key, verdict, factors, from));
        }

        foreach (var s in signals.Values.OrderBy(v => v.Key, StringComparer.Ordinal))
            trail.Add(TrailEntry.Of(StageName, "p1e.state." + s.Key, s.Verdict,
                [EngineMath.Factor("value", s.Value ?? double.NaN),
                 EngineMath.Factor("baseline", s.Baseline ?? double.NaN),
                 EngineMath.Factor("dev", s.Deviation ?? double.NaN),
                 EngineMath.Factor("confidence", s.Confidence.ToString().ToLowerInvariant()),
                 EngineMath.Factor("grade", s.Grade.Token())],
                s.EvidenceIds));

        Conclude("p1e.state.sleep_poor", FactKeys.SleepMinutes, sleepDeficitHours >= SleepDeficitHours,
            $"sleep below personal baseline by >= {SleepDeficitHours}h (ported: RuleEngine.SleepDeficitHours)",
            "no usable sleep deficit signal",
            [EngineMath.Factor("deficit_h", sleepDeficitHours ?? double.NaN)], signals[FactKeys.SleepMinutes].EvidenceIds);

        Conclude("p1e.state.meeting_load_high", FactKeys.MeetingMinutes,
            meetings.Deviation >= MeetingLoadHighDeviation || meetings.Value >= MeetingLoadAbsoluteMinutes,
            $"meeting minutes at/above +{EngineMath.Pct(MeetingLoadHighDeviation)} of own baseline or >= {MeetingLoadAbsoluteMinutes:0} min absolute",
            "meeting load within the personal band",
            [EngineMath.Factor("minutes", meetings.Value ?? double.NaN),
             EngineMath.Factor("dev", meetings.Deviation ?? double.NaN)],
            signals[FactKeys.MeetingMinutes].EvidenceIds);

        Conclude("p1e.state.screen_load_high", FactKeys.ScreenMinutes,
            screen.Deviation >= ScreenLoadHighDeviation || screen.Value >= ScreenLoadAbsoluteMinutes,
            $"screen minutes at/above +{EngineMath.Pct(ScreenLoadHighDeviation)} of own baseline or >= {ScreenLoadAbsoluteMinutes:0} min absolute",
            "screen time within the personal band",
            [EngineMath.Factor("minutes", screen.Value ?? double.NaN),
             EngineMath.Factor("dev", screen.Deviation ?? double.NaN)],
            signals[FactKeys.ScreenMinutes].EvidenceIds);

        Conclude("p1e.state.activity_low", FactKeys.Steps,
            steps.Value is { } stv && steps.Baseline is { } stb && stv < stb * (1 - ActivityDeficitFraction),
            $"steps below {(1 - ActivityDeficitFraction):0%} of own baseline (ported: RuleEngine.ActivityDeficit)",
            "activity within the personal band",
            [EngineMath.Factor("steps", steps.Value ?? double.NaN),
             EngineMath.Factor("baseline", steps.Baseline ?? double.NaN)],
            signals[FactKeys.Steps].EvidenceIds);

        Conclude("p1e.state.recovery_low", FactKeys.RecoveryScore,
            (recovery.Value ?? double.NaN) < RecoveryBelow || recovery.Deviation <= RecoveryRelativeBelow,
            $"recovery below {RecoveryBelow} absolute or {EngineMath.Pct(RecoveryRelativeBelow)} vs own baseline",
            "recovery adequate",
            [EngineMath.Factor("recovery", recovery.Value ?? double.NaN),
             EngineMath.Factor("dev", recovery.Deviation ?? double.NaN)],
            signals[FactKeys.RecoveryScore].EvidenceIds);

        Conclude("p1e.state.stress_high", FactKeys.Stress, stress.Value > StressAbove,
            $"stress above {StressAbove} (ported: RuleEngine.StressAbove)",
            "stress within range",
            [EngineMath.Factor("stress", stress.Value ?? double.NaN)],
            signals[FactKeys.Stress].EvidenceIds);

        bool trustworthy = quality.CanDecide && quality.Conflicts.Count == 0;
        Conclude("p1e.state.data_trustworthy", "", trustworthy,
            "quality gate passed",
            "completeness below the decision gate or conflicting sources recorded — the pipeline must say what is missing",
            [EngineMath.Factor("completeness", quality.Completeness),
             EngineMath.Factor("conflicts", quality.Conflicts.Count.ToString())],
            Array.Empty<string>());

        // A conclusion inherits the CEILING of its inputs' grades — never a fabricated grade, and
        // never "verified": a self-reported day cannot read SystemVerified.
        var grade = EvidenceGrades.Ceiling(signals.Values
            .Where(s => s.Value is not null).Select(s => s.Grade));

        return new StateSnapshot(signals, conclusions, grade, quality.Completeness, trail);
    }
}

/// <summary>Directional verdict for one signal in polarity-aware words, so "High" never means good
/// for one metric and bad for another.</summary>
public enum SignalLevel
{
    Unknown = 0,
    /// <summary>Value exists but no trustworthy personal baseline does (early learning).</summary>
    NoBaseline = 1,
    WorseThanUsual = 2,
    Normal = 3,
    /// <summary>A higher-is-worse signal sitting above its baseline band.</summary>
    High = 4,
    BetterThanUsual = 5,
}

/// <summary>One signal's derived state with its trust attached. Confidence is the BASELINE
/// confidence; Grade is the EVIDENCE grade — two different axes, never multiplied together.</summary>
public sealed record DomainSignal(
    string Key,
    double? Value,
    double? Baseline,
    double? Deviation,
    SignalLevel Level,
    EvidenceGrade Grade,
    BaselineConfidence Confidence,
    IReadOnlyList<string> EvidenceIds,
    string Verdict);

/// <summary>A named cross-domain conclusion: machine key + verdict + why + the fact ids it was
/// computed from. Later stages may only reason over these keys — that is what makes a cross-domain
/// test falsifiable: remove the sleep fact and the sleep conclusion must stop firing.</summary>
public sealed record StateConclusion(
    string Key,
    /// <summary>Which signal key this conclusion reads ("" for the aggregate data-trust gate) —
    /// the explicit bridge that lets stages 8+ ask "what grade/confidence is this built on?"
    /// without string heuristics.</summary>
    string SignalKey,
    string Verdict,
    string Why,
    IReadOnlyList<string> Factors,
    IReadOnlyList<string> EvidenceIds);

/// <summary>Stage 4 output.</summary>
public sealed record StateSnapshot(
    IReadOnlyDictionary<string, DomainSignal> Signals,
    IReadOnlyList<StateConclusion> Conclusions,
    EvidenceGrade EvidenceCeiling,
    double Completeness,
    IReadOnlyList<TrailEntry> Trail)
{
    public bool Fired(string conclusionKey) =>
        Conclusions.Any(c => c.Key == conclusionKey && c.Verdict == "fired");

    public StateConclusion? Find(string conclusionKey) =>
        Conclusions.FirstOrDefault(c => c.Key == conclusionKey);

    public DomainSignal Signal(string key) =>
        Signals.TryGetValue(key, out var s)
            ? s
            : new DomainSignal(key, null, null, null, SignalLevel.Unknown, EvidenceGrade.Unrated,
                BaselineConfidence.None, Array.Empty<string>(), "missing");

    /// <summary>The evidence grade a conclusion was built on (its own signal's grade, else the
    /// snapshot ceiling). Stages 8+ never touch a raw grade directly — they ask by conclusion key.</summary>
    public EvidenceGrade GradeFor(string conclusionKey)
    {
        var c = Conclusions.FirstOrDefault(x => string.Equals(x.Key, conclusionKey, StringComparison.Ordinal));
        if (c is null || c.SignalKey.Length == 0) return EvidenceCeiling;
        return Signals.TryGetValue(c.SignalKey, out var s) && s.Value is not null ? s.Grade : EvidenceGrade.Unrated;
    }

    public BaselineConfidence BaselineConfidenceFor(string conclusionKey)
    {
        var c = Conclusions.FirstOrDefault(x => string.Equals(x.Key, conclusionKey, StringComparison.Ordinal));
        if (c is null || c.SignalKey.Length == 0) return BaselineConfidence.None;
        return Signals.TryGetValue(c.SignalKey, out var s) ? s.Confidence : BaselineConfidence.None;
    }
}
