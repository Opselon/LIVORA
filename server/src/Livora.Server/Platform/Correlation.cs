using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Livora.Server;

/// <summary>
/// PURPOSE: one ActivitySource for the whole server so traces and request metrics share a stable
///          name that tests and dashboards can filter on.
/// OWNER: Agent 02. Lanes read it; nobody renames it.
/// INVARIANTS: span names are lowercase dot-separated, and no span may carry personal data
///             (raw health values, tokens, payment data) as a tag — only identifiers and counts.
/// </summary>
public static class FlivoraActivity
{
    public const string SourceName = "livora.server";
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}

/// <summary>
/// PURPOSE: correlation-id plumbing. An incoming <c>X-Correlation-Id</c> is honoured when it is
///          safe, otherwise a new one is minted; it always goes back out on the response.
/// OWNER: Agent 02.
/// INVARIANTS:
///   - accepted values: 1..64 chars of [A-Za-z0-9-_]; anything else is replaced, so a client can
///     never forge log lines or split headers with its own id.
///   - the id is available to every handler via <see cref="FlivoraCorrelation.GetCorrelationId"/>
///     and appears in every problem envelope and log scope.
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = Sanitise(ctx.Request.Headers[HeaderName].ToString());
        ctx.Items[FlivoraCorrelation.ItemKey] = id;
        ctx.Response.Headers[HeaderName] = id;

        using var activity = FlivoraActivity.Source.StartActivity("http.request", ActivityKind.Server);
        activity?.SetTag("correlation_id", id);
        activity?.SetTag("http.request.method", ctx.Request.Method);
        activity?.SetTag("url.path", ctx.Request.Path.Value);

        var log = ctx.RequestServices.GetRequiredService<ILogger<CorrelationMiddleware>>();
        using (log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id }))
        {
            await _next(ctx);
        }

        activity?.SetTag("http.response.status_code", ctx.Response.StatusCode);
    }

    private static string Sanitise(string? incoming)
        => !string.IsNullOrWhiteSpace(incoming)
           && incoming.Length <= 64
           && incoming.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.NewGuid().ToString("N");
}

/// <summary>Accessors for the correlation id, shared by handlers and the problem builder.</summary>
public static class FlivoraCorrelation
{
    /// <summary>HttpContext item key holding the per-request correlation id.</summary>
    internal const string ItemKey = "livora.correlation_id";

    /// <summary>The id minted/validated by <see cref="CorrelationMiddleware"/>; "unknown" only if
    /// the middleware was skipped (unit tests that call a handler directly).</summary>
    public static string GetCorrelationId(this HttpContext ctx)
        => ctx.Items[ItemKey] as string ?? "unknown";
}
