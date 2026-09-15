namespace Livora.Server.Tests.Fixtures_Wave4;

/// <summary>
/// PURPOSE: the deterministic cross-domain test-fixture catalogue the Wave 4 brief demands: the
///          twelve user scenarios (Normal, Sleep-Deprived, High-Meeting-Load, Highly-Active,
///          Beginner, Long-Term, No-Data, Partial-Data, AI-Unavailable, Offline, Creator, Premium)
///          as reusable builders that Phase-2 lanes (and P1-E's engines today) can drive without
///          inventing their own data. "Cross-domain" means ONE persona supplies every domain the
///          product reasons about at once: health days, calendar load, screen time, account/tier,
///          connector truth, AI availability, network posture.
/// OWNER: Agent 16 (P1-F QA lane) — the brief assigns this catalogue to the quality lane.
/// CONSUMES: nothing (pure data; no EF, no host, no client assembly — it must compile before any
///           Phase-2 domain exists).
/// PROVIDES: <see cref="Wave4Persona"/> (the shape) + <see cref="Personas.All"/> /
///           <see cref="Personas.By"/> (the twelve seeded scenarios) +
///           <see cref="HealthDay"/> and friends.
/// INVARIANTS (the catalogue's whole point — mechanical, checked by PersonaFixtureCatalogueTests):
///   - NO randomness anywhere: no Guid.NewGuid, no Random, no DateTime.UtcNow in this namespace.
///     Every id is a literal slug; every value is a fixed number or computed from the persona's
///     own deterministic day-index. Same input, same output, on every machine, every run —
///     a flaky fixture catalogue is worse than none, because lanes start trusting "usually".
///   - NO wall-clock reads: the fixed epoch <see cref="Personas.EpochUtc"/> anchors every
///     timestamp, so a persona cannot drift across midnight or time zones.
///   - labelled, not hidden: every persona names itself (<see cref="Wave4Persona.Label"/>) and the
///     data is synthetic by construction — builders are deterministic mock (labelled), the only
///     kind product law §0.1 allows ("real, deterministic-mock (labelled), or computed").
///   - values are plausible but never "real": minutes, steps, scores within legal ranges so the
///     engines' own validation cannot be blamed for fixture noise; each scenario's EXPECTED
///     interpretation is carried alongside the data (expectedState) so a test can assert the
///     engine agrees with the scenario, not just that it ran.
/// EXTEND: a new persona is a new named builder here + a row in the catalogue test. Never edit an
///         existing persona's numbers: Phase-2 assertions will pin them.
/// </summary>
public sealed record HealthDay(
    int DayIndex,
    double SleepHours,
    double SleepQuality01,
    int Steps,
    int ActiveMinutes,
    double Recovery01,
    double Stress01,
    bool Logged);

/// <summary>Calendar-load slice of a persona — the "High-Meeting-Load" domain.</summary>
public sealed record CalendarDay(int DayIndex, int MeetingMinutes, int FreeMinutes);

/// <summary>Screen-time slice — the domain the personalization engine reads.</summary>
public sealed record ScreenDay(int DayIndex, int TotalMinutes, int LateNightMinutes);

/// <summary>The connector truth a scenario presents — never a guessed "connected".</summary>
public sealed record ConnectorTruth(string Provider, string State, string Detail);

/// <summary>One complete cross-domain persona. All lists are 90 days unless the scenario says less.</summary>
public sealed record Wave4Persona(
    string Slug,
    string Label,
    string Tier,                 // "free" | "premium" | "creator" (mirrors AccountTier names, lowercase)
    string Locale,               // "en" | "fa"
    string UserId,               // deterministic literal id, never a GUID call
    IReadOnlyList<HealthDay> Health,
    IReadOnlyList<CalendarDay> Calendar,
    IReadOnlyList<ScreenDay> ScreenTime,
    IReadOnlyList<ConnectorTruth> Connectors,
    bool AiAvailable,            // false => the deterministic fallback path MUST serve
    bool NetworkOnline,          // false => offline posture, queue must hold honestly
    DateTimeOffset CreatedAtUtc,
    /// <summary>What a correct engine must conclude about this persona (the assertion anchor).</summary>
    string ExpectedState,
    /// <summary>What the Today surface must still show when AI is off / network is off.</summary>
    string ExpectedDegradedBehaviour);

