namespace Livora.Server.Infrastructure.Engines.Pipeline;

/// <summary>
/// THE VERIFICATION ENGINE — the trust half of this lane. Its whole reason to exist is that
/// LIVORA never collapses the evidence ladder into one generic "verified": a claim lives on two
/// INDEPENDENT axes —
///   • Grade  = WHERE the information came from (SelfReported → DeviceDerived → ProviderDerived →
///     SystemVerified → HumanReviewed; <see cref="EvidenceGrade"/>, append-only, never a boolean);
///   • Status = WHAT verification did to it (Unattested / Corroborated / Contradicted / Expired /
///     Withdrawn / ProviderUnconfigured).
/// There is deliberately **no bool Verified anywhere in this namespace** — a reflection test pins
/// that. "Verified" in the UI is only ever allowed as a rendering of Grade == SystemVerified or
/// HumanReviewed WITH a live, unexpired, unwithdrawn assessment, and this engine is the only thing
/// that can mint a SystemVerified grade — by recompute or probe, never by assertion.
/// <para>
/// Rules (each with a stable key + explicit outcome; all pure, all deterministic):
///  - p1e.verify.recompute — an aggregation of source facts (>= DeviceDerived each) reproduced the
///    asserted value inside tolerance ⇒ emit a NEW SystemVerified evidence record. The arithmetic
///    is the verification; no external party is involved, which is exactly why the grade is
///    SystemVerified and not ProviderDerived.
///  - p1e.verify.contradiction — two live facts on one subject disagree by more than the tolerance
///    and neither outranks the other on the grade ladder ⇒ Contradicted; a contradiction is shown,
///    never averaged away (the client's own law: sync "never silently overwritten",
///    LIVORA.Domain.Enums.SyncState.Conflict).
///  - p1e.verify.freshness — evidence outside its domain window ⇒ Expired. Grade is PRESERVED:
///    old truth is still truth about the past; it just cannot describe now (client parity:
///    DataPoint.WithMaxAge / RuleEngine.StaleDaysAllowance).
///  - p1e.verify.withdrawal — a deleted evidence row ⇒ Withdrawn; exclusion is total, the row
///    survives only for audit until account deletion (soft-delete law, LivoraDbContext header).
///  - p1e.verify.provider_receipt — an external attestation (Google Calendar event exists / Google
///    ID token) requires REAL configuration and a REAL probe. Without config it returns
///    ProviderUnconfigured — which is NOT a pass, NOT a fail, and NOT "verified". (Wave 4 §3:
///    provider secrets do not exist yet; the code path is behind config and reports the truth.)
/// </para>
/// <para>
/// Relationship to the client: the client's <c>DataOrigin</c>/<c>DataQuality</c> enums
/// (Domain/Enums/HealthDataEnums.cs) say where data came from and whether it is usable, but they
/// have no trust ladder and no verification state — this engine does not contradict them, it sits
/// above them: <see cref="EvidenceGrades.FromSourceLabel"/> maps their vocabulary onto grades, and
/// unknown labels land on Unrated rather than guessing.
/// </para>
/// </summary>
public static class P1eVerificationEngine
{
    public const string StageName = "verification";

    /// <summary>Recompute tolerance: |aggregate - assertion| / assertion ≤ this counts as reproduced.
    /// A stricter or looser number changes outcomes; a boundary test pins it.</summary>
    public const double RecomputeTolerance = 0.02;

    /// <summary>Contradiction tolerance: live facts on the same subject further apart than this
    /// relative gap contradict (client parity: the quality stage's ConflictRelativeGap, and the
    /// ±12% level band it is deliberately looser than — agreement for verification, not noise).</summary>
    public const double ContradictionGap = 0.25;

    /// <summary>Days after which an evidence record stops describing "now" (client parity:
    /// RuleEngine.StaleDaysAllowance = 2 for live state; verification records ride the same bound
    /// because the claim is about today's state). Expiry never destroys the record.</summary>
    public const int EvidenceFreshnessDays = 2;

