namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 9 — recommendations. Turns scored candidates into a BOUNDED set of high-value actions and
/// records an explicit suppression reason for everything dropped. An unbounded list is a design
/// failure (the client learned this: RecommendationService.cs:26 "LIVORA should not overwhelm"),
/// and a silent drop is a trust failure — so both halves ship: at most 3 visible actions, plus a
/// reason line per dropped candidate.
/// <para>
/// PORTED selection rules:
///  - budget: at most 1 High/Critical card + up to 2 others ⇒ 3 total
///    (RecommendationService.cs:35-37 takes 1 high + 2 rest; the ranker's DefaultK=4 is the client
///    surface, the cloud lane is stricter and says so: <see cref="MaxVisibleActions"/> = 3)
///  - dedupe by action keeping the best-priority producer (RecommendationService.cs:29-33)
///  - nothing fired ⇒ keep-routine anchor (RecommendationService.cs:39-55)
///  - plan adjustments ride on the action that earned them, in the client's own grammar
///    (RuleEngine.cs:42 "bedtime:-30min", :56 "exercise:*0.5", :74 "recovery:+15min", :102
///    "winddown:+30min", :120 "walk:+15min") so the scheduler below is a shared implementation,
///    not a re-invention
/// NEW suppression gates (each pinned by a boundary test): demanding-while-recovery-low,
/// unsupported evidence (Unrated), and a signal with no usable baseline (provisional verdicts may
/// inform the plan but may not be sold as actions).
/// </para>
/// </summary>
public static class RecommendationStage
{
    public const string StageName = "recommend";

    /// <summary>HARD cap on emitted recommendations (asks + plan adjustments together).</summary>
    public const int MaxVisibleActions = 4;
    /// <summary>The brief's budget: at most 1-3 high-value ACTIONS. An "action" (an ask) is a
    /// candidate that commits minutes or demands a decision; a zero-minute timing/intensity
    /// adjustment (shift bedtime, halve intensity) reshapes the plan instead of asking the person
    /// to do a new thing — so it does not consume the ask budget, exactly as the client treats it
    /// (RecommendationService caps visible cards at 1+2 while DailyPlanService applies EVERY fired
    /// adjustment: two surfaces, one set of facts). Pinned by the budget tests.</summary>
    public const int MaxVisibleAsks = 3;
    public const int MaxHighPriorityCards = 1;      // RecommendationService.cs:35 / Ranker:48
    public const int MinVisibleActions = 1;         // the anchor keeps "nothing wrong today" honest

