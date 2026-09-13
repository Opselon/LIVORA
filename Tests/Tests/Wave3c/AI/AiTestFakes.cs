using System.Net;
using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using Microsoft.Extensions.Logging;

namespace LIVORA.Tests.Tests;

// =============================================================================
// Wave 3c lane 02 (AI orchestration) — test doubles. NO live network anywhere in
// the unit suite: every byte the transport reads comes from FakeAiHandler.
// =============================================================================

/// <summary>Fake HTTP transport: scripted responses per attempt, full request capture.</summary>
public sealed class FakeAiHandler : HttpMessageHandler
{
    public sealed record ScriptedResponse(HttpStatusCode Status, string? Body, TimeSpan? RetryAfter = null);

    private readonly List<ScriptedResponse> _responses;
    public List<HttpRequestMessage> Requests { get; } = new();
    public List<string> RequestBodies { get; } = new();
    public int CallCount => Requests.Count;
    /// <summary>When set, the handler honors ct but blocks this long first (timeout tests).</summary>
    public TimeSpan ArtificialDelay { get; set; }

    public FakeAiHandler(params ScriptedResponse[] responses)
        => _responses = responses.ToList();

    public static ScriptedResponse Ok(string body) => new(HttpStatusCode.OK, body);
    public static ScriptedResponse Status(HttpStatusCode code, string? body = null, TimeSpan? retryAfter = null)
        => new(code, body, retryAfter);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        RequestBodies.Add(request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(ct));

        var scripted = _responses[Math.Min(CallCount - 1, _responses.Count - 1)];
        if (ArtificialDelay > TimeSpan.Zero) await Task.Delay(ArtificialDelay, ct);
        ct.ThrowIfCancellationRequested();

        var resp = new HttpResponseMessage(scripted.Status);
        if (scripted.Body is not null)
            resp.Content = new StringContent(scripted.Body, Encoding.UTF8, "application/json");
        if (scripted.RetryAfter is { } ra)
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
        return resp;
    }
}

/// <summary>Records delays instead of sleeping — backoff becomes assertable and instant.</summary>
public sealed class RecordingDelays
{
    public List<TimeSpan> Waits { get; } = new();
    public Task Delay(TimeSpan t, CancellationToken ct)
    {
        Waits.Add(t);
        return Task.CompletedTask;
    }
}

/// <summary>Captures every formatted log message so privacy can be asserted.</summary>
public sealed class RecordingLogger : ILogger
{
    public List<string> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
}

/// <summary>Config seam with fixed values (the transport under test is real).</summary>
public sealed class FakeGatewayConfig : IGatewayConfigService
{
    public GatewayConfig Config { get; set; } = AiTestFixtures.GatewayConfig();
    public int Calls { get; private set; }
    public bool Throw { get; set; }

    public Task<GatewayConfig?> GetEffectiveAsync(CancellationToken ct = default)
    {
        Calls++;
        if (Throw) throw new InvalidOperationException("config blew up");
        return Task.FromResult<GatewayConfig?>(Config);
    }
    public Task SetUserKeyOverrideAsync(string? apiKey, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    public Task<GatewayPublicStatus> GetPublicStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new GatewayPublicStatus
        {
            IsConfigured = true, IsEnabled = Config.Enabled, UsesInsecureTransport = true,
            Model = Config.Model, KeySource = "fake",
        });
}

public sealed class FakeConsent : IConsentService
{
    public ConsentDecision AiDecision { get; set; } = ConsentDecision.Granted;
    public ConsentDecision Get(ConsentCategory category) =>
        category == ConsentCategory.AiProcessing ? AiDecision : ConsentDecision.Denied;
    public Task SetAsync(ConsentCategory category, ConsentDecision decision, CancellationToken ct = default)
    { if (category == ConsentCategory.AiProcessing) AiDecision = decision; return Task.CompletedTask; }
    public Task<IReadOnlyDictionary<ConsentCategory, ConsentDecision>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<ConsentCategory, ConsentDecision>>(
            new Dictionary<ConsentCategory, ConsentDecision> { [ConsentCategory.AiProcessing] = AiDecision });
    public Task RevokeAllAsync(CancellationToken ct = default) { AiDecision = ConsentDecision.Denied; return Task.CompletedTask; }
}

/// <summary>Chat seam for orchestrator tests: canned completion, no HTTP at all.</summary>
public sealed class FakeChatProvider : IIntelligenceChatProvider
{
    public string? Completion { get; set; }
    public bool Configured { get; set; } = true;
    public bool Hang { get; set; }
    public int Calls { get; private set; }
    public string? LastSystemPrompt { get; private set; }
    public string? LastUserJson { get; private set; }
    public Exception? ThrowOnCall { get; set; }

    public bool IsConfigured => Configured;
    public AiProviderKind Kind => AiProviderKind.ExternalLlm;
    public string ProviderLabel => "AI: fake";

    public async Task<string?> CompleteStructuredAsync(
        string systemPrompt, string userJson, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls++;
        LastSystemPrompt = systemPrompt;
        LastUserJson = userJson;
        if (ThrowOnCall is not null) throw ThrowOnCall;
        if (Hang) { await Task.Delay(Timeout.Infinite, ct); return null; }
        return Completion;
    }
}

/// <summary>Shared fixtures: a synthetic sleep-debt state + the shapes the gateway sends.</summary>
internal static class AiTestFixtures
{
    public const string FakeApiKey = "sk-TES…cdef";
    /// <summary>Tokens that must NEVER reach the wire — planted in free-text fields of the fake user.</summary>
    public const string ForbiddenNameToken = "Zartosht-Restricted-Name";
    public const string ForbiddenNoteToken = "NEVERLEAK-note-token-7";
    public const string ForbiddenHabitToken = "SecretHabitNoteToken";

