namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 12 — feedback → execution likelihood. The person's own history of accepting, completing,
/// skipping, finding an action too hard, or being offered it at the wrong time, turned into a
/// per-action estimate the prioritiser can weight. This is a REVISED ESTIMATE, never a character
/// trait: the record carries a half-life, decays toward the neutral prior, and can be deleted by
/// the user, after which the estimate behaves as if it had never been computed.
/// <para>
/// Model (deliberately small and inspectable — no hidden weights):
///  - each event contributes weight <see cref="WeightHalfLifeDays"/>^-age_days, so a 3-week-old
///    skip counts half as much as yesterday's (Bayes-weighted mean, not a running average);
///  - a completed action is worth <see cref="WeightCompleted"/> (1.0), an accepted-but-unreported
///    action <see cref="WeightAccepted"/> (0.6), a skipped one 0, "too_hard" and "wrong_time" 0 —
///    the distinction between them matters for the EXPLANATION and for which lever stage 11 pulls,
///    not for the numeric estimate;
///  - the estimate is Laplace-smoothed toward <see cref="PriorLikelihood"/> = 0.5 with
///    <see cref="PriorEquivalentEvents"/> = 2 pseudo-observations, so 1–2 data points nudge the
///    number instead of pinning it to 0 or 1;
///  - below <see cref="MinEventsForNonPrior"/> real events the answer is exactly the prior and
///    <see cref="ExecutionLikelihood.IsPriorOnly"/> is true — the pipeline must then say
///    "no history yet", not imply it knows the person.
/// </para>
/// <para>
/// Anti-labeling law, enforced here and by tests: the model exposes per-ACTION numbers only. There
/// is no API that returns a trait, a diagnosis, a "discipline score", or any aggregate over a
/// person. Every record is stamped with an evidence grade and is delete-able.
/// </para>
/// </summary>
public sealed class ExecutionLikelihoodModel
{
    public const string StageName = "execution_likelihood";

    public const double PriorLikelihood = 0.5;
    public const int PriorEquivalentEvents = 2;
    public const double WeightHalfLifeDays = 21;
    public const double WeightCompleted = 1.0;
    public const double WeightAccepted = 0.6;
    public const int MinEventsForNonPrior = 1;
    /// <summary>Estimates older than this many days are treated as no longer representative even
    /// if rows exist (a life change invalidates the history; the client's own staleness allowance
    /// is 2 days for STATE — RuleEngine.cs:20 — this is the much longer HISTORY bound).</summary>
    public const int MaxRepresentativeAgeDays = 120;

