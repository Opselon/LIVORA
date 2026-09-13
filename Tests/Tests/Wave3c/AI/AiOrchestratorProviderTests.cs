using System.Diagnostics;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Intelligence;
using LIVORA.Application.Rules;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.IntelligenceProviders;
using LIVORA.Infrastructure.IntelligenceProviders.Wave3c;
using Microsoft.Extensions.Logging;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3c lane 02 — the orchestrator + registry end-to-end behavior: gates, key mapping,
/// watchdog/fallback honesty (Scenario F/G), and provenance codes. No live network: the chat
/// seam is faked at the IIntelligenceChatProvider level (transport has its own suite).
/// </summary>
public class AiOrchestratorProviderTests
{
    private static AiOrchestratorProvider Make(
        FakeChatProvider chat,
        ConsentDecision consent = ConsentDecision.Granted,
        bool gatewayEnabled = true,
        TimeSpan? timeout = null,
        TimeSpan? watchdog = null)
        => AiOrchestratorProvider.ForProvider(
            new ContextBuilder(),
            chat,
            new AiSafetyValidator(),
            new SampleIntelligenceProvider(new RuleEngine()),
            new FakeConsent { AiDecision = consent },
            new FakeGatewayConfig { Config = AiTestFixtures.GatewayConfig(gatewayEnabled, timeout) },
            watchdogMargin: watchdog ?? TimeSpan.FromSeconds(2));

    private static Task<AiInsightOutcome> Gen(AiOrchestratorProvider p)
        => p.GenerateWithProvenanceAsync(AiTestFixtures.State(), new[] { AiTestFixtures.Rec() }, AiTestFixtures.Profile());

    private static readonly string[] DeterministicKeys =
    {
        "Insight.Title.SleepDebt", "Insight.Title.LowRecovery", "Insight.Title.HighStress",
        "Insight.Title.PositiveMomentum", "Insight.Title.BalancedDay",
    };

    // ---- accepted path -------------------------------------------------------

