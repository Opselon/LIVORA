namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// PURPOSE: the trust vocabulary for every server-side intelligence/verification output. An
///          evidence grade is a POSITION ON A LADDER, never a boolean: nothing in this lane may
///          collapse a grade into "verified=true".
/// OWNER: Agent 10/11 (lane w4-p1e-engines).
/// CONSUMES: nothing (pure vocabulary).
/// PROVIDES: <see cref="EvidenceGrade"/> ordering, ceilings and the reason codes every stage speaks.
/// INVARIANTS:
///   - ordering is strict and total: SelfReported &lt; DeviceDerived &lt; ProviderDerived &lt;
///     SystemVerified &lt; HumanReviewed; a weaker source can never out-rank a stronger one
///   - Unrated means "no evidence object exists" — it is NOT a grade of last resort, and no
///     outcome may claim Verified with Unrated on the record
///   - enum values are persisted as strings (append-only); never renumber or rename a member
/// </summary>
public enum EvidenceGrade
{
    /// <summary>No evidence record backs this claim. Honest absence, not a low score.</summary>
    Unrated = 0,

    /// <summary>The user said it (manual check-in, self-entered value). Honest, uncorroborated.</summary>
    SelfReported = 1,

    /// <summary>A device the user carries computed it (wearable/phone pipeline). Derivation is
    /// visible and repeatable but not attested by an outside party.</summary>
    DeviceDerived = 2,

    /// <summary>An external provider's own system produced it (e.g. Google Calendar event exists).
    /// Third-party attestation, still outside LIVORA's own verification code path.</summary>
    ProviderDerived = 3,

    /// <summary>LIVORA's own deterministic code verified it (signature check, probe, recomputation
    /// from source records). This is what "verified" may mean — and only with this grade on file.</summary>
    SystemVerified = 4,

    /// <summary>A human reviewer (staff) checked it. Highest grade; never automatic.</summary>
    HumanReviewed = 5,
}

/// <summary>
/// Grade algebra (pure). Kept separate from the enum so the ordering rules are testable and so
/// callers cannot accidentally compare raw enum values for "trust" without saying which rule they
/// used.
/// </summary>
public static class EvidenceGrades
{
    /// <summary>Total order, weakest first. Unrated excluded: it is the absence of a grade.</summary>
    public static IReadOnlyList<EvidenceGrade> Ordered { get; } =
        [
            EvidenceGrade.SelfReported,
            EvidenceGrade.DeviceDerived,
            EvidenceGrade.ProviderDerived,
            EvidenceGrade.SystemVerified,
            EvidenceGrade.HumanReviewed,
        ];

    public static int Rank(this EvidenceGrade grade) => grade switch
    {
        EvidenceGrade.SelfReported => 1,
        EvidenceGrade.DeviceDerived => 2,
        EvidenceGrade.ProviderDerived => 3,
        EvidenceGrade.SystemVerified => 4,
        EvidenceGrade.HumanReviewed => 5,
        _ => 0,
    };

    public static bool IsEvidence(this EvidenceGrade grade) => grade.Rank() > 0;

    public static EvidenceGrade Max(EvidenceGrade a, EvidenceGrade b) =>
        a.Rank() >= b.Rank() ? a : b;

    /// <summary>Ceiling over a set; Unrated when the set holds no actual evidence.</summary>
    public static EvidenceGrade Ceiling(IEnumerable<EvidenceGrade> grades)
    {
        var best = EvidenceGrade.Unrated;
        foreach (var g in grades) best = Max(best, g);
        return best;
    }

    /// <summary>
    /// Map a provenance label (what a client or provider calls its data) onto a grade. Unknown
    /// labels return Unrated rather than guessing — a guess would silently mint trust.
    /// </summary>
    public static EvidenceGrade FromSourceLabel(string? sourceLabel) => sourceLabel?.Trim().ToLowerInvariant() switch
    {
        "manual" or "self" or "self_reported" or "user_entry" => EvidenceGrade.SelfReported,
        "device" or "device_derived" or "wearable" or "phone" or "healthconnect" or "applehealth"
            or "samsunghealth" or "wearos" => EvidenceGrade.DeviceDerived,
        "provider" or "provider_derived" or "google_calendar" or "google" or "screen_time" => EvidenceGrade.ProviderDerived,
        "system" or "system_verified" or "server_probe" or "recomputed" => EvidenceGrade.SystemVerified,
        "human" or "human_reviewed" or "staff_review" => EvidenceGrade.HumanReviewed,
        _ => EvidenceGrade.Unrated,
    };

