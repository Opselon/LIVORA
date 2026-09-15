using System.Text.Json;
using Livora.Server.Application;
using Microsoft.AspNetCore.Diagnostics;

namespace Livora.Server;

/// <summary>
/// PURPOSE: the shared error surface — one envelope and one machine code for handled problems AND
///          for unhandled exceptions, so nothing reaches the client as HTML or stack text.
/// OWNER: Agent 02. Lanes answer errors through <see cref="Problems.Of"/>; they do not hand-roll
///        JSON error bodies.
/// INVARIANTS:
///   - an unhandled exception becomes 500 + code "internal_error" + correlation id, and the real
///     reason goes only to the log (product law I: no secret/personal leakage in responses).
///   - a status code is always non-2xx on a problem; there is no "200 with error body" anywhere.
///   - the code comes from <see cref="ProblemCodes"/> constants, never a literal, so the client's
///     localisation table cannot drift.
/// </summary>
public static class Problems
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Stable status per code. Unlisted codes default to 400 — never to 200.</summary>
    public static int StatusFor(string code) => code switch
    {
        ProblemCodes.NotFound => 404,
        ProblemCodes.Conflict or ProblemCodes.VersionConflict or ProblemCodes.PurchaseAlreadyExists
            or ProblemCodes.IdempotencyKeyReuseMismatch or ProblemCodes.ReportAlreadyOpen
            or ProblemCodes.EmailAlreadyRegistered => 409,
        ProblemCodes.Unauthenticated or ProblemCodes.InvalidCredentials or ProblemCodes.TokenExpired
            or ProblemCodes.TokenRevoked or ProblemCodes.RecoveryTokenInvalid => 401,
        ProblemCodes.Forbidden or ProblemCodes.EntitlementRequired or ProblemCodes.CreatorNotApproved
            or ProblemCodes.BlockedByParticipant or ProblemCodes.ContentRemoved
            or ProblemCodes.DeletionPending or ProblemCodes.AccountLocked => 403,
        ProblemCodes.RateLimited => 429,
        ProblemCodes.PermissionRequired => 428,
        ProblemCodes.ProviderUnconfigured or ProblemCodes.PaymentProviderUnconfigured
            or ProblemCodes.AiUnavailable or ProblemCodes.ProviderUnavailable
            or ProblemCodes.VerificationRuleUnknown => 503,
        ProblemCodes.InternalError => 500,
        _ => 400,
    };

    /// <summary>Build the envelope for a handler to return.</summary>
    public static IResult Of(
        HttpContext ctx,
        string code,
        string detail,
        int? status = null,
        IReadOnlyDictionary<string, string[]>? fieldErrors = null)
    {
        var s = status ?? StatusFor(code);
        ctx.Response.StatusCode = s;
        return Results.Json(new ApiProblem(
            Type: $"https://livora.app/problems/{code}",
            Title: code,
            Status: s,
            Detail: detail,
            Instance: ctx.Request.Path.Value ?? "/",
            Code: code,
            CorrelationId: ctx.GetCorrelationId(),
            Errors: fieldErrors), Json);
    }

    /// <summary>Register the catch-all handler. Must run before endpoint routing.</summary>
    public static IApplicationBuilder UseFlivoraProblemDetails(this IApplicationBuilder app)
    {
        return app.UseExceptionHandler(handler => handler.Run(async ctx =>
        {
            var feature = ctx.Features.Get<IExceptionHandlerFeature>();
            ctx.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("livora.error")
                .LogError(feature?.Error, "unhandled exception on {Path}", ctx.Request.Path.Value);

            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiProblem(
                Type: "https://livora.app/problems/internal_error",
                Title: "internal_error",
                Status: 500,
                Detail: "The request could not be completed. Quote this correlation id if it persists.",
                Instance: ctx.Request.Path.Value ?? "/",
                Code: ProblemCodes.InternalError,
                CorrelationId: ctx.GetCorrelationId()), Json));
        }));
    }

    public static IApplicationBuilder UseFlivoraCorrelation(this IApplicationBuilder app)
        => app.UseMiddleware<CorrelationMiddleware>();
}
