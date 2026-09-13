using System.Globalization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Application.Intelligence;

/// <summary>
/// Outcome of one AI attempt WITH its provenance — the honesty seam. The UI/telemetry may read
/// Source + RejectionCodes (codes only, never provider text) to label what it is showing.
/// </summary>
public sealed record AiInsightOutcome(
    InsightInterpretation Interpretation,
    IntelligenceSource Source,
    IReadOnlyList<string> RejectionCodes);

/// <summary>
/// The REAL intelligence provider (Wave 3c lane 02): consent-gated, validated, deterministic
/// fallback. Pipeline per call:
///   gates -> ContextBuilder -> InsightPromptBuilder -> chat provider -> AiResponseParser
///        -> AiSafetyValidator -> map to InsightInterpretation
/// and ANY failure path (gate closed, null completion, unparseable, rejected, hung provider)
/// lands on the deterministic SampleIntelligenceProvider. This class never throws and never
/// hangs the caller (watchdog Task.WhenAny at config timeout + 2s).
///
/// KEY-MAPPING RULE (documented for tests): the accepted response uses HeadlineKey
/// "Ai.Headline.Provider" / BodyKey "Ai.Body.Provider" with BodyArgs = [provider text]
/// ONLY when the validator cleared the free-text path (prose survived every text.* check).
/// A partial accept that dropped the prose has NO AI text left to show, so the result is the
/// deterministic interpretation from the fallback provider (honest labels: AiRejectedFallback).
/// AI never sets High priority, never overrides recommendations — phrasing only.
/// </summary>
public sealed class AiOrchestratorProvider : IIntelligenceProvider
{
    private readonly IContextBuilder _contextBuilder;
    private readonly Func<IIntelligenceChatProvider?> _chatPicker;
    private readonly IAiOutputValidator _validator;
    private readonly IIntelligenceProvider _fallback;
    private readonly IConsentService? _consent;
    private readonly IGatewayConfigService? _gateway;
    private readonly TimeSpan _watchdogMargin;
    private readonly ILogger _log;

    /// <summary>Default headroom before the watchdog abandons the chat task.</summary>
    public static readonly TimeSpan DefaultWatchdogMargin = TimeSpan.FromSeconds(2);

    /// <summary>Registry-driven variant: the picker runs per call so consent revocation is live.</summary>
    public AiOrchestratorProvider(
        IContextBuilder contextBuilder,
        Func<IIntelligenceChatProvider?> chatPicker,
        IAiOutputValidator validator,
        IIntelligenceProvider fallback,
        IConsentService? consent = null,
        IGatewayConfigService? gateway = null,
        ILogger<AiOrchestratorProvider>? logger = null,
        TimeSpan watchdogMargin = default)
    {
        _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
        _chatPicker = chatPicker ?? throw new ArgumentNullException(nameof(chatPicker));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _consent = consent;
        _gateway = gateway;
        _watchdogMargin = watchdogMargin == default ? DefaultWatchdogMargin : watchdogMargin;
        _log = logger ?? NullLogger<AiOrchestratorProvider>.Instance; // codes/durations/ids only — never prompts/keys
    }

    /// <summary>Fixed-provider convenience (tests + simple wiring).</summary>
    public static AiOrchestratorProvider ForProvider(
        IContextBuilder contextBuilder,
        IIntelligenceChatProvider chatProvider,
        IAiOutputValidator validator,
        IIntelligenceProvider fallback,
        IConsentService? consent = null,
        IGatewayConfigService? gateway = null,
        ILogger<AiOrchestratorProvider>? logger = null,
        TimeSpan watchdogMargin = default)
    {
        ArgumentNullException.ThrowIfNull(chatProvider);
        return new AiOrchestratorProvider(contextBuilder, () => chatProvider, validator, fallback,
            consent, gateway, logger, watchdogMargin);
    }

