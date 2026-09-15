using System.Net;
using System.Net.Http.Headers;
using System.Text;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Cloud;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Cloud;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;

namespace LIVORA.Tests.Tests.Wave4ClientSeam;

/// <summary>
/// Shared scaffolding for the Wave 4 P1-D suites. Two things matter here:
/// <list type="bullet">
///   <item><b>No live backend exists</b>, so the wire is driven by <see cref="ScriptedHandler"/> — a
///     deterministic <see cref="HttpMessageHandler"/> that records every request and replays frozen
///     §5c shapes. Every seam test therefore runs the REAL <see cref="LivoraApiPort"/>,
///     <see cref="CloudSyncTransport"/> and <see cref="CloudSyncBridge"/> code, not a stand-in.</item>
///   <item>Everything else is the real class from the app tree too (SecureStorageService + a fake
///     platform box, LocalJsonStore in a temp dir, the actual SyncQueue/MetaIndex), so a change to
///     any of them fails these tests rather than silently diverging.</item>
/// </list>
/// This file defines its own fakes instead of borrowing Wave 3c's <c>Lane01Harness</c>: lanes run in
/// parallel and a borrowed fake that another lane edits would make this suite fail for a reason that
/// is not this lane's.
/// </summary>
internal static class Wave4SeamHarness
{
    /// <summary>A temp directory that deletes itself with the test.</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Root { get; }

        public TempDir(string area)
        {
            Root = Path.Combine(Path.GetTempPath(), "livora-w4-p1d", area, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>A LocalJsonStore rooted at &lt;tmp&gt;/&lt;sub&gt; (the app passes AppData/LIVORA/&lt;sub&gt;).</summary>
        public LocalJsonStore Store(string sub = "root") => new(Path.Combine(Root, sub));

        public string PathOf(string sub) => Path.Combine(Root, sub);

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The non-Windows box shape: identity transform + an honest "no cipher" label.</summary>
    internal sealed class NoCipherBox : IPlatformSecureBox
    {
        public string Label => "test-no-cipher";
        public bool IsHardwareOrOsBacked => false;
        public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();
        public byte[] Unprotect(byte[] ciphertext) => (byte[])ciphertext.Clone();
    }

    /// <summary>A box that claims OS backing, so the IsOsBacked read-through is provable both ways.</summary>
    internal sealed class OsBackedBox : IPlatformSecureBox
    {
        public string Label => "test-os-backed";
        public bool IsHardwareOrOsBacked => true;
        // Deliberately NOT the identity: a byte scan must find no plaintext for this box either.
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
    }

    /// <summary>Real SecureStorageService over a temp dir + the fake box (the same stack the app runs).</summary>
    public static SecureStorageService SecureStore(TempDir dir, IPlatformSecureBox? box = null) =>
        new(dir.Store(SecureStorageService.StoreDirName), box ?? new NoCipherBox());

    /// <summary>In-memory ISecureStorageService for call-count assertions.</summary>
    internal sealed class MemoryStorage : ISecureStorageService
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
        public int Gets, Sets, Removes;

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            Gets++;
            return Task.FromResult(_map.TryGetValue(key, out var v) ? v : null);
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Sets++;
            _map[key] = value;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken ct = default)
        {
            Removes++;
            _map.Remove(key);
            return Task.CompletedTask;
        }

        /// <summary>The live map (tests plant corrupt/foreign values directly into it).</summary>
        public Dictionary<string, string> Raw => _map;
    }

    /// <summary>One scripted answer, matched by method + path (query included).</summary>
    internal sealed record Scripted(
        string Method, string PathContains, Func<HttpRequestMessage, HttpResponseMessage> Responder);

    /// <summary>
    /// The scripted wire: records every request (method, path, headers, body) and answers in script
    /// order for the matching path. A request that matches nothing FAILS the test loudly (a 599 +
    /// recorded marker) instead of hanging or inventing a response.
    /// </summary>
    internal sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly List<Scripted> _routes = new();
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _any = new();

        public sealed record Seen(string Method, string Uri, string? Body,
            IReadOnlyDictionary<string, string> Headers, string? Authorization);

