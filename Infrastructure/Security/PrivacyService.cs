using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Security;
/// <summary>
/// Privacy inventory: shows the user WHAT LIVORA stores locally and lets them delete it.
/// Wave 2 honesty rule: everything stored today originates from Mock/Manual input and lives
/// on-device — the descriptions say exactly that.
///
/// SECURITY MODEL
/// <list type="bullet">
///   <item>Local-only storage. Every file named here lives in the app's own data directory
///     (<c>FileSystem.AppDataDirectory/LIVORA</c>, see <see cref="JsonFileStore"/>). Filenames are
///     fixed constants from <see cref="AppConstants"/> — no user name, device name or other
///     identifying value is ever part of a path, so the on-disk layout leaks nothing.</item>
///   <item>No network. This service performs no upload, sync or telemetry, and none is added by
///     the wipe path. The claim shown to the user ("everything stays on this device, no account,
///     no upload") is enforced by there being no client/server code to do otherwise.</item>
///   <item>No permission prompts. Deletion is a local file operation only; it never calls
///     <see cref="IPermissionService.RequestAsync"/> or any platform permission API (see
///     <see cref="PermissionService"/> for the Wave 2 no-prompt policy).</item>
///   <item>Wipe coverage: <see cref="DeleteAllLocalDataAsync"/> removes <b>6 files</b> — the 5
///     live data files (profile, goals, habits, bootcamps, history) plus the legacy Wave 1
///     settings file. Non-personal app settings (language choice, onboarding flag, profile id)
///     live in the platform preferences store instead; they are intentionally kept on wipe and
///     are listed in <see cref="RetainedSettingKeys"/> so nothing about them is hidden.</item>
///   <item>Destructive by design: the wipe is irreversible and must stay behind an explicit
///     user confirmation in the UI (Profile page) — never call it from an automatic flow.</item>
/// </list>
/// </summary>
public sealed class PrivacyService : IPrivacyService
{
    /// <summary>
    /// Every file the app has ever written under its data directory, newest first. Kept as one
    /// list so the privacy wipe cannot drift behind the persistence layer: adding a data file in
    /// <see cref="AppConstants"/> means adding it here too.
    /// <see cref="AppConstants.SettingsFileName"/> is the legacy Wave 1 settings store
    /// (replaced by <c>PreferencesSettingsService</c>); it is deleted for completeness so an
    /// upgraded install leaves nothing behind.
    /// </summary>
    internal static readonly string[] WipedFiles =
    {
        AppConstants.ProfileFile,
        AppConstants.GoalsFile,
        AppConstants.HabitsFile,
        AppConstants.BootcampsFile,
        AppConstants.HistoryFile,
        AppConstants.SettingsFileName,
    };

    /// <summary>
    /// Settings keys owned by the app in the platform preferences store. <b>Read-only
    /// inventory</b>: <see cref="DeleteAllLocalDataAsync"/> does not touch them, because they hold
    /// no personal data (language choice, onboarding flag, profile id) and wiping them would send
    /// a confirmed user back through onboarding. Exposed so the audit/tests can state exactly
    /// what remains after a wipe.
    /// </summary>
    internal static readonly string[] RetainedSettingKeys =
    {
        "preferred_language",
        "language_explicitly_set",
        "onboarding_completed",
        "profile_id",
    };

    private readonly JsonFileStore _store;
    private readonly ISettingsService _settings;

    public PrivacyService(JsonFileStore store, ISettingsService settings)
    {
        _store = store;
        _settings = settings;
    }

    /// <summary>
    /// Describes the stored-data categories. Returns localization keys and origin labels only —
    /// never user values (no names, no metric readings, no goal text) — so the inventory itself
    /// is safe to render in any UI or capture in any diagnostic.
    /// </summary>
    public Task<IReadOnlyList<StoredDataCategory>> DescribeStoredDataAsync()
    {
        IReadOnlyList<StoredDataCategory> list = new List<StoredDataCategory>
        {
            new()
            {
                Key = "Privacy.Item.Profile",
                Origin = DataOrigin.Manual,
                StorageLocationKey = "Privacy.Location.Device",
            },
            new()
            {
                Key = "Privacy.Item.GoalsHabits",
                Origin = DataOrigin.Manual,
                StorageLocationKey = "Privacy.Location.Device",
            },
            new()
            {
                Key = "Privacy.Item.HealthHistory",
                Origin = DataOrigin.Mock,
                StorageLocationKey = "Privacy.Location.Device",
            },
            new()
            {
                Key = "Privacy.Item.Programs",
                Origin = DataOrigin.Manual,
                StorageLocationKey = "Privacy.Location.Device",
            },
        };
        return Task.FromResult(list);
    }

    /// <summary>
    /// Irreversibly deletes all local data files (see <see cref="WipedFiles"/>, 6 files). Writes
    /// no log line containing user data — there is no user data in this method's inputs or
    /// outputs. Callers in the UI must confirm with the user first.
    /// </summary>
    public async Task DeleteAllLocalDataAsync()
    {
        foreach (var file in WipedFiles)
            await _store.DeleteFileAsync(file);
    }
}
