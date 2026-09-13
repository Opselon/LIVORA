using LIVORA.Application.Abstractions;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;

namespace LIVORA.Application.Intelligence;

/// <summary>
/// Builds the minimal, privacy-filtered context an AI call may receive (Wave 3c lane 02).
///
/// PRIVACY CONTRACT — the whole point of this class:
///  - only STATE-DELTA FACTS go out: MetricKey + current + baseline + unit (already computed
///    deterministically by the state layer), capped at <see cref="MaxFacts"/>;
///  - goal TITLES only (never descriptions, never whole stores);
///  - profile contributes NOTHING free-text: the user's Name and FocusAreas notes are excluded
///    by construction. Only the language code (an enum rendering) is derived from settings-ish
///    state, and no profile field is serialized at all here;
///  - no raw history, no habit names, no timestamps.
/// A future reviewer must be able to read this file and see that a leak is structurally
/// impossible: no dictionary of "extras" exists to smuggle text through.
/// </summary>
public sealed class ContextBuilder : IContextBuilder
{
    /// <summary>Hard cap on facts per call — keeps the prompt small and the surface auditable.</summary>
    public const int MaxFacts = 12;

    /// <summary>Canonical human units per metric family (the AI must echo these verbatim).</summary>
    internal static string UnitFor(string metricKey) => metricKey switch
    {
        Metrics.SleepMinutes or Metrics.ActiveMinutes or Metrics.BedtimeMinutes => "minutes",
        Metrics.Steps => "steps",
        Metrics.RecoveryScore or Metrics.SleepQuality or Metrics.SleepConsistency
            or Metrics.Stress or Metrics.Mood or Metrics.Energy or Metrics.FocusEstimate => "ratio",
        Metrics.RestingHeartRate => "bpm",
        Metrics.HrvMs => "ms",
        _ => "unit",
    };

    private readonly Func<string> _language;

    /// <param name="languageResolver">Returns "en"/"fa" at build time (app language). Defaults to en.</param>
    public ContextBuilder(Func<string>? languageResolver = null)
        => _language = languageResolver ?? (() => "en");

    /// <summary>Metric keys ordered by coaching importance — the cap keeps the top N.</summary>
    private static readonly string[] FactOrder =
    {
        Metrics.SleepMinutes, Metrics.RecoveryScore, Metrics.Stress, Metrics.Steps,
        Metrics.ActiveMinutes, Metrics.SleepQuality, Metrics.SleepConsistency,
        Metrics.RestingHeartRate, Metrics.HrvMs, Metrics.Mood, Metrics.Energy,
        Metrics.FocusEstimate, Metrics.BedtimeMinutes,
    };

    public Task<IntelligenceContext> BuildAsync(
        PersonalState state, UserProfile profile,
        IReadOnlyList<Recommendation> deterministicRecommendations,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(profile);

        var facts = new List<StateDeltaFact>(Math.Min(MaxFacts, FactOrder.Length));
        foreach (var key in FactOrder)
        {
            if (facts.Count >= MaxFacts) break;
            if (!state.Metrics.TryGetValue(key, out var m)) continue;
            if (double.IsNaN(m.Value)) continue;
            facts.Add(new StateDeltaFact(
                MetricKey: m.MetricKey,
                Current: Math.Round(m.Value, 2),
                Baseline: m.BaselineValue is null ? null : Math.Round(m.BaselineValue.Value, 2),
                Unit: UnitFor(m.MetricKey),
                FactKey: string.Empty,
                FactArgs: Array.Empty<object>()));
        }

        // Goal titles: PersonalState carries snapshots with display names; titles only, never
        // descriptions or progress history. Capped so a huge goal list cannot bloat the prompt.
        var goalTitles = state.GoalSnapshots
            .Select(g => g.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(SanitizeTitle)
            .Take(6)
            .ToList();

        var language = _language() == "fa" ? "fa" : "en";

        var context = new IntelligenceContext
        {
            CorrelationId = "ai-" + Guid.NewGuid().ToString("N")[..12],
            LanguageCode = language,
            State = state,
            Profile = profile,
            DeterministicRecommendations = deterministicRecommendations ?? Array.Empty<Recommendation>(),
            ActiveGoalTitles = goalTitles,
            StateFacts = facts,
        };
        return Task.FromResult(context);
    }

    /// <summary>
    /// Titles are user text, so they get a defensive scrub: no control chars, no digits
    /// (numbers may only travel as verified claims), capped length. Anything uglier is dropped.
    /// </summary>
    private static string SanitizeTitle(string title)
    {
        var t = new string(title.Where(c => !char.IsControl(c) && !char.IsDigit(c)).ToArray()).Trim();
        if (t.Length > 60) t = t[..60];
        return t;
    }
}
