using System.Text.Json;
using Livora.Server.Application;
using Livora.Server.Infrastructure.Engines.Decision;
using Livora.Server.Infrastructure.Engines.Decision.Explanation;
using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Modules.Intelligence;

/// <summary>
/// PURPOSE: the deterministic decision surface — state, rules, cross-domain fusion, weekly
///          look-back, patterns (+dismissals), and the AI-free explanation port, all behind
///          /api/v1/intelligence. NOTHING on this path calls an AI provider (Wave 4 P1 decision:
///          no live-LLM in CI; engines decide, templates phrase).
/// OWNER: Agent 10+11 (lane w4-p1e-engines). Self-registered via IFlivoraModule; nobody edited
///        the composition root to get here.
/// PROVIDES:
///   POST /api/v1/intelligence/decision          — full pipeline over a caller-supplied context
///   POST /api/v1/intelligence/weekly-review     — honest look-back (refusal under 3 days)
///   POST /api/v1/intelligence/pattern-scan      — confidence-aware patterns (never diagnoses)
///   GET  /api/v1/intelligence/pattern-dismissals — the user's "stop suggesting this" list
///   PUT  /api/v1/intelligence/pattern-dismissals/{patternId}   — dismiss (idempotent)
///   DELETE /api/v1/intelligence/pattern-dismissals/{patternId} — undelete: patterns are deletable
///   POST /api/v1/intelligence/explain           — deterministic en/fa phrasing of engine keys
///   GET  /api/v1/intelligence/rules             — the rule keys the engine may emit (no guessing)
/// INVARIANTS:
///   - personal data is per-user; every persistence read filters by the authenticated livora.uid
///     (policy proves the token, never ownership — the handler compares, and an IDOR test proves it)
///   - over-budget bodies answer 400 context_budget_exceeded with per-field errors, never compute
///   - Report() probes the engine with a fixed self-check vector: "Ok" is earned by EXECUTION,
///     not by the file existing (Wave 4 §31: no fake connected states)
///   - the model contribution is registered here (LivoraDbContext is frozen), and the lead's
///     Wave4P1Schema migration picks it up — this lane ships no migration file
/// </summary>
public sealed class IntelligenceModule : IFlivoraModule
{
    public const string ModuleKey = "intelligence";

    public string Key => ModuleKey;

    public void ConfigureServices(ModuleSeed seed)
    {
        // Entity contribution — behind the schema gate (see EnginesSchemaGate for the why:
        // the lead generates Wave4P1Schema from these at merge; until then the migration tripwire
        // in the frozen persistence tests must stay green). Append-only; frozen by the host after
        // ALL modules register.
        EnginesSchemaGate.EnsureRegistered(seed.Configuration, seed.Logger);

        seed.Services.AddSingleton<DecisionPipeline>();
        seed.Services.AddSingleton<IExplanationProvider, DeterministicExplanationProvider>();
        // The adapter is DbContext-typed (testability note in PatternPersistence.cs); bind it to
        // the real shared context here.
        seed.Services.AddScoped(sp => new EfPatternDismissalStore(
            sp.GetRequiredService<LivoraDbContext>()));
    }

