namespace Livora.Server.Application;

/// <summary>
/// PURPOSE: the only authorised way to name an authorization policy or role on the LIVORA backend.
///          Frozen in the scaffold so no lane ever writes a policy string literal, and so a route
///          written in lane C and a policy registered in lane B cannot drift apart silently.
/// OWNER: Agent 01 (contract). Amendments are a lead-only PR; lanes file a request instead.
/// PROVIDES: policy names used by <c>RequireAuthorization(...)</c> in every module.
/// INVARIANTS:
///   - a route may not invent a policy name; an unknown name fails at startup, which is the point
///   - "server-authoritative" (Wave 4 §54): entitlement, creator status, moderation and verification
///     decisions are checked here or in the handler — never trusted from a client-supplied flag
/// EXTEND: request a new policy in INTEGRATION_REQUESTS with the exact capability it guards.
/// </summary>
public static class Policies
{
    /// <summary>Any authenticated account. The default for personal data.</summary>
    public const string SignedIn = "livora.signed_in";

    /// <summary>Owns the resource being touched — handlers must still compare the row's owner id.
    /// This policy is the intent marker; ownership assertions belong in the handler (IDOR gate).</summary>
    public const string OwnsResource = "livora.owns_resource";

    /// <summary>Approved creator publishing/editing their own programs.</summary>
    public const string Creator = "livora.creator";

    /// <summary>Entitlement check for paid features (server-side, from the entitlement table).</summary>
    public const string Entitled = "livora.entitled";

    /// <summary>Moderator actions: review reports, remove content, resolve appeals.</summary>
    public const string Moderator = "livora.moderator";

    /// <summary>Operator actions: creator approval, tier changes, data-deletion execution.</summary>
    public const string Admin = "livora.admin";

    /// <summary>All policy names, for the startup assertion that none is unregistered.</summary>
    public static readonly IReadOnlyList<string> All =
        [SignedIn, OwnsResource, Creator, Entitled, Moderator, Admin];
}

/// <summary>Role names carried in the auth token. Deliberately few — capabilities do the rest.</summary>
public static class Roles
{
    public const string User = "user";
    public const string Creator = "creator";
    public const string Moderator = "moderator";
    public const string Admin = "admin";

    public static readonly IReadOnlyList<string> All = [User, Creator, Moderator, Admin];
}

/// <summary>
/// PURPOSE: stable keys for every backend capability the client may render a connector/status screen
///          for. One list so the client, the docs matrix and /api/v1/platform/capabilities agree.
/// OWNER: Agent 01. A lane adds a key by request; it never invents one in a handler.
/// INVARIANTS: keys are lowercase snake_case and never reused after retirement (retire = remove the
///             const and note it in the ADR, so an old client cannot read a new meaning into it).
/// </summary>
public static class CapabilityKeys
{
    public const string Platform = "platform";
    public const string Sync = "sync";
    public const string Identity = "identity";
    public const string Intelligence = "intelligence";
    public const string Verification = "verification";
    public const string Health = "health";
    public const string Calendar = "calendar";
    public const string ScreenTime = "screen_time";
    public const string Nutrition = "nutrition";
    public const string Personalization = "personalization";
    public const string Marketplace = "marketplace";
    public const string Community = "community";
    public const string Commerce = "commerce";

    public static readonly IReadOnlyList<string> All =
    [
        Platform, Sync, Identity, Intelligence, Verification, Health, Calendar, ScreenTime,
        Nutrition, Personalization, Marketplace, Community, Commerce,
    ];
}
