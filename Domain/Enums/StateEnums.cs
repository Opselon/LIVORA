namespace LIVORA.Domain.Enums;

// Derived-state vocabulary: baselines, trends and levels.

/// <summary>How much history backs a baseline. Early-learning honesty is mandatory.</summary>
public enum BaselineConfidence
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>Trend direction over a window. InsufficientData prevents fake conclusions.</summary>
public enum TrendDirection
{
    InsufficientData,
    Stable,
    Improving,
    Declining,
}

/// <summary>Derived state label for one domain — relative to the user's personal baseline.</summary>
public enum StateLevel
{
    Unknown,
    BelowBaseline,
    Normal,
    AboveBaseline,
}
