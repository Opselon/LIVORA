namespace LIVORA.Application.Reminders;

/// <summary>
/// "What would notify me right now?" — the in-app preview behind the reminders screen (lane 09).
/// Implemented by the platform service (it owns settings + fired map); declared here so view
/// models depend on a MAUI-free seam and the pure engine stays the single source of truth.
/// </summary>
public interface IReminderEvaluator
{
    /// <summary>Candidates the engine says are due, already de-duplicated per day. Keys + args only.</summary>
    Task<IReadOnlyList<ReminderCandidate>> EvaluateNowAsync(CancellationToken ct = default);
}
