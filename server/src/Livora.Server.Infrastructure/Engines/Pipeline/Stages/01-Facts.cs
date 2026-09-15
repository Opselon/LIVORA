namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 1 — facts. The only input the whole intelligence lane accepts: named, typed, grade-stamped
/// observations. No engine stage may invent a value that is not in a fact or derived from one by a
/// named rule.
/// <para>
/// WHY A SEPARATE SHAPE FROM THE CLIENT'S DataPoint: the client model
/// (Domain/Models/Health/DataPoint.cs) carries <c>DataOrigin</c> + <c>DataQuality</c>, which mixes
/// "where it came from" with "is it usable", and has no place for verification grade. Server-side
/// intelligence needs the grade visible at the fact boundary, so a fact carries BOTH: a source
/// label (provenance) and an <see cref="EvidenceGrade"/> (trust). They are not redundant: a
/// provider-sourced fact can be merely DeviceDerived (their pipeline computed it), and only a
/// completed LIVORA probe/check upgrades it to SystemVerified.
/// </para>
/// </summary>
public sealed record Fact(
    string Key,
    double Value,
    string Unit,
    FactQuality Quality,
    EvidenceGrade Grade,
    DateTimeOffset ObservedAtUtc,
    string? SourceLabel = null,
    string? EvidenceId = null)
{
    public static Fact Of(string key, double value, string unit, EvidenceGrade grade,
        DateTimeOffset observedAtUtc, string? sourceLabel = null, string? evidenceId = null)
        => new(key, value, unit, FactQuality.Measured, grade, observedAtUtc, sourceLabel, evidenceId);

    /// <summary>An ESTIMATED fact: derived by a named rule from other facts. Still needs a grade —
    /// derivation does not create trust (a sum of self-reports is still self-reported).</summary>
    public static Fact Estimated(string key, double value, string unit, EvidenceGrade grade,
        DateTimeOffset observedAtUtc, string derivedByRule)
        => new(key, value, unit, FactQuality.Estimated, grade, observedAtUtc,
               SourceLabel: "derived:" + derivedByRule);

    public bool IsNumeric => !double.IsNaN(Value) && !double.IsInfinity(Value);
}

/// <summary>How complete a fact is. Distinct from trust: a complete fact can still be self-reported.</summary>
public enum FactQuality
{
    /// <summary>A real observation arrived.</summary>
    Measured = 0,
    /// <summary>Computed from other facts by a named rule (honest label, never presented as measured).</summary>
    Estimated = 1,
    /// <summary>The user was expected to answer and did not (or the feed went silent).</summary>
    Absent = 2,
    /// <summary>Arrived but failed a plausibility gate — usable for audit, never for a decision.</summary>
    Rejected = 3,
}

/// <summary>
/// The canonical fact keys. A stage may only read keys declared here, which is what makes a
/// "cross-domain reasoning" test falsifiable: if a rule claims to use screen time, the fact key must
/// appear in its trail.
/// </summary>
public static class FactKeys
{
    // sleep domain
    public const string SleepMinutes = "sleep.minutes";
    public const string SleepQuality = "sleep.quality";
    public const string BedtimeMinutesOfDay = "sleep.bedtime";

    // load / attention domain
    public const string MeetingMinutes = "load.meeting_minutes";
    public const string MeetingCount = "load.meeting_count";
    public const string ScreenMinutes = "load.screen_minutes";

    // activity / recovery domain
    public const string Steps = "activity.steps";
    public const string ActiveMinutes = "activity.active_minutes";
    public const string RecoveryScore = "recovery.score";
    public const string StrainScore = "recovery.strain";
    public const string Stress = "wellness.stress";

    // commitment domain (plan shapes, not health)
    public const string BootcampTargetMinutes = "plan.bootcamp_target_minutes";

    /// <summary>Every key the lane recognises (sweep-tested: a fixture cannot typo a fact key).</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        SleepMinutes, SleepQuality, BedtimeMinutesOfDay, MeetingMinutes, MeetingCount, ScreenMinutes,
        Steps, ActiveMinutes, RecoveryScore, StrainScore, Stress, BootcampTargetMinutes,
    ];
}

/// <summary>
/// An immutable bundle of facts for one user and one decision moment. Duplicate keys are resolved by
/// trust-then-freshness (deterministic, documented), never by "last write wins".
/// </summary>
public sealed class FactSet
{
    private readonly Dictionary<string, Fact> _byKey;

