using System.Reflection;
using Livora.Server.Infrastructure.Engines.Decision;

namespace Livora.Server.Tests.Engines;

/// <summary>
/// PURPOSE: the anti-drift gate. The server engines RESTATE client rules (the assemblies may never
///          reference each other), so this test reads BOTH source files as text and pins every
///          shared name and number: a client threshold changed without the server twin (or vice
///          versa) fails here with both values in the message. This is the mechanical answer to
///          "the client and server must not drift silently".
/// OWNER: Agent 10+11 (lane w4-p1e-engines).
/// METHOD: regex over `public const <type> <Name> = <value>;` / enum member lists in the client's
///         Domain/Application files, compared against the server's NumericRules fields, enum
///         members, and Metrics constants by reflection — one mechanism, no hand-copied numbers.
/// </summary>
public sealed class SemanticParityTests
{
    private static readonly string RepoRoot = FindRoot();

    private static string FindRoot()
    {
        // Anchor at the test output dir; fall back to the working directory so the lane can also
        // run these tests from a temporary harness outside the repo tree (gate-isolated runs).
        return FindRootFrom(AppContext.BaseDirectory) ?? FindRootFrom(Environment.CurrentDirectory)
            ?? throw new DirectoryNotFoundException(
                "LIVORA repo root not found from " + AppContext.BaseDirectory);
    }

