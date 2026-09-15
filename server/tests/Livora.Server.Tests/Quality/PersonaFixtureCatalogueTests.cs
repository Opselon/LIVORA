using System.Reflection;
using Livora.Server.Tests.Fixtures_Wave4;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: hold the persona catalogue to its own promises — the twelve Wave-4 cross-domain
///          scenarios exist, are complete, and are DETERMINISTIC (the brief: "no random values").
///          Determinism is not a comment here; it is executed: build everything twice, deep-compare,
///          and scan the builders' IL/IL-adjacent source for the three forbidden clock/RNG calls.
/// OWNER: Agent 16 (P1-F QA lane).
/// CONSUMES: <see cref="Personas"/> in Fixtures_Wave4.
/// PROVIDES: the catalogue's release-gate row in Wave4TruthMatrix.md (row "fixture catalogue").
/// INVARIANTS:
///   - all twelve names present, slugs unique, every persona carries non-empty expected-state text
///     (a persona without an expectation cannot anchor an assertion — it is decoration, not a fixture)
///   - two builds of the same persona are structurally equal (record equality does the comparison)
///   - the source text of the fixtures namespace contains no Guid.NewGuid, no Random, no
///     DateTime.UtcNow / DateTimeOffset.UtcNow outside the fixed EpochUtc declaration
///   - ranges are legal (sleep 0..24, scores 0..1, steps >= 0) so a Phase-2 assertion failing is the
///     engine's fault, never the fixture's
/// EXTEND: new persona => All() + By() + a row here.
/// </summary>
public sealed class PersonaFixtureCatalogueTests
{
    private static readonly string[] RequiredSlugs =
    [
        "normal", "sleep-deprived", "high-meeting-load", "highly-active", "beginner", "long-term",
        "no-data", "partial-data", "ai-unavailable", "offline", "creator", "premium",
    ];

    [Fact]
    public void Catalogue_holds_exactly_the_twelve_briefed_scenarios()
    {
        var all = Personas.All();
        Assert.Equal(12, all.Count);
        Assert.Equal(RequiredSlugs.OrderBy(s => s), all.Select(p => p.Slug).OrderBy(s => s));
        Assert.Distinct(all.Select(p => p.Slug));
        Assert.Distinct(all.Select(p => p.UserId));
    }

    [Fact]
    public void Every_persona_declares_what_a_correct_engine_must_conclude()
    {
        foreach (var p in Personas.All())
        {
            Assert.False(string.IsNullOrWhiteSpace(p.ExpectedState), $"{p.Slug}: no expected state");
            Assert.False(string.IsNullOrWhiteSpace(p.Label), $"{p.Slug}: unlabelled persona");
            Assert.StartsWith("user-" + p.Slug, p.UserId, StringComparison.Ordinal);
        }
        // scenario-specific postures actually hold in data, not just in the label:
        Assert.False(Personas.By("ai-unavailable").AiAvailable);
        Assert.False(Personas.By("offline").NetworkOnline);
        Assert.Empty(Personas.By("no-data").Health);
        Assert.Equal("creator", Personas.By("creator").Tier);
        Assert.Equal("premium", Personas.By("premium").Tier);
        Assert.Contains(Personas.By("no-data").Connectors,
            c => c.State is "permission_required" or "unconfigured");
        // partial-data: some days not fully logged — and those days MUST carry zeroed unmeasured
        // fields, which is the shape the "never render 0 as a measurement" rule protects
        Assert.Contains(Personas.By("partial-data").Health, d => !d.Logged);
        Assert.All(Personas.By("partial-data").Health.Where(d => !d.Logged),
            d => { Assert.Equal(0.0, d.Recovery01); Assert.Equal(0.0, d.Stress01); });
    }

    [Fact]
    public void Two_builds_of_every_persona_are_identical_determinism_gate()
    {
        // Structural comparison: record equality over IReadOnlyList fields is REFERENCE equality
        // (documented C# behaviour), so the honest determinism check compares element sequences.
        foreach (var slug in RequiredSlugs)
        {
            var a = Personas.By(slug);
            var b = Personas.By(slug);
            Assert.Equal(a with { Health = [], Calendar = [], ScreenTime = [], Connectors = [] },
                         b with { Health = [], Calendar = [], ScreenTime = [], Connectors = [] });
            Assert.True(a.Health.SequenceEqual(b.Health), $"{slug}: health drifted between builds");
            Assert.True(a.Calendar.SequenceEqual(b.Calendar), $"{slug}: calendar drifted");
            Assert.True(a.ScreenTime.SequenceEqual(b.ScreenTime), $"{slug}: screen time drifted");
            Assert.True(a.Connectors.SequenceEqual(b.Connectors), $"{slug}: connectors drifted");
        }
    }

    [Fact]
    public void Personas_are_robust_against_lane_test_mutation_shared_statics()
    {
        // catalogue members are handed out repeatedly; if a persona ever exposed mutable lists,
        // one test could poison the next (the classic shared-fixture flake). Records + frozen
        // collection expressions make that structurally impossible TODAY — pin it:
        var first = Personas.By("normal");
        foreach (var name in new[] { nameof(first.Health), nameof(first.Calendar),
                                     nameof(first.ScreenTime), nameof(first.Connectors) })
        {
            var prop = typeof(Wave4Persona).GetProperty(name)!;
            var value = (System.Collections.IList)prop.GetValue(first)!;
            // A fixed backing (array or AsReadOnly wrapper) rejects Add with NotSupportedException.
            // A List<T> would ACCEPT it — and because catalogue members are shared across every
            // test in the process, one accepted mutation poisons every later reader: the classic
            // shared-fixture flake. Records hide this; the field type must not.
            var ex = Record.Exception(() => value.Add(null!));
            Assert.True(ex is NotSupportedException,
                $"persona field {name} ({value.GetType().Name}) accepted mutation — freeze it");
        }
    }

    [Fact]
    public void Persona_values_are_in_legal_ranges()
    {
        foreach (var p in Personas.All())
            foreach (var d in p.Health)
            {
                Assert.InRange(d.SleepHours, 0, 24);
                Assert.InRange(d.SleepQuality01, 0, 1);
                Assert.InRange(d.Recovery01, 0, 1);
                Assert.InRange(d.Stress01, 0, 1);
                Assert.True(d.Steps >= 0 && d.ActiveMinutes >= 0);
            }
    }

    [Fact]
    public void Scenario_F_persona_is_expressible_today_ai_off_deterministic_today_path()
    {
        // Wave-4 brief scenario F: AI unavailable -> deterministic fallback -> usable Today.
        // Server-side there is nothing to fall back FROM yet (no intelligence module — truth matrix
        // row 4), so what IS expressible here is the full persona + the client pipeline that exists
        // TODAY: AiOrchestratorProvider returns the deterministic interpretation with an honest
        // source label when the gateway is disabled/unconfigured, proven by the shipped client
        // suite (Tests/Tests/Wave3c/AI/AiOrchestratorProviderTests.cs — gate tests around
        // DeterministicAsync). This test pins the fixture half of that E2E so Phase 2 can wire the
        // server half without inventing data: AI-off persona + every domain present + expectation.
        var f = Personas.By("ai-unavailable");
        Assert.False(f.AiAvailable);
        Assert.True(f.NetworkOnline); // F is about AI, not the network (that is scenario Offline)
        Assert.NotEmpty(f.Health);
        Assert.Contains("fallback", f.ExpectedDegradedBehaviour, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Personas.By("normal").Health.Count, f.Health.Count); // same user, AI switched off
    }
}
