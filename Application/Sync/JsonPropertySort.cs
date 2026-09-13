namespace LIVORA.Application.Sync;

/// <summary>
/// Wave 3c (lane 06): the property-ordering primitive behind every deterministic JSON grammar in
/// the sync/storage stack (<see cref="CanonicalJson"/>, <c>MetaIndex</c>, <c>JsonFileStoreV2</c>).
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo.Properties"/> is an
/// <c>IList</c>, so it exposes no in-place <c>Sort(Comparison&lt;T&gt;)</c> — this copies, sorts by
/// ordinal name (stable), and rebuilds the list in place. One tiny helper so the three grammars
/// cannot drift apart on ordering rules.
/// </summary>
internal static class JsonPropertySort
{
    /// <summary>Sort an object contract's properties by ordinal name. Non-object types: no-op.</summary>
    internal static void SortByName(System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object) return;
        var props = typeInfo.Properties;
        if (props.Count < 2) return;
        var ordered = new System.Collections.Generic.List<System.Text.Json.Serialization.Metadata.JsonPropertyInfo>(props);
        ordered.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        props.Clear();
        foreach (var p in ordered) props.Add(p);
    }
}
