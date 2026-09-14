using System.Security.Cryptography;
using System.Text;

namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: the ONLY sanctioned way a LIVORA password becomes stored bytes: PBKDF2-HMAC-SHA512 with
///          a fresh CSPRNG salt PER PASSWORD. No plaintext, no bare hash, ever at rest.
/// OWNER: Agent 03 (P1-C identity). (Reconciled with the parallel edit of this file on 2026-09-14:
///        the frozen Entities.cs documents the "iterations$salt$subkey" shape — that format stays —
///        and the timing-equalization / rehash-upgrade API the login path depends on is restored.)
/// FORMAT: "iterations$saltBase64$subkeyBase64" — the cost parameter travels WITH the hash, so the
///         iteration count can rise later without a flag-day migration: Verify honours the stored
///         count, NeedsRehash says when a row predates the current cost, and login re-hashes
///         transparently while the plaintext is known-correct.
///         Max 140 ASCII chars at these sizes — fits users.PasswordHash(200).
/// INVARIANTS:
///   - 210,000 iterations (above the OWASP 2023 SHA-512 floor), 32-byte salt, 64-byte subkey;
///     the cost is CLAMPED at MinIterations on Hash even if a caller passes less — the floor is
///     not negotiable by accident
///   - verification is constant-time over the derived key (FixedTimeEquals): a byte-at-a-time
///     compare leaks the first-differing position to a timing adversary
///   - Verify NEVER throws on malformed stored data — garbage in the column returns false, so a
///     corrupt row can never become a backdoor or a 500 on the login path
///   - EqualizingVerify burns the SAME KDF work as a real check: callers run it on the
///     "no such user" branch so unknown-email and wrong-password responses are identical in BODY
///     (contract §5c) and in COST (this lane's addition — timing must not leak existence either)
/// </summary>
public sealed class PasswordHasher
{
    public const int MinIterations = 100_000;
    public const int DefaultIterations = 210_000;
    public const int SaltBytes = 32;
    public const int SubkeyBytes = 64;
    /// <summary>Upper bound on accepted UTF-8 password length (KDF DoS guard for huge inputs).</summary>
    public const int MaxPasswordBytes = 1024;

    private static readonly HashAlgorithmName Alg = HashAlgorithmName.SHA512;

    /// <summary>A real PBKDF2 output over a random, process-local, never-known password — this is a
    /// stopwatch, not a credential. Built at startup so its cost matches the current scheme exactly.</summary>
    private readonly string _dummyHash;

    public PasswordHasher()
        => _dummyHash = Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    /// <summary>Hash with a fresh random salt. Two calls for the same password never match.</summary>
    public string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var cost = Math.Max(MinIterations, iterations);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var subkey = Derive(password, salt, cost);
        return $"{cost}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
    }

    /// <summary>True when <paramref name="password"/> matches <paramref name="stored"/>.
    /// Returns false (never throws) for null/empty/garbage stored values — fail closed.</summary>
    public bool Verify(string? password, string? stored)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(stored))
            return false;

        var parts = stored.Split('$');
        if (parts.Length != 3)
            return false;
        if (!int.TryParse(parts[0], out var cost) || cost is < MinIterations or > 5_000_000)
            return false;

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }
        if (salt.Length != SaltBytes || expected.Length != SubkeyBytes)
            return false;

        var actual = Derive(password, salt, cost);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Full-cost verification against the dummy hash — the "user not found" branch calls
    /// this so a miss costs exactly the CPU of a hit.</summary>
    public void EqualizingVerify(string? password) => Verify(password, _dummyHash);

    /// <summary>True when the row predates the current cost parameters and should be rewritten
    /// after a successful verification (transparent upgrade on login).</summary>
    public bool NeedsRehash(string? stored)
        => string.IsNullOrEmpty(stored)
           || stored.Split('$') is not [_, _, _]
           || !int.TryParse(stored.Split('$')[0], out var cost)
           || cost != DefaultIterations;

    private static byte[] Derive(string password, byte[] salt, int iterations)
    {
        var bytes = Encoding.UTF8.GetBytes(
            password.Length > MaxPasswordBytes ? password[..MaxPasswordBytes] : password);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, iterations, Alg, SubkeyBytes);
        }
        catch (ArgumentOutOfRangeException)
        {
            // A stored hash with an absurd iteration count is refused, not OOM'd.
            return new byte[SubkeyBytes];
        }
    }
}