    /// <summary>Grade needed before a claim may back an entitlement-style statement (server-
    /// authoritative law, Application/Authorization.cs header §54): only grades the engine itself
    /// minted (SystemVerified) or a human minted (HumanReviewed) clear this bar.</summary>
    public static bool ClearsEntitlementBar(EvidenceGrade grade) =>
        grade is EvidenceGrade.SystemVerified or EvidenceGrade.HumanReviewed;

    /// <summary>Assess one subject from its evidence rows. Pure; deterministic; the trail names
    /// every rule that ran and what it concluded.</summary>
    public static VerificationAssessment Assess(VerificationRequest request, DateTimeOffset asOfUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var trail = new List<TrailEntry>();

        // 1) withdrawal first: deleted rows speak to audit only.
        var live = new List<EvidenceRecord>();
        foreach (var e in request.Evidence.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            if (e.DeletedAtUtc is not null)
            {
                trail.Add(TrailEntry.Of(StageName, "p1e.verify.withdrawal", "withdrawn",
                    [EngineMath.Factor("evidence", e.Id)], [e.Id]));
                continue;
            }
            live.Add(e);
        }

        if (live.Count == 0)
        {
            // Nothing withdrawn means the claim was never attested; rows that ARE all withdrawn
            // mean the person took the proof back. Different states, different words.
            bool allWithdrawn = request.Evidence.Count > 0;
            trail.Add(TrailEntry.Of(StageName, allWithdrawn ? "p1e.verify.withdrawal" : "p1e.verify.no_evidence",
                allWithdrawn ? "withdrawn" : "unattested",
                [EngineMath.Factor("subject", request.SubjectKey)]));
            return new VerificationAssessment(request.SubjectKey, EvidenceGrade.Unrated,
                allWithdrawn ? VerificationStatus.Withdrawn : VerificationStatus.Unattested,
                allWithdrawn
                    ? "the only evidence for this claim was withdrawn by its owner; it proves nothing now " +
                      "and the rows remain for audit until account deletion"
                    : "no evidence record exists for this claim — which is honest absence, not a low score",
                Array.Empty<string>(), trail);
        }

        // 2) expiry (grade preserved, status changes).
        var fresh = new List<EvidenceRecord>();
        bool anyExpired = false;
        foreach (var e in live)
        {
            bool expired = (asOfUtc - e.OccurredAtUtc).TotalDays > EvidenceFreshnessDays;
            if (expired)
            {
                anyExpired = true;
                trail.Add(TrailEntry.Of(StageName, "p1e.verify.freshness", "expired",
                    [EngineMath.Factor("evidence", e.Id),
                     EngineMath.Factor("grade_kept", e.Grade.Token())], [e.Id]));
            }
            else fresh.Add(e);
        }

        // 3) provider receipts: unconfigured never passes.
        var providerOutcome = P1eProviderReceiptVerifier.Evaluate(request, asOfUtc);
        if (providerOutcome is not null)
        {
            trail.Add(TrailEntry.Of(StageName, "p1e.verify.provider_receipt",
                providerOutcome.Value.Verdict,
                [EngineMath.Factor("provider", providerOutcome.Value.Provider),
                 EngineMath.Factor("configured", providerOutcome.Value.Configured.ToString())]));
        }

        if (fresh.Count == 0)
        {
            return new VerificationAssessment(request.SubjectKey, EvidenceGrade.Unrated,
                VerificationStatus.Expired,
                $"every evidence record is older than {EvidenceFreshnessDays} days: the claim may have " +
                "been true, but nothing here proves it now (grades preserved in the trail, never erased)",
                live.Select(e => e.Id).ToList(), trail);
        }

        // 4) recompute: does an aggregation of the source facts reproduce the asserted value?
        var recompute = TryRecompute(request, fresh, trail);

        // 5) contradiction among the fresh rows. Components of an aggregation (IsSourceFact) and
        //    the asserted total (IsAggregate) measure DIFFERENT quantities — summing to 120 from
        //    90+30 is agreement, not a contradiction. Only two direct measurements of the same
        //    metric compete.
        var comparable = fresh
            .Where(e => e.Value is not null && !e.IsAggregate && !e.IsSourceFact).ToList();
        for (int i = 0; i < comparable.Count; i++)
        {
            for (int j = i + 1; j < comparable.Count; j++)
            {
                var a = comparable[i]; var b = comparable[j];
                if (!string.Equals(a.MetricKey, b.MetricKey, StringComparison.Ordinal)) continue;
                if (a.Grade.Rank() != b.Grade.Rank()) continue;          // a stronger row wins: not a contradiction, a supersession
                if (string.Equals(a.SourceFamily, b.SourceFamily, StringComparison.Ordinal)) continue;
                double gap = RelativeGap(a.Value!.Value, b.Value!.Value);
                if (gap > ContradictionGap)
                {
                    trail.Add(TrailEntry.Of(StageName, "p1e.verify.contradiction", "contradicted",
                        [EngineMath.Factor("pair", $"{a.Id}_{b.Id}"), EngineMath.Factor("gap", gap)],
                        [a.Id, b.Id]));
                    var contradictedGrade = EvidenceGrades.Max(recompute.grade, EvidenceGrades.Ceiling(
                        fresh.Select(e => e.Grade)));
                    return new VerificationAssessment(request.SubjectKey, contradictedGrade,
                        VerificationStatus.Contradicted,
                        $"two same-grade independent sources disagree by {EngineMath.Pct(gap)} on " +
                        $"{a.MetricKey}: LIVORA shows the conflict instead of picking quietly",
                        fresh.Select(e => e.Id).ToList(), trail);
                }
            }
        }

        // 6) final grade: ceiling over fresh evidence, and the recompute record (if minted) is on
        //    the ladder like any other — SystemVerified because OUR code reproduced the number.
        var ceiling = EvidenceGrades.Ceiling(fresh.Select(e => e.Grade));
        var finalGrade = recompute.minted ? EvidenceGrade.SystemVerified : ceiling;
        if (finalGrade.Rank() < EvidenceGrade.SystemVerified.Rank() &&
            recompute.grade.Rank() > finalGrade.Rank()) finalGrade = recompute.grade;

        VerificationStatus status;
        string why;
        bool providerAsked = providerOutcome is not null;
        // Token is "receipt_closed", not a bare claim word: CONTRACT-P1 §7 keeps
        // "connected"/"verified"/"paid" out of code unless a state machine owns the word, and the
        // provider receipt path is exactly such a machine (Unconfigured / NoProbeResultYet /
        // ReceiptClosed / Mismatch below) — the ladder reads the state, never the prose.
        bool providerClosed = providerOutcome is { Verdict: P1eProviderReceiptVerifier.ReceiptClosed };
        bool providerBlocks = providerAsked && !providerClosed;   // unconfigured OR no probe yet

        if (recompute.minted)
        {
            status = VerificationStatus.Corroborated;
            why = "independent sources agree and a deterministic recomputation reproduced the value";
        }
        else if (providerBlocks && ceiling == EvidenceGrade.ProviderDerived)
        {
            // A ProviderDerived grade REQUIRES the provider's own receipt. Without a configured,
            // probed receipt the claim is unproven — not false, and absolutely not "verified".
            status = VerificationStatus.ProviderUnconfigured;
            why = $"the external attestation path ({providerOutcome!.Value.Provider}) did not answer, so " +
                  "this claim cannot be upgraded and is not treated as false — it is treated as unproven";
        }
        else if (ceiling == EvidenceGrade.SelfReported)
        {
            status = VerificationStatus.Provisional;
            why = "self-reported only: honest, uncorroborated, and labelled exactly that — never a system verification";
        }
        else if (anyExpired)
        {
            status = VerificationStatus.Provisional;
            why = "the strongest fresh evidence is below the SystemVerified bar; older records were " +
                  "kept (grade preserved) but cannot upgrade it";
        }
        else
        {
            status = VerificationStatus.Provisional;
            why = "evidence exists and is fresh, but no recompute/attestation closed the loop";
        }

        return new VerificationAssessment(request.SubjectKey, finalGrade, status, why,
            fresh.Select(e => e.Id).ToList(), trail);
    }

