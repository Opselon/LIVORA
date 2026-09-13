using System.Text;
using System.Text.RegularExpressions;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Intelligence;

/// <summary>
/// AGENT-10 defense layer (Wave 3c lane 02): assume every AI output is wrong until proven
/// against the deterministic context. Verdicts are machine reason codes ONLY — raw provider
/// text never appears in reasons, logs, or the UI path unless it survived the prose checks.
///
/// Rejection codes (stable strings, asserted by tests):
///   response.empty      — nothing usable came back (null/blank response)
///   metric.unknown      — a claim references a metric that is NOT in the context facts
///   claim.deviation     — a claim's value differs &gt;10% from BOTH current and baseline
///   claim.impossible    — value outside the physically sane range for the metric family
///   action.unsupported  — proposed action is not a RecommendationActionKind name
///   text.digits         — free text contains digits while the claims are not all verified
///   text.toolong        — headline &gt; 120 or body &gt; 600 chars
///   text.control        — control characters or URLs in free text (FATAL: URLs never ship)
///   text.injection      — prompt-injection phrasing or system-prompt echo (FATAL)
///   confidence.range    — confidence outside 0..1 (FATAL: the model is not calibrated)
///   text.lang           — prose language disagrees with the context language (non-fatal strip)
///
/// PARTIAL ACCEPT: offending claims are dropped and unsupported actions trimmed; prose is kept
/// ONLY when every prose check passed. Accepted=true may still carry codes describing what was
/// removed. When nothing safe remains (prose stripped AND no valid keys), Accepted=false.
/// </summary>
public sealed class AiSafetyValidator : IAiOutputValidator
{
    /// <summary>Approved localization keys used when validated provider prose is rendered.</summary>
    public const string HeadlineProviderKey = "Ai.Headline.Provider";
    public const string BodyProviderKey = "Ai.Body.Provider";

    public const int MaxHeadlineChars = 120;
    public const int MaxBodyChars = 600;
    public const double ClaimTolerance = 0.10;   // ±10% vs the context value

    private static readonly string[] InjectionPatterns =
    {
        "ignore previous", "ignore all previous", "ignore the previous", "disregard previous",
        "disregard the previous", "disregard above", "forget previous", "forget the above",
        "system prompt", "new instructions", "you are now", "reveal your prompt",
        "act as the developer", "developer mode", "print your instructions",
    };

    /// <summary>Distinctive phrase from OUR system prompt — an echo means the model leaked it.</summary>
    internal const string SystemPromptEchoMarker = "calm, honest daily-insight writer";

    private static readonly Regex UrlShape =
        new(@"(\w+://|www\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public AiValidationResult Validate(AiInsightResponse? response, IntelligenceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (response is null) return Rejected("response.empty");

        var reasons = new List<string>();

        // ---- fatal structure checks ----------------------------------------
        if (response.Confidence is < 0 or > 1) reasons.AddIfMissing("confidence.range");

        var headline = (response.HeadlineKey ?? string.Empty).Trim();
        var body = (response.ProviderText ?? string.Empty).Trim();
        var headlineIsText = response.HeadlineIsProviderText && headline.Length > 0;
        var hasAnyContent = headline.Length > 0 || body.Length > 0
            || (response.BodyKey ?? string.Empty).Length > 0;

        if (!hasAnyContent) reasons.AddIfMissing("response.empty");
        foreach (var text in new[] { headline, body })
        {
            if (ContainsInjection(text)) reasons.AddIfMissing("text.injection");
            if (ContainsControlOrUrl(text)) reasons.AddIfMissing("text.control");
        }
        var fatal = reasons.Count > 0;
        if (fatal) return Rejected(reasons);   // reasons carry CODES only — never the offending text

        // ---- claims: verify each against context facts ---------------------
        var facts = new Dictionary<string, StateDeltaFact>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in context.StateFacts)
            if (!string.IsNullOrEmpty(f?.MetricKey)) facts.TryAdd(f.MetricKey, f);

        var kept = new List<NumericClaim>();
        foreach (var claim in response.Claims ?? Array.Empty<NumericClaim>())
        {
            var claimKey = claim?.MetricKey ?? string.Empty;
            if (claim is null || !facts.TryGetValue(claimKey, out var fact))
            { reasons.AddIfMissing("metric.unknown"); continue; }
            if (!IsPlausibleRange(claimKey, fact.Unit, claim.Value))
            { reasons.AddIfMissing("claim.impossible"); continue; }
            if (!MatchesContext(claim.Value, fact))
            { reasons.AddIfMissing("claim.deviation"); continue; }
            kept.Add(claim);
        }
        // Prose may carry numbers only when EVERY claim the model made was verified.
        var claimsVerified = kept.Count > 0 && kept.Count == (response.Claims?.Count ?? 0);

        // ---- actions: trim to real RecommendationActionKind names ----------
        var actions = new List<string>();
        foreach (var a in response.ProposedActionKinds ?? Array.Empty<string>())
        {
            if (Enum.TryParse<RecommendationActionKind>(a, ignoreCase: false, out var parsed)
                && parsed != RecommendationActionKind.None) actions.Add(parsed.ToString());
            else reasons.AddIfMissing("action.unsupported");
        }

