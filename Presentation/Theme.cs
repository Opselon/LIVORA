using Microsoft.Maui.Graphics;

namespace LIVORA;

/// <summary>
/// The single place code-behind and VMs get colors from — design system v2 (lane 05).
///
/// SINGLE SOURCE OF TRUTH (wave-3 audit fix): values resolve from the LivoraColors.xaml token
/// resources through <see cref="Application.Current"/>.Resources. The hex literals below are a
/// startup-safety fallback ONLY (resource lookup before the App dictionary exists, or a resource
/// rename during a merge) — they mirror the dictionary exactly and are never authoritative.
///
/// Determinism contract for tests (lane 10): resolution is cached per key on first read, so the
/// same key returns the same Color instance for the process lifetime; <see cref="ResetCache"/>
/// exists to make a unit test re-runnable. Metric/semantic hues are theme-STABLE by design (rails,
/// dots, bars); only surfaces/text differ between light and dark — callers that need the dark
/// mirror ask for the explicit "*Dark" key by name.
/// </summary>
public static class Theme
{
    // Fallback hexes == LivoraColors.xaml v2 values (kept in sync; dictionary wins at runtime).
    private static readonly Dictionary<string, string> _fallback = new(StringComparer.Ordinal)
    {
        ["Accent"] = "#3E7C6F",
        ["AccentSoft"] = "#E4F0ED",
        ["AccentDeep"] = "#2C5D53",
        ["AccentText"] = "#2C5D53",
        ["TextPrimary"] = "#1C1B1A",
        ["TextSecondary"] = "#67655F",
        ["TextTertiary"] = "#75726B",
        ["TextOnAccent"] = "#FFFFFF",
        ["MetricSleep"] = "#5B6FA8",
        ["MetricActivity"] = "#C97B3D",
        ["MetricRecovery"] = "#3E7C6F",
        ["MetricWellness"] = "#8A6FA8",
        ["Positive"] = "#3E7C6F",
        ["Caution"] = "#8F6410",
        ["Negative"] = "#B65C4B",
    };

    private static readonly Dictionary<string, Color> _cache = new(StringComparer.Ordinal);
    private static readonly object _gate = new();

    public static Color Accent => Get("Accent");
    public static Color TextSecondary => Get("TextSecondary");
    public static Color MetricSleep => Get("MetricSleep");
    public static Color MetricActivity => Get("MetricActivity");
    public static Color MetricRecovery => Get("MetricRecovery");
    public static Color MetricWellness => Get("MetricWellness");
    public static Color Positive => Get("Positive");
    public static Color Caution => Get("Caution");
    public static Color Negative => Get("Negative");

    /// <summary>
    /// Resolve a §2 color token by key (e.g. "Accent", "CautionDark") with the documented
    /// hex fallback. Cached: repeated reads never touch the resource tree again.
    /// </summary>
    public static Color Get(string key)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }
        // Resource lookup happens OUTSIDE the lock: Application.Current?.Resources is a live
        // UI-side structure and a nested dictionary write inside the lock could interleave with
        // XAML inflation on another dispatcher turn.
        var resolved = FromResources(key)
            ?? Color.FromArgb(_fallback.TryGetValue(key, out var hex) ? hex : "#3E7C6F");
        lock (_gate)
        {
            _cache[key] = resolved;
        }
        return resolved;
    }

    private static Color? FromResources(string key)
    {
        try
        {
            var res = Microsoft.Maui.Controls.Application.Current?.Resources;
            if (res is not null && res.TryGetValue(key, out var v) && v is Color c) return c;
        }
        catch
        {
            // A not-yet-built app dictionary must never take down a color read.
        }
        return null;
    }

    /// <summary>Test hook: drop the memo so the next read re-resolves (lane 10 determinism).</summary>
    public static void ResetCache()
    {
        lock (_gate) _cache.Clear();
    }

    public static Color ForGoalStatus(Domain.Enums.GoalStatus s) => s switch
    {
        Domain.Enums.GoalStatus.OnTrack => Positive,
        Domain.Enums.GoalStatus.AtRisk => Caution,
        Domain.Enums.GoalStatus.Behind => Negative,
        _ => Accent,
    };
}