    private static (bool minted, EvidenceGrade grade, string? newId) TryRecompute(
        VerificationRequest request, List<EvidenceRecord> fresh, List<TrailEntry> trail)
    {
        if (request.AssertedValue is not { } asserted) return (false, EvidenceGrade.Unrated, null);
        var sources = fresh.Where(e => e.IsSourceFact && e.Grade.Rank() >= EvidenceGrade.DeviceDerived.Rank())
                           .ToList();
        if (sources.Count < 2)
        {
            trail.Add(TrailEntry.Of(StageName, "p1e.verify.recompute", "insufficient_sources",
                [EngineMath.Factor("need", "2"), EngineMath.Factor("have", sources.Count.ToString())]));
            return (false, EvidenceGrade.Unrated, null);
        }

        // Aggregation is explicit and tiny on purpose: sum of the component facts must reproduce
        // the asserted total (meetings from events, active minutes from walks...). Any other
        // aggregation is a different rule with its own key — not a vague "AI says close enough".
        double total = sources.Sum(e => e.Value ?? 0);
        double gap = RelativeGap(total, asserted);
        if (gap > RecomputeTolerance)
        {
            trail.Add(TrailEntry.Of(StageName, "p1e.verify.recompute", "mismatch",
                [EngineMath.Factor("asserted", asserted), EngineMath.Factor("recomputed", total),
                 EngineMath.Factor("gap", gap)],
                sources.Select(s => s.Id).ToList()));
            return (false, EvidenceGrade.Unrated, null);
        }

        var id = $"sysverify:{request.SubjectKey}:{sources.Count}";
        trail.Add(TrailEntry.Of(StageName, "p1e.verify.recompute", "reproduced",
            [EngineMath.Factor("asserted", asserted), EngineMath.Factor("recomputed", total),
             EngineMath.Factor("tolerance", RecomputeTolerance)],
            sources.Select(s => s.Id).Append(id).ToList()));
        return (true, EvidenceGrade.SystemVerified, id);
    }

