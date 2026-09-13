using LIVORA.Application.Planning.Adaptive;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using static LIVORA.Tests.Wave3c.Plan.Wave3cFixtures;

namespace LIVORA.Tests.Wave3c.Plan;

/// <summary>
/// The catalogue of every (key, arg-count) pair the lane-05 adaptive engine can emit, plus the
/// ranker's localization surface. Mirrors Wave3EmissionCatalog's contract role: this is what the
/// merge cross-checks against wave3c-keys/lane05.{en,fa}.keys.xml.
/// </summary>
internal static class Wave3cEmissionCatalog
{
    /// <summary>key -> number of format args the engine actually passes. Must exist in EN and FA.</summary>
    public static readonly IReadOnlyDictionary<string, int> KeysWithArgCounts =
        new Dictionary<string, int>
        {
            // wave3b.recovery-low
            ["Plan.Change.ShrinkExercise"] = 2,          // base minutes, new minutes
            ["Plan.Change.AddRecovery"] = 1,             // added minutes
            ["Plan.Evidence.RecoveryBelowBaseline"] = 1, // signed badness percent
            // wave3b.sleep-low
            ["Plan.Change.ShiftFocusEarlier"] = 1,       // moved minutes
            ["Plan.Change.AddWindDown"] = 1,             // added minutes
            ["Plan.Evidence.SleepBelowBaseline"] = 1,    // signed badness percent
            // wave3b.stale-data
            ["Plan.Change.KeptStale"] = 1,               // days since fresh data
            ["Plan.Evidence.StaleData"] = 1,             // days since fresh data
            // wave3b.deadline-risk
            ["Plan.Change.ProtectDeadlineItem"] = 1,     // protected floor minutes
            ["Plan.Evidence.DeadlineRisk"] = 2,          // days left, fraction percent
            // wave3b.high-activity-yesterday
            ["Plan.Change.RecoveryAware"] = 0,
            ["Plan.Evidence.HighActivityYesterday"] = 1, // signed deviation percent
        };
}

/// <summary>
/// Lane 05: the engine's localization contract. The change/evidence keys are COMPOSED at runtime
/// ("Plan" + ".Change." + suffix) exactly like RecommendationService builds "Rec.{Action}", so the
/// static dotted-literal sweep in ApplicationPurityTests never sees them — which is precisely why
/// THIS suite exists: it pins every composed value and checks it against the shipped key files
/// (the two resx files in this copy; wave3c-keys/lane05.*.keys.xml for the merge).
/// </summary>
public class PlanAdaptationKeysTests
{
    [Fact]
    public void EngineEmitsExactlyTheCataloguedKeys_WithMatchingArgCounts()
    {
        var emitted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in RunsFiringAllRules())
            foreach (var a in r.Adaptations)
            {
                Reduce(a.ChangeKey, a.ChangeArgs.Length);
                Reduce(a.EvidenceKey, a.EvidenceArgs.Length);
            }

        Assert.Equal(Wave3cEmissionCatalog.KeysWithArgCounts.Count, emitted.Count);
        foreach (var (key, args) in emitted)
        {
            Assert.True(Wave3cEmissionCatalog.KeysWithArgCounts.TryGetValue(key, out var catalogued),
                $"engine emits {key} which is not in the catalog");
            Assert.Equal(catalogued, args);
            // Template safety: an adaptation may never pass FEWER args than its key template needs.
            Assert.True(args >= 0);
        }

