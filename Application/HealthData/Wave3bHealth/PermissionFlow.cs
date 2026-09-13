namespace LIVORA.Application.HealthData.Wave3bHealth;

using LIVORA.Domain.Enums;

// Wave 3b (lane 02) — the permission ask, as a state machine with a budget.
//
// Two failure modes this exists to kill:
//   1. The prompt loop. A screen that calls RequestPermission on every render can ask a user who
//      already pressed "Don't allow" ten times a minute. Android stops showing the dialog after
//      the second denial anyway, so the app silently reads a stale "not granted" forever — and the
//      user reads a wall of nothing. The max-asks-per-session rule makes the SECOND denial the
//      last ask, in code, deterministically, with a count a test can read.
//   2. Asking for an integration that cannot deliver (PermissionService's Wave 2 note calls this
//      "trust debt"). A bridge whose probe is not PlatformPresent is never prompted at all: the
//      flow goes straight to Unavailable and says so.
//
// Pure layer: it talks to IHealthPlatformBridge only, so the plain-net10.0 test project drives the
// whole machine. It emits localization keys (via PermissionStatusKey), never prose (§0.6).

/// <summary>
/// Where the flow stands: NotAsked → Requested → Granted/Denied/Unavailable. Once terminal it stays
/// terminal: a Denied user who changes their mind does so in the OS settings, which the next probe
/// observes as a fresh grant — the app never re-asks behind a refusal (§0.4 trust accounting).
/// </summary>
public enum PermissionPhase
{
    /// <summary>Nothing queried or asked yet.</summary>
    NotAsked = 0,
    /// <summary>Queried (or asked) once; the platform has not answered with a grant yet.</summary>
    Requested = 1,
    /// <summary>Granted — reads are allowed through this seam.</summary>
    Granted = 2,
    /// <summary>Explicitly denied (or the ask budget is spent on denials).</summary>
    Denied = 3,
    /// <summary>No integration on this head to grant anything to — distinct from Denied.</summary>
    Unavailable = 4,
}

/// <summary>Machine-readable outcome of one <c>EnsureAsync</c> call (tags, never display text).</summary>
public sealed record PermissionAttempt(
    PermissionPhase Phase,
    // Docs as plain comments: XML comments on record positional parameters warn (CS1587).
    //   PromptedPlatform — this call actually reached the platform prompt (the loop-sensitive counter)
    //   CanRead          — the caller may read provider data after this attempt
    //   BlockedReason    — why nothing happened when the phase did not advance (machine tag)
    bool PromptedPlatform,
    bool CanRead,
    string BlockedReason)
{
    /// <summary>Localization key for the resulting phase line (0 args).</summary>
    public string StatusKey => HealthCapabilityMap.PermissionStatusKey(PermissionFlow.ToState(Phase));
}

/// <summary>
/// Deterministic permission state machine over one <see cref="IHealthPlatformBridge"/>. Not
/// thread-safe by design: one flow instance belongs to one session/UI owner, and the ask budget is
/// a per-session counter, so a shared instance would let two panels spend the same allowance twice.
/// </summary>
public sealed class PermissionFlow
{
    /// <summary>The documented default: after two asks the app stops asking this session.</summary>
    public const int DefaultMaxAsksPerSession = 2;

    // Blocked-reason tags (machine-facing; a test pins them, the UI maps them to keys if it wants).
    public const string ReasonBudgetExhausted = "ask-budget-exhausted";
    public const string ReasonPlatformUnavailable = "platform-unavailable";
    public const string ReasonAlreadyDenied = "already-denied";
    public const string ReasonGrantedAtQuery = "granted-without-prompt";
    public const string ReasonPrompted = "prompted";
    public const string ReasonQuerySaysDenied = "query-reports-denied";   // used by the budget path

    private readonly IHealthPlatformBridge _bridge;

    /// <param name="bridge">The platform seam to ask. A null bridge is not a thing: the composition
    /// root passes <see cref="UnsupportedHealthPlatformBridge"/> on heads with no integration, so
    /// the flow always has something honest to query.</param>
    /// <param name="maxAsksPerSession">Prompt budget for this session. 0 means "never prompt" — a
    /// legitimate setting (query only), not a mistake, so it is allowed; a negative cap is.</param>
    public PermissionFlow(IHealthPlatformBridge bridge, int maxAsksPerSession = DefaultMaxAsksPerSession)
    {
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        if (maxAsksPerSession < 0)
            throw new ArgumentOutOfRangeException(nameof(maxAsksPerSession));   // no prose in this layer
        MaxAsksPerSession = maxAsksPerSession;
    }

    /// <summary>The configured cap (readable so the UI can say "try again in settings" honestly).</summary>
    public int MaxAsksPerSession { get; }

    /// <summary>Platform prompts actually sent so far this session.</summary>
    public int AskCount { get; private set; }

    public PermissionPhase Phase { get; private set; } = PermissionPhase.NotAsked;

    /// <summary>The reason the last attempt did not prompt, or empty. Machine tag.</summary>
    public string LastBlockedReason { get; private set; } = string.Empty;

    /// <summary>True when an ask is still allowed (budget left and the phase not terminal).</summary>
    public bool CanAsk =>
        AskCount < MaxAsksPerSession
        && Phase is PermissionPhase.NotAsked or PermissionPhase.Requested;

    /// <summary>The mapped domain state — the same enum <c>IPermissionService</c> reports, so the Settings page
    /// needs no new concept to display a health permission.</summary>
    public PermissionState State => ToState(Phase);

