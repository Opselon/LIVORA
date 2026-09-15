namespace Livora.Server.Infrastructure.Engines.Decision;

/// <summary>
/// PURPOSE: the deterministic rule layer — a server-side restatement of the client
///          Application/Rules/RuleEngine.cs R1..R7, evaluated over an <see cref="EngineStateComputer"/>
///          snapshot instead of a MAUI PersonalState. Same thresholds, same conditions, same
///          priority ladder, same deterministic ordering (priority desc, then RuleKey ordinal).
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// INVARIANTS:
///   - every RuleHit carries: reason key, numeric args, the fact ids those args read, a confidence
///   - R7 (stale data) is a GUARD, not a task: Action=None — the engine refuses to pretend a
///     missing feed is fresh, but a missing feed is also not a reason to change anyone's day
///   - rules only fire on Complete readings with a usable baseline where the client demands one
///   - ordering is total: (priority desc, rule key asc) so two runs over equal input are equal output
/// </summary>
public static class EngineRuleEvaluator
{
    // Stable rule keys — the client strings, verbatim (RuleEngine.cs), so an evidence reference
    // recorded client-side resolves identically server-side.
    public const string R1SleepDebt = "Rule.SleepDebt";
    public const string R1bSleepDebtReduceIntensity = "Rule.SleepDebtReduceIntensity";
    public const string R2LowRecovery = "Rule.LowRecoveryReduce";
    public const string R3HighStress = "Rule.HighStress";
    public const string R3bHighStressScreens = "Rule.HighStressScreens";
    public const string R4ActivityDeficit = "Rule.ActivityDeficit";
    public const string R5PositiveMomentum = "Rule.PositiveMomentum";
    public const string R6HabitAtRisk = "Rule.HabitAtRisk";
    public const string R7StaleSleep = "Rule.StaleSleep";

    // Server-only rules (no client calendar/screen-time ingestion exists yet). Documented as
    // declared deviations in docs/architecture/wave4/requests/p1e-engines.md.
    public const string S1MeetingLoad = "Rule.Server.MeetingLoad";
    public const string S2ScreenTimeHigh = "Rule.Server.ScreenTimeHigh";

