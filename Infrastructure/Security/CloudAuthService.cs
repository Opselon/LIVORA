using LIVORA.Application.Abstractions;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// Wave 3c (lane 01) — the cloud-auth seam, honestly empty. There is no account backend in this
/// wave (no server, no client, no endpoint), and the product law is that a missing backend is
/// reported as missing, never simulated.
///
/// So this class does exactly three things and cannot do anything else:
/// <list type="bullet">
///   <item><see cref="IsBackendConfigured"/> is <c>false</c> — hardcoded, not inferred, not
///     configurable. The UI reads this instead of guessing, and lane 07's AI/settings screens render
///     the read-only "not available" card from it.</item>
///   <item><see cref="StatusReasonKey"/> is <see cref="NotAvailableKey"/>, the localization key that
///     explains WHY (no backend shipped yet) in both languages.</item>
///   <item><see cref="SignInAsync"/> returns <c>false</c> with <see cref="LastAttemptReasonKey"/> set.
///     It performs NO network call, NO token, no "demo success", no in-memory session that would let
///     any downstream code believe a user is signed in. A fake success here is the single worst
///     failure mode available to this lane: it would light up every account-gated feature with
///     nothing behind it.</item>
/// </list>
/// MAUI-free. A future wave replaces the registration in the composition root with a real provider;
/// deleting this file is then the only way the flag can become true, and no call site needs to
/// change (that is the point of the seam).
/// </summary>
public sealed class CloudAuthService : ICloudAuthService
{
    /// <summary>The reason key the UI shows for the unavailable state.</summary>
    public const string NotAvailableKey = "Auth.Cloud.NotAvailable";

    /// <summary>Machine tag of the last refused attempt (never user email, never prose).</summary>
    public const string NoBackendCategory = "no-backend";

    /// <summary>Hardcoded: this build has no cloud backend. Not a setting, not a guess.</summary>
    public bool IsBackendConfigured => false;

    public string StatusReasonKey => NotAvailableKey;

    /// <summary>Reason key of the most recent refused sign-in (null when nothing was attempted).</summary>
    public string? LastAttemptReasonKey { get; private set; }

    /// <summary>Number of attempts refused this session — lets the UI say "we did try" honestly.</summary>
    public int RefusedAttempts { get; private set; }

    /// <summary>
    /// Always <c>false</c>: no request is made, no session is created. The email argument is never
    /// stored, logged or echoed — only the shape of the attempt is counted.
    /// </summary>
    public Task<bool> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RefusedAttempts++;
        LastAttemptReasonKey = NotAvailableKey;
        // Deliberate: no `return true` path exists in this class. A future contributor who wants a
        // demo login has to build the backend first, which is exactly the ordering the honesty rules
        // demand.
        return Task.FromResult<bool>(false);
    }

    /// <summary>Nothing to sign out of. Clears the local attempt state; performs no call.</summary>
    public Task SignOutAsync(CancellationToken ct = default)
    {
        LastAttemptReasonKey = null;
        return Task.CompletedTask;
    }
}
