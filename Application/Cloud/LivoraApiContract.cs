using System.Text.Json;
using System.Text.Json.Serialization;

namespace LIVORA.Application.Cloud;

/// <summary>
/// WAVE 4 P1-D — the machine codes of the frozen §5c server contract, mirrored CLIENT-side.
///
/// Why a mirror instead of a project reference: <c>server/src/Livora.Server.Application/ApiProblem.cs</c>
/// is a frozen lead file and the MAUI client does not (and must not) reference the ASP.NET host
/// assembly. The values below are copied verbatim from it and from §5c, and
/// <c>Tests/Tests/Wave4ClientSeam/CloudContractMirrorTests.cs</c> reads that frozen file FROM DISK and
/// asserts the two sets agree — so a rename on either side turns the client suite red instead of
/// shipping a table that silently stops matching the server.
///
/// The client switches on these codes and localizes by them (product law: never parse prose).
/// </summary>
public static class LivoraApiCodes
{
    /// <summary><c>ApiProblem.Type</c> is <c>https://livora.app/problems/&lt;code&gt;</c> — one place.</summary>
    public const string ProblemTypeNamespace = "https://livora.app/problems/";

    // ---- transport / validation -------------------------------------------------------------
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string IdempotencyKeyReuseMismatch = "idempotency_key_reuse_mismatch";
    public const string InternalError = "internal_error";

    // ---- identity / authorization -----------------------------------------------------------
    public const string Unauthenticated = "unauthenticated";
    public const string InvalidCredentials = "invalid_credentials";
    public const string TokenExpired = "token_expired";
    public const string TokenRevoked = "token_revoked";
    public const string Forbidden = "forbidden";
    public const string AccountLocked = "account_locked";
    public const string EmailAlreadyRegistered = "email_already_registered";
    public const string RecoveryTokenInvalid = "recovery_token_invalid";
    public const string DeletionPending = "deletion_pending";

    // ---- capability / integration truthfulness ----------------------------------------------
    public const string ProviderUnconfigured = "provider_unconfigured";
    public const string PermissionRequired = "permission_required";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string DataStale = "data_stale";

    // ---- intelligence / AI -------------------------------------------------------------------
    public const string AiUnavailable = "ai_unavailable";
    public const string AiOutputRejected = "ai_output_rejected";
    public const string ContextBudgetExceeded = "context_budget_exceeded";
    public const string VerificationRuleUnknown = "verification_rule_unknown";

    // ---- commerce / marketplace --------------------------------------------------------------
    public const string EntitlementRequired = "entitlement_required";
    public const string PaymentProviderUnconfigured = "payment_provider_unconfigured";
    public const string WebhookSignatureInvalid = "webhook_signature_invalid";
    public const string PurchaseAlreadyExists = "purchase_already_exists";
    public const string RefundNotPermissible = "refund_not_permissible";
    public const string CreatorNotApproved = "creator_not_approved";
    public const string ProgramNotPublished = "program_not_published";

    // ---- community / moderation ---------------------------------------------------------------
    public const string ContentRemoved = "content_removed";
    public const string BlockedByParticipant = "blocked_by_participant";
    public const string ReportAlreadyOpen = "report_already_open";

    // ---- sync ---------------------------------------------------------------------------------
    public const string VersionConflict = "version_conflict";
    public const string SyncTooLarge = "sync_too_large";

    /// <summary>
    /// Every mirrored code. The mirror test asserts this set equals the server's constant set, and
    /// the client-side status surface asserts it never renders a code that is absent from it.
    /// </summary>
    public static IReadOnlyCollection<string> All { get; } = new[]
    {
        ValidationFailed, NotFound, Conflict, RateLimited, IdempotencyKeyReuseMismatch, InternalError,
        Unauthenticated, InvalidCredentials, TokenExpired, TokenRevoked, Forbidden, AccountLocked,
        EmailAlreadyRegistered, RecoveryTokenInvalid, DeletionPending,
        ProviderUnconfigured, PermissionRequired, ProviderUnavailable, DataStale,
        AiUnavailable, AiOutputRejected, ContextBudgetExceeded, VerificationRuleUnknown,
        EntitlementRequired, PaymentProviderUnconfigured, WebhookSignatureInvalid, PurchaseAlreadyExists,
        RefundNotPermissible, CreatorNotApproved, ProgramNotPublished,
        ContentRemoved, BlockedByParticipant, ReportAlreadyOpen,
        VersionConflict, SyncTooLarge,
    };

