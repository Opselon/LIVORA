using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LIVORA.Application.Abstractions;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// Wave 3c (lane 01) — the device passcode (the only REAL "account" this app has). It guards access
/// on THIS device; it is not a cloud identity, and nothing here ever reaches a network.
///
/// WHAT IS STORED, AND WHERE:
/// <list type="bullet">
///   <item>PBKDF2 (RFC 2898) over SHA-256, <see cref="Iterations"/> rounds, with a fresh
///     <see cref="SaltBytes"/>-byte <see cref="RandomNumberGenerator"/> salt per set. The passcode
///     itself is NEVER stored, hashed-then-truncated, or recoverable: verification recomputes the
///     derived key from the stored salt and compares digests.</item>
///   <item>The verifier (salt + digest, hex, with the iteration count so it can be raised later) is
///     persisted through <see cref="ISecureStorageService"/> — DPAPI CurrentUser on Windows, private
///     app directory elsewhere — under a key derived from the profile id, never from the passcode. So
///     the entry is unlinkable from the secret and the secret never touches the disk in any form.
///     A verifier without the secure store behind it would still not reveal a passcode, but the
///     secure store is what keeps the salt out of a casual file read.</item>
///   <item>Verification uses <c>CryptographicOperations.FixedTimeEquals</c>: no timing oracle on the comparison. The
///     work factor (150 000 iterations) is what actually slows an attacker down; the comparison
///     detail only removes a free bit per attempt.</item>
/// </list>
///
/// BRUTE-FORCE LOCKOUT (honest scope): <see cref="MaxFailedAttempts"/> wrong verifications inside
/// <see cref="LockoutWindow"/> flip <see cref="IsLocked"/>; while locked, <see cref="VerifyAsync"/>
/// returns false WITHOUT running PBKDF2 at all (so a locked app cannot be used to burn CPU at the
/// attacker's speed). The lock lifts by itself once the window expires, or explicitly via
/// <see cref="Unlock"/> — there is no backdoor and no reset secret, because there is no server to
/// call: if the user forgets the passcode the only path is the privacy wipe.
///
/// Cost is paid off the caller's thread via <c>Task.Run</c> so
/// the UI can show a spinner instead of freezing on ~150k SHA-256 rounds.
/// </summary>
public sealed class LocalPasscodeService : ILocalPasscodeService
{
    /// <summary>PBKDF2-SHA256 rounds. Chosen for a ~100 ms device-side verify on 2026 hardware.</summary>
    public const int Iterations = 150_000;
    /// <summary>Random per-set salt length (128-bit — well past anything rainbow-tabled).</summary>
    public const int SaltBytes = 16;
    /// <summary>Derived digest length.</summary>
    public const int HashBytes = 32;
    /// <summary>Shortest passcode the service will accept. Below this a "PIN" is guessable by hand.</summary>
    public const int MinPasscodeLength = 4;
    /// <summary>Wrong attempts tolerated inside <see cref="LockoutWindow"/> before the service locks.</summary>
    public const int MaxFailedAttempts = 5;
    /// <summary>Rolling window the failure count is measured in.</summary>
    public static readonly TimeSpan LockoutWindow = TimeSpan.FromMinutes(1);

    /// <summary>Prefix of the secure-storage key. The suffix is a digest of the profile id.</summary>
    internal const string VerifierKeyPrefix = "passcode.v1.";

    private readonly ISecureStorageService _storage;
    private readonly Func<string?> _profileId;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    private int _failed;
    private DateTime? _firstFailureUtc;