    [Fact]
    public async Task Accepted_MapsToProviderKeys_TextAsSoleBodyArg()
    {
        var chat = new FakeChatProvider { Completion = AiTestFixtures.InsightJson() };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiValidated, outcome.Source);
        Assert.Empty(outcome.RejectionCodes);
        Assert.Equal("Ai.Headline.Provider", outcome.Interpretation.HeadlineKey);
        Assert.Equal("Ai.Body.Provider", outcome.Interpretation.BodyKey);
        Assert.Single(outcome.Interpretation.BodyArgs);
        Assert.IsType<string>(outcome.Interpretation.BodyArgs[0]);
        Assert.Equal(InsightPriority.Normal, outcome.Interpretation.Priority);   // AI never escalates
        Assert.Empty(outcome.Interpretation.SuggestedRecommendationOverrides);   // phrasing only
    }

    [Fact]
    public async Task Accepted_LegacyInterpretDelegatesToProvenance()
    {
        var chat = new FakeChatProvider { Completion = AiTestFixtures.InsightJson() };
        var p = Make(chat);
        var interp = await p.InterpretAsync(AiTestFixtures.State(), new[] { AiTestFixtures.Rec() }, AiTestFixtures.Profile());
        Assert.Equal("Ai.Body.Provider", interp.BodyKey);
    }

    // ---- gates ----------------------------------------------------------------

    [Fact]
    public async Task ConsentDenied_FallsBackDeterministic_NeverCallsChat()
    {
        var chat = new FakeChatProvider { Completion = "{}" };
        var outcome = await Gen(Make(chat, consent: ConsentDecision.Denied));
        Assert.Equal(0, chat.Calls);
        Assert.Equal(IntelligenceSource.AiDisabledByUser, outcome.Source);
        Assert.Contains("consent.denied", outcome.RejectionCodes);
        Assert.Contains(outcome.Interpretation.HeadlineKey, DeterministicKeys);
    }

    [Fact]
    public async Task ConsentUntouched_FallsBackDeterministic()
    {
        var chat = new FakeChatProvider { Completion = "{}" };
        var outcome = await Gen(Make(chat, consent: ConsentDecision.Untouched));
        Assert.Equal(IntelligenceSource.AiDisabledByUser, outcome.Source);
        Assert.Contains("consent.untouched", outcome.RejectionCodes);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task GatewayDisabled_FallsBackDeterministic()
    {
        var chat = new FakeChatProvider { Completion = "{}" };
        var outcome = await Gen(Make(chat, gatewayEnabled: false));
        Assert.Equal(IntelligenceSource.AiDisabledByUser, outcome.Source);
        Assert.Contains("gateway.disabled", outcome.RejectionCodes);
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task ProviderNotConfigured_FallsBackDeterministic()
    {
        var chat = new FakeChatProvider { Completion = "{}", Configured = false };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiDisabledByUser, outcome.Source);
        Assert.Contains("provider.not-configured", outcome.RejectionCodes);
    }

    // ---- failure paths (Scenario F/G) ----------------------------------------

    [Fact]
    public async Task ScenarioF_TransportFailure_NullCompletion_FallsBackDeterministic()
    {
        var chat = new FakeChatProvider { Completion = null };   // null = ANY transport failure
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
        Assert.Contains("provider.empty", outcome.RejectionCodes);
        Assert.Contains(outcome.Interpretation.HeadlineKey, DeterministicKeys);
    }

    [Fact]
    public async Task ScenarioG_GarbageBody_RejectedAndDeterministic()
    {
        var chat = new FakeChatProvider { Completion = "I am a helpful assistant! Here you go: nonsense" };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
        Assert.Contains("response.unparseable", outcome.RejectionCodes);
        Assert.Contains(outcome.Interpretation.HeadlineKey, DeterministicKeys);
        Assert.DoesNotContain("assistant", string.Concat(outcome.Interpretation.BodyArgs.Select(a => a?.ToString() ?? "")),
            StringComparison.OrdinalIgnoreCase);   // garbage prose never reaches the card
    }

    [Fact]
    public async Task UnsafeAiText_Rejected_FallsBackWithCodesOnly()
    {
        // URL-bearing prose is fatal for the whole response: deterministic card, and the
        // rejection codes are machine strings — never a snippet of the offending text.
        var chat = new FakeChatProvider { Completion = AiTestFixtures.InsightJson("Read more at http://evil.example/medical-advice") };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
        Assert.Contains("text.control", outcome.RejectionCodes);
        Assert.All(outcome.RejectionCodes, c => Assert.DoesNotContain("evil", c, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatProviderThrows_FallbackHolds_NeverThrowsToCaller()
    {
        var chat = new FakeChatProvider { ThrowOnCall = new InvalidOperationException("boom") };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
    }

    [Fact]
    public async Task Watchdog_HungProvider_AbandonedWithinTimeoutPlusMargin()
    {
        var chat = new FakeChatProvider { Hang = true };
        var p = Make(chat, timeout: TimeSpan.FromMilliseconds(60), watchdog: TimeSpan.FromMilliseconds(40));
        var sw = Stopwatch.StartNew();
        var outcome = await Gen(p);
        sw.Stop();
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
        Assert.Contains("provider.empty", outcome.RejectionCodes);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"watchdog let the caller hang {sw.Elapsed}");
    }

    [Fact]
    public async Task ClaimsTrimmedPartialAccept_ProseKeptBecauseItIsClean()
    {
        // one good claim + one unknown metric → claim trimmed, digits-free prose still ships
        var json = AiTestFixtures.InsightJson(
            body: "Your sleep is below your usual.",
            claimsJson: """{"metricKey":"sleep.minutes","value":300,"unit":"minutes"},{"metricKey":"bogus.metric","value":7,"unit":"kg"}""");
        var chat = new FakeChatProvider { Completion = json };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiValidated, outcome.Source);   // partial accept still validated
        Assert.Contains("metric.unknown", outcome.RejectionCodes);
        Assert.Equal("Ai.Body.Provider", outcome.Interpretation.BodyKey);
    }

    [Fact]
    public async Task ProseWithDigitsAndBadClaims_ProseDropped_DeterministicFallback()
    {
        var json = AiTestFixtures.InsightJson(
            body: "You slept 300 minutes.",
            claimsJson: """{"metricKey":"bogus.metric","value":7,"unit":"kg"}""");
        var chat = new FakeChatProvider { Completion = json };
        var outcome = await Gen(Make(chat));
        Assert.Equal(IntelligenceSource.AiRejectedFallback, outcome.Source);
        Assert.Contains("text.digits", outcome.RejectionCodes);
        Assert.Contains(outcome.Interpretation.HeadlineKey, DeterministicKeys);
    }

    [Fact]
    public async Task Orchestrator_UsesPromptAndContextContracts_ChatSeesSystemPrompt()
    {
        var chat = new FakeChatProvider { Completion = AiTestFixtures.InsightJson() };
        await Gen(Make(chat));
        Assert.Equal(InsightPromptBuilder.SystemPrompt, chat.LastSystemPrompt);
        Assert.NotNull(chat.LastUserJson);
        Assert.Contains("stateFacts", chat.LastUserJson!, StringComparison.Ordinal);
        Assert.DoesNotContain(AiTestFixtures.ForbiddenNameToken, chat.LastUserJson!, StringComparison.Ordinal);
    }

    // ---- registry ---------------------------------------------------------------

    [Fact]
    public void Registry_RealProviderFirst_WhenEffective()
    {
        var real = new FakeChatProvider();
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { MockChatProvider.Instance, real },
            new FakeConsent(), new FakeGatewayConfig());
        Assert.Same(real, reg.PickEffective());
    }

    [Fact]
    public void Registry_SampleLast_MockWhenNothingReal()
    {
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { MockChatProvider.Instance });
        Assert.Same(MockChatProvider.Instance, reg.PickEffective());
    }

    [Fact]
    public void Registry_ConsentDenied_SkipsRealProvider()
    {
        var real = new FakeChatProvider();
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { real },
            new FakeConsent { AiDecision = ConsentDecision.Denied }, new FakeGatewayConfig());
        Assert.Null(reg.PickEffective());   // no mock in the list → rules only, honestly null
    }

    [Fact]
    public void Registry_GatewayDisabled_SkipsRealProvider()
    {
        var real = new FakeChatProvider();
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { real },
            new FakeConsent(), new FakeGatewayConfig { Config = AiTestFixtures.GatewayConfig(enabled: false) });
        Assert.Null(reg.PickEffective());
    }

    [Fact]
    public void Registry_ProviderUnconfigured_SkipsIt()
    {
        var real = new FakeChatProvider { Configured = false };
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { real },
            new FakeConsent(), new FakeGatewayConfig());
        Assert.Null(reg.PickEffective());
    }

    [Fact]
    public void Registry_NeverThrows_EvenWithBombsInside()
    {
        var bomb = new ThrowingChatProvider();
        var reg = new ProviderRegistry(new IIntelligenceChatProvider[] { bomb, MockChatProvider.Instance },
            new FakeConsent { AiDecision = ConsentDecision.Granted },
            new FakeGatewayConfig { Throw = true });
        Assert.NotNull(reg.PickEffective());   // lands on the mock floor, not an exception
    }

    [Fact]
    public void Registry_EmptyList_ReturnsNull()
        => Assert.Null(new ProviderRegistry(Array.Empty<IIntelligenceChatProvider>()).PickEffective());

    private sealed class ThrowingChatProvider : IIntelligenceChatProvider
    {
        public bool IsConfigured => throw new InvalidOperationException("gate exploded");
        public AiProviderKind Kind => AiProviderKind.ExternalLlm;
        public string ProviderLabel => "bomb";
        public Task<string?> CompleteStructuredAsync(string s, string u, TimeSpan t, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task InterpretAsync_NeverThrows_WhenEverythingIsBroken()
    {
        var p = new AiOrchestratorProvider(
            new ThrowingContextBuilder(),
            () => throw new InvalidOperationException("picker explodes"),
            new AiSafetyValidator(),
            new ThrowingFallback());
        var interp = await p.InterpretAsync(AiTestFixtures.State(), Array.Empty<Domain.Models.Recommendation>(), AiTestFixtures.Profile());
        Assert.Equal("Insight.Title.BalancedDay", interp.HeadlineKey);   // static honest card
    }

    private sealed class ThrowingContextBuilder : IContextBuilder
    {
        public Task<IntelligenceContext> BuildAsync(Domain.Models.State.PersonalState s, UserProfile p,
            IReadOnlyList<Domain.Models.Recommendation> r, CancellationToken ct = default)
            => throw new InvalidOperationException("builder exploded");
    }

    private sealed class ThrowingFallback : IIntelligenceProvider
    {
        public bool IsAvailable => true;
        public Domain.Enums.DataOrigin Origin => Domain.Enums.DataOrigin.Mock;
        public Task<InsightInterpretation> InterpretAsync(Domain.Models.State.PersonalState s,
            IReadOnlyList<Domain.Models.Recommendation> r, UserProfile p, CancellationToken ct = default)
            => throw new InvalidOperationException("fallback exploded");
    }
}
