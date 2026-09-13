using System;
using System.Linq;

namespace LIVORA.Presentation.Responsive;

/// <summary>
/// Width buckets the whole app keys its desktop layout off. Measured in device-independent
/// units (MAUI's "effective px" — the same space <c>VisualElement.Width</c> lives in, so
/// DPI scaling on Windows/high-density displays cannot fake a desktop breakpoint).
///
/// Effective values (lane 04's documented choice, referenced by LANES.md §Lane 04):
///   Narrow  width &lt; 700          — phones (portrait+landscape), split view, narrow desktop snap
///   Medium  700 &lt;= width &lt; 1000  — tablets, small snapped desktop windows
///   Wide    width &gt;= 1000        — real desktop windows: multi-column + centered content measure
/// </summary>
public enum Breakpoint
{
    Narrow = 0,
    Medium = 1,
    Wide = 2,
}

/// <summary>Pure bucket maths + spec parsing for <see cref="Breakpoint"/>. No state, no events.</summary>
public static class BreakpointScale
{
    /// <summary>Width (exclusive upper bound of Narrow / inclusive floor of Medium).</summary>
    public const double MediumFrom = 700;

    /// <summary>Width (exclusive upper bound of Medium / inclusive floor of Wide).</summary>
    public const double WideFrom = 1000;

    /// <summary>Maps a width to its bucket. Anything unmeasured (&lt;0, NaN) is Narrow — safest default.</summary>
    public static Breakpoint FromWidth(double width)
    {
        if (double.IsNaN(width) || width < MediumFrom) return Breakpoint.Narrow;
        if (width < WideFrom) return Breakpoint.Medium;
        return Breakpoint.Wide;
    }

    /// <summary>
    /// Resolves a per-bucket spec string like <c>"1,2,3"</c>:
    ///   1 value  → used for every bucket,
    ///   2 values → narrow/medium = first, wide = second,
    ///   3 values → narrow, medium, wide.
    /// Malformed input returns <paramref name="fallback"/> instead of throwing — layout code
    /// must never crash over a typo in XAML.
    /// </summary>
    public static int ValueForBucket(string? spec, Breakpoint bucket, int fallback = 1)
    {
        if (string.IsNullOrWhiteSpace(spec)) return fallback;
        var raw = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (raw.Length is 0 or > 3) return fallback;
        var nums = new int[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            if (!int.TryParse(raw[i], out nums[i]) || nums[i] < 1) return fallback;
        return nums.Length switch
        {
            1 => nums[0],
            2 => bucket == Breakpoint.Wide ? nums[1] : nums[0],
            _ => bucket switch
            {
                Breakpoint.Narrow => nums[0],
                Breakpoint.Medium => nums[1],
                _ => nums[2],
            },
        };
    }
}
