namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 8 — action catalog + priorities. Candidates are generated from named state conclusions
/// (never from raw numbers twice-banded elsewhere), then scored with the client's ranking algebra
/// extended by the execution-likelihood term this lane adds.
/// <para>
/// PORTED action semantics (Application/Rules/RuleEngine.cs — same conclusion ⇒ same action,
/// same priority tier):
///  - sleep deficit ≥ 1.5 h → earlier bedtime; ≥ 2.5 h makes it High (RuleEngine.cs:36-47)
///  - sleep deficit also reduces training intensity (RuleEngine.cs:50-58, Medium)
///  - recovery low → short walk, Medium (RuleEngine.cs:66-77)
///  - stress &gt; 0.65 → take a break (High &gt; 0.80, Medium above 0.65) (RuleEngine.cs:80-93)
///  - screen high → moderate screens (Low) — the client only fires this under stress
///    (RuleEngine.cs:96-104); the server fires on the screen signal itself because the cloud lane
///    has a real screen feed; the priority tier is kept identical (documented deviation)
///  - activity deficit → short walk, but ONLY when recovery is not also low
///    (RuleEngine.cs:110: `rec?.Value >= RecoveryBelow`); recovery-low already scheduled the walk
///  - nothing fired → keep-routine anchor (RecommendationService.cs:39-55)
///  - NEW server rule (no client equivalent; the client's focus scaling lives in
///    RecommendationService.cs:154-156): high meeting load protects ONE focus block.
/// Scoring: the wave-3b ranker's algebra, weights extended for the likelihood term.
/// PORTED FROM Application/Planning/Wave3b/RecommendationRanker.cs:41-45 (W1..W5) — the client
/// weights are multiplied by 0.8 and a W6=0.20 execution-likelihood term takes the remainder, so
/// the relative ordering of the ORIGINAL five terms is preserved exactly while the new signal gets
/// a bounded, stated share. Re-normalisation is deliberate and pinned by PrioritiserTests.
/// </para>
/// </summary>
public static class ActionCatalog
{
    // Action keys are machine tokens (client parity: RecommendationActionKind names, dotted lower).
    public const string ReduceIntensity = "action.reduce_intensity";
    public const string ShortWalk = "action.short_walk";
    public const string ProtectFocusBlock = "action.protect_focus_block";
    public const string EarlierSleep = "action.earlier_sleep";
    public const string ModerateScreen = "action.moderate_screen";
    public const string TakeBreak = "action.take_break";
    public const string DoProgramDay = "action.do_program_day";
    public const string KeepRoutine = "action.keep_routine";

    /// <summary>Minutes an action commits when scheduled (client parity:
    /// RecommendationService.DurationFor:86-93 — walk 15, break 10, focus block 50, bedtime 0).</summary>
    public const int WalkMinutes = 15;
    public const int BreakMinutes = 10;
    public const int FocusBlockMinutes = 50;
    public const int BedtimeShiftMinutes = 30;       // RuleEngine.cs:42 "bedtime:-30min"

    /// <summary>Demanding = adds load the body must pay for. The recovery/low-reserve gate only
    /// blocks demanding additions; restorative ones (a walk on a low-recovery day) stay allowed —
    /// that is exactly what the client's R2 rule does (RuleEngine.cs:66-77).</summary>
    public static bool IsDemanding(string actionKey) => actionKey == DoProgramDay;

    public static int DurationMinutes(string actionKey) => actionKey switch
    {
        ShortWalk => WalkMinutes,
        TakeBreak => BreakMinutes,
        ProtectFocusBlock => FocusBlockMinutes,
        _ => 0,
    };

    /// <summary>Generate candidates from fired conclusions. Deterministic order: conclusion key.</summary>
    public static IReadOnlyList<CandidateAction> Generate(StateSnapshot state, GoalHealthResult goals)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(goals);
        var list = new List<CandidateAction>();

