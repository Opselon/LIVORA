namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// THE PIPELINE — the twelve stages wired end to end as one pure function. The composition itself
/// carries no judgement: every decision lives in its stage, and this class only (a) runs the
/// stages in the documented order, (b) accumulates every stage's trail into one decision trail,
/// (c) refuses loudly when the quality gate says the day cannot support a decision.
///
/// Order (the brief's chain): facts → data quality → (baseline → state) → patterns → goals →
/// constraints → (candidates → priorities → recommendations) → schedule → adaptation, with the
/// feedback-derived execution likelihood (stage 12) feeding the prioritiser as an INPUT.
///
/// Determinism contract, pinned by PipelineRunnerTests:
///  - identical input ⇒ identical output (the full trail is byte-equal when re-run);
///  - no wall clock, no Random, no environment read anywhere on this path — `AsOfUtc` is required;
///  - no LLM call can appear here: this namespace has no HTTP client, no AI abstraction, and its
///    dependency direction (Infrastructure/Engines → nothing) makes that structural, and a test
///    asserts the assembly-reference-free purity of the stage types.
/// </summary>
public static class PipelineRunner
{
    public const string StageName = "pipeline";

    public static PipelineResult Run(PipelineInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var trail = new List<TrailEntry>();
        trail.Add(TrailEntry.Of(StageName, "p1e.pipeline.version", "input",
            [EngineMath.Factor("engine", EngineMath.EngineVersion),
             EngineMath.Factor("as_of", input.AsOfUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))]));

        // ---- 1) facts ---------------------------------------------------------------------------
        var facts = input.Facts;
        trail.Add(TrailEntry.Of(StageName, "p1e.pipeline.facts", "loaded",
            [EngineMath.Factor("rows", facts.Raw.Count), EngineMath.Factor("keys", facts.Keys.Count)]));

        // ---- 2) data quality --------------------------------------------------------------------
        var expected = DataQualityStage.ExpectedKeysFor(input.Profile);
        var quality = DataQualityStage.Assess(facts, expected);
        trail.AddRange(quality.Trail);

        if (!quality.CanDecide)
        {
            // Safe degradation, stated: the honest output of a data-poor day is a data-poor answer.
            trail.Add(TrailEntry.Of(StageName, "p1e.pipeline.refused", "insufficient_data",
                [EngineMath.Factor("completeness", quality.Completeness),
                 EngineMath.Factor("gate", DataQualityStage.MinCompletenessToDecide)]));
            return new PipelineResult(
                input.AsOfUtc, quality, null, Array.Empty<PatternFinding>(), null, null, null,
                new RecommendationBundle(Array.Empty<Recommendation>(),
                    [new SuppressedAction("(all)", "p1e.budget.insufficient_data",
                        "data quality below the decision gate: LIVORA states what is missing instead of recommending")],
                    DirectiveSet.Empty(), Array.Empty<TrailEntry>()),
                null, Array.Empty<PlanChange>(), trail, Refused: true);
        }

        // ---- 3) baseline (from history) ---------------------------------------------------------
        var baselines = BaselineStage.Compute(input.History, input.AsOfUtc.UtcDateTime);
        trail.AddRange(baselines.Trail);

        // ---- 4) state ---------------------------------------------------------------------------
        var state = StateStage.Derive(facts, quality, baselines);
        trail.AddRange(state.Trail);

        // ---- 5) patterns ------------------------------------------------------------------------
        var patterns = PatternStage.Assess(input.History, baselines, input.AsOfUtc.UtcDateTime);

        // ---- 6) goals ---------------------------------------------------------------------------
        var goals = GoalStage.Assess(input.Goals, input.AsOfUtc.UtcDateTime);
        trail.AddRange(goals.Trail);

        // ---- 7) constraints -----------------------------------------------------------------------
        var constraints = ConstraintStage.Build(state, input.FixedBlocks, input.Availability,
            input.BasePlan.Sum(p => p.PlannedMinutes));
        trail.AddRange(constraints.Trail);

        // ---- 12) execution likelihood (FEEDBACK path; consumed by 8, so computed first) ---------
        var likelihood = ExecutionLikelihoodModel.Build(input.Feedback, input.AsOfUtc);
        var candidates = ActionCatalog.Generate(state, goals);
        trail.AddRange(likelihood.Trail(candidates.Select(c => c.ActionKey).ToList()));

        // ---- 8) priorities ------------------------------------------------------------------------
        var scored = Prioritiser.Rank(candidates, state, likelihood, input.FocusTokens, goals, input.AsOfUtc);
        foreach (var s in scored)
            trail.Add(TrailEntry.Of(Prioritiser.StageName, "p1e.rank." + s.ActionKey, "scored",
                [EngineMath.Factor("score", s.Score, "0.######"),
                 EngineMath.Factor("goal_align", s.Components.GoalAlignment),
                 EngineMath.Factor("urgency", s.Components.Urgency),
                 EngineMath.Factor("confidence", s.Components.Confidence),
                 EngineMath.Factor("exec", s.Components.Execution),
                 EngineMath.Factor("fit", s.Components.ScheduleFit),
                 EngineMath.Factor("effort", s.Components.Effort)],
                s.Candidate.EvidenceIds));

        // ---- 9) recommendations (budget + suppression) -------------------------------------------
        var bundle = RecommendationStage.Select(scored, state, constraints, likelihood);
        trail.AddRange(bundle.Trail);

        // ---- 10) schedule ---------------------------------------------------------------------------
        var schedule = ScheduleStage.Build(bundle, constraints, input.BasePlan,
            input.DayStartUtc, input.UtcOffsetMinutes);
        trail.AddRange(schedule.Trail);

