namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 6 — goals. Turns the caller's goal rows into goal *health* relative to the user's own
/// pace, so recommendations can respect what the person said they care about without ever
/// inventing progress. Only three verdicts exist on purpose: on-track, at-risk, stalled — more
/// nuance than that cannot be earned from a fraction + deadline and would be fake precision
/// (client parity: the client's goal stagnation gate carries a 10-day flat window + 30-day
/// deadline window: Application/Patterns/PatternEngine.cs:95-96, and
/// Application/Planning/Adaptive/PlanAdaptationEngine.cs:100-101 DeadlineRiskDays=30 /
/// DeadlineRiskFractionCeiling=0.5).
/// </summary>
public static class GoalStage
{
    public const string StageName = "goals";

    /// <summary>PORTED: PlanAdaptationEngine.cs:100 — inside this many days, an incomplete goal is at risk.</summary>
    public const int DeadlineRiskDays = 30;
    /// <summary>PORTED: PlanAdaptationEngine.cs:101 — below this fraction, deadline risk counts.</summary>
    public const double DeadlineRiskFractionCeiling = 0.5;
    /// <summary>PORTED: PatternEngine.cs:95 — progress flat for this many days reads as stalled.</summary>
    public const int StagnationDays = 10;

    public static GoalHealthResult Assess(IReadOnlyList<GoalInput> goals, DateTime asOfUtcDate)
    {
        ArgumentNullException.ThrowIfNull(goals);
        var rows = new Dictionary<string, GoalVerdict>(StringComparer.Ordinal);
        var trail = new List<TrailEntry>();

        foreach (var g in goals.OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            string verdict;
            var factors = new List<string>
            {
                EngineMath.Factor("fraction", g.Fraction),
                EngineMath.Factor("days_since_progress", g.DaysSinceProgressAtUtc is null ? "never" : "-1"),
            };

            int daysLeft = g.DeadlineUtc is { } d
                ? (int)Math.Round((d.UtcDateTime.Date - asOfUtcDate.Date).TotalDays)
                : int.MaxValue;
            factors.Add(EngineMath.Factor("days_left", daysLeft == int.MaxValue ? "none" : daysLeft.ToString()));

            bool deadlineRisk = daysLeft < DeadlineRiskDays && g.Fraction < DeadlineRiskFractionCeiling;
            bool stalled = g.DaysSinceProgressAtUtc >= StagnationDays && g.Fraction < 0.999;

            if (stalled) verdict = "stalled";
            else if (deadlineRisk) verdict = "at_risk";
            else verdict = "on_track";

            factors.Add(EngineMath.Factor("deadline_risk", deadlineRisk.ToString()));
            factors.Add(EngineMath.Factor("stagnant", stalled.ToString()));
            // Unknown input cannot be silently trusted: a NaN fraction refuses to a verdict of
            // "unknown" instead of coercing to 0 (which would fake an unstarted goal) or to
            // on_track (which would hide it).
            if (double.IsNaN(g.Fraction)) verdict = "unknown";

            rows[g.Id] = new GoalVerdict(g.Id, verdict, daysLeft == int.MaxValue ? null : daysLeft, g.Fraction);
            trail.Add(TrailEntry.Of(StageName, "p1e.goal." + verdict, verdict, factors,
                g.EvidenceId is null ? Array.Empty<string>() : [g.EvidenceId]));
        }
        return new GoalHealthResult(rows, trail);
    }
}

/// <summary>A goal as far as the intelligence lane sees it: identity, progress fraction, deadline,
/// and how long since it last moved. All of it supplied by the caller — the engine stores nothing
/// and fabricates nothing.</summary>
public sealed record GoalInput(
    string Id,
    string Kind,                  // machine token: "goal" | "habit" | "program"
    double Fraction,              // 0..1, NaN = unknown
    DateTimeOffset? DeadlineUtc,
    int? DaysSinceProgressAtUtc,
    string? EvidenceId = null);

public sealed record GoalVerdict(string Id, string Verdict, int? DaysLeft, double Fraction)
{
    public bool AtRisk => Verdict is "at_risk" or "stalled";
}

public sealed record GoalHealthResult(
    IReadOnlyDictionary<string, GoalVerdict> ById,
    IReadOnlyList<TrailEntry> Trail)
{
    public IReadOnlyList<GoalVerdict> AtRisk =>
        ById.Values.Where(v => v.AtRisk).OrderBy(v => v.Id, StringComparer.Ordinal).ToList();
}
