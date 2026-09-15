using LIVORA.Application.Cloud;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 DI glue: the late-bound <see cref="ICloudAuthContext"/> hop that breaks the
/// port ⇄ session-manager construction cycle. <see cref="LivoraApiPort"/> needs an auth context to
/// attach bearers and drive 401 refresh; <see cref="CloudSessionManager"/> needs the port to call
/// /auth/*. Both are singletons, so the composition root wires the port with THIS object and each
/// member resolves the real session manager lazily, on first network use — never during
/// construction. The seam tests build the cycle by hand (Wave4SeamHarness); this is the only place
/// the app defers.
///
/// Null-target behaviour is the honest one: no session, no bearer, refresh false. A missing hop
/// must read as "signed out", never as an error the port could retry.
/// </summary>
public sealed class DeferredCloudAuthContext : ICloudAuthContext
{
    private readonly Func<ICloudAuthContext?> _resolve;
    private readonly object _gate = new();
    private ICloudAuthContext? _target;

    public DeferredCloudAuthContext(Func<ICloudAuthContext?> resolve)
        => _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));

    private ICloudAuthContext? Target
    {
        get
        {
            if (_target is not null) return _target;
            lock (_gate) { _target ??= _resolve(); return _target; }
        }
    }

    public bool HasSession => Target?.HasSession ?? false;

    public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        => Target?.GetAccessTokenAsync(ct) ?? Task.FromResult<string?>(null);

    public Task<bool> TryRefreshAsync(CancellationToken ct = default)
        => Target?.TryRefreshAsync(ct) ?? Task.FromResult(false);

    public void HandleAuthRejected(string? code)
    {
        try { Target?.HandleAuthRejected(code); }
        catch { /* rejection handling never throws outward (port contract); a failed hop = still unauthenticated */ }
    }

    public event Action? SessionChanged
    {
        add
        {
            var t = Target;
            if (t is not null) t.SessionChanged += value;
        }
        remove
        {
            var t = Target;
            if (t is not null) t.SessionChanged -= value;
        }
    }
}