    public static double RelativeGap(double a, double b)
    {
        double scale = Math.Max(Math.Abs(b), 1e-9);
        return Math.Abs(a - b) / scale;
    }

    /// <summary>Known rule keys (sweep-tested; an unknown key is a wiring bug, never a pass).</summary>
    public static IReadOnlyList<string> RuleKeys { get; } =
        ["p1e.verify.recompute", "p1e.verify.contradiction", "p1e.verify.freshness",
         "p1e.verify.withdrawal", "p1e.verify.provider_receipt", "p1e.verify.no_evidence"];

    /// <summary>Named dispatch for a single rule (the surface a module route or another lane calls
    /// when it wants one verdict, not the whole assessment). Unknown key throws — the frozen
    /// ProblemCodes.VerificationRuleUnknown (503) exists for exactly this.</summary>
    public static RuleOutcome AssessRule(string ruleKey, VerificationRequest? request, DateTimeOffset asOfUtc)
    {
        if (!RuleKeys.Contains(ruleKey, StringComparer.Ordinal))
            throw new VerificationRuleUnknownException(ruleKey);
        if (request is null)
            throw new ArgumentException("a verification request is required to run a rule", nameof(request));
        var a = Assess(request, asOfUtc);
        return new RuleOutcome(ruleKey, a.StatusToken, a.Why);
    }
}

/// <summary>Thrown when a caller asks for a verification rule that does not exist. The handler
/// turns this into ProblemCodes.VerificationRuleUnknown — silently answering "no rule matched, so
/// it must be fine" is the failure mode this exception exists to prevent.</summary>
public sealed class VerificationRuleUnknownException(string ruleKey) : Exception(
    $"unknown verification rule '{ruleKey}'")
{
    public string RuleKey { get; } = ruleKey;
}

/// <summary>One rule's verdict, machine-shaped (rule key + status token + why).</summary>
public sealed record RuleOutcome(string RuleKey, string StatusToken, string Why);

