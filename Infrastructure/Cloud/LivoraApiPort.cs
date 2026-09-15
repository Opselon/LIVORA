using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the real HTTP implementation of the typed API port (§5c). This is the ONLY class in
/// the client that talks to the cloud backend, and it is deliberately reachable only through
/// <see cref="ILivoraApiPort"/>: no ViewModel or page may construct an <see cref="HttpClient"/>.
///
/// MAUI-free by construction: System.Net.Http + System.Text.Json + Microsoft.Extensions.Logging
/// only, so the plain-net10 test project compiles and runs THIS class against scripted
/// <see cref="HttpMessageHandler"/> fakes. Every test in Tests/Tests/Wave4ClientSeam exercises this
/// implementation, not a stand-in.
///
/// CONTRACT DETAILS THAT ARE LOAD-BEARING:
/// <list type="bullet">
///   <item><b>Correlation:</b> every request carries <c>X-Correlation-Id</c> (a fresh
///     <c>Guid:N</c> — which satisfies the server's [A-Za-z0-9-_]{1,64} acceptance rule), and the id
///     the response echoes back is what lands in the result. Support can ask a user for that string;
///     nothing else about the request is retained (§0.5).</item>
///   <item><b>Errors are values:</b> the class never throws for an expected failure — offline, 401,
///     409, 429, 5xx and an unparseable body all come back as a classified
///     <see cref="LivoraApiResult{T}"/> with a localization key. Only cancellation propagates, and
///     only as <see cref="LivoraApiTransportCodes.Canceled"/>.</item>
///   <item><b>Never trusts prose:</b> <c>ApiProblem.detail</c> is kept for the diagnostics sheet only;
///     the user-visible string always comes from the code table
///     (<see cref="LivoraApiCodes.ReasonKeyFor"/>).</item>
///   <item><b>One refresh, one retry:</b> a 401 on a protected call may consume exactly one refresh
///     round-trip. A second rejection means the session is gone; retrying in a loop would hammer the
///     lockout counter the server keeps for this account.</item>
///   <item><b>Unconfigured is not an error:</b> with no base URL every method returns
///     <see cref="LivoraApiTransportCodes.NotConfigured"/> without touching the network. The UI then
///     renders the <c>Unconfigured</c> connector state — the honest answer for a build with no backend.</item>
/// </list>
/// </summary>
public sealed class LivoraApiPort : ILivoraApiPort, IDisposable
{
    /// <summary>Header the server's CorrelationMiddleware honours and echoes.</summary>
    public const string CorrelationHeader = "X-Correlation-Id";
    /// <summary>§5c: replay protection for POST /sync/batch.</summary>
    public const string IdempotencyHeader = "Idempotency-Key";

    private static readonly string[] AuthFreePaths =
    {
        LivoraApiPaths.Register, LivoraApiPaths.Login, LivoraApiPaths.Google,
        LivoraApiPaths.Refresh, LivoraApiPaths.Capabilities,
    };

    private readonly HttpClient _http;
    private readonly ICloudApiOptions _options;
    private readonly ICloudAuthContext? _auth;
    private readonly ILogger _log;
    private readonly Func<string> _correlationIdFactory;

    /// <summary>
    /// <paramref name="handler"/> is injectable on purpose: it is the seam the unit tests drive the
    /// REAL wire format through. The composition root passes nothing and the platform handler is used.
    /// </summary>
    public LivoraApiPort(
        ICloudApiOptions options,
        HttpMessageHandler? handler = null,
        ICloudAuthContext? auth = null,
        ILogger<LivoraApiPort>? logger = null,
        Func<string>? correlationIdFactory = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auth = auth;
        _log = logger ?? NullLogger<LivoraApiPort>.Instance;
        _correlationIdFactory = correlationIdFactory ?? (() => Guid.NewGuid().ToString("N"));
        _http = handler is null
            ? new HttpClient { Timeout = _options.Timeout }
            : new HttpClient(handler, disposeHandler: false) { Timeout = _options.Timeout };
    }

    /// <summary>Machine tag of the transport in play (never a claim about reachability).</summary>
    public const string TransportLabel = "livora-cloud";

    public bool IsConfigured => _options.IsConfigured;

    // ============================ identity (§5c) ==============================================

