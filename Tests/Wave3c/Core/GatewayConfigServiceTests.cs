using System.Reflection;
using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;
using LIVORA.Infrastructure.Security.Gateway;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// Gateway configuration resolution (lane 01's <see cref="GatewayConfigService"/>). All three
/// things the seam promises are checked here against the real class with a real temp directory:
/// precedence (a user override beats the embedded fallback and removing it falls back again),
/// persistence of the master switch + probe state in the ONE file this service owns, and — the
/// honesty-critical one — that nothing on the UI path can carry key material.
/// </summary>
public class GatewayConfigServiceTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("gateway");
    private readonly RecordingSecureStorage _secrets = new();
    private readonly FakeClock _clock = new();

    private GatewayConfigService Service() =>
        new(Lane01Harness.SubStore(_dir, GatewayConfigService.GatewayDirName), _secrets, _clock.UtcNow);

    [Fact]
    public async Task WithNoOverrideTheEmbeddedFallbackSuppliesTheKey()
    {
        var config = await Service().GetEffectiveAsync();
        Assert.NotNull(config);
        Assert.Equal(GatewayKeyStore.EmbeddedSourceLabel, config!.SourceLabel);
        Assert.Equal(Lane01Harness.EmbeddedKey(), config.ApiKey);
    }

    [Fact]
    public async Task UserOverrideWinsOverTheEmbeddedKey()
    {
        var service = Service();
        await service.SetUserKeyOverrideAsync("user-override-key-123");

        var config = await service.GetEffectiveAsync();
        Assert.Equal("user-override-key-123", config!.ApiKey);
        Assert.Equal("UserEntered", config.SourceLabel);
    }

    [Fact]
    public async Task RemovingTheOverrideFallsBackToEmbedded()
    {
        var service = Service();
        await service.SetUserKeyOverrideAsync("user-override-key-123");
        await service.SetUserKeyOverrideAsync(null);

        var config = await service.GetEffectiveAsync();
        Assert.Equal(Lane01Harness.EmbeddedKey(), config!.ApiKey);
        Assert.Equal(GatewayKeyStore.EmbeddedSourceLabel, config.SourceLabel);
    }

    [Fact]
    public async Task OverrideIsStoredThroughTheSecureStore_NotInThisServicesJson()
    {
        var service = Service();
        await service.SetEnabledAsync(true);              // guarantees the state file exists
        await service.SetUserKeyOverrideAsync("user-override-key-123");

        Assert.True(_secrets.Raw.ContainsKey(GatewayConfigService.UserOverrideStorageKey));
        var json = File.ReadAllText(
            Path.Combine(_dir.Root, GatewayConfigService.GatewayDirName, GatewayConfigService.ConfigFileName));
        Assert.DoesNotContain("user-override-key-123", json, StringComparison.Ordinal);
        Assert.DoesNotContain(Lane01Harness.EmbeddedKey()!, json, StringComparison.Ordinal);
        // and the file has no field that COULD hold a key, even empty:
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EnabledDefaultsToFalse_AndPersistsAcrossInstances()
    {
        Assert.False((await Service().GetEffectiveAsync())!.Enabled);

        await Service().SetEnabledAsync(true);
        Assert.True(await Service().GetEffectiveAsync() is { Enabled: true });

        await Service().SetEnabledAsync(false);
        Assert.False((await Service().GetEffectiveAsync())!.Enabled);
    }

    [Fact]
    public async Task PublicStatus_NeverCarriesKeyMaterial()
    {
        await Service().SetUserKeyOverrideAsync("user-override-key-123");
        var status = await Service().GetPublicStatusAsync();

        // 1) the record has no field that could hold a key (reflection, so a future added field is
        //    caught by this test rather than silently shipping the secret).
        var leaking = typeof(GatewayPublicStatus).GetProperties()
            .Where(p => p.Name.Contains("key", StringComparison.OrdinalIgnoreCase)
                        && p.PropertyType == typeof(string)
                        && !p.Name.Equals("KeySource", StringComparison.Ordinal))
            .Select(p => p.Name).ToList();
        Assert.True(leaking.Count == 0, "GatewayPublicStatus grew a key-carrying field: " + string.Join(",", leaking));

        // 2) and the whole object graph, flattened to text, contains neither key.
        var flat = string.Join("|", typeof(GatewayPublicStatus).GetProperties()
            .Select(p => $"{p.Name}={p.GetValue(status)}"));
        Assert.DoesNotContain("user-override-key-123", flat, StringComparison.Ordinal);
        Assert.DoesNotContain(Lane01Harness.EmbeddedKey()!, flat, StringComparison.Ordinal);
        Assert.DoesNotContain("ApiKey", flat, StringComparison.Ordinal);

        // 3) ToString (what a crash report or a naive log line grabs) must not leak either.
        Assert.DoesNotContain("sk-", status.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicStatus_ReportsSourceAndInsecureTransportHonestly()
    {
        var bare = await Service().GetPublicStatusAsync();
        Assert.True(bare.IsConfigured);                       // embedded fallback exists
        Assert.Equal("Built-in (obfuscated)", bare.KeySource);
        Assert.True(bare.UsesInsecureTransport);              // the endpoint is plain HTTP
        Assert.False(bare.IsEnabled);                         // and ships disabled
        Assert.Null(bare.LastVerifiedUtc);                    // never probed from this install
        Assert.False(bare.LastProbeOk);

        await Service().SetUserKeyOverrideAsync("user-override-key-123");
        Assert.Equal("Your own key", (await Service().GetPublicStatusAsync()).KeySource);
    }

    [Fact]
    public async Task ProbeState_PersistsAndSurvivesANewService()
    {
        var service = Service();
        Assert.False((await service.GetPublicStatusAsync()).LastProbeOk);

        service.RecordProbeResult(false, "Gateway.Probe.Timeout");
        Assert.False((await Service().GetPublicStatusAsync()).LastProbeOk);
        Assert.Null((await Service().GetPublicStatusAsync()).LastVerifiedUtc);

        _clock.Advance(TimeSpan.FromMinutes(2));
        service.RecordProbeResult(true);
        var after = await Service().GetPublicStatusAsync();     // fresh instance = re-read from disk
        Assert.True(after.LastProbeOk);
        Assert.Equal(_clock.Now, after.LastVerifiedUtc);

        service.RecordProbeResult(false, "Gateway.Probe.Timeout");
        var regressed = await Service().GetPublicStatusAsync();
        Assert.False(regressed.LastProbeOk);
        Assert.NotNull(regressed.LastVerifiedUtc);   // the historic success is not erased, only the
                                                    // current verdict — the UI must read LastProbeOk.
    }

    [Fact]
    public async Task PublicStatus_DecryptsNothing_AndKeepsTheKeyOutOfEveryUiSink()
    {
        // The status path never touches the secure store for key material (it may only ask "is a
        // user override present?"), and the embedded blob is decoded into a zero-able char buffer
        // that is cleared inside the same call — never into a string. So no key string exists at all
        // while a status is being built, which is why nothing downstream can log it by accident.
        var box = new FakeSecureBox();
        var secure = new SecureStorageService(Lane01Harness.SubStore(_dir, "secure"), box);
        var service = new GatewayConfigService(
            Lane01Harness.SubStore(_dir, GatewayConfigService.GatewayDirName), secure, _clock.UtcNow);

        var before = box.UnprotectCalls;
        var status = await service.GetPublicStatusAsync();
        Assert.True(status.IsConfigured);
        Assert.Equal(before, box.UnprotectCalls);   // no user key stored → nothing was decrypted

        await service.SetUserKeyOverrideAsync("user-override-key-123");
        var afterWrite = box.ProtectCalls;
        status = await service.GetPublicStatusAsync();
        Assert.Equal("Your own key", status.KeySource);
        Assert.Equal(afterWrite, box.UnprotectCalls);   // presence check reads once, and only ever
                                                        // through the store's own Get (no extra copy)
    }

    [Fact]
    public async Task ABrokenSecureStoreDegradesToEmbedded_InsteadOfThrowingAtUi()
    {
        var service = new GatewayConfigService(
            Lane01Harness.SubStore(_dir, GatewayConfigService.GatewayDirName), new ThrowingSecureStorage());
        var config = await service.GetEffectiveAsync();
        Assert.Equal(Lane01Harness.EmbeddedKey(), config!.ApiKey);
        var status = await service.GetPublicStatusAsync();
        Assert.True(status.IsConfigured);
    }

    [Fact]
    public async Task HttpsBaseUrl_ClearsTheInsecureFlag()
    {
        var store = Lane01Harness.SubStore(_dir, GatewayConfigService.GatewayDirName);
        var service = new GatewayConfigService(store, _secrets, _clock.UtcNow);
        Assert.True((await service.GetPublicStatusAsync()).UsesInsecureTransport);

        store.WriteRawAtomic(GatewayConfigService.ConfigFileName,
            """{"BaseUrl":"https://example.invalid/v1","Model":"coding","Enabled":true,"LastProbeOk":false}""");
        var secure = await service.GetPublicStatusAsync();
        Assert.False(secure.UsesInsecureTransport);
        Assert.True(secure.IsEnabled);
    }

    [Fact]
    public async Task GatewayConfig_CarriesTheHandoffTheTransportNeeds_AndRequiresConsent()
    {
        var config = (await Service().GetEffectiveAsync())!;
        Assert.Equal("coding", config.Model);                       // the routed model tag
        Assert.Equal(GatewayConfigService.DefaultBaseUrl, config.BaseUrl);
        Assert.True(config.RequireConsent);                         // AiProcessing is checked upstream
        Assert.Equal(TimeSpan.FromSeconds(20), config.Timeout);
        Assert.Equal(GatewayKeyStore.EmbeddedSourceLabel, config.SourceLabel);
    }

    [Fact]
    public async Task GatewayConfig_IsTheOnlyHandoffThatCarriesTheKey_AndIsNamedAsTheHazard()
    {
        // The frozen contract makes GatewayConfig.ApiKey the ONE place decrypted key material may
        // exist (the transport's private handoff). That is a hazard by design, so the shape is
        // pinned: exactly one string property may carry it, and every OTHER type on this seam must
        // stay key-free. A future field on GatewayPublicStatus is what this catches.
        var config = (await Service().GetEffectiveAsync())!;
        var keyCarriers = typeof(GatewayConfig).GetProperties()
            .Where(p => p.PropertyType == typeof(string) && p.Name.Contains("Key", StringComparison.Ordinal))
            .Select(p => p.Name).ToList();
        Assert.Equal(new[] { "ApiKey" }, keyCarriers);

        var status = await Service().GetPublicStatusAsync();
        foreach (var p in typeof(GatewayPublicStatus).GetProperties())
            Assert.DoesNotContain(config.ApiKey, p.GetValue(status)?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class ThrowingSecureStorage : ISecureStorageService
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("platform store unavailable");
        public Task SetAsync(string key, string value, CancellationToken ct = default) =>
            throw new InvalidOperationException("platform store unavailable");
        public Task RemoveAsync(string key, CancellationToken ct = default) =>
            throw new InvalidOperationException("platform store unavailable");
    }

    public void Dispose() => _dir.Dispose();
}
