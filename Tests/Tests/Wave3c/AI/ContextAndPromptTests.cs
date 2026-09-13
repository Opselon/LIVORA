using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Intelligence;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3c lane 02 — ContextBuilder privacy/minimality + InsightPromptBuilder contract +
/// AiResponseParser robustness. The forbidden-token assertions are the whole point: whatever
/// the fake user typed into free-text fields must NEVER reach the serialized prompt.
/// </summary>
public class ContextAndPromptTests
{
    private static IntelligenceContext Build(bool fa = false)
        => new ContextBuilder(() => fa ? "fa" : "en")
            .BuildAsync(AiTestFixtures.State(extraMetrics: 6), AiTestFixtures.Profile(),
                new[] { AiTestFixtures.Rec() }).GetAwaiter().GetResult();

    // ---- context minimality --------------------------------------------------

    [Fact]
    public void Context_FactsAreStateDeltaOnly()
    {
        var ctx = Build();
        Assert.All(ctx.StateFacts, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.MetricKey));
            Assert.False(string.IsNullOrWhiteSpace(f.Unit));
            Assert.NotEqual(0, f.Current);
        });
        var sleep = ctx.StateFacts.Single(f => f.MetricKey == Domain.Models.State.Metrics.SleepMinutes);
        Assert.Equal(300, sleep.Current);
        Assert.Equal(420, sleep.Baseline);
        Assert.Equal("minutes", sleep.Unit);
    }

    [Fact]
    public void Context_CapsAtTwelveFacts()
    {
        var ctx = Build();
        Assert.True(ctx.StateFacts.Count <= 12);
        Assert.Equal(12, ctx.StateFacts.Count);   // fixture has 13+ metrics — the cap must bite
        Assert.DoesNotContain(ctx.StateFacts, f => f.MetricKey.StartsWith("extra.", StringComparison.Ordinal));
    }

    [Fact]
    public void Context_GoalTitlesOnly_NoDescriptionsOrProgress()
    {
        var ctx = Build();
        Assert.Contains("Deep Sleep Project", ctx.ActiveGoalTitles);
        // Titles only: no fraction/status leaked, nothing long, sanitized (no digits from names).
        Assert.All(ctx.ActiveGoalTitles, t =>
        {
            Assert.True(t.Length <= 60);
            Assert.False(t.Any(char.IsDigit));
        });
        Assert.DoesNotContain("Fraction", InsightPromptBuilder.SerializeUser(ctx), StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedUserJson_ContainsNoForbiddenTokens()
    {
        var ctx = Build();
        var json = InsightPromptBuilder.SerializeUser(ctx);
        Assert.DoesNotContain(AiTestFixtures.ForbiddenNameToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AiTestFixtures.ForbiddenNoteToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain(AiTestFixtures.ForbiddenHabitToken, json, StringComparison.Ordinal);
        Assert.DoesNotContain("Zartosht", json, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializedUserJson_CarriesFactsGoalsActions_LanguageAndCorrelation()
    {
        var ctx = Build(fa: true);
        using var doc = JsonDocument.Parse(InsightPromptBuilder.SerializeUser(ctx));
        var root = doc.RootElement;
        Assert.Equal("fa", root.GetProperty("languageCode").GetString());
        Assert.NotEmpty(root.GetProperty("correlationId").GetString()!);
        Assert.True(root.GetProperty("stateFacts").GetArrayLength() > 0);
        Assert.True(root.GetProperty("activeGoals").GetArrayLength() > 0);
        Assert.Contains("ShortWalk", root.GetProperty("allowedActionKinds").ToString());
    }

    [Fact]
    public void Context_CorrelationIdIsStableShape_AndLanguageResolved()
    {
        var en = Build();
        Assert.StartsWith("ai-", en.CorrelationId, StringComparison.Ordinal);
        Assert.Equal("en", en.LanguageCode);
        Assert.Equal("fa", Build(fa: true).LanguageCode);
    }

    // ---- prompt contract -------------------------------------------------------

    [Theory]
    [InlineData("NO diagnoses")]
    [InlineData("NO medical claims")]
    [InlineData("NO emergency advice")]
    [InlineData("Do NOT invent")]
    [InlineData("headlineKey")]
    [InlineData("bodyKey")]
    [InlineData("bodyText")]
    [InlineData("confidence")]
    [InlineData("claims")]
    [InlineData("metricKey")]
    [InlineData("proposedActionKinds")]
    [InlineData("LIVORA coach")]
    [InlineData("en")]
    [InlineData("fa")]
    public void SystemPrompt_PinsItsContract(string mustContain)
        => Assert.Contains(mustContain, InsightPromptBuilder.SystemPrompt, StringComparison.Ordinal);

    [Fact]
    public void SchemaKeys_AreExact()
    {
        Assert.Equal(
            new[] { "headlineKey", "bodyKey", "bodyText", "confidence", "claims", "metricKey", "value", "unit", "proposedActionKinds" },
            InsightPromptBuilder.SchemaKeys);
    }

    [Fact]
    public void SystemPrompt_NamesRoleAndShortSentences()
    {
        Assert.Contains("You are the LIVORA coach", InsightPromptBuilder.SystemPrompt, StringComparison.Ordinal);
        Assert.Contains("short sentences", InsightPromptBuilder.SystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    // ---- parser robustness over 10 malformed bodies ------------------------------

    public static TheoryData<string, bool> MalformedBodies() => new()
    {
        { "```json\n{\"headlineKey\":\"Insight.X\",\"bodyText\":\"ok\",\"confidence\":0.5}\n```", true },   // fence
        { "Here is your answer: {\"headlineKey\":\"Insight.X\",\"bodyText\":\"ok\"}", true },               // prose before
        { "{\"headlineKey\":\"Insight.X\",\"bodyText\":\"trunc", false },                                   // truncated string — unsalvageable? it CAN close
        { "{\"headlineKey\":\"Insight.X\",\"claims\":[{\"metricKey\":\"m\",\"value\":1,}]}", true },        // trailing comma
        { "{\"headlineKey\":null,\"bodyText\":null}", false },                                              // nulls everywhere
        { "{\"headlineKey\":123,\"bodyText\":true,\"confidence\":\"0.7\"}", true },                         // wrong types, tolerant
        { "{\"headlineKey\":\"Insight.X\",\"confidence\":1e309}", true },                                   // out-of-range number: kept as 0, never crashes
        { "totally not json at all", false },                                                               // garbage
        { "", false },                                                                                      // empty
        { "{\"headlineKey\":\"Insight.X\",\"bodyText\":\"a\",\"claims\":\"not-an-array\"}", true },         // wrong shape for claims — skip them
    };

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public void Parse_NeverThrows_OnEveryMalformedBody(string body, bool _)
    {
        var result = Record.Exception(() => AiResponseParser.Parse(body));
        Assert.Null(result);   // the contract: never throw, null = garbage
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public void Parse_OutcomesAreDeterministic(string body, bool expectParsed)
    {
        var parsed = AiResponseParser.Parse(body);
        Assert.Equal(expectParsed, parsed is not null);
    }

    [Fact]
    public void Parse_AcceptsSseAccumulatedContentAndCodeFenceTogether()
    {
        var fenced = "```json\n" + AiTestFixtures.InsightJson() + "\n```";
        var parsed = AiResponseParser.Parse(fenced);
        Assert.NotNull(parsed);
        Assert.Equal("Ai.Headline.Provider", parsed!.HeadlineKey);
        Assert.True(parsed.BodyIsProviderText);
        Assert.Single(parsed.Claims);
        Assert.Equal(0.8, parsed.Confidence, 6);
    }

    [Fact]
    public void Parse_TruncatedObject_IsSalvagedWhenPossible()
    {
        // cut mid-array — brace/bracket salvage closes them; claims survive partially
        var parsed = AiResponseParser.Parse("{\"headlineKey\":\"Insight.X\",\"bodyText\":\"fine\",\"claims\":[{\"metricKey\":\"m\",\"value\":5");
        Assert.NotNull(parsed);
        Assert.Equal("Insight.X", parsed!.HeadlineKey);
    }

    [Fact]
    public void SseAccumulator_SkipsMalformedChunkLines()
    {
        var stream = "data: {oops\n\ndata: [DONE]\n\n";
        Assert.Null(AiResponseParser.AccumulateSse(stream));
        var good = AiResponseParser.AccumulateSse(
            "data: not-json\n\n" + AiResponseLine("hi") + "\n\ndata: [DONE]\n\n");
        Assert.Equal("hi", good);
    }

    private static string AiResponseLine(string content) =>
        AiTestFixtures.SseStream(content);   // already has its own DONE; used for concat sanity
}
