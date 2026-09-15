namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 2 — data quality. Every fact must pass three gates before a decision stage may use it:
/// plausibility (physically impossible values are refused), freshness (a stale signal describes
/// the past, never "today"), and presence (expected keys that never arrived are stated, not
/// zero-filled). Failures are demoted with a machine reason and stay visible in the trail —
/// silently dropping a fact would make "no data" indistinguishable from "bad data".
/// <para>
/// Ported thresholds (attributed, never silently reinvented):
///  - the Missing/Invalid exclusion idea + "never zero-fill" law:
///    Application/State/Wave3b/PersonalStateProjector.cs:18-20 and BaselineEngine.cs:150-153
///  - stale-is-history, not now: Domain/Models/Health/DataPoint.cs:26-30 (DataPoint.WithMaxAge)
///  - completeness fraction over expected fields: Domain/Models/Health/DataPoint.cs:57-65
///    (NormalizedDay.Completeness) — here the expected set is profile-driven.
/// NEW server gates (no client equivalent; each pinned by a boundary test):
///  - FreshWindowHours* per domain, Max* plausibility bounds, MinCompletenessToDecide.
/// </para>
/// </summary>
public static class DataQualityStage
{
    public const string StageName = "data_quality";

    // ---- freshness windows (hours) — "how old before a signal stops describing *today*" --------
    public const int FreshWindowHoursSleep = 24;      // a night's sleep is known by the next evening
    public const int FreshWindowHoursDaily = 36;      // steps / meetings / screen roll over daily
    public const int FreshWindowHoursRecovery = 12;   // recovery is a "this morning" number

    // ---- plausibility bounds (impossible values are bad data, not extreme users) ---------------
    public const double MaxSleepMinutes = 1200;       // 20 h
    public const double MaxSteps = 150_000;
    public const double MaxMinutesPerDay = 1440;      // any minutes-of-day / minutes-per-day value
    public const double MaxFraction = 1.0;            // 0..1 signals
    public const double MinFraction = 0.0;

    /// <summary>Below this fraction of expected facts usable, the pipeline must answer
    /// "insufficient data" instead of recommending — safe degradation, product law 3.</summary>
    public const double MinCompletenessToDecide = 0.5;

    /// <summary>Two sources disagreeing by more than this relative gap on the same key is a
    /// conflict, recorded even though the higher grade wins the dedupe.</summary>
    public const double ConflictRelativeGap = 0.25;

    /// <summary>The expected fact set per decision profile. A decision may only claim
    /// completeness over keys it actually asked for.</summary>
    public static IReadOnlyList<string> ExpectedKeysFor(DecisionProfile profile) => profile switch
    {
        DecisionProfile.DailyPlan =>
        [
            FactKeys.SleepMinutes, FactKeys.MeetingMinutes, FactKeys.ScreenMinutes,
            FactKeys.Steps, FactKeys.RecoveryScore,
        ],
        DecisionProfile.PlanPlacement => [FactKeys.SleepMinutes, FactKeys.MeetingMinutes, FactKeys.Steps],
        DecisionProfile.Full => FactKeys.All,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    /// <summary>Which freshness window guards a key.</summary>
    public static int FreshWindowHoursFor(string key) => key switch
    {
        FactKeys.SleepMinutes or FactKeys.SleepQuality or FactKeys.BedtimeMinutesOfDay
            => FreshWindowHoursSleep,
        FactKeys.RecoveryScore or FactKeys.StrainScore => FreshWindowHoursRecovery,
        _ => FreshWindowHoursDaily,
    };

    /// <summary>The plausible range for a key (null bounds = unbounded above).</summary>
    public static (double Min, double Max) PlausibleRangeFor(string key) => key switch
    {
        FactKeys.SleepMinutes or FactKeys.MeetingMinutes or FactKeys.ScreenMinutes
            or FactKeys.ActiveMinutes or FactKeys.BootcampTargetMinutes => (0, MaxMinutesPerDay),
        FactKeys.Steps => (0, MaxSteps),
        FactKeys.SleepQuality or FactKeys.RecoveryScore => (MinFraction, MaxFraction),
        FactKeys.BedtimeMinutesOfDay => (0, MaxMinutesPerDay),
        _ => (0, double.PositiveInfinity),
    };

    public static QualityReport Assess(FactSet facts, IReadOnlyList<string> expectedKeys)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(expectedKeys);

        var verdicts = new Dictionary<string, FactVerdict>(StringComparer.Ordinal);
        var trail = new List<TrailEntry>();

        // 1) verdicts for every raw fact row (ordinal key, then time = deterministic trail)
        foreach (var fact in facts.Raw.OrderBy(f => f.Key, StringComparer.Ordinal)
                     .ThenBy(f => f.ObservedAtUtc.UtcTicks))
        {
            var (usableOk, reason) = Inspect(fact, facts.AsOfUtc);
            int sourceCount = facts.Raw.Count(f => f.Key == fact.Key);
            verdicts[FactFingerprint(fact)] = new FactVerdict(fact.Key, usableOk, reason, fact.Quality,
                fact.Grade, usableOk ? fact.Value : null, AgeHours(fact, facts.AsOfUtc), sourceCount);
            trail.Add(TrailEntry.Of(StageName, "p1e.quality." + reason, usableOk ? "accepted" : "demoted",
                [EngineMath.Factor("key", fact.Key), EngineMath.Factor("grade", fact.Grade.Token()),
                 EngineMath.Factor("age_h", AgeHours(fact, facts.AsOfUtc))],
                fact.EvidenceId is null ? Array.Empty<string>() : [fact.EvidenceId]));
        }

        // The usable slot takes FactSet's PREFERRED copy (highest grade, then freshest — see
        // FactSet.Prefer) but only if it passed the gates; a preferred-but-stale key stays absent
        // rather than falling back to a weaker, older duplicate of the same signal.
        var usable = new Dictionary<string, Fact>(StringComparer.Ordinal);
        foreach (var fact in facts.Items)
        {
            if (verdicts.TryGetValue(FactFingerprint(fact), out var v) && v.Usable) usable[fact.Key] = fact;
        }

        // 2) absent expected keys are stated, never zero-filled
        foreach (var key in expectedKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!usable.ContainsKey(key))
            {
                trail.Add(TrailEntry.Of(StageName, "p1e.quality.absent", "demoted",
                    [EngineMath.Factor("key", key)]));
            }
        }

