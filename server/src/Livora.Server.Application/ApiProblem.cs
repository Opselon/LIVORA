namespace Livora.Server.Application;

/// <summary>
/// PURPOSE: the single error envelope every /api/v1 endpoint returns, so the bilingual client can
///          render a correct message without parsing prose.
/// OWNER: Agent 01 (frozen). Agents 02/03/14/16 read it; nobody edits the values without a lead PR.
/// CONSUMES: nothing.
/// PROVIDES: ProblemCodes (stable machine codes) + ProblemDetails-shaped DTO used by the host.
/// INVARIANTS:
///   - Codes are stable forever: renaming one is a breaking change (client switches on them).
///   - The client localizes by code, never by message text.
///   - No raw exception text, no SQL, no file paths, no tokens, never in <see cref="Detail"/>.
/// EXTEND: a new code is additive — append it to the right group with a one-line meaning.
/// </summary>
public static class ProblemCodes
{
    // ---- transport / validation -------------------------------------------------------------
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string RateLimited = "rate_limited";
    public const string IdempotencyKeyReuseMismatch = "idempotency_key_reuse_mismatch";
    /// <summary>Catch-all for a fault the client cannot act on; the detail is always generic.</summary>
    public const string InternalError = "internal_error";

    // ---- identity / authorization (Agent 03) ------------------------------------------------
    public const string Unauthenticated = "unauthenticated";
    public const string InvalidCredentials = "invalid_credentials";
    public const string TokenExpired = "token_expired";
    public const string TokenRevoked = "token_revoked";
    public const string Forbidden = "forbidden";
    public const string AccountLocked = "account_locked";
    public const string EmailAlreadyRegistered = "email_already_registered";
    public const string RecoveryTokenInvalid = "recovery_token_invalid";
    public const string DeletionPending = "deletion_pending";

    // ---- capability / integration truthfulness (Agents 04-07) -------------------------------
    public const string ProviderUnconfigured = "provider_unconfigured";
    public const string PermissionRequired = "permission_required";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string DataStale = "data_stale";

    // ---- intelligence / AI (Agents 09-11) ---------------------------------------------------
    public const string AiUnavailable = "ai_unavailable";
    public const string AiOutputRejected = "ai_output_rejected";
    public const string ContextBudgetExceeded = "context_budget_exceeded";
    public const string VerificationRuleUnknown = "verification_rule_unknown";

    // ---- commerce / marketplace (Agents 12-14) ----------------------------------------------
    public const string EntitlementRequired = "entitlement_required";
    public const string PaymentProviderUnconfigured = "payment_provider_unconfigured";
    public const string WebhookSignatureInvalid = "webhook_signature_invalid";
    public const string PurchaseAlreadyExists = "purchase_already_exists";
    public const string RefundNotPermissible = "refund_not_permissible";
    public const string CreatorNotApproved = "creator_not_approved";
    public const string ProgramNotPublished = "program_not_published";

    // ---- community / moderation (Agent 13) --------------------------------------------------
    public const string ContentRemoved = "content_removed";
    public const string BlockedByParticipant = "blocked_by_participant";
    public const string ReportAlreadyOpen = "report_already_open";

    // ---- sync (Agent 02) --------------------------------------------------------------------
    public const string VersionConflict = "version_conflict";
    public const string SyncTooLarge = "sync_too_large";
}

/// <summary>Machine-readable problem body. RFC-9457 shaped with one extra field: <c>code</c>.</summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string CorrelationId,
    IReadOnlyDictionary<string, string[]>? Errors = null);