    /// <summary>Stable machine token for logs/DTOs (never localized prose, never "verified").</summary>
    public static string Token(this EvidenceGrade grade) => grade switch
    {
        EvidenceGrade.SelfReported => "self_reported",
        EvidenceGrade.DeviceDerived => "device_derived",
        EvidenceGrade.ProviderDerived => "provider_derived",
        EvidenceGrade.SystemVerified => "system_verified",
        EvidenceGrade.HumanReviewed => "human_reviewed",
        _ => "unrated",
    };
}

/// <summary>
/// One line of the decision trail: WHICH stage, WHICH rule, WHAT verdict, WHICH evidence ids it
/// was derived from, and the numeric factors it used. Every stage output carries these so a
/// reviewer (or a faking test) can point at a rule id and re-run it.
/// <para>
/// Machine-shaped on purpose — rule keys and factor tokens, not prose — following the client's
/// localization-key convention (ported from Application/Planning/Adaptive/PlanAdaptationEngine.cs
/// Adaptation.EvidenceKey/EvidenceArgs and Application/Rules/RuleEngine.cs RuleResult.RuleKey).
/// </para>
/// </summary>
/// <param name="Stage">Pipeline stage name, e.g. "baseline", "recommend".</param>
/// <param name="RuleKey">Stable rule id, e.g. "Rule.SleepDebt" (ported) or "p1e.budget.drop".</param>
/// <param name="Verdict">Short machine token: "fired" | "refused" | "suppressed" | "moved" | ...</param>
/// <param name="Factors">Numeric/string factors used, invariant-formatted, ordered ("dev=-0.19").</param>
/// <param name="EvidenceIds">Fact/evidence record ids this line was derived from (may be empty for
/// pure structural steps — never a placeholder string).</param>
public sealed record TrailEntry(
    string Stage,
    string RuleKey,
    string Verdict,
    IReadOnlyList<string> Factors,
    IReadOnlyList<string> EvidenceIds)
{
    public static TrailEntry Of(string stage, string ruleKey, string verdict,
        IReadOnlyList<string>? factors = null, IReadOnlyList<string>? evidenceIds = null)
        => new(stage, ruleKey, verdict, factors ?? Array.Empty<string>(), evidenceIds ?? Array.Empty<string>());

    /// <summary>Deterministic one-line rendering for logs (ordinal, invariant).</summary>
    public string Render() =>
        $"{Stage}|{RuleKey}|{Verdict}|{string.Join(",", Factors)}|{string.Join(",", EvidenceIds)}";
}

/// <summary>
/// Shared numeric helpers for the engine lane. Determinism rules, ported with attribution from the
/// client so the two sides of LIVORA can never disagree about a statistic.
/// </summary>
public static class EngineMath
{
    /// <summary>Stable engine build token (appears in every output; bump on behaviour change).</summary>
    public const string EngineVersion = "p1e-1";

    /// <summary>±12% of personal baseline is noise, not signal — the client's own band.
    /// PORTED FROM: Domain/Models/State/PersonalState.cs:26 (MetricState.Level band = 0.12).</summary>
    public const double LevelBand = 0.12;

    /// <summary>Population standard deviation — identical formula to the client's
    /// Baseline.FromSamples (Domain/Models/State/PersonalState.cs:69).</summary>
    public static double StdDev(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        double mean = values.Average();
        double variance = values.Sum(x => (x - mean) * (x - mean)) / values.Count;
        return Math.Sqrt(variance);
    }

    /// <summary>Clean, ordered sample filter: NaN/negatives out (client parity,
    /// Baseline.FromSamples:57 / BaselineService.Valid:53-54).</summary>
    public static List<double> Clean(this IEnumerable<double> values) =>
        values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0).OrderBy(v => v).ToList();

    /// <summary>Invariant number formatting so a trail line is byte-stable across cultures
    /// (client convention: Convert.ToString(x, CultureInfo.InvariantCulture)).</summary>
    public static string Num(double value, string format = "0.###") =>
        double.IsNaN(value) ? "nan" : value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    public static string Pct(double fraction, string format = "0.#") =>
        Num(fraction * 100, format) + "%";

    /// <summary>"key=value" factor token used across every trail line.</summary>
    public static string Factor(string key, double value, string format = "0.###") =>
        $"{key}={Num(value, format)}";

    public static string Factor(string key, string value) => $"{key}={value}";
}