    public static RecommendationBundle Select(
        IReadOnlyList<ScoredAction> scored,
        StateSnapshot state,
        ConstraintSet constraints,
        ExecutionLikelihoodModel likelihood)
    {
        ArgumentNullException.ThrowIfNull(scored);
        var chosen = new List<Recommendation>();
        var dropped = new List<SuppressedAction>();
        var trail = new List<TrailEntry>();
        // Directives are plan changes, not asks: a candidate that cleared the EVIDENCE gates keeps
        // authorising its plan adjustments even when the visible-card budget dropped its card
        // (client parity: RecommendationService caps visible recs at 1+2 while DailyPlanService
        // applies adjustments from every fired rule — two surfaces, one set of facts).
        var directiveCarriers = new List<CandidateAction>();

        if (!state.Fired("p1e.state.data_trustworthy"))
        {
            // Insufficient or contradictory data: the honest answer is the empty bundle plus the
            // reason, never a recommendation dressed up as a decision (product law 3).
            trail.Add(TrailEntry.Of(StageName, "p1e.recommend.insufficient_data", "refused",
                [EngineMath.Factor("completeness", state.Completeness)]));
            return new RecommendationBundle(Array.Empty<Recommendation>(),
                [new SuppressedAction("(all)", "p1e.budget.insufficient_data",
                    "data quality below the decision gate: LIVORA states what is missing instead of recommending")],
                DirectiveSet.Empty(), trail);
        }

        // Selection order mirrors the client: RecommendationService.cs:27-33 builds `visible` by
        // dedupe + OrderByDescending(Priority) and ONLY THEN applies the 1-high + 2-rest cap, so
        // a Low-tier candidate is dropped before a Medium one regardless of score. Ranking the
        // budget by score alone let a Low-tier "moderate screens" card evict a Medium-tier plan
        // adjustment it exists to explain.
        foreach (var s in scored
                     .OrderByDescending(x => (int)x.Candidate.BasePriority)
                     .ThenByDescending(x => x.Score)
                     .ThenBy(x => x.ActionKey, StringComparer.Ordinal))
        {
            var c = s.Candidate;
            (string, string)? suppression = Suppress(c, s, state, constraints, chosen);
            if (suppression is ({ } dropKey, { } dropWhy))
            {
                dropped.Add(new SuppressedAction(c.ActionKey, dropKey, dropWhy));
                trail.Add(TrailEntry.Of(StageName, "p1e.budget.drop." + dropKey, "suppressed",
                    [EngineMath.Factor("action", c.ActionKey), EngineMath.Factor("score", s.Score)],
                    c.EvidenceIds));
                continue;
            }
            directiveCarriers.Add(c);   // evidence-cleared: keeps its directives even if dropped
            if ((c.Ask && chosen.Count(x => x.Ask) >= MaxVisibleAsks) || chosen.Count >= MaxVisibleActions)
            {
                dropped.Add(new SuppressedAction(c.ActionKey, "budget",
                    $"the visible budget is full ({chosen.Count(x => x.Ask)} asks / {MaxVisibleAsks}, " +
                    $"{chosen.Count} cards / {MaxVisibleActions}); the {EngineMath.Num(s.Score)} score is below the ones kept"));
                trail.Add(TrailEntry.Of(StageName, "p1e.budget.drop.budget", "suppressed",
                    [EngineMath.Factor("action", c.ActionKey), EngineMath.Factor("score", s.Score)],
                    c.EvidenceIds));
                continue;
            }
            if (chosen.Count(x => (int)x.Priority >= (int)RecommendationPriority.High) >= MaxHighPriorityCards
                && (int)c.BasePriority >= (int)RecommendationPriority.High)
            {
                dropped.Add(new SuppressedAction(c.ActionKey, "one_high_priority_only",
                    "one urgent message per day; the higher-scoring action holds the slot"));
                trail.Add(TrailEntry.Of(StageName, "p1e.budget.drop.one_high_priority_only", "suppressed",
                    [EngineMath.Factor("action", c.ActionKey), EngineMath.Factor("score", s.Score)],
                    c.EvidenceIds));
                continue;
            }

            chosen.Add(new Recommendation(
                c.ActionKey, c.BasePriority, c.DurationMinutes, c.SourceConclusion, c.Why,
                s.Score, s.Components,
                state.GradeFor(c.SourceConclusion), state.BaselineConfidenceFor(c.SourceConclusion),
                likelihood.LikelihoodFor(c.ActionKey),
                DirectivesFor(c), c.EvidenceIds));
            trail.Add(TrailEntry.Of(StageName, "p1e.budget.keep", "kept",
                [EngineMath.Factor("action", c.ActionKey), EngineMath.Factor("score", s.Score),
                 EngineMath.Factor("grade", state.GradeFor(c.SourceConclusion).Token())],
                c.EvidenceIds));
        }

        if (chosen.Count < MinVisibleActions)
        {
            var anchor = new Recommendation(
                ActionCatalog.KeepRoutine, RecommendationPriority.Low, 0, "p1e.state.data_trustworthy",
                "no signal exceeds the personal band", 0,
                new ScoreComponents(0, 0, 0, likelihood.LikelihoodFor(ActionCatalog.KeepRoutine), 1, 0),
                state.EvidenceCeiling, BaselineConfidence.None,
                likelihood.LikelihoodFor(ActionCatalog.KeepRoutine),
                Array.Empty<string>(), Array.Empty<string>());
            chosen.Add(anchor);
            trail.Add(TrailEntry.Of(StageName, "p1e.recommend.anchor", "kept",
                [EngineMath.Factor("action", ActionCatalog.KeepRoutine)]));
        }

        return new RecommendationBundle(chosen, dropped,
            DirectiveSet.From(directiveCarriers, constraints), trail);
    }

    /// <summary>The suppression ladder, in order: the FIRST matching reason is the recorded reason.</summary>
    private static (string, string)? Suppress(CandidateAction c, ScoredAction s, StateSnapshot state,
        ConstraintSet constraints, List<Recommendation> chosen)
    {
        if (chosen.Any(x => string.Equals(x.ActionKey, c.ActionKey, StringComparison.Ordinal)))
            return ("duplicate_action", "the same action is already offered from a stronger conclusion");

        if (c.Demanding && constraints.RecoveryLow)
            return ("recovery_gate",
                "recovery_low fired: nothing demanding is added today (constraint p1e.constraint.max_new_minutes)");

        var grade = state.GradeFor(c.SourceConclusion);
        if (grade == EvidenceGrade.Unrated)
            return ("unsupported_evidence",
                $"no evidence record backs {c.SourceConclusion}: LIVORA will not recommend on nothing");

        if (c.SourceConclusion.StartsWith("p1e.state.", StringComparison.Ordinal)
            && state.BaselineConfidenceFor(c.SourceConclusion) == BaselineConfidence.None
            && !string.Equals(c.ActionKey, ActionCatalog.KeepRoutine, StringComparison.Ordinal))
            return ("provisional_baseline",
                "no trustworthy personal baseline yet: the verdict stays provisional and is not sold as an action");

        return null;
    }

