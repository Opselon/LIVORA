using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Livora.Server.Application;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Livora.Server;

/// <summary>
/// PURPOSE: the ONE place the backend learns how to mint and validate its own access tokens, so the
///          identity lane (Agent 03) and every feature lane share a single signing key and a single
///          policy table. Pre-wired by the integration lead on purpose: feature lanes must not each
///          invent auth wiring, and routes must be protectable from day one.
/// OWNER: Agent 02 / lead (frozen). Amendments are a lead PR.
/// CONSUMES: configuration keys
///   Identity:TokenSigningKey      base64, >= 32 bytes. Absent => ephemeral per-process key.
///   Identity:Issuer               default "livora"
///   Identity:Audience             default "livora"
///   Identity:AccessTokenLifetimeMinutes  default 15
/// PROVIDES:
///   <see cref="LivoraSigningKey"/>  singleton — the shared key material, with an honest
///                                   <see cref="LivoraSigningKey.Ephemeral"/> flag;
///   JWT bearer authentication, the <see cref="Policies.All"/> authorization policies, and
///   <see cref="AccessTokenMint"/> for the identity lane to issue tokens.
/// INVARIANTS:
///   - the host MUST start with no signing key configured. An ephemeral key is a documented dev
///     posture (tokens die with the process, and the capability report says so) — never a secret in
///     source, never a fallback that silently pretends to be production.
///   - claims contract: "livora.uid" = account id, ClaimTypes.Role = roles, "livora.sid" = session id.
///     Feature handlers read the uid through <see cref="LivoraPrincipal"/> only.
///   - validation is strict: issuer, audience, lifetime, and exactly the one signing key. No
///     signature-bypass toggle exists for tests; tests mint with the same key instead.
/// EXTEND: the identity lane injects <see cref="LivoraSigningKey"/> and uses
///   <see cref="AccessTokenMint"/> — it must not construct its own TokenValidationParameters.
/// </summary>
public static class LivoraAuth
{
    public const string UidClaim = "livora.uid";
    public const string SessionClaim = "livora.sid";
    public const string DefaultIssuer = "livora";
    public const string DefaultAudience = "livora";
    public const string ConfigKeySection = "Identity";

    public static IServiceCollection AddLivoraAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var (key, ephemeral) = ResolveKey(configuration);
        var issuer = configuration[$"{ConfigKeySection}:Issuer"] ?? DefaultIssuer;
        var audience = configuration[$"{ConfigKeySection}:Audience"] ?? DefaultAudience;

        services.AddSingleton(new LivoraSigningKey(key, issuer, audience, ephemeral));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = !string.Equals(
                    configuration[$"{ConfigKeySection}:AllowInsecureHttp"], "true", StringComparison.OrdinalIgnoreCase);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    // No replay/revocation is possible from the token alone: revocation lives in the
                    // auth_sessions table and is enforced by the identity lane per request.
                    SaveSigninToken = false,
                };
                options.MapInboundClaims = false; // keep claim names exactly as minted
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.SignedIn, p => p.RequireAuthenticatedUser())
            // Ownership is NOT a token property: the handler must compare the row's owner id.
            .AddPolicy(Policies.OwnsResource, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Creator, p => p.RequireAuthenticatedUser().RequireRole(Roles.Creator))
            .AddPolicy(Policies.Entitled, p => p.RequireAuthenticatedUser())
            .AddPolicy(Policies.Moderator, p => p.RequireAuthenticatedUser().RequireRole(Roles.Moderator))
            .AddPolicy(Policies.Admin, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        return services;
    }

    private static (byte[] Key, bool Ephemeral) ResolveKey(IConfiguration configuration)
    {
        var configured = configuration[$"{ConfigKeySection}:TokenSigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            byte[] material;
            try { material = Convert.FromBase64String(configured.Trim()); }
            catch (FormatException)
            {
                throw new InvalidOperationException(
                    "Identity:TokenSigningKey must be base64 of at least 32 random bytes " +
                    "(generate: openssl rand -base64 48 or Convert.ToBase64String([RandomNumberGenerator]::GetBytes(48))).");
            }
            if (material.Length < 32)
                throw new InvalidOperationException(
                    $"Identity:TokenSigningKey is too short ({material.Length} bytes); HS256 needs >= 32.");
            return (material, false);
        }

        // Ephemeral: honest dev mode. Logged once, and reported by the identity capability.
        return (RandomNumberGenerator.GetBytes(48), true);
    }
}