    /// <summary>True for a code this build knows how to localize.</summary>
    public static bool IsKnown(string? code) =>
        !string.IsNullOrWhiteSpace(code) && All.Contains(code, StringComparer.Ordinal);

    /// <summary>
    /// Recover the code from an <c>ApiProblem.Type</c> URI when the body omitted or damaged
    /// <c>code</c> (defensive: the URI and the code are produced from one constant server-side).
    /// Returns null for anything that is not in the known namespace.
    /// </summary>
    public static string? FromProblemType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (!type.StartsWith(ProblemTypeNamespace, StringComparison.Ordinal)) return null;
        var tail = type[ProblemTypeNamespace.Length..];
        return tail.Length == 0 || tail.Contains('/') ? null : tail;
    }

    /// <summary>
    /// The localization key for one machine code: <c>Api.Error.&lt;code&gt;</c> (dots, never spaces —
    /// the same key grammar every other layer emits). Unknown codes deliberately collapse to
    /// <see cref="UnknownReasonKey"/> so an un-mirrored server code can never print raw prose or a
    /// Persian sentence with an English code embedded in it.
    /// </summary>
    public const string UnknownReasonKey = "Api.Error.unknown";

    public static string ReasonKeyFor(string? code) =>
        IsKnown(code) ? "Api.Error." + code! : UnknownReasonKey;
}

/// <summary>
/// Wave 4 P1-D: the endpoint paths of the frozen §5c contract, in one table so no call site can
/// drift. Every path is relative to <see cref="ApiPrefix"/> and is exactly the server's surface —
/// nothing here is invented, and anything §5c does not define does not exist in this client.
/// </summary>
public static class LivoraApiPaths
{
    /// <summary>The versioned prefix the host enforces (<c>FlivoraEndpointContext.VersionPrefix</c>).</summary>
    public const string ApiPrefix = "/api/v1";

    public const string Register = ApiPrefix + "/auth/register";
    public const string Login = ApiPrefix + "/auth/login";
    public const string Google = ApiPrefix + "/auth/google";
    public const string Refresh = ApiPrefix + "/auth/refresh";
    public const string Logout = ApiPrefix + "/auth/logout";
    public const string Sessions = ApiPrefix + "/auth/sessions";
    public static string Session(string id) => Sessions + "/" + Uri.EscapeDataString(id);
    public const string Account = ApiPrefix + "/account";
    public const string DeleteRequests = ApiPrefix + "/account/delete-requests";
    public const string Export = ApiPrefix + "/account/export";
    public const string SyncBatch = ApiPrefix + "/sync/batch";
    public const string SyncChanges = ApiPrefix + "/sync/changes";
    public const string Capabilities = ApiPrefix + "/platform/capabilities";
}

/// <summary>
/// Wave 4 P1-D: the one <see cref="JsonSerializerOptions"/> the seam serializes and deserializes
/// with. It is <c>JsonSerializerDefaults.Web</c> — which is what the server's
/// <c>Problems.Json</c> uses — so camelCase property naming, case-insensitive reads and
/// <c>number</c> handling for <c>DateTimeOffset</c> match the backend byte for byte.
///
/// <c>DefaultIgnoreCondition = WhenWritingNull</c> matters for §5c's optional request fields
/// (<c>displayName?</c>, <c>locale?</c>, <c>deviceLabel?</c>, <c>platform?</c>): an omitted field is
/// ABSENT from the body, never <c>null</c>, so a null locale cannot be read server-side as "clear
/// my locale".
/// </summary>
public static class LivoraJson
{
    public static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Persian display names / note payloads must survive as UTF-8 text, not \uXXXX noise.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // .NET 10 ships reflection-based serialization OFF by default, where MakeReadOnly()
            // throws without a resolver; naming the same resolver the plain-default would use keeps
            // this one options object legal in every head that builds the seam.
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        };
        o.MakeReadOnly();
        return o;
    }
}

// ============================================================================================
// DTOs — §5c verbatim. Property names are pinned with JsonPropertyName (not left to a naming
// policy) because this table IS the contract: a refactor that renames a C# property must break a
// test, not the wire format. Every field is exactly a field §5c defines; no extras.
// ============================================================================================

/// <summary>The 201/200 body of register/login/google/refresh (§5c). One shape, four endpoints.</summary>
public sealed record AuthTokensDto(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("sessionId")] string SessionId);

public sealed record RegisterRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("locale")] string? Locale = null);

public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("deviceLabel")] string? DeviceLabel = null,
    [property: JsonPropertyName("platform")] string? Platform = null);

