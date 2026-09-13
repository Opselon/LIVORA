using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// The honest placeholder for the (not yet existing) sync backend — the built-in
/// <see cref="ISyncTransport"/> of wave 3c. There is no server: nothing is uploaded, nothing is
/// confirmed, and this class contains no HTTP client of any kind.
///
/// Contract it exists to satisfy:
/// <list type="bullet">
///   <item><see cref="IsConfigured"/> is <b>false</b>, permanently. The composed sync queue drains
///     nowhere and records stay <see cref="SyncState.Pending"/>; the UI must render exactly that
///     ("not connected", never "synced"). A queue that silently discards work while reporting
///     success is the failure mode this class is designed to make impossible.</item>
///   <item><see cref="PushAsync"/> reports <c>Success=false</c> with
///     <see cref="SyncPushResult.ErrorCategory"/> = <see cref="NoBackendCategory"/> and an empty
///     conflict list. No exception, no retry storm, no fabricated latency.</item>
///   <item><see cref="GatewayLabel"/> is a machine tag (never display prose) so logs and the
///     advanced screen can name the transport that refused the batch.</item>
/// </list>
/// Envelopes are only ever hashed descriptors (see <see cref="SyncEnvelope.PayloadHash"/>) — this
/// class reads no payload and stores nothing, so a push attempt cannot leak user data anywhere.
/// A real transport replaces this one in a future wave; deleting it is the only way the queue can
/// ever claim <see cref="SyncState.Synced"/>.
/// </summary>
public sealed class NoopSyncTransport : ISyncTransport
{
    /// <summary>Error category the UI maps to a localization key (machine identifier, not prose).</summary>
    public const string NoBackendCategory = "no-backend";

    /// <summary>Localization key the UI shows for why sync is idle.</summary>
    public const string StatusReasonKey = "Sync.Reason.NoBackend";

    /// <summary>Machine tag of this transport.</summary>
    public const string Label = "noop-local-only";

    public bool IsConfigured => false;

    public string GatewayLabel => Label;

    /// <summary>Localization key explaining the state — no remote call is attempted.</summary>
    public string ReasonKey => StatusReasonKey;

    public Task<SyncPushResult> PushAsync(
        IReadOnlyList<SyncEnvelope> batch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ct.ThrowIfCancellationRequested();
        // Nothing leaves the device. Success=false is the whole answer: no partials, no conflicts,
        // because no server was ever reached.
        return Task.FromResult(new SyncPushResult
        {
            Success = false,
            Conflicts = Array.Empty<ConflictKind>(),
            ErrorCategory = NoBackendCategory,
        });
    }
}
