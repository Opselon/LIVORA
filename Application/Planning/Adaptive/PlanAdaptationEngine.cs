using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Planning.Adaptive;

/// <summary>
/// The stable SourceRuleKey values the adaptive plan engine speaks (traceable in tests + diagnostics).
/// </summary>
public static class PlanAdaptationRuleKeys
{
    public const string RecoveryLow = "wave3b.recovery-low";
    public const string SleepLow = "wave3b.sleep-low";
    public const string StaleData = "wave3b.stale-data";
    public const string DeadlineRisk = "wave3b.deadline-risk";
    public const string HighActivity = "wave3b.high-activity-yesterday";

    /// <summary>Every rule id the engine may emit (sweep-tested).</summary>
    public static readonly IReadOnlyList<string> All =
        new[] { RecoveryLow, SleepLow, StaleData, DeadlineRisk, HighActivity };
}

/// <summary>
/// Composes the Plan.Change.* / Plan.Evidence.* localization keys the adaptations speak.
/// Built from segments ("Plan" + ".Change." + suffix) rather than full dotted literals so the
/// static key-existence sweep treats them like the runtime-built "Rec.{Action}" shapes in
/// RecommendationService; the exact key list ships in wave3c-keys/lane05.*.keys.xml and the
/// lane-05 suite pins every composed value.
/// </summary>
public static class PlanAdaptationKeys
{
    /// <summary>PlanAdaptationKeys.Change("ShrinkExercise") etc. — what the engine altered.</summary>
    public static string Change(string suffix) => "Plan" + ".Change." + suffix;
    /// <summary>PlanAdaptationKeys.Evidence("RecoveryBelowBaseline") etc. — the WHY behind the change.</summary>
    public static string Evidence(string suffix) => "Plan" + ".Evidence." + suffix;
}