public sealed record GoogleLoginRequest(
    [property: JsonPropertyName("idToken")] string IdToken,
    [property: JsonPropertyName("deviceLabel")] string? DeviceLabel = null,
    [property: JsonPropertyName("platform")] string? Platform = null);

public sealed record RefreshTokenRequest(
    [property: JsonPropertyName("refreshToken")] string RefreshToken);

/// <summary>One row of <c>GET /auth/sessions</c>.</summary>
public sealed record SessionInfoDto(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("deviceLabel")] string? DeviceLabel,
    [property: JsonPropertyName("platform")] string? Platform,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset CreatedAtUtc,
    [property: JsonPropertyName("lastUsedAtUtc")] DateTimeOffset? LastUsedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("isCurrent")] bool IsCurrent);

/// <summary>One row of <c>GET /account</c>. Nullable where Phase 1 may honestly return nothing.</summary>
public sealed record AccountInfoDto(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("locale")] string? Locale,
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("createdAtUtc")] DateTimeOffset? CreatedAtUtc,
    [property: JsonPropertyName("lastLoginAtUtc")] DateTimeOffset? LastLoginAtUtc);

/// <summary>Body of <c>POST /account/delete-requests</c>.</summary>
public sealed record DeleteRequestDto(
    [property: JsonPropertyName("deletionRequestedAtUtc")] DateTimeOffset DeletionRequestedAtUtc,
    [property: JsonPropertyName("scheduledForUtc")] DateTimeOffset ScheduledForUtc,
    [property: JsonPropertyName("reversibleUntilUtc")] DateTimeOffset ReversibleUntilUtc);

/// <summary>
/// Body of <c>GET /account/export</c>. Every section stays raw JSON on purpose: §5c says a section
/// may be <c>{"source":"not_implemented"}</c>, and re-materialising it into a client model would
/// invent a schema the server has not frozen yet. <see cref="SectionSource"/> reads the provenance
/// marker so the UI can label each section "server" vs "not implemented" — never both, never a
/// fabricated count.
/// </summary>
public sealed record AccountExportDto(
    [property: JsonPropertyName("exportedAtUtc")] DateTimeOffset ExportedAtUtc,
    [property: JsonPropertyName("profile")] JsonElement Profile,
    [property: JsonPropertyName("goals")] JsonElement Goals,
    [property: JsonPropertyName("habits")] JsonElement Habits,
    [property: JsonPropertyName("plans")] JsonElement Plans,
    [property: JsonPropertyName("history")] JsonElement History,
    [property: JsonPropertyName("connectedDataMetadata")] JsonElement ConnectedDataMetadata,
    [property: JsonPropertyName("purchases")] JsonElement Purchases,
    [property: JsonPropertyName("communityContent")] JsonElement CommunityContent)
{
    /// <summary>The §5c top-level keys, in contract order (everything but the timestamp).</summary>
    public static IReadOnlyList<string> Sections { get; } =
        new[] { "profile", "goals", "habits", "plans", "history", "connectedDataMetadata", "purchases", "communityContent" };

    public JsonElement Section(string key) => key switch
    {
        "profile" => Profile,
        "goals" => Goals,
        "habits" => Habits,
        "plans" => Plans,
        "history" => History,
        "connectedDataMetadata" => ConnectedDataMetadata,
        "purchases" => Purchases,
        "communityContent" => CommunityContent,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Not a §5c export section."),
    };

    /// <summary>
    /// The provenance marker of one section: <c>"server"</c>, <c>"not_implemented"</c>, or
    /// <c>"unlabelled"</c> when the server omitted it. The absence of a marker is reported as
    /// absence — it is never guessed into "server".
    /// </summary>
    public string SectionSource(string key)
    {
        var el = Section(key);
        if (el.ValueKind != JsonValueKind.Object) return Unlabelled;
        if (!el.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.String)
            return Unlabelled;
        var v = src.GetString();
        return v switch
        {
            "server" => "server",
            "not_implemented" => "not_implemented",
            null or "" => Unlabelled,
            // An unrecognised marker is surfaced verbatim in the machine channel only; the UI maps
            // anything unknown to its "unlabelled" row, so a new server value can never render as
            // a trusted "server" section by accident.
            _ => v,
        };
    }

    public const string Server = "server";
    public const string NotImplemented = "not_implemented";
    public const string Unlabelled = "unlabelled";

    /// <summary>
    /// True only when the section says <c>source: server</c> AND carries at least one non-empty
    /// payload property. An unlabelled or <c>not_implemented</c> section is empty here even if the
    /// server sent bytes: "not implemented" plus a stray array is a contradiction the client refuses
    /// to resolve in favour of showing data (§0.1 — no fabricated content).
    /// </summary>
    public bool HasServerData(string key)
    {
        if (SectionSource(key) != Server) return false;
        var el = Section(key);
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (string.Equals(p.Name, "source", StringComparison.Ordinal)) continue;
            if (p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() == 0) continue;
            if (p.Value.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(p.Value.GetString())) continue;
            return true;
        }
        return false;
    }
}

