namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// STAGE 11 — adaptation. When a committed plan item collides with reality (a fixed meeting owns
/// its slot, or the day simply cannot hold it), something has to give, and the engine must state
/// WHICH lever it pulled and WHY: move, shorten, or skip.
/// <para>
/// The brief's plan-conflict case — bootcamp wants 45 minutes, a meeting owns that slot, recovery
/// is low — resolves here as: (1) if the action's feedback history says "wrong_time"
/// (<see cref="ExecutionLikelihoodModel.AdaptationLever"/>), MOVE first and keep the length;
/// (2) otherwise with recovery low and a demanding block, shorten along the client's recovery
/// ladder, then move the shortened block if the original slot is occupied; (3) only if no slot
/// exists even after shortening, skip — with the three forcing conditions as the reason codes
/// (no_slot + recovery_low + below-floor). Every branch emits a <see cref="PlanChange"/> naming
/// its rule key.
/// </para>
/// <para>
/// PORTED invariants (Application/Planning/Adaptive/PlanAdaptationEngine.cs — the client owns the
/// arithmetic; this lane owns the server-side decision trail, not new thresholds):
///  - recovery ladder {45, 30, 20} and the 15-minute floor (:93-94); the ladder NEVER raises a
///    block (:57-59 "the target is clamped by the item's current planned value, then by the
///    floor min(15, base)")
///  - deadline protection outranks reduction, runs AFTER the shrink, and only pushes back UP to
///    the floor, never above min(15, base) (:66-69, :196-221)
///  - deep-clone before mutation; the caller's plan is never touched (:120-123)
///  - idempotence: a rule key already in the applied list does not fire twice (:352-354)
///  - each change carries change factors + evidence rule key + the state that justified it,
///    mirroring the Adaptation record (:414-432).
/// </para>
/// </summary>
public static class AdaptationStage
{
    public const string StageName = "adaptation";

    public const string RuleMove = "p1e.adapt.conflict.move";
    public const string RuleShorten = "p1e.adapt.conflict.shorten";
    public const string RuleProtect = "p1e.adapt.conflict.protect";
    public const string RuleSkip = "p1e.adapt.conflict.skip";

    /// <summary>PORTED: PlanAdaptationEngine.cs:93 RecoveryLadderMinutes.</summary>
    public static readonly IReadOnlyList<int> RecoveryLadderMinutes = [45, 30, 20];
    /// <summary>PORTED: PlanAdaptationEngine.cs:94 ShrinkFloorMinutes.</summary>
    public const int ShrinkFloorMinutes = 15;

    public static AdaptationResult Resolve(
        ScheduledPlan plan,
        StateSnapshot state,
        ConstraintSet constraints,
        IReadOnlyList<string> conflictingItemIds,
        ExecutionLikelihoodModel likelihood,
        IReadOnlySet<string> protectedItemIds,
        IReadOnlyList<string> alreadyAppliedRuleKeys)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(conflictingItemIds);

        var applied = new HashSet<string>(alreadyAppliedRuleKeys, StringComparer.Ordinal);
        var changes = new List<PlanChange>();
        var trail = new List<TrailEntry>();
        var items = plan.Items.Select(p => p with { }).ToList();   // clone-first

        bool recoveryLow = state.Fired("p1e.state.recovery_low");
        var initialApplied = new HashSet<string>(alreadyAppliedRuleKeys, StringComparer.Ordinal);
        bool licensed(string ruleKey) => !initialApplied.Contains(ruleKey);