    public static IReadOnlyList<RuleHit> Evaluate(StateSnapshot state, DecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(input);
        var hits = new List<RuleHit>();
        var m = state.Metrics;

        // ---- R1: sleep debt vs personal baseline (only when the baseline is usable) -----------
        var sleep = m[EngineStateComputer.EngineMetrics.SleepMinutes];
        if (sleep.Quality == EngineQuality.Complete
            && sleep.BaselineConfidence != EngineBaselineConfidence.None
            && sleep.BaselineValue is > 0 && sleep.Value is not null)
        {
            double deficitHours = (sleep.BaselineValue.Value - sleep.Value.Value) / 60.0;
            if (deficitHours >= NumericRules.SleepDeficitHours)
            {
                hits.Add(new RuleHit(R1SleepDebt, "Rule.Reason.SleepBelowBaseline",
                    [deficitHours], EngineAction.EarlierBedtime, EngineCategory.Sleep,
                    deficitHours >= NumericRules.SleepDeficitHighHours
                        ? EnginePriority.High : EnginePriority.Medium,
                    ["bedtime:-30min"],
                    // Client parity: BaselineConfidence High->0.9, Medium->0.8, Low/None->0.6
                    sleep.BaselineConfidence switch
                    {
                        EngineBaselineConfidence.High => NumericRules.ConfSleepDebtHigh,
                        EngineBaselineConfidence.Medium => NumericRules.ConfSleepDebtMedium,
                        _ => NumericRules.ConfSleepDebtLow,
                    },
                    Evidence(sleep)));

                hits.Add(new RuleHit(R1bSleepDebtReduceIntensity, "Rule.Reason.SleepBelowBaseline",
                    [deficitHours], EngineAction.ReduceTrainingIntensity, EngineCategory.Activity,
                    EnginePriority.Medium, ["exercise:*0.5"], NumericRules.ConfSleepDebtReduce,
                    Evidence(sleep)));
            }
        }

        // ---- R2: low recovery — absolute floor OR a big drop vs personal baseline --------------
        var rec = m[EngineStateComputer.EngineMetrics.RecoveryScore];
        if (rec.Quality == EngineQuality.Complete && rec.Value is not null
            && (rec.Value.Value < NumericRules.RecoveryBelow
                || (rec.RelativeDeviation is not null && rec.RelativeDeviation < NumericRules.RecoveryBaselineDrop)))
        {
            hits.Add(new RuleHit(R2LowRecovery, "Rule.Reason.RecoveryBelowBaseline",
                [rec.Value.Value], EngineAction.ShortWalk, EngineCategory.Recovery,
                EnginePriority.Medium, ["exercise:*0.5", "recovery:+15min"], NumericRules.ConfLowRecovery,
                Evidence(rec)));
        }

        // ---- R3: elevated stress ----------------------------------------------------------------
        var stress = m[EngineStateComputer.EngineMetrics.Stress];
        if (stress.Quality == EngineQuality.Complete && stress.Value is not null
            && stress.Value.Value > NumericRules.StressAbove)
        {
            hits.Add(new RuleHit(R3HighStress, "Rule.Reason.StressAboveUsual",
                [stress.Value.Value], EngineAction.TakeBreak, EngineCategory.Stress,
                stress.Value.Value > NumericRules.StressHighAbove ? EnginePriority.High : EnginePriority.Medium,
                ["focus:-1block", "recovery:+10min"], NumericRules.ConfHighStress, Evidence(stress)));

            hits.Add(new RuleHit(R3bHighStressScreens, "Rule.Reason.StressAboveUsual",
                [stress.Value.Value], EngineAction.ModerateScreenTime, EngineCategory.Sleep,
                EnginePriority.Low, ["winddown:+30min"], NumericRules.ConfHighStressScreens, Evidence(stress)));
        }

        // ---- R4: activity deficit vs personal baseline (gated on recovery not being low) -------
        var steps = m[EngineStateComputer.EngineMetrics.Steps];
        if (steps.Quality == EngineQuality.Complete && steps.BaselineValue is > 0 && steps.Value is not null
            && steps.Value.Value < steps.BaselineValue.Value * (1 - NumericRules.ActivityDeficitFraction)
            && rec.Value >= NumericRules.RecoveryBelow)
        {
            hits.Add(new RuleHit(R4ActivityDeficit, "Rule.Reason.StepsBelowBaseline",
                Array.Empty<object>(), EngineAction.ShortWalk, EngineCategory.Activity,
                EnginePriority.Low, ["walk:+15min"], NumericRules.ConfActivityDeficit,
                Evidence(steps).Concat(Evidence(rec)).ToList()));
        }

        // ---- R5: protecting momentum (a good day gets ONE quiet anchor, not an intervention) ---
        if (sleep.Level == EngineLevel.Normal
            && rec.Value is >= NumericRules.MomentumRecoveryFloor
            && stress.Value is < NumericRules.MomentumStressCeiling)
        {
            hits.Add(new RuleHit(R5PositiveMomentum, "Rule.Reason.AllNearBaseline",
                Array.Empty<object>(), EngineAction.KeepRoutine, EngineCategory.General,
                EnginePriority.Low, Array.Empty<string>(), NumericRules.ConfMomentum,
                Evidence(sleep).Concat(Evidence(rec)).Concat(Evidence(stress)).ToList()));
        }

        // ---- R6: habit at-risk — streak >= 3, nothing logged, evening already -------------------
        if (input.AsOfUtc.Hour >= NumericRules.HabitRiskHour)
        {
            foreach (var h in input.Habits
                         .Where(h => h.Streak >= NumericRules.HabitRiskStreak && !h.CompletedToday)
                         .OrderBy(h => h.HabitId, StringComparer.Ordinal))
            {
                hits.Add(new RuleHit(R6HabitAtRisk, "Rule.Reason.HabitStreakAtRisk",
                    [h.Name, h.Streak], EngineAction.CompleteHabit, EngineCategory.Habit,
                    EnginePriority.Medium, [$"habit:{h.HabitId}:prompt"], NumericRules.ConfHabitAtRisk,
                    [InputFactId(h.HabitId)]));
            }
        }

        // ---- R7: stale feed guard — says nothing about intensity, marks the data old -------------
        if (state.DaysSinceFreshSleep > NumericRules.StaleDaysAllowance)
        {
            hits.Add(new RuleHit(R7StaleSleep, "Rule.Reason.SleepDataStale",
                [state.DaysSinceFreshSleep], EngineAction.None, EngineCategory.General,
                EnginePriority.Optional, Array.Empty<string>(), 1.0,
                [StaleLedgerFactId]));
        }

        // ---- S1: meeting load (server-only: the fusion engine needs the calendar pressure) ------
        int meetingMinutes = MeetingMinutes(input);
        if (meetingMinutes >= NumericRules.MeetingLoadHighMinutes)
        {
            hits.Add(new RuleHit(S1MeetingLoad, "Rule.Reason.MeetingLoadHigh",
                [meetingMinutes], EngineAction.ProtectFocusBlocks, EngineCategory.Focus,
                EnginePriority.High, ["focus:-1block"], 0.8,
                [MeetingFactId]));
        }

        // ---- S2: screen time (server-only until the client ships the ScreenTime provider) -------
        if (input.ScreenTimeMinutesToday is >= NumericRules.ScreenTimeHighMinutes)
        {
            hits.Add(new RuleHit(S2ScreenTimeHigh, "Rule.Reason.ScreenTimeHigh",
                [(int)input.ScreenTimeMinutesToday.Value], EngineAction.ModerateScreenTime, EngineCategory.Sleep,
                EnginePriority.Medium, ["winddown:+30min"], 0.75,
                [ScreenTimeFactId]));
        }

        return hits
            .OrderByDescending(r => (int)r.Priority)
            .ThenBy(r => r.RuleKey, StringComparer.Ordinal)
            .ToList();
    }

