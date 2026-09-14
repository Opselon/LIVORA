namespace Livora.Server.Infrastructure.Identity;

/// <summary>
/// PURPOSE: injectable wall clock. Every security decision in this lane (lockout windows, session
///          and token expiry, deletion scheduling) reads "now" through this seam — never directly
///          from DateTime.UtcNow — so expiry behaviour is testable without sleeping.
/// OWNER: Agent 03 (identity lane).
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Test double: a mutable clock. Labelled fake — production wires SystemClock only.</summary>
public sealed class FixedClock : IClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
