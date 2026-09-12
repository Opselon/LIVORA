namespace LIVORA.Infrastructure.Localization;

/// <summary>
/// Central hook so long-lived localized objects (markup extensions etc.) can subscribe to
/// language changes without holding a service reference at construction time.
/// </summary>
public static class LanguageHook
{
    private static readonly List<Action> Subscribers = new();

    public static void Subscribe(Action onLanguageChanged)
    {
        lock (Subscribers) Subscribers.Add(onLanguageChanged);
    }

    /// <summary>Called by LocalizationService when the language flips.</summary>
    public static void NotifyLanguageChanged()
    {
        lock (Subscribers)
        {
            foreach (var s in Subscribers) s();
        }
    }
}