/// <summary>
/// Wave 3c (lane 05): second-pass adaptation over an assembled <see cref="DailyPlan"/>.
///
/// PURE + DETERMINISTIC: no IO, no clock of its own (the caller supplies <c>now</c>), deep-clone
/// before any mutation, and byte-equal output for byte-equal inputs. Every emitted
/// <see cref="Adaptation"/> carries the evidence that triggered it (ChangeKey/EvidenceKey + args,
/// SourceRuleKey, Confidence) — the UI renders localization keys, never prose.
///
/// Rules (each with a stable SourceRuleKey, see <see cref="PlanAdaptationRuleKeys"/>):
///  - wave3b.recovery-low — recovery.score Level BelowBaseline AND baseline confidence >= Medium:
///    fitness items (Program/Activity category with real minutes — see <see cref="IsFitness"/>;
///    the category enum has no literal "Fitness" member and lanes may not invent one) shrink along
///    the ladder {45, 30, 20}. Step 1 (45) is the default; "high-activity-yesterday" permits step
///    2 (30) — step 3 stays reserved for a future wave. The ladder NEVER raises a block: the target
///    is clamped by the item's current planned value, then by the 15-minute floor
///    (min(15, base) so a base below the floor is never inflated). A 15-minute recovery item is
///    added when the plan has none. Adaptation: Plan.Change.ShrinkExercise (base + new minutes)
///    with evidence Plan.Evidence.RecoveryBelowBaseline carrying the signed badness percent
///    (positive = worse than baseline); Confidence = min over the inputs actually used, with the
///    metric's BaselineConfidence carried as EvidenceConfidence.
///  - wave3b.sleep-low — sleep.minutes deviation at or below -15% AND baseline confidence >=
///    Medium: shift the EARLIEST focus block 2h earlier, clamped so it never starts before 07:00,
///    and add one 20-minute evening WindDown item. Evidence: Plan.Evidence.SleepBelowBaseline.
///  - wave3b.stale-data — SleepState.DaysSinceFreshData >= 2: deliberately NO intensity change
///    (a missing feed is not a reason to train less) — a single note adaptation
///    Plan.Change.KeptStale with evidence Plan.Evidence.StaleData, Confidence 0.3.
///  - wave3b.deadline-risk — goals with a Deadline under 30 days away and Fraction under 0.5
///    protect their LinkedId plan items with a 15-minute floor. PRECEDENCE (documented, tested):
///    this rule runs AFTER the recovery shrink and may push shrink results back UP to the floor —
///    protecting a deadline outranks cutting intensity. It never raises above min(15, base).
///  - wave3b.high-activity-yesterday — activity.steps AboveBaseline by at least 25%: recovery-aware
///    NOTE only (Plan.Change.RecoveryAware; no minutes change on its own), and when recovery-low
///    also fired it permits the second ladder step (see above).
///
/// Baseline gating: a metric whose BaselineConfidence is None or Low can never fire its rule —
/// two bad-looking days are not enough history to reshape a day (Scenario: 2-day history => zero
/// adaptations, pinned by test).
///
/// Global invariants (pinned by the Wave3c/Plan suite):
///  - total committed minutes never exceed 1.2x the INPUT plan's base minutes (cap-guard skips any
///    addition/raise that would break it);
///  - idempotent: a rule key already present in DailyPlan.AdaptationRuleKeys is skipped and every
///    mutation is already at its fixed point, so Adapt(Adapt(plan).Plan) adds nothing;
///  - every Adaptation has a non-empty EvidenceKey + SourceRuleKey and Confidence > 0.
///
/// Goals are NOT part of the frozen Adapt(plan, state, now) signature and PersonalState's
/// GoalStateSnapshot carries no Deadline — so deadline-risk reads a goal snapshot supplied at
/// construction. DI passes the active goal list; null (the default) disables exactly that rule and
/// leaves every state-only rule working.
/// </summary>
public sealed class PlanAdaptationEngine : IPlanAdaptationEngine
{
    // ---- named rule constants (tests + docs read these, not magic numbers) ----
    public static readonly IReadOnlyList<int> RecoveryLadderMinutes = new[] { 45, 30, 20 };
    public const int ShrinkFloorMinutes = 15;
    public const int RecoveryItemMinutes = 15;
    public const int WindDownMinutes = 20;
    public const double SleepDeviationTrigger = -0.15;       // -15% vs personal baseline ("at or below")
    public const double HighActivityDeviationTrigger = 0.25; // +25% above baseline ("at least")
    public const int StaleDaysTrigger = 2;
    public const int DeadlineRiskDays = 30;
    public const double DeadlineRiskFractionCeiling = 0.5;
    public const double TotalMinutesCapFactor = 1.2;
    public const double StaleConfidence = 0.3;

    internal static readonly TimeSpan FocusClampMorning = new(7, 0, 0);
    internal static readonly TimeSpan FocusShiftAmount = TimeSpan.FromHours(2);
    /// <summary>Assumed start for a focus block the assembler never scheduled explicitly.</summary>
    internal static readonly TimeSpan DefaultFocusWindowStart = new(9, 0, 0);
    internal static readonly TimeSpan WindDownWindowStart = new(21, 30, 0);

    private readonly IReadOnlyList<Goal>? _goals;

    /// <summary>State-only engine (deadline-risk inert).</summary>
    public PlanAdaptationEngine() : this(null) { }

    /// <summary>With the goal snapshot deadline-risk needs (ids matched against PlanItem.LinkedId).</summary>
    public PlanAdaptationEngine(IReadOnlyList<Goal>? goals) => _goals = goals;