    /// <param name="storage">Encrypted-at-rest seam — the verifier never goes to a plain file.</param>
    /// <param name="profileId">Local profile id (SessionState) — used only to namespace the key, so
    /// a second profile on the same install gets its own passcode. Null is fine (one key).</param>
    /// <param name="utcNow">Clock seam so the lockout window is testable without sleeping.</param>
    public LocalPasscodeService(
        ISecureStorageService storage,
        Func<string?>? profileId = null,
        Func<DateTime>? utcNow = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _profileId = profileId ?? (() => null);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>True when a verifier exists (so the UI asks for a passcode instead of offering to set one).</summary>
    public bool IsSet => GetBlocking() is not null;

    /// <summary>True while the lockout is in force this session.</summary>
    public bool IsLocked
    {
        get
        {
            lock (_gate)
            {
                if (_failed < MaxFailedAttempts) return false;
                var started = _firstFailureUtc ?? default;
                if (_utcNow() - started > LockoutWindow) { ResetFailures(); return false; }
                return true;
            }
        }
    }

    /// <summary>How many attempts remain before the lock engages (0 when locked).</summary>
    public int AttemptsRemaining
    {
        get { lock (_gate) return Math.Max(0, MaxFailedAttempts - _failed); }
    }

    /// <summary>The storage key this install/profile uses — machine identifier, safe to log.</summary>
    public string VerifierKey
    {
        get
        {
            var profile = _profileId();
            var tag = string.IsNullOrWhiteSpace(profile)
                ? "local"
                : ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(profile.Trim()))).Substring(0, 16);
            return VerifierKeyPrefix + tag;
        }
    }

    // ---- ILocalPasscodeService ---------------------------------------------------

    public Task<bool> SetAsync(string passcode, CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                if (string.IsNullOrEmpty(passcode) || passcode.Length < MinPasscodeLength) return false;

                var salt = RandomNumberGenerator.GetBytes(SaltBytes);
                var hash = Derive(passcode, salt);
                // Versioned, self-describing payload: an iteration bump can migrate old verifiers
                // instead of silently invalidating everyone's passcode.
                var payload = string.Concat(
                    Iterations.ToString(CultureInfo.InvariantCulture), ".", ToHex(salt), ".", ToHex(hash));
                _storage.SetAsync(VerifierKey, payload, CancellationToken.None)
                         .GetAwaiter().GetResult();
                lock (_gate) ResetFailures();
                return true;
            }, ct);

    public Task<bool> VerifyAsync(string passcode, CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                if (IsLocked) return false;               // no PBKDF2 while locked — see class comment
                var stored = GetBlocking();
                if (stored is null) return false;         // nothing set: never an implicit "ok"
                if (!TryParse(stored, out var iterations, out var salt, out var expected))
                {
                    // Unreadable verifier: fail closed and say nothing more than the bool.
                    return false;
                }
                if (string.IsNullOrEmpty(passcode)) { NoteFailure(); return false; }

                var actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(passcode), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
                bool ok = CryptographicOperations.FixedTimeEquals(actual, expected);
                CryptographicOperations.ZeroMemory(actual);
                if (ok) { lock (_gate) ResetFailures(); return true; }
                NoteFailure();
                return false;
            }, ct);

    public Task RemoveAsync(CancellationToken ct = default) =>
        Task.Run(
            () =>
            {
                _storage.RemoveAsync(VerifierKey, CancellationToken.None).GetAwaiter().GetResult();
                lock (_gate) ResetFailures();
            }, ct);

    /// <summary>Lock now, regardless of the failure count (app backgrounding / explicit user lock).</summary>
    public void Lock()
    {
        lock (_gate)
        {
            _failed = MaxFailedAttempts;
            _firstFailureUtc ??= _utcNow();
        }
    }

    /// <summary>Clear a lock engaged by <see cref="Lock"/> or by too many failures (the caller must
    /// have already proven knowledge of the passcode — the UI is the only caller and calls this
    /// after a successful verify).</summary>
    public void Unlock()
    {
        lock (_gate) ResetFailures();
    }

    // ---- internals ---------------------------------------------------------------

    private void NoteFailure()
    {
        lock (_gate)
        {
            var now = _utcNow();
            if (_firstFailureUtc is null || now - _firstFailureUtc.Value > LockoutWindow)
            {
                _firstFailureUtc = now;
                _failed = 0;
            }
            _failed++;
        }
    }

    private void ResetFailures()
    {
        _failed = 0;
        _firstFailureUtc = null;
    }

    private static byte[] Derive(string passcode, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passcode), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

    private string? GetBlocking() =>
        _storage.GetAsync(VerifierKey, CancellationToken.None).GetAwaiter().GetResult();

    private static bool TryParse(
        string payload, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0; salt = Array.Empty<byte>(); hash = Array.Empty<byte>();
        var parts = payload.Split('.');
        if (parts.Length != 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out iterations)) return false;
        if (iterations < 1_000 || iterations > 10_000_000) return false;   // refuse a nonsense work factor
        salt = FromHex(parts[1]); hash = FromHex(parts[2]);
        return salt.Length == SaltBytes && hash.Length == HashBytes;
    }

    private static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    private static byte[] FromHex(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (Exception) { return Array.Empty<byte>(); }
    }
}