/// <summary>
/// The twelve seeded scenarios. Every number below is a literal or an index-computed value:
/// running this class twice in the same process (or on two machines) yields byte-equal data.
/// </summary>
public static class Personas
{
    /// <summary>Fixed clock anchor: 2026-03-01T00:00:00Z (a stable, non-dst-ambiguous instant).</summary>
    public static readonly DateTimeOffset EpochUtc =
        new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset DayUtc(int index) => EpochUtc.AddDays(index);

    private const int Days = 90;

    /// <summary>Smooth deterministic wobble in [-amp, +amp] with period ~7 days — no RNG.</summary>
    private static double Wobble(int day, int phase, double amp) =>
        amp * Math.Sin((day + phase) * Math.PI / 7.0);

    public static Wave4Persona Normal()
    {
        var health = Enumerable.Range(0, Days).Select(d => new HealthDay(
            d, SleepHours: Round(7.6 + Wobble(d, 0, 0.25), 2), SleepQuality01: 0.78,
            Steps: 8200 + (d % 5) * 150, ActiveMinutes: 32 + (d % 4) * 3,
            Recovery01: 0.70, Stress01: 0.38, Logged: true)).ToArray();
        return Build("normal", "Normal", "free", "en", health,
            meetings: d => 60 + (d % 3) * 20, screen: d => 180 + (d % 7) * 10,
            ai: true, online: true, accountAgeDays: 120,
            expected: "balanced", degraded: "n/a (all dependencies up)");
    }

    public static Wave4Persona SleepDeprived()
    {
        // last 6 days collapse to ~5.2 h with a visible debt vs the 7.5 h baseline
        var health = Enumerable.Range(0, Days).Select(d => d >= Days - 6
            ? new HealthDay(d, 5.2 + (d % 2) * 0.2, 0.42, 5200, 12, 0.35, 0.62, true)
            : new HealthDay(d, 7.5 + Wobble(d, 1, 0.2), 0.75, 8000, 30, 0.68, 0.40, true)).ToArray();
        return Build("sleep-deprived", "Sleep-Deprived", "free", "en", health,
            meetings: d => 70, screen: d => 220,
            ai: true, online: true, accountAgeDays: 90,
            expected: "sleep-debt (≥1.5 h below baseline: the 5.2 vs 7.5 leg must fire)",
            degraded: "n/a");
    }

    public static Wave4Persona HighMeetingLoad()
    {
        var health = Enumerable.Range(0, Days).Select(d => new HealthDay(
            d, 7.1, 0.7, 6400, 18, 0.6, 0.55, true)).ToArray();
        return Build("high-meeting-load", "High-Meeting-Load", "premium", "en", health,
            // meetings eat 4.5 h of every weekday; weekends stay free
            meetings: d => DayOfWeekIndex(d) is 5 or 6 ? 15 : 270,
            screen: d => 320 + (d % 5) * 15,
            ai: true, online: true, accountAgeDays: 200,
            expected: "time-constrained: plan must compress, never pretend free time exists",
            degraded: "n/a");
    }

    public static Wave4Persona HighlyActive()
    {
        var health = Enumerable.Range(0, Days).Select(d => new HealthDay(
            d, 8.0, 0.85, 16500 + (d % 3) * 900, 110 + (d % 4) * 6, 0.82, 0.25, true)).ToList();
        return Build("highly-active", "Highly-Active", "free", "fa", health,
            meetings: d => 30, screen: d => 90,
            ai: true, online: true, accountAgeDays: 365,
            expected: "high-load-but-recovered: momentum, not deficit",
            degraded: "n/a");
    }

