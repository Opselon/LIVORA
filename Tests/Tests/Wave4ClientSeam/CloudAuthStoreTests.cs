using System.Text.RegularExpressions;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Cloud;
using LIVORA.Infrastructure.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// The token store + session manager: where a cloud credential may and may not live, and the two
/// rules that decide whether the product tells the truth about being signed in — rotation replaces
/// the whole blob (a rotated-out refresh token must not survive on disk), and sign-out clears local
/// state only after the server answered.
/// </summary>
public class CloudAuthStoreTests
{
    // ============================ at-rest shape ==============================================

    [Fact]
    public async Task Tokens_ReachTheDiskOnlyThroughThePlatformBox_AndReadBackIntact()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-atrest");
        var box = new Wave4SeamHarness.OsBackedBox();
        var secure = Wave4SeamHarness.SecureStore(dir, box);
        var store = new CloudTokenStore(secure, () => secure.IsPlatformHardwareBacked);   // the DI wiring

        var session = Session("at-secret-value", "rt-secret-value");
        await store.WriteAsync(session);

        var read = await store.ReadAsync();
        Assert.NotNull(read);
        Assert.Equal("at-secret-value", read!.AccessToken);
        Assert.Equal("rt-secret-value", read.RefreshToken);
        Assert.Equal("u-1", read.UserId);
        Assert.True(store.IsOsBacked);   // the box says so, the store only relays it

