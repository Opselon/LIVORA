using System.Security.Cryptography;

namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: refresh-token material — generation and the at-rest transform. The plaintext token
///          exists exactly twice: inside the minting call and in the one response that returns it.
///          The database only ever sees <see cref="Hash"/> output (CONTRACT-P1 §5c note 1).
/// OWNER: Agent 03 (identity lane).
/// PROVIDES: <see cref="NewToken"/>, <see cref="Hash"/>.
/// INVARIANTS:
///   - 48 CSPRNG bytes, base64url (no padding) ⇒ 64 chars of ~288 bits of entropy — brute force
///     of the token space is not a live attack even if rate limiting is bypassed
///   - hash-at-rest is unsalted SHA-256 over 288 bits of random input: the input's own entropy makes
///     rainbow tables moot, and the one-way function means a stolen DB dump cannot be replayed
///   - <see cref="Hash"/> never throws on null: garbage tokens hash to a value that matches nothing
///     rather than turning a probe attempt into a 500
/// EXTEND: if the token format ever changes, bump the minting side only — lookup is by hash of the
///         presented string, so old rows keep working until they expire.
/// </summary>
public static class RefreshTokens
{
    public const int TokenBytes = 48;

    public static string NewToken()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Lowercase hex SHA-256 (64 chars) — fits the 128-char unique index on
    /// auth_sessions.RefreshTokenHash and is comparable across providers.</summary>
    public static string Hash(string? plaintextToken)
    {
        if (string.IsNullOrEmpty(plaintextToken))
            return new string('0', 64); // matches nothing real; constant shape for probes
        var digest = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextToken));
        return Convert.ToHexStringLower(digest);
    }

    private static class Base64Url
    {
        public static string Encode(byte[] bytes)
            => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
