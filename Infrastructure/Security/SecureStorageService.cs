using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Abstractions;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// The <see cref="ISecureStorageService"/> seam for small secrets (a user-entered gateway key, a
/// future provider token, the local passcode verifier). MAUI-free by construction: the file layout
/// comes from <see cref="LocalJsonStore"/> (injected directory) and the cryptographic decision from
/// <see cref="IPlatformSecureBox"/>, so the plain-net10 test head runs the real class against a fake
/// box and the Windows head runs it against DPAPI.
///
/// ON-DISK CONTRACT — what "no plaintext on disk" means concretely here:
/// <list type="bullet">
///   <item>the value is UTF-8 encoded, run through the box, and the result is written as base64 in
///     <c>secure/secure-store.json</c>. On Windows that base64 is DPAPI ciphertext, so a byte scan
///     of the file can never find the input string (asserted by test).</item>
///     <item>temp + <c>File.Replace(string,string,string)</c>: a crash mid-write leaves the previous ciphertext, not
///     a half-written value, and never a stray plaintext temp file (the temp file already carries
///     the protected bytes).</item>
///   <item>no logging whatsoever in this class — the interface's own contract forbids it, and a
///     secret would otherwise escape through the logger.</item>
/// </list>
///
/// HONESTY: <see cref="IsPlatformHardwareBacked"/> is the only claim this class makes about its own
/// strength, and it is a straight read-through of the injected box. On the non-Windows fallback the
/// box is the identity transform, so the flag is false and the value is protected ONLY by the
/// private app-data directory — the UI must never render "encrypted" there. The stronger path for
/// user keys (DPAPI CurrentUser on Windows, keystore/keychain on the mobile heads) is what the
/// real platform box implements; obfuscation of the built-in gateway credential
/// (<c>GatewayKeyStore</c>) is NOT encryption and is never routed through here.
/// </summary>
public sealed class SecureStorageService : ISecureStorageService
{
    /// <summary>Subdirectory owned by this service, under the app data dir.</summary>
    public const string StoreDirName = "secure";
    /// <summary>The one file. Contents are key → protected-base64, nothing else.</summary>
    public const string StoreFileName = "secure-store.json";

    private readonly LocalJsonStore _store;
    private readonly IPlatformSecureBox _box;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public SecureStorageService(LocalJsonStore store, IPlatformSecureBox box)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _box = box ?? throw new ArgumentNullException(nameof(box));
    }

    /// <summary>Machine tag of the active box ("dpapi-current-user" | "private-app-dir-no-cipher").</summary>
    public string PlatformLabel => _box.Label;

    /// <summary>
    /// True only when the platform box provides a real at-rest boundary (Windows/DPAPI). The UI
    /// reads this instead of assuming, so a Linux/Android test run can never display "encrypted".
    /// </summary>
    public bool IsPlatformHardwareBacked => _box.IsHardwareOrOsBacked;

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var map = Load();
            if (!map.TryGetValue(key, out var protectedValue) || string.IsNullOrEmpty(protectedValue))
                return Task.FromResult<string?>(null);
            byte[] plain;
            try
            {
                plain = _box.Unprotect(Convert.FromBase64String(protectedValue));
            }
            catch (Exception)
            {
                // Corrupt/foreign ciphertext (e.g. DPAPI blob from another Windows user) reads as
                // "absent" — callers degrade to the documented fallback, never crash a page.
                return Task.FromResult<string?>(null);
            }
            var text = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return Task.FromResult<string?>(text);
        }
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            var map = Load();
            var bytes = Encoding.UTF8.GetBytes(value);
            string encoded;
            try
            {
                encoded = Convert.ToBase64String(_box.Protect(bytes));
            }
            finally
            {
                Array.Clear(bytes, 0, bytes.Length);
            }
            map[key] = encoded;
            Save(map);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var map = Load();
            if (map.Remove(key)) Save(map);
        }
        return Task.CompletedTask;
    }

    /// <summary>Every stored key (no values). Used by the privacy inventory, never by UI text.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return Load().Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); }
    }

    // ---- raw file helpers (the catalog's "settings"/wipe paths use these) --------

    private Dictionary<string, string> Load()
    {
        var json = _store.ReadRaw(StoreFileName);
        if (json is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Save(Dictionary<string, string> map) =>
        _store.WriteRawAtomic(StoreFileName, JsonSerializer.Serialize(map, Options));
}