    public FactSet(IReadOnlyList<Fact> facts, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(facts);
        AsOfUtc = asOfUtc;
        _byKey = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var f in facts.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (_byKey.TryGetValue(f.Key, out var existing))
                _byKey[f.Key] = Prefer(f.Key, existing, f);
            else
                _byKey[f.Key] = f;
        }
        Items = _byKey.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        Raw = facts;
    }

    /// <summary>The untouched input list (before key dedupe) — the quality stage inspects it for
    /// cross-source conflicts; dropping the weaker duplicate silently would hide contradictions.</summary>
    public IReadOnlyList<Fact> Raw { get; }

    /// <summary>Key selection rule: higher grade wins; on a tie the fresher observation; on a tie
    /// again the ordinal-smaller source label. Deterministic and explainable.</summary>
    private static Fact Prefer(string key, Fact a, Fact b)
    {
        int byGrade = a.Grade.Rank().CompareTo(b.Grade.Rank());
        if (byGrade != 0) return byGrade > 0 ? a : b;
        int byTime = a.ObservedAtUtc.CompareTo(b.ObservedAtUtc);
        if (byTime != 0) return byTime > 0 ? a : b;
        return string.CompareOrdinal(a.SourceLabel ?? "", b.SourceLabel ?? "") <= 0 ? a : b;
    }

    public DateTimeOffset AsOfUtc { get; }
    public IReadOnlyList<Fact> Items { get; }

    public Fact? Find(string key) => _byKey.TryGetValue(key, out var f) ? f : null;
    public bool Has(string key) => _byKey.ContainsKey(key);
    public IReadOnlyCollection<string> Keys => _byKey.Keys;
}

/// <summary>
/// STAGE 1b — fact-set construction helpers. These exist so a lane (or a test) builds a coherent
/// snapshot in one call instead of hand-picking numbers, and so the derivation rules that DO create
/// estimated facts are named and testable rather than scattered.
/// </summary>
public static class FactSets
{
    /// <summary>
    /// The four-domain "hard day" snapshot the brief demands be reasoned about coherently. Values
    /// come from the caller; nothing here decides what "poor" means (that is stage 2/3's job).
    /// </summary>
    public static FactSet Build(
        DateTimeOffset observedAtUtc,
        double? sleepMinutes, EvidenceGrade sleepGrade,
        double? meetingMinutes, EvidenceGrade meetingGrade,
        double? screenMinutes, EvidenceGrade screenGrade,
        double? steps, EvidenceGrade stepsGrade,
        double? activeMinutes = null, double? recoveryScore = null, EvidenceGrade? recoveryGrade = null,
        double? bedtimeMinutesOfDay = null, double? meetingCount = null, double? strain = null,
        double? bootcampTargetMinutes = null)
    {
        var list = new List<Fact>();
        void Add(string key, double? value, string unit, EvidenceGrade grade)
        {
            if (value is null) return;
            list.Add(Fact.Of(key, value.Value, unit, grade, observedAtUtc, grade.Token()));
        }

        Add(FactKeys.SleepMinutes, sleepMinutes, "minutes", sleepGrade);
        if (bedtimeMinutesOfDay is { } bt) Add(FactKeys.BedtimeMinutesOfDay, bt, "minutes_of_day", sleepGrade);
        Add(FactKeys.MeetingMinutes, meetingMinutes, "minutes", meetingGrade);
        if (meetingCount is { } mc) Add(FactKeys.MeetingCount, mc, "count", meetingGrade);
        Add(FactKeys.ScreenMinutes, screenMinutes, "minutes", screenGrade);
        Add(FactKeys.Steps, steps, "steps", stepsGrade);
        if (activeMinutes is { } am) Add(FactKeys.ActiveMinutes, am, "minutes", stepsGrade);
        if (recoveryScore is { } rs) Add(FactKeys.RecoveryScore, rs, "fraction", recoveryGrade ?? stepsGrade);
        if (strain is { } st) Add(FactKeys.StrainScore, st, "index", recoveryGrade ?? stepsGrade);
        if (bootcampTargetMinutes is { } bm) Add(FactKeys.BootcampTargetMinutes, bm, "minutes", EvidenceGrade.SelfReported);
        return new FactSet(list, observedAtUtc);
    }
}