        // The protected value is the ONLY thing on disk: no plaintext token in the file.
        var file = Path.Combine(dir.PathOf(SecureStorageService.StoreDirName), SecureStorageService.StoreFileName);
        var bytes = File.ReadAllBytes(file);
        Assert.DoesNotContain("at-secret-value", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("rt-secret-value", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsOsBacked_FollowsTheBox_NeverTheAspiration()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-osbacked");
        using var dir2 = new Wave4SeamHarness.TempDir("tokens-osbacked2");

        var plainStore = Wave4SeamHarness.SecureStore(dir2, new Wave4SeamHarness.NoCipherBox());
        var plain = new CloudTokenStore(plainStore, () => plainStore.IsPlatformHardwareBacked);
        Assert.False(plain.IsOsBacked);   // the no-cipher box must never let the UI say "encrypted"

        var backedStore = Wave4SeamHarness.SecureStore(dir, new Wave4SeamHarness.OsBackedBox());
        var backed = new CloudTokenStore(backedStore, () => backedStore.IsPlatformHardwareBacked);
        Assert.True(backed.IsOsBacked);
    }

    [Fact]
    public async Task IsOsBacked_FalseWhenTheProbeThrows()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-probe");
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir),
            osBacked: () => throw new IOException("box went away"));
        Assert.False(store.IsOsBacked);   // "unproven" collapses to false, never up
    }

    [Fact]
    public async Task Write_RejectsASessionThatCouldNotBeUsed()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-shape");
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteAsync(new CloudSession
            {
                UserId = "u", SessionId = "s", AccessToken = "", RefreshToken = "r",
                ExpiresAtUtc = DateTimeOffset.UtcNow,
            }));
        Assert.Null(await store.ReadAsync());   // nothing half-written survived the rejection
    }

    [Fact]
    public async Task CorruptOrForeignCiphertext_ReadsAsNoSession_AndIsErased()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-corrupt");
        var mem = new Wave4SeamHarness.MemoryStorage();
        var store = new CloudTokenStore(mem);

        mem.Raw[CloudTokenStore.SessionStorageKey] = "{ this is not a session }";
        Assert.Null(await store.ReadAsync());
        Assert.False(mem.Raw.ContainsKey(CloudTokenStore.SessionStorageKey));   // repaired, not retried forever

        mem.Raw[CloudTokenStore.SessionStorageKey] = "null";   // parses, but is not a session
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task Write_OverEntiresTheWholeSession_RotationCannotLeaveTheOldRefreshTokenBehind()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-rotate");
        var mem = new Wave4SeamHarness.MemoryStorage();
        var store = new CloudTokenStore(mem);

        await store.WriteAsync(Session("at-1", "rt-OLD"));
        await store.WriteAsync(Session("at-2", "rt-NEW"));

        var read = await store.ReadAsync();
        Assert.Equal("rt-NEW", read!.RefreshToken);
        Assert.Single(mem.Raw);   // one key, one blob — no second copy of a live credential
        Assert.DoesNotContain("rt-OLD", mem.Raw[CloudTokenStore.SessionStorageKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clear_ErasesTheSessionAndTheLegacyKey()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-clear");
        var mem = new Wave4SeamHarness.MemoryStorage();
        mem.Raw[CloudTokenStore.SessionStorageKey] = "junk";
        mem.Raw[CloudTokenStore.LegacySessionStorageKey] = "junk";
        var store = new CloudTokenStore(mem);

        await store.ClearAsync();
        Assert.False(mem.Raw.ContainsKey(CloudTokenStore.SessionStorageKey));
        Assert.False(mem.Raw.ContainsKey(CloudTokenStore.LegacySessionStorageKey));
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task DescribeKeys_ReportsPresenceOnly_NeverAValue()
    {
        using var dir = new Wave4SeamHarness.TempDir("tokens-describe");
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        Assert.Empty(await store.DescribeKeysAsync());

        await store.WriteAsync(Session("at-x", "rt-x"));
        var described = await store.DescribeKeysAsync();
        Assert.Equal(2, described.Count);
        Assert.All(described, d => Assert.DoesNotContain(d, "at-x", StringComparison.Ordinal));
        Assert.DoesNotContain(string.Join(",", described), "rt-x", StringComparison.Ordinal);
    }

    // ============================ expiry =========================================================

    [Fact]
    public void AccessExpiry_HonoursTheSafetyMargin()
    {
        var now = new DateTimeOffset(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        var expiring = Session(expires: now.AddSeconds(30));
        Assert.True(expiring.IsAccessExpired(now, TimeSpan.FromSeconds(60)));    // inside the margin = expired
        Assert.False(expiring.IsAccessExpired(now, TimeSpan.FromSeconds(10)));   // outside it = still good
    }

    [Fact]
    public void Fingerprint_IsOneWay_AndStable()
    {
        var a = Session(refresh: "rt-abc");
        var b = Session(refresh: "rt-abc");
        var c = Session(refresh: "rt-def");
        Assert.Equal(a.RefreshTokenFingerprint, b.RefreshTokenFingerprint);
        Assert.NotEqual(a.RefreshTokenFingerprint, c.RefreshTokenFingerprint);
        Assert.DoesNotContain("rt-abc", a.RefreshTokenFingerprint, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{12}$", a.RefreshTokenFingerprint);
    }

    // ============================ session manager ===============================================

    [Fact]
    public async Task SignIn_StoresTheSessionOnlyFromARealResponse()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-signin");
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.Json("POST", "/api/v1/auth/login", 200, Wave4SeamHarness.TokensBody(access: "at-live", refresh: "rt-live"));
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);

        var outcome = await auth.SignInWithPasswordAsync("a@b.test", "pw");
        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Session);
        var stored = await store.ReadAsync();
        Assert.Equal("rt-live", stored!.RefreshToken);          // what the server actually returned
        Assert.True(auth.HasSession);
        Assert.Equal("at-live", await auth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task AFailedSignIn_StoresNothing_AndCarriesTheServerCode()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-fail");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/login", 403, LivoraApiCodes.AccountLocked);
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);

        var outcome = await auth.SignInWithPasswordAsync("a@b.test", "pw");
        Assert.False(outcome.Succeeded);
        Assert.Equal(LivoraApiCodes.AccountLocked, outcome.Code);
        Assert.Equal("Api.Error.account_locked", outcome.ReasonKey);
        Assert.Null(outcome.Session);
        Assert.Null(await store.ReadAsync());                   // nothing was written
        Assert.False(auth.HasSession);
    }

    [Fact]
    public async Task Google_SignInSurfacesProviderUnconfigured_AsItself_NotAsSuccess()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-google");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/google", 503, LivoraApiCodes.ProviderUnconfigured);
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);

        var outcome = await auth.SignInWithGoogleAsync("idtok");
        Assert.False(outcome.Succeeded);
        Assert.Equal(LivoraApiCodes.ProviderUnconfigured, outcome.Code);
        Assert.Equal("Api.Error.provider_unconfigured", outcome.ReasonKey);   // "server has no client_id yet"
        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task Concurrent401s_ProduceExactlyOneRefresh_RotationCannotRaceIntoATheftSignal()
    {
        // §5c: a rotated-out refresh token replayed revokes the whole family and is logged as theft.
        // A racing client could do that to its own user, so the gate is load-bearing, not cosmetic.
        using var dir = new Wave4SeamHarness.TempDir("auth-race");
        var store = new Wave4SeamHarness.MemoryStorage();
        var tokens = new CloudTokenStore(store);
        await tokens.WriteAsync(Session(access: "at-old", refresh: "rt-old",
            expires: DateTimeOffset.UtcNow.AddMinutes(-5)));   // expired: refresh is legitimately needed

        var h = new Wave4SeamHarness.ScriptedHandler();
        h.Json("POST", "/api/v1/auth/refresh", 200, Wave4SeamHarness.TokensBody(access: "at-new", refresh: "rt-new",
            expires: DateTimeOffset.UtcNow.AddMinutes(15)));
        h.Json("GET", "/api/v1/account", 200,
            """{"userId":"u","email":null,"displayName":null,"locale":null,"tier":null,"status":null,"createdAtUtc":null,"lastLoginAtUtc":null}""");
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, tokens, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();

        var tasks = Enumerable.Range(0, 6).Select(_ => auth.TryRefreshAsync());
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r));

        Assert.Equal(1, h.CallCount("POST", "/auth/refresh"));
        Assert.Contains("rt-new", store.Raw[CloudTokenStore.SessionStorageKey], StringComparison.Ordinal);
        Assert.DoesNotContain("rt-old", store.Raw[CloudTokenStore.SessionStorageKey], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefreshThatSaysTokenRevoked_DropsTheLocalSession()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-revoked");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .Problem("POST", "/api/v1/auth/refresh", 401, LivoraApiCodes.TokenRevoked);
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await store.WriteAsync(Session(expires: DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();

        Assert.False(await auth.TryRefreshAsync());
        Assert.Null(await store.ReadAsync());        // the family is gone server-side; the copy is gone here
        Assert.False(auth.HasSession);
        Assert.Null(await auth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task ARefreshThatFailedOnTheNetwork_KeepsTheSession()
    {
        // Deleting a live session because a packet was lost would sign the user out for nothing.
        using var dir = new Wave4SeamHarness.TempDir("auth-offline-refresh");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/auth/refresh", _ => throw new System.Net.Http.HttpRequestException("gone"));
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await store.WriteAsync(Session(expires: DateTimeOffset.UtcNow.AddMinutes(-5)));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();

        Assert.False(await auth.TryRefreshAsync());
        Assert.NotNull(await store.ReadAsync());     // still here, still retryable next launch
        Assert.True(auth.HasSession);
    }

    [Fact]
    public async Task SignOut_ClearsLocalStateOnlyAfterTheServerAnswered204()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-signout");
        var h = new Wave4SeamHarness.ScriptedHandler();
        h.On("POST", "/api/v1/auth/logout", _ => Wave4SeamHarness.NoContent());
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await store.WriteAsync(Session());
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();

        var res = await auth.SignOutAsync();
        Assert.True(res.Ok);
        Assert.Null(await store.ReadAsync());
        Assert.False(auth.HasSession);
    }

    [Fact]
    public async Task OfflineSignOut_KeepsTheSession_AndExplainsItself()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-signout-offline");
        var h = new Wave4SeamHarness.ScriptedHandler()
            .On("POST", "/api/v1/auth/logout", _ => throw new System.Net.Http.HttpRequestException("no route"));
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        await store.WriteAsync(Session());
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);
        await auth.RestoreAsync();

        var res = await auth.SignOutAsync();
        Assert.False(res.Ok);
        Assert.Equal(LivoraApiTransportCodes.Network, res.Code);
        Assert.Equal("Api.Error.network", auth.LastSignOutReasonKey);
        Assert.NotNull(await store.ReadAsync());     // server-side session still exists; so does ours

        // The explicit escape hatch: forget locally, and keep the reason visible.
        var forced = await auth.SignOutAsync(forceLocalSignOut: true);
        Assert.False(forced.Ok);
        Assert.Null(await store.ReadAsync());
        Assert.Equal("Cloud.Auth.SignOut.ForcedLocally", auth.LastSignOutReasonKey);
    }

    [Fact]
    public async Task SignOutWithoutASession_IsNotAFailure()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-signout-none");
        var h = new Wave4SeamHarness.ScriptedHandler();
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir));
        using var port = new LivoraApiPort(Wave4SeamHarness.Options(dir), h, null, NullLogger<LivoraApiPort>.Instance);
        var auth = new CloudSessionManager(port, store, NullLogger<CloudSessionManager>.Instance);

        Assert.True((await auth.SignOutAsync()).Ok);
        Assert.Equal(0, h.CallCount());   // nothing to revoke, so no request was made
    }

    // ============================ secrets never escape (privacy law §0.5) ======================

    [Fact]
    public async Task NoCloudLayerWritesATokenIntoAFileOutsideSecureStorage()
    {
        using var dir = new Wave4SeamHarness.TempDir("auth-noleak");
        var options = Wave4SeamHarness.Options(dir);
        var state = Wave4SeamHarness.StateStore(dir);
        var store = new CloudTokenStore(Wave4SeamHarness.SecureStore(dir, new Wave4SeamHarness.NoCipherBox()));
        await store.WriteAsync(Session(access: "at-TOKENSCAN", refresh: "rt-TOKENSCAN"));
        options.TrySetBaseUrl("https://api.livora.test");
        state.SaveCursor(11);
        state.RecordAttempt(succeeded: true, code: null, reasonKey: null, correlationId: "cid", httpStatus: 200);

        foreach (var file in Directory.EnumerateFiles(dir.Root, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            bool secure = Path.GetFileName(Path.GetDirectoryName(file)!) == SecureStorageService.StoreDirName;
            if (!secure)
            {
                Assert.DoesNotContain("at-TOKENSCAN", text, StringComparison.Ordinal);
                Assert.DoesNotContain("rt-TOKENSCAN", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void TokenStoreCode_HasNoLogger_AndNoNetworkType()
    {
        var text = Wave4SeamHarness.ReadRepoFile("Infrastructure/Cloud/CloudTokenStore.cs");
        if (text is null) return;
        Assert.DoesNotContain("ILogger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Write", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionManager_LogsIdentifiersOnly()
    {
        var text = Wave4SeamHarness.ReadRepoFile("Infrastructure/Cloud/CloudSessionManager.cs");
        if (text is null) return;
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(text, @"_log\.Log\w+\(([^;]*);"))
        {
            // The fingerprint is a hash, not a credential: mask the word before scanning.
            var args = m.Groups[1].Value.Replace("RefreshTokenFingerprint", "FINGERPRINT");
            Assert.DoesNotContain("AccessToken", args, StringComparison.Ordinal);
            Assert.DoesNotContain("RefreshToken", args, StringComparison.Ordinal);
            Assert.DoesNotContain("email", args, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", args, StringComparison.OrdinalIgnoreCase);
        }
        // The fingerprint is the only token-derived value allowed in a log line.
        Assert.Contains("RefreshTokenFingerprint", text, StringComparison.Ordinal);
    }

    private static CloudSession Session(string? access = null, string? refresh = null, DateTimeOffset? expires = null) =>
        new()
        {
            UserId = "u-1",
            SessionId = "s-1",
            AccessToken = access ?? "at-1",
            RefreshToken = refresh ?? "rt-1",
            ExpiresAtUtc = expires ?? DateTimeOffset.UtcNow.AddMinutes(15),
        };
}
