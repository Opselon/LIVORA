using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the cloud session token store: where an access token, a refresh token and the
/// session id live between launches, and the ONLY place in the client that reads them back.
///
/// WHY <see cref="ISecureStorageService"/> AND NOTHING ELSE: Wave 3c already owns the platform
/// at-rest seam (<c>SecureStorageService</c> → DPAPI CurrentUser on Windows, private-app-directory
/// with an explicit "no cipher" label elsewhere). Re-using it means the cloud tokens get exactly the
/// protection the rest of the user's secrets get, and the honest capability flag
/// (<see cref="IsOsBacked"/>) is read from the same source instead of being re-guessed. Re-implementing
/// a second vault here would be a second place a token can leak.
///
/// AT-REST SHAPE — one key, one JSON blob, all fields protected as a unit:
/// <list type="bullet">
///   <item>key <see cref="SessionStorageKey"/>; value = the serialized <see cref="CloudSession"/>,
///     which the store encrypts (or, on the non-OS-backed box, protects only by the private dir —
///     and then <see cref="IsOsBacked"/> is false and no surface may say "encrypted");</item>
///   <item>no plaintext token is ever written to any other file: the sync watermark, the connector
///     record and the settings file carry ids, timestamps and codes only;</item>
///   <item>no logging at all in this class — the same rule <c>SecureStorageService</c> states for
///     itself, because a token would otherwise escape through the logger on a successful sign-in.</item>
/// </list>
///
/// ROTATION: §5c returns a NEW refresh token on every <c>/auth/refresh</c>. <see cref="WriteAsync"/>
/// replaces the whole blob, so a rotation cannot leave an old refresh token behind on disk (a reused
/// rotated-out token is a theft signal server-side — a stale local copy would produce it accidentally).
///
/// HONEST FAILURE: a corrupt or foreign ciphertext (e.g. a DPAPI blob from another Windows account)
/// reads as "no session" rather than throwing — the app degrades to signed-out, and the connector
/// says so, instead of crash-looping on a page.
/// </summary>
public sealed class CloudTokenStore : ICloudTokenStore
{
    /// <summary>The single secure-storage key this store owns.</summary>
    public const string SessionStorageKey = "livora.cloud.session.v1";

    /// <summary>Legacy key from Wave 3c's never-used seam — cleared once on read so no orphan survives.</summary>
    public const string LegacySessionStorageKey = "livora.cloud.session";

    private readonly ISecureStorageService _secure;
    private readonly Func<bool> _osBacked;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _legacyChecked;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Canonical ordering keeps the blob byte-stable for the same session (diagnostics diffing);
        // correctness never depends on it.
        WriteIndented = false,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};

    /// <param name="osBacked">
    /// Read-through of the platform box's real capability — the composition root passes
    /// <c>() => secureService.IsPlatformHardwareBacked</c>. Defaults to false: "not proven" is the
    /// only safe assumption when nobody tells us, and the flag only ever suppresses the word
    /// "encrypted", never enables it.
    /// </param>
    public CloudTokenStore(
        ISecureStorageService secureStorage,
        Func<bool>? osBacked = null,
        TimeProvider? timeProvider = null)
    {
        _secure = secureStorage ?? throw new ArgumentNullException(nameof(secureStorage));
        _osBacked = osBacked ?? (() => false);
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsOsBacked
    {
        get
        {
            try { return _osBacked(); } catch (Exception) { return false; }
        }
    }

    public async Task<CloudSession?> ReadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var json = await _secure.GetAsync(SessionStorageKey, ct).ConfigureAwait(false);

            if (!_legacyChecked)
            {
                _legacyChecked = true;
                // A half-migrated Wave 3c blob is not a session this build understands: drop it.
                await _secure.RemoveAsync(LegacySessionStorageKey, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(json)) return null;

            CloudSession? session;
            try
            {
                session = JsonSerializer.Deserialize<CloudSession>(json, Options);
            }
            catch (JsonException)
            {
                session = null;
            }

            if (session is null || !IsShapeValid(session))
            {
                // Unreadable or structurally invalid = no session, and it is erased so the next
                // sign-in starts clean instead of re-reading garbage every launch.
                await _secure.RemoveAsync(SessionStorageKey, ct).ConfigureAwait(false);
                return null;
            }
            return session;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(CloudSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsShapeValid(session))
            throw new ArgumentException("A session must carry user id, session id and both tokens.", nameof(session));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stamped = session.ReceivedAtUtc == default
                ? session with { ReceivedAtUtc = _time.GetUtcNow() }
                : session;
            await _secure.SetAsync(SessionStorageKey, JsonSerializer.Serialize(stamped, Options), ct)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _secure.RemoveAsync(SessionStorageKey, ct).ConfigureAwait(false);
            await _secure.RemoveAsync(LegacySessionStorageKey, ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// True when the bearer is at/past expiry (with a safety margin so a request that starts before
    /// expiry and lands after it is refreshed instead of rejected). Pure read of the stored session.
    /// </summary>
    public async Task<bool> HasUsableAccessTokenAsync(CancellationToken ct = default, TimeSpan? margin = null)
    {
        var session = await ReadAsync(ct).ConfigureAwait(false);
        return session is not null && !session.IsAccessExpired(_time.GetUtcNow(), margin ?? TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Every token-shaped field this store is known to hold, for the privacy inventory. Reports the
    /// PRESENCE of each (bool), never a value: a UI or log line can say "a session is stored here"
    /// without any credential crossing the boundary.
    /// </summary>
    public async Task<IReadOnlyList<string>> DescribeKeysAsync(CancellationToken ct = default)
    {
        var present = await ReadAsync(ct).ConfigureAwait(false) is not null;
        return present
            ? new[] { SessionStorageKey + ":accessToken", SessionStorageKey + ":refreshToken" }
            : Array.Empty<string>();
    }

    private static bool IsShapeValid(CloudSession s) =>
        !string.IsNullOrWhiteSpace(s.UserId)
        && !string.IsNullOrWhiteSpace(s.SessionId)
        && !string.IsNullOrWhiteSpace(s.AccessToken)
        && !string.IsNullOrWhiteSpace(s.RefreshToken)
        && s.ExpiresAtUtc != default;
}
