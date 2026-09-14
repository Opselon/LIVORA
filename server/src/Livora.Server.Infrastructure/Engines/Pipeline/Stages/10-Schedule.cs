namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 10 — schedule. Places the chosen actions into the user's local day around fixed calendar
/// blocks and the declared availability window. Placement is a greedy earliest-fit in a FIXED
/// candidate order (priority desc, then score desc, then action key ordinal) so the same input
/// always yields the same day — a schedule that reshuffles itself on refresh would be unusable.
/// <para>
/// PORTED rules:
///  - evening cutoff: nothing with minutes may END at/after 22:00
///    (Application/Planning/Wave3b/RecommendationRanker.cs:54 EveningCutoff, :126 finish-before rule)
///  - a focus block is 50 minutes (RecommendationService.cs:162 focusBlocks*50)
///  - sleep-domain items (walk, focus, break) never land inside the protected sleep window; the
///    protected window is derived from the caller-supplied bedtime/wake rhythm (UserProfile's
///    PreferredBedtime/PreferredWakeTime — client Domain/Models/UserProfile.cs:11-12) so the
///    engine never assumes when someone sleeps.
/// NEW server rules: committed-minutes cap enforcement (constraint stage's number, not a re-derivation),
/// deterministic minute grid, and conflict reporting instead of silent failure.
/// </para>
/// </summary>
public static class ScheduleStage
{
    public const string StageName = "schedule";
    public const int GridMinutes = 15;              // the placement granularity
    public const int MorningStartMinutes = 7 * 60;  // earliest placement (client parity:
                                                    // PlanAdaptationEngine.cs:105 FocusClampMorning 07:00)

    public static ScheduledPlan Build(
        RecommendationBundle bundle,
        ConstraintSet constraints,
        IReadOnlyList<PlannedItem> basePlanItems,
        DateTimeOffset dayStartUtc,
        int utcOffsetMinutes)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(constraints);

        var trail = new List<TrailEntry>();
        var placed = new List<PlacedItem>();
        var blocked = new List<ScheduleConflict>();

        // Occupied minute grid for the local day (fixed blocks + base plan items).
        var occupied = new bool[1440 / GridMinutes];
        foreach (var b in constraints.FixedBlocks)
            Mark(occupied, b.Window.StartMinutesOfDay, b.Window.EndMinutesOfDay);
        foreach (var p in basePlanItems)
            Mark(occupied, p.StartMinutesOfDay, p.StartMinutesOfDay + p.PlannedMinutes);

        int committed = basePlanItems.Sum(p => p.PlannedMinutes);

        // Placement order is fixed and explainable.
        var order = bundle.Chosen
            .OrderByDescending(r => (int)r.Priority)
            .ThenByDescending(r => r.Score)
            .ThenBy(r => r.ActionKey, StringComparer.Ordinal)
            .ToList();

