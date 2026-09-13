namespace LIVORA.Application.Reminders;

/// <summary>
/// Dedupe ledger for reminder candidates (lane 09). The engine is pure, so the "what already
/// fired today" memory has to be injected — the platform implementation keeps it in
/// <c>livora_reminders.json</c> beside the settings so a restart cannot re-nag.
/// </summary>
public interface IReminderLedger
{
    /// <summary>FireKey → the local day it last fired. Missing key = never fired.</summary>
    Task<IReadOnlyDictionary<string, DateTime>> GetFiredDaysAsync(CancellationToken ct = default);

    /// <summary>Record that a candidate fired on <paramref name="localDay"/> (per-day dedupe).</summary>
    Task MarkFiredAsync(string fireKey, DateTime localDay, CancellationToken ct = default);
}
