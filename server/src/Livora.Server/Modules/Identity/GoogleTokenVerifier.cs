using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Livora.Server.Modules.Identity;

/// <summary>
/// PURPOSE: verify a Google ID token for real: RS256 signature against Google's published JWKS,
///          issuer, audience (our ClientId), expiry — then hand the validated claims to
///          <see cref="GoogleClaimMapper"/>. This is the code that flips to REAL the moment a
///          client_id is configured; until then the endpoint never calls it (503 provider_unconfigured).
/// OWNER: Agent 03 (identity lane).
/// CONSUMES: Identity:Google:ClientId, Identity:Google:MetadataAddress (via IdentityOptions);
///           Microsoft.IdentityModel 8.x assemblies (already in the closure via JwtBearer —
///           verified by resolving the host project.assets.json; NO new package reference).
/// PROVIDES: <see cref="IGoogleIdTokenVerifier"/> + <see cref="GoogleTokenVerifier"/>.
/// INVARIANTS:
///   - unconfigured ⇒ <see cref="GoogleVerifyStatus.Unconfigured"/> — never a silent accept
///   - the IDocumentRetriever is injectable ONLY so tests serve a fake discovery document + JWKS
///     from a local RSA key offline (the validation crypto still runs — the fake replaces Google's
///     NETWORK, not Google's CHECKS). Production wires the real HTTPS retriever; threat model T-08
///     records that MetadataAddress is trusted ops config, same class as a connection string.
///   - RS256-only allowlist: HS256-forgery (key-type mismatch), alg=none, and payload tampering all
///     fail — each is covered by a test with a real locally-generated token.
///   - a metadata fetch failure is Unavailable ⇒ 503: auth never fails OPEN.
///   - the raw id_token is never logged, echoed, or stored; failure detail is an exception TYPE name.
/// VERIFIED BY EXECUTION: probe run 2026-09-14 — good=VALID, bad-aud/bad-iss/expired/HS256-forged/
///   alg-none/tampered = INVALID (see lane report); the same matrix now exists as xunit tests.
/// </summary>
public enum GoogleVerifyStatus { Ok, Invalid, Unavailable, Unconfigured }

/// <param name="Status">Outcome of the cryptographic + claim validation.</param>
/// <param name="Claims">On Ok: the token payload as strings (sub, email, email_verified, name, locale…).</param>
/// <param name="Detail">Ops-facing failure reason — a class/enum name only, never token content.</param>
public sealed record GoogleVerifyOutcome(
    GoogleVerifyStatus Status,
    IReadOnlyDictionary<string, string?>? Claims = null,
    string? Detail = null);

public interface IGoogleIdTokenVerifier
{
    bool IsConfigured { get; }
    Task<GoogleVerifyOutcome> VerifyAsync(string? idToken, CancellationToken ct);
}

public sealed class GoogleTokenVerifier : IGoogleIdTokenVerifier
{
    /// <summary>Google's documented ID-token issuer. Hardcoded: an attacker who controls config to
    /// point at their own JWKS would still not mint tokens with THIS issuer + our audience without
    /// their own signing domain; pinning removes one misconfiguration vector entirely.</summary>
    public const string ExpectedIssuer = "https://accounts.google.com";
    public const int MaxTokenChars = 8192;

    private readonly string _clientId;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>Production construction: discovery document + JWKS fetched over HTTPS with
    /// IdentityModel's cached, auto-refreshing configuration manager.</summary>
    public GoogleTokenVerifier(string clientId, string metadataAddress)
        : this(clientId, metadataAddress, new HttpDocumentRetriever())
    {
    }

    /// <summary>Test seam: an alternative <see cref="IDocumentRetriever"/> serving a fake discovery
    /// document + JWKS (local RSA key) so the FULL signature/iss/aud/exp path runs with no network.</summary>
    public GoogleTokenVerifier(string clientId, string metadataAddress, IDocumentRetriever retriever)
    {
        _clientId = clientId.Trim();
        _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            metadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            retriever)
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(12),
            RefreshInterval = TimeSpan.FromMinutes(5),
        };
    }

    /// <summary>Audience is pinned to OUR client id: a Google token minted for any other app must
    /// not log into LIVORA (token-substitution, threat model T-08).</summary>
    public bool IsConfigured => _clientId.Length > 0;

    public async Task<GoogleVerifyOutcome> VerifyAsync(string? idToken, CancellationToken ct)
    {
        if (!IsConfigured)
            return new GoogleVerifyOutcome(GoogleVerifyStatus.Unconfigured, null, "Identity:Google:ClientId is empty");
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > MaxTokenChars)
            return new GoogleVerifyOutcome(GoogleVerifyStatus.Invalid, null, "malformed id_token");

        OpenIdConnectConfiguration config;
        try
        {
            config = await _configuration.GetConfigurationAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GoogleVerifyOutcome(GoogleVerifyStatus.Unavailable, null,
                $"metadata fetch failed: {ex.GetType().Name}");
        }

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = ExpectedIssuer,
            ValidateAudience = true,
            ValidAudience = _clientId,
            RequireAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2), // Google's documented tolerance
            RequireSignedTokens = true,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = ["RS256"], // Google's only ID-token alg; blocks HS256/alg-none classes
            SaveSigninToken = false,
        };

        try
        {
            var res = await _handler.ValidateTokenAsync(idToken, parameters);
            if (!res.IsValid)
                return new GoogleVerifyOutcome(GoogleVerifyStatus.Invalid, null,
                    res.Exception?.GetType().Name ?? "invalid");

            var claims = res.Claims.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value?.ToString(),
                StringComparer.Ordinal);
            return new GoogleVerifyOutcome(GoogleVerifyStatus.Ok, claims);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new GoogleVerifyOutcome(GoogleVerifyStatus.Invalid, null, ex.GetType().Name);
        }
    }

    /// <summary>The real HTTPS document fetcher. Short timeout; a hung Google endpoint must
    /// surface as Unavailable (503), never as an open request pool.</summary>
    private sealed class HttpDocumentRetriever : IDocumentRetriever
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

        public Task<string> GetDocumentAsync(string address, CancellationToken cancel)
            => Http.GetStringAsync(address, cancel);
    }
}
