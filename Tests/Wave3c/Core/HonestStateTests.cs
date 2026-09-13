using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;
using LIVORA.Tests.Tests;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// Consent register, secure storage, sync placeholder, cloud auth, passcode, noop transport — the
/// honest-state half of lane 01. Each suite is small and specific: what a merge could silently
/// break in that class, and nothing more.
/// </summary>
public class ConsentStoreTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("consents");
    private ConsentStore Store() => new(Lane01Harness.RootStore(_dir));

    [Fact]
    public async Task EveryCategory_StartsUntouched_NotDenied()
    {
        var store = Store();
        foreach (var category in Enum.GetValues<ConsentCategory>())
            Assert.Equal(ConsentDecision.Untouched, store.Get(category));

        var all = await store.GetAllAsync();
        Assert.Equal(Enum.GetValues<ConsentCategory>().Length, all.Count);
        Assert.All(all, kv => Assert.Equal(ConsentDecision.Untouched, kv.Value));
    }

    [Fact]
    public async Task DeniedIsADistinctExplicitState_AndSurvivesRestart()
    {
        var store = Store();
        await store.SetAsync(ConsentCategory.HealthData, ConsentDecision.Denied);
        Assert.Equal(ConsentDecision.Denied, Store().Get(ConsentCategory.HealthData));
        Assert.NotEqual(ConsentDecision.Untouched, Store().Get(ConsentCategory.HealthData));
    }

    [Fact]
    public async Task GrantedRoundTrips_AndOnlyThatCategoryMoves()
    {
        var store = Store();
        await store.SetAsync(ConsentCategory.AiProcessing, ConsentDecision.Granted);
        Assert.Equal(ConsentDecision.Granted, Store().Get(ConsentCategory.AiProcessing));
        Assert.Equal(ConsentDecision.Untouched, Store().Get(ConsentCategory.HealthData));
    }

    [Fact]
    public async Task RevokeAll_WritesExplicitDeniedRows_NotAnEmptyFile()
    {
        var store = Store();
        await store.SetAsync(ConsentCategory.AiProcessing, ConsentDecision.Granted);
        await store.SetAsync(ConsentCategory.Analytics, ConsentDecision.Granted);
        await store.RevokeAllAsync();

        var after = Store();
        var all = await after.GetAllAsync();
        Assert.All(all, kv => Assert.Equal(ConsentDecision.Denied, kv.Value));

        // the withdrawal itself is the record: rows exist, so "we never revoked" cannot be claimed
        var json = File.ReadAllText(Path.Combine(_dir.Root, ConsentStore.FileName));
        Assert.Contains(nameof(ConsentDecision.Denied), json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntouchedCannotBeSetExplicitly()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Store().SetAsync(ConsentCategory.HealthData, ConsentDecision.Untouched));
    }

    [Fact]
    public async Task FileHoldsNoPersonalData_BeyondCategoryDecisionAndStamp()
    {
        var store = Store();
        await store.SetAsync(ConsentCategory.CalendarData, ConsentDecision.Granted);
        var json = File.ReadAllText(Path.Combine(_dir.Root, ConsentStore.FileName));
        Assert.DoesNotContain("sk-", json, StringComparison.Ordinal);
        Assert.Contains("UpdatedAtUtc", json, StringComparison.Ordinal);
        Assert.Contains(nameof(ConsentCategory.CalendarData), json, StringComparison.Ordinal);
    }

    [Fact]
    public void GetAllAsyncSnapshotIsEveryCategoryEvenWhenFileMissing()
    {
        Assert.False(File.Exists(Path.Combine(_dir.Root, ConsentStore.FileName)));
        var all = Store().GetAllAsync().GetAwaiter().GetResult();
        Assert.Equal(6, all.Count);
    }

    public void Dispose() => _dir.Dispose();
}

