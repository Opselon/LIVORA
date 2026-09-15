using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: the lockout store — failure counting and escalating lockout per normalized email, with
///          the sliding window and the escalation ladder in ONE place so no handler re-implements
///          (and re-weakens) it.
/// OWNER: Agent 03 (identity lane).
/// PROVIDES: <see cref="LoadForUpdateAsync"/> (the caller holds the per-email in-process gate, so a
///           plain read-modify-write has exactly one writer per key on the single-host deployment),
///           plus <see cref="RecordFailure"/> / <see cref="ResetOnSuccess"/> as PURE state
///           transitions — deterministic to unit-test with <see cref="FixedClock"/>, no HTTP, no
///           thread races, no sleeping.
/// LADDER (defaults, Identity:Lockout:*): failures 1..WarnAfter (3) → 401 invalid_credentials;
///   WarnAfter+1..Max (5) → 429 rate_limited; beyond Max → 403 account_locked until LockoutMinutes
///   elapsed. Any success clears the state. If the WindowMinutes window aged out, the counter
///   restarts — a slow drip never accumulates into a lockout.
/// INVARIANTS:
///   - keyed by NORMALIZED EMAIL including for addresses that resolve to no account: the ladder is
///     identical for both, so 429/403 cannot reveal existence (threat model T-03)
///   - callers check LockoutUntilUtc BEFORE any credential work; RecordFailure re-extends the
///     lockout on every failure while locked — hammering a locked mailbox keeps it locked
///   - nothing here calls SaveChanges: the transition rides on the caller's save alongside its
///     audit row, so a login attempt commits exactly once
/// </summary>
public sealed class IdentitySecurityStore
{
    public enum FailureState { Allowed, RateLimited, LockedOut }

    private readonly Func<LivoraDbContext> _dbFactory;

    public IdentitySecurityStore(Func<LivoraDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>The lane's DbContext factory seam: request handlers run inside a DI scope; a hosted
    /// scheduler resolves its own. One place to point them both.</summary>
    public LivoraDbContext Db => _dbFactory();

    public async Task<IdentitySecurityProfile> LoadForUpdateAsync(
        string normalizedEmail, CancellationToken ct)
    {
        var db = Db;
        var profile = await db.Set<IdentitySecurityProfile>()
            .FirstOrDefaultAsync(p => p.NormalizedEmail == normalizedEmail, ct);
        if (profile is null)
        {
            profile = new IdentitySecurityProfile { NormalizedEmail = normalizedEmail };
            db.Set<IdentitySecurityProfile>().Add(profile);
        }
        return profile;
    }

    /// <summary>Apply one failure and report the state the HTTP response must encode.</summary>
    public FailureState RecordFailure(
        IdentitySecurityProfile profile, IdentityOptions options, DateTimeOffset now)
    {
        var windowAged = profile.WindowStartedAtUtc is null
            || now - profile.WindowStartedAtUtc.Value > TimeSpan.FromMinutes(options.LockoutWindowMinutes);
        if (windowAged)
        {
            profile.FailedAttempts = 0;
            profile.WindowStartedAtUtc = now;
        }

        profile.FailedAttempts += 1;
        profile.LastFailureAtUtc = now;
        profile.UpdatedAtUtc = now;

        if (profile.FailedAttempts > options.LockoutMaxAttempts)
        {
            profile.LockoutUntilUtc = now.AddMinutes(options.LockoutMinutes);
            return FailureState.LockedOut;
        }
        return profile.FailedAttempts > options.LockoutWarnAfterFailures
            ? FailureState.RateLimited
            : FailureState.Allowed;
    }

    public void ResetOnSuccess(IdentitySecurityProfile profile, DateTimeOffset now)
    {
        profile.FailedAttempts = 0;
        profile.WindowStartedAtUtc = null;
        profile.LastFailureAtUtc = null;
        profile.LockoutUntilUtc = null;
        profile.UpdatedAtUtc = now;
    }

    /// <summary>Current lockout verdict from a (possibly missing) profile row.</summary>
    public static bool IsLockedOut(IdentitySecurityProfile? profile, DateTimeOffset now)
        => profile?.LockoutUntilUtc is { } until && until > now;
}