    public void MapEndpoints(FlivoraEndpointContext ctx)
    {
        var group = ctx.MapVersionedGroup("intelligence");
        group.RequireAuthorization(Policies.SignedIn);

        // ---------------- decision ----------------------------------------------------------
        group.MapPost("/decision", async (HttpContext http, DecisionPipeline pipeline,
            DecisionRequest request, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);

            if (request is null)
                return Problems.Of(http, ProblemCodes.ValidationFailed, "a decision body is required");

            var invalid = IntelligenceLimits.ValidationErrors(request);
            if (invalid is not null)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "the decision request carries impossible values", fieldErrors: invalid);

            var over = IntelligenceLimits.BudgetViolations(request);
            if (over is not null)
                return Problems.Of(http, ProblemCodes.ContextBudgetExceeded,
                    "the request context exceeds the deterministic-engine budget", fieldErrors: over);

            await Task.CompletedTask; // handler is async by contract; the engine itself is pure CPU
            var output = pipeline.Run(request.ToInput());
            return Results.Ok(output.ToResponse());
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- weekly review -----------------------------------------------------
        group.MapPost("/weekly-review", async (HttpContext http, DecisionRequest request,
            CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);

            var invalid = IntelligenceLimits.ValidationErrors(request);
            if (invalid is not null)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "the review request carries impossible values", fieldErrors: invalid);
            var over = IntelligenceLimits.BudgetViolations(request);
            if (over is not null)
                return Problems.Of(http, ProblemCodes.ContextBudgetExceeded,
                    "the request context exceeds the deterministic-engine budget", fieldErrors: over);

            await Task.CompletedTask;
            var r = EngineWeeklyReview.Review(request.ToInput());
            return Results.Ok(new WeeklyReviewResponse(
                r.Available, r.RefusalReason, r.DaysPresentInWindow, r.WeekStartUtc, r.WeekEndUtc,
                Wire.Trend(r.SleepTrend), Wire.Trend(r.ActivityTrend), Wire.Trend(r.RecoveryTrend),
                Wire.Trend(r.StressTrend), r.HabitConsistency, r.StreakDays, r.ImprovementKeys,
                r.DeclineKeys, r.FocusKey, r.FocusArgs, Wire.BaselineConfidence(r.Confidence),
                GeneratedBy: "deterministic_engines"));
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- pattern scan ------------------------------------------------------
        group.MapPost("/pattern-scan", async (HttpContext http, DecisionRequest request,
            EfPatternDismissalStore dismissals, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);

            var invalid = IntelligenceLimits.ValidationErrors(request);
            if (invalid is not null)
                return Problems.Of(http, ProblemCodes.ValidationFailed,
                    "the scan request carries impossible values", fieldErrors: invalid);
            var over = IntelligenceLimits.BudgetViolations(request);
            if (over is not null)
                return Problems.Of(http, ProblemCodes.ContextBudgetExceeded,
                    "the request context exceeds the deterministic-engine budget", fieldErrors: over);

            var scan = EnginePatternScanner.Scan(request.ToInput());
            if (!EnginesSchemaGate.Active)
            {
                // Compute what can be computed; say what is missing (safe degradation, not failure).
                return Results.Ok(new PatternScanResponse(
                    scan.Findings
                        .Select(f => new PatternView(f.PatternId, f.Kind, f.Confidence, f.SampleCount,
                            f.DateFromUtc, f.DateToUtc, f.Trend,
                            f.EvidenceKeys.Select(k => new PatternEvidenceView(k.Key, k.Args)).ToList(),
                            f.EvidenceFactIds)).ToList(),
                    scan.InsufficientKinds, [], scan.DistinctHistoryDays,
                    GeneratedBy: "deterministic_engines"));
            }
            var dismissed = await dismissals.ListAsync(userId, ct);
            var dismissedIds = dismissed.Select(d => d.PatternId)
                .OrderBy(s => s, StringComparer.Ordinal).ToList();

            // A dismissed pattern is REMOVED from the output (revisable + deletable, product law);
            // DELETE-ing the dismissal brings it back on the next scan — verified by test.
            var visible = scan.Findings
                .Where(f => !dismissedIds.Contains(f.PatternId, StringComparer.Ordinal))
                .Select(f => new PatternView(f.PatternId, f.Kind, f.Confidence, f.SampleCount,
                    f.DateFromUtc, f.DateToUtc, f.Trend,
                    f.EvidenceKeys.Select(k => new PatternEvidenceView(k.Key, k.Args)).ToList(),
                    f.EvidenceFactIds))
                .ToList();

            return Results.Ok(new PatternScanResponse(visible, scan.InsufficientKinds, dismissedIds,
                scan.DistinctHistoryDays, GeneratedBy: "deterministic_engines"));
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- dismissal list -----------------------------------------------------
        group.MapGet("/pattern-dismissals", async (HttpContext http, EfPatternDismissalStore store,
            int offset, int limit, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);
            if (!EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the pattern ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");

            var all = await store.ListAsync(userId, ct);
            var page = new PageRequest { Offset = offset, Limit = limit };
            var items = all.Skip(page.Offset).Take(page.SafeLimit)
                .Select(d => new DismissalView(d.PatternId, d.Kind, d.CreatedAtUtc))
                .ToList();
            return Results.Ok(PagedResult<DismissalView>.Of(items, page, all.Count));
        }).RequireAuthorization(Policies.SignedIn);

        group.MapPut("/pattern-dismissals/{patternId}", async (HttpContext http,
            EfPatternDismissalStore store, string patternId, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);
            if (!EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the pattern ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");
            if (string.IsNullOrWhiteSpace(patternId) || patternId.Length > 160)
                return Problems.Of(http, ProblemCodes.ValidationFailed, "patternId is required (max 160 chars)");

            await store.AddAsync(userId, patternId, KindFromPatternId(patternId), ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.SignedIn);

        group.MapDelete("/pattern-dismissals/{patternId}", async (HttpContext http,
            EfPatternDismissalStore store, string patternId, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);
            if (!EnginesSchemaGate.Active)
                return Problems.Of(http, ProblemCodes.ProviderUnavailable,
                    "the pattern ledger schema is not applied on this server (Modules:Intelligence:SchemaContribution)");
            var removed = await store.RemoveAsync(userId, patternId, ct);
            return removed ? Results.NoContent() : Problems.Of(http, ProblemCodes.NotFound,
                "no dismissal is recorded for that pattern");
        }).RequireAuthorization(Policies.SignedIn);

        // ---------------- explanation port ----------------------------------------------------
        group.MapPost("/explain", async (HttpContext http, ExplanationRequest request,
            IExplanationProvider provider, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);
            if (request?.Items is null || request.Items.Count == 0)
                return Problems.Of(http, ProblemCodes.ValidationFailed, "items are required");
            if (request.Items.Count > ExplanationLimits.MaxItemsPerCall)
                return Problems.Of(http, ProblemCodes.ContextBudgetExceeded,
                    $"at most {ExplanationLimits.MaxItemsPerCall} keys may be rendered per call");

            await Task.CompletedTask; // async by contract; the renderer is pure CPU
            var lines = request.Items.Select(i =>
            {
                var args = (IReadOnlyList<object>)(i.Args ?? Array.Empty<object>());
                return new ExplanationLineDto(i.Key, args,
                    DeterministicExplanationRenderer.Render(i.Key, args,
                        ExplanationLimits.SafeLocale(request.Locale)),
                    DeterministicExplanationRenderer.Knows(i.Key, ExplanationLimits.SafeLocale(request.Locale)));
            }).ToList();
            return Results.Ok(new ExplanationResponseDto(provider.GeneratedBy,
                ExplanationLimits.SafeLocale(request.Locale), lines));
        }).RequireAuthorization(Policies.SignedIn);

        // The only AI-shaped door on this surface: an ask for AI phrasing answered TRUTHFULLY.
        group.MapPost("/explain-ai", (HttpContext http) =>
            Problems.Of(http, ProblemCodes.AiUnavailable,
                "no AI explanation provider is configured on this server; /explain answers " +
                "deterministically from the same keys")).RequireAuthorization(Policies.SignedIn);

        // ---------------- rule catalog --------------------------------------------------------
        group.MapGet("/rules", async (HttpContext http, CancellationToken ct) =>
        {
            var userId = RequireUser(http);
            if (userId is null) return Unauthenticated(http);
            await Task.CompletedTask;
            return Results.Ok(new
            {
                ruleEngine = EngineRuleEvaluator.AllRuleKeys,
                fusion = new[] { "move", "shorten", "skip" },
                budget = new { maxHigh = NumericRules.MaxHighPriorityRecommendations,
                               maxTotal = NumericRules.MaxTotalRecommendations },
                thresholds = NumericRules.KnownConstants(),
            });
        }).RequireAuthorization(Policies.SignedIn);
    }

    // ---- auth helpers ----------------------------------------------------------------------
    private static string? RequireUser(HttpContext http) => http.User.UserId();

    private static IResult Unauthenticated(HttpContext http)
        => Problems.Of(http, ProblemCodes.Unauthenticated, "a signed-in identity is required");

    /// <summary>pat:&lt;kind&gt;:&lt;from&gt;..&lt;to&gt; — keep the kind readable without parsing games.</summary>
    private static string KindFromPatternId(string patternId)
    {
        var parts = patternId.Split(':');
        return parts.Length >= 2 ? parts[1] : "";
    }

    /// <summary>
    /// PURPOSE: honest live probe — run the whole pipeline over a fixed self-check vector and
    ///          report Ok only when the computation actually answered. No external dependency is
    ///          claimed: the DB is probed by the platform module; AI is disclosed as NOT configured
    ///          (it does not exist on this surface), never silently "connected".
    /// </summary>
    public ModuleHealth Report()
    {
        try
        {
            var probe = EngineSelfCheck.Run();
            var caps = new Dictionary<string, string>
            {
                ["ai_explanation"] = "not_configured",       // no provider exists; /explain-ai says so
                ["engine"] = "deterministic",
                ["selfcheck"] = probe.Passed ? "ok" : "failing",
            };
            return probe.Passed
                ? new ModuleHealth(ModuleKey, DependencyState.Ok,
                    "deterministic engines computed the self-check vector in-process", caps)
                : new ModuleHealth(ModuleKey, DependencyState.Degraded,
                    "engine self-check failed: " + probe.FailureReason, caps);
        }
        catch (Exception ex)
        {
            return new ModuleHealth(ModuleKey, DependencyState.Degraded,
                $"engine self-check threw {ex.GetType().Name}",
                new Dictionary<string, string> { ["ai_explanation"] = "not_configured" });
        }
    }
}

/// <summary>The fixed vector the capability probe executes — the SAME numeric spine the parity
/// tests pin. If the engines stop agreeing with their own constants, capabilities say degraded.</summary>
internal static class EngineSelfCheck
{
    public static (bool Passed, string? FailureReason) Run()
    {
        var input = EngineFixtures.SleepDeprived();
        var output = new DecisionPipeline().Run(input);

        if (output.RulesFired.Count == 0) return (false, "no rules fired on the sleep-deprived vector");
        if (output.Recommendations.Count is < 1 or > NumericRules.MaxTotalRecommendations)
            return (false, "recommendation budget violated on the self-check vector");
        if (output.State.Metrics[EngineStateComputer.EngineMetrics.SleepMinutes].Value is null)
            return (false, "self-check vector lost the sleep reading");
        return (true, null);
    }
}
