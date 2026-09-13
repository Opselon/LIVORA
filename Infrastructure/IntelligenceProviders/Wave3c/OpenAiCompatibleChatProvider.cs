using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Intelligence;
using LIVORA.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Infrastructure.IntelligenceProviders.Wave3c;

/// <summary>
/// OpenAI-compatible transport for the Wave 3c AI gateway (lane 02). Talks to any
/// /chat/completions endpoint; the one LIVORA ships configured against STREAMS SSE even when
/// we ask for stream:false — so this class accepts BOTH response shapes: a plain
/// chat.completion JSON body OR an SSE `data:` stream accumulated to [DONE].
///
/// HONESTY / SAFETY CONTRACT (asserted by Tests/Wave3c/AI):
///  - returns null on ANY failure (never throws into the app — the orchestrator falls back);
///  - the logger sees ONLY status code + duration ms + a correlation id — never the key, the
///    prompt, or the response body;
///  - 429/5xx: exponential backoff, max 2 retries, honoring Retry-After when present;
///  - a 4xx that complains about response_format is retried ONCE without that field (older
///    gateways reject it); every other 4xx is final;
///  - per-attempt deadline = the caller's timeout (from GatewayConfig) AND the caller's
///    CancellationToken are both honored; User-Agent is LIVORA-app.
/// Tests inject a fake HttpMessageHandler + a fake delay function; production uses the static
/// default handler and Task.Delay.
/// </summary>
public sealed class OpenAiCompatibleChatProvider : IIntelligenceChatProvider
{
    /// <summary>The app's identity on the wire. Nothing else may masquerade as LIVORA.</summary>
    public const string UserAgentValue = "LIVORA-app";

    /// <summary>Cap the response we're willing to buffer — a runaway stream must not eat RAM.</summary>
    internal const int MaxResponseBytes = 256 * 1024;

    private const int MaxRetries = 2;                       // 429/5xx only
    private static readonly TimeSpan BackoffBase = TimeSpan.FromMilliseconds(500);

    private readonly IGatewayConfigService _gateway;
    private readonly HttpMessageHandler? _injectedHandler;  // tests: fake handler; prod: null
    private readonly Func<TimeSpan, CancellationToken, Task> _delay; // tests: record, don't sleep
    private readonly ILogger _log;

    /// <summary>Shared default handler for production (no test handler injected).</summary>
    private static readonly HttpMessageHandler DefaultHandler = new HttpClientHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    // IsConfigured caching: the property is sync by contract but config is async, so we cache a
    // verdict briefly (Task.Run avoids a UI-thread deadlock against a sync context).
    private readonly object _gate = new();
    private bool? _configuredCache;
    private DateTime _configuredUntilUtc;

    public OpenAiCompatibleChatProvider(
        IGatewayConfigService gateway,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger<OpenAiCompatibleChatProvider>? logger = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _injectedHandler = handler;
        _delay = delay ?? Task.Delay;
        _log = logger ?? NullLogger<OpenAiCompatibleChatProvider>.Instance;
    }

    public AiProviderKind Kind => AiProviderKind.ExternalLlm;
    public string ProviderLabel => "AI: external endpoint";

    /// <summary>
    /// True when a usable endpoint is configured (base URL + key + model). NOTE the deviation
    /// from the contract comment ("reachable at least once"): blocking the FIRST call until some
    /// earlier call succeeded would make the app unable to ever prove reachability. The
    /// "verified reachable" fact is tracked separately (LastVerifiedUtc below) for the honesty
    /// UI — this gate only says "we have something to call".
    /// </summary>
    public bool IsConfigured
    {
        get
        {
            lock (_gate)
            {
                if (_configuredCache.HasValue && DateTime.UtcNow < _configuredUntilUtc)
                    return _configuredCache.Value;
            }
            bool ok;
            try
            {
                ok = Usable(Task.Run(() => _gateway.GetEffectiveAsync()).GetAwaiter().GetResult());
            }
            catch { ok = false; }
            lock (_gate)
            {
                _configuredCache = ok;
                _configuredUntilUtc = DateTime.UtcNow.AddSeconds(15);
            }
            return ok;
        }
    }

    /// <summary>UTC of the last successful round-trip (honesty UI only; null = never proven).</summary>
    public DateTime? LastVerifiedUtc { get; private set; }

    private static bool Usable(GatewayConfig? cfg) =>
        cfg is not null && !string.IsNullOrWhiteSpace(cfg.BaseUrl)
        && !string.IsNullOrWhiteSpace(cfg.ApiKey) && !string.IsNullOrWhiteSpace(cfg.Model);

