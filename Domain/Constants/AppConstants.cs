namespace LIVORA.Domain.Constants;

public static class AppConstants
{
    // Settings keys
    public const string SettingsFileName = "livora_settings.json";

    // Data store file names (one file per aggregate)
    public const string GoalsFile = "livora_goals.json";
    public const string HabitsFile = "livora_habits.json";
    public const string BootcampsFile = "livora_bootcamps.json";
    public const string ProfileFile = "livora_profile.json";
    public const string HistoryFile = "livora_history.json";

    public const int DailyStepTarget = 8000;
    public const int DailyActiveMinutesTarget = 30;
}