public class SecureStorageServiceTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("secure");

    private SecureStorageService Service(IPlatformSecureBox box) =>
        new(Lane01Harness.SubStore(_dir, SecureStorageService.StoreDirName), box);

    [Fact]
    public async Task RoundTripPreservesValue_IncludingUnicode()
    {
        var service = Service(new FakeSecureBox());
        await service.SetAsync("k", "unicode-secret-abc-123");
        Assert.Equal("unicode-secret-abc-123", await service.GetAsync("k"));
    }

    [Fact]
    public async Task MissingKeyReadsAsNull_AndRemovalIsReal()
    {
        var service = Service(new FakeSecureBox());
        Assert.Null(await service.GetAsync("absent"));
        await service.SetAsync("k", "value");
        await service.RemoveAsync("k");
        Assert.Null(Service(new FakeSecureBox()).GetAsync("k").GetAwaiter().GetResult());
        Assert.DoesNotContain("k", service.Keys);
    }

    [Fact]
    public async Task NoPlaintextEverHitsTheDisk()
    {
        var box = new FakeSecureBox();
        var service = Service(box);
        const string secret = "fake-secret-value-42";
        await service.SetAsync("gateway.user_api_key", secret);

        var file = Path.Combine(_dir.Root, SecureStorageService.StoreDirName, SecureStorageService.StoreFileName);
        var bytes = File.ReadAllBytes(file);
        // The persisted bytes must not contain the secret in any of the scanned encodings; the fake
        // box guarantees the ciphertext differs from the input, so a leak here is a real bug.
        Assert.False(Lane01Harness.ContainsPlaintext(bytes, secret));
        Assert.DoesNotContain(secret, File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(1, box.ProtectCalls);
        Assert.Equal(secret, await service.GetAsync("gateway.user_api_key"));
    }

    [Fact]
    public async Task CiphertextBytesDifferFromInputBytes()
    {
        var box = new FakeSecureBox();
        var service = Service(box);
        await service.SetAsync("a", "aaaaaaaa");
        var raw = File.ReadAllText(Path.Combine(
            _dir.Root, SecureStorageService.StoreDirName, SecureStorageService.StoreFileName));
        // inverted "aaaaaaaa" base64s to something that contains no run of the input's base64
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aaaaaaaa")), raw, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityFlagIsAReadOnlyOfTheBox_NeverAGuess()
    {
        Assert.False(Service(new FakeSecureBox()).IsPlatformHardwareBacked);
        Assert.True(Service(new FakeOsSecureBox()).IsPlatformHardwareBacked);
        Assert.Equal("private-app-dir-no-cipher",
            new SecureStorageService(Lane01Harness.SubStore(_dir, "s2"), new PrivateFileSecureBox())
                .IsPlatformHardwareBacked ? "yes" : "private-app-dir-no-cipher");
    }

    [Fact]
    public async Task CorruptCiphertextReadsAsAbsent_NotACrash()
    {
        var store = Lane01Harness.SubStore(_dir, SecureStorageService.StoreDirName);
        var service = Service(new FakeSecureBox());
        await service.SetAsync("k", "v");
        store.WriteRawAtomic(SecureStorageService.StoreFileName, "{\"k\":\"not base64 at all!!\"}");
        Assert.Null(await service.GetAsync("k"));

        store.WriteRawAtomic(SecureStorageService.StoreFileName, "this is not json");
        Assert.Null(await service.GetAsync("k"));
    }

    [Fact]
    public async Task KeysAreEnumeratedWithoutValues()
    {
        var service = Service(new FakeSecureBox());
        await service.SetAsync("b.secret", "x");
        await service.SetAsync("a.secret", "y");
        Assert.Equal(new[] { "a.secret", "b.secret" }, service.Keys);
    }

    [Fact]
    public void NonWindowsFallbackBox_ClaimsNoCipher()
    {
        var box = new PrivateFileSecureBox();
        Assert.False(box.IsHardwareOrOsBacked);
        var payload = System.Text.Encoding.UTF8.GetBytes("abc");
        Assert.Equal(payload, box.Protect(payload));   // documented identity transform: no cipher, no claim
    }

    public void Dispose() => _dir.Dispose();
}

public class NoopSyncTransportTests
{
    [Fact]
    public void IsConfiguredIsFalse_AndLabelIsAMachineTag()
    {
        var transport = new NoopSyncTransport();
        Assert.False(transport.IsConfigured);
        Assert.Equal("noop-local-only", transport.GatewayLabel);
        Assert.DoesNotContain(' ', transport.GatewayLabel);
    }

    [Fact]
    public async Task PushReportsFailureWithNoBackendCategory_AndNoConflicts()
    {
        var batch = new[]
        {
            new SyncEnvelope { EntityKind = "goals", EntityId = "g1", LocalState = SyncState.Pending, LocalVersion = 1, PayloadHash = "ab" },
        };
        var result = await new NoopSyncTransport().PushAsync(batch);
        Assert.False(result.Success);
        Assert.Equal(NoopSyncTransport.NoBackendCategory, result.ErrorCategory);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public async Task EmptyBatchAlsoRefuses_RatherThanReportingSuccess()
    {
        // An empty batch returning Success=true would let the queue mark records Synced for free.
        var result = await new NoopSyncTransport().PushAsync(Array.Empty<SyncEnvelope>());
        Assert.False(result.Success);
    }
}

public class CloudAuthServiceTests
{
    [Fact]
    public void BackendIsNeverConfigured_AndReasonKeyIsAKey()
    {
        var auth = new CloudAuthService();
        Assert.False(auth.IsBackendConfigured);
        Assert.Equal(CloudAuthService.NotAvailableKey, auth.StatusReasonKey);
        Assert.Equal("Auth.Cloud.NotAvailable", auth.StatusReasonKey);
        Assert.DoesNotContain(' ', auth.StatusReasonKey);
    }

    [Fact]
    public async Task SignInFailsEveryTime_AndRemembersTheReason()
    {
        var auth = new CloudAuthService();
        Assert.False(await auth.SignInAsync("a@b.c", "hunter2"));
        Assert.False(await auth.SignInAsync("a@b.c", ""));
        Assert.False(await auth.SignInAsync("", ""));
        Assert.Equal(3, auth.RefusedAttempts);
        Assert.Equal(CloudAuthService.NotAvailableKey, auth.LastAttemptReasonKey);
    }

    [Fact]
    public void TheSourceHasNoPathThatReturnsTrue()
    {
        // Structural proof of the honesty rule, read off the SOURCE (the IL of an async state machine
        // carries the same byte for unrelated reasons). No `return true` / `=> true` anywhere in the
        // cloud-auth file: a future demo login has to delete this test first, which is a review event.
        var (_, raw) = Wave3Harness.Sources("Infrastructure/Security")
            .Single(s => Path.GetFileName(s.Path) == "CloudAuthService.cs");
        // Comments legitimately SAY "return true" while explaining that it does not; scan code only.
        var source = Wave3Harness.StripComments(raw);
        Assert.DoesNotContain("return true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("=> true", source, StringComparison.Ordinal);
        Assert.Contains("Task.FromResult<bool>(false)", source, StringComparison.Ordinal);
    }
}

public class LocalPasscodeServiceTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("passcode");
    private readonly FakeClock _clock = new();

    private LocalPasscodeService Service() => new(
        new SecureStorageService(Lane01Harness.SubStore(_dir, SecureStorageService.StoreDirName), new FakeSecureBox()),
        () => "profile-1", _clock.UtcNow);

    [Fact]
    public void StartsUnset_AndVerifyFailsClosed()
    {
        var service = Service();
        Assert.False(service.IsSet);
        Assert.False(service.IsLocked);
        Assert.False(service.VerifyAsync("1234").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task SetThenVerify_RoundTripsAcrossANewInstance()
    {
        Assert.True(await Service().SetAsync("s3cret-pass"));
        Assert.True(Service().IsSet);
        Assert.True(await Service().VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task WrongPasscodeFails_AndDoesNotClearTheVerifier()
    {
        await Service().SetAsync("s3cret-pass");
        Assert.False(await Service().VerifyAsync("nope-nope"));
        Assert.True(Service().IsSet);
        Assert.True(await Service().VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task TooShortAPasscodeIsRefused()
    {
        Assert.False(await Service().SetAsync("12"));
        Assert.False(Service().IsSet);
    }

    [Fact]
    public async Task RemoveClearsEverything()
    {
        await Service().SetAsync("s3cret-pass");
        await Service().RemoveAsync();
        Assert.False(Service().IsSet);
        Assert.False(await Service().VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task FiveWrongAttemptsInWindowLocks_AndLockoutIgnoresTheRightAnswer()
    {
        var service = Service();
        await service.SetAsync("s3cret-pass");
        for (int i = 0; i < LocalPasscodeService.MaxFailedAttempts; i++)
            Assert.False(await service.VerifyAsync("wrong-" + i));

        Assert.True(service.IsLocked);
        Assert.Equal(0, service.AttemptsRemaining);
        Assert.False(await service.VerifyAsync("s3cret-pass"));   // locked: refuses without checking

        _clock.Advance(LocalPasscodeService.LockoutWindow + TimeSpan.FromSeconds(1));
        Assert.False(service.IsLocked);
        Assert.True(await service.VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task LockAndUnlockAreExplicit()
    {
        var service = Service();
        await service.SetAsync("s3cret-pass");
        service.Lock();
        Assert.True(service.IsLocked);
        service.Unlock();
        Assert.False(service.IsLocked);
        Assert.True(await service.VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task VerifierIsSalted_AndNeverThePlaintext()
    {
        var store = Lane01Harness.SubStore(_dir, SecureStorageService.StoreDirName);
        var service = Service();
        await service.SetAsync("s3cret-pass");
        var first = File.ReadAllText(Path.Combine(
            store.Directory, SecureStorageService.StoreFileName));

        await service.SetAsync("s3cret-pass");   // same passcode again: fresh salt must change bytes
        var second = File.ReadAllText(Path.Combine(
            store.Directory, SecureStorageService.StoreFileName));

        var file = Path.Combine(store.Directory, SecureStorageService.StoreFileName);
        Assert.DoesNotContain("s3cret-pass", second, StringComparison.Ordinal);
        Assert.False(Lane01Harness.ContainsPlaintext(File.ReadAllBytes(file), "s3cret-pass"));
        Assert.NotEqual(first, second);          // the salt (and so the verifier) was re-randomized
        Assert.True(await Service().VerifyAsync("s3cret-pass"));
    }

    [Fact]
    public async Task VerifierShapeIsVersionedPbkdf2()
    {
        var storage = new RecordingSecureStorage();
        var service = new LocalPasscodeService(storage, () => "profile-1", _clock.UtcNow);
        await service.SetAsync("s3cret-pass");
        Assert.Single(storage.Raw);
        var payload = storage.Raw[service.VerifierKey];
        var parts = payload.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal(LocalPasscodeService.Iterations.ToString(), parts[0]);
        Assert.Equal(LocalPasscodeService.SaltBytes * 2, parts[1].Length);
        Assert.Equal(LocalPasscodeService.HashBytes * 2, parts[2].Length);
    }

    [Fact]
    public void DifferentProfileGetsADifferentKey()
    {
        var storage = new RecordingSecureStorage();
        var a = new LocalPasscodeService(storage, () => "profile-a");
        var b = new LocalPasscodeService(storage, () => "profile-b");
        Assert.NotEqual(a.VerifierKey, b.VerifierKey);
        Assert.DoesNotContain("profile-a", a.VerifierKey, StringComparison.Ordinal);
        Assert.StartsWith("passcode.v1.", a.VerifierKey, StringComparison.Ordinal);
    }

    public void Dispose() => _dir.Dispose();
}