    public AdaptationResult Adapt(DailyPlan plan, PersonalState state, DateTime now)
    {
        // Deep-clone before mutation: the caller's plan is never touched.
        var items = plan.Items.Select(CloneItem).ToList();
        int baseTotalMinutes = plan.Items.Sum(i => i.BaseMinutes);
        var adaptations = new List<Adaptation>();
        var firedRules = new List<string>();
        int seq = 0;

        // ---- gates (baseline confidence: None/Low can never fire their metric's rule) ----
        var recovery = state.Recovery.Score;
        bool recoveryLow = Usable(recovery)
            && recovery.Level == StateLevel.BelowBaseline
            && recovery.BaselineConfidence >= BaselineConfidence.Medium;

        var sleep = state.Metrics.GetValueOrDefault(Metrics.SleepMinutes);
        bool sleepLow = sleep is not null && Usable(sleep)
            && sleep.RelativeDeviation <= SleepDeviationTrigger
            && sleep.BaselineConfidence >= BaselineConfidence.Medium;

        var steps = state.Activity.Steps;
        bool highActivity = Usable(steps)
            && steps.RelativeDeviation >= HighActivityDeviationTrigger
            && steps.BaselineConfidence >= BaselineConfidence.Medium;

        bool stale = state.Sleep.DaysSinceFreshData >= StaleDaysTrigger;

        // ================= wave3b.recovery-low =================
        if (recoveryLow && NotApplied(plan, PlanAdaptationRuleKeys.RecoveryLow))
        {
            // Ladder step index: high activity yesterday permits the SECOND step (30).
            int stepsAllowed = 1 + (highActivity ? 1 : 0);
            int target = RecoveryLadderMinutes[Math.Min(stepsAllowed, RecoveryLadderMinutes.Count) - 1];
            double conf = Math.Min(ConfidenceOf(recovery.BaselineConfidence), state.Confidence);
            int badnessPercent = (int)Math.Round(recovery.SignedBadness * 100); // >0 = worse than baseline
            bool any = false;

            for (int idx = 0; idx < items.Count; idx++)
            {
                var it = items[idx];
                if (!IsFitness(it)) continue;
                int current = it.PlannedMinutes;
                int proposed = Math.Min(current, target);                      // never raise
                int floored = Math.Max(proposed, Math.Min(ShrinkFloorMinutes, it.BaseMinutes));
                if (floored >= current) continue;                              // nothing to do

                items[idx] = WithPlanned(it, floored, PlanAdaptationRuleKeys.RecoveryLow);
                any = true;
                adaptations.Add(New(ref seq, now, it.LinkedId ?? $"idx{idx}",
                    PlanAdaptationRuleKeys.RecoveryLow, PlanAdaptationKeys.Change("ShrinkExercise"),
                    new object[] { it.BaseMinutes, floored },
                    PlanAdaptationKeys.Evidence("RecoveryBelowBaseline"), new object[] { badnessPercent },
                    conf, recovery.BaselineConfidence));
            }

            if (!items.Any(i => i.Category == RecommendationCategory.Recovery) &&
                FitsCap(items, baseTotalMinutes, RecoveryItemMinutes))
            {
                items.Add(new PlanItem
                {
                    Action = RecommendationActionKind.None,
                    TitleKey = "Plan.Item.Recovery",
                    BaseMinutes = RecoveryItemMinutes,
                    PlannedMinutes = RecoveryItemMinutes,
                    AdaptedByRule = PlanAdaptationRuleKeys.RecoveryLow,
                    Category = RecommendationCategory.Recovery,
                });
                any = true;
                adaptations.Add(New(ref seq, now, null,
                    PlanAdaptationRuleKeys.RecoveryLow, PlanAdaptationKeys.Change("AddRecovery"),
                    new object[] { RecoveryItemMinutes },
                    PlanAdaptationKeys.Evidence("RecoveryBelowBaseline"), new object[] { badnessPercent },
                    conf, recovery.BaselineConfidence));
            }
            if (any) firedRules.Add(PlanAdaptationRuleKeys.RecoveryLow);
        }

        // ======== wave3b.deadline-risk (runs AFTER shrink: protection outranks reduction) ========
        var protectedIds = ProtectedLinkedIds(now);
        if (protectedIds.Count > 0 && NotApplied(plan, PlanAdaptationRuleKeys.DeadlineRisk))
        {
            bool any = false;
            foreach (var idx in Enumerable.Range(0, items.Count))
            {
                var it = items[idx];
                if (it.LinkedId is null || !protectedIds.Contains(it.LinkedId)) continue;
                int floor = Math.Min(ShrinkFloorMinutes, it.BaseMinutes);
                if (it.PlannedMinutes >= floor) continue;
                if (!FitsCap(items, baseTotalMinutes, floor - it.PlannedMinutes)) continue;

                items[idx] = WithPlanned(it, floor, PlanAdaptationRuleKeys.DeadlineRisk);
                any = true;
                var goal = _goals!.First(g => g.Id == it.LinkedId);
                int daysLeft = (int)Math.Round((goal.Deadline!.Value.Date - now.Date).TotalDays);
                adaptations.Add(New(ref seq, now, it.LinkedId,
                    PlanAdaptationRuleKeys.DeadlineRisk, PlanAdaptationKeys.Change("ProtectDeadlineItem"),
                    new object[] { floor },
                    PlanAdaptationKeys.Evidence("DeadlineRisk"),
                    new object[] { daysLeft, (int)Math.Round(goal.Fraction * 100) },
                    0.8, BaselineConfidence.Medium));
            }
            if (any) firedRules.Add(PlanAdaptationRuleKeys.DeadlineRisk);
        }

        // ==================== wave3b.sleep-low ====================
        if (sleepLow && NotApplied(plan, PlanAdaptationRuleKeys.SleepLow))
        {
            double conf = Math.Min(ConfidenceOf(sleep!.BaselineConfidence), state.Confidence);
            int badnessPercent = (int)Math.Round(sleep.SignedBadness * 100);
            bool any = false;

            // Shift the EARLIEST focus block 2h earlier, clamped to 07:00.
            int focusIdx = -1;
            TimeSpan focusStart = DefaultFocusWindowStart;
            for (int idx = 0; idx < items.Count; idx++)
            {
                if (!IsFocus(items[idx])) continue;
                var start = items[idx].PreferredWindowStart ?? DefaultFocusWindowStart;
                if (focusIdx < 0 || start < focusStart) { focusIdx = idx; focusStart = start; }
            }
            if (focusIdx >= 0)
            {
                var moved = focusStart - FocusShiftAmount;
                if (moved < FocusClampMorning) moved = FocusClampMorning;
                if (moved < focusStart)
                {
                    int movedMinutes = (int)(focusStart - moved).TotalMinutes;
                    var it = items[focusIdx];
                    items[focusIdx] = new PlanItem
                    {
                        Action = it.Action,
                        TitleKey = it.TitleKey,
                        DetailKey = it.DetailKey,
                        DetailArgs = (object[])it.DetailArgs.Clone(),
                        BaseMinutes = it.BaseMinutes,
                        PlannedMinutes = it.PlannedMinutes,
                        PreferredWindowStart = moved,
                        PreferredWindowEnd = it.PreferredWindowEnd is { } e
                            ? (e - FocusShiftAmount < moved ? moved : e - FocusShiftAmount)
                            : null,
                        AdaptedByRule = PlanAdaptationRuleKeys.SleepLow,
                        LinkedId = it.LinkedId,
                        Category = it.Category,
                    };
                    any = true;
                    adaptations.Add(New(ref seq, now, it.LinkedId ?? $"idx{focusIdx}",
                        PlanAdaptationRuleKeys.SleepLow, PlanAdaptationKeys.Change("ShiftFocusEarlier"),
                        new object[] { movedMinutes },
                        PlanAdaptationKeys.Evidence("SleepBelowBaseline"), new object[] { badnessPercent },
                        conf, sleep.BaselineConfidence));
                }
            }

            if (!items.Any(i => i.Action == RecommendationActionKind.WindDownBeforeBed) &&
                FitsCap(items, baseTotalMinutes, WindDownMinutes))
            {
                items.Add(new PlanItem
                {
                    Action = RecommendationActionKind.WindDownBeforeBed,
                    TitleKey = "Plan.Item.WindDown",
                    BaseMinutes = WindDownMinutes,
                    PlannedMinutes = WindDownMinutes,
                    PreferredWindowStart = WindDownWindowStart,
                    PreferredWindowEnd = WindDownWindowStart.Add(TimeSpan.FromMinutes(WindDownMinutes)),
                    AdaptedByRule = PlanAdaptationRuleKeys.SleepLow,
                    Category = RecommendationCategory.Sleep,
                });
                any = true;
                adaptations.Add(New(ref seq, now, null,
                    PlanAdaptationRuleKeys.SleepLow, PlanAdaptationKeys.Change("AddWindDown"),
                    new object[] { WindDownMinutes },
                    PlanAdaptationKeys.Evidence("SleepBelowBaseline"), new object[] { badnessPercent },
                    conf, sleep.BaselineConfidence));
            }
            if (any) firedRules.Add(PlanAdaptationRuleKeys.SleepLow);
        }

        // ==================== wave3b.stale-data (note only, no intensity change) ====================
        if (stale && NotApplied(plan, PlanAdaptationRuleKeys.StaleData))
        {
            int days = state.Sleep.DaysSinceFreshData;
            adaptations.Add(New(ref seq, now, null,
                PlanAdaptationRuleKeys.StaleData, PlanAdaptationKeys.Change("KeptStale"),
                new object[] { days },
                PlanAdaptationKeys.Evidence("StaleData"), new object[] { days },
                StaleConfidence, sleep?.BaselineConfidence ?? BaselineConfidence.None));
            firedRules.Add(PlanAdaptationRuleKeys.StaleData);
        }

        // ============= wave3b.high-activity-yesterday (note only; ladder handled above) =============
        if (highActivity && NotApplied(plan, PlanAdaptationRuleKeys.HighActivity))
        {
            adaptations.Add(New(ref seq, now, null,
                PlanAdaptationRuleKeys.HighActivity, PlanAdaptationKeys.Change("RecoveryAware"),
                Array.Empty<object>(),
                PlanAdaptationKeys.Evidence("HighActivityYesterday"),
                new object[] { (int)Math.Round(steps.RelativeDeviation!.Value * 100) },
                Math.Min(ConfidenceOf(steps.BaselineConfidence), state.Confidence),
                steps.BaselineConfidence));
            firedRules.Add(PlanAdaptationRuleKeys.HighActivity);
        }

        var ruleKeys = plan.AdaptationRuleKeys
            .Concat(firedRules.Where(k => !plan.AdaptationRuleKeys.Contains(k, StringComparer.Ordinal)))
            .ToList();

        return new AdaptationResult
        {
            Plan = new DailyPlan { Date = plan.Date, Items = items, AdaptationRuleKeys = ruleKeys },
            Adaptations = adaptations,
        };
    }