        // ---- 11) adaptation (conflict resolution) ---------------------------------------------------
        AdaptationResult? adaptation = null;
        if (input.ConflictingItemIds.Count > 0)
        {
            adaptation = AdaptationStage.Resolve(schedule, state, constraints,
                input.ConflictingItemIds, likelihood, input.ProtectedItemIds, input.AlreadyAppliedRuleKeys,
                // the decision moment in local minutes-of-day — a moved block must land in the future
                Math.Clamp((int)(input.AsOfUtc - input.DayStartUtc).TotalMinutes, 0, 1440));
            trail.AddRange(adaptation.Trail);
        }

        trail.Add(TrailEntry.Of(StageName, "p1e.pipeline.done", "decided",
            [EngineMath.Factor("visible_actions", bundle.Chosen.Count),
             EngineMath.Factor("suppressed", bundle.Dropped.Count),
             EngineMath.Factor("placed", (schedule.Items.Count - input.BasePlan.Count).ToString()),
             EngineMath.Factor("adaptations", (adaptation?.Changes.Count ?? 0)),
             EngineMath.Factor("evidence_ceiling", state.EvidenceCeiling.Token())]));

        // The schedule the caller sees is the ADAPTED one: reporting the pre-adaptation grid
        // alongside "this block moved/shrank/was skipped" changes would describe a day the
        // engine did not actually produce.
        var finalSchedule = adaptation?.Plan ?? schedule;

        return new PipelineResult(input.AsOfUtc, quality, baselines, patterns, state, goals,
            constraints, bundle, finalSchedule, adaptation?.Changes ?? Array.Empty<PlanChange>(), trail,
            Refused: false);
    }
}

/// <summary>Everything the pipeline reads, in one typed value. The caller owns the clock and the
/// data; the engine owns no inputs of its own — which is exactly what makes a test able to pin an
/// entire day in a fixture with no hidden state.</summary>
public sealed record PipelineInput(
    DateTimeOffset AsOfUtc,
    FactSet Facts,
    IReadOnlyList<HistoryDay> History,
    DecisionProfile Profile,
    IReadOnlyList<GoalInput> Goals,
    IReadOnlyList<CalendarBlock> FixedBlocks,
    TimeWindowUser Availability,
    IReadOnlyList<PlannedItem> BasePlan,
    IReadOnlyList<ExecutionFeedback> Feedback,
    IReadOnlyCollection<string> FocusTokens,
    DateTimeOffset DayStartUtc,
    int UtcOffsetMinutes,
    IReadOnlyList<string> ConflictingItemIds,
    IReadOnlySet<string> ProtectedItemIds,
    IReadOnlyList<string> AlreadyAppliedRuleKeys)
{
    /// <summary>A same-day default: conflict-free, protection-free, nothing applied yet.</summary>
    public static PipelineInput Simple(
        DateTimeOffset asOfUtc, FactSet facts, IReadOnlyList<HistoryDay> history,
        IReadOnlyList<GoalInput>? goals = null, IReadOnlyList<CalendarBlock>? fixedBlocks = null,
        TimeWindowUser? availability = null, IReadOnlyList<PlannedItem>? basePlan = null,
        IReadOnlyList<ExecutionFeedback>? feedback = null, IReadOnlyCollection<string>? focusTokens = null,
        DecisionProfile profile = DecisionProfile.DailyPlan,
        IReadOnlyList<string>? conflictingItemIds = null, IReadOnlySet<string>? protectedItemIds = null,
        IReadOnlyList<string>? alreadyAppliedRuleKeys = null)
        => new(asOfUtc, facts, history, profile,
            goals ?? Array.Empty<GoalInput>(),
            fixedBlocks ?? Array.Empty<CalendarBlock>(),
            availability ?? TimeWindowUser.WholeDay,
            basePlan ?? Array.Empty<PlannedItem>(),
            feedback ?? Array.Empty<ExecutionFeedback>(),
            focusTokens ?? Array.Empty<string>(),
            asOfUtc.Date, 0,
            conflictingItemIds ?? Array.Empty<string>(),
            protectedItemIds ?? new HashSet<string>(StringComparer.Ordinal),
            alreadyAppliedRuleKeys ?? Array.Empty<string>());
}

/// <summary>The whole decision for one day: each stage's typed output plus the single accumulated
/// decision trail every conclusion can be traced through. <see cref="Refused"/> true means the
/// quality gate stopped the run — and the trail says exactly where and why.</summary>
public sealed record PipelineResult(
    DateTimeOffset AsOfUtc,
    QualityReport Quality,
    BaselineResult? Baselines,
    IReadOnlyList<PatternFinding> Patterns,
    StateSnapshot? State,
    GoalHealthResult? Goals,
    ConstraintSet? Constraints,
    RecommendationBundle Recommendations,
    ScheduledPlan? Schedule,
    IReadOnlyList<PlanChange> Adaptations,
    IReadOnlyList<TrailEntry> Trail,
    bool Refused)
{
    /// <summary>The trail rendered for logs/diagnostics: one deterministic line per decision step,
    /// in execution order. This is the "rule/decision trail" every output must carry.</summary>
    public string TrailText() => string.Join('\n', Trail.Select(t => t.Render()));

    /// <summary>Visible action keys in offered order (a test reads the plan through this).</summary>
    public IReadOnlyList<string> Actions => Recommendations.Chosen.Select(c => c.ActionKey).ToList();

    /// <summary>The evidence grade the whole day's advice is built on — never a boolean.</summary>
    public EvidenceGrade EvidenceCeiling => State?.EvidenceCeiling ?? EvidenceGrade.Unrated;
}
