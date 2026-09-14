namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 5 — patterns. Longitudinal regularities over the user's own history that a single day's
/// state cannot show. Every finding names its gate, how many samples earned it, and the dates it
/// covers, so a reviewer can recompute it from the same rows or falsify it by removing one row.
/// <para>
/// PORTED gates (Application/Patterns/PatternEngine.cs — the client is the source of truth):
///  - MinHistoryDays = 21 (PatternEngine.cs:75) — below that nothing is reported at all
///  - late-sleep pattern: 7 of the last 7 nights recorded (GateLateSleepNights, :78),
///    ≥ 3 qualifying nights (LateSleepNightsToFire, :79), bedtime > baseline + 60 min
///    (LateSleepThresholdMinutes, :80)
///  - weekday activity dip: ≥ 4 observations of that weekday (GateWeekdaySamples, :82),
///    −20% steps vs the personal mean (WeekdayDipThreshold, :83)
///  - poor-sleep → next-day coupling: ≥ 8 pairs (GateFocusPairs, :85), poor sleep = −15%
///    (PoorSleepDeviation, :86), fires when it happens more often than not (:87)
/// The client's PatternKind enum names are mirrored so a synced payload means the same thing on
/// both sides (LIVORA.Domain.Enums.PatternKind: LateSleepRecurring, WeekdayActivityDip,
/// FocusAfterPoorSleep) — the server reports the coupling as next-day *activity* because that is
/// the pair this lane's history rows carry (no measured focus feed exists — see
/// PersonalStateProjector.cs:22-23, "no direct focus measurement exists yet").
/// </para>
/// </summary>
public static class PatternStage
{
    public const string StageName = "patterns";

    public const int MinHistoryDays = 21;              // PatternEngine.cs:75
    public const int GateLateSleepNights = 7;          // PatternEngine.cs:78
    public const int LateSleepNightsToFire = 3;        // PatternEngine.cs:79
    public const double LateSleepThresholdMinutes = 60; // PatternEngine.cs:80
    public const int GateWeekdaySamples = 4;           // PatternEngine.cs:82
    public const double WeekdayDipThreshold = -0.20;   // PatternEngine.cs:83
    public const int GateCouplingPairs = 8;            // PatternEngine.cs:85
    public const double PoorSleepDeviation = -0.15;    // PatternEngine.cs:86
    public const double CouplingToFire = 0.50;         // PatternEngine.cs:87

    public static IReadOnlyList<PatternFinding> Assess(
        IReadOnlyList<HistoryDay> history, BaselineResult baselines, DateTime asOfDate)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(baselines);

        var rows = history.Where(d => d.DateUtc.Date <= asOfDate.Date)
                          .OrderBy(d => d.DateUtc.Date).ToList();
        var trail = new List<TrailEntry>();
        var findings = new List<PatternFinding>();

        int distinctDays = rows.Select(r => r.DateUtc.Date).Distinct().Count();
        if (distinctDays < MinHistoryDays)
        {
            trail.Add(TrailEntry.Of(StageName, "p1e.pattern.min_history", "refused",
                [EngineMath.Factor("distinct_days", distinctDays.ToString()),
                 EngineMath.Factor("gate", MinHistoryDays.ToString())]));
            return findings;
        }

