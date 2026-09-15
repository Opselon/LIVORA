using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 — the cloud-auth seam, now REAL. Wave 3c shipped <c>CloudAuthService</c> as an honestly
/// empty placeholder ("no backend exists"); Wave 4 P1-C shipped one and P1-D shipped the client
/// session stack. This adapter projects that stack onto the pre-existing <see cref="ICloudAuthService"/>
/// contract so every current call site (the settings cloud card and its tests) keeps working while
/// the answers become true facts read from the session manager:
/// <list type="bullet">
///   <item><see cref="IsBackendConfigured"/> is <see cref="ICloudApiOptions.IsConfigured"/> — a URL
///     has been provisioned by the user/build, which is exactly what "configured" was ever allowed
///     to mean (CLIENT-CONTRACT-P1 §1: configuration and reachability are separate sentences).</item>
///   <item><see cref="StatusReasonKey"/> describes the real next missing step (no URL / no session /
///     signed in), never a guess.</item>
///   <item><see cref="SignInAsync"/> performs the real /auth/login round-trip through
///     <see cref="CloudSessionManager"/> and returns whether the SERVER said yes. Failure reasons
///     are the §5c reason keys — never server prose, never the email.</item>
/// </list>
/// The lazy resolver breaks the composition cycle the same way <see cref="DeferredCloudAuthContext"/>
/// does: a null hop (or an unconfigured build) degrades to exactly the Wave 3c behaviour — false,
/// not-available — so deleting the seam's wiring can never fake a success.
/// </summary>
public sealed class SessionCloudAuthService : ICloudAuthService
{
    /// <summary>Reason keys this adapter may report (bilingual; the Wave 3c key is kept for the
    /// unconfigured case so the existing card copy still resolves).</summary>
    public const string SignedInKey = "Cloud.Auth.Reason.SignedIn";
    public const string NoSessionKey = "Cloud.Auth.Reason.NoSession";

    private readonly Func<CloudSessionManager?> _sessions;
    private readonly ICloudApiOptions _options;

    public SessionCloudAuthService(Func<CloudSessionManager?> sessions, ICloudApiOptions options)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsBackendConfigured => _options.IsConfigured;

    public string StatusReasonKey
    {
        get
        {
            if (!_options.IsConfigured) return "Auth.Cloud.NotAvailable";
            var sm = _sessions();
            if (sm is null) return LIVORA.Infrastructure.Security.CloudAuthService.NotAvailableKey;
            if (sm.HasSession) return SignedInKey;
            return sm.LastSignOutReasonKey is { Length: > 0 } reason ? reason : NoSessionKey;
        }
    }

    public Task<bool> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var sm = _sessions();
        if (!_options.IsConfigured || sm is null)
            return Task.FromResult(false);   // same refusal shape as Wave 3c — no network, no session
        return SignInCoreAsync(sm, email, password, ct);
    }

    private static async Task<bool> SignInCoreAsync(CloudSessionManager sm, string email, string password, CancellationToken ct)
    {
        try
        {
            var outcome = await sm.SignInWithPasswordAsync(email, password, deviceLabel: null, platform: null, ct)
                .ConfigureAwait(false);
            return outcome.Succeeded;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }   // a transport that threw outward still means "the server did not say yes"
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        var sm = _sessions();
        if (sm is null) return;
        try { await sm.SignOutAsync(ct, forceLocalSignOut: true).ConfigureAwait(false); }
        catch { /* best-effort: forceLocalSignOut clears local state even when the server is unreachable */ }
    }
}