    private static string? FindRootFrom(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "server", "src", "Livora.Server"))
                                    && Directory.Exists(Path.Combine(dir.FullName, "Application", "Rules"))))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepoRoot, Path.Combine(parts)));

    /// <summary>All `public [static] const T Name = value;` from a client source file.</summary>
    private static Dictionary<string, string> ClientConstants(string source)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     source, @"public\s+(?:static\s+)?const\s+[\w.<>\[\]]+\s+(\w+)\s*=\s*([^;]+);"))
            found[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return found;
    }

    private static string ServerConstant(string name)
    {
        var field = typeof(NumericRules).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(field);
        var value = field!.GetValue(null);
        return value is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
             : value is int i ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
             : value?.ToString() ?? "";
    }

    [Theory]
    // Every RuleEngine threshold the server restates must carry the SAME literal (source-pinned).
    [InlineData("SleepDeficitHours")]
    [InlineData("RecoveryBelow")]
    [InlineData("StressAbove")]
    [InlineData("ActivityDeficitFraction")]
    [InlineData("StaleDaysAllowance")]
    public void RuleEngine_thresholds_match_between_client_source_and_server_rules(string name)
    {
        var client = ClientConstants(Read("Application", "Rules", "RuleEngine.cs"));
        Assert.True(client.ContainsKey(name), $"client RuleEngine has no constant {name}");
        Assert.Equal(NormalizeNumber(client[name]), NormalizeNumber(ServerConstant(name)));
    }

    [Theory]
    [InlineData("WindowDays", "BaselineWindowDays")]     // BaselineService 28
    [InlineData("MinSamples", "TrendMinSamples")]        // TrendService 5
    [InlineData("BandFraction", "TrendBandFraction")]    // TrendService 0.06
    public void State_engine_constants_match(string clientName, string serverName)
    {
        var baseline = ClientConstants(Read("Application", "State", "BaselineService.cs"));
        var trend = ClientConstants(Read("Application", "State", "TrendService.cs"));
        var client = baseline.TryGetValue(clientName, out var b) ? b : trend[clientName];
        Assert.Equal(NormalizeNumber(client), NormalizeNumber(ServerConstant(serverName)));
    }

    [Fact]
    public void Level_band_is_the_clients_12_percent_noise_band()
    {
        var personalState = Read("Domain", "Models", "State", "PersonalState.cs");
        Assert.Contains("const double band = 0.12;", personalState, StringComparison.Ordinal);
        Assert.Equal(0.12, (double)typeof(NumericRules).GetField("LevelBand")!.GetValue(null)!);
    }

    [Fact]
    public void Confidence_ladder_matches_Baseline_FromSamples_boundaries()
    {
        var personalState = Read("Domain", "Models", "State", "PersonalState.cs");
        Assert.Contains("< 3 => BaselineConfidence.None", personalState, StringComparison.Ordinal);
        Assert.Contains("< 7 => BaselineConfidence.Low", personalState, StringComparison.Ordinal);
        Assert.Contains("< 14 => BaselineConfidence.Medium", personalState, StringComparison.Ordinal);
        Assert.Equal(EngineBaselineConfidence.None, NumericRules.ConfidenceForSampleCount(2));
        Assert.Equal(EngineBaselineConfidence.Low, NumericRules.ConfidenceForSampleCount(3));
        Assert.Equal(EngineBaselineConfidence.Medium, NumericRules.ConfidenceForSampleCount(7));
        Assert.Equal(EngineBaselineConfidence.High, NumericRules.ConfidenceForSampleCount(14));
    }

    [Theory]
    // PlanAdaptationEngine constants, mirrored with the SAME names for mechanical parity.
    [InlineData("ShrinkFloorMinutes")]
    [InlineData("RecoveryItemMinutes")]
    [InlineData("WindDownMinutes")]
    [InlineData("SleepDeviationTrigger")]
    [InlineData("HighActivityDeviationTrigger")]
    [InlineData("StaleDaysTrigger")]
    [InlineData("DeadlineRiskDays")]
    [InlineData("DeadlineRiskFractionCeiling")]
    [InlineData("TotalMinutesCapFactor")]
    [InlineData("StaleConfidence")]
    public void Plan_adaptation_constants_match(string name)
    {
        var client = ClientConstants(Read("Application", "Planning", "Adaptive", "PlanAdaptationEngine.cs"));
        Assert.True(client.ContainsKey(name), $"client PlanAdaptationEngine has no constant {name}");
        Assert.Equal(NormalizeNumber(client[name]), NormalizeNumber(ServerConstant(name)));
    }

    [Fact]
    public void Recovery_ladder_minutes_are_the_same_three_steps()
    {
        var source = Read("Application", "Planning", "Adaptive", "PlanAdaptationEngine.cs");
        Assert.Contains("new[] { 45, 30, 20 }", source, StringComparison.Ordinal);
        Assert.Equal([45, 30, 20], NumericRules.RecoveryLadderMinutes.ToArray());
    }

    [Fact]
    public void Weekly_refusal_floor_is_the_clients_three_day_rule()
    {
        var source = Read("Application", "Insights", "WeeklySummaryService.cs");
        Assert.Contains("thisWeek.Count < 3", source, StringComparison.Ordinal);   // the refusal, verbatim
        Assert.Equal(3, (int)typeof(NumericRules).GetField("WeeklyMinDays")!.GetValue(null)!);
    }

    [Theory]
    [InlineData("MinHistoryDays")]
    [InlineData("GateLateSleepNights")]
    [InlineData("LateSleepNightsToFire")]
    [InlineData("LateSleepThresholdMinutes")]
    [InlineData("GateWeekdaySamples")]
    [InlineData("WeekdayDipThreshold")]
    [InlineData("GateFocusPairs")]
    [InlineData("PoorSleepDeviation")]
    [InlineData("FocusCoOccurrenceToFire")]
    [InlineData("GateStagnationDays")]
    [InlineData("StagnationDeadlineWindowDays")]
    [InlineData("GateCouplingPairs")]
    [InlineData("CouplingMinMagnitude")]
    public void Pattern_gates_match(string name)
    {
        var client = ClientConstants(Read("Application", "Patterns", "PatternEngine.cs"));
        Assert.True(client.ContainsKey(name), $"client PatternEngine has no constant {name}");
        Assert.Equal(NormalizeNumber(client[name]), NormalizeNumber(ServerConstant(name)));
    }

    [Theory]
    [InlineData("BaselineConfidence", "EngineBaselineConfidence")]
    [InlineData("TrendDirection", "EngineTrend")]
    [InlineData("StateLevel", "EngineLevel")]
    [InlineData("DataQuality", "EngineQuality")]
    [InlineData("RecommendationPriority", "EnginePriority")]
    [InlineData("RecommendationCategory", "EngineCategory")]
    public void Enum_members_and_order_match_the_client_enums(string clientEnum, string serverEnum)
    {
        var source = EnumSource(clientEnum);
        var clientMembers = ParseEnumMembers(source, clientEnum);
        var serverType = typeof(NumericRules).Assembly.GetType(
            "Livora.Server.Infrastructure.Engines.Decision." + serverEnum)
            ?? throw new InvalidOperationException("missing server enum " + serverEnum);
        var serverMembers = Enum.GetNames(serverType);
        Assert.Equal(clientMembers, serverMembers);   // names AND order (persisted as strings)
    }

    [Fact]
    public void Recommendation_actions_cover_the_client_kinds_with_named_server_extensions()
    {
        var source = EnumSource("RecommendationActionKind");
        var clientMembers = ParseEnumMembers(source, "RecommendationActionKind");
        var serverNames = Enum.GetNames(typeof(EngineAction));
        foreach (var member in clientMembers)
            Assert.Contains(member, serverNames);   // every client action exists server-side
        // the four server placement verbs are APPENDED (never renumbered), like the client's
        // append-only enum rule (Wave3Enums comment) — pinned count so a rename is a loud diff:
        Assert.Equal(clientMembers.Count + 4, serverNames.Length);
        Assert.Contains("MoveWorkout", serverNames);
        Assert.Contains("SkipWorkout", serverNames);
    }

    [Theory]
    [InlineData("SleepMinutes", "sleep.minutes")]
    [InlineData("SleepQuality", "sleep.quality")]
    [InlineData("SleepConsistency", "sleep.consistency")]
    [InlineData("BedtimeMinutes", "sleep.bedtime")]
    [InlineData("Steps", "activity.steps")]
    [InlineData("ActiveMinutes", "activity.minutes")]
    [InlineData("RecoveryScore", "recovery.score")]
    [InlineData("Stress", "wellness.stress")]
    [InlineData("Mood", "wellness.mood")]
    [InlineData("Energy", "wellness.energy")]
    [InlineData("FocusEstimate", "focus.estimate")]
    public void Metric_key_strings_are_identical_both_sides(string clientMember, string expected)
    {
        var source = Read("Domain", "Models", "State", "PersonalState.cs");
        Assert.Contains($"public const string {clientMember} = \"{expected}\";", source, StringComparison.Ordinal);
        var serverField = typeof(EngineStateComputer.EngineMetrics).GetField(clientMember);
        Assert.NotNull(serverField);
        Assert.Equal(expected, serverField!.GetValue(null));
    }

    [Fact]
    public void Baseline_engine_window_gates_match()
    {
        var client = ClientConstants(Read("Application", "State", "Wave3b", "BaselineEngine.cs"));
        Assert.Equal(NormalizeNumber(client["GateDays7"]), NormalizeNumber(ServerConstant("GateWindowDays7")));
        Assert.Equal(NormalizeNumber(client["GateDays14"]), NormalizeNumber(ServerConstant("GateWindowDays14")));
        Assert.Equal(NormalizeNumber(client["GateDays30"]), NormalizeNumber(ServerConstant("GateWindowDays30")));
    }

    [Fact]
    public void ConfidenceOf_tiers_match_the_client_plan_engine()
    {
        var client = Read("Application", "Planning", "Adaptive", "PlanAdaptationEngine.cs");
        Assert.Contains("BaselineConfidence.High => 0.9", client, StringComparison.Ordinal);
        Assert.Contains("BaselineConfidence.Medium => 0.75", client, StringComparison.Ordinal);
        Assert.Contains("BaselineConfidence.Low => 0.5", client, StringComparison.Ordinal);
        Assert.Equal(0.9, NumericRules.ConfidenceOf(EngineBaselineConfidence.High));
        Assert.Equal(0.75, NumericRules.ConfidenceOf(EngineBaselineConfidence.Medium));
        Assert.Equal(0.5, NumericRules.ConfidenceOf(EngineBaselineConfidence.Low));
        Assert.Equal(0.0, NumericRules.ConfidenceOf(EngineBaselineConfidence.None));
    }

    [Fact]
    public void Rule_keys_the_server_emits_exist_in_the_client_rule_engine()
    {
        var client = Read("Application", "Rules", "RuleEngine.cs");
        foreach (var key in EngineRuleEvaluator.AllRuleKeys)
        {
            if (key.StartsWith("Rule.Server.", StringComparison.Ordinal)) continue; // declared additions
            Assert.Contains($"\"{key}\"", client, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reason_keys_are_shared_vocabulary_not_invented_server_prose()
    {
        var client = Read("Application", "Rules", "RuleEngine.cs");
        foreach (var key in new[] { "Rule.Reason.SleepBelowBaseline", "Rule.Reason.RecoveryBelowBaseline",
                                    "Rule.Reason.StressAboveUsual", "Rule.Reason.StepsBelowBaseline",
                                    "Rule.Reason.AllNearBaseline", "Rule.Reason.HabitStreakAtRisk",
                                    "Rule.Reason.SleepDataStale" })
            Assert.Contains($"\"{key}\"", client, StringComparison.Ordinal);
    }

    // ---- plumbing ---------------------------------------------------------------------------

    private static string EnumSource(string enumName) => enumName switch
    {
        "BaselineConfidence" or "TrendDirection" or "StateLevel" => Read("Domain", "Enums", "StateEnums.cs"),
        "RecommendationPriority" or "RecommendationCategory" or "RecommendationActionKind" =>
            Read("Domain", "Enums", "PlanningEnums.cs"),
        "DataQuality" => Read("Domain", "Enums", "HealthDataEnums.cs"),
        _ => throw new ArgumentOutOfRangeException(nameof(enumName)),
    };

    private static List<string> ParseEnumMembers(string source, string enumName)
    {
        var start = source.IndexOf("enum " + enumName, StringComparison.Ordinal);
        Assert.True(start >= 0, $"enum {enumName} not found in client source");
        var body = source[(source.IndexOf('{', start) + 1)..];
        body = body[..body.IndexOf('}')];
        // members may carry /// docs and = value; strip to bare names in declaration order
        var names = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("///") || line.StartsWith("//")) continue;
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^(\w+)\s*(?:=.*)?,?$");
            if (match.Success) names.Add(match.Groups[1].Value);
        }
        return names;
    }

    private static string NormalizeNumber(string literal)
    {
        literal = literal.Trim();
        if (literal.EndsWith("f", StringComparison.Ordinal)) literal = literal[..^1];
        if (literal.EndsWith("d", StringComparison.Ordinal)) literal = literal[..^1];
        if (double.TryParse(literal, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return literal;
    }
}
