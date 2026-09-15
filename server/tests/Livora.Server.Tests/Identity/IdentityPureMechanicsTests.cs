using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

namespace Livora.Server.Tests.Identity;

/// <summary>
/// PURPOSE: pin the identity lane's pure decision tables with the IClock seam so window arithmetic
///          — the part of lockout/expiry that MUST NOT be tested by sleeping — is proven exactly:
///          the 401/429/403 ladder at the configured thresholds, the sliding-window reset, the
///          lockout extension under continued hammering, and the success reset. Plus the password
///          hash format's own contract (cost travels with the hash; garbage never authenticates)
///          and the claim mapper's email-verification law.
/// OWNER: Agent 03 (identity lane); written by repair lane R2.
/// CONSUMES: IdentitySecurityStore/IdentityOptions/FixedClock (the lane's own seams), PasswordHasher,
///           GoogleClaimMapper, EmailNormalized — all framework-free or DbContext-free here (the
///           store is used purely: RecordFailure/ResetOnSuccess are documented pure transitions).
/// INVARIANTS: the ladder thresholds the HTTP tests observed come from THESE numbers; a change here
///           must move IdentityAuthLifecycleTests's ladder together (both read IdentityOptions).
/// </summary>
public sealed class IdentityPureMechanicsTests
{
    private static IdentityOptions Options => IdentityOptions.FromConfiguration(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // explicit, tiny, non-default numbers: the test proves the ladder FOLLOWS config
            ["Identity:Lockout:WarnAfterFailures"] = "3",
            ["Identity:Lockout:MaxAttempts"] = "5",
            ["Identity:Lockout:WindowMinutes"] = "15",
            ["Identity:Lockout:LockoutMinutes"] = "10",
        }).Build());

    [Fact]
    public void Lockout_ladder_follows_the_configured_thresholds_exactly()
    {
        var store = new IdentitySecurityStore(() => throw new NotSupportedException("pure path"));
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var profile = new IdentitySecurityProfile { NormalizedEmail = "x@y.test" };
        var o = Options;

        Assert.Equal(3, o.LockoutWarnAfterFailures);
        Assert.Equal(5, o.LockoutMaxAttempts);

        var ladder = Enumerable.Range(1, o.LockoutMaxAttempts + 1)
            .Select(_ => store.RecordFailure(profile, o, now))
            .ToArray();

        Assert.All(ladder[..3], s => Assert.Equal(IdentitySecurityStore.FailureState.Allowed, s));
        Assert.All(ladder[3..5], s => Assert.Equal(IdentitySecurityStore.FailureState.RateLimited, s));
        Assert.Equal(IdentitySecurityStore.FailureState.LockedOut, ladder[5]);
        Assert.NotNull(profile.LockoutUntilUtc);
        Assert.Equal(now.AddMinutes(10), profile.LockoutUntilUtc);
    }

    [Fact]
    public void The_sliding_window_restarts_a_slow_drip_and_a_locked_mailbox_stays_locked()
    {
        var store = new IdentitySecurityStore(() => throw new NotSupportedException("pure path"));
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var profile = new IdentitySecurityProfile { NormalizedEmail = "drip@y.test" };
        var o = Options;

        store.RecordFailure(profile, o, now);
        store.RecordFailure(profile, o, now);
        store.RecordFailure(profile, o, now);                       // 3 — still allowed

        var aged = now.AddMinutes(16);                              // window (15 min) elapsed
        Assert.Equal(IdentitySecurityStore.FailureState.Allowed,
            store.RecordFailure(profile, o, aged));                 // counter restarted at 1
        Assert.Equal(1, profile.FailedAttempts);
        Assert.Equal(aged, profile.WindowStartedAtUtc);

        // hammer through to lockout, then keep hammering: each hit re-extends, never shortens
        for (var i = 0; i < 4; i++) store.RecordFailure(profile, o, aged);   // attempts 2..5
        Assert.Equal(IdentitySecurityStore.FailureState.LockedOut,
            store.RecordFailure(profile, o, aged));                 // attempt 5+1 -> locked
        var first = profile.LockoutUntilUtc!.Value;
        Assert.Equal(IdentitySecurityStore.FailureState.LockedOut,
            store.RecordFailure(profile, o, aged.AddMinutes(9)));   // while locked: re-extend
        Assert.True(profile.LockoutUntilUtc!.Value > first);

        // IsLockedOut agrees with the stored state at both ends of the lock
        Assert.True(IdentitySecurityStore.IsLockedOut(profile, aged));
        Assert.False(IdentitySecurityStore.IsLockedOut(profile, aged.AddMinutes(2 * o.LockoutMinutes + 1)));
        Assert.False(IdentitySecurityStore.IsLockedOut(null, aged)); // missing profile = not locked
    }

    [Fact]
    public void Success_clears_every_piece_of_failure_state()
    {
        var store = new IdentitySecurityStore(() => throw new NotSupportedException("pure path"));
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var profile = new IdentitySecurityProfile { NormalizedEmail = "comeback@y.test" };
        var o = Options;
        for (var i = 0; i < 6; i++) store.RecordFailure(profile, o, now);
        Assert.NotNull(profile.LockoutUntilUtc);

        store.ResetOnSuccess(profile, now);
        Assert.Equal(0, profile.FailedAttempts);
        Assert.Null(profile.WindowStartedAtUtc);
        Assert.Null(profile.LastFailureAtUtc);
        Assert.Null(profile.LockoutUntilUtc);
        Assert.Equal(now, profile.UpdatedAtUtc);
    }

    [Fact]
    public void Password_hash_carries_its_cost_and_only_the_right_password_verifies()
    {
        var hasher = new PasswordHasher();
        const string pw = "а very long passphrase with unicode ✓ ✓";

        var h1 = hasher.Hash(pw);
        var h2 = hasher.Hash(pw);
        Assert.NotEqual(h1, h2);                                    // fresh salt per hash
        Assert.True(hasher.Verify(pw, h1));
        Assert.True(hasher.Verify(pw, h2));
        Assert.False(hasher.Verify(pw + "x", h1));
        Assert.False(hasher.Verify(null, h1));

        // the cost travels WITH the hash: a stored 100k-cost row still verifies, and NeedsRehash
        // flags it for the transparent upgrade the login path performs
        var legacy = hasher.Hash(pw, iterations: 100_000);
        Assert.Equal("100000", legacy.Split('$')[0]);
        Assert.True(hasher.Verify(pw, legacy));
        Assert.True(hasher.NeedsRehash(legacy));
        Assert.False(hasher.NeedsRehash(hasher.Hash(pw)));

        // garbage in the column fails closed — never an exception, never a backdoor
        Assert.False(hasher.Verify(pw, "garbage"));
        Assert.False(hasher.Verify(pw, "100000$not-base64$also-not"));
        Assert.False(hasher.Verify(pw, "10$aaaa$bbbb"));            // cost below the floor
        // an empty column is "needs (re)hash" by definition — the upgrade path, not a backdoor
        Assert.True(hasher.NeedsRehash(""));
    }

    [Fact]
    public void Refresh_tokens_are_high_entropy_one_way_and_hash_stably()
    {
        var a = RefreshTokens.NewToken();
        var b = RefreshTokens.NewToken();
        Assert.NotEqual(a, b);
        Assert.Equal(64, a.Length);                                  // 48 bytes base64url
        Assert.Equal(64, RefreshTokens.Hash(a).Length);              // 64 hex chars
        Assert.Equal(RefreshTokens.Hash(a), RefreshTokens.Hash(a));  // lookup-by-hash works
        Assert.NotEqual(RefreshTokens.Hash(a), RefreshTokens.Hash(b));
        Assert.Equal(new string('0', 64), RefreshTokens.Hash(null)); // probes never throw or collide
        Assert.Equal(new string('0', 64), RefreshTokens.Hash(""));
    }

    [Fact]
    public void Claim_mapper_honours_only_verified_emails_and_never_anchors_without_a_sub()
    {
        var ok = GoogleClaimMapper.Map(new Dictionary<string, string?>
        {
            ["sub"] = " sub-1 ", ["email"] = "Sara@Test.Local", ["email_verified"] = "True",
            ["name"] = "  سارا  ",
        });
        Assert.True(ok.Accepted);
        Assert.Equal("sub-1", ok.Identity!.Subject);                 // trimmed
        Assert.Equal("Sara@Test.Local", ok.Identity!.Email);         // preserved; store normalizes
        Assert.True(ok.Identity!.EmailVerified);
        Assert.Equal("سارا", ok.Identity!.DisplayName);              // trimmed, multilingual kept

        var unverified = GoogleClaimMapper.Map(new Dictionary<string, string?>
        {
            ["sub"] = "s", ["email"] = "victim@someone.test", ["email_verified"] = "false",
        });
        Assert.True(unverified.Accepted);
        Assert.Null(unverified.Identity!.Email);                     // the takeover bridge is closed
        Assert.False(unverified.Identity!.EmailVerified);

        Assert.Equal(GoogleMapFailure.MissingSubject,
            GoogleClaimMapper.Map(new Dictionary<string, string?> { ["email"] = "a@b.cc" }).Failure);
        Assert.Equal(GoogleMapFailure.MissingSubject,
            GoogleClaimMapper.Map(new Dictionary<string, string?> { ["sub"] = "   " }).Failure);
        Assert.Equal(GoogleMapFailure.SubjectTooLong,
            GoogleClaimMapper.Map(new Dictionary<string, string?> { ["sub"] = new string('s', 201) }).Failure);
    }

    [Fact]
    public void Email_normalization_is_the_one_canonical_form_the_unique_index_sees()
    {
        Assert.Equal("sara@test.local", EmailNormalized.Normalize("  Sara@TEST.local \t"));
        Assert.Null(EmailNormalized.Normalize("   "));
        Assert.True(EmailNormalized.LooksValid("sara@test.local"));
        Assert.False(EmailNormalized.LooksValid("sara@test"));        // no dot in domain
        Assert.False(EmailNormalized.LooksValid("sa..ra@test.local")); // consecutive dots
        Assert.False(EmailNormalized.LooksValid("a b@test.local"));
        Assert.False(EmailNormalized.LooksValid(new string('a', 315) + "@test.local")); // > 320
        // case-folding is what makes the two spellings collide on the unique index:
        Assert.Equal(EmailNormalized.Normalize("X@Y.TEST"), EmailNormalized.Normalize("x@y.test"));
    }

    [Fact]
    public void Options_defaults_are_the_10_attempt_ladder_the_http_tests_observed()
    {
        var o = IdentityOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.Equal(5, o.LockoutWarnAfterFailures);
        Assert.Equal(10, o.LockoutMaxAttempts);
        Assert.Equal(30, o.DeletionGraceDays);
        Assert.Equal(15, o.AccessTokenLifetimeMinutes);
        Assert.Equal(30, o.RefreshTokenLifetimeDays);
        Assert.False(o.GoogleConfigured);
        Assert.Empty(o.BootstrapAdminEmails);
        Assert.Equal(IdentityOptions.GoogleDefaultMetadataAddress, o.GoogleMetadataAddress);
    }
}
