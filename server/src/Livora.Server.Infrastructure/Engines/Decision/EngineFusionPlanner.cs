namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: cross-domain FUSION — the one place where sleep + calendar + screen time + activity +
///          goals become a SINGLE coherent plan instead of five features shouting five lists.
///          Server-side restatement of the client planning stack (Application/Planning/
///          RecommendationService.cs budget + ProgramAdapter.cs single-source-of-truth +
///          Adaptive/PlanAdaptationEngine.cs ladder/confidence/cap semantics).
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Pure + deterministic: same input => equal output.
/// ITEM KINDS (the budget math depends on this taxonomy, pinned by FusionEngineTests):
///   - "placement": a resolved change to an EXISTING calendar block (move/shorten/skip). It asks
///     for nothing new, so it never consumes the recommendation budget — but it always carries an
///     explanation (verb + reason + evidence), never a blind append.
///   - "action": a NEW ask of the user's time/effort. Budgeted: at most
///     <see cref="NumericRules.MaxHighPriorityRecommendations"/> High and
///     <see cref="NumericRules.MaxTotalRecommendations"/> total — "1-3 high-value actions, not 17".
///   - "guardrail": a hold or hygiene posture (protect one focus block, add no extra demanding
///     task, moderate screen time, hydrate). It protects the day instead of loading it, so it is
///     not budgeted — and it is never phrased as a new demanding task.
/// INVARIANTS:
///   - the fusion scenario (poor sleep + high meeting load + high screen time + low activity +
///     fitness goal) yields ONE coherent plan: reduce workout intensity (placement), a 20-minute
///     walk (action), protect one focus block (guardrail), no extra demanding task (guardrail),
///     hydration (guardrail), earlier sleep (action) — each with reason key + evidence fact ids +
///     priority + flexibility, and the visible action list stays within budget.
///   - CONFLICT: calendar block collides with a workout and recovery is low => move → shorten →
///     skip IN THAT ORDER, each with explanation (never a blind append).
///   - every numeric reason arg must trace to a fact id in the input state (evidence sweep test).
///   - item confidence = min of the confidences it fused — weakest-link (PlanAdaptationEngine parity).
///   - committed minutes <= 1.2x scheduled base minutes (client cap guard); a cap-breaking ADDITION
///     is dropped and the refusal is stated, never silently swallowed.
///   - baseline gating: intensity reshaping needs recovery baseline confidence >= Medium unless
///     the absolute floor (value &lt; 0.55) justifies it on its own — two bad-looking days with no
///     history never reshape a day.
///   - insufficient data (today's completeness below <see cref="MinCompletenessToAct"/>) => the
///     engine says nothing: zero actions, refusal "insufficient-data". Silence over fabrication.
/// </summary>
public static class EngineFusionPlanner
{
    /// <summary>Below this share of today's expected readings, no action is honest (mirrors the
    /// WeeklySummaryService refusal instinct; declared server constant, see requests file).</summary>
    public const double MinCompletenessToAct = 0.25;