    public static Wave4Persona Beginner()
    {
        // only 9 days of history — under every engine's minimum-window gate
        var health = Enumerable.Range(Days - 9, 9).Select(d => new HealthDay(
            d, 7.2, 0.65, 6000, 20, 0.6, 0.5, true)).ToArray();
        return Build("beginner", "Beginner", "free", "en", health,
            meetings: d => 60, screen: d => 200,
            ai: true, online: true, accountAgeDays: 9,
            expected: "insufficient-data: baselines must REFUSE, not extrapolate 9 days to 30",
            degraded: "Today shows empty-state honesty, no trend claims");
    }

    public static Wave4Persona LongTerm()
    {
        var health = Enumerable.Range(0, Days).Select(d => new HealthDay(
            d, 7.4 + Wobble(d, 3, 0.3), 0.76, 7500 + (d % 7) * 300, 28 + (d % 5) * 2,
            0.66, 0.44, true)).ToArray();
        return Build("long-term", "Long-Term", "premium", "en", health,
            meetings: d => 90, screen: d => 160,
            ai: true, online: true, accountAgeDays: 730,
            expected: "stable with slow drift; windows all satisfied",
            degraded: "n/a");
    }

    public static Wave4Persona NoData() =>
        Build("no-data", "No-Data", "free", "fa", [],
            meetings: _ => 0, screen: _ => 0,
            ai: true, online: true, accountAgeDays: 0,
            expected: "no-baseline: every derived number must be absent, not zero",
            degraded: "Today must be usable with logging CTA only; zero metrics rendered")
        with
        {
            // no connector has EVER answered: the honest state, not "connected"
            Connectors = new[]
            {
                new ConnectorTruth("healthconnect", "permission_required", "user has not granted yet"),
                new ConnectorTruth("googlecalendar", "unconfigured", "no client id (contract §3)"),
            },
        };

    public static Wave4Persona PartialData()
    {
        // sleep + steps present; recovery/stress absent on 40% of days (the null-shaped 0 below is
        // carried by Logged=false rows so engines cannot read "0.0 recovery" as a real low value)
        var health = Enumerable.Range(0, Days).Select(d => d % 5 < 2
            ? new HealthDay(d, 7.0 + Wobble(d, 2, 0.3), 0.6, 7000 + (d % 4) * 250, 20, 0.0, 0.0, Logged: false)
            : new HealthDay(d, 7.0 + Wobble(d, 2, 0.3), 0.6, 7000 + (d % 4) * 250, 20, 0.6, 0.5, true)).ToArray();
        return Build("partial-data", "Partial-Data", "free", "en", health,
            meetings: d => 60, screen: d => 150,
            ai: true, online: true, accountAgeDays: 150,
            expected: "confidence down-weighted by missing days; metrics show as-of dates",
            degraded: "missing fields render as missing, never as 0");
    }

    public static Wave4Persona AiUnavailable()
    {
        var p = Normal() with
        {
            Slug = "ai-unavailable", Label = "AI-Unavailable", UserId = "user-ai-unavailable-0001",
            AiAvailable = false,
            ExpectedState = "rules-owned interpretation; AI contributes nothing",
            ExpectedDegradedBehaviour = "deterministic fallback serves Today with honest provenance " +
                "(the §-scenario F shape: every card labelled, no AI text, no crash, no blank page)",
        };
        return p;
    }

    public static Wave4Persona Offline()
    {
        var p = Normal() with
        {
            Slug = "offline", Label = "Offline", UserId = "user-offline-0001", NetworkOnline = false,
            ExpectedState = "local truth only; connector rows say disconnected, not stale-ok",
            ExpectedDegradedBehaviour = "queue accepts writes, drain says 'not connected'; UI badges data age",
        };
        return p with { Connectors = p.Connectors
            .Select(c => c.Provider == "healthconnect"
                ? c with { State = "disconnected", Detail = "network unreachable, last sync 36 h ago" }
                : c).ToArray() };
    }

