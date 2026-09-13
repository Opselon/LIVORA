using System.Net;
using System.Text.Json;
using LIVORA.Application.Intelligence;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.IntelligenceProviders.Wave3c;
using Microsoft.Extensions.Logging;
namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3c lane 02 — OpenAiCompatibleChatProvider transport tests. Fake handler only; the suite
/// NEVER touches the network. Covers: plain vs SSE bodies, request shape, response_format
/// fallback, retry/backoff/Retry-After, timeout, cancellation, and the logging privacy contract.
/// </summary>
public class OpenAiCompatibleChatProviderTests
{
    private const string Sys = "SYS";
    private const string User = "{\"a\":1}";

    private static OpenAiCompatibleChatProvider Make(
        FakeAiHandler handler, RecordingDelays? delays = null, RecordingLogger? log = null,
        bool enabled = true, TimeSpan? timeout = null)
        => new(new FakeGatewayConfig { Config = AiTestFixtures.GatewayConfig(enabled, timeout) },
            handler, delays is null ? null : (t, c) => delays.Delay(t, c),
            log is null ? null : new PassthroughLogger(log));

    // ---- response shapes ---------------------------------------------------

    [Fact]
    public async Task PlainChatCompletionBody_ReturnsMessageContent()
    {
        var insight = AiTestFixtures.InsightJson();
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(insight)));
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Equal(insight, result);
    }

    [Fact]
    public async Task SseStreamBody_AccumulatesDeltasUntilDone()
    {
        var insight = AiTestFixtures.InsightJson("Longer body with more characters so it splits across SSE delta chunks for sure.");
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.SseStream(insight)));
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Equal(insight, result);
    }

    [Fact]
    public async Task SseAndPlain_BodiesProduceParity()
    {
        var insight = AiTestFixtures.InsightJson();
        var plain = await Make(new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(insight))))
            .CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        var sse = await Make(new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.SseStream(insight))))
            .CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.NotNull(plain);
        Assert.Equal(plain, sse);   // the gateway's streaming gotcha must not change the answer
    }

    // ---- request shape -----------------------------------------------------

    [Fact]
    public async Task Request_HasRequiredFields_AndSendsNoStreamFlag()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope("{}")));
        await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        var body = JsonDocument.Parse(handler.RequestBodies[0]);
        var root = body.RootElement;
        Assert.Equal("coding", root.GetProperty("model").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        var msgs = root.GetProperty("messages");
        Assert.Equal(2, msgs.GetArrayLength());
        Assert.Equal("system", msgs[0].GetProperty("role").GetString());
        Assert.Equal(Sys, msgs[0].GetProperty("content").GetString());
        Assert.Equal(User, msgs[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Request_CarriesUserAgent_AndBearerKey()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope("{}")));
        await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        var req = handler.Requests[0];
        Assert.Contains(OpenAiCompatibleChatProvider.UserAgentValue,
            req.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        var auth = req.Headers.Authorization?.ToString() ?? "";
        Assert.Equal($"Bearer {AiTestFixtures.FakeApiKey}", auth);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/chat/completions", req.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Request_CancellationRequested_ReturnsNullNotThrow()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok("x"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5), cts.Token);
        Assert.Null(result);   // null on ANY failure — never an exception to the app
    }

    // ---- response_format fallback ------------------------------------------

    [Fact]
    public async Task FourHundredMentioningResponseFormat_RetriesOnceWithoutIt()
    {
        var err = """{"error":{"message":"Unsupported parameter: response_format"}}""";
        var handler = new FakeAiHandler(
            FakeAiHandler.Status(HttpStatusCode.BadRequest, err),
            FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(AiTestFixtures.InsightJson())));
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("response_format", handler.RequestBodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("response_format", handler.RequestBodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FourHundredOtherReason_NoRetry()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Status(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}"""));
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Null(result);
        Assert.Equal(1, handler.CallCount);
    }

    // ---- retries / backoff ---------------------------------------------------

    [Fact]
    public async Task TwoHundredAfterTwo503s_RetriesWithExponentialBackoff_AndSucceeds()
    {
        var delays = new RecordingDelays();
        var handler = new FakeAiHandler(
            FakeAiHandler.Status(HttpStatusCode.ServiceUnavailable),
            FakeAiHandler.Status(HttpStatusCode.ServiceUnavailable),
            FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(AiTestFixtures.InsightJson())));
        var result = await Make(handler, delays).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(2, delays.Waits.Count);
        Assert.True(delays.Waits[1] > delays.Waits[0]);   // exponential, not flat
    }

    [Fact]
    public async Task ThirdPersistent5xx_StopsAtMaxTwoRetries_ReturnsNull()
    {
        var delays = new RecordingDelays();
        var handler = new FakeAiHandler(FakeAiHandler.Status(HttpStatusCode.BadGateway));
        var result = await Make(handler, delays).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Null(result);
        Assert.Equal(3, handler.CallCount);      // initial + 2 retries — no more
        Assert.Equal(2, delays.Waits.Count);
    }

    [Fact]
    public async Task FourTwentyNineThenOk_HonorsRetryAfterSeconds()
    {
        var delays = new RecordingDelays();
        var handler = new FakeAiHandler(
            FakeAiHandler.Status((HttpStatusCode)429, null, TimeSpan.FromSeconds(7)),
            FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(AiTestFixtures.InsightJson())));
        var result = await Make(handler, delays).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.NotNull(result);
        Assert.Single(delays.Waits);
        Assert.Equal(TimeSpan.FromSeconds(7), delays.Waits[0]);   // server-said wait beats our curve
    }

    // ---- timeout -------------------------------------------------------------

    [Fact]
    public async Task SlowGateway_BeyondConfigTimeout_ReturnsNull()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope("{}")))
        { ArtificialDelay = TimeSpan.FromMilliseconds(400) };
        var result = await Make(handler, timeout: TimeSpan.FromMilliseconds(50))
            .CompleteStructuredAsync(Sys, User, TimeSpan.FromMilliseconds(50));
        Assert.Null(result);   // honored the caller's deadline and failed soft
    }

    // ---- config gates ----------------------------------------------------------

    [Fact]
    public async Task DisabledGateway_MakesNoHttpCall_ReturnsNull()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok("{}"));
        var result = await Make(handler, enabled: false).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public void IsConfigured_TrueWhenUsable_FalseWhenConfigThrows()
    {
        Assert.True(Make(new FakeAiHandler()).IsConfigured);
        Assert.False(new OpenAiCompatibleChatProvider(
            new FakeGatewayConfig { Throw = true }, new FakeAiHandler()).IsConfigured);
    }

    // ---- logging privacy -------------------------------------------------------

    [Fact]
    public async Task Logs_NeverContainKeyPromptOrResponseBody()
    {
        var log = new RecordingLogger();
        var insight = AiTestFixtures.InsightJson("the-secret-prose-should-never-be-logged");
        var handler = new FakeAiHandler(FakeAiHandler.Ok(AiTestFixtures.PlainEnvelope(insight)));
        await Make(handler, log: log).CompleteStructuredAsync("the-prompt-never-logs", User, TimeSpan.FromSeconds(5));

        Assert.NotEmpty(log.Messages);   // it DOES log status/duration/correlation
        foreach (var m in log.Messages)
        {
            Assert.DoesNotContain(AiTestFixtures.FakeApiKey, m, StringComparison.Ordinal);
            Assert.DoesNotContain("the-prompt-never-logs", m, StringComparison.Ordinal);
            Assert.DoesNotContain("the-secret-prose", m, StringComparison.Ordinal);
            Assert.DoesNotContain(User, m, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GarbageBody_ReturnsNullWithoutThrowing()
    {
        var handler = new FakeAiHandler(FakeAiHandler.Ok("<html>502 from proxy</html>"));
        var result = await Make(handler).CompleteStructuredAsync(Sys, User, TimeSpan.FromSeconds(5));
        Assert.Null(result);
    }
}

/// <summary>ILogger adapter exposing the raw strings for the privacy assertions.</summary>
public sealed class PassthroughLogger : ILogger<OpenAiCompatibleChatProvider>
{
    private readonly RecordingLogger _inner;
    public PassthroughLogger(RecordingLogger inner) => _inner = inner;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => _inner.Log(logLevel, eventId, state, exception, formatter);
}
