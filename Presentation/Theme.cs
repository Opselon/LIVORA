using Microsoft.Maui.Graphics;

namespace LIVORA;
/// <summary>
/// The single place code-behind and VMs get colors from (mirrors LivoraColors.xaml tokens).
/// Metric accents are theme-stable; only surfaces/text differ between light and dark.
/// </summary>
public static class Theme
{
    public static readonly Color Accent = Color.FromArgb("#3E7C6F");
    public static readonly Color TextSecondary = Color.FromArgb("#6B6963");
    public static readonly Color MetricSleep = Color.FromArgb("#5B6FA8");
    public static readonly Color MetricActivity = Color.FromArgb("#C97B3D");
    public static readonly Color MetricRecovery = Color.FromArgb("#3E7C6F");
    public static readonly Color MetricWellness = Color.FromArgb("#8A6FA8");
    public static readonly Color Positive = Color.FromArgb("#3E7C6F");
    public static readonly Color Caution = Color.FromArgb("#C0871F");
    public static readonly Color Negative = Color.FromArgb("#B65C4B");

    public static Color ForGoalStatus(Domain.Enums.GoalStatus s) => s switch
    {
        Domain.Enums.GoalStatus.OnTrack => Positive,
        Domain.Enums.GoalStatus.AtRisk => Caution,
        Domain.Enums.GoalStatus.Behind => Negative,
        _ => Accent,
    };
}