        public List<Seen> Requests { get; } = new();

        public ScriptedHandler On(string method, string pathContains,
            Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _routes.Add(new Scripted(method, pathContains, responder));
            return this;
        }

        public ScriptedHandler Json(string method, string pathContains, int status, string body)
            => On(method, pathContains, _ => Response(status, body));

        public ScriptedHandler Problem(string method, string pathContains, int status, string code,
            string? detail = null, string? correlationId = "cid-test")
            => Json(method, pathContains, status, Envelope(status, code, detail, correlationId));

        /// <summary>Answers thrown out of order are fine for simple scripts: last matching route wins per call.</summary>
        public ScriptedHandler Sequence(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _any.Enqueue(responder);
            return this;
        }

        public int CallCount(string? method = null, string? pathContains = null) =>
            Requests.Count(r => (method is null || r.Method == method)
                                 && (pathContains is null || r.Uri.Contains(pathContains, StringComparison.Ordinal)));

        public Seen? Last(string? method = null, string? pathContains = null) =>
            Requests.LastOrDefault(r => (method is null || r.Method == method)
                                         && (pathContains is null || r.Uri.Contains(pathContains, StringComparison.Ordinal)));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null
                ? null!
                : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var headers = request.Headers.ToDictionary(
                h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri?.ToString() ?? "", body,
                headers, request.Headers.Authorization?.Parameter));

            var route = _routes.LastOrDefault(r =>
                string.Equals(r.Method, request.Method.Method, StringComparison.OrdinalIgnoreCase)
                && (request.RequestUri?.AbsolutePath ?? "").Contains(r.PathContains, StringComparison.Ordinal));
            HttpResponseMessage Answered(Func<HttpRequestMessage, HttpResponseMessage> f)
            {
                var res = f(request);
                // CorrelationMiddleware honours the incoming id and echoes it back: mirror that here so
                // the port's "the id I sent is the id I read" path is exercised for real.
                if (request.Headers.TryGetValues("X-Correlation-Id", out var ids))
                {
                    res.Headers.Remove("X-Correlation-Id");
                    res.Headers.TryAddWithoutValidation("X-Correlation-Id", ids.FirstOrDefault());
                }
                return res;
            }

            if (route is not null) return Answered(route.Responder);
            if (_any.Count > 0) return Answered(_any.Dequeue());

            // Unscripted: a loud, non-2xx answer so the assertion failure points at the missing script.
            return Response(599, """{"type":"https://livora.app/problems/test_unscripted","title":"test_unscripted","status":599,"detail":"no script","instance":"/","code":"test_unscripted","correlationId":"unscripted","errors":null}""");
        }
    }

    /// <summary>The frozen §5c envelope, built from the same field list the server record has.</summary>
    public static string Envelope(int status, string code, string? detail = null, string? correlationId = "cid-test") =>
        $$"""{"type":"https://livora.app/problems/{{code}}","title":"{{code}}","status":{{status}},"detail":"{{detail ?? "scripted detail"}}","instance":"/api/v1","code":"{{code}}","correlationId":"{{correlationId}}","errors":null}""";

    public static HttpResponseMessage Response(int status, string json, string? correlationId = null)
    {
        var res = new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var cid = correlationId ?? "cid-" + Guid.NewGuid().ToString("N")[..8];
        res.Headers.TryAddWithoutValidation("X-Correlation-Id", cid);
        return res;
    }

    public static HttpResponseMessage NoContent(int status = 204) => new((HttpStatusCode)status);

    /// <summary>A non-2xx answer carrying the frozen envelope (what every failure path must parse).</summary>
    public static HttpResponseMessage ProblemResponse(int status, string code, string? correlationId = "cid-test") =>
        Response(status, Envelope(status, code, correlationId: correlationId));

    /// <summary>The §5c token body, with a distinctive refresh token per call unless overridden.</summary>
    public static string TokensBody(string userId = "u-1", string sessionId = "s-1",
        string? access = "at-1", string? refresh = "rt-1",
        DateTimeOffset? expires = null) =>
        $$"""{"userId":"{{userId}}","accessToken":"{{access ?? "at-" + Guid.NewGuid().ToString("N")[..6]}}","refreshToken":"{{refresh ?? "rt-" + Guid.NewGuid().ToString("N")[..6]}}","expiresAtUtc":"{{(expires ?? DateTimeOffset.UtcNow.AddMinutes(15)).ToString("o")}}","sessionId":"{{sessionId}}"}""";

    public static string BatchBody(params (string Id, string Outcome)[] results)
    {
        var rows = results.Select(r =>
            $$"""{"operationId":"{{r.Id}}","outcome":"{{r.Outcome}}","resultRevision":7,"conflict":null}""");
        return $$"""{"results":[{{string.Join(",", rows)}}],"serverTimeUtc":"{{DateTimeOffset.UtcNow.ToString("o")}}"}""";
    }

    public static string ChangesBody(long latest, params string[] entityIds)
    {
        var rows = entityIds.Select(id =>
            "{\"revision\":" + latest + ",\"entityType\":\"goal\",\"entityId\":\"" + id + "\",\"kind\":\"update\",\"payload\":{\"id\":\"" + id + "\"}}");
        return "{\"changes\":[" + string.Join(",", rows) + "],\"latestRevision\":" + latest + ",\"hasMore\":false}";
    }

    /// <summary>
    /// A batch responder that echoes the ACTUAL operationIds it was sent with, each carrying the
    /// given outcome. The transport pairs results to operations by id (§5c), so a script that
    /// invented an id would be testing a mismatch, not a confirmation.
    /// </summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> BatchEcho(string outcome = "applied",
        string? conflictJson = null, long revision = 8)
    {
        return req =>
        {
            var text = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var rows = doc.RootElement.GetProperty("operations").EnumerateArray()
                .Select(o => "{\"operationId\":\"" + o.GetProperty("operationId").GetString() +
                             "\",\"outcome\":\"" + outcome + "\",\"resultRevision\":" +
                             (outcome == "conflict" ? "null" : revision.ToString()) +
                             ",\"conflict\":" + (conflictJson ?? "null") + "}");
            return Response(200, "{\"results\":[" + string.Join(",", rows) + "],\"serverTimeUtc\":\"" +
                                DateTimeOffset.UtcNow.ToString("o") + "\"}");
        };
    }

    /// <summary>The /platform/capabilities body, built by concatenation (raw-brace JSON + interpolation is unreadable).</summary>
    public static string CapabilitiesBody(string identityState = "ok", string syncState = "ok",
        string dbState = "ok", IReadOnlyList<string>? mappingFailures = null)
    {
        var failures = mappingFailures is null || mappingFailures.Count == 0
            ? "[]"
            : "[" + string.Join(",", mappingFailures.Select(f => "\"" + f + "\"")) + "]";
        return "{\"serverTimeUtc\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\",\"apiVersion\":\"v1\",\"modules\":[" +
               "{\"key\":\"identity\",\"state\":\"" + identityState + "\",\"detail\":\"scripted\",\"capabilities\":{\"google_oauth\":\"unconfigured\"}}," +
               "{\"key\":\"sync\",\"state\":\"" + syncState + "\",\"detail\":\"scripted\",\"capabilities\":{}}]," +
               "\"database\":{\"key\":\"database\",\"state\":\"" + dbState + "\",\"detail\":\"scripted\",\"capabilities\":{\"provider\":\"sqlite\"}}," +
               "\"endpointMappingFailures\":" + failures + "}";
    }

    // ---- wiring shortcuts -----------------------------------------------------------------

    /// <summary>Options rooted in a temp dir, configured with a loopback https URL.</summary>
    public static CloudApiOptions Options(TempDir dir, bool configured = true, string url = "https://api.livora.test")
    {
        var o = new CloudApiOptions(dir.Store(CloudApiOptions.CloudDirName));
        if (configured)
        {
            var reject = o.TrySetBaseUrl(url);
            if (reject is not null) throw new InvalidOperationException("script URL rejected: " + reject);
        }
        return o;
    }

    public static CloudTokenStore Tokens(TempDir dir, IPlatformSecureBox? box = null) =>
        new(SecureStore(dir, box));

    public static SyncQueue Queue(TempDir dir, out MetaIndex meta)
    {
        meta = new MetaIndex(dir.PathOf("queue"));
        return new SyncQueue(dir.PathOf("queue"), meta);
    }

    public static CloudConnectorStateStore StateStore(TempDir dir, TimeProvider? time = null) =>
        new(dir.Store(CloudApiOptions.CloudDirName), time);

    /// <summary>A payload source over an in-memory dictionary (the real one reads the local catalog).</summary>
    internal sealed class MapPayloadSource : ICloudSyncPayloadSource
    {
        private readonly Dictionary<string, string?> _byKey = new(StringComparer.Ordinal);
        public List<string> Asked { get; } = new();
        public bool Available { get; set; } = true;

        public string Label => Available ? "test-map" : "none";
        public bool CanProvidePayloads => Available;

        public void Put(string kind, string id, string? json) => _byKey[EntityMeta.KeyOf(kind, id)] = json;

        public Task<string?> GetPayloadAsync(string entityKind, string entityId, CancellationToken ct = default)
        {
            var key = EntityMeta.KeyOf(entityKind, entityId);
            Asked.Add(key);
            return Task.FromResult(_byKey.TryGetValue(key, out var v) ? v : null);
        }
    }

    /// <summary>A session the port can authorize with, without the token store (call counting included).</summary>
    internal sealed class ScriptedAuth : ICloudAuthContext
    {
        private string? _bearer;
        public int RefreshCalls { get; private set; }
        public List<string?> RejectedCodes { get; } = new();
        public Func<bool> RefreshResult { get; set; } = () => true;
        public string? NewBearer { get; set; } = "at-rotated";

        public ScriptedAuth(string? bearer) => _bearer = bearer;

        public bool HasSession => _bearer is not null;
        public event Action? SessionChanged;

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_bearer);

        public Task<bool> TryRefreshAsync(CancellationToken ct = default)
        {
            RefreshCalls++;
            bool ok = RefreshResult();
            if (ok && NewBearer is not null) _bearer = NewBearer;
            if (!ok) _bearer = null;
            SessionChanged?.Invoke();
            return Task.FromResult(ok);
        }

        public void HandleAuthRejected(string? code)
        {
            RejectedCodes.Add(code);
            if (code is LivoraApiCodes.TokenRevoked or LivoraApiCodes.TokenExpired) _bearer = null;
        }
    }

    /// <summary>A fixed clock (so "confirmed at" is assertable to the second).</summary>
    internal sealed class FixedTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A clock that advances one second per read: distinct stamps without sleeping.</summary>
    internal sealed class SteppingTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now = _now.AddSeconds(1);
    }

    // ---- queue helpers --------------------------------------------------------------------

    /// <summary>
    /// One queued entity change with a real canonical-JSON hash, exactly the way the app's write path
    /// produces it: the store bumps the meta row (version+1, Pending) and THEN enqueues the transport
    /// record. The queue alone does not touch meta — asserting meta state requires both halves.
    /// </summary>
    public static SyncEnvelope Enqueue(SyncQueue queue, string kind, string id, long version = 1,
        string? payload = null, MetaIndex? meta = null)
    {
        var canonical = CanonicalJson.Serialize(new { id, kind, n = version, payload = payload ?? "{\"ok\":true}" });
        var envelope = queue.EnqueueAsync(kind, id, version, CanonicalJson.Sha256HexOfCanonical(canonical),
            Encoding.UTF8.GetByteCount(canonical)).GetAwaiter().GetResult();
        if (meta is not null)
            meta.BumpAsync(kind, id).GetAwaiter().GetResult();
        return envelope;
    }

    /// <summary>Repo root, or null when the sources are not next to the test binaries.</summary>
    public static readonly string? RepoRoot = FindRoot();

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LIVORA.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string RepoFile(params string[] parts) =>
        RepoRoot is null ? "" : Path.Combine(new[] { RepoRoot }.Concat(parts).ToArray());

    public static string? ReadRepoFile(params string[] parts)
    {
        var p = RepoFile(parts);
        return string.IsNullOrEmpty(p) || !File.Exists(p) ? null : File.ReadAllText(p);
    }
}
