using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Cloud;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — where the cloud base URL comes from, and what "configured" is allowed to mean.
///
/// The default is UNCONFIGURED. There is no built-in backend host baked into the client: §5c freezes
/// the shapes, not the deployment, and a guessed hostname would let the seam look alive while it
/// talked to nothing (product law §0.1). The URL arrives either from the local advanced-settings file
/// (<c>cloud/cloud-settings.json</c> under the app data dir, the same layout every other service
/// uses) or from a caller-supplied factory (a build default the lead can wire in one place later).
///
/// WHAT "configured" does NOT mean: it means a request COULD be sent. Reachability is a separate fact
/// that only a completed round-trip can produce, and the connector state machine keeps them apart —
/// <c>Unconfigured</c> vs <c>Unverified</c> vs <c>Synced</c> are three different sentences to a user.
///
/// MAUI-free: the directory arrives as an injected <see cref="LocalJsonStore"/>.
/// </summary>
public sealed class CloudApiOptions : ICloudApiOptions
{
    /// <summary>Subdirectory of the app data dir this service owns.</summary>
    public const string CloudDirName = "cloud";
    /// <summary>The one state file: base URL + timeout. No credentials, ever (they live in secure storage).</summary>
    public const string SettingsFileName = "cloud-settings.json";

    /// <summary>Default per-request timeout. Long enough for a sync batch on a phone link, short
    /// enough that a dead host cannot hang a page.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Minimum accepted scheme — the client refuses to send a bearer over anything else.</summary>
    public const string RequiredScheme = "https";

    private readonly LocalJsonStore _store;
    private readonly Func<string?>? _overrideUrl;
    private readonly bool _allowInsecureLoopback;
    private readonly object _gate = new();

    private sealed class SettingsFile
    {
        public string? BaseUrl { get; set; }
        public double? TimeoutSeconds { get; set; }
    }

    /// <param name="store">A LocalJsonStore rooted at <c>&lt;appData&gt;/LIVORA/cloud</c>.</param>
    /// <param name="overrideUrl">
    /// Optional higher-priority URL (a build default or an env-provided host in a dev head). Wins over
    /// the file so a developer's on-device edit cannot silently defeat the build they are testing.
    /// </param>
    /// <param name="allowInsecureLoopback">
    /// Test/dev only: lets <c>http://localhost</c> and <c>http://127.0.0.1</c> through the https rule.
    /// The scripted tests need a URL that is syntactically valid without pretending a cleartext
    /// deployment is acceptable for a real account; production composition never passes true.
    /// </param>
    public CloudApiOptions(
        LocalJsonStore store,
        Func<string?>? overrideUrl = null,
        bool allowInsecureLoopback = false)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _overrideUrl = overrideUrl;
        _allowInsecureLoopback = allowInsecureLoopback;
    }

    /// <summary>The normalized URL after policy, or null when absent/rejected.</summary>
    public string? ResolvedUrl => TryResolve(out var url, out _) ? url : null;

    /// <summary>Why a configured URL was rejected (machine tag: "absent" | "relative" | "scheme" | "host").</summary>
    public string? RejectionReason => TryResolve(out _, out var reason) ? null : reason;

    public bool IsConfigured => ResolvedUrl is not null;

    string? ICloudApiOptions.BaseUrl => ResolvedUrl;

    public TimeSpan Timeout
    {
        get
        {
            var secs = Load()?.TimeoutSeconds;
            if (secs is null || secs.Value <= 0) return DefaultTimeout;
            return TimeSpan.FromSeconds(Math.Clamp(secs.Value, 1, 120));
        }
    }

    public string SourceLabel
    {
        get
        {
            var over = Safe(_overrideUrl);
            if (Normalize(over) is not null) return "override";
            var file = Load()?.BaseUrl;
            return string.IsNullOrWhiteSpace(file) ? "none" : "store";
        }
    }

    /// <summary>
    /// Persist the base URL (empty/whitespace clears it, back to unconfigured). Validated BEFORE the
    /// write: a typo must not leave the app in a state where the seam looks configured and fails
    /// every call. Returns null on success, or the machine rejection tag.
    /// </summary>
    public string? TrySetBaseUrl(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            Mutate(s => s.BaseUrl = null);
            return null;
        }
        if (!TryNormalize(baseUrl, _allowInsecureLoopback, out var normalized, out var reason))
            return reason ?? "invalid";
        Mutate(s => s.BaseUrl = normalized);
        return null;
    }

    public void SetTimeout(TimeSpan timeout)
    {
        var seconds = Math.Clamp(timeout.TotalSeconds, 1, 120);
        Mutate(s => s.TimeoutSeconds = seconds);
    }

    // ---- policy -----------------------------------------------------------------

    private bool TryResolve(out string? url, out string? reason)
    {
        url = null;
        reason = "absent";

        var over = Safe(_overrideUrl);
        if (!string.IsNullOrWhiteSpace(over))
        {
            if (TryNormalize(over, _allowInsecureLoopback, out url, out reason)) return url is not null;
            return false;
        }

        var file = Load()?.BaseUrl;
        if (string.IsNullOrWhiteSpace(file)) { reason = "absent"; return false; }
        return TryNormalize(file, _allowInsecureLoopback, out url, out reason);
    }

    private static string? Safe(Func<string?>? f)
    {
        try { return f?.Invoke(); } catch (Exception) { return null; }   // a broken source = unconfigured, never a crash
    }

    private static string? Normalize(string? candidate) =>
        TryNormalize(candidate, allowInsecureLoopback: true, out var url, out _) ? url : null;

    /// <summary>
    /// Absolute http(s) URL with a host, trailing slash trimmed. Loopback http is allowed only when
    /// the caller opted in (tests / a local dev head); everything else must be https because a bearer
    /// token travels in the request.
    /// </summary>
    internal static bool TryNormalize(
        string? candidate, bool allowInsecureLoopback, out string? url, out string? reason)
    {
        url = null;
        reason = "absent";
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var parsed) || parsed is null)
        {
            reason = "relative";
            return false;
        }

        bool https = string.Equals(parsed.Scheme, RequiredScheme, StringComparison.OrdinalIgnoreCase);
        bool loopbackHttp = allowInsecureLoopback
            && string.Equals(parsed.Scheme, "http", StringComparison.OrdinalIgnoreCase)
            && parsed.Host is "localhost" or "127.0.0.1" or "::1";

        if (!https && !loopbackHttp) { reason = "scheme"; return false; }
        if (string.IsNullOrWhiteSpace(parsed.Host)) { reason = "host"; return false; }

        var text = parsed.GetLeftPart(UriPartial.Authority) + parsed.PathAndQuery.TrimEnd('/');
        url = text.TrimEnd('/');
        reason = null;
        return true;
    }

    // ---- file IO ----------------------------------------------------------------

    private SettingsFile? Load()
    {
        lock (_gate)
        {
            var raw = _store.ReadRaw(SettingsFileName);
            if (raw is null) return null;
            try { return JsonSerializer.Deserialize<SettingsFile>(raw, JsonOpts); }
            catch (JsonException) { return null; }   // corrupt = unconfigured, and the next write repairs it
        }
    }

    private void Mutate(Action<SettingsFile> mutate)
    {
        lock (_gate)
        {
            var state = Load() ?? new SettingsFile();
            mutate(state);
            _store.WriteRawAtomic(SettingsFileName, JsonSerializer.Serialize(state, JsonOpts));
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};
}