/// <summary>
/// PURPOSE: turns the framework's bare 401/403 into the shared problem envelope, so an anonymous or
///          under-privileged caller gets the same machine-readable shape as every other failure
///          (Wave 4 §27: consistent errors). Without this, auth failures return an empty body and
///          clients start guessing from status codes alone.
/// OWNER: lead (frozen). Registered in Program.cs right after <c>UseAuthorization</c>.
/// INVARIANTS: only an UNWRITTEN 401/403 is converted (a handler that answered its own problem body
///             wins); no challenge detail beyond the code is emitted — the reason goes to the log.
/// </summary>
public sealed class AuthEnvelopeMiddleware
{
    private readonly RequestDelegate _next;

    public AuthEnvelopeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        await _next(ctx);

        if (ctx.Response.StatusCode is not (StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden))
            return;
        if (ctx.Response.HasStarted)
            return;

        var code = ctx.Response.StatusCode == StatusCodes.Status401Unauthorized
            ? ProblemCodes.Unauthenticated
            : ProblemCodes.Forbidden;

        ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("livora.auth")
            .LogDebug("auth envelope emitted for {Path}: {Code}", ctx.Request.Path.Value, code);

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new ApiProblem(
            Type: $"https://livora.app/problems/{code}",
            Title: code,
            Status: ctx.Response.StatusCode,
            Detail: code == ProblemCodes.Unauthenticated
                ? "A valid access token is required for this endpoint."
                : "This account is not permitted to perform this action.",
            Instance: ctx.Request.Path.Value ?? "/",
            Code: code,
            CorrelationId: ctx.GetCorrelationId()), Problems.Json));
    }
}

/// <summary>The process signing key + token settings. Injected; never reconstructed elsewhere.</summary>
public sealed record LivoraSigningKey(byte[] Key, string Issuer, string Audience, bool Ephemeral)
{
    public SigningCredentials Credentials { get; } =
        new(new SymmetricSecurityKey(Key), SecurityAlgorithms.HmacSha256);
}

/// <summary>
/// PURPOSE: mint an access token. Used by the identity lane's login/refresh endpoints and by tests.
/// OWNER: lead (frozen). Kept tiny on purpose — the refresh-token lifecycle, storage and revocation
///        are the identity lane's job; this only produces the short-lived bearer artifact.
/// INVARIANTS: lifetime defaults to <see cref="DefaultLifetimeMinutes"/>; the returned string is a
///             JWT whose claims are exactly the ones the contract promises.
/// </summary>
public static class AccessTokenMint
{
    public const int DefaultLifetimeMinutes = 15;

    public static string Create(
        LivoraSigningKey key,
        string userId,
        string sessionId,
        IEnumerable<string> roles,
        int lifetimeMinutes = DefaultLifetimeMinutes,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity([
            new Claim(LivoraAuth.UidClaim, userId),
            new Claim(LivoraAuth.SessionClaim, sessionId),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            ..roles.Select(r => new Claim(ClaimTypes.Role, r)),
        ]);

        var token = new JwtSecurityToken(
            issuer: key.Issuer,
            audience: key.Audience,
            claims: identity.Claims,
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(Math.Max(1, lifetimeMinutes)).UtcDateTime,
            signingCredentials: key.Credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>
/// PURPOSE: the only sanctioned way a handler reads the caller's identity, so no lane parses
///          ClaimsPrincipal by hand and gets the claim type wrong.
/// OWNER: lead (frozen).
/// </summary>
public static class LivoraPrincipal
{
    public static string? UserId(this ClaimsPrincipal? user)
        => user?.FindFirstValue(LivoraAuth.UidClaim);

    public static string? SessionId(this ClaimsPrincipal? user)
        => user?.FindFirstValue(LivoraAuth.SessionClaim);

    public static IReadOnlyList<string> RolesOf(this ClaimsPrincipal? user)
        => user?.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray() ?? [];

    /// <summary>True when the caller carries an admin/moderator role — capability checks go here,
    /// not on a client-sent flag (Wave 4 §54).</summary>
    public static bool IsStaff(this ClaimsPrincipal? user)
    {
        var roles = user.RolesOf();
        return roles.Contains(Roles.Admin) || roles.Contains(Roles.Moderator);
    }
}