        // 3) cross-source conflicts on the same key (values differing by > ConflictRelativeGap)
        var conflicts = new List<string>();
        foreach (var group in facts.Raw.GroupBy(f => f.Key, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var sorted = group.OrderBy(f => f.Value).ToList();
            double gap = sorted[^1].Value == 0
                ? (sorted[^1].Value > sorted[0].Value ? double.PositiveInfinity : 0)
                : Math.Abs((sorted[^1].Value - sorted[0].Value) / Math.Max(Math.Abs(sorted[0].Value), 1e-9));
            if (gap > ConflictRelativeGap)
            {
                var token = $"{group.Key}:{string.Join("_vs_", group
                    .OrderBy(f => f.Grade.Rank()).Select(f => f.Grade.Token()))}";
                conflicts.Add(token);
                trail.Add(TrailEntry.Of(StageName, "p1e.quality.conflict", "conflict_recorded",
                    [EngineMath.Factor("key", group.Key), EngineMath.Factor("gap", gap)],
                    group.Select(f => f.EvidenceId ?? f.Key).ToList()));
            }
        }

        int usableCount = expectedKeys.Count(usable.ContainsKey);
        double completeness = expectedKeys.Count == 0 ? 1.0 : usableCount / (double)expectedKeys.Count;
        trail.Add(TrailEntry.Of(StageName, "p1e.quality.completeness", "summary",
            [EngineMath.Factor("completeness", completeness, "0.###"),
             EngineMath.Factor("expected", expectedKeys.Count.ToString()),
             EngineMath.Factor("usable", usableCount.ToString())]));

        return new QualityReport(completeness, usable, conflicts, trail);
    }

    private static (bool, string) Inspect(Fact fact, DateTimeOffset asOfUtc)
    {
        if (!fact.IsNumeric) return (false, "absent");
        if (fact.Quality is FactQuality.Absent or FactQuality.Rejected) return (false, "absent");

        var (min, max) = PlausibleRangeFor(fact.Key);
        if (fact.Value < min || fact.Value > max) return (false, "rejected_plausible_range");

        if (AgeHours(fact, asOfUtc) > FreshWindowHoursFor(fact.Key)) return (false, "stale");
        return (true, "ok");
    }

    private static int AgeHours(Fact fact, DateTimeOffset asOfUtc)
    {
        var hours = (int)Math.Floor((asOfUtc - fact.ObservedAtUtc).TotalHours);
        return Math.Max(0, hours);   // clock skew can never INFLATE trust (client parity:
                                     // StateConfidenceCalculator.cs:131-132)
    }

    private static string FactFingerprint(Fact fact) =>
        $"{fact.Key}@{fact.ObservedAtUtc.UtcTicks}";
}

/// <summary>Which facts a decision needs. Keeps the completeness denominator honest.</summary>
public enum DecisionProfile { DailyPlan, PlanPlacement, Full }

/// <summary>Per-fact verdict. <see cref="Fingerprint"/> ties the verdict to the exact raw fact
/// row it judged (a deduped-away duplicate still has its own verdict in the trail).</summary>
public sealed record FactVerdict(
    string Key,
    bool Usable,
    string Reason,
    FactQuality Quality,
    EvidenceGrade Grade,
    double? Value,
    int AgeHours,
    int SourceCount);

/// <summary>What stage 2 hands downstream: the usable facts (deduped, gated), the completeness
/// ratio over the expected set, recorded conflicts, and the full trail.</summary>
public sealed record QualityReport(
    double Completeness,
    IReadOnlyDictionary<string, Fact> Usable,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<TrailEntry> Trail)
{
    /// <summary>The gate every consuming stage checks first.</summary>
    public bool CanDecide => Completeness >= DataQualityStage.MinCompletenessToDecide;

    public Fact? Fact(string key) => Usable.TryGetValue(key, out var f) ? f : null;
}