// ---- sync (§5c batch + changes) --------------------------------------------------------------

/// <summary>One entry of <c>POST /sync/batch</c>'s <c>operations</c> array.</summary>
public sealed record SyncOperationDto(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("entityType")] string EntityType,
    [property: JsonPropertyName("entityId")] string EntityId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("baseRevision")] long? BaseRevision,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

/// <summary>
/// The <c>kind</c> vocabulary. §5c names the field but does not freeze its enum; these three are the
/// minimum a local-first outbox can honestly express (create/update/delete) and are mirrored as a
/// request for P1-B to confirm (<c>R-P1D-3</c>). Unknown values are never sent.
/// </summary>
public static class SyncOperationKinds
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";

    public static bool IsKnown(string? kind) =>
        kind is Create or Update or Delete;
}

public sealed record SyncBatchRequest(
    [property: JsonPropertyName("operations")] IReadOnlyList<SyncOperationDto> Operations);

/// <summary>The <c>outcome</c> vocabulary of one batch result (§5c: applied|conflict|rejected|duplicate).</summary>
public static class SyncOutcomes
{
    public const string Applied = "applied";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
    public const string Duplicate = "duplicate";

    public static bool IsKnown(string? outcome) =>
        outcome is Applied or Conflict or Rejected or Duplicate;
}

public sealed record SyncConflictDto(
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("remoteRevision")] long? RemoteRevision);

public sealed record SyncOperationResultDto(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("resultRevision")] long? ResultRevision,
    [property: JsonPropertyName("conflict")] SyncConflictDto? Conflict);

public sealed record SyncBatchResponseDto(
    [property: JsonPropertyName("results")] IReadOnlyList<SyncOperationResultDto> Results,
    [property: JsonPropertyName("serverTimeUtc")] DateTimeOffset ServerTimeUtc);

public sealed record SyncChangeDto(
    [property: JsonPropertyName("revision")] long? Revision,
    [property: JsonPropertyName("entityType")] string? EntityType,
    [property: JsonPropertyName("entityId")] string? EntityId,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("payload")] JsonElement? Payload);

public sealed record SyncChangesResponseDto(
    [property: JsonPropertyName("changes")] IReadOnlyList<SyncChangeDto> Changes,
    [property: JsonPropertyName("latestRevision")] long LatestRevision,
    [property: JsonPropertyName("hasMore")] bool HasMore);

// ---- error envelope ---------------------------------------------------------------------------

/// <summary>
/// The frozen problem envelope (<c>ApiProblem</c>): <c>type,title,status,detail,instance,code,
/// correlationId,errors</c>. <see cref="CorrelationId"/> is the only identifier the client may log or
/// display (§0.5 privacy law); <see cref="Detail"/> is server prose and is therefore never rendered
/// — every message the user sees comes from <see cref="LivoraApiCodes.ReasonKeyFor"/>.
/// </summary>
public sealed record ApiProblemDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("instance")] string? Instance,
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("errors")] IReadOnlyDictionary<string, string[]>? Errors)
{
    /// <summary>
    /// The effective code: the body's <c>code</c> when present, else the <c>type</c> URI tail, else
    /// null. Never a guess — null means "the server sent no machine code", which the result type
    /// reports as <see cref="LivoraApiCodes.UnknownReasonKey"/>.
    /// </summary>
    public string? EffectiveCode =>
        !string.IsNullOrWhiteSpace(Code) ? Code : LivoraApiCodes.FromProblemType(Type);
}

/// <summary>
/// What one typed call returned. There is exactly one channel for success and one for failure —
/// never "200 with an error body" (which the server also guarantees), and never an exception on the
/// expected-failure paths: an offline device, a 409 and a 401 are all values, so no UI call site has
/// to wrap a network call in a try/catch to stay alive (product law 3).
/// </summary>
public sealed class LivoraApiResult<T>
{
    public bool Ok { get; private init; }
    /// <summary>HTTP status; 0 means the request never got an answer (DNS/connect/timeout/cancel).</summary>
    public int Status { get; private init; }
    public T? Value { get; private init; }
    public ApiProblemDto? Problem { get; private init; }

