namespace LIVORA.Application.Normalization;

/// <summary>
/// Thrown when a provider hands us a unit we cannot map into a canonical one, or when a metric
/// key is not part of the normalization vocabulary. Failures must be loud at the adapter edge —
/// silently assuming a unit is how a 5k becomes a marathon.
/// The message is developer-facing text (logs/diagnostics); user-facing reporting goes through
/// the <c>Norm.Reject.Unit</c> localization key emitted by the mappers that catch this.
/// </summary>
public sealed class UnitConversionException : Exception
{
    public string MetricKey { get; }
    public string Unit { get; }

    public UnitConversionException(string metricKey, string unit)
        : base($"No canonical conversion for unit '{unit}' of metric '{metricKey}'.")
    {
        MetricKey = metricKey;
        Unit = unit;
    }

    public UnitConversionException(string metricKey, string unit, string reason)
        : base($"Cannot convert unit '{unit}' of metric '{metricKey}': {reason}")
    {
        MetricKey = metricKey;
        Unit = unit;
    }
}
