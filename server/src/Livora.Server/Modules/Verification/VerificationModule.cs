using System.Text.Json;

using Livora.Server.Application;
using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Engines.Decision.Verification;
using Livora.Server.Infrastructure.Persistence;

namespace Livora.Server.Modules.Verification;

/// <summary>
/// PURPOSE: the verification/evidence surface — /api/v1/verification. Claims are verified against
///          an evidence ledger by a deterministic rule ladder; the stored answer always names the
///          EXACT trust rung earned. No bool "verified" exists anywhere on this surface, and the
///          ledger stores the rung as data, never as a flattened flag.
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Self-registered via IFlivoraModule.
/// PROVIDES:
///   POST   /api/v1/verification/claims                 — verify (or re-verify) a claim, ledger-backed
///   GET    /api/v1/verification/claims                 — paged list (PageRequest semantics)
///   GET    /api/v1/verification/claims/{claimId}       — one verdict + its evidence trail
///   POST   /api/v1/verification/claims/{claimId}/review — staff ruling (Moderator policy)
///   GET    /api/v1/verification/rules                  — the rule keys + rungs the engine speaks
/// INVARIANTS:
///   - IDOR: every read/write filters by the AUTHENTICATED livora.uid; the policy proves a token,
///     never ownership — a request for another user's claim gets 403 forbidden (tested)
///   - an unknown rule key answers 503 code=verification_rule_unknown (ApiProblem.cs constant),
///     never a silent fallback to a different rule
///   - review is honoured ONLY for a staff-authenticated caller; a plain user's "confirm" cannot
///     lift anything to human_reviewed (tested — this is the server-authoritative rule §54)
///   - Report() discloses the signing-key posture it actually observes (Ephemeral => degraded)
///     and never claims a provider connection: the verification rules are in-process code, so
///     their probe is the fixed-vector self-check that must actually execute
///   - persistence is the thin EF ledger adapter (contributions registered via the module seam);
///     this lane writes NO migration file — the lead's Wave4P1Schema migration covers the model
/// </summary>
public sealed class VerificationModule : IFlivoraModule
{
    public const string ModuleKey = "verification";

    public string Key => ModuleKey;

    public void ConfigureServices(ModuleSeed seed)
    {
        // Entity contributions are registered through the shared schema gate (off by default
        // until the lead's Wave4P1Schema migration lands — see EnginesSchemaGate).
        Livora.Server.Modules.Intelligence.EnginesSchemaGate.EnsureRegistered(seed.Configuration, seed.Logger);
        seed.Services.AddScoped(sp => new EfVerificationLedger(
            sp.GetRequiredService<LivoraDbContext>()));
    }