    /// <summary>Machine code (§5c) or a client-side transport tag. Null only on success.</summary>
    public string? Code { get; private init; }

    /// <summary>
    /// The localization key for this outcome — always set on failure, always a dotted key, never
    /// prose. Un-mirrored codes collapse to <see cref="LivoraApiCodes.UnknownReasonKey"/>.
    /// </summary>
    public string ErrorReasonKey { get; private init; } = "";

    public string? CorrelationId { get; private init; }

    /// <summary>Field-level validation messages keyed by field (already localized by code, not prose).</summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; private init; }

    public bool IsNetworkFailure => Code == LivoraApiTransportCodes.Network
                                    || Code == LivoraApiTransportCodes.Timeout;

    public bool IsUnauthorized => Status == 401 || Code == LivoraApiCodes.Unauthenticated
                                  || Code == LivoraApiCodes.TokenExpired
                                  || Code == LivoraApiCodes.TokenRevoked;

    /// <summary>Worth retrying later unchanged (offline, 429, 5xx, provider unconfigured).</summary>
    public bool IsTransient =>
        IsNetworkFailure || Status == 429 || Status == 503 || Status >= 500;

    public static LivoraApiResult<T> Success(T value, int status = 200, string? correlationId = null) =>
        new() { Ok = true, Value = value, Status = status, CorrelationId = correlationId };

    public static LivoraApiResult<T> Failure(
        int status, string? code, ApiProblemDto? problem, string? correlationId,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null) =>
        new()
        {
            Ok = false,
            Status = status,
            Code = code,
            Problem = problem,
            CorrelationId = correlationId ?? problem?.CorrelationId,
            FieldErrors = fieldErrors ?? problem?.Errors,
            ErrorReasonKey = LivoraApiCodes.ReasonKeyFor(code),
        };

    /// <summary>A transport-level failure with no HTTP answer at all.</summary>
    public static LivoraApiResult<T> TransportFailure(string transportCode, string? detailCategory = null) =>
        new()
        {
            Ok = false,
            Status = 0,
            Code = transportCode,
            ErrorReasonKey = LivoraApiTransportCodes.ReasonKeyFor(transportCode),
            Problem = null,
            CorrelationId = null,
            FieldErrors = null,
        };

    /// <summary>Re-type a failure (auth layer returns a session-typed failure to a value-typed caller).</summary>
    public LivoraApiResult<U> AsFailure<U>() => new()
    {
        Ok = false,
        Status = Status,
        Code = Code,
        Problem = Problem,
        CorrelationId = CorrelationId,
        FieldErrors = FieldErrors,
        ErrorReasonKey = ErrorReasonKey,
    };
}

/// <summary>
/// Client-side outcome tags for the cases where the server never answered. They live in the same
/// namespace as the server codes but with a distinct prefix so a log line can never be read as
/// "the backend said this".
/// </summary>
public static class LivoraApiTransportCodes
{
    public const string Network = "network";
    public const string Timeout = "timeout";
    public const string Canceled = "canceled";
    /// <summary>2xx with a body that is not the contract's shape (or not parseable).</summary>
    public const string MalformedResponse = "malformed_response";
    /// <summary>No base URL configured — the whole cloud seam is off, and that is a fact, not an error.</summary>
    public const string NotConfigured = "not_configured";
    /// <summary>A protected call was attempted with no usable session.</summary>
    public const string NotSignedIn = "not_signed_in";
    /// <summary>Malformed base URL (the one user-entered value the seam reads).</summary>
    public const string InvalidBaseUrl = "invalid_base_url";
    /// <summary>A status the seam cannot classify (no envelope, unexpected 2xx shape).</summary>
    public const string UnexpectedResponse = "unexpected_response";

    public static bool IsKnown(string? code) => code is Network or Timeout or Canceled
        or MalformedResponse or NotConfigured or NotSignedIn or InvalidBaseUrl or UnexpectedResponse;

    public static string ReasonKeyFor(string code) => code switch
    {
        Network => "Api.Error.network",
        Timeout => "Api.Error.timeout",
        Canceled => "Api.Error.canceled",
        MalformedResponse => "Api.Error.malformed_response",
        NotConfigured => "Api.Error.not_configured",
        NotSignedIn => "Api.Error.not_signed_in",
        InvalidBaseUrl => "Api.Error.invalid_base_url",
        UnexpectedResponse => "Api.Error.unexpected_response",
        _ => LivoraApiCodes.UnknownReasonKey,
    };
}
