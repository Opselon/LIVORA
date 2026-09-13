using LIVORA.Domain.Enums;

namespace LIVORA.Application.Abstractions;

// =============================================================================
// Wave 3c (master) — accounts/auth seam + local data portability.
// Owned by the Integration Lead. HONESTY CONTRACT: this wave ships the seam +
// local-only implementations. No fake remote login may ever be presented as
// real: IAuthProviderFactory reports NotAvailable until a backend exists, and
// the UI must render exactly that.
// =============================================================================

public enum AuthMode
{
    /// <summary>Everything stays on-device; no account. Default and only REAL mode today.</summary>
    LocalOnly = 0,
    /// <summary>Local account (PIN/password) protecting app access — real, device-side only.</summary>
    LocalPasscode = 1,
    /// <summary>Cloud account — CONTRACT SLOT ONLY. No backend ships in wave 3c.</summary>
    CloudAccount = 2,
}

/// <summary>Local device-passcode state. Implementations hash (never store plaintext), use platform secure storage.</summary>
public interface ILocalPasscodeService
{
    bool IsSet { get; }
    Task<bool> SetAsync(string passcode, CancellationToken ct = default);
    Task<bool> VerifyAsync(string passcode, CancellationToken ct = default);
    Task RemoveAsync(CancellationToken ct = default);
    /// <summary>True when the app is currently locked this session.</summary>
    bool IsLocked { get; }
    void Lock();
}

/// <summary>
/// Cloud-auth seam for FUTURE waves. The built-in implementation returns NotAvailable with a
/// reason key — it must be composed and displayed honestly, never silently skipped.
/// </summary>
public interface ICloudAuthService
{
    bool IsBackendConfigured { get; }              // false until wave 4+ — UI reads this, never guesses
    string StatusReasonKey { get; }                // localization key explaining WHY it is unavailable
    Task<bool> SignInAsync(string email, string password, CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);
}

// -----------------------------------------------------------------------------
// Full local-database editing (user requested: "change every variable, localdb")
// -----------------------------------------------------------------------------

/// <summary>
/// Inspect/edit ANY stored entity by kind through the registered repositories.
/// Write path is the same one all UI editors use: validate -> version bump -> save ->
/// sync-metadata Pending. Advanced screen is gated behind an "advanced mode" flag.
/// </summary>
public interface ILocalDataCatalogService
{
    /// <summary>Entity kinds available ("goals", "habits", "history", "manual", "settings", "consents", ...).</summary>
    Task<IReadOnlyList<string>> GetKindsAsync(CancellationToken ct = default);
    Task<string> ExportEntityAsync(string kind, string id, CancellationToken ct = default);
    /// <summary>Import full JSON for one entity; validates schema + range before writing. Returns error keys on reject.</summary>
    Task<IReadOnlyList<string>> ImportEntityAsync(string kind, string json, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DeleteEntityAsync(string kind, IEnumerable<string> ids, CancellationToken ct = default);
    /// <summary>Whole-store export/import (privacy "export my data" + backup). JSON, UTF-8, stable key order.</summary>
    Task<string> ExportAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> ImportAllAsync(string json, bool merge, CancellationToken ct = default);
}