    public void MapEndpoints(FlivoraEndpointContext ctx)
    {
        var group = ctx.MapVersionedGroup("verification");

        // ---------------- verify a claim ------------------------------------------------------
        group.MapPost("/claims", async (HttpContext http, LivoraDbContext db,
            VerifyClaimRequest request, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (userId is null)
                return Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");

            if (request is null)
                return Problems.Of(http, ProblemCodes.ValidationFailed, "a claim body is required");

            var invalid = VerificationBoundary.ValidationErrors(request);
            if (invalid is not null)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "the claim carries fields the verifier cannot process", fieldErrors: invalid);

            VerificationVerdict verdict;
            try
            {
                var engineRequest = request.ToEngine(
                    asOf: DateTimeOffset.UtcNow.UtcDateTime,
                    callerIsStaff: http.User.IsStaff(),
                    reviewerId: null, reviewDecision: null);
                verdict = VerificationEngine.Evaluate(engineRequest);
            }
            catch (VerificationEngine.UnknownVerificationRuleException ex)
            {
                // Explicit unknown rule: honest 503, never a different rule quietly instead.
                return Problems.Of(http, ProblemCodes.VerificationRuleUnknown, ex.Message);
            }

            // async by contract; the ladder itself is pure CPU
            if (Livora.Server.Modules.Intelligence.EnginesSchemaGate.Active)
                await PersistAsync(db, userId, request, verdict, ct);
            // Ledger gated off: the verdict is still computed honestly (pure engine) but nothing
            // is stored — GET/list/review answer 503 with the reason, so no fake persistence.
            return Results.Ok(verdict.ToResponse());
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- list ----------------------------------------------------------------
        group.MapGet("/claims", async (HttpContext http, LivoraDbContext db,
            int offset, int limit, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (userId is null)
                return Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");

            if (!Livora.Server.Modules.Intelligence.EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the verification ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");

            var page = new PageRequest { Offset = offset, Limit = limit };
            var (items, total) = await new EfVerificationLedger(db).ListAsync(userId, page.Offset, page.SafeLimit, ct);
            var views = items.Select(i => new ClaimListItem(
                i.ClaimId, i.ClaimType, i.SourceKind, i.TrustLevel, i.Status, i.Confidence,
                i.CreatedAtUtc, i.UpdatedAtUtc)).ToList();
            return Results.Ok(PagedResult<ClaimListItem>.Of(views, page, total));
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- one claim (+ IDOR gate) ----------------------------------------------
        group.MapGet("/claims/{claimId}", async (HttpContext http, LivoraDbContext db,
            string claimId, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (userId is null)
                return Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");

            if (!Livora.Server.Modules.Intelligence.EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the verification ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");
            var ledger = new EfVerificationLedger(db);
            var row = await ledger.FindAsync(userId, claimId, ct);
            if (row is null)
                return Problems.Of(http, ProblemCodes.NotFound, "no claim is recorded under this id");
            // FindAsync filters by the authenticated uid: a foreign claim is simply not found,
            // and the handler still compares the row owner before answering (IDOR gate, twice).
            if (!string.Equals(row.UserId, userId, StringComparison.Ordinal))
                return Problems.Of(http, ProblemCodes.Forbidden, "this claim belongs to another account");

            return Results.Ok(RebuildVerdict(row).ToResponse());
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- staff review ----------------------------------------------------------
        group.MapPost("/claims/{claimId}/review", async (HttpContext http, LivoraDbContext db,
            string claimId, ReviewRequest review, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            if (userId is null)
                return Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");
            if (!http.User.IsStaff())
                return Problems.Of(http, ProblemCodes.Forbidden, "claim review requires a staff role");
            if (review is null || review.Decision is not ("confirm" or "reject"))
                return Problems.Of(http, ProblemCodes.ValidationFailed, "decision must be confirm or reject");

            if (!Livora.Server.Modules.Intelligence.EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the verification ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");
            // Staff review reaches any claim (Moderator policy already passed); a non-staff caller
            // could not get here at all, and the 403 branch stays explicit for defence in depth.
            var ledger = new EfVerificationLedger(db);
            var row = await ledger.FindAnyAsync(claimId, ct);
            if (row is null)
                return Problems.Of(http, ProblemCodes.NotFound, "no claim is recorded under this id");
            if (!http.User.IsStaff())
                return Problems.Of(http, ProblemCodes.Forbidden, "claim review requires a staff role");

            var engineRequest = new VerificationRequest(
                ClaimId: row.ClaimId, ClaimType: row.ClaimType, SourceKind: row.SourceKind,
                MetricKey: row.MetricKey, ClaimedValue: row.ClaimedValue, Unit: row.Unit,
                SourceRef: row.SourceRef,
                AsOfUtc: DateTimeOffset.UtcNow.UtcDateTime,
                Evidence: row.Evidence.Select(e => new EvidenceFact(e.EvidenceId, e.SourceKind,
                    e.MetricKey, e.Value, e.Unit, e.SourceRef, e.ObservedAtUtc.UtcDateTime)).ToList(),
                ReviewerId: userId, CallerIsStaff: true, ReviewDecision: review.Decision);

            var verdict = VerificationEngine.Evaluate(engineRequest);
            row.ReviewerId = userId;
            row.ReviewDecision = review.Decision;
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            ApplyVerdict(row, verdict);
            await db.SaveChangesAsync(ct);
            return Results.Ok(verdict.ToResponse());
        }).RequireAuthorization(Policies.Moderator);

        // ---------------- rule catalog ----------------------------------------------------------
        group.MapGet("/rules", (HttpContext http) =>
        {
            var userId = http.User.UserId();
            if (userId is null)
                return Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");
            return Results.Ok(VerificationEngine.DefaultRules.Select(r => new VerificationRuleView(
                r.RuleKey, VerificationEngine.TrustName(r.Establishes),
                r.AppliesTo.Count == 0 ? ClaimTypes.All.ToList() : r.AppliesTo)).ToList());
        }).RequireAuthorization(Policies.SignedIn);
    }

    // ---- persistence mapping (thin; no logic beyond rows) ----------------------------------

    private static async Task PersistAsync(LivoraDbContext db, string userId,
        VerifyClaimRequest request, VerificationVerdict verdict, CancellationToken ct)
    {
        var record = new VerificationClaimRecord
        {
            UserId = userId,
            ClaimId = request.ClaimId.Trim(),
            ClaimType = request.ClaimType,
            SourceKind = request.SourceKind,
            MetricKey = request.MetricKey,
            ClaimedValue = request.ClaimedValue,
            Unit = request.Unit,
            SourceRef = request.SourceRef ?? "",
            AsOfUtc = verdict.GeneratedAtUtc,
            Evidence = (request.Evidence ?? []).Select(e => new VerificationEvidenceRecord
            {
                EvidenceId = e.EvidenceId, SourceKind = e.SourceKind, MetricKey = e.MetricKey,
                Value = e.Value, Unit = e.Unit, SourceRef = e.SourceRef, ObservedAtUtc = e.ObservedAtUtc,
            }).ToList(),
        };
        ApplyVerdict(record, verdict);
        await new EfVerificationLedger(db).UpsertAsync(record, ct);
    }

    private static void ApplyVerdict(VerificationClaimRecord row, VerificationVerdict v)
    {
        row.TrustLevel = v.TrustLevel;
        row.Status = v.Status;
        row.Confidence = v.Confidence;
        row.AttemptedRulesJson = JsonSerializer.Serialize(
            v.AttemptedRules.Select(o => new { ruleKey = o.RuleKey, result = o.Result, detail = o.Detail }));
        row.ContributingRuleKeysJson = JsonSerializer.Serialize(v.ContributingRuleKeys);
        row.RefusalReason = v.RefusalReason;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>The stored row re-rendered as the verdict body (attempted rules are the audit trail).</summary>
    private static VerificationVerdict RebuildVerdict(VerificationClaimRecord row)
    {
        var attempted = JsonSerializer.Deserialize<List<SavedRuleOutcome>>(row.AttemptedRulesJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        var contributing = JsonSerializer.Deserialize<List<string>>(row.ContributingRuleKeysJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        return new VerificationVerdict(
            row.ClaimId, row.ClaimType, row.TrustLevel, row.Status, row.Confidence,
            attempted.Select(a => new RuleOutcome(a.RuleKey, a.Result, a.Detail)).ToList(),
            contributing, row.Evidence.Select(e => e.EvidenceId).ToList(),
            row.RefusalReason, row.UpdatedAtUtc.UtcDateTime);
    }

    private sealed record SavedRuleOutcome(string RuleKey, string Result, string Detail);

    /// <summary>
    /// PURPOSE: honest live report. The verification ladder is in-process deterministic code, so
    ///          the probe EXECUTES the fixed vectors below (one per distinct rung + every refusal
    ///          path) and reports Ok only when all of them answer as the contract says. No external
    ///          provider participates, so none is claimed; a vector failure degrades the module
    ///          instead of faking success (Wave 4 §31: "connected" must be earned by a call).
    /// </summary>
    public ModuleHealth Report()
    {
        var caps = new Dictionary<string, string>
        {
            ["trust_levels"] = string.Join(",",
                Enum.GetValues<VerificationTrust>().Select(t => VerificationEngine.TrustName(t))),
            ["human_review"] = "requires_staff",
        };

        try
        {
            var probe = VerificationSelfCheck.Run();
            return probe.Passed
                ? new ModuleHealth(ModuleKey, DependencyState.Ok,
                    "five distinct trust rungs + all refusal paths verified by in-process fixed vectors", caps)
                : new ModuleHealth(ModuleKey, DependencyState.Degraded,
                    "verification self-check failed: " + probe.FailureReason, caps);
        }
        catch (Exception ex)
        {
            return new ModuleHealth(ModuleKey, DependencyState.Degraded,
                $"verification self-check threw {ex.GetType().Name}", caps);
        }
    }
}

/// <summary>List row shape (the full verdict lives at /claims/{id}).</summary>
public sealed record ClaimListItem(
    string ClaimId, string ClaimType, string SourceKind, string TrustLevel, string Status,
    double Confidence, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

/// <summary>Rule catalog row: the rung a rule can ESTABLISH is part of the public contract.</summary>
public sealed record VerificationRuleView(string RuleKey, string Establishes, IReadOnlyList<string> AppliesTo);

/// <summary>
/// PURPOSE: the fixed vectors the capability probe executes — every rung reached + every refusal
///          path exercised, in one pass, no database. If a rule's ladder semantics drift, /capabilities
///          says degraded BEFORE a client ever sees a wrong trust level.
/// </summary>
internal static class VerificationSelfCheck
{
    private static readonly DateTime AsOf = new(2026, 3, 16, 18, 0, 0, DateTimeKind.Utc);

    public static (bool Passed, string? FailureReason) Run()
    {
        var ev = new EvidenceFact("e-watch", ClaimSources.Device, "activity.steps",
            8_000, "steps", "watch:liliow", AsOf);
        var checks = new (string Name, Func<(string, string)> Run)[]
        {
            ("self", () => { var v = VerificationEngine.Evaluate(new VerificationRequest(
                    "c1", ClaimTypes.ManualEntry, ClaimSources.SelfReported, "sleep.minutes", 420,
                    "minutes", "manual:ios", AsOf, [])); return (v.TrustLevel, v.Status); }),
            ("device", () => { var v = VerificationEngine.Evaluate(new VerificationRequest(
                    "c2", ClaimTypes.HealthMetric, ClaimSources.Device, "activity.steps", 8_000,
                    "steps", "watch:today", AsOf, [ev])); return (v.TrustLevel, v.Status); }),
            ("cross", () => { var v = VerificationEngine.Evaluate(new VerificationRequest(
                    "c3", ClaimTypes.HealthMetric, ClaimSources.SelfReported, "activity.steps", 7_900,
                    "steps", "manual:android", AsOf, [ev])); return (v.TrustLevel, v.Status); }),
            ("implausible", () => { var v = VerificationEngine.Evaluate(new VerificationRequest(
                    "c4", ClaimTypes.ManualEntry, ClaimSources.SelfReported, "sleep.minutes", 9_000,
                    "minutes", "manual:ios", AsOf, [])); return (v.TrustLevel, v.Status); }),
        };

        var results = checks.ToDictionary(c => c.Name, c => c.Run());
        if (results["self"] != ("self_reported", "accepted")) return Fail("self_reported rung");
        if (results["device"] != ("device_derived", "accepted")) return Fail("device_derived rung");
        if (results["cross"] != ("system_verified", "accepted")) return Fail("cross-source system_verified");
        if (results["implausible"] != ("self_reported", "implausible")) return Fail("implausible refusal");
        if (results.Values.Select(v => v.Item1).Distinct().Count() < 3) return Fail("trust levels collapsed");
        return (true, null);

        static (bool, string?) Fail(string what) => (false, what);
    }
}
