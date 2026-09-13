using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LIVORA.Application.Sync;

/// <summary>
/// Wave 3c (lane 06): the ONE canonical JSON grammar the sync layer hashes and stores with.
///
/// Why it exists: a payload hash is only useful if two semantically identical writes produce the
/// same bytes. The app-wide <c>JsonFileStore</c> options use System.Text.Json's *declaration
/// order*, which changes the moment a lane reorders properties in a model — every stored hash
/// would silently stop matching. Here object properties serialize sorted by name (stable across
/// refactors) and <see cref="Sha256HexOfCanonical"/> re-walks any already-serialized JSON with
/// sorted keys before hashing, so a hash depends on content only — never on byte order or on
/// declaration order of the producing model.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Indented + string enums + null-ignoring + name-sorted properties: the stable on-disk shape.</summary>
    public static JsonSerializerOptions Options { get; } = Build(indented: true);

    /// <summary>The same grammar with no significant whitespace — where bytes must be minimal + deterministic.</summary>
    public static JsonSerializerOptions CompactOptions { get; } = Build(indented: false);

    private static JsonSerializerOptions Build(bool indented)
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            // Readable UTF-8 for user text (Persian notes) — hashes normalize anyway.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { SortPropertiesByName },
            },
        };
        opts.MakeReadOnly();
        return opts;
    }

    private static void SortPropertiesByName(JsonTypeInfo ti)
    {
        if (ti.Kind != JsonTypeInfoKind.Object) return;
        // Ordinal-name sort: deterministic across property reordering in the model. The resolver
        // runs the same way for serialize and deserialize, so round-tripping our own files holds.
        JsonPropertySort.SortByName(ti);
    }

    /// <summary>Serialize an object in the canonical (compact, sorted) grammar.</summary>
    public static string Serialize(object payload) =>
        JsonSerializer.Serialize(payload, payload.GetType(), CompactOptions);

    /// <summary>SHA-256 (lowercase hex) of the canonical form of an arbitrary object.</summary>
    public static string Sha256Hex(object payload) => Sha256HexOfCanonical(Serialize(payload));

    /// <summary>
    /// SHA-256 (lowercase hex) of <paramref name="json"/> in canonical form (parsed, keys sorted,
    /// whitespace removed). Input that is not valid JSON is hashed over its raw UTF-8 so callers
    /// never throw on a garbage payload — the hash stays a stable content fingerprint.
    /// </summary>
    public static string Sha256HexOfCanonical(string json)
    {
        string canonical;
        try
        {
            using var doc = JsonDocument.Parse(json ?? string.Empty);
            var sb = new StringBuilder(256);
            WriteCanonical(doc.RootElement, sb);
            canonical = sb.ToString();
        }
        catch (JsonException)
        {
            canonical = json ?? string.Empty;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement el, StringBuilder sb)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                bool first = true;
                foreach (var p in el.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(Escape(p.Name)).Append("\":");
                    WriteCanonical(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                bool af = true;
                foreach (var item in el.EnumerateArray())
                {
                    if (!af) sb.Append(',');
                    af = false;
                    WriteCanonical(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.String:
                sb.Append('"').Append(Escape(el.GetString() ?? string.Empty)).Append('"');
                break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            default:
                sb.Append(el.GetRawText()); // numbers keep their raw text — round-trip exact
                break;
        }
    }

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c); // non-ASCII stays literal UTF-8; hashing is over UTF-8 bytes
                    break;
            }
        }
        return sb.ToString();
    }
}