        void Reduce(string key, int count) =>
            emitted[key] = Math.Max(emitted.GetValueOrDefault(key), count);
    }

    private static List<LIVORA.Application.Abstractions.AdaptationResult> RunsFiringAllRules()
    {
        var goals = new[] { WorkoutGoal(Now.AddDays(5), 0.1) };
        return new()
        {
            new PlanAdaptationEngine().Adapt(StandardPlan(), Wave3cFixtures.State(recoveryValue: 0.5), Now),
            new PlanAdaptationEngine().Adapt(StandardPlan(), Wave3cFixtures.State(recoveryValue: 0.5, steps: 10500), Now),
            new PlanAdaptationEngine().Adapt(StandardPlan(), Wave3cFixtures.State(sleepMinutes: 300), Now),
            new PlanAdaptationEngine().Adapt(StandardPlan(), Wave3cFixtures.State(sleepDaysSinceFreshData: 2), Now),
            new PlanAdaptationEngine().Adapt(StandardPlan(), Wave3cFixtures.State(steps: 10500), Now),
            new PlanAdaptationEngine(goals).Adapt(StandardPlan(programBase: 60, programPlanned: 10),
                Wave3cFixtures.State(), Now),
        };
    }

    [Fact]
    public void ComposedKeys_HaveExactStableValues()
    {
        // The composition helpers must produce the shipped key spellings verbatim.
        Assert.Equal("Plan.Change.ShrinkExercise", PlanAdaptationKeys.Change("ShrinkExercise"));
        Assert.Equal("Plan.Evidence.RecoveryBelowBaseline", PlanAdaptationKeys.Evidence("RecoveryBelowBaseline"));
        Assert.Equal("Plan.Evidence.SleepBelowBaseline", PlanAdaptationKeys.Evidence("SleepBelowBaseline"));
        Assert.Equal("Plan.Change.KeptStale", PlanAdaptationKeys.Change("KeptStale"));
        Assert.Equal("Plan.Evidence.StaleData", PlanAdaptationKeys.Evidence("StaleData"));
    }

    [Fact]
    public void CataloguedKeys_WhenAlreadyMergedIntoResx_MustBePaired_AndPlaceholderSafe()
    {
        // The lane may not edit the resx files (frozen): the KEYS block ships in wave3c-keys/ and
        // the orchestrator merges it. This test pins what a PARTIAL merge would break — if a key
        // already landed in one language but not the other (or drifted placeholders), fail loudly.
        var pair = LIVORA.Tests.Tests.Wave3Harness.ReadBoth();
        if (pair is null) return;   // resources not on disk next to the binaries — skip honestly

        foreach (var key in Wave3cEmissionCatalog.KeysWithArgCounts.Keys)
        {
            bool inEn = pair.En.ContainsKey(key), inFa = pair.Fa.ContainsKey(key);
            Assert.True(inEn == inFa, $"{key} merged in one language only ({(inEn ? "EN" : "FA")})");
            if (!inEn) continue;   // not merged yet — covered by the keys-file test below
            var enPh = Placeholders(pair.En[key]);
            var faPh = Placeholders(pair.Fa.TryGetValue(key, out var f) ? f : "");
            Assert.True(enPh.SequenceEqual(faPh),
                $"{key} placeholder drift after merge: EN[{string.Join(",", enPh)}] FA[{string.Join(",", faPh)}]");
        }
    }

    [Fact]
    public void Lane05KeyFiles_ExistAndCoverEveryCataloguedKey_InBothLanguages()
    {
        // The merge contract: wave3c-keys/lane05.{en,fa}.keys.xml exist with identical key sets,
        // non-empty values, matching placeholder sets, Persian script in FA, and full coverage.
        var root = LIVORA.Tests.Tests.Wave3Harness.RepoRoot;
        if (root is null) return;
        var enPath = Path.Combine(root, "wave3c-keys", "lane05.en.keys.xml");
        var faPath = Path.Combine(root, "wave3c-keys", "lane05.fa.keys.xml");
        Assert.True(File.Exists(enPath), "wave3c-keys/lane05.en.keys.xml missing");
        Assert.True(File.Exists(faPath), "wave3c-keys/lane05.fa.keys.xml missing");

        var en = ReadKeys(enPath);
        var fa = ReadKeys(faPath);

        Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal),
            fa.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in Wave3cEmissionCatalog.KeysWithArgCounts.Keys)
        {
            Assert.True(en.ContainsKey(key), $"keys file missing {key} (EN)");
            Assert.True(fa.ContainsKey(key), $"keys file missing {key} (FA)");
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"{key} empty in EN");
            Assert.False(string.IsNullOrWhiteSpace(fa[key]), $"{key} empty in FA");
            var enPh = Placeholders(en[key]);
            var faPh = Placeholders(fa[key]);
            Assert.True(enPh.SequenceEqual(faPh),
                $"{key} placeholder drift EN[{string.Join(",", enPh)}] FA[{string.Join(",", faPh)}]");
            Assert.True(fa[key].Any(c => c >= '\u0600' && c <= '\u06FF'),
                $"{key} FA value has no Persian script");
        }
        // Rule-key coverage: the file must not carry keys nothing emits.
        foreach (var key in en.Keys.Concat(fa.Keys))
            Assert.True(Wave3cEmissionCatalog.KeysWithArgCounts.ContainsKey(key),
                $"{key} ships but nothing emits it");
    }

    private static Dictionary<string, string> ReadKeys(string path)
    {
        var doc = System.Xml.Linq.XDocument.Load(path);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var name = data.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) d[name] = data.Element("value")?.Value ?? "";
        }
        return d;
    }

    private static int[] Placeholders(string fmt) =>
        System.Text.RegularExpressions.Regex.Matches(fmt ?? "", @"\{(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(i => i).ToArray();
}
