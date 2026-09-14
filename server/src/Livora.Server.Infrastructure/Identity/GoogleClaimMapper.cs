namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: the pure, framework-free half of the Google sign-in path — turn ALREADY-CRYPTographically-
///          validated ID-token claims into the account anchor the rest of the system understands.
///          The crypto lives in <c>GoogleTokenVerifier</c> (host side); this mapping is testable with a
///          hand-built claim dictionary and a fake RSA key, which is exactly how it is proven.
/// OWNER: Agent 03 (identity lane).
/// INVARIANTS (mirrors Google's documented ID-token contract):
///   - <c>sub</c> is the identity. No sub ⇒ no identity, reject — email is never an anchor by itself.
///   - <c>sub</c> must fit the unique 200-char column; over-long is rejected rather than truncated.
///   - an <c>email</c> claim is honoured ONLY when <c>email_verified == "true"</c>. A present-but-
///     unverified email is dropped to null: the account links on sub, and auto-linking an
///     unverified address is the classic account-takeover bridge.
///   - nothing else in the token is trusted into storage: locale/name are bounded and sanitised.
/// </summary>
public static class GoogleClaimMapper
{
    public const int MaxSubjectLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxNameLength = 120;

    public static GoogleMapResult Map(IReadOnlyDictionary<string, string?> claims)
    {
        var sub = claims.GetValueOrDefault("sub");
        if (string.IsNullOrWhiteSpace(sub))
            return GoogleMapResult.Reject(GoogleMapFailure.MissingSubject);
        sub = sub.Trim();
        if (sub.Length > MaxSubjectLength)
            return GoogleMapResult.Reject(GoogleMapFailure.SubjectTooLong);

        var email = claims.GetValueOrDefault("email")?.Trim();
        var emailVerifiedRaw = claims.GetValueOrDefault("email_verified");
        var emailVerified = string.Equals(emailVerifiedRaw, "true", StringComparison.OrdinalIgnoreCase);
        if (email is { Length: > 0 } && !emailVerified)
            email = null; // unverified ⇒ the claim does not exist for our purposes
        if (email is { Length: > MaxEmailLength })
            email = null;
        // Google sends email_verified as a JSON bool; the handler stringifies it to "True"/"False".
        var emailVerifiedFinal = emailVerified && !string.IsNullOrEmpty(email);

        var name = Sanitise(claims.GetValueOrDefault("name"), MaxNameLength);
        if (string.IsNullOrEmpty(name))
            name = Sanitise(claims.GetValueOrDefault("given_name"), MaxNameLength);
        var locale = Sanitise(claims.GetValueOrDefault("locale"), 10);

        return GoogleMapResult.Ok(new GoogleIdentity(sub, email, emailVerifiedFinal, name, locale));
    }

    private static string? Sanitise(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        // strip control characters; keep everything else (names are multilingual by design)
        var cleaned = new string(trimmed.Where(c => !char.IsControl(c)).ToArray());
        if (cleaned.Length == 0) return null;
        return cleaned.Length > max ? cleaned[..max] : cleaned;
    }
}

/// <summary>The only Google identity facts the server persists. Deliberately tiny.</summary>
public sealed record GoogleIdentity(
    string Subject, string? Email, bool EmailVerified, string? DisplayName, string? Locale);

public enum GoogleMapFailure { None = 0, MissingSubject, SubjectTooLong }

public sealed record GoogleMapResult(bool Accepted, GoogleIdentity? Identity, GoogleMapFailure Failure)
{
    public static GoogleMapResult Ok(GoogleIdentity identity) => new(true, identity, GoogleMapFailure.None);
    public static GoogleMapResult Reject(GoogleMapFailure failure) => new(false, null, failure);
}
