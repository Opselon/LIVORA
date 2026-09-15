namespace Livora.Server.Modules.Identity;

// ============================================================================
// Wire DTOs for the frozen §5c identity contract. camelCase comes from the
// shared JsonSerializerDefaults.Web options; the record property names below
// ARE the JSON names. P1-D codes against these exact shapes — do not rename.
// ============================================================================

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName, string? Locale);

public sealed record LoginRequest(string? Email, string? Password, string? DeviceLabel, string? Platform);

public sealed record GoogleLoginRequest(string? IdToken, string? DeviceLabel, string? Platform);

public sealed record RefreshRequest(string? RefreshToken);

/// <summary>The shared 200/201 token response of CONTRACT-P1 §5c — register, login, google, refresh.</summary>
public sealed record TokenResponse(
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAtUtc,
    string SessionId);

public sealed record SessionInfo(
    string SessionId,
    string? DeviceLabel,
    string? Platform,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastUsedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    bool IsCurrent);

public sealed record AccountInfo(
    string UserId,
    string? Email,
    string DisplayName,
    string Locale,
    string Tier,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastLoginAtUtc);

public sealed record DeletionRequestResponse(
    DateTimeOffset DeletionRequestedAtUtc,
    DateTimeOffset ScheduledForUtc,
    DateTimeOffset ReversibleUntilUtc);

/// <summary>A §5c export section: provenance first ("server" | "not_implemented"), then the data.
/// Empty-and-honest beats fabricated — product law I.</summary>
public sealed record ExportSection(string Source, object? Data);

public sealed record AccountExport(
    DateTimeOffset ExportedAtUtc,
    ExportSection Profile,
    ExportSection Goals,
    ExportSection Habits,
    ExportSection Plans,
    ExportSection History,
    ExportSection ConnectedDataMetadata,
    ExportSection Purchases,
    ExportSection CommunityContent);
