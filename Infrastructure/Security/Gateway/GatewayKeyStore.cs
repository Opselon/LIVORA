using System.Security.Cryptography;
using System.Text;

namespace LIVORA.Infrastructure.Security.Gateway;

/// <summary>
/// The ONE place the built-in AI-gateway credential lives, and it does NOT live there as
/// plaintext. The value is stored as a base64 blob XOR-masked with a keystream derived from
/// SHA-256(salt || blockIndex) — enough to keep the key out of casual greps, string dumps and
/// accidental source commits, and NOT enough to be called encryption.
///
/// HONESTY BOUNDARY (this comment is the ADR statement; no UI copy may contradict it):
/// obfuscation != encryption. Anyone who can read the shipped binary can rebuild this key, so
/// it only ever serves as a BUILT-IN FALLBACK credential for a plain-HTTP demo endpoint that
/// ships DISABLED by default. The stronger path — DPAPI (Windows CryptProtectData,
/// CurrentUser) / platform keystore through the secure-storage seam
/// (<see cref="LIVORA.Application.Abstractions.ISecureStorageService"/> +
/// <see cref="LIVORA.Infrastructure.Security.IPlatformSecureBox"/>) — is what protects
/// USER-ENTERED keys at rest, and a user-entered key always takes precedence over this blob
/// (see <see cref="GatewayConfigService"/>).
///
/// Decoding happens ONLY at use time into a zero-able char buffer; plaintext never goes to disk,
/// never to a log, never to the UI. The UI-safe view is GatewayPublicStatus (no key material).
/// </summary>
public static class GatewayKeyStore
{
    /// <summary>Machine tag persisted in gateway-config.json / reported as GatewayConfig.SourceLabel.</summary>
    public const string EmbeddedSourceLabel = "Embedded-obfuscated";

    /// <summary>Keystream salt. Public by design: it is a domain separator, not a secret.</summary>
    internal const string MaskSalt = "LIVORA::gateway::wave3c::v1";

    /// <summary>XOR-masked + base64 embedded credential. Decode through this class only.</summary>
    private const string Blob = "x0I9peGsa3GqiBZmOntViJMBwuC5E8N/MfllWntiZQBk9Eo=";

    /// <summary>
    /// Decodes the embedded credential into a FRESH zero-able char buffer. Callers MUST
    /// <c>Array.Clear(buf, 0, buf.Length)</c> as soon as they are done (prefer
    /// <see cref="GetEmbeddedKey"/>, which does that for you). Returns false — without throwing —
    /// when the blob is corrupt or tampered with.
    /// </summary>
    public static bool TryDecodeToBuffer(out char[] buffer)
    {
        buffer = Array.Empty<char>();
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(Blob);
        }
        catch (FormatException)
        {
            return false;
        }
        if (bytes.Length == 0) return false;

        var mask = Keystream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++) bytes[i] ^= mask[i];

        // The credential is printable ASCII by contract; anything else means a corrupt blob.
        foreach (var b in bytes)
        {
            if (b is < 0x21 or > 0x7E)
            {
                Array.Clear(bytes, 0, bytes.Length);
                return false;
            }
        }

        buffer = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) buffer[i] = (char)bytes[i];
        Array.Clear(bytes, 0, bytes.Length);
        return buffer.Length >= 8;
    }

    /// <summary>
    /// Convenience: materialize the embedded key as a string, zeroing the intermediate buffer.
    /// (The returned string is immutable and cannot be zeroed — hold it nowhere long-lived, never
    /// log it, never persist it. On the Windows path a user key lives in DPAPI instead.)
    /// Null when the blob is corrupt.
    /// </summary>
    public static string? GetEmbeddedKey()
    {
        if (!TryDecodeToBuffer(out var buf)) return null;
        try { return new string(buf); }
        finally { Array.Clear(buf, 0, buf.Length); }
    }

    /// <summary>
    /// SHA-256(salt || 1-byte-block-index) blocks concatenated, 1-based index — must match the
    /// build-time masker exactly (the round-trip is pinned by GatewayKeyStoreTests).
    /// </summary>
    private static byte[] Keystream(int length)
    {
        var salt = Encoding.ASCII.GetBytes(MaskSalt);
        var output = new byte[length];
        int pos = 0;
        for (int block = 1; pos < length; block++)
        {
            var seed = new byte[salt.Length + 1];
            Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
            seed[^1] = (byte)block;
            var hash = SHA256.HashData(seed);
            var take = Math.Min(hash.Length, length - pos);
            new ReadOnlySpan<byte>(hash, 0, take).CopyTo(new Span<byte>(output, pos, take));
            pos += take;
        }
        return output;
    }
}