        foreach (var id in conflictingItemIds.OrderBy(x => x, StringComparer.Ordinal))
        {
            var item = items.FirstOrDefault(i => string.Equals(i.ItemId, id, StringComparison.Ordinal));
            if (item is null) continue;
            int wanted = item.PlannedMinutes;
            int originalWanted = wanted;

            // history-driven lever preference: repeated "wrong time" feedback says move first;
            // repeated "too hard" feedback says shorten first. Absent history => the default ladder.
            string lever = likelihood.LeverFor(item.ItemId);

            // ---------- 1) MOVE (when history says wrong-time, or shortening is not licensed) ----
            bool moveFirst = (lever == "move" || !recoveryLow) && licensed(RuleMove);
            if (moveFirst)
            {
                int start = FindFreeStart(items, item, wanted, constraints);
                if (start >= 0)
                {
                    int old = item.StartMinutesOfDay;
                    items[items.IndexOf(item)] = item with { StartMinutesOfDay = start };
                    Emit(RuleMove, "moved",
                        [EngineMath.Factor("from", ScheduleStage.RenderMinutes(old)),
                         EngineMath.Factor("to", ScheduleStage.RenderMinutes(start)),
                         EngineMath.Factor("minutes", wanted)],
                        $"the slot is owned by a fixed block; a free window exists at {ScheduleStage.RenderMinutes(start)} " +
                        (lever == "move" ? "— and your own feedback history said this time never works (wrong_time)" : ""),
                        item.ItemId);
                    continue;
                }
            }

            // ---------- 2) SHORTEN along the recovery ladder ------------------------------------
            int shortened = licensed(RuleShorten) ? ShortenTarget(wanted, recoveryLow) : wanted;
            if (shortened < wanted)
            {
                bool protectedItem = protectedItemIds.Contains(item.ItemId);
                if (!protectedItem || shortened >= Math.Min(ShrinkFloorMinutes, wanted))
                {
                    items[items.IndexOf(item)] = item with { PlannedMinutes = shortened };
                    Emit(RuleShorten, "shortened",
                        [EngineMath.Factor("from_min", wanted), EngineMath.Factor("to_min", shortened),
                         EngineMath.Factor("ladder", string.Join("/", RecoveryLadderMinutes)),
                         EngineMath.Factor("floor", EngineMath.Num(Math.Min(ShrinkFloorMinutes, wanted)))],
                        (recoveryLow
                            ? "recovery_low: the ladder steps down (ported PlanAdaptationEngine.RecoveryLadderMinutes) and never raises"
                            : "the day cannot hold the full block; a shorter block preserves the habit") +
                        (protectedItem ? "; the deadline floor keeps it at or above the 15-minute protection" : ""),
                        item.ItemId);
                    wanted = shortened;
                }
            }

            // after shortening, try to move the smaller block into a free window
            int slotAfter = licensed(RuleMove) ? FindFreeStart(items, item, wanted, constraints) : -1;
            if (slotAfter >= 0 && slotAfter != item.StartMinutesOfDay)
            {
                int old = item.StartMinutesOfDay;
                items[items.IndexOf(item)] = items[items.IndexOf(item)] with { StartMinutesOfDay = slotAfter };
                Emit(RuleMove, "moved",
                    [EngineMath.Factor("from", ScheduleStage.RenderMinutes(old)),
                     EngineMath.Factor("to", ScheduleStage.RenderMinutes(slotAfter)),
                     EngineMath.Factor("minutes", wanted)],
                    "the shortened block fits a free window — conflict cleared by shorten+move",
                    item.ItemId);
                continue;
            }

            // ---------- 3) SKIP — last resort, always with stated conditions ---------------------
            int floor = Math.Min(ShrinkFloorMinutes, item.PlannedMinutes);
            if (protectedItemIds.Contains(item.ItemId) && !recoveryLow)
            {
                // a protected item keeps its floor and eats the overlap: protection outranks
                // reduction (ported PlanAdaptationEngine.cs:66-69)
                Emit(RuleProtect, "protected",
                    [EngineMath.Factor("floor_min", floor)],
                    "deadline-protected: the item keeps its floor even against the conflict " +
                    "(ported wave3b.deadline-risk precedence over reduction)",
                    item.ItemId);
                continue;
            }

            items.RemoveAll(i => string.Equals(i.ItemId, item.ItemId, StringComparison.Ordinal));
            Emit(RuleSkip, "skipped",
                [EngineMath.Factor("wanted_min", originalWanted),
                 EngineMath.Factor("free_slot", "none"),
                 EngineMath.Factor("recovery_low", recoveryLow.ToString()),
                 EngineMath.Factor("floor_min", floor)],
                "no free window before the evening cutoff even after shortening, and going below the " +
                $"{ShrinkFloorMinutes}-minute floor is worse than skipping: the day stays coherent " +
                "instead of pretending",
                item.ItemId);

            void Emit(string ruleKey, string resolution, IReadOnlyList<string> factors, string why, string itemId)
            {
                changes.Add(new PlanChange(itemId, resolution, string.Join(" ", factors), factors,
                    ruleKey, why, state.EvidenceCeiling, recoveryLow));
                trail.Add(TrailEntry.Of(StageName, ruleKey, resolution,
                    [EngineMath.Factor("item", itemId), .. factors], [itemId]));
                applied.Add(ruleKey);
            }
        }