        // ---- prose cleanliness ---------------------------------------------
        var proseClean = true;
        if (headlineIsText && headline.Length > MaxHeadlineChars) { reasons.AddIfMissing("text.toolong"); proseClean = false; }
        if (body.Length > MaxBodyChars) { reasons.AddIfMissing("text.toolong"); proseClean = false; }
        if (!claimsVerified && (HasAnyDigit(headline) || HasAnyDigit(body)))
        { reasons.AddIfMissing("text.digits"); proseClean = false; }
        if (body.Length > 0 && !LanguageMatches(context.LanguageCode, body))
        { reasons.AddIfMissing("text.lang"); proseClean = false; }

        // ---- build the cleaned verdict --------------------------------------
        var modelBodyKey = response.BodyKey ?? string.Empty;
        var keepProse = proseClean && (body.Length > 0 || headlineIsText);
        var safe = new AiInsightResponse
        {
            Confidence = response.Confidence,
            Claims = kept,
            ProposedActionKinds = actions,
            RawForDiagnostics = null,   // never propagate raw payloads through the safe path
            HeadlineKey = keepProse
                ? HeadlineProviderKey   // keys PINNED by the validator — the model's own key strings are never trusted
                : (KeepsAsKey(headline) ? headline : string.Empty),
            BodyKey = keepProse
                ? BodyProviderKey
                : (KeepsAsKey(modelBodyKey) ? modelBodyKey : string.Empty),
            ProviderText = keepProse ? body : null,
            BodyIsProviderText = keepProse,
            HeadlineIsProviderText = false,
        };

        var nothingLeft = !safe.BodyIsProviderText
            && safe.HeadlineKey.Length == 0 && safe.BodyKey.Length == 0
            && kept.Count == 0 && actions.Count == 0;
        if (nothingLeft) return Rejected(reasons);

        return new AiValidationResult
        {
            Accepted = true,          // full or partial accept: structure survived, dirt was trimmed
            RejectionReasons = reasons,
            Safe = safe,
        };
    }

    // ---- helpers -----------------------------------------------------------

    private static AiValidationResult Rejected(string reason) => Rejected(new List<string> { reason });

    private static AiValidationResult Rejected(List<string> reasons) => new()
    {
        Accepted = false,
        RejectionReasons = reasons,
        Safe = null,    // never hand unsafe text downstream
    };

    private static bool ContainsInjection(string text)
    {
        if (text.Length == 0) return false;
        var norm = Normalize(text);
        foreach (var p in InjectionPatterns)
            if (norm.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        return norm.Contains(SystemPromptEchoMarker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsControlOrUrl(string text)
    {
        if (text.Length == 0) return false;
        // \n and \t are layout, not smuggling; everything else in the control class is rejected.
        foreach (var c in text)
            if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') return true;
        return UrlShape.IsMatch(text);
    }

    /// <summary>char.IsDigit covers ASCII AND Persian/Arabic-Indic digits — no digit escapes.</summary>
    private static bool HasAnyDigit(string text) => text.Any(char.IsDigit);

    private static bool LanguageMatches(string languageCode, string text)
    {
        var persian = text.Any(c => c is >= '\u0600' and <= '\u06FF');
        return languageCode == "fa" ? persian : !persian;
    }

    private static bool LooksLikeKey(string s) =>
        s.Length > 0 && s.Length <= 64 && !s.Contains(' ') && s.Contains('.') && char.IsUpper(s[0]);

    /// <summary>
    /// A model-supplied string counts as a renderable key only if it looks like a localization
    /// key AND is not the provider-pin echo ("Ai.Headline.Provider" without validated prose is
    /// a dangling reference to text we just deleted — shipping it would render an empty card).
    /// </summary>
    private static bool KeepsAsKey(string s) =>
        LooksLikeKey(s) && s != HeadlineProviderKey && s != BodyProviderKey;

    /// <summary>True when value is within 10% of current OR baseline (whichever the model meant).</summary>
    internal static bool MatchesContext(double value, StateDeltaFact fact) =>
        WithinTolerance(value, fact.Current) ||
        (fact.Baseline is double b && WithinTolerance(value, b));

    private static bool WithinTolerance(double value, double expected)
    {
        const double eps = 1e-9;
        if (Math.Abs(expected) < 1)          // tiny/zero expected: compare absolutely (±0.1 units)
            return Math.Abs(value - expected) <= 0.1 + eps;
        return Math.Abs(value - expected) / Math.Abs(expected) <= ClaimTolerance + eps;
    }

    /// <summary>Physical sanity ranges keyed by metric family (canonical units from ContextBuilder).</summary>
    internal static bool IsPlausibleRange(string metricKey, string unit, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return false;
        return metricKey switch
        {
            "sleep.minutes" or "sleep.bedtime" or "activity.minutes" => value >= 0 && value <= 1440,
            "activity.steps" => value >= 0 && value <= 200000,
            "recovery.rhr" => value >= 20 && value <= 250,
            "recovery.hrv" => value >= 1 && value <= 500,
            _ => string.Equals(unit, "ratio", StringComparison.OrdinalIgnoreCase)
                ? value >= 0 && value <= 1                      // ratio-family: stress/mood/energy/quality
                : value >= -1_000_000 && value <= 1_000_000,    // known metric, no hard bound: loose guard
        };
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsControl(c)) sb.Append(c == '\u200b' ? ' ' : c);
        return sb.ToString().ToLowerInvariant();
    }
}

internal static class AiValidationListExtensions
{
    public static void AddIfMissing(this List<string> list, string code)
    {
        if (!list.Contains(code, StringComparer.Ordinal)) list.Add(code);
    }
}
