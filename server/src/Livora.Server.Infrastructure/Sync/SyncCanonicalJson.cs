using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Livora.Server.Infrastructure.Sync;

/// <summary>
/// PURPOSE: make a sync request body comparable to itself. A client that retries an offline batch
///          byte-for-byte, and one that reserialises the same logical request with different
///          whitespace or different property order, MUST produce the same hash — otherwise a benign
///          retry would look like an idempotency-key reuse and answer 409.
/// OWNER: Agent 02 (platform/sync lane).
/// CONSUMES: raw JSON text from the request body.
/// PROVIDES: <see cref="Canonicalize(JsonElement)"/> (stable text) and <see cref="Sha256Hex"/>.
/// INVARIANTS:
///   - object properties are emitted sorted by name; arrays keep their order (order is meaningful
///     in a sync batch: it is the client's intended apply order)
///   - output is ASCII-only (the default encoder escapes non-ASCII), so character length equals
///     UTF-8 byte length and size limits cannot be argued about
///   - the function is pure and deterministic: same logical JSON in, same text out, always
/// </summary>
public static class SyncCanonicalJson
{
    /// <summary>Shared serializer for everything the sync lane writes: camelCase, and no
    /// indented/pretty text so stored payloads and hashes stay compact.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Stable text for a JSON element (numbers keep their literal form, which is what the
    /// client sent — re-formatting a number through double would be a lie about precision).</summary>
    public static string Canonicalize(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => CanonicalObject(element),
        JsonValueKind.Array => CanonicalArray(element),
        _ => element.GetRawText(),
    };

    /// <summary>SHA-256 of the UTF-8 bytes, lowercase hex — a comparison value, never a secret.</summary>
    public static string Sha256Hex(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string CanonicalObject(JsonElement element)
    {
        var props = element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{JsonSerializer.Serialize(p.Name, Options)}:{Canonicalize(p.Value)}");
        return "{" + string.Join(",", props) + "}";
    }

    private static string CanonicalArray(JsonElement element)
        => "[" + string.Join(",", element.EnumerateArray().Select(Canonicalize)) + "]";

    /// <summary>Round-trip a canonical string back to a <see cref="JsonNode"/> (used when rebuilding
    /// a stored response body for an idempotent replay).</summary>
    public static JsonNode? Parse(string canonicalJson)
        => string.IsNullOrWhiteSpace(canonicalJson) ? null : JsonNode.Parse(canonicalJson);
}