    public async Task<string?> CompleteStructuredAsync(
        string systemPrompt, string userJson, TimeSpan timeout, CancellationToken ct = default)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        var sw = Stopwatch.StartNew();
        try
        {
            var config = await _gateway.GetEffectiveAsync(ct).ConfigureAwait(false);
            if (!Usable(config) || config!.Enabled != true)
            {
                Log("not-configured-or-disabled", 0);
                return null;
            }
            var effectiveTimeout = timeout <= TimeSpan.Zero ? config.Timeout : timeout;

            bool responseFormatSupported = true;   // flips false after the once-only variant retry
            int attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var (body, status, retryAfter) = await SendOnceAsync(
                    config, systemPrompt, userJson, responseFormatSupported, effectiveTimeout, ct)
                    .ConfigureAwait(false);

                if (status is >= 200 and < 300 && body is not null)
                {
                    var content = AiResponseParser.ExtractAssistantContent(body);
                    if (!AiResponseParser.ContainsJsonStart(content)) { Log("non-json-content", status); return null; }
                    LastVerifiedUtc = DateTime.UtcNow;
                    Log($"http-{status}", status);
                    return content;
                }

                // 4xx that names response_format: the gateway is old — retry ONCE without it.
                if (status is >= 400 and < 500 && responseFormatSupported
                    && body is not null && body.Contains("response_format", StringComparison.OrdinalIgnoreCase))
                {
                    responseFormatSupported = false;
                    Log($"format-retry({status})", status);
                    continue;
                }

                // 429/5xx: exponential backoff honoring Retry-After, max 2 retries.
                if ((status == 429 || status >= 500) && attempt < MaxRetries)
                {
                    var backoff = retryAfter ?? BackoffBase * (int)Math.Pow(2, attempt);
                    Log($"retry({status},wait={backoff.TotalMilliseconds:F0}ms)", status);
                    await _delay(backoff, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                Log(status == 0 ? "transport-failure" : $"http-{status}", status);
                return null;
            }
        }
        catch (OperationCanceledException) { Log("canceled-or-timeout", 0); return null; }
        catch (Exception) { Log("error", 0); return null; }   // NEVER throw — null = fall back

        void Log(string status, int code) =>
            // status+duration+correlation-id ONLY: no key, no prompt, no response body — ever.
            _log.LogInformation("ai-call cid={CorrelationId} status={Status} ms={ElapsedMs} code={StatusCode}",
                correlationId, status, sw.ElapsedMilliseconds, code);
    }

    private async Task<(string? Body, int Status, TimeSpan? RetryAfter)> SendOnceAsync(
        GatewayConfig config, string systemPrompt, string userJson, bool includeResponseFormat,
        TimeSpan timeout, CancellationToken ct)
    {
        // Injected handlers are OWNED by the caller (tests reuse them); the static default is
        // shared forever — disposeHandler:false in both cases.
        using var client = new HttpClient(_injectedHandler ?? DefaultHandler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,   // we drive the deadline via CTS so cancellation is uniform
        };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentValue + "/1");  // "LIVORA-app/1"
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");

        var payload = BuildRequestJson(config.Model, systemPrompt, userJson, includeResponseFormat);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var message = new HttpRequestMessage(HttpMethod.Post, JoinChatUrl(config.BaseUrl))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return (null, 0, null);   // transport failure: status 0 — caller maps to null
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            TimeSpan? retryAfter = TryReadRetryAfter(response.Headers.RetryAfter);
            string? body = null;
            try
            {
                body = await ReadCappedAsync(response.Content, cts.Token).ConfigureAwait(false);
            }
            catch { body = null; }

            if (response.IsSuccessStatusCode)
                return (body, status, null);
            return (body, status, retryAfter);
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[8192];
        var sb = new StringBuilder();
        int total = 0, n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += n;
            if (total > MaxResponseBytes) return null;   // runaway body: refuse, treat as failure
            sb.Append(Encoding.UTF8.GetString(buffer, 0, n));
        }
        return sb.ToString();
    }

    private static TimeSpan? TryReadRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null) return null;
        if (header.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(60))
            return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero && wait <= TimeSpan.FromSeconds(60)) return wait;
        }
        return null;
    }

    /// <summary>The request is hand-built with System.Text.Json — fixed field order, no POCO magic.</summary>
    internal static string BuildRequestJson(
        string model, string systemPrompt, string userJson, bool includeResponseFormat)
    {
        var dict = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new Dictionary<string, object> { ["role"] = "system", ["content"] = systemPrompt },
                new Dictionary<string, object> { ["role"] = "user", ["content"] = userJson },
            },
            ["temperature"] = 0.2,
            ["max_tokens"] = 512,
            ["stream"] = false,
        };
        if (includeResponseFormat)
            dict["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" };
        return JsonSerializer.Serialize(dict);
    }

    /// <summary>/v1 + /chat/completions without ever doubling the suffix or dropping the path.</summary>
    internal static string JoinChatUrl(string baseUrl)
    {
        var b = baseUrl.TrimEnd('/');
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? b
            : b + "/chat/completions";
    }
}
