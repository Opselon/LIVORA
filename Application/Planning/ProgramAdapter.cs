using LIVORA.Application.Abstractions;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Planning;
/// <summary>
/// Replaces Wave 1's inline bootcamp adaptation. Uses the shared deterministic rule engine so
/// program adaptation and recommendations can never disagree (single source of truth), and so
/// both remain testable without UI.
/// </summary>
public sealed class ProgramAdapter
{
    private readonly IRuleEngine _rules;

    public ProgramAdapter(IRuleEngine rules) => _rules = rules;

    public (BootcampDay Day, string? AdaptationRuleKey) AdaptDay(
        Bootcamp bootcamp, BootcampDay planned, PersonalState state, UserProfile profile,
        IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits, DateTime now)
    {
        var intensityKeys = new[] { "Bootcamp.Plan.Workout" };
        bool intense = intensityKeys.Contains(planned.PlanTitleKey);

        var fired = _rules.Evaluate(state, profile, goals, habits, now)
            .Where(r => r.PlanAdjustments.Any(a => a.StartsWith("exercise:")))
            .OrderByDescending(r => (int)r.Priority)
            .ToList();

        if (intense && fired.Count > 0)
        {
            var rule = fired[0];
            var adapted = new BootcampDay
            {
                DayNumber = planned.DayNumber,
                PlanTitleKey = "Bootcamp.Plan.LightWalk",
                PlanDescriptionKey = "Bootcamp.Plan.LightWalk.Desc",
                TargetMinutes = 10,
                IsAdapted = true,
                IsCompleted = planned.IsCompleted,
            };
            return (adapted, rule.RuleKey);
        }
        return (planned, null);
    }
}
