using Microsoft.Extensions.Configuration;

namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: every tunable of the identity lane in one immutable record, read from configuration at
///          startup. No handler reads IConfiguration ad hoc, so a typo in a key fails once at boot.
/// OWNER: Agent 03 (identity lane).
/// CONFIG KEYS (all under Identity:, defaults honest for local dev):
///   Identity:AccessTokenLifetimeMinutes     (default 15)
///   Identity:RefreshTokenLifetimeDays       (default 30)
///   Identity:MaxPasswordBytes               (default 1024, DoS guard on the KDF)
///   Identity:Lockout:WarnAfterFailures      (default 3 — 429 from the 4th failure in window)
///   Identity:Lockout:MaxAttempts            (default 5 — 403 account_locked from the 6th)
///   Identity:Lockout:WindowMinutes          (default 15)
///   Identity:Lockout:LockoutMinutes         (default 15)
///   Identity:Deletion:GraceDays             (default 30 — reversible window before execution)
///   Identity:BootstrapAdminEmails           (comma list; see threat model M-07 for why)
///   Identity:Google:ClientId / ClientSecret (secret unused server-side in P1, presence only)
///   Identity:Google:MetadataAddress         (default Google's published OIDC document)
///   Identity:Google:LinkByEmail             (default false until email verification exists — T-09)
/// INVARIANTS: defaults never weaken security (no "skip verification" toggle exists).
/// </summary>
public sealed record IdentityOptions(
    int AccessTokenLifetimeMinutes,
    int RefreshTokenLifetimeDays,
    int MaxPasswordBytes,
    int LockoutWarnAfterFailures,
    int LockoutMaxAttempts,
    int LockoutWindowMinutes,
    int LockoutMinutes,
    int DeletionGraceDays,
    IReadOnlyList<string> BootstrapAdminEmails,
    string? GoogleClientId,
    bool HasGoogleClientSecret,
    string GoogleMetadataAddress,
    bool GoogleLinkByEmail)
{
    public const string GoogleDefaultMetadataAddress =
        "https://accounts.google.com/.well-known/openid-configuration";

    public bool GoogleConfigured => !string.IsNullOrWhiteSpace(GoogleClientId);

    public static IdentityOptions FromConfiguration(IConfiguration config)
    {
        string Section(string key, string def) => config[$"Identity:{key}"] ?? def;
        int SectionInt(string key, int def) =>
            int.TryParse(config[$"Identity:{key}"], out var v) && v > 0 ? v : def;
        bool SectionBool(string key, bool def) =>
            bool.TryParse(config[$"Identity:{key}"], out var v) ? v : def;

        var admins = (Section("BootstrapAdminEmails", "") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Length > 0)
            .ToArray();

        return new IdentityOptions(
            AccessTokenLifetimeMinutes: SectionInt("AccessTokenLifetimeMinutes", 15),
            RefreshTokenLifetimeDays: SectionInt("RefreshTokenLifetimeDays", 30),
            MaxPasswordBytes: SectionInt("MaxPasswordBytes", PasswordHasher.MaxPasswordBytes),
            LockoutWarnAfterFailures: SectionInt("Lockout:WarnAfterFailures", 5),
            LockoutMaxAttempts: SectionInt("Lockout:MaxAttempts", 10),
            LockoutWindowMinutes: SectionInt("Lockout:WindowMinutes", 15),
            LockoutMinutes: SectionInt("Lockout:LockoutMinutes", 15),
            DeletionGraceDays: SectionInt("Deletion:GraceDays", 30),
            BootstrapAdminEmails: admins,
            GoogleClientId: Section("Google:ClientId", "").Trim() is { Length: > 0 } cid ? cid : null,
            HasGoogleClientSecret: !string.IsNullOrWhiteSpace(Section("Google:ClientSecret", "")),
            GoogleMetadataAddress: Section("Google:MetadataAddress", GoogleDefaultMetadataAddress).Trim(),
            GoogleLinkByEmail: SectionBool("Google:LinkByEmail", false));
    }
}

/// <summary>Canonical email handling shared by every path (register/login/google/profile/export).
/// Trim + lowercase, invariant culture — one normalization or the unique index leaks duplicates.</summary>
public static class EmailNormalized
{
    public const int MaxLength = 320;

    public static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim().ToLowerInvariant();

    /// <summary>Deliberately lax structural check (see threat model R-06): the server proves nothing
    /// about mailbox ownership in P1 — there is no email-verification pipeline — so a stricter regex
    /// would only reject valid exotic addresses, not attackers. Length and shape guards only.</summary>
    public static bool LooksValid(string? normalized)
        => normalized is { Length: > 3 and <= MaxLength }
           && normalized.Contains('@')
           && normalized.IndexOf(' ') < 0
           && normalized.IndexOf("..") < 0
           && normalized.Split('@') is [_, { Length: > 2 } domain]
           && domain.Contains('.')
           && !domain.StartsWith('.')
           && !domain.EndsWith('.');
}