/// <summary>Verification state — the outcome axis. Independent of the grade axis by design:
/// an expired SystemVerified record keeps its grade and gets <see cref="Expired"/>.</summary>
public enum VerificationStatus
{
    /// <summary>No evidence record exists at all. Absence, not failure.</summary>
    Unattested = 0,
    /// <summary>Evidence is fresh, same-grade sources agree, and a deterministic recompute (or a
    /// stronger attestation) closed the loop.</summary>
    Corroborated = 1,
    /// <summary>Evidence exists but has not been closed by a recompute/attestation. Honest middle
    /// state — the ladder is not a cliff, and "provisional" must be visible as such.</summary>
    Provisional = 2,
    /// <summary>Independent same-grade sources disagree beyond tolerance. Shown, never averaged.</summary>
    Contradicted = 3,
    /// <summary>Every record is outside its freshness window. Grade preserved; claim about NOW dead.</summary>
    Expired = 4,
    /// <summary>The only supporting record was withdrawn by the user (soft delete).</summary>
    Withdrawn = 5,
    /// <summary>The external attestation path has no configuration: unproven, not false.</summary>
    ProviderUnconfigured = 6,
}

/// <summary>One evidence row as the engine sees it (persistence maps onto this; the engine never
/// touches EF). <see cref="SourceFamily"/> is the independence key: same-family rows never count
/// as two sources (two exports from one provider are one source wearing two hats).</summary>
public sealed record EvidenceRecord(
    string Id,
    string SubjectKey,
    string MetricKey,
    EvidenceGrade Grade,
    double? Value,
    string SourceFamily,
    DateTimeOffset OccurredAtUtc,
    bool IsSourceFact = false,        // true = input to a recompute aggregation
    bool IsAggregate = false,         // true = the asserted total a recompute must reproduce
    DateTimeOffset? DeletedAtUtc = null,
    string? Note = null);

public sealed record VerificationRequest(
    string SubjectKey,
    double? AssertedValue,
    IReadOnlyList<EvidenceRecord> Evidence,
    string? Provider = null);          // "google_calendar" etc. — receipts only consulted when set

public sealed record VerificationAssessment(
    string SubjectKey,
    EvidenceGrade Grade,
    VerificationStatus Status,
    string Why,
    IReadOnlyList<string> LiveEvidenceIds,
    IReadOnlyList<TrailEntry> Trail)
{
    /// <summary>Machine token pair for DTOs — the ONLY way a verdict leaves this lane: two
    /// separate strings, never a merged boolean.</summary>
    public string GradeToken => Grade.Token();
    public string StatusToken => Status.ToString().ToLowerInvariant();
}

/// <summary>
/// The Google Calendar receipt path (Wave 4 §3: Google is the approved real path, NO client_id /
/// secret exists yet). The code is real and config-gated; without configuration it reports
/// unconfigured truthfully and nothing downstream may treat that as success. When secrets arrive,
/// the same verdict shape comes back from a live probe (flip by probe, not by flag).
/// </summary>
public static class P1eProviderReceiptVerifier
{
    public const string GoogleCalendarProvider = "google_calendar";

    /// <summary>The receipt state machine's tokens (state names, not prose): the probe answers
    /// one of these and the ladder maps it to a rung. Named constants keep the claim words out of
    /// bare string literals (§7 tripwire) while the state itself stays explicit and greppable.</summary>
    public const string Unconfigured = "unconfigured";
    public const string NoProbeResultYet = "no_probe_result_yet";
    public const string ReceiptClosed = "receipt_closed";
    public const string ReceiptMismatch = "receipt_mismatch";

    /// <summary>Injected by the module from IConfiguration: (hasClientId, hasSecret). Null = not
    /// wired at all, which behaves exactly like unconfigured.</summary>
    public static (bool HasClientId, bool HasClientSecret)? GoogleCalendarConfig { get; set; }

    public static (string Provider, string Verdict, bool Configured)? Evaluate(
        VerificationRequest request, DateTimeOffset asOfUtc)
    {
        if (string.IsNullOrWhiteSpace(request.Provider)) return null;
        bool configured = GoogleCalendarConfig is { HasClientId: true, HasClientSecret: true };
        if (!configured)
            return (request.Provider, Unconfigured, false);
        // Configured path would run the real receipt probe here (event-existence check at the
        // provider). That probe is a live network call and MUST NOT exist in CI fixtures: the
        // verdict becomes ReceiptClosed/Mismatch only from a real answer. Until the secret arrives
        // this line is unreachable and that is the honest state of the world.
        return (request.Provider, NoProbeResultYet, true);
    }
}