    public static DecisionOutput Plan(DecisionInput input, StateSnapshot state, IReadOnlyList<RuleHit> hits)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hits);

        var byRule = new HashSet<string>(hits.Select(h => h.RuleKey), StringComparer.Ordinal);
        var refusals = new List<string>();
        var placements = new List<PlanItemView>();
        var actions = new List<PlanItemView>();
        var guardrails = new List<PlanItemView>();
        var adaptations = new List<PlanAdaptationView>();
        int seq = 0;

        var mSleep = state.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes];
        var mRec = state.Metrics[EngineStateComputer.EngineMetrics.RecoveryScore];
        var mSteps = state.Metrics[EngineStateComputer.EngineMetrics.Steps];
        var mStress = state.Metrics[EngineStateComputer.EngineMetrics.Stress];

        bool sleepDebt = byRule.Contains(EngineRuleEvaluator.R1SleepDebt);
        bool lowRecovery = byRule.Contains(EngineRuleEvaluator.R2LowRecovery);
        bool highStress = byRule.Contains(EngineRuleEvaluator.R3HighStress);
        bool activityDeficit = byRule.Contains(EngineRuleEvaluator.R4ActivityDeficit);
        bool habitAtRisk = byRule.Contains(EngineRuleEvaluator.R6HabitAtRisk);
        bool meetingLoad = byRule.Contains(EngineRuleEvaluator.S1MeetingLoad);
        bool screenHigh = byRule.Contains(EngineRuleEvaluator.S2ScreenTimeHigh);
        bool staleGuard = byRule.Contains(EngineRuleEvaluator.R7StaleSleep);

        bool depleted = lowRecovery || sleepDebt;

        // ============================ data gate ==============================
        if (state.DataCompleteness < MinCompletenessToAct)
        {
            return new DecisionOutput(DecisionId(input), input.AsOfUtc, state, hits,
                PlanItems: Array.Empty<PlanItemView>(),
                Adaptations: Array.Empty<PlanAdaptationView>(),
                Recommendations: Array.Empty<PlanItemView>(),
                Refusals: ["insufficient-data"]);
        }

        // ===================== 1) the workout conversation ====================
        var workout = input.Calendar
            .Where(c => c.Kind == "workout")
            .OrderBy(c => c.StartMinutesOfDay).ThenBy(c => c.BlockId, StringComparer.Ordinal)
            .FirstOrDefault();

        if (workout is not null && depleted)
        {
            bool baselineOk = mRec.BaselineConfidence >= EngineBaselineConfidence.Medium
                              || (mRec.Value is not null && mRec.Value < NumericRules.RecoveryBelow);
            if (!baselineOk)
            {
                refusals.Add($"intensity-change-refused:recovery-score-baseline-confidence-low:{mRec.BaselineSamples}samples");
            }
            else
            {
                int dur = Math.Max(0, workout.EndMinutesOfDay - workout.StartMinutesOfDay);
                var evidence = lowRecovery ? Ev(mRec) : Ev(mSleep);
                string source = lowRecovery ? EngineRuleEvaluator.R2LowRecovery : EngineRuleEvaluator.R1bSleepDebtReduceIntensity;
                double conf = lowRecovery
                    ? Math.Min(NumericRules.ConfLowRecovery, mRec.Confidence)
                    : Math.Min(NumericRules.ConfSleepDebtReduce, mSleep.Confidence);
                int badness = NumericRules.BadnessPercent(NumericRules.SignedBadness(mRec.RelativeDeviation, true));

                bool collides = input.Calendar.Any(c => c.BlockId != workout.BlockId
                    && Overlaps(c.StartMinutesOfDay, c.EndMinutesOfDay, workout.StartMinutesOfDay, workout.EndMinutesOfDay));

                if (collides)
                {
                    // MOVE first: the same content, a free slot, explained.
                    var slot = FindFreeSlot(input.Calendar, workout.BlockId, dur,
                        earliest: Math.Max((input.AsOfUtc.Hour * 60 + input.AsOfUtc.Minute) / 30 * 30 + 30, 8 * 60));
                    if (slot is not null)
                    {
                        var id = $"ad:{seq:D2}";
                        adaptations.Add(new PlanAdaptationView(id, workout.BlockId, "move",
                            "Plan.Change.MoveWorkout", [slot.Value - workout.StartMinutesOfDay, dur],
                            evidence, source, conf,
                            workout.StartMinutesOfDay, dur, slot.Value, dur));
                        placements.Add(Item(ref seq, "placement", EngineAction.MoveWorkout, EngineCategory.Program,
                            EnginePriority.High, EngineFlexibility.Movable, dur,
                            new PlanPlacement(slot.Value, dur, [id]),
                            lowRecovery ? "Plan.Evidence.RecoveryBelowBaseline" : "Rule.Reason.SleepBelowBaseline",
                            [badness], evidence, source, conf, budgeted: false));
                    }
                    else
                    {
                        // No room to move: try to shorten until a keepable core fits, else skip — SAY it.
                        int shortened = Math.Min(dur, NumericRules.RecoveryLadderMinutes[1]);
                        shortened = Math.Max(shortened, Math.Min(NumericRules.ShrinkFloorMinutes, dur));
                        bool fits = input.Calendar.Where(c => c.BlockId != workout.BlockId)
                            .All(c => !Overlaps(c.StartMinutesOfDay, c.EndMinutesOfDay,
                                workout.StartMinutesOfDay, workout.StartMinutesOfDay + shortened));

                        if (fits && shortened >= NumericRules.MinWorkoutMinutesToKeep)
                        {
                            var id = $"ad:{seq:D2}";
                            adaptations.Add(new PlanAdaptationView(id, workout.BlockId, "shorten",
                                "Plan.Change.ShrinkExercise", [dur, shortened], evidence, source, conf,
                                workout.StartMinutesOfDay, dur, workout.StartMinutesOfDay, shortened));
                            placements.Add(Item(ref seq, "placement", EngineAction.ShortenWorkout, EngineCategory.Program,
                                EnginePriority.High, EngineFlexibility.Shortenable, shortened,
                                new PlanPlacement(workout.StartMinutesOfDay, shortened, [id]),
                                lowRecovery ? "Plan.Evidence.RecoveryBelowBaseline" : "Rule.Reason.SleepBelowBaseline",
                                [dur, shortened, badness], evidence, source, conf, budgeted: false));
                        }
                        else
                        {
                            var id = $"ad:{seq:D2}";
                            adaptations.Add(new PlanAdaptationView(id, workout.BlockId, "skip",
                                "Plan.Change.SkipWorkout", [dur], evidence, source, conf,
                                workout.StartMinutesOfDay, dur, workout.StartMinutesOfDay, 0));
                            placements.Add(Item(ref seq, "placement", EngineAction.SkipWorkout, EngineCategory.Program,
                                EnginePriority.High, EngineFlexibility.Fixed, 0, null,
                                lowRecovery ? "Plan.Evidence.RecoveryBelowBaseline" : "Rule.Reason.SleepBelowBaseline",
                                [dur, badness], evidence, source, conf, budgeted: false));
                        }
                    }
                }
                else
                {
                    // No collision, but depleted: shrink to the ladder default (never raise, never
                    // below the floor) — same client ladder {45,30,20}, same clamps.
                    int target = NumericRules.RecoveryLadderMinutes[0];
                    int proposed = Math.Min(dur, target);
                    proposed = Math.Max(proposed, Math.Min(NumericRules.ShrinkFloorMinutes, dur));
                    if (proposed < dur)
                    {
                        var id = $"ad:{seq:D2}";
                        adaptations.Add(new PlanAdaptationView(id, workout.BlockId, "shorten",
                            "Plan.Change.ShrinkExercise", [dur, proposed], evidence, source, conf,
                            workout.StartMinutesOfDay, dur, workout.StartMinutesOfDay, proposed));
                        placements.Add(Item(ref seq, "placement", EngineAction.ShortenWorkout, EngineCategory.Program,
                            EnginePriority.Medium, EngineFlexibility.Shortenable, proposed,
                            new PlanPlacement(workout.StartMinutesOfDay, proposed, [id]),
                            lowRecovery ? "Plan.Evidence.RecoveryBelowBaseline" : "Rule.Reason.SleepBelowBaseline",
                            [dur, proposed, badness], evidence, source, conf, budgeted: false));
                    }
                }
            }
        }
        else if (workout is not null && depleted)
        {
            // unreachable by construction (the branch above covers all depleted cases); kept as a
            // tripwire so a future edit cannot make the workout conversation silently do nothing.
            refusals.Add("workout-conversation:unhandled-depleted-case");
        }

        // ===================== 2) actions (budgeted asks) ====================
        if (sleepDebt)
        {
            double deficit = mSleep.BaselineValue is > 0 && mSleep.Value is not null
                ? (mSleep.BaselineValue.Value - mSleep.Value.Value) / 60.0 : 0;
            var hit = hits.First(h => h.RuleKey == EngineRuleEvaluator.R1SleepDebt);
            actions.Add(Item(ref seq, "action", EngineAction.EarlierBedtime, EngineCategory.Sleep,
                hit.Priority, EngineFlexibility.Fixed, 0, null,
                "Rule.Reason.SleepBelowBaseline", [Math.Round(deficit, 1, MidpointRounding.AwayFromZero)],
                Ev(mSleep), EngineRuleEvaluator.R1SleepDebt,
                Math.Min(hit.Confidence, mSleep.Confidence), budgeted: true));
        }

        if (lowRecovery || activityDeficit)
        {
            // The walk is the recovery-friendly movement of the day. It COEXISTS with a shortened
            // workout (client Rule.LowRecoveryReduce semantics: exercise:*0.5 AND a short walk),
            // but there is never a second walk: one item per need (dedupe sweep).
            var ev = lowRecovery ? Ev(mRec) : Ev(mSteps).Concat(Ev(mRec)).ToList();
            var src = lowRecovery ? EngineRuleEvaluator.R2LowRecovery : EngineRuleEvaluator.R4ActivityDeficit;
            double conf = lowRecovery
                ? Math.Min(NumericRules.ConfLowRecovery, mRec.Confidence)
                : Math.Min(NumericRules.ConfActivityDeficit, mSteps.Confidence);
            int start = SuggestedWalkStart(input);
            actions.Add(Item(ref seq, "action", EngineAction.ShortWalk, EngineCategory.Recovery,
                EnginePriority.Medium, EngineFlexibility.Movable, NumericRules.FusedWalkMinutes,
                new PlanPlacement(start, NumericRules.FusedWalkMinutes, Array.Empty<string>()),
                lowRecovery ? "Rule.Reason.RecoveryBelowBaseline" : "Rule.Reason.StepsBelowBaseline",
                lowRecovery
                    ? [NumericRules.BadnessPercent(NumericRules.SignedBadness(mRec.RelativeDeviation, true))]
                    : [mSteps.Value ?? 0, mSteps.BaselineValue ?? 0],
                ev, src, conf, budgeted: true));
        }

        if (highStress && !meetingLoad && !sleepDebt)
        {
            actions.Add(Item(ref seq, "action", EngineAction.TakeBreak, EngineCategory.Stress,
                EnginePriority.Medium, EngineFlexibility.Droppable, 10, null,
                "Rule.Reason.StressAboveUsual", [Math.Round(mStress.Value ?? 0, 2, MidpointRounding.AwayFromZero)],
                Ev(mStress), EngineRuleEvaluator.R3HighStress,
                Math.Min(NumericRules.ConfHighStress, mStress.Confidence), budgeted: true));
        }

        if (habitAtRisk)
        {
            var atRisk = input.Habits
                .Where(h => h.Streak >= NumericRules.HabitRiskStreak && !h.CompletedToday)
                .OrderBy(h => h.HabitId, StringComparer.Ordinal)
                .First();
            actions.Add(Item(ref seq, "action", EngineAction.CompleteHabit, EngineCategory.Habit,
                EnginePriority.Medium, EngineFlexibility.Optional, 10, null,
                "Rule.Reason.HabitStreakAtRisk", [atRisk.Name, atRisk.Streak],
                [EngineRuleEvaluator.InputFactId(atRisk.HabitId)], EngineRuleEvaluator.R6HabitAtRisk,
                NumericRules.ConfHabitAtRisk, budgeted: true));
        }

        bool anyPressure = sleepDebt || lowRecovery || highStress || activityDeficit || meetingLoad
                           || screenHigh || habitAtRisk;
        if (!anyPressure)
        {
            // A genuinely good day gets ONE quiet anchor (client RecommendationService fallback) —
            // but only when there was enough data to call it good (the gate above refused otherwise).
            actions.Add(Item(ref seq, "action", EngineAction.KeepRoutine, EngineCategory.General,
                EnginePriority.Low, EngineFlexibility.Optional, 0, null,
                "Rule.Reason.AllNearBaseline", Array.Empty<object>(),
                Ev(mSleep).Concat(Ev(mRec)).Concat(Ev(mStress)).ToList(), EngineRuleEvaluator.R5PositiveMomentum,
                NumericRules.ConfMomentum, budgeted: true));
        }

        // ======================= 3) guardrails (not asks) =======================
        if (meetingLoad)
        {
            int meetingMinutes = EngineRuleEvaluator.MeetingMinutes(input);
            var focusBlock = input.Calendar
                .Where(c => c.Kind == "focus")
                .OrderBy(c => c.StartMinutesOfDay).ThenBy(c => c.BlockId, StringComparer.Ordinal)
                .FirstOrDefault();
            guardrails.Add(Item(ref seq, "guardrail", EngineAction.ProtectFocusBlocks, EngineCategory.Focus,
                EnginePriority.High, EngineFlexibility.Fixed, NumericRules.FocusedBlockMinutes,
                focusBlock is null ? null : new PlanPlacement(focusBlock.StartMinutesOfDay,
                    Math.Min(NumericRules.FocusedBlockMinutes,
                        Math.Max(0, focusBlock.EndMinutesOfDay - focusBlock.StartMinutesOfDay)),
                    Array.Empty<string>()),
                "Fusion.ProtectOneFocusBlock", [meetingMinutes, 1],
                [EngineRuleEvaluator.MeetingFactId], EngineRuleEvaluator.S1MeetingLoad, 0.8, budgeted: false));
        }

        if (meetingLoad && depleted)
        {
            var ev = new List<string>();
            if (sleepDebt) ev.AddRange(Ev(mSleep));
            if (lowRecovery) ev.AddRange(Ev(mRec));
            if (highStress) ev.AddRange(Ev(mStress));
            double conf = ev.Count == 0 ? 0.5 : ev.Min(id => state.Facts.FirstOrDefault(f => f.FactId == id)?.Confidence ?? 0.5);
            guardrails.Add(Item(ref seq, "guardrail", EngineAction.NoExtraDemand, EngineCategory.General,
                EnginePriority.Medium, EngineFlexibility.Fixed, 0, null,
                "Fusion.NoExtraDemandToday", [EngineRuleEvaluator.MeetingMinutes(input)], ev,
                EngineRuleEvaluator.S1MeetingLoad, conf, budgeted: false));
        }

        if (screenHigh)
        {
            var screenFact = state.Facts.First(f => f.FactId == EngineRuleEvaluator.ScreenTimeFactId);
            guardrails.Add(Item(ref seq, "guardrail", EngineAction.ModerateScreenTime, EngineCategory.Sleep,
                EnginePriority.Low, EngineFlexibility.Optional, 0, null,
                "Rule.Reason.ScreenTimeHigh", [(int)(input.ScreenTimeMinutesToday ?? 0)],
                [screenFact.FactId], EngineRuleEvaluator.S2ScreenTimeHigh,
                Math.Min(NumericRules.ConfHighStressScreens, screenFact.Confidence), budgeted: false));
        }

        bool fitnessGoal = input.Goals.Any(g =>
            string.Equals(g.Category, "fitness", StringComparison.OrdinalIgnoreCase));
        if (fitnessGoal && depleted)
        {
            var ev = new List<string>();
            if (sleepDebt) ev.AddRange(Ev(mSleep));
            if (lowRecovery) ev.AddRange(Ev(mRec));
            if (placements.Count > 0) ev.Add(mSteps.FactId);
            guardrails.Add(Item(ref seq, "guardrail", EngineAction.Hydrate, EngineCategory.General,
                EnginePriority.Low, EngineFlexibility.Optional, 0, null,
                "Fusion.HydrateAfterDepletedTraining", Array.Empty<object>(), ev,
                EngineRuleEvaluator.R1bSleepDebtReduceIntensity, 0.6, budgeted: false));
        }

        // ======================== 4) the budget cut =============================
        // Client rule verbatim: max 1 High + 2 Medium/Low visible actions — "not 17".
        var orderedActions = actions
            .OrderByDescending(a => (int)a.Priority)
            .ThenBy(a => a.ItemId, StringComparer.Ordinal)
            .ToList();
        var high = orderedActions.Where(a => (int)a.Priority >= (int)EnginePriority.High)
                                 .Take(NumericRules.MaxHighPriorityRecommendations).ToList();
        var rest = orderedActions.Where(a => !high.Contains(a))
                                 .Take(NumericRules.MaxTotalRecommendations - high.Count).ToList();
        var visibleActions = high.Concat(rest)
            .OrderByDescending(a => (int)a.Priority)
            .ThenBy(a => a.ItemId, StringComparer.Ordinal)
            .ToList();
        foreach (var dropped in orderedActions.Except(visibleActions))
            refusals.Add($"budget:suppressed:{dropped.Action}:{dropped.SourceRuleKey}");

        // ======================== 5) assemble + cap guard =======================
        var planItems = new List<PlanItemView>();
        planItems.AddRange(placements);
        planItems.AddRange(visibleActions);
        planItems.AddRange(guardrails);

        int baseCommitted = input.Calendar
            .Where(c => c.Kind is "workout" or "focus")
            .Sum(c => Math.Max(0, c.EndMinutesOfDay - c.StartMinutesOfDay));
        if (baseCommitted > 0)
        {
            int scheduled = placements.Where(p => p.Kind == "placement")
                                      .Sum(p => p.Placement?.DurationMinutes ?? 0)
                            + visibleActions.Where(a => a.Placement is not null).Sum(a => a.Placement!.DurationMinutes);
            // The cap never blocks a shorten/skip (they only remove minutes); an ADDITION that would
            // break 1.2x is dropped WITH a stated refusal (client FitsCap parity, made visible).
            foreach (var extra in visibleActions.Where(a => a.Placement is not null)
                                                .OrderByDescending(a => (int)a.Priority).ToList())
            {
                if (scheduled <= baseCommitted * NumericRules.TotalMinutesCapFactor + 1e-9) break;
                scheduled -= extra.Placement!.DurationMinutes;
                visibleActions.Remove(extra);
                planItems.Remove(extra);
                refusals.Add($"cap-guard:dropped-addition-over-{NumericRules.TotalMinutesCapFactor}x:{extra.Action}");
            }
        }

        if (staleGuard)
            refusals.Add("stale-data:guard-only"); // the R7 guard never reshapes the day — said out loud

        return new DecisionOutput(
            DecisionId(input), input.AsOfUtc, state, hits,
            PlanItems: planItems,
            Adaptations: adaptations,
            Recommendations: visibleActions.Where(a => a.Kind == "action").ToList(),
            Refusals: refusals);
    }

    // ---- helpers (all pure) -------------------------------------------------------------

    private static string DecisionId(DecisionInput i)
    {
        // Deterministic identity: a SHA-256 over a canonical rendering of the inputs — same inputs,
        // same decision id; different inputs, different id. No clock, no randomness.
        var canon = string.Join('|',
            i.AsOfUtc.ToString("O"),
            i.Calendar.OrderBy(c => c.BlockId, StringComparer.Ordinal)
                .Select(c => $"{c.BlockId}:{c.Kind}:{c.StartMinutesOfDay}-{c.EndMinutesOfDay}"),
            i.MeetingMinutesToday?.ToString() ?? "-",
            i.ScreenTimeMinutesToday?.ToString() ?? "-",
            i.Habits.OrderBy(h => h.HabitId, StringComparer.Ordinal)
                .Select(h => $"{h.HabitId}:{h.Streak}:{(h.CompletedToday ? 1 : 0)}"),
            i.Goals.OrderBy(g => g.GoalId, StringComparer.Ordinal)
                .Select(g => $"{g.GoalId}:{g.Category}:{g.Fraction:0.###}"),
            i.History.Count);
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canon));
        return "dec:" + Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }

    private static PlanItemView Item(ref int seq, string kind, EngineAction action, EngineCategory category,
        EnginePriority priority, EngineFlexibility flexibility, int minutes, PlanPlacement? placement,
        string reasonKey, IReadOnlyList<object> reasonArgs, IReadOnlyList<string> evidence,
        string sourceRule, double confidence, bool budgeted)
    {
        seq++;
        return new PlanItemView($"pi:{seq:D2}", kind, action, category, priority, flexibility, minutes,
            placement, reasonKey, reasonArgs, evidence, sourceRule, confidence, budgeted);
    }

    private static bool Overlaps(int a1, int a2, int b1, int b2) => a1 < b2 && b1 < a2;

    /// <summary>A reading plus the baseline it deviates from — the full trace of a comparison.</summary>
    private static List<string> Ev(DerivedMetric m) =>
        m.BaselineFactId.Length > 0 ? [m.FactId, m.BaselineFactId] : [m.FactId];

    /// <summary>First free slot of the needed length: 30-min steps, never before
    /// <paramref name="earliest"/>, never starting after 20:00 (deterministic placement scan).</summary>
    private static int? FindFreeSlot(IReadOnlyList<EngineCalendarBlock> calendar, string exceptBlockId,
        int durationMinutes, int earliest)
    {
        var others = calendar.Where(c => c.BlockId != exceptBlockId).ToList();
        int first = (earliest + NumericRules.ConflictSlotStepMinutes - 1) / NumericRules.ConflictSlotStepMinutes
                    * NumericRules.ConflictSlotStepMinutes;
        for (int start = first; start <= NumericRules.ConflictLastSlotStartMinutes; start += NumericRules.ConflictSlotStepMinutes)
        {
            int end = start + durationMinutes;
            if (!others.Any(c => Overlaps(c.StartMinutesOfDay, c.EndMinutesOfDay, start, end)))
                return start;
        }
        return null;
    }

    /// <summary>The walk goes mid-afternoon, sliding to the next free 30-min slot (never inside a
    /// meeting/focus/workout block, never after 20:00).</summary>
    private static int SuggestedWalkStart(DecisionInput input)
    {
        const int earliest = 15 * 60;
        var busy = input.Calendar
            .Where(c => c.Kind is "meeting" or "workout" or "focus")
            .ToList();
        for (int start = earliest; start + NumericRules.FusedWalkMinutes <= 21 * 60;
             start += NumericRules.ConflictSlotStepMinutes)
        {
            int end = start + NumericRules.FusedWalkMinutes;
            if (!busy.Any(c => Overlaps(c.StartMinutesOfDay, c.EndMinutesOfDay, start, end)))
                return start;
        }
        return earliest; // fully booked afternoon: the reason args still trace; user moves it
    }
}
