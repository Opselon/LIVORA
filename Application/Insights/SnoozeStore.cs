namespace LIVORA.Application.Insights;

/// <summary>
/// One snoozed UI item: which recommendation was hidden and until when (UTC).
/// ItemId is a STABLE identity (producing rule + text key), not the per-build Guid —
/// recommendations are rebuilt deterministically every load, so a Guid id would never match again.
/// </summary>
public sealed record SnoozedItem(string ItemId, DateTime SnoozedUntilUtc);

/// <summary>
/// Persistence seam for "hide this recommendation for now" on the Today page (lane 09).
/// The implementation lives in Infrastructure (livora_snoozed.json via JsonFileStore); this
/// contract is MAUI-free so the test project can compile it and fake it in a test.
/// </summary>
public interface ISnoozeStore
{
    /// <summary>Every snooze still in effect at <paramref name="nowUtc"/>.</summary>
    Task<IReadOnlyList<SnoozedItem>> GetActiveAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>Hide <paramref name="itemId"/> for <paramref name="duration"/> (upserts; one entry per id).</summary>
    Task SnoozeAsync(string itemId, TimeSpan duration, CancellationToken ct = default);

    /// <summary>Drop a single snooze.</summary>
    Task ClearAsync(string itemId, CancellationToken ct = default);

    /// <summary>Drop every snooze (Today's "show hidden again").</summary>
    Task ClearAllAsync(CancellationToken ct = default);
}