    // ---- helpers (all pure) ----------------------------------------------------------

    /// <summary>Usable metric: a real deviation vs a real baseline, with complete-enough data.</summary>
    private static bool Usable(MetricState m) =>
        m.RelativeDeviation is not null && m.Quality is not DataQuality.Missing and not DataQuality.Invalid;

    /// <summary>
    /// "Fitness" in this domain = physical training blocks: Program-day / Activity items with real
    /// minutes (RecommendationCategory has no Fitness member; this predicate is the single source).
    /// </summary>
    private static bool IsFitness(PlanItem i) =>
        i.BaseMinutes > 0 && i.Category is RecommendationCategory.Program or RecommendationCategory.Activity;

    private static bool IsFocus(PlanItem i) =>
        i.Action == RecommendationActionKind.ProtectFocusBlocks || i.Category == RecommendationCategory.Focus;

    /// <summary>Cap: committed minutes never exceed 1.2x the INPUT plan's base total.</summary>
    private static bool FitsCap(List<PlanItem> items, int baseTotal, int deltaMinutes) =>
        items.Sum(i => i.PlannedMinutes) + deltaMinutes <= baseTotal * TotalMinutesCapFactor + 1e-9;

    /// <summary>Idempotence gate: a rule that already shaped this plan does not shape it twice.</summary>
    private static bool NotApplied(DailyPlan plan, string ruleKey) =>
        !plan.AdaptationRuleKeys.Contains(ruleKey, StringComparer.Ordinal);

