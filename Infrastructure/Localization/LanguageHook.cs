namespace LIVORA.Infrastructure.Localization;

/// <summary>
/// Central hook so long-lived localized objects (markup extensions etc.) can react to language
/// changes without holding a service reference at construction time.
///
/// Subscribers are held WEAKLY on purpose: a XAML markup extension is created once per inflated
/// element, so a strong list here would pin every label the app ever rendered — a slow leak that
/// grows with navigation. An extension instance is kept alive by the binding it installs on its
/// target; when the page goes away, the weak entry simply prunes itself on the next notify.
/// </summary>
public static class LanguageHook
{
    private static readonly List<WeakReference<Action>> Subscribers = new();
    private static int _sincePrune;

    public static void Subscribe(Action onLanguageChanged)
    {
        lock (Subscribers)
        {
            Subscribers.Add(new WeakReference<Action>(onLanguageChanged));
            // Prune amortized: cheap on the common path, bounded memory over long sessions.
            if (++_sincePrune >= 32) Prune();
        }
    }

    /// <summary>Called by LocalizationService when the language flips.</summary>
    public static void NotifyLanguageChanged()
    {
        Action[] live;
        lock (Subscribers)
        {
            if (++_sincePrune >= 32) Prune();
            live = Subscribers
                .Where(w => w.TryGetTarget(out _))
                .Select(w => { w.TryGetTarget(out var t); return t!; })
                .ToArray();
        }
        // Invoked outside the lock: a handler may (re)subscribe, and the lock must not be held
        // while arbitrary UI code runs.
        foreach (var s in live) s();
    }

    /// <summary>Drop entries whose target has been collected. Caller holds the lock.</summary>
    private static void Prune()
    {
        _sincePrune = 0;
        Subscribers.RemoveAll(w => !w.TryGetTarget(out _));
    }
}