    // ---- fact ids this evaluator READS; EngineStateComputer.InputFacts() must supply them -------
    public const string MeetingFactId = "fact:calendar.meeting-minutes:today";
    public const string ScreenTimeFactId = "fact:screen.minutes:today";
    public const string StaleLedgerFactId = "fact:feed.stale-days:today";
    public static string InputFactId(string habitId) => $"fact:habit:{habitId}:streak";
    public static string CalendarFactId(string blockId) => $"fact:calendar:{blockId}";

    /// <summary>Total meeting minutes today (explicit field wins; otherwise summed from calendar).</summary>
    public static int MeetingMinutes(DecisionInput input) =>
        input.MeetingMinutesToday
        ?? input.Calendar.Where(c => c.Kind == "meeting")
            .Sum(c => Math.Max(0, c.EndMinutesOfDay - c.StartMinutesOfDay));


    /// <summary>Evidence for a reading that leans on a baseline: cites BOTH the reading and the
    /// baseline fact when one exists — the never-fabricate chain then reaches the window too.</summary>
    private static IReadOnlyList<string> Evidence(DerivedMetric m) =>
        m.BaselineFactId.Length > 0 ? [m.FactId, m.BaselineFactId] : [m.FactId];

    /// <summary>Every rule key the evaluator may emit — the client keys verbatim plus the two
    /// declared server-only rules. /rules serves this list so no client guesses a key.</summary>
    public static readonly IReadOnlyList<string> AllRuleKeys =
    [
        R1SleepDebt, R1bSleepDebtReduceIntensity, R2LowRecovery, R3HighStress, R3bHighStressScreens,
        R4ActivityDeficit, R5PositiveMomentum, R6HabitAtRisk, R7StaleSleep, S1MeetingLoad, S2ScreenTimeHigh,
    ];

    /// <summary>Plan-adjustment grammar shared with the fusion engine.</summary>
    public static bool IsExerciseReduction(string planAdjustment) =>
        planAdjustment.StartsWith("exercise:", StringComparison.Ordinal);
}