    /// <summary>Plan adjustments an action carries, in the client's adjustment grammar
    /// (RuleEngine.cs PlanAdjustments — ported, not re-invented).</summary>
    public static IReadOnlyList<string> DirectivesFor(CandidateAction c) => c.ActionKey switch
    {
        ActionCatalog.EarlierSleep => ["bedtime:-30min"],                       // RuleEngine.cs:42
        ActionCatalog.ReduceIntensity => ["exercise:*0.5"],                      // RuleEngine.cs:56
        ActionCatalog.ShortWalk => ["walk:+15min"],                              // RuleEngine.cs:120
        ActionCatalog.TakeBreak => ["recovery:+10min"],                          // RuleEngine.cs:91
        ActionCatalog.ModerateScreen => ["winddown:+30min"],                     // RuleEngine.cs:102
        ActionCatalog.ProtectFocusBlock => ["focus:protect:1"],
        _ => Array.Empty<string>(),
    };
}

/// <summary>A visible recommendation: what to do, why, on what evidence, with which score
/// components, and which plan adjustments it authorises.</summary>
public sealed record Recommendation(
    string ActionKey,
    RecommendationPriority Priority,
    int DurationMinutes,
    string SourceConclusion,
    string Why,
    double Score,
    ScoreComponents Components,
    EvidenceGrade Grade,
    BaselineConfidence BaselineConfidence,
    double ExecutionLikelihood,
    IReadOnlyList<string> Directives,
    IReadOnlyList<string> EvidenceIds)
{
    /// <summary>Same rule as CandidateAction.Ask — an ask commits minutes, demands something, or
    /// is the keep-routine anchor; anything else is a plan adjustment carried for the scheduler.</summary>
    public bool Ask => DurationMinutes > 0 || string.Equals(
        ActionKey, ActionCatalog.KeepRoutine, StringComparison.Ordinal);
}

/// <summary>A dropped candidate with its stated reason — part of the output, not a log line.</summary>
public sealed record SuppressedAction(string ActionKey, string ReasonKey, string Reason);

/// <summary>Plan-level directives the day must obey even when no card is shown for them.</summary>
public sealed record DirectiveSet(IReadOnlyList<PlanDirective> Directives, IReadOnlyList<TrailEntry> Trail)
{
    /// <summary>The refused-day bundle carries no directives: absence stated, not invented.</summary>
    public static DirectiveSet Empty() =>
        new(Array.Empty<PlanDirective>(), Array.Empty<TrailEntry>());

    public static DirectiveSet From(IEnumerable<CandidateAction> carriers, ConstraintSet constraints)
    {
        var list = new List<PlanDirective>();
        var trail = new List<TrailEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var c in carriers.OrderBy(x => x.ActionKey, StringComparer.Ordinal))
            foreach (var d in RecommendationStage.DirectivesFor(c))
                if (seen.Add(d))
                {
                    list.Add(new PlanDirective(d, c.ActionKey, $"authorised by {c.SourceConclusion}"));
                    trail.Add(TrailEntry.Of(RecommendationStage.StageName, "p1e.directive." + d, "issued",
                        [EngineMath.Factor("action", c.ActionKey)], c.EvidenceIds));
                }

        if (constraints.RecoveryLow && seen.Add("load:no_new_demanding"))
        {
            list.Add(new PlanDirective("load:no_new_demanding", "",
                "recovery_low: no demanding minutes are added today (constraint, not a suggestion)"));
            trail.Add(TrailEntry.Of(RecommendationStage.StageName, "p1e.directive.load:no_new_demanding",
                "issued", [EngineMath.Factor("source", "p1e.constraint.max_new_minutes")]));
        }
        return new DirectiveSet(list, trail);
    }

    public bool Has(string directive) => Directives.Any(d => string.Equals(d.Grammar, directive, StringComparison.Ordinal));
}

public sealed record PlanDirective(string Grammar, string FromActionKey, string Why)
{
    /// <summary>Multiplier a "exercise:*0.5"-style directive carries (1 when it is not a scaling).</summary>
    public double ScaleFactor =>
        Grammar.StartsWith("exercise:*", StringComparison.Ordinal)
        && double.TryParse(Grammar["exercise:*".Length..], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 1.0;
}

public sealed record RecommendationBundle(
    IReadOnlyList<Recommendation> Chosen,
    IReadOnlyList<SuppressedAction> Dropped,
    DirectiveSet Directives,
    IReadOnlyList<TrailEntry> Trail);
