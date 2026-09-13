using LIVORA.Domain.Enums;

namespace LIVORA.Application.State.Wave3b;

/// <summary>
/// Wave 3b lane 05 (pure): per metric, pick the PRIMARY baseline window — the HIGHEST (longest)
/// declared window that passed its sample gates and carries a trustworthy baseline
/// (confidence != None). Preference is longest-first (30 -> 14 -> 7) because the longitudinal
/// promise is "your month-long normal"; a shorter window is a fallback, never an upgrade.
///
/// When a longer window was skipped, the selection carries a machine fallback tag
/// ('fallback:30->14' reads: 30 was preferred, 14 is what we trust). No trustworthy window at
/// all => Window/Baseline null + NullReason ('no_trustworthy_window'). Trust rule shared with
/// <see cref="StateConfidenceModel"/> via <c>Wave3bTrust</c> so the two pure layers can never
/// disagree about what counts as usable. No I/O, no clock — deterministic.
/// </summary>
public sealed class PrimaryWindowSelector
{
    public PrimaryWindowSelection Select(WindowedBaselineResult result)
    {
        var preferred = Wave3bWindows.Declared[^1]; // longest declared window = the preference
        var primary = Wave3bTrust.PrimaryWindow(result);

        if (primary is null)
        {
            return new PrimaryWindowSelection
            {
                MetricKey = result.MetricKey,
                Window = null,
                Baseline = null,
                NullReason = Wave3bTags.NoTrustworthyWindow,
            };
        }

        var tags = new List<string>();
        // Step-down tag from the preferred (longest) window to what we actually trust:
        // trustworthy 30 -> no tags; 30 failed, 14 trusted -> 'fallback:30->14';
        // only 7 trusted -> 'fallback:30->7'. One tag, exact, deterministic.
        if (primary.Value != preferred)
            tags.Add($"fallback:{Wave3bWindows.Days(preferred)}->{Wave3bWindows.Days(primary.Value)}");

        return new PrimaryWindowSelection
        {
            MetricKey = result.MetricKey,
            Window = primary,
            Baseline = result[primary.Value],
            FallbackTags = tags,
        };
    }

    /// <summary>All metrics, deterministic canonical order (see <see cref="StateConfidenceModel.OrderMetrics"/>).</summary>
    public IReadOnlyDictionary<string, PrimaryWindowSelection> SelectAll(
        IReadOnlyDictionary<string, WindowedBaselineResult> baselines)
    {
        var picked = new Dictionary<string, PrimaryWindowSelection>();
        foreach (var metric in StateConfidenceModel.OrderMetrics(baselines.Keys))
            picked[metric] = Select(baselines[metric]);
        return picked;
    }
}
