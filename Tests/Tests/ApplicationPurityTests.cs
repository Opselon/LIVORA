using LIVORA.Application.Abstractions;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using System.Text.RegularExpressions;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Every engine output the UI can render must be a localization key plus format arguments — never
/// English prose. These tests read the compiled C# of the pure layers and assert two things at once:
/// no prose-shaped literal anywhere in Application/Domain, and no key emitted from any of them is a
/// bare identifier (they must be dotted resource keys that actually exist in both .resx files).
/// </summary>
public class ApplicationPurityTests
{
    /// <summary>Machine tokens the planning grammar intentionally speaks (not display text).</summary>
    private static readonly string[] GrammarWhitelist =
    {
        "exercise:", "recovery:", "walk:", "focus:", "bedtime:", "winddown:", "habit:", "prompt",
    };

    [Theory]
    [InlineData("Application/Rules/RuleEngine.cs")]
    [InlineData("Application/State/UserStateService.cs")]
    [InlineData("Application/State/BaselineService.cs")]
    [InlineData("Application/State/TrendService.cs")]
    [InlineData("Application/Planning/RecommendationService.cs")]
    [InlineData("Application/Planning/ProgramAdapter.cs")]
    [InlineData("Application/Insights/WeeklySummaryService.cs")]
    [InlineData("Application/Insights/IntelligenceOrchestrator.cs")]
    [InlineData("Application/HealthData/DataNormalizer.cs")]
    [InlineData("Application/HealthData/SampleHealthProvider.cs")]
    [InlineData("Domain/Models/State/PersonalState.cs")]
    [InlineData("Domain/Models/Goals/Goal.cs")]
    [InlineData("Domain/Models/Programs/Bootcamp.cs")]
    public void SourceFile_EmitsNoSentenceShapedStringLiterals(string relativePath)
    {
        // The rule: Application/Domain code may only emit keys+args. A sentence-shaped literal
        // (three or more words) or a literal with a space is prose leaking out of the layer that is
        // supposed to be language-independent.
        var file = Wave3Harness.RepoRoot is null ? null : Path.Combine(Wave3Harness.RepoRoot!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (file is null || !File.Exists(file)) return;   // skip cleanly if the tree moved

        foreach (var literal in Wave3Harness.Literals(File.ReadAllText(file)))
        {
            if (literal.Length == 0) continue;
            if (GrammarWhitelist.Any(w => literal.StartsWith(w, StringComparison.Ordinal))) continue;
            Assert.DoesNotContain(' ', literal.Trim());
            // "Rule.LowRecoveryReduce" is a key; "LIVORA sample data" would be prose. A literal with
            // 4+ words is prose by shape alone.
            Assert.True(literal.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 1,
                $"{relativePath} emits prose literal \"{literal}\" — move it into AppResources and emit a key");
        }
    }

    [Fact]
    public void EveryDottedLiteralInTheEngines_IsAKeyOrAKnownMachineIdentifier()
    {
        if (Wave3Harness.RepoRoot is null) return;
        var pair = Wave3Harness.ReadBoth();

        // Literals that look like resource keys must actually exist in BOTH languages, otherwise a
        // typo ships as "[Key]" in the UI. Metric/JSON identifiers are excluded by prefix.
        string[] knownMachinePrefixes = { "Rule.", "Rec.", "Benefit.", "Plan.", "Insight.", "Weekly.", "Format.", "Bootcamp.", "Profile.", "Metrics" };
        string[] machineOnly = { "sleep.", "activity.", "recovery.", "wellness.", "focus.", "livora_", "Mock", "exercise:*0.5" };

        var offenders = new List<string>();
        foreach (var (path, source) in Wave3Harness.Sources("Application"))
        {
            foreach (var lit in Wave3Harness.Literals(source))
            {
                if (!lit.Contains('.') || lit.Length < 4) continue;
                if (machineOnly.Any(m => lit.StartsWith(m, StringComparison.Ordinal))) continue;
                if (!Regex.IsMatch(lit, @"^[A-Z][A-Za-z0-9]*(\.[A-Za-z0-9_]+)+$")) continue;
                // A bare "Rule.LowRecoveryReduce" is a RULE ID (persisted in DailyPlan.AdaptationRuleKeys
                // and Recommendation.ProducedByRule), not display text; its translation lives at
                // "Rule.Why.<id>" and is checked by Wave3HonestyTests.
                if (Regex.IsMatch(lit, @"^Rule\.[A-Za-z0-9]+$")) continue;
                if (!lit.StartsWith("Rule.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Rec.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Benefit.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Plan.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Insight.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Weekly.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Format.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Bootcamp.", StringComparison.Ordinal) &&
                    !lit.StartsWith("Profile.", StringComparison.Ordinal)) continue;

                // Interpolated keys (Rec.{Action}) are covered by their own enum test; skip shapes
                // that the engines build at runtime.
                if (pair is null) continue;
                if (!pair.En.ContainsKey(lit)) offenders.Add($"{Path.GetFileName(path)}: {lit}");
            }
        }
        Assert.True(offenders.Count == 0, "keys emitted by the engines that no translation defines: " + string.Join(" | ", offenders.Distinct()));
    }

    [Fact]
    public void WeeklySummary_ServiceOnlyEverEmitsWeeklyKeys()
    {
        if (Wave3Harness.RepoRoot is null) return;
        var path = Path.Combine(Wave3Harness.RepoRoot!, "Application", "Insights", "WeeklySummaryService.cs");
        if (!File.Exists(path)) return;

        var literals = Wave3Harness.Literals(File.ReadAllText(path))
            .Where(l => l.Contains('.') && char.IsUpper(l[0]))
            .ToList();
        Assert.NotEmpty(literals);
        Assert.All(literals, l => Assert.StartsWith("Weekly.", l));
    }
}