        var sleepPoor = state.Find("p1e.state.sleep_poor");
        if (sleepPoor?.Verdict == "fired")
        {
            double deficitH = FactorValue(sleepPoor, "deficit_h");
            list.Add(new CandidateAction(EarlierSleep, "p1e.state.sleep_poor",
                deficitH >= StateStage.SleepDeficitHighHours
                    ? RecommendationPriority.High : RecommendationPriority.Medium,
                0,                      // timing change, not a block (DurationFor parity)
                Demanding: false,
                sleepPoor.EvidenceIds,
                $"sleep {EngineMath.Num(deficitH, "0.#")} h below personal baseline"));

            list.Add(new CandidateAction(ReduceIntensity, "p1e.state.sleep_poor",
                RecommendationPriority.Medium, 0, Demanding: false, sleepPoor.EvidenceIds,
                "deficit day: halve planned intensity (ported Rule.SleepDebtReduceIntensity)"));
        }

        var recoveryLow = state.Find("p1e.state.recovery_low");
        if (recoveryLow?.Verdict == "fired")
        {
            list.Add(new CandidateAction(ShortWalk, "p1e.state.recovery_low",
                RecommendationPriority.Medium, WalkMinutes, Demanding: false, recoveryLow.EvidenceIds,
                "gentle movement aids recovery without cost (ported Rule.LowRecoveryReduce)"));
        }

        var activityLow = state.Find("p1e.state.activity_low");
        if (activityLow?.Verdict == "fired" && recoveryLow?.Verdict != "fired")
        {
            // Client gate RuleEngine.cs:110 — when recovery is low, the walk is already placed
            // above with the restorative priority; a deficit-only day gets the gentle nudge.
            list.Add(new CandidateAction(ShortWalk, "p1e.state.activity_low",
                RecommendationPriority.Low, WalkMinutes, Demanding: false, activityLow.EvidenceIds,
                "steps below personal band (ported Rule.ActivityDeficit)"));
        }

        var meetingsHigh = state.Find("p1e.state.meeting_load_high");
        if (meetingsHigh?.Verdict == "fired")
        {
            list.Add(new CandidateAction(ProtectFocusBlock, "p1e.state.meeting_load_high",
                RecommendationPriority.Medium, FocusBlockMinutes, Demanding: false,
                meetingsHigh.EvidenceIds,
                "meeting-heavy day: protect one uninterrupted block (server rule; focus-scaling parity with RecommendationService.cs:154-165)"));
        }

        var screenHigh = state.Find("p1e.state.screen_load_high");
        if (screenHigh?.Verdict == "fired")
        {
            list.Add(new CandidateAction(ModerateScreen, "p1e.state.screen_load_high",
                RecommendationPriority.Low, 0, Demanding: false, screenHigh.EvidenceIds,
                "screen time above personal band (priority tier ported from Rule.HighStressScreens)"));
        }

        var stressHigh = state.Find("p1e.state.stress_high");
        if (stressHigh?.Verdict == "fired")
        {
            double stress = FactorValue(stressHigh, "stress");
            list.Add(new CandidateAction(TakeBreak, "p1e.state.stress_high",
                stress > StateStage.StressHigh ? RecommendationPriority.High : RecommendationPriority.Medium,
                BreakMinutes, Demanding: false, stressHigh.EvidenceIds,
                "stress above personal ceiling (ported Rule.HighStress)"));
        }

        if (goals.AtRisk.Count > 0)
        {
            var g = goals.AtRisk[0];
            list.Add(new CandidateAction(DoProgramDay, "p1e.goal." + g.Verdict,
                RecommendationPriority.Medium, 0, Demanding: true, Array.Empty<string>(),
                $"goal {g.Id} {g.Verdict} (deadline {g.DaysLeft?.ToString() ?? "none"} days away)"));
        }

        if (list.Count == 0)
        {
            list.Add(new CandidateAction(KeepRoutine, "p1e.state.data_trustworthy",
                RecommendationPriority.Low, 0, Demanding: false, Array.Empty<string>(),
                "nothing exceeds the personal band — protect what is working (client parity: RecommendationService.cs:39-55)"));
        }

        return list
            .GroupBy(c => c.ActionKey, StringComparer.Ordinal)   // client parity: RecommendationService.cs:29-33
            .Select(g => g.OrderByDescending(c => (int)c.BasePriority)
                          .ThenBy(c => c.SourceConclusion, StringComparer.Ordinal)
                          .First())
            .OrderBy(c => c.ActionKey, StringComparer.Ordinal)
            .ToList();
    }

    internal static double FactorValue(StateConclusion c, string key)
    {
        var prefix = key + "=";
        var raw = c.Factors.FirstOrDefault(f => f.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
    }
}

/// <summary>Priority ladder mirrored from LIVORA.Domain.Enums.RecommendationPriority (values
/// identical so a synced payload reads the same on both sides).</summary>
public enum RecommendationPriority { Optional = 0, Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>A candidate before scoring: which conclusion produced it, what it commits.

public sealed record CandidateAction(
    string ActionKey,
    string SourceConclusion,
    RecommendationPriority BasePriority,
    int DurationMinutes,
    bool Demanding,
    IReadOnlyList<string> EvidenceIds,
    string Why)
{
    /// <summary>An ASK commits minutes or demands a decision (or is the keep-routine anchor); a
    /// zero-minute, non-demanding candidate is a PLAN ADJUSTMENT (bedtime shift, intensity cap)
    /// and does not consume the visible-action budget. Pinned by CrossDomainPipelineTests.</summary>
    public bool Ask => DurationMinutes > 0 || Demanding
        || string.Equals(ActionKey, ActionCatalog.KeepRoutine, StringComparison.Ordinal);
}

/// <summary>
/// The prioritiser: rank candidates with the documented weight table. Execution likelihood comes
/// from the feedback lane's own model (stage 12); with no history it is the neutral prior 0.5 —
/// never a guess about the person.
/// </summary>
public static class Prioritiser
{
    public const string StageName = "priorities";

    // ---- weight table (see file header: client weights × 0.8 + W6 = 0.20) ----------------------
    public const double WeightScale = 0.8;
    public const double W1GoalAlignment = 0.35 * WeightScale;   // 0.280
    public const double W2Urgency = 0.30 * WeightScale;         // 0.240
    public const double W3Confidence = 0.25 * WeightScale;      // 0.200
    public const double W4ScheduleFit = 0.20 * WeightScale;     // 0.160
    public const double W5Effort = 0.25 * WeightScale;          // 0.200 (penalty)
    public const double W6Execution = 0.20;
    public const double MaxHighPriorityCards = 1;               // RecommendationRanker.cs:48
    public const int EffortDivisorMinutes = 120;                // RecommendationRanker.cs:32

    /// <summary>Client parity: RecommendationRanker.EveningCutoff (line 54).</summary>
    public static readonly TimeSpan EveningCutoff = new(22, 0, 0);

    /// <summary>Evidence-weight for scoring only (monotone on the grade ladder, stated, pinned).
    /// The OUTPUT still carries the real grade — nothing collapses the ladder; a SelfReported
    /// signal simply scores lower than a SystemVerified one, it never reads "verified".</summary>
    public static double EvidenceWeight(EvidenceGrade grade) => grade switch
    {
        EvidenceGrade.Unrated => 0.0,
        EvidenceGrade.SelfReported => 0.60,
        EvidenceGrade.DeviceDerived => 0.80,
        EvidenceGrade.ProviderDerived => 0.90,
        EvidenceGrade.SystemVerified => 1.0,
        EvidenceGrade.HumanReviewed => 1.0,
        _ => 0.0,
    };

    /// <summary>Baseline-confidence factor, PORTED from
    /// Application/State/Wave3b/StateConfidenceCalculator.cs:61-62 (None 0 / Low .45 / Medium .75 / High 1).</summary>
    public static double BaselineFactor(BaselineConfidence c) => c switch
    {
        BaselineConfidence.None => 0.0,
        BaselineConfidence.Low => 0.45,
        BaselineConfidence.Medium => 0.75,
        BaselineConfidence.High => 1.0,
        _ => 0.0,
    };

    public static IReadOnlyList<ScoredAction> Rank(
        IReadOnlyList<CandidateAction> candidates,
        StateSnapshot state,
        ExecutionLikelihoodModel likelihood,
        IReadOnlyCollection<string> userFocusTokens,
        GoalHealthResult? goals,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        double deadlineProximity = DeadlineProximity(goals);
        var scored = new List<ScoredAction>();

        foreach (var c in candidates.OrderBy(x => x.ActionKey, StringComparer.Ordinal))
        {
            double goalAlignment = userFocusTokens.Contains(ActionToken(c.ActionKey), StringComparer.Ordinal) ? 1.0 : 0.0;
            double priorityNorm = Math.Clamp((int)c.BasePriority, 0, 4) / 4.0;
            // Client parity: urgency = 0.5*priority + 0.5*deadlineProximity (RecommendationRanker.cs:121).
            double urgency = 0.5 * priorityNorm + 0.5 * deadlineProximity;
            double confidence = EvidenceWeight(state.GradeFor(c.SourceConclusion))
                              * BaselineFactor(state.BaselineConfidenceFor(c.SourceConclusion));
            double exec = likelihood.LikelihoodFor(c.ActionKey);
            double effort = Math.Max(0, c.DurationMinutes) / (double)EffortDivisorMinutes;

            // schedule fit: an action with minutes must still end before the evening cutoff
            double scheduleFit = c.DurationMinutes == 0
                ? 1.0
                : (nowUtc.TimeOfDay + TimeSpan.FromMinutes(c.DurationMinutes) < EveningCutoff ? 1.0 : 0.0);

            double total = W1GoalAlignment * goalAlignment
                         + W2Urgency * urgency
                         + W3Confidence * confidence
                         + W4ScheduleFit * scheduleFit
                         + W6Execution * exec
                         - W5Effort * effort;

            scored.Add(new ScoredAction(c, Math.Round(total, 6),
                new ScoreComponents(goalAlignment, urgency, confidence, exec, scheduleFit, effort)));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.ActionKey, StringComparer.Ordinal)   // stable tie-break
            .ToList();
    }

    /// <summary>Action key → the lowercase focus-area token vocabulary (client parity:
    /// RecommendationRanker.CategoryToken:186-197).</summary>
    public static string ActionToken(string actionKey) => actionKey switch
    {
        ActionCatalog.EarlierSleep or ActionCatalog.ModerateScreen => "sleep",
        ActionCatalog.ShortWalk => "activity",
        ActionCatalog.ReduceIntensity or ActionCatalog.DoProgramDay => "program",
        ActionCatalog.ProtectFocusBlock => "focus",
        ActionCatalog.TakeBreak => "stress",
        _ => "general",
    };

    /// <summary>Nearest at-risk goal deadline over a 30-day horizon: due today → 1, in 30 days → 0.
    /// PORTED from RecommendationRanker.cs:199-212 (DeadlineProximity) with the same horizon const;
    /// here it reads the goal-health verdicts (stage 6) rather than raw goals.</summary>
    public static double DeadlineProximity(GoalHealthResult? goals)
    {
        if (goals is null || goals.ById.Count == 0) return 0;
        double best = 0;
        foreach (var g in goals.ById.Values)
        {
            if (g.DaysLeft is not { } days) continue;
            if (days < 0 || days > GoalStage.DeadlineRiskDays) continue;
            if (g.Fraction >= GoalStage.DeadlineRiskFractionCeiling) continue;
            double prox = 1 - days / (double)GoalStage.DeadlineRiskDays;
            if (prox > best) best = prox;
        }
        return best;
    }
}

/// <summary>Component breakdown exposed so each weight has an isolatable test
/// (client parity: ScoreBreakdown, RecommendationRanker.cs:17-19).</summary>
public sealed record ScoreComponents(
    double GoalAlignment, double Urgency, double Confidence, double Execution,
    double ScheduleFit, double Effort);

public sealed record ScoredAction(CandidateAction Candidate, double Score, ScoreComponents Components)
{
    public string ActionKey => Candidate.ActionKey;
}