        return new AdaptationResult(
            plan with { Items = items, Trail = [.. plan.Trail, .. trail] },
            changes, trail);
    }

    /// <summary>One step DOWN the recovery ladder from the current length: the largest ladder
    /// value strictly below <paramref name="wanted"/>, clamped up by the floor min(15, base) —
    /// the same clamp the client applies (PlanAdaptationEngine.cs:160-162). DEVIATION, stated:
    /// the client picks its ladder index from "high activity yesterday"; with no such input the
    /// client leaves a 45-minute block at 45, which is not a resolution for a conflict, so this
    /// lane steps by position (45→30, 30→20, ≤20→no step). The ladder never raises a block.
    /// </summary>
    public static int ShortenTarget(int wanted, bool recoveryLow)
    {
        int target = wanted;
        foreach (var step in RecoveryLadderMinutes.OrderByDescending(x => x))
            if (step < target) { target = step; break; }
        int floored = Math.Max(target, Math.Min(ShrinkFloorMinutes, wanted));
        return floored >= wanted ? wanted : floored;
    }

    /// <summary>Earliest free grid slot for <paramref name="minutes"/> of the given item, ignoring
    /// the item's own current occupancy (so "move" can evaluate alternatives). -1 = none exists.</summary>
    private static int FindFreeStart(List<PlannedItem> items, PlannedItem self, int minutes, ConstraintSet constraints)
    {
        int g = ScheduleStage.GridMinutes;
        var grid = new bool[1440 / g];
        void Occupy(int from, int to)
        {
            for (int i = Math.Max(0, from / g); i < Math.Min(grid.Length, (int)Math.Ceiling(to / (double)g)); i++)
                grid[i] = true;
        }
        foreach (var b in constraints.FixedBlocks) Occupy(b.Window.StartMinutesOfDay, b.Window.EndMinutesOfDay);
        foreach (var it in items)
            if (!ReferenceEquals(it, self) && !string.Equals(it.ItemId, self.ItemId, StringComparison.Ordinal))
                Occupy(it.StartMinutesOfDay, it.StartMinutesOfDay + it.PlannedMinutes);

        int cells = (int)Math.Ceiling(minutes / (double)g);
        int from2 = Math.Max(ScheduleStage.MorningStartMinutes, constraints.Availability.StartMinutesOfDay);
        int to2 = Math.Min(constraints.Availability.EndMinutesOfDay, ScheduleStage.EveningCutoffTotalMinutes);
        for (int start = from2; start + cells * g <= to2; start += g)
        {
            bool free = true;
            for (int i = start / g; i < start / g + cells; i++)
                if (grid[i]) { free = false; break; }
            if (free) return start;
        }
        return -1;
    }
}

/// <summary>One resolved conflict: WHAT happened to the item, the numeric factors of the change,
/// the rule key that decided it, and the plain-language why. Mirrors the client's Adaptation
/// record discipline (PlanAdaptationEngine.cs:414-432) without reusing its MAUI-bound type.</summary>
public sealed record PlanChange(
    string ItemId,
    string Resolution,          // moved | shortened | skipped | protected
    string ChangeSummary,
    IReadOnlyList<string> ChangeFactors,
    string RuleKey,
    string Why,
    EvidenceGrade EvidenceGrade,
    bool RecoveryLow);

public sealed record AdaptationResult(
    ScheduledPlan Plan,
    IReadOnlyList<PlanChange> Changes,
    IReadOnlyList<TrailEntry> Trail);