        // ---- 1) recurring late sleep ----------------------------------------------------------
        var sleepBaseline = baselines.Find(FactKeys.SleepMinutes);
        var bedtimeBaseline = baselines.Find(FactKeys.BedtimeMinutesOfDay);
        var lastSeven = rows.Where(r => r.DateUtc.Date > asOfDate.Date.AddDays(-GateLateSleepNights))
                            .Where(r => r.BedtimeMinutesOfDay is > 0).ToList();
        if (bedtimeBaseline is { Usable: true } && lastSeven.Count >= GateLateSleepNights)
        {
            double baseWrapped = BaselineStage.WrapBedtime(bedtimeBaseline.Value);
            var late = lastSeven
                .Where(r => BaselineStage.WrapBedtime(r.BedtimeMinutesOfDay!.Value)
                            > baseWrapped + LateSleepThresholdMinutes)
                .ToList();
            trail.Add(TrailEntry.Of(StageName, "p1e.pattern.late_sleep_recurring",
                late.Count >= LateSleepNightsToFire ? "fired" : "below_gate",
                [EngineMath.Factor("nights_late", late.Count.ToString()),
                 EngineMath.Factor("nights_observed", lastSeven.Count.ToString()),
                 EngineMath.Factor("gate", LateSleepNightsToFire.ToString())]));
            if (late.Count >= LateSleepNightsToFire)
                findings.Add(new PatternFinding(PatternKind.LateSleepRecurring, "fired",
                    $"bedtime more than {LateSleepThresholdMinutes:0} min past the personal normal on " +
                    $"{late.Count} of the last {lastSeven.Count} nights",
                    late.Count, lastSeven.Count,
                    ConfidenceFromRatio(late.Count, lastSeven.Count),
                    late.Select(r => r.DateUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList()));
        }
        else
        {
            trail.Add(TrailEntry.Of(StageName, "p1e.pattern.late_sleep_recurring", "gate_unmet",
                [EngineMath.Factor("nights_observed", lastSeven.Count.ToString()),
                 EngineMath.Factor("gate", GateLateSleepNights.ToString()),
                 EngineMath.Factor("baseline", sleepBaseline is { Usable: true } ? "usable" : "unusable")]));
        }

        // ---- 2) weekday activity dip ----------------------------------------------------------
        var stepBaseline = baselines.Find(FactKeys.Steps);
        if (stepBaseline is { Usable: true })
        {
            var byDow = rows.Where(r => r.Steps is { } s && s >= 0)
                            .GroupBy(r => r.DateUtc.Date.DayOfWeek)
                            .OrderBy(g => (int)g.Key)
                            .ToList();
            foreach (var g in byDow)
            {
                if (g.Count() < GateWeekdaySamples) continue;
                double mean = g.Average(r => r.Steps!.Value);
                double dev = stepBaseline.Value == 0 ? 0 : (mean - stepBaseline.Value) / stepBaseline.Value;
                if (dev > WeekdayDipThreshold) continue;
                findings.Add(new PatternFinding(PatternKind.WeekdayActivityDip, "fired",
                    $"{g.Key} averages {EngineMath.Pct(dev)} vs the personal steps baseline",
                    g.Count(), rows.Count, ConfidenceFromRatio(g.Count(), 10),
                    g.Select(r => r.DateUtc.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList()));
                trail.Add(TrailEntry.Of(StageName, "p1e.pattern.weekday_activity_dip", "fired",
                    [EngineMath.Factor("day", g.Key.ToString()), EngineMath.Factor("samples", g.Count().ToString()),
                     EngineMath.Factor("dev", dev)],
                    Array.Empty<string>()));
            }
            if (!findings.Any(f => f.Kind == PatternKind.WeekdayActivityDip))
                trail.Add(TrailEntry.Of(StageName, "p1e.pattern.weekday_activity_dip", "clear",
                    [EngineMath.Factor("gate", GateWeekdaySamples.ToString()),
                     EngineMath.Factor("threshold", WeekdayDipThreshold)]));
        }

        // ---- 3) poor sleep → next-day low activity (lag-1 coupling) ---------------------------
        var pairs = 0; var hits = 0;
        var coveredDates = new List<string>();
        for (int i = 1; i < rows.Count; i++)
        {
            var prev = rows[i - 1];
            var cur = rows[i];
            if (prev.SleepMinutes is not { } pm || sleepBaseline is not { Usable: true }) continue;
            if (cur.Steps is not { } cs || stepBaseline is not { Usable: true }) continue;
            double sleepDev = (pm - sleepBaseline.Value) / sleepBaseline.Value;
            if (sleepDev > PoorSleepDeviation) continue;      // previous night was not poor
            pairs++;
            double stepsDev = (cs - stepBaseline.Value) / stepBaseline.Value;
            if (stepsDev < -StateStage.NoiseBand)
            {
                hits++;
                coveredDates.Add(cur.DateUtc.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        bool couplingFired = pairs >= GateCouplingPairs && (double)hits / Math.Max(pairs, 1) > CouplingToFire;
        trail.Add(TrailEntry.Of(StageName, "p1e.pattern.poor_sleep_low_activity",
            couplingFired ? "fired" : pairs < GateCouplingPairs ? "gate_unmet" : "below_rate",
            [EngineMath.Factor("pairs", pairs.ToString()), EngineMath.Factor("hits", hits.ToString()),
             EngineMath.Factor("gate_pairs", GateCouplingPairs.ToString())]));
        if (couplingFired)
            findings.Add(new PatternFinding(PatternKind.LowActivityAfterPoorSleep, "fired",
                $"{hits} of {pairs} days after a poor night stayed below the personal steps band",
                pairs, pairs, ConfidenceFromRatio(pairs, 20), coveredDates));

        return findings;
    }

    private static BaselineConfidence ConfidenceFromRatio(int earned, int reference) =>
        (earned, reference) switch
        {
            (>= 14, _) => BaselineConfidence.High,
            (>= 7, _) => BaselineConfidence.Medium,
            (>= 3, _) => BaselineConfidence.Low,
            _ => BaselineConfidence.None,
        };
}

/// <summary>Mirrors the client's LIVORA.Domain.Enums.PatternKind vocabulary (append-consistent);
/// <see cref="LowActivityAfterPoorSleep"/> is the server-side wording of the client's
/// FocusAfterPoorSleep pair, because only activity is measured here (see file header).</summary>
public enum PatternKind
{
    LateSleepRecurring = 0,
    WeekdayActivityDip = 1,
    LowActivityAfterPoorSleep = 2,
}

/// <summary>One earned pattern: what it is, how much history earned it, and the dates it covers.
/// Never a diagnosis, never a label about the person — a regularity in their own data.</summary>
public sealed record PatternFinding(
    PatternKind Kind,
    string Verdict,
    string Why,
    int Samples,
    int Observations,
    BaselineConfidence Confidence,
    IReadOnlyList<string> DatesUtc);
