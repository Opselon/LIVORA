using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Notifications;

/// <summary>
/// The on-disk shape of <c>livora_reminders.json</c> (lane 09). One file holds three things:
/// the user's reminder settings, the per-day fired map that keeps the engine from nagging twice,
/// and a single flag recording whether this install has ever ASKED the OS for notification
/// permission. That flag is what makes the grant banner honest: "not asked yet" and "you said no"
/// are different states and must never be collapsed.
/// </summary>
public sealed class ReminderFile
{
    public List<ReminderSetting> Reminders { get; set; } = new();

    /// <summary>FireKey (<see cref="LIVORA.Application.Reminders.ReminderEngine.FireKey(string,string)"/>) → local day it last fired.</summary>
    public Dictionary<string, DateTime> FiredDays { get; set; } = new();

    /// <summary>True once RequestGrantAsync has actually been called on this install.</summary>
    public bool PermissionAsked { get; set; }

    /// <summary>Scheduled notification ids per setting id, so a re-sync can cancel exactly its own rows.</summary>
    public Dictionary<string, List<int>> ScheduledIds { get; set; } = new();
}

/// <summary>
/// Thin, corrupt-safe persistence around <see cref="JsonFileStore"/> for the reminder file.
/// Both <see cref="LocalReminderService"/> and the Today view model read/write reminders through
/// this ONE class so the fired map cannot drift behind the scheduler. File IO stays synchronous
/// inside JsonFileStore by design (see its comment about the pre-message-pump deadlock hazard).
/// </summary>
public sealed class ReminderStore
{
    /// <summary>Named here (not in AppConstants, which this lane does not own) — lane 06 lists it in the privacy inventory.</summary>
    public const string FileName = "livora_reminders.json";

    private readonly JsonFileStore _store;

    public ReminderStore(JsonFileStore store) => _store = store;

    public async Task<ReminderFile> LoadAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadObjectAsync<ReminderFile>(FileName);
        if (file is null) return new ReminderFile();
        // A hand-edited or half-written file must never crash a page: normalize, don't throw.
        file.Reminders ??= new List<ReminderSetting>();
        file.FiredDays ??= new Dictionary<string, DateTime>();
        file.ScheduledIds ??= new Dictionary<string, List<int>>();
        return file;
    }

    public Task SaveAsync(ReminderFile file, CancellationToken ct = default) =>
        _store.SaveObjectAsync(FileName, file);

    /// <summary>Mark a reminder as fired on the given local day (per-day dedupe).</summary>
    public async Task MarkFiredAsync(string fireKey, DateTime localDay, CancellationToken ct = default)
    {
        var file = await LoadAsync(ct);
        file.FiredDays[fireKey] = localDay.Date;
        // Keep the map bounded: only the last 14 days are ever compared against "today".
        if (file.FiredDays.Count > 64)
        {
            var cutoff = localDay.Date.AddDays(-13);
            foreach (var stale in file.FiredDays.Where(kv => kv.Value.Date < cutoff).Select(kv => kv.Key).ToList())
                file.FiredDays.Remove(stale);
        }
        await SaveAsync(file, ct);
    }
}
