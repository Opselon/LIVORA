namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 7 — constraints. The hard boundaries the scheduler must never cross: fixed calendar
/// blocks, the user's declared availability, the sleep-protected window, and the safety ceilings
/// derived from state (max added load today). Every constraint names where it came from — the
/// user's own statement, a meeting block, or a fired state conclusion — so "no room today" is
/// always explainable.
/// <para>
/// PORTED ceilings (client is the source of truth):
///  - total committed minutes may not exceed 1.2× the plan's base minutes:
///    Application/Planning/Adaptive/PlanAdaptationEngine.cs:102 (TotalMinutesCapFactor)
///  - focus-block shrink never below half the original count:
///    Application/Planning/RecommendationService.cs:226 (Math.Max(0.5, ...))
///  - the 15-minute floor below which a block is better dropped than truncated:
///    PlanAdaptationEngine.cs:94 (ShrinkFloorMinutes)
/// NEW server gate (documented): when recovery is low the pipeline adds ZERO demanding minutes
/// (MaxNewDemandingMinutesWhenRecoveryLow = 0) — the brief's "add nothing demanding".
/// </para>
/// </summary>
public static class ConstraintStage
{
    public const string StageName = "constraints";

    public const double TotalMinutesCapFactor = 1.2;      // PlanAdaptationEngine.cs:102
    public const int ShrinkFloorMinutes = 15;             // PlanAdaptationEngine.cs:94
    public const double FocusShrinkFloor = 0.5;           // RecommendationService.cs:226
    public const int MaxNewDemandingMinutesWhenRecoveryLow = 0;
    public const int MaxNewDemandingMinutesNormal = 90;

    public static ConstraintSet Build(
        StateSnapshot state,
        IReadOnlyList<CalendarBlock> fixedBlocks,
        TimeWindowUser userAvailability,
        int baselinePlanMinutes)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(fixedBlocks);

        var constraints = new List<Constraint>();
        var trail = new List<TrailEntry>();

        void Add(Constraint c)
        {
            constraints.Add(c);
            trail.Add(TrailEntry.Of(StageName, c.RuleKey, c.Verdict, c.Factors, c.EvidenceIds));
        }

        bool recoveryLow = state.Fired("p1e.state.recovery_low");

        Add(new Constraint(
            "p1e.constraint.fixed_blocks",
            "bound",
            "calendar blocks the user does not control cannot be moved by the engine",
            [EngineMath.Factor("count", fixedBlocks.Count.ToString())],
            fixedBlocks.Select(b => b.EvidenceId ?? b.ItemId).ToList()));

        Add(new Constraint(
            "p1e.constraint.availability",
            "bound",
            "placement outside the user's declared availability window is refused",
            [EngineMath.Factor("window", userAvailability.Render())],
            Array.Empty<string>()));

        Add(new Constraint(
            "p1e.constraint.max_new_minutes",
            recoveryLow ? "reduced_to_zero" : "active",
            recoveryLow
                ? "recovery_low fired: add nothing demanding today (brief rule; client parity: PlanAdaptationEngine never *raises* a block, :58)"
                : "new demanding load capped at the normal ceiling",
            [EngineMath.Factor("ceiling_minutes",
                recoveryLow ? MaxNewDemandingMinutesWhenRecoveryLow : MaxNewDemandingMinutesNormal)],
            state.Find("p1e.state.recovery_low")?.EvidenceIds ?? Array.Empty<string>()));

        Add(new Constraint(
            "p1e.constraint.total_cap",
            "active",
            $"committed minutes must stay <= {EngineMath.Pct(TotalMinutesCapFactor - 1)} above the base plan (ported: PlanAdaptationEngine.TotalMinutesCapFactor)",
            [EngineMath.Factor("base_minutes", baselinePlanMinutes),
             EngineMath.Factor("cap_minutes", Math.Round(baselinePlanMinutes * TotalMinutesCapFactor))],
            Array.Empty<string>()));

        if (state.Fired("p1e.state.meeting_load_high"))
        {
            Add(new Constraint(
                "p1e.constraint.focus_protection",
                "active",
                "high meeting load: at least one uninterrupted focus block must survive the schedule",
                [EngineMath.Factor("min_focus_blocks", "1")],
                state.Find("p1e.state.meeting_load_high")?.EvidenceIds ?? Array.Empty<string>()));
        }

        if (state.Fired("p1e.state.sleep_poor"))
        {
            Add(new Constraint(
                "p1e.constraint.sleep_window_protected",
                "active",
                "poor sleep: nothing may be scheduled inside the protected sleep window, and the evening wind-down slot is reserved",
                [EngineMath.Factor("protect", "sleep_window")],
                state.Find("p1e.state.sleep_poor")?.EvidenceIds ?? Array.Empty<string>()));
        }

        return new ConstraintSet(constraints, fixedBlocks, userAvailability, baselinePlanMinutes,
            recoveryLow, trail);
    }
}

/// <summary>A non-negotiable calendar block (meeting, class). The engine may move ITS OWN items
/// around these, never through them.</summary>
public sealed record CalendarBlock(string ItemId, TimeWindowUser Window, string? EvidenceId = null);

/// <summary>A window expressed as minutes-of-day so it survives serialization without timezone
/// games (placement is evaluated against the user's local day, supplied by the caller).</summary>
public readonly record struct TimeWindowUser(int StartMinutesOfDay, int EndMinutesOfDay)
{
    public static readonly TimeWindowUser WholeDay = new(0, 1440);
    public bool Contains(int minutesOfDay) => minutesOfDay >= StartMinutesOfDay && minutesOfDay < EndMinutesOfDay;
    public int Length => Math.Max(0, EndMinutesOfDay - StartMinutesOfDay);
    public string Render() => $"{StartMinutesOfDay / 60:00}:{StartMinutesOfDay % 60:00}-{EndMinutesOfDay / 60:00}:{EndMinutesOfDay % 60:00}";
}

public sealed record Constraint(
    string RuleKey,
    string Verdict,
    string Why,
    IReadOnlyList<string> Factors,
    IReadOnlyList<string> EvidenceIds);

public sealed record ConstraintSet(
    IReadOnlyList<Constraint> Constraints,
    IReadOnlyList<CalendarBlock> FixedBlocks,
    TimeWindowUser Availability,
    int BaselinePlanMinutes,
    bool RecoveryLow,
    IReadOnlyList<TrailEntry> Trail)
{
    /// <summary>Committed-minutes ceiling (ported PlanAdaptationEngine.cs:348-350: the cap compares
    /// against the INPUT plan's base total, and the client always receives a non-empty skeleton).
    /// With no base plan at all, "1.2× nothing" would forbid every recommendation of a free day —
    /// a degenerate reading of the ported rule — so an empty plan carries no growth cap and the
    /// DEMANDING-minutes ceiling (MaxNewDemandingMinutes) remains the guard on what gets added.</summary>
    public int CapMinutes => BaselinePlanMinutes <= 0
        ? int.MaxValue
        : (int)Math.Round(BaselinePlanMinutes * ConstraintStage.TotalMinutesCapFactor);
    public int MaxNewDemandingMinutes => RecoveryLow
        ? ConstraintStage.MaxNewDemandingMinutesWhenRecoveryLow
        : ConstraintStage.MaxNewDemandingMinutesNormal;
}
