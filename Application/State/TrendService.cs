using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.State;
/// <summary>
/// Deliberately simple trend engine: first-half mean vs second-half mean with a noise band.
/// Fewer than 5 samples => InsufficientData (no fake conclusions from tiny windows).
/// </summary>
public sealed class TrendService : ITrendService
{
    public const int MinSamples = 5;
    public const double BandFraction = 0.06; // 6% swing = still "stable"

    public TrendDirection Compute(IReadOnlyList<double> values, bool higherIsBetter)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToList();
        if (v.Count < MinSamples) return TrendDirection.InsufficientData;

        int half = v.Count / 2;
        double first = v.Take(half).Average();
        double second = v.Skip(half).Average();
        double scale = Math.Max(Math.Abs(first), 1e-6);
        double change = (second - first) / scale;

        if (Math.Abs(change) < BandFraction) return TrendDirection.Stable;
        bool improving = higherIsBetter ? change > 0 : change < 0;
        return improving ? TrendDirection.Improving : TrendDirection.Declining;
    }
}
