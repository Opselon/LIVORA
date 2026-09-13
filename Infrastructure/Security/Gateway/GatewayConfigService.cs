using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security.Gateway;

namespace LIVORA.Infrastructure.Security.Gateway;

/// <summary>
/// The single implementation of the frozen <see cref="IGatewayConfigService"/> seam (wave 3c,
/// lane 01). Resolution rules, in order, and nothing else:
/// <list type="number">
///   <item><b>Key:</b> a USER-ENTERED override (stored through <see cref="ISecureStorageService"/>
///     — DPAPI on Windows) wins. Only when there is no override does the obfuscated embedded
///     fallback (<see cref="GatewayKeyStore"/>) apply. "None" when both are absent.</item>
///   <item><b>Enabled:</b> master switch persisted in <c>gateway/gateway-config.json</c> under the
///     gateway dir; DEFAULTS TO FALSE. The endpoint is plain HTTP, so honesty rules require the AI
///     off until the user explicitly opts in.</item>
///   <item><b>Probe state:</b> <c>LastVerifiedUtc</c> / <c>LastProbeOk</c> are written ONLY after a
///     real round-trip health check via <see cref="RecordProbeResult"/> — "never verified" renders
///     as never verified, never as connected.</item>
/// </list>
/// The raw key exists in memory only inside <see cref="GetEffectiveAsync"/>'s returned
/// <see cref="GatewayConfig"/> (the transport's private handoff). <see
/// cref="GetPublicStatusAsync"/> builds a DTO with no key field at all, and this class writes no
/// log line containing it. The JSON state file holds enabled/probe/base-url/model — never a key.
/// MAUI-free by construction: platform IO comes in through <see cref="LocalJsonStore"/>.
/// </summary>
public sealed class GatewayConfigService : IGatewayConfigService
{
    /// <summary>Subdirectory of the app data dir this service owns (state JSON lives here).</summary>
    public const string GatewayDirName = "gateway";
    /// <summary>The state file — no key material, ever (asserted by the tripwire tests).</summary>
    public const string ConfigFileName = "gateway-config.json";
    /// <summary>Secure-storage key for the user-entered override (value at rest = DPAPI ciphertext).</summary>
    public const string UserOverrideStorageKey = "gateway.user_api_key";

    /// <summary>The probed gateway (see WAVE3B env facts). Plain HTTP → insecure transport flag.</summary>
    public const string DefaultBaseUrl = "http://sub.legoten.com:4455/v1";
    public const string DefaultModel = "coding";

    /// <summary>Sentinel that clears the override even if a platform store rejects RemoveAsync.</summary>
    private const string RemoveSentinel = "";

