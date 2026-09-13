using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Discovery;

/// <summary>Why a program surfaced: which signal class won, so the UI can phrase it honestly.</summary>
public enum ProgramReasonKind
{
    /// <summary>Matches a focus area the user picked in onboarding.</summary>
    FocusArea,
    /// <summary>Matches a live signal (sleep debt / stress / low recovery / activity deficit).</summary>
    StateSignal,
    /// <summary>No strong signal: sensible first step.</summary>
    General,
}

/// <summary>One deterministic suggestion. Keys + machine values only — never prose.</summary>
public sealed class ProgramSuggestion
{
    public required Bootcamp Program { get; init; }
    public required int Score { get; init; }
    public required ProgramReasonKind ReasonKind { get; init; }
    /// <summary>Localization key for the short "because …" phrase (Enum.BootcampCategory.* or Rule.Why.*).</summary>
    public required string PhraseKey { get; init; }
    /// <summary>Composition key for the full sentence ("Programs.Recommend.Reason", {0} = phrase).</summary>
    public required string ReasonKey { get; init; }
    public required object[] ReasonArgs { get; init; }
    /// <summary>The rule key that produced this suggestion (traceability/tests), null for focus-area matches.</summary>
    public string? ProducedByRule { get; init; }
}

/// <summary>
/// Deterministic program discovery (lane 08): ranks un-enrolled programs from
/// (1) the user's chosen focus areas and (2) the current derived state, with an explainable
/// reason per suggestion. Same inputs always produce the same order — the ranking is a plain
/// weighted score, not a model, and it never recommends a program the user already enrolled in.
/// MAUI-free: the test project compiles this file.
/// </summary>
public static class ProgramDiscovery
{
    /// <summary>How many suggestions a rail should show.</summary>
    public const int DefaultLimit = 3;

    // Signal thresholds mirror the rule engine so discovery and adaptation can never disagree
    // about what "low recovery" or "elevated stress" means.
    public const double RecoveryFloor = 0.55;
    public const double StressCeiling = 0.65;
    public const double SleepDeficitHours = 1.5;
    public const double ActivityDeficitFraction = 0.45;

    /// <summary>Onboarding focus-area key -> program category (semantic keys, not display text).</summary>
    public static BootcampCategory? CategoryForFocusArea(string area) => area switch
    {
        "sleep" => BootcampCategory.Sleep,
        "energy" => BootcampCategory.Fitness,
        "fitness" => BootcampCategory.Fitness,
        "focus" => BootcampCategory.Focus,
        "stress" => BootcampCategory.Mindfulness,
        "learning" => BootcampCategory.Learning,
        _ => null,
    };

    public static IReadOnlyList<ProgramSuggestion> Recommend(
        IReadOnlyList<Bootcamp> catalog,
        UserProfile profile,
        PersonalState? state,
        int limit = DefaultLimit)
    {
        var focusCategories = (profile.FocusAreas ?? new List<string>())
            .Select(CategoryForFocusArea)
            .OfType<BootcampCategory>()
            .ToList();

        var scored = new List<(Bootcamp b, int score, ProgramReasonKind kind, string phraseKey, string? rule)>();

        foreach (var b in catalog)
        {
            if (b.IsEnrolled || b.DurationDays <= 0) continue;
            int score = 0;
            var kind = ProgramReasonKind.General;
            string phraseKey = "Enum.BootcampCategory." + b.Category;
            string? rule = null;

            // (1) Focus areas: the strongest, most stable signal — the user said what matters.
            int focusHits = focusCategories.Count(c => c == b.Category);
            if (focusHits > 0)
            {
                score += 40 + 10 * (focusHits - 1);
                kind = ProgramReasonKind.FocusArea;
                phraseKey = "Enum.BootcampCategory." + b.Category;
            }

            // (2) Live signals: each mirrors exactly one RuleEngine boundary.
            if (state is not null)
            {
                var (stateScore, statePhrase, stateRule) = StateSignal(b, state);
                if (stateScore > 0)
                {
                    score += stateScore;
                    if (kind != ProgramReasonKind.FocusArea) { kind = ProgramReasonKind.StateSignal; phraseKey = statePhrase; }
                    rule = stateRule;
                }
            }

            // (3) Approachability tie-breakers: beginner first, shorter first at equal score.
            score += b.Difficulty switch
            {
                BootcampDifficulty.Beginner => 8,
                BootcampDifficulty.Intermediate => 4,
                _ => 0,
            };
            score += Math.Max(0, 6 - b.DurationDays / 7); // a 7-day program is an easier yes than 30

            scored.Add((b, score, kind, phraseKey, rule));
        }

        var ordered = scored
            .OrderByDescending(s => s.score)
            .ThenBy(s => s.b.Category)                       // deterministic, never insertion-order luck
            .ThenBy(s => s.b.DurationDays)
            .ThenBy(s => s.b.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToList();

        return ordered.Select(s => new ProgramSuggestion
        {
            Program = s.b,
            Score = s.score,
            ReasonKind = s.kind,
            PhraseKey = s.phraseKey,
            ReasonKey = "Programs.Recommend.Reason",
            ReasonArgs = Array.Empty<object>(),
            ProducedByRule = s.rule,
        }).ToList();
    }

    /// <summary>(bonus points, phrase key, rule key) for the state signal this program answers, if any.</summary>
    static (int score, string phraseKey, string? rule) StateSignal(Bootcamp b, PersonalState state)
    {
        var m = state.Metrics;

        var rec = m.GetValueOrDefault(Metrics.RecoveryScore);
        bool lowRecovery = rec is { Quality: DataQuality.Complete } && rec.Value < RecoveryFloor;
        if (lowRecovery && b.Category is BootcampCategory.Sleep or BootcampCategory.Mindfulness)
            return (25, "Rule.Why.Rule.LowRecoveryReduce", "Rule.LowRecoveryReduce");

        var stress = m.GetValueOrDefault(Metrics.Stress);
        bool highStress = stress is { Quality: DataQuality.Complete } && stress.Value > StressCeiling;
        if (highStress && b.Category is BootcampCategory.Mindfulness or BootcampCategory.Sleep or BootcampCategory.Focus)
            return (25, "Rule.Why.Rule.HighStress", "Rule.HighStress");

        var sleep = m.GetValueOrDefault(Metrics.SleepMinutes);
        bool sleepDebt = sleep is { BaselineValue: > 0, Quality: DataQuality.Complete }
            && (sleep.BaselineValue.Value - sleep.Value) / 60.0 >= SleepDeficitHours;
        if (sleepDebt && b.Category is BootcampCategory.Sleep)
            return (30, "Rule.Why.Rule.SleepDebt", "Rule.SleepDebt");

        var steps = m.GetValueOrDefault(Metrics.Steps);
        bool activityDeficit = steps is { BaselineValue: > 0, Quality: DataQuality.Complete }
            && steps.Value < steps.BaselineValue * (1 - ActivityDeficitFraction);
        if (activityDeficit && b.Category is BootcampCategory.Fitness or BootcampCategory.Habit)
            return (20, "Rule.Why.Rule.ActivityDeficit", "Rule.ActivityDeficit");

        return (0, string.Empty, null);
    }
}