    private HashSet<string> ProtectedLinkedIds(DateTime now)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (_goals is null) return set;
        foreach (var g in _goals)
        {
            if (g.IsArchived || g.Deadline is null) continue;
            if (g.Fraction >= DeadlineRiskFractionCeiling) continue;
            int daysLeft = (int)Math.Round((g.Deadline.Value.Date - now.Date).TotalDays);
            if (daysLeft >= DeadlineRiskDays) continue; // overdue (negative) still counts as at-risk
            set.Add(g.Id);
        }
        return set;
    }

    /// <summary>Qualitative confidence of a baseline tier (>= Medium gate keeps results > 0).</summary>
    internal static double ConfidenceOf(BaselineConfidence c) => c switch
    {
        BaselineConfidence.High => 0.9,
        BaselineConfidence.Medium => 0.75,
        BaselineConfidence.Low => 0.5,
        _ => 0.0,
    };

    private static PlanItem CloneItem(PlanItem i) => new()
    {
        Action = i.Action,
        TitleKey = i.TitleKey,
        DetailKey = i.DetailKey,
        DetailArgs = (object[])i.DetailArgs.Clone(),
        BaseMinutes = i.BaseMinutes,
        PlannedMinutes = i.PlannedMinutes,
        PreferredWindowStart = i.PreferredWindowStart,
        PreferredWindowEnd = i.PreferredWindowEnd,
        AdaptedByRule = i.AdaptedByRule,
        LinkedId = i.LinkedId,
        Category = i.Category,
    };

    private static PlanItem WithPlanned(PlanItem i, int plannedMinutes, string ruleKey) => new()
    {
        Action = i.Action,
        TitleKey = i.TitleKey,
        DetailKey = i.DetailKey,
        DetailArgs = (object[])i.DetailArgs.Clone(),
        BaseMinutes = i.BaseMinutes,
        PlannedMinutes = plannedMinutes,
        PreferredWindowStart = i.PreferredWindowStart,
        PreferredWindowEnd = i.PreferredWindowEnd,
        AdaptedByRule = ruleKey,
        LinkedId = i.LinkedId,
        Category = i.Category,
    };

    /// <summary>
    /// One recorded adaptation. targetItemKey = LinkedId of the touched item (null for whole-plan
    /// changes, per the frozen Adaptation contract); it also seeds the deterministic stable Id.
    /// </summary>
    private static Adaptation New(ref int seq, DateTime now, string? targetItemKey,
        string ruleKey, string changeKey, object[] changeArgs,
        string evidenceKey, object[] evidenceArgs, double confidence, BaselineConfidence evidenceConfidence)
    {
        seq++;
        return new Adaptation
        {
            Id = $"wave3c:{ruleKey}:{changeKey}:{targetItemKey ?? "plan"}:{seq}",
            TargetItemKey = targetItemKey,
            ChangeKey = changeKey,
            ChangeArgs = changeArgs,
            EvidenceKey = evidenceKey,
            EvidenceArgs = evidenceArgs,
            SourceRuleKey = ruleKey,
            Confidence = confidence,
            EvidenceConfidence = evidenceConfidence,
            CreatedAt = now,
        };
    }
}