    private readonly LocalJsonStore _store;
    private readonly ISecureStorageService _secure;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    /// <summary>Persisted state. Deliberately has NO field that could hold a key.</summary>
    private sealed class GatewayState
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = DefaultBaseUrl;
        public string Model { get; set; } = DefaultModel;
        /// <summary>ISO-8601 ("o") UTC stamp of the last SUCCESSFUL real probe; null = never proven.</summary>
        public string? LastVerifiedUtc { get; set; }
        public bool LastProbeOk { get; set; }
        /// <summary>Machine-readable reason of the last failed probe (no response bodies, no keys).</summary>
        public string? LastProbeFailureKey { get; set; }
    }

    public GatewayConfigService(
        LocalJsonStore store,
        ISecureStorageService secureStorage,
        Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _secure = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ---- IGatewayConfigService ------------------------------------------------

    public async Task<GatewayConfig?> GetEffectiveAsync(CancellationToken ct = default)
    {
        var state = LoadState();

        string? userKey = null;
        try { userKey = await _secure.GetAsync(UserOverrideStorageKey, ct); }
        catch { userKey = null; }   // a broken secure store degrades to embedded, never throws at UI

        bool hasUserKey = !string.IsNullOrWhiteSpace(userKey);
        string? key = hasUserKey ? userKey : GatewayKeyStore.GetEmbeddedKey();
        if (key is null) return null;   // NotConfigured — honest null, the AI layer must fall back to rules

        var baseUrl = string.IsNullOrWhiteSpace(state.BaseUrl) ? DefaultBaseUrl : state.BaseUrl;
        return new GatewayConfig
        {
            BaseUrl = baseUrl,
            ApiKey = key,
            Model = string.IsNullOrWhiteSpace(state.Model) ? DefaultModel : state.Model,
            Enabled = state.Enabled,
            RequireConsent = true,
            Timeout = TimeSpan.FromSeconds(20),
            SourceLabel = hasUserKey ? "UserEntered" : GatewayKeyStore.EmbeddedSourceLabel,
            IsInsecureTransport = !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
        };
    }

    public async Task SetUserKeyOverrideAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            // Remove the override → precedence falls back to the embedded source. The sentinel write
            // is belt-and-braces for platform stores where Remove is best-effort: an empty value
            // reads back as "no user key" (IsNullOrWhiteSpace), so the fallback cannot be skipped.
            await _secure.RemoveAsync(UserOverrideStorageKey, ct);
            await _secure.SetAsync(UserOverrideStorageKey, RemoveSentinel, ct);
            return;
        }
        await _secure.SetAsync(UserOverrideStorageKey, apiKey.Trim(), ct);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        Mutate(s => s.Enabled = enabled);
        return Task.CompletedTask;
    }

    public async Task<GatewayPublicStatus> GetPublicStatusAsync(CancellationToken ct = default)
    {
        var state = LoadState();
        // IsConfigured must answer WITHOUT materializing the key: presence of a user override or a
        // decodable embedded blob is enough, so no plaintext ever exists on the status path.
        bool hasUserKey;
        try { hasUserKey = !string.IsNullOrWhiteSpace(await _secure.GetAsync(UserOverrideStorageKey, ct)); }
        catch { hasUserKey = false; }
        bool hasEmbedded = GatewayKeyStore.TryDecodeToBuffer(out var probe) && probe.Length > 0;
        if (probe.Length > 0) Array.Clear(probe, 0, probe.Length);

        var baseUrl = string.IsNullOrWhiteSpace(state.BaseUrl) ? DefaultBaseUrl : state.BaseUrl;
        var status = new GatewayPublicStatus
        {
            IsConfigured = hasUserKey || hasEmbedded,
            IsEnabled = state.Enabled,
            UsesInsecureTransport = !baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase),
            Model = string.IsNullOrWhiteSpace(state.Model) ? DefaultModel : state.Model,
            KeySource = hasUserKey ? "Your own key" : hasEmbedded ? "Built-in (obfuscated)" : "None",
            LastVerifiedUtc = TryParseUtc(state.LastVerifiedUtc, out var verified) ? verified : (DateTime?)null,
            LastProbeOk = state.LastProbeOk && TryParseUtc(state.LastVerifiedUtc, out _),
        };
        return status;
    }

    // ---- probe bookkeeping (called by whoever actually performs the health check) ----

    /// <summary>
    /// Records the outcome of a REAL round-trip probe (lane 02's transport calls this after a
    /// successful/failed completion). On success the UTC instant is persisted and LastProbeOk
    /// flips true; on failure LastProbeOk goes false and the failure reason key is kept for UI.
    /// </summary>
    public void RecordProbeResult(bool ok, string? failureReasonKey = null)
    {
        Mutate(s =>
        {
            if (ok)
            {
                s.LastProbeOk = true;
                s.LastVerifiedUtc = _utcNow().ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
                s.LastProbeFailureKey = null;
            }
            else
            {
                s.LastProbeOk = false;
                s.LastProbeFailureKey = failureReasonKey;
            }
        });
    }

    /// <summary>The reason key of the last failed probe (null when the last probe passed/none ran).</summary>
    public string? LastProbeFailureKey => LoadState().LastProbeFailureKey;

    // ---- state IO ---------------------------------------------------------------

    private GatewayState LoadState()
    {
        lock (_gate)
        {
            var state = _store.LoadObject<GatewayState>(ConfigFileName);
            if (state is null) return new GatewayState();   // first run: disabled, defaults, never verified
            if (string.IsNullOrWhiteSpace(state.BaseUrl)) state.BaseUrl = DefaultBaseUrl;
            if (string.IsNullOrWhiteSpace(state.Model)) state.Model = DefaultModel;
            return state;
        }
    }

    private void Mutate(Action<GatewayState> mutate)
    {
        lock (_gate)
        {
            var state = LoadState();
            mutate(state);
            _store.SaveObject(ConfigFileName, state);
        }
    }

    private static bool TryParseUtc(string? raw, out DateTime utc)
    {
        utc = default;
        return !string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind, out var parsed)
            && (utc = parsed.ToUniversalTime()) != default;
    }
}