    public Task<LivoraApiResult<AuthTokensDto>> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAuthenticatedAsync<AuthTokensDto>(HttpMethod.Post, LivoraApiPaths.Register,
            body: request, allowsRefresh: false, ct: ct);
    }

    public Task<LivoraApiResult<AuthTokensDto>> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAuthenticatedAsync<AuthTokensDto>(HttpMethod.Post, LivoraApiPaths.Login,
            body: request, allowsRefresh: false, ct: ct);
    }

    public Task<LivoraApiResult<AuthTokensDto>> LoginWithGoogleAsync(GoogleLoginRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAuthenticatedAsync<AuthTokensDto>(HttpMethod.Post, LivoraApiPaths.Google,
            body: request, allowsRefresh: false, ct: ct);
    }

    /// <summary>
    /// Refresh is special: the port must NOT attach the old bearer (the whole point of the call is
    /// that it is expired) and must NOT route through the auth context (which would recurse). The
    /// caller — the session manager — owns the rotation and stores what comes back.
    /// </summary>
    public Task<LivoraApiResult<AuthTokensDto>> RefreshAsync(RefreshTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<AuthTokensDto>(HttpMethod.Post, LivoraApiPaths.Refresh, body: request,
            attachBearer: false, allowsRefresh: false, ct: ct);
    }

    public Task<LivoraApiResult<bool>> LogoutAsync(CancellationToken ct = default) =>
        SendVoidAsync(HttpMethod.Post, LivoraApiPaths.Logout, body: new { }, ct: ct);

    public Task<LivoraApiResult<IReadOnlyList<SessionInfoDto>>> GetSessionsAsync(CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<SessionInfoDto>>(HttpMethod.Get, LivoraApiPaths.Sessions, body: null,
            attachBearer: true, allowsRefresh: true, ct: ct);

    public Task<LivoraApiResult<bool>> RevokeSessionAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return SendVoidAsync(HttpMethod.Delete, LivoraApiPaths.Session(sessionId), body: null, ct: ct);
    }

    // ============================ account (§5c) ================================================

    public Task<LivoraApiResult<AccountInfoDto>> GetAccountAsync(CancellationToken ct = default) =>
        SendAsync<AccountInfoDto>(HttpMethod.Get, LivoraApiPaths.Account, body: null,
            attachBearer: true, allowsRefresh: true, ct: ct);

    public Task<LivoraApiResult<DeleteRequestDto>> RequestAccountDeletionAsync(CancellationToken ct = default) =>
        SendAsync<DeleteRequestDto>(HttpMethod.Post, LivoraApiPaths.DeleteRequests, body: new { },
            attachBearer: true, allowsRefresh: true, ct: ct);

    public Task<LivoraApiResult<bool>> CancelAccountDeletionAsync(CancellationToken ct = default) =>
        SendVoidAsync(HttpMethod.Delete, LivoraApiPaths.DeleteRequests, body: new { }, ct: ct);

    public Task<LivoraApiResult<AccountExportDto>> GetAccountExportAsync(CancellationToken ct = default) =>
        SendAsync<AccountExportDto>(HttpMethod.Get, LivoraApiPaths.Export, body: null,
            attachBearer: true, allowsRefresh: true, ct: ct);

    // ============================ sync (§5c) =====================================================

    public Task<LivoraApiResult<SyncBatchResponseDto>> SyncBatchAsync(
        SyncBatchRequest request, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return SendAsync<SyncBatchResponseDto>(HttpMethod.Post, LivoraApiPaths.SyncBatch, body: request,
            attachBearer: true, allowsRefresh: true, extraHeaders: new Dictionary<string, string>
            {
                [IdempotencyHeader] = idempotencyKey,
            }, ct: ct);
    }

    public Task<LivoraApiResult<SyncChangesResponseDto>> GetSyncChangesAsync(
        long? since, int? limit, CancellationToken ct = default)
    {
        var query = new List<string>(2);
        if (since is not null) query.Add("since=" + since.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (limit is not null) query.Add("limit=" + Math.Clamp(limit.Value, 1, 1000).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var path = LivoraApiPaths.SyncChanges + (query.Count > 0 ? "?" + string.Join("&", query) : "");
        return SendAsync<SyncChangesResponseDto>(HttpMethod.Get, path, body: null,
            attachBearer: true, allowsRefresh: true, ct: ct);
    }

    public Task<LivoraApiResult<CapabilitySnapshotDto>> GetCapabilitiesAsync(CancellationToken ct = default) =>
        SendAsync<CapabilitySnapshotDto>(HttpMethod.Get, LivoraApiPaths.Capabilities, body: null,
            attachBearer: false, allowsRefresh: false, ct: ct);

    // ============================ the one request pipeline ======================================

    private async Task<LivoraApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, bool attachBearer, bool allowsRefresh,
        IReadOnlyDictionary<string, string>? extraHeaders = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.NotConfigured);

        var attempt = await OnceAsync<T>(method, path, body, attachBearer, extraHeaders, ct).ConfigureAwait(false);

        // One transparent refresh + replay for a protected call whose bearer the server rejected.
        if (allowsRefresh && attempt.Status == (int)HttpStatusCode.Unauthorized
            && _auth is not null && !RefreshOnly(path))
        {
            _log.LogInformation("cloud-auth cid={CorrelationId} path={Path} refresh=attempt",
                attempt.CorrelationId, path);
            bool rotated = await _auth.TryRefreshAsync(ct).ConfigureAwait(false);
            if (rotated)
                attempt = await OnceAsync<T>(method, path, body, attachBearer, extraHeaders, ct).ConfigureAwait(false);
            else
                _auth.HandleAuthRejected(attempt.Code);
        }
        return attempt;
    }

    private Task<LivoraApiResult<T>> SendAuthenticatedAsync<T>(
        HttpMethod method, string path, object? body, bool allowsRefresh, CancellationToken ct)
        // Auth entry points never attach a bearer (there is no session yet); the flag exists so a
        // reviewer can see the rule is explicit per call site, not inherited from a default.
        => SendAsync<T>(method, path, body, attachBearer: false, allowsRefresh: allowsRefresh, ct: ct);

    /// <summary>Void endpoints (204/200 with no contract body) map to a bool result.</summary>
    private async Task<LivoraApiResult<bool>> SendVoidAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        if (!IsConfigured)
            return LivoraApiResult<bool>.TransportFailure(LivoraApiTransportCodes.NotConfigured);

        var attempt = await OnceAsync<object?>(method, path, body, attachBearer: true, extraHeaders: null, ct)
            .ConfigureAwait(false);
        if (attempt.Ok) return LivoraApiResult<bool>.Success(true, attempt.Status, attempt.CorrelationId);
        return attempt.AsFailure<bool>();
    }

    /// <summary>
    /// Exactly one round-trip: build, send, classify. No retry policy lives here — a drain that wants
    /// retries owns its own attempt count, so the queue can record what each attempt proved.
    /// </summary>
    private async Task<LivoraApiResult<T>> OnceAsync<T>(
        HttpMethod method, string path, object? body, bool attachBearer,
        IReadOnlyDictionary<string, string>? extraHeaders, CancellationToken ct)
    {
        string correlationId = SafeCorrelationId(_correlationIdFactory());
        using var request = new HttpRequestMessage(method, _options.BaseUrl + path);
        request.Headers.TryAddWithoutValidation(CorrelationHeader, correlationId);

        if (attachBearer && _auth is not null)
        {
            var bearer = await _auth.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        foreach (var (name, value) in extraHeaders ?? Empty)
            request.Headers.TryAddWithoutValidation(name, value);

        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, LivoraJson.Options),
                Encoding.UTF8, "application/json");

        HttpResponseMessage? response = null;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            int status = (int)response.StatusCode;
            string echoed = EchoedCorrelationId(response, correlationId);
            string text = await ReadBodyAsync(response, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                if (status == (int)HttpStatusCode.NoContent || string.IsNullOrWhiteSpace(text))
                {
                    // Only the void endpoints (mapped to object?) may answer with no body. A data
                    // endpoint that returns 200 + nothing has NOT delivered the contract's shape, and
                    // reporting that as a success with a null value is how a UI ends up rendering an
                    // empty account card as if it were real data.
                    if (typeof(T) == typeof(object))
                        return LivoraApiResult<T>.Success(default!, status, echoed);
                    return LivoraApiResult<T>.Failure(status, LivoraApiTransportCodes.MalformedResponse,
                        problem: null, correlationId: echoed);
                }

                try
                {
                    var value = JsonSerializer.Deserialize<T>(text, LivoraJson.Options);
                    if (value is null && typeof(T) != typeof(object))
                        return LivoraApiResult<T>.Failure(status, LivoraApiTransportCodes.MalformedResponse,
                            problem: null, correlationId: echoed);
                    return LivoraApiResult<T>.Success(value!, status, echoed);
                }
                catch (JsonException)
                {
                    // 2xx with a body that is not the contract's shape. Report the shape problem —
                    // do NOT reinterpret it as a success with an empty value.
                    return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.MalformedResponse);
                }
            }

            ApiProblemDto? problem = TryReadProblem(text);
            string? code = problem?.EffectiveCode ?? CodeFromStatus(status);
            if (code is not null && problem is null)
            {
                // No envelope: classify by status only, and say so through the transport tags rather
                // than inventing a server code the host never sent.
                return LivoraApiResult<T>.Failure(status, code, problem: null, correlationId: echoed);
            }
            return LivoraApiResult<T>.Failure(status, code, problem, echoed, problem?.Errors);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.Canceled);
        }
        catch (TaskCanceledException)
        {
            // HttpClient signals a timeout by cancelling the request task (no inner exception on .NET).
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.Timeout);
        }
        catch (HttpRequestException)
        {
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.Network);
        }
        catch (InvalidOperationException)
        {
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.InvalidBaseUrl);
        }
        catch (JsonException)
        {
            return LivoraApiResult<T>.TransportFailure(LivoraApiTransportCodes.MalformedResponse);
        }
        finally
        {
            response?.Dispose();
        }
    }

    // ---- classification helpers ---------------------------------------------------------------

    /// <summary>
    /// Status-only codes. The set is exactly what §5c's error table can produce without an envelope;
    /// anything else is <see cref="LivoraApiTransportCodes.UnexpectedResponse"/> — the client does not
    /// guess a machine code the server never sent.
    /// </summary>
    internal static string? CodeFromStatus(int status) => status switch
    {
        400 => LivoraApiCodes.ValidationFailed,
        401 => LivoraApiCodes.Unauthenticated,
        403 => LivoraApiCodes.Forbidden,
        404 => LivoraApiCodes.NotFound,
        409 => LivoraApiCodes.Conflict,
        428 => LivoraApiCodes.PermissionRequired,
        429 => LivoraApiCodes.RateLimited,
        503 => LivoraApiCodes.ProviderUnavailable,
        >= 500 => LivoraApiCodes.InternalError,
        _ => LivoraApiTransportCodes.UnexpectedResponse,
    };

    private static ApiProblemDto? TryReadProblem(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            // A gateway HTML error page or a stack trace is not an envelope; refuse to parse it into
            // a problem (and therefore refuse to render any of its prose).
            if (!doc.RootElement.TryGetProperty("status", out _) &&
                !doc.RootElement.TryGetProperty("code", out _) &&
                !doc.RootElement.TryGetProperty("type", out _)) return null;
            return doc.RootElement.Deserialize<ApiProblemDto>(LivoraJson.Options);
        }
        catch (JsonException) { return null; }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (InvalidOperationException) { return ""; }   // body already consumed / connection reset
    }

    private static string SafeCorrelationId(string candidate)
    {
        // Same acceptance rule the server applies, applied client-side so a factory bug cannot forge
        // log lines through a header value (CR/LF, spaces, length).
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 64
            || !candidate.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return Guid.NewGuid().ToString("N");
        return candidate;
    }

    private static string EchoedCorrelationId(HttpResponseMessage response, string fallback)
    {
        if (response.Headers.TryGetValues(CorrelationHeader, out var values))
        {
            var echoed = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(echoed) && echoed.Length <= 64) return echoed;
        }
        return fallback;
    }

    private static bool RefreshOnly(string path) =>
        path.StartsWith(LivoraApiPaths.Refresh, StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    /// <summary>Paths that never carry a bearer, exposed for the port's own honesty test.</summary>
    public static bool IsAuthFreePath(string path) => AuthFreePaths.Contains(path, StringComparer.Ordinal);

    public void Dispose() => _http.Dispose();
}