    public static Wave4Persona Creator()
    {
        var p = Normal() with
        {
            Slug = "creator", Label = "Creator", UserId = "user-creator-0001", Tier = "creator",
            ExpectedState = "creator role + own-programs ownership checks (IDOR leg)",
            ExpectedDegradedBehaviour = "n/a",
        };
        return p;
    }

    public static Wave4Persona Premium()
    {
        var p = Normal() with
        {
            Slug = "premium", Label = "Premium", UserId = "user-premium-0001", Tier = "premium",
            ExpectedState = "entitlement from server state, never from a client flag",
            ExpectedDegradedBehaviour = "payment provider unconfigured must not revoke an existing entitlement",
        };
        return p;
    }

    public static IReadOnlyList<Wave4Persona> All() =>
    [
        Normal(), SleepDeprived(), HighMeetingLoad(), HighlyActive(), Beginner(), LongTerm(),
        NoData(), PartialData(), AiUnavailable(), Offline(), Creator(), Premium(),
    ];

    public static Wave4Persona By(string slug) =>
        All().FirstOrDefault(p => p.Slug == slug)
        ?? throw new KeyNotFoundException($"persona '{slug}' is not in the catalogue of " +
                                          $"{All().Count} — extend here, never inline in a lane test");

    // ---------------------------------------------------------------- shared builders

    private static Wave4Persona Build(
        string slug, string label, string tier, string locale, IReadOnlyList<HealthDay> health,
        Func<int, int> meetings, Func<int, int> screen, bool ai, bool online,
        int accountAgeDays, string expected, string degraded)
    {
        // A no-history persona is no-history in EVERY domain: inventing calendar rows for it would
        // break the scenario the catalogue exists to express.
        var calendar = health.Count == 0
            ? Array.Empty<CalendarDay>()
            : Enumerable.Range(0, Days).Select(d => new CalendarDay(
                d, meetings(d), Math.Max(0, 960 - meetings(d)))).ToArray();
        var screens = health.Count == 0
            ? Array.Empty<ScreenDay>()
            : Enumerable.Range(0, Days).Select(d => new ScreenDay(
                d, screen(d), LateNightMinutes: d % 9 == 0 ? 45 : 0)).ToArray();
        return new Wave4Persona(
            slug, label, tier, locale, $"user-{slug}-0001",
            health.ToArray(), calendar.ToArray(), screens.ToArray(),
            DefaultConnectors(ai, online),
            ai, online,
            EpochUtc.AddDays(-accountAgeDays),
            expected, degraded);
    }

    /// <summary>All personas share the same honest connector spine; scenarios override what differs.
    /// Google is UNCONFIGURED everywhere at Phase 1 (contract §3: no client secret exists) — the
    /// catalogue encodes the truth so no lane has to remember it.</summary>
    private static IReadOnlyList<ConnectorTruth> DefaultConnectors(bool ai, bool online) =>
    [
        new ConnectorTruth("healthconnect", online ? "ok" : "disconnected",
            online ? "last sync 2 h ago (fixture posture, not a measurement)" : "network unreachable"),
        new ConnectorTruth("googlecalendar", "unconfigured", "Identity:Google:ClientId is empty in Phase 1"),
        new ConnectorTruth("ai-gateway", ai ? "ok" : "degraded",
            ai ? "fixture posture: gateway answered the probe" : "provider_unavailable (fixture)"),
    ];

    private static int DayOfWeekIndex(int dayIndex) =>
        (int)((EpochUtc.AddDays(dayIndex)).DayOfWeek); // 5=Sat, 6=Sun

    private static double Round(double v, int digits) => Math.Round(v, digits, MidpointRounding.AwayFromZero);
}