        foreach (var r in order)
        {
            if (r.DurationMinutes <= 0)
            {
                // Timing changes (earlier sleep, moderate screens, keep-routine, intensity cap)
                // carry no block to place; they attach to the day as directives, already recorded.
                trail.Add(TrailEntry.Of(StageName, "p1e.schedule.noop_placement", "placed_as_directive",
                    [EngineMath.Factor("action", r.ActionKey)], r.EvidenceIds));
                continue;
            }

            if (ActionCatalog.IsDemanding(r.ActionKey) &&
                committed + r.DurationMinutes > constraints.MaxNewDemandingMinutes + SumNew(placed))
            {
                blocked.Add(new ScheduleConflict(r.ActionKey, "demanding_budget",
                    $"adding {r.DurationMinutes} demanding minutes exceeds today's ceiling " +
                    $"({constraints.MaxNewDemandingMinutes})"));
                trail.Add(TrailEntry.Of(StageName, "p1e.schedule.blocked.demanding_budget", "blocked",
                    [EngineMath.Factor("action", r.ActionKey)], r.EvidenceIds));
                continue;
            }
            if (committed + r.DurationMinutes > constraints.CapMinutes)
            {
                blocked.Add(new ScheduleConflict(r.ActionKey, "total_cap",
                    $"the day is already at the {EngineMath.Pct(ConstraintStage.TotalMinutesCapFactor - 1)} cap " +
                    $"({constraints.CapMinutes} min)"));
                trail.Add(TrailEntry.Of(StageName, "p1e.schedule.blocked.total_cap", "blocked",
                    [EngineMath.Factor("action", r.ActionKey)], r.EvidenceIds));
                continue;
            }

            int start = FindSlot(occupied, r.DurationMinutes, constraints.Availability);
            if (start < 0)
            {
                blocked.Add(new ScheduleConflict(r.ActionKey, "no_slot",
                    "no free window before the evening cutoff inside the availability window"));
                trail.Add(TrailEntry.Of(StageName, "p1e.schedule.blocked.no_slot", "blocked",
                    [EngineMath.Factor("action", r.ActionKey),
                     EngineMath.Factor("needs_min", r.DurationMinutes)], r.EvidenceIds));
                continue;
            }

            Mark(occupied, start, start + r.DurationMinutes);
            committed += r.DurationMinutes;
            placed.Add(new PlacedItem(r.ActionKey, start, r.DurationMinutes, r.Priority, r.EvidenceIds));
            trail.Add(TrailEntry.Of(StageName, "p1e.schedule.place", "placed",
                [EngineMath.Factor("action", r.ActionKey),
                 EngineMath.Factor("start", RenderMinutes(start)),
                 EngineMath.Factor("minutes", r.DurationMinutes)], r.EvidenceIds));
        }

        return new ScheduledPlan(dayStartUtc, utcOffsetMinutes,
            basePlanItems.Concat(placed.Select(p => new PlannedItem(p.ActionKey, p.StartMinutesOfDay, p.PlannedMinutes)))
                .ToList(),
            blocked, trail);
    }

    private static int SumNew(List<PlacedItem> placed) => placed.Sum(p => p.PlannedMinutes);

    /// <summary>Earliest free slot ≥ MorningStartMinutes that fits and ends before the evening
    /// cutoff, inside the availability window. -1 when none exists (never a fake placement).</summary>
    private static int FindSlot(bool[] occupied, int minutes, TimeWindowUser availability)
    {
        int cells = (int)Math.Ceiling(minutes / (double)GridMinutes);
        int from = Math.Max(MorningStartMinutes, availability.StartMinutesOfDay);
        int to = Math.Min(availability.EndMinutesOfDay, (int)ScheduleStage.EveningCutoffTotalMinutes);
        for (int start = from; start + cells * GridMinutes <= to; start += GridMinutes)
        {
            bool free = true;
            for (int i = start / GridMinutes; i < start / GridMinutes + cells; i++)
                if (occupied[i]) { free = false; break; }
            if (free) return start;
        }
        return -1;
    }

    /// <summary>Evening cutoff (client parity: RecommendationRanker.cs:54 — 22:00).</summary>
    public const int EveningCutoffTotalMinutes = 22 * 60;

    private static void Mark(bool[] grid, int fromMinutes, int toMinutes)
    {
        for (int i = Math.Max(0, fromMinutes / GridMinutes);
             i < Math.Min(grid.Length, (int)Math.Ceiling(toMinutes / (double)GridMinutes)); i++)
            grid[i] = true;
    }

    public static string RenderMinutes(int minutesOfDay) =>
        $"{minutesOfDay / 60:00}:{minutesOfDay % 60:00}";
}

/// <summary>A block already on the day before the intelligence lane touches it.</summary>
public sealed record PlannedItem(string ItemId, int StartMinutesOfDay, int PlannedMinutes);

public sealed record PlacedItem(
    string ActionKey, int StartMinutesOfDay, int PlannedMinutes,
    RecommendationPriority Priority, IReadOnlyList<string> EvidenceIds);

/// <summary>A placement that could not happen — stated, with the reason, never hidden.</summary>
public sealed record ScheduleConflict(string ActionKey, string ReasonKey, string Reason);

public sealed record ScheduledPlan(
    DateTimeOffset DayStartUtc,
    int UtcOffsetMinutes,
    IReadOnlyList<PlannedItem> Items,
    IReadOnlyList<ScheduleConflict> Unplaced,
    IReadOnlyList<TrailEntry> Trail);
