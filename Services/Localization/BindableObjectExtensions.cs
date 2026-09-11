namespace LIVORA.Services.Localization;

/// <summary>
/// Runtime-managed extension properties for bindable objects (e.g. per-language FontFamily on Labels).
/// Backed by conditional weak tables, so nothing leaks.
/// </summary>
public static class BindableObjectExtensions
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<BindableObject, Dictionary<string, object>> Props = new();

    public static void SetExtensionValue(this BindableObject obj, string key, object value)
    {
        var dict = Props.GetOrAdd(obj, _ => new Dictionary<string, object>());
        lock (dict) dict[key] = value;
    }

    public static T? GetExtensionValue<T>(this BindableObject obj, string key) where T : class
    {
        if (!Props.TryGetValue(obj, out var dict)) return null;
        lock (dict)
        {
            return dict.TryGetValue(key, out var v) && v is T t ? t : null;
        }
    }
}
