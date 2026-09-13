using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.IntelligenceProviders.Wave3c;

/// <summary>
/// Chooses the effective AI chat provider (Wave 3c lane 02). Ordering is a trust ladder:
/// the REAL endpoint first (when consent + enabled + configured all hold), the Sample/mock
/// provider LAST so it is always the honest floor. PickEffective NEVER throws: any gate that
/// cannot be evaluated (missing service, throwing config, revoked consent) means "not
/// effective" and simply skips that provider. Null return = rules only, which callers must
/// label as such. MAUI-free by construction (interfaces + the sample provider only).
/// </summary>
public sealed class ProviderRegistry : IIntelligenceProviderRegistry
{
    private readonly IConsentService? _consent;
    private readonly IGatewayConfigService? _gateway;

    public ProviderRegistry(
        IReadOnlyList<IIntelligenceChatProvider> providers,
        IConsentService? consent = null,
        IGatewayConfigService? gateway = null)
    {
        // Sample/mock goes last no matter how the caller ordered the list — the ladder is the
        // invariant, not a convention.
        Providers = providers
            .Where(p => p is not null)
            .OrderBy(p => IsMock(p!) ? 1 : 0)
            .ThenBy(_ => 0)   // stable within each class (OrderBy is stable in LINQ)
            .ToList();
        _consent = consent;
        _gateway = gateway;
    }

    public IReadOnlyList<IIntelligenceChatProvider> Providers { get; }

    /// <summary>First provider whose gates pass, or null = rules only. Never throws.</summary>
    public IIntelligenceChatProvider? PickEffective()
    {
        bool? gatesOpen = null;   // evaluated once per call (consent + enabled are global)
        foreach (var p in Providers)
        {
            try
            {
                // Mock providers need no remote gates — they are the floor — but they also never
                // count as "the effective AI": a registry that only holds the mock returns it so
                // callers can still phrase via it; real providers must pass every gate.
                if (IsMock(p)) return p;

                gatesOpen ??= GatesOpen();
                if (gatesOpen == true && p.IsConfigured) return p;
            }
            catch { /* a broken provider is a skipped provider — never a crash */ }
        }
        return null;
    }

    private bool GatesOpen()
    {
        try
        {
            if (_consent is not null &&
                _consent.Get(ConsentCategory.AiProcessing) != ConsentDecision.Granted) return false;
            if (_gateway is not null)
            {
                // Sync-over-async on a cached, non-blocking config read; any deadlock risk is
                // bounded by the timeout on the caller side. A throwing config service = closed.
                var cfg = Task.Run(() => _gateway.GetEffectiveAsync()).GetAwaiter().GetResult();
                if (cfg is null || !cfg.Enabled) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool IsMock(IIntelligenceChatProvider p) => p.Kind == AiProviderKind.Mock;
}

/// <summary>
/// Chat-provider facade over the deterministic SampleIntelligenceProvider so the registry's
/// ladder can be expressed in one type universe. It never touches the network; CompleteStructured
/// honestly returns null (a mock has no free-form completion) and IsConfigured is false — it is
/// reachable ONLY as the registry's last resort marker, never as an AI source.
/// </summary>
public sealed class MockChatProvider : IIntelligenceChatProvider
{
    public static MockChatProvider Instance { get; } = new();
    public bool IsConfigured => false;
    public AiProviderKind Kind => AiProviderKind.Mock;
    public string ProviderLabel => "AI: mock";
    public Task<string?> CompleteStructuredAsync(
        string systemPrompt, string userJson, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
