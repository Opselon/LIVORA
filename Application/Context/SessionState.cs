using LIVORA.Domain.Models;

namespace LIVORA.Application.Context;
/// <summary>Holds the active user profile for the session. Loaded once at startup.</summary>
public sealed class SessionState
{
    public UserProfile CurrentProfile { get; set; } = new();
}
