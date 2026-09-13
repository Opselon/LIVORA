using LIVORA.Application.Insights;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Notifications;

/// <summary>
/// JSON-file implementation of <see cref="ISnoozeStore"/>: <c>livora_snoozed.json</c> through the
/// shared <see cref="JsonFileStore"/>. Synchronous IO is by design (see JsonFileStore's
/// pre-message-pump note); corrupt files read as empty, never throw. Expired snoozes are pruned on
/// every write so the file cannot grow unbounded.
/// </summary>
public sealed class SnoozeStore : ISnoozeStore
{
    /// <summary>File name (named here; lane 06 lists it in the privacy inventory).</summary>
    public const string FileName = "livora_snoozed.json";

    private readonly JsonFileStore _store;

    public SnoozeStore(JsonFileStore store) => _store = store;

    public async Task<IReadOnlyList<SnoozedItem>> GetActiveAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var items = await _store.LoadListAsync<SnoozedItem>(FileName);
        return items.Where(i => i is { ItemId: not null } && i.SnoozedUntilUtc > nowUtc).ToList();
    }

    public async Task SnoozeAsync(string itemId, TimeSpan duration, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var items = (await _store.LoadListAsync<SnoozedItem>(FileName))
            .Where(i => !string.IsNullOrEmpty(i.ItemId) && i.ItemId != itemId && i.SnoozedUntilUtc > nowUtc)
            .ToList();
        items.Add(new SnoozedItem(itemId, nowUtc + duration));
        await _store.SaveListAsync(FileName, items);
    }

    public async Task ClearAsync(string itemId, CancellationToken ct = default)
    {
        var items = (await _store.LoadListAsync<SnoozedItem>(FileName))
            .Where(i => i.ItemId != itemId)
            .ToList();
        await _store.SaveListAsync(FileName, items);
    }

    public Task ClearAllAsync(CancellationToken ct = default) =>
        _store.SaveListAsync(FileName, new List<SnoozedItem>());
}