    /// <summary>Localization key for the current phase line (resolve through ILocalizationService).</summary>
    public string StatusKey => HealthCapabilityMap.PermissionStatusKey(State);

    /// <summary>
    /// Key for the "we will not ask again this session" line, shown only once the budget is spent —
    /// the honest replacement for a prompt the user would otherwise never see and never be told about.
    /// </summary>
    public const string AskLimitStatusKey = "Health.Cap.Permission.AskLimit";

    /// <summary>True when the flow is holding the UI's "ask again?" affordance closed by budget.</summary>
    public bool AskBudgetExhausted => !CanAsk && Phase is PermissionPhase.NotAsked or PermissionPhase.Requested;

    /// <summary>Args for <see cref="AskLimitStatusKey"/>.</summary>
    public IReadOnlyList<object> AskLimitArgs => new object[] { AskCount };

    /// <summary>
    /// The one entry point: query, and prompt only if the platform is real, the budget allows it and
    /// nothing was already settled. Repeat calls are idempotent once terminal, so a page that
    /// re-enters can call this unconditionally without building a prompt loop.
    /// </summary>
    public async Task<PermissionAttempt> EnsureAsync(CancellationToken ct = default)
    {
        // 1. Terminal states never re-open within a session (Denied -> the user goes to settings).
        if (Phase == PermissionPhase.Granted)
            return new PermissionAttempt(Phase, false, true, ReasonGrantedAtQuery);
        if (Phase is PermissionPhase.Denied or PermissionPhase.Unavailable)
            return new PermissionAttempt(Phase, false, false, ReasonAlreadyDenied);

        // 2. No real platform → no prompt, ever. Unavailable is not Denied: nothing was refused.
        var probe = _bridge.Probe();
        if (!probe.PlatformPresent)
        {
            Phase = PermissionPhase.Unavailable;
            LastBlockedReason = ReasonPlatformUnavailable;
            return new PermissionAttempt(Phase, false, false, LastBlockedReason);
        }

        // 3. Cheap query first: an existing grant must not cost the user a dialog.
        var queried = _bridge.QueryPermission();
        if (queried == PermissionState.Granted)
        {
            Phase = PermissionPhase.Granted;
            LastBlockedReason = ReasonGrantedAtQuery;
            return new PermissionAttempt(Phase, false, true, LastBlockedReason);
        }
        if (queried == PermissionState.UnavailableInPhase)
        {
            Phase = PermissionPhase.Unavailable;
            LastBlockedReason = ReasonPlatformUnavailable;
            return new PermissionAttempt(Phase, false, false, LastBlockedReason);
        }

        // 4. The budget. This is the whole anti-prompt-loop rule: once the cap is spent the flow
        //    stops touching the platform, however many times the UI calls it. The phase only
        //    hardens into Denied when the platform itself said so — "we ran out of asks" is
        //    reported as the blocked reason, never laundered into a refusal the user never made.
        if (!CanAsk)
        {
            if (queried == PermissionState.Denied)
            {
                Phase = PermissionPhase.Denied;
                LastBlockedReason = ReasonQuerySaysDenied;
            }
            else
            {
                LastBlockedReason = ReasonBudgetExhausted;
            }
            return new PermissionAttempt(Phase, false, false, LastBlockedReason);
        }

        // 5. Ask — once, and counted before the await so a thrown platform call cannot buy a free retry.
        AskCount++;
        Phase = PermissionPhase.Requested;
        var answer = await _bridge.RequestPermissionAsync(ct);
        switch (answer)
        {
            case PermissionState.Granted:
                Phase = PermissionPhase.Granted;
                LastBlockedReason = ReasonPrompted;
                return new PermissionAttempt(Phase, true, true, LastBlockedReason);
            case PermissionState.UnavailableInPhase:
                Phase = PermissionPhase.Unavailable;
                LastBlockedReason = ReasonPlatformUnavailable;
                return new PermissionAttempt(Phase, true, false, LastBlockedReason);
            case PermissionState.Denied:
                Phase = PermissionPhase.Denied;
                LastBlockedReason = ReasonPrompted;
                return new PermissionAttempt(Phase, true, false, LastBlockedReason);
            default:
                // NotDetermined after an ask: Android suppressed the dialog (two denials in the
                // system UI) or the answer never came. Stay Requested and let the budget close
                // the loop — claiming Denied here would invent a refusal the user did not make.
                LastBlockedReason = ReasonPrompted;
                return new PermissionAttempt(Phase, true, false, LastBlockedReason);
        }
    }

    /// <summary>
    /// Whether a data read may be attempted: Ready platform AND a grant the flow actually holds.
    /// Exposed as one expression so no caller can re-derive it wrong ("granted" on a mock head).
    /// </summary>
    public bool MayRead =>
        Phase == PermissionPhase.Granted && _bridge.Probe().Availability == BridgeAvailability.Ready;

    /// <summary>Maps the flow onto the existing domain enum (no new permission concept in Domain).</summary>
    public static PermissionState ToState(PermissionPhase phase) => phase switch
    {
        PermissionPhase.Granted => PermissionState.Granted,
        PermissionPhase.Denied => PermissionState.Denied,
        PermissionPhase.Unavailable => PermissionState.UnavailableInPhase,
        // NotAsked and Requested are both "the OS has not answered yes yet" — NotDetermined is the
        // only existing value that says that without implying refusal.
        _ => PermissionState.NotDetermined,
    };
}
