using System.Text;
using LIVORA.Application.Abstractions;

namespace LIVORA.Infrastructure.IntelligenceProviders.Wave3c;

/// <summary>
/// The single gateway-config seam (Wave 3c lane 02). Serves the embedded built-in endpoint
/// OFF BY DEFAULT — the user must flip Enabled on (plain HTTP, honest UI warning) AND grant
/// AiProcessing consent before anything leaves the device. A user-entered key overrides the
/// built-in one.
///
/// THE KEY: never plaintext in this file. The built-in secret is stored as an XOR-obfuscated
/// base64 blob (the one sanctioned spot for key material in source). This is OBFUSCATION, not
/// encryption — a deliberate, documented tradeoff (see docs ADR note): a shipped app can never
/// truly hide a shared key; the blob only raises the bar above grep. If the file-based secure
/// store (ISecureStorageService) holds a user override, that wins. The decrypted key is handed
/// to the transport at call time and is never logged, never cached in public status, never
/// surfaced to UI DTOs. MAUI-free so it compiles into the plain-net10.0 test project.
/// </summary>
public sealed class EmbeddedGatewayConfigService : IGatewayConfigService
{
    /// <summary>Probed gateway (docs/LANES-WAVE3B.md): plain HTTP — hence off by default.</summary>
    public const string BuiltInBaseUrl = "http://sub.legoten.com:4455/v1";
    public const string BuiltInModel = "coding";

    // ---- obfuscated built-in key (XOR with split halves, base64) -----------
    // Salt kept split so no single grep-able constant reassembles the blob trivially.
    private const string BlobA = "P1pbARElTjUAAhthBBcZ";
    private const string BlobB = "f1MVA18vWiJGBV91VBYWLwZFBkU=";
    private const string Salt1 = "L1v0r";      // "L1v0r" + "A-W3c-X0r!"
    private const string Salt2 = "A-W3c-X0r!";

    private const int KeyLength = 35;   // fixed sanity bound; the decoder refuses anything else

    private readonly ISecureStorageService? _store;
    private const string UserKeyStoreName = "gateway.user.key";
    private const string EnabledStoreName = "gateway.enabled";

    private string? _memoryKeyOverride;
    private bool? _memoryEnabled;

    public EmbeddedGatewayConfigService(ISecureStorageService? secureStore = null)
        => _store = secureStore;

    public async Task<GatewayConfig?> GetEffectiveAsync(CancellationToken ct = default)
    {
        var userKey = await ReadUserKeyAsync(ct).ConfigureAwait(false);
        var enabled = await ReadEnabledAsync(ct).ConfigureAwait(false);
        return new GatewayConfig
        {
            BaseUrl = BuiltInBaseUrl,
            ApiKey = !string.IsNullOrWhiteSpace(userKey) ? userKey! : DecodeBuiltInKey(),
            Model = BuiltInModel,
            Enabled = enabled,
            RequireConsent = true,
            Timeout = TimeSpan.FromSeconds(20),
            SourceLabel = !string.IsNullOrWhiteSpace(userKey) ? "UserEntered" : "Embedded-obfuscated",
            IsInsecureTransport = BuiltInBaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        };
    }

    public async Task SetUserKeyOverrideAsync(string? apiKey, CancellationToken ct = default)
    {
        _memoryKeyOverride = apiKey;
        if (_store is null) return;
        if (string.IsNullOrEmpty(apiKey)) await _store.RemoveAsync(UserKeyStoreName, ct).ConfigureAwait(false);
        else await _store.SetAsync(UserKeyStoreName, apiKey, ct).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        _memoryEnabled = enabled;
        if (_store is null) return;
        await _store.SetAsync(EnabledStoreName, enabled ? "1" : "0", ct).ConfigureAwait(false);
    }

    /// <summary>UI-safe by construction: no key material, ever — base URL + model + labels only.</summary>
    public async Task<GatewayPublicStatus> GetPublicStatusAsync(CancellationToken ct = default)
    {
        var cfg = await GetEffectiveAsync(ct).ConfigureAwait(false);
        return new GatewayPublicStatus
        {
            IsConfigured = cfg is not null,
            IsEnabled = cfg?.Enabled == true,
            UsesInsecureTransport = cfg?.IsInsecureTransport == true,
            Model = cfg?.Model ?? "",
            KeySource = cfg?.SourceLabel == "UserEntered" ? "Your own key" : "Built-in (obfuscated)",
            LastVerifiedUtc = null,     // probes live in the transport; wired by the orchestrator wave
            LastProbeOk = false,
        };
    }

    private async Task<string?> ReadUserKeyAsync(CancellationToken ct)
    {
        if (_memoryKeyOverride is not null) return _memoryKeyOverride;
        if (_store is null) return null;
        try { return await _store.GetAsync(UserKeyStoreName, ct).ConfigureAwait(false); }
        catch { return null; }
    }

    private async Task<bool> ReadEnabledAsync(CancellationToken ct)
    {
        if (_memoryEnabled.HasValue) return _memoryEnabled.Value;   // OFF unless the user turned it on
        if (_store is null) return false;
        try
        {
            var v = await _store.GetAsync(EnabledStoreName, ct).ConfigureAwait(false);
            return v == "1";    // anything else (null/typo) = disabled — fail closed
        }
        catch { return false; }
    }

    /// <summary>Reassemble + de-obfuscate the built-in key. Allocation-light, no logging.</summary>
    internal static string DecodeBuiltInKey()
    {
        var blob = Convert.FromBase64String(BlobA + BlobB);
        if (blob.Length != KeyLength)
            throw new InvalidOperationException("gateway key blob failed its sanity length check");
        var salt = Salt1 + Salt2;
        var sb = new StringBuilder(blob.Length);
        for (int i = 0; i < blob.Length; i++)
            sb.Append((char)(blob[i] ^ (byte)salt[i % salt.Length]));
        var key = sb.ToString();
        if (!key.StartsWith("sk-", StringComparison.Ordinal))
            throw new InvalidOperationException("gateway key blob failed its sanity prefix check");
        return key;
    }
}
