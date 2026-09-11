namespace LIVORA.Core.Constants;

public static class AppConstants
{
    public const string AppName = "LIVORA";

    // Settings keys
    public const string SettingsFileName = "livora_settings.json";

    // Data store file names (one file per aggregate)
    public const string GoalsFile = "livora_goals.json";
    public const string HabitsFile = "livora_habits.json";
    public const string BootcampsFile = "livora_bootcamps.json";
    public const string ProfileFile = "livora_profile.json";

    public const double SleepTargetHours = 7.5;
    public const int DailyStepTarget = 8000;
    public const int DailyActiveMinutesTarget = 30;
}