    /// <summary>Keyed by action key (ordinal). Built once per decision from the supplied events.</summary>
    public static ExecutionLikelihoodModel Build(
        IReadOnlyList<ExecutionFeedback> events, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(events);
        var live = events
            .Where(e => e.DeletedAtUtc is null)
            .GroupBy(e => e.ActionKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<ExecutionFeedback>)g.OrderByDescending(x => x.OccurredAtUtc.UtcTicks).ToList(),
                StringComparer.Ordinal);
        var byAction = live.ToDictionary(kv => kv.Key, kv => Estimate(kv.Value, asOfUtc), StringComparer.Ordinal);
        return new ExecutionLikelihoodModel(byAction, asOfUtc) { History = live };
    }

    /// <summary>An empty model: every action reads the prior (the honest "no history yet").</summary>
    public static ExecutionLikelihoodModel Empty(DateTimeOffset asOfUtc) =>
        new(new Dictionary<string, double>(StringComparer.Ordinal), asOfUtc);

    private ExecutionLikelihoodModel(Dictionary<string, double> byAction, DateTimeOffset asOfUtc)
    {
        ByAction = byAction;
        AsOfUtc = asOfUtc;
    }

    public DateTimeOffset AsOfUtc { get; }
    public IReadOnlyDictionary<string, double> ByAction { get; }

    public double LikelihoodFor(string actionKey) =>
        ByAction.TryGetValue(actionKey, out var v) ? v : PriorLikelihood;

    public bool IsPriorOnly(string actionKey) =>
        !ByAction.TryGetValue(actionKey, out var v) || v == PriorLikelihood;

    /// <summary>One trail line per action that has history (ordinal order = deterministic).</summary>
    public IReadOnlyList<TrailEntry> Trail(IReadOnlyCollection<string> actionKeys)
    {
        var trail = new List<TrailEntry>();
        foreach (var key in actionKeys.OrderBy(k => k, StringComparer.Ordinal))
        {
            bool known = ByAction.ContainsKey(key);
            trail.Add(TrailEntry.Of(StageName, "p1e.likelihood." + (known ? "estimated" : "prior_only"),
                known ? "estimated" : "prior_only",
                [EngineMath.Factor("action", key), EngineMath.Factor("likelihood", LikelihoodFor(key))]));
        }
        return trail;
    }

    private static double Estimate(IReadOnlyList<ExecutionFeedback> rows, DateTimeOffset asOfUtc)
    {
        double weightSum = 0, valueSum = 0;
        foreach (var r in rows)
        {
            double ageDays = Math.Max(0, (asOfUtc - r.OccurredAtUtc).TotalDays);
            if (ageDays > MaxRepresentativeAgeDays) continue;      // stale history is not identity
            double w = Math.Pow(0.5, ageDays / WeightHalfLifeDays);
            weightSum += w;
            valueSum += w * OutcomeValue(r.Outcome);
        }
        // Laplace smoothing toward the prior.
        double prior = PriorLikelihood * PriorEquivalentEvents;
        return Math.Clamp((valueSum + prior) / (weightSum + PriorEquivalentEvents), 0, 1);
    }

    /// <summary>Which lever stage 11 should prefer for this action, from the most recent
    /// representative feedback: repeated wrong-time means move, too-hard means shorten, anything
    /// else (including no history) means "use the default ladder". Deterministic: newest record
    /// wins, ties broken by outcome ordinal. This is a scheduling preference derived from
    /// behaviour — it is not a claim about the person.</summary>
    public string LeverFor(string actionKey)
    {
        var rows = History.TryGetValue(actionKey, out var list) ? list : (IReadOnlyList<ExecutionFeedback>)Array.Empty<ExecutionFeedback>();
        var recent = rows.Where(r => (AsOfUtc - r.OccurredAtUtc).TotalDays <= MaxRepresentativeAgeDays)
                         .OrderByDescending(r => r.OccurredAtUtc.UtcTicks)
                         .ThenBy(r => (int)r.Outcome)
                         .FirstOrDefault();
        if (recent is null) return "default";
        return AdaptationLever(recent.Outcome) switch
        {
            "move" => "move",
            "shorten" => "shorten",
            _ => "default",
        };
    }

    /// <summary>Live (non-deleted) rows per action, newest first within an action.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ExecutionFeedback>> History { get; private set; }
        = new Dictionary<string, IReadOnlyList<ExecutionFeedback>>(StringComparer.Ordinal);

    public static double OutcomeValue(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.Completed => WeightCompleted,
        ExecutionOutcome.Accepted => WeightAccepted,
        ExecutionOutcome.Skipped => 0.0,
        ExecutionOutcome.TooHard => 0.0,
        ExecutionOutcome.WrongTime => 0.0,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>Which lever stage 11 should pull for this outcome — the reason code that travels
    /// with the adaptation so "we shortened it because you said it was too hard" is auditable.</summary>
    public static string AdaptationLever(ExecutionOutcome outcome) => outcome switch
    {
        ExecutionOutcome.TooHard => "shorten",
        ExecutionOutcome.WrongTime => "move",
        ExecutionOutcome.Skipped => "move_or_shorten",
        ExecutionOutcome.Accepted => "keep",
        ExecutionOutcome.Completed => "keep",
        _ => "keep",
    };
}

/// <summary>How an offered action ended up. Five values, all user-reportable, none a judgement.</summary>
public enum ExecutionOutcome
{
    Accepted = 0,
    Completed = 1,
    Skipped = 2,
    TooHard = 3,
    WrongTime = 4,
}

/// <summary>
/// One feedback record. <see cref="EvidenceGrade"/> says how we know: a user tap is SelfReported,
/// a watch-detected walk is DeviceDerived, a completed calendar block is ProviderDerived, a
/// server-confirmed sync is SystemVerified. A skipped-action estimate built only on self-reports
/// must never be presented as fact — <see cref="Grade"/> rides along on every read.
/// <see cref="DeletedAtUtc"/> implements the deletion right: a soft-deleted row is excluded from
/// every estimate but stays in the audit store (account-deletion path is the only hard delete).
/// </summary>
public sealed record ExecutionFeedback(
    string Id,
    string ActionKey,
    ExecutionOutcome Outcome,
    DateTimeOffset OccurredAtUtc,
    EvidenceGrade Grade,
    string? SourceLabel = null,
    DateTimeOffset? DeletedAtUtc = null,
    string? Idempotency = null)
{
    public bool IsDeleted => DeletedAtUtc is not null;
}