    /// <summary>Always servable — worst case it IS the deterministic fallback.</summary>
    public bool IsAvailable => true;

    /// <summary>
    /// The measurement origin stays Mock/deterministic: the AI only rephrases numbers the app
    /// already computed; it contributes no data of its own. (DataOrigin has no Ai member and the
    /// contracts are frozen — labeling it anything else would misrepresent the numbers.)
    /// </summary>
    public DataOrigin Origin => DataOrigin.Mock;

    public async Task<InsightInterpretation> InterpretAsync(
        PersonalState state, IReadOnlyList<Recommendation> deterministicRecommendations,
        UserProfile profile, CancellationToken ct = default)
    {
        var outcome = await GenerateWithProvenanceAsync(state, deterministicRecommendations, profile, ct)
            .ConfigureAwait(false);
        return outcome.Interpretation;   // legacy delegate: provenance available via GenerateWithProvenanceAsync
    }

    /// <summary>InterpretAsync + provenance for honesty labels/tests.</summary>
    public async Task<AiInsightOutcome> GenerateWithProvenanceAsync(
        PersonalState state, IReadOnlyList<Recommendation> deterministicRecommendations,
        UserProfile profile, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // ---- GATES: consent AND gateway-enabled AND provider-configured --------------
            if (!GateConsentOk(out var consentCodes))
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiDisabledByUser, consentCodes, ct).ConfigureAwait(false);
            var config = await GetConfigSafeAsync(ct).ConfigureAwait(false);
            if (config is null || !config.Enabled)
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiDisabledByUser, new[] { "gateway.disabled" }, ct).ConfigureAwait(false);

            var chat = Safe(_chatPicker);
            if (chat is null || !SafeBool(() => chat.IsConfigured))
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiDisabledByUser, new[] { "provider.not-configured" }, ct).ConfigureAwait(false);

            // ---- PIPELINE ---------------------------------------------------------------
            var context = await _contextBuilder.BuildAsync(state, profile, deterministicRecommendations, ct)
                .ConfigureAwait(false);
            var userJson = InsightPromptBuilder.SerializeUser(context);
            var timeout = config.Timeout > TimeSpan.Zero ? config.Timeout : TimeSpan.FromSeconds(20);

            var completion = await RaceWithWatchdogAsync(chat, timeout, userJson, ct).ConfigureAwait(false);
            if (completion is null)
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiRejectedFallback, new[] { "provider.empty" }, ct).ConfigureAwait(false);

            var parsed = AiResponseParser.Parse(completion);
            if (parsed is null)
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiRejectedFallback, new[] { "response.unparseable" }, ct).ConfigureAwait(false);

            var verdict = _validator.Validate(parsed, context);
            if (!verdict.Accepted || verdict.Safe is null)
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiRejectedFallback, verdict.RejectionReasons, ct).ConfigureAwait(false);

            var safe = verdict.Safe;

            // Free-text path must have survived validation clean, else there is no AI text
            // to show and we honestly fall back (partial-accept-with-dropped-prose rule).
            if (!safe.BodyIsProviderText || string.IsNullOrWhiteSpace(safe.ProviderText))
            {
                var codes = verdict.RejectionReasons.Count > 0
                    ? verdict.RejectionReasons
                    : (IReadOnlyList<string>)new[] { "text.prose-dropped" };
                return await DeterministicAsync(state, deterministicRecommendations, profile,
                    IntelligenceSource.AiRejectedFallback, codes, ct).ConfigureAwait(false);
            }

            var interp = new InsightInterpretation
            {
                HeadlineKey = AiSafetyValidator.HeadlineProviderKey,   // "Ai.Headline.Provider"
                BodyKey = AiSafetyValidator.BodyProviderKey,           // "Ai.Body.Provider"
                BodyArgs = new object[] { safe.ProviderText!.Trim() }, // ONLY validated-clean prose
                Priority = InsightPriority.Normal,                     // AI never escalates
                Confidence = Math.Clamp(safe.Confidence, 0, 1),
                SuggestedRecommendationOverrides = Array.Empty<Recommendation>(), // phrasing only
            };

            _log.LogInformation("ai-ok cid={Cid} ms={Ms} codes={Codes}",
                context.CorrelationId, sw.ElapsedMilliseconds, string.Join(",", verdict.RejectionReasons));

            return new AiInsightOutcome(interp, IntelligenceSource.AiValidated, verdict.RejectionReasons);
        }
        catch (Exception)
        {
            // Any unexpected slip (builder throw, cancellation, validator bug) — deterministic.
            return await DeterministicAsync(state, deterministicRecommendations, profile,
                IntelligenceSource.AiRejectedFallback, new[] { "pipeline.exception" }, ct).ConfigureAwait(false);
        }
    }

    // ---- gates -------------------------------------------------------------

    private bool GateConsentOk(out IReadOnlyList<string> codes)
    {
        if (_consent is null) { codes = Array.Empty<string>(); return true; }  // no consent service wired = host controls gating
        var decision = Safe(() => _consent.Get(ConsentCategory.AiProcessing));
        if (decision == ConsentDecision.Granted) { codes = Array.Empty<string>(); return true; }
        codes = new[] { decision == ConsentDecision.Denied ? "consent.denied" : "consent.untouched" };
        return false;
    }

    private async Task<GatewayConfig?> GetConfigSafeAsync(CancellationToken ct)
    {
        if (_gateway is null) return null;   // no config service = treated as disabled (honest)
        try { return await _gateway.GetEffectiveAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    // ---- watchdog: the AI may hang, the app may not ------------------------

    private async Task<string?> RaceWithWatchdogAsync(
        IIntelligenceChatProvider chat, TimeSpan timeout, string userJson, CancellationToken ct)
    {
        var budget = timeout + _watchdogMargin;
        try
        {
            var callTask = RunChatSafelyAsync(chat, userJson, timeout, ct);
            var winner = await Task.WhenAny(callTask, Task.Delay(budget, CancellationToken.None))
                .ConfigureAwait(false);
            if (winner != callTask) return null;           // watchdog won: provider hung — abandon it
            return await callTask.ConfigureAwait(false);   // observe (never rethrow) its result
        }
        catch { return null; }
    }

    private static async Task<string?> RunChatSafelyAsync(
        IIntelligenceChatProvider chat, string userJson, TimeSpan timeout, CancellationToken ct)
    {
        try { return await chat.CompleteStructuredAsync(InsightPromptBuilder.SystemPrompt, userJson, timeout, ct)
                .ConfigureAwait(false); }
        catch { return null; }   // the contract says it won't throw; defense in depth anyway
    }

    // ---- deterministic landing gear ----------------------------------------

    private async Task<AiInsightOutcome> DeterministicAsync(
        PersonalState state, IReadOnlyList<Recommendation> recs, UserProfile profile,
        IntelligenceSource source, IReadOnlyList<string> codes, CancellationToken ct)
    {
        InsightInterpretation interp;
        try
        {
            interp = await _fallback.InterpretAsync(state, recs, profile, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // The fallback is deterministic and exception-free today; if a future one isn't,
            // say so plainly with a static balanced-day card rather than crash the Today page.
            interp = new InsightInterpretation
            {
                HeadlineKey = "Insight.Title.BalancedDay",
                BodyKey = "Insight.Reason.Balanced",
                Priority = InsightPriority.Normal,
                Confidence = 0.5,
            };
        }
        _log.LogInformation("ai-fallback source={Source} codes={Codes}", source, string.Join(",", codes));
        return new AiInsightOutcome(interp, source, codes);
    }

    private static T? Safe<T>(Func<T?> fn) { try { return fn(); } catch { return default; } }
    private static bool SafeBool(Func<bool> fn) { try { return fn(); } catch { return false; } }
}