    public static GatewayConfig GatewayConfig(bool enabled = true, TimeSpan? timeout = null) => new()
    {
        BaseUrl = "http://gateway.invalid.test/v1",
        ApiKey = FakeApiKey,
        Model = "coding",
        Enabled = enabled,
        Timeout = timeout ?? TimeSpan.FromSeconds(20),
    };

    /// <summary>Sleep-debt state: 300 min vs 420 baseline. Extra metrics exercise the 12-cap.</summary>
    public static PersonalState State(int extraMetrics = 0)
    {
        var m = new Dictionary<string, MetricState>
        {
            [Metrics.SleepMinutes] = Metric(Metrics.SleepMinutes, 300, 420),
            [Metrics.RecoveryScore] = Metric(Metrics.RecoveryScore, 0.5, 0.7),
            [Metrics.Stress] = Metric(Metrics.Stress, 0.8, 0.4, higherBetter: false),
            [Metrics.Steps] = Metric(Metrics.Steps, 4000, 9000),
            [Metrics.ActiveMinutes] = Metric(Metrics.ActiveMinutes, 10, 30),
            [Metrics.SleepQuality] = Metric(Metrics.SleepQuality, 0.6, 0.8),
            [Metrics.SleepConsistency] = Metric(Metrics.SleepConsistency, 0.7, 0.9),
            [Metrics.BedtimeMinutes] = Metric(Metrics.BedtimeMinutes, 1400, 1380, higherBetter: false),
            [Metrics.RestingHeartRate] = Metric(Metrics.RestingHeartRate, 62, 56, higherBetter: false),
            [Metrics.HrvMs] = Metric(Metrics.HrvMs, 28, 45),
            [Metrics.Mood] = Metric(Metrics.Mood, 0.5, 0.7),
            [Metrics.Energy] = Metric(Metrics.Energy, 0.4, 0.7),
            [Metrics.FocusEstimate] = Metric(Metrics.FocusEstimate, 0.45, 0.7),
        };
        for (int i = 0; i < extraMetrics; i++) m[$"extra.metric{i}"] = Metric($"extra.metric{i}", 5, 5);

        return new PersonalState
        {
            GeneratedAt = new DateTime(2026, 9, 11, 10, 0, 0),
            Confidence = 0.8,
            DataCompleteness = 1,
            Sleep = new SleepState
            {
                Duration = m[Metrics.SleepMinutes], Quality = m[Metrics.SleepQuality],
                Consistency = m[Metrics.SleepConsistency], Bedtime = m[Metrics.BedtimeMinutes],
            },
            Activity = new DailyActivityState { Steps = m[Metrics.Steps], ActiveMinutes = m[Metrics.ActiveMinutes] },
            Recovery = new RecoveryState { Score = m[Metrics.RecoveryScore], RestingHeartRate = m[Metrics.RestingHeartRate], Hrv = m[Metrics.HrvMs] },
            Wellness = new WellnessState2 { Stress = m[Metrics.Stress], Mood = m[Metrics.Mood], Energy = m[Metrics.Energy] },
            Focus = new FocusState { Estimated = m[Metrics.FocusEstimate] },
            Habits = new HabitStateSnapshot { HabitId = "h", Name = ForbiddenHabitToken, Streak = 3 },
            HabitSnapshots = new[] { new HabitStateSnapshot { HabitId = "h", Name = ForbiddenHabitToken, Streak = 3 } },
            GoalSnapshots = new[] { new GoalStateSnapshot { GoalId = "g", Name = "Deep Sleep Project", Fraction = 0.4, Status = GoalStatus.OnTrack } },
            Metrics = m,
        };
    }

    private static MetricState Metric(string key, double value, double baseline, bool higherBetter = true) => new()
    {
        MetricKey = key,
        Value = value,
        BaselineValue = baseline,
        BaselineConfidence = BaselineConfidence.High,
        RelativeDeviation = baseline > 0 ? (value - baseline) / baseline : null,
        HigherIsBetter = higherBetter,
        Quality = DataQuality.Complete,
    };

    public static UserProfile Profile() => new()
    {
        Id = "p",
        Name = ForbiddenNameToken,
        FocusAreas = new List<string> { "note:" + ForbiddenNoteToken },
    };

    public static Recommendation Rec(RecommendationActionKind kind = RecommendationActionKind.ShortWalk)
        => new() { TextKey = "Rec." + kind, ActionKind = kind };

    /// <summary>The insight JSON our prompt asks for — the payload the fake gateway echoes back.</summary>
    public static string InsightJson(
        string body = "Your sleep is below your usual. A short walk may lift the afternoon.",
        string claimsJson = "{\"metricKey\":\"sleep.minutes\",\"value\":300,\"unit\":\"minutes\"}")
        => $$"""
           {"headlineKey":"Ai.Headline.Provider","bodyKey":"Ai.Body.Provider","bodyText":"{{body}}","confidence":0.8,"claims":[{{claimsJson}}],"proposedActionKinds":["ShortWalk"]}
           """;

    /// <summary>Plain chat.completion envelope carrying the given content.</summary>
    public static string PlainEnvelope(string content) => JsonSerializer.Serialize(new
    {
        id = "chatcmpl-1",
        model = "coding",
        choices = new[] { new { index = 0, message = new { role = "assistant", content } } },
    });

    /// <summary>SSE stream: content split across delta chunks, finished by [DONE].</summary>
    public static string SseStream(string content, int chunkSize = 24)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < content.Length; i += chunkSize)
        {
            var piece = content.Substring(i, Math.Min(chunkSize, content.Length - i));
            sb.Append("data: ")
              .Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = piece } } } }))
              .Append("\n\n");
        }
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }
}
