using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Intelligence;

/// <summary>
/// Tolerant JSON reader for AI responses (Wave 3c lane 02). Models wrap JSON in markdown
/// fences, prepend prose, forget commas, or send whole SSE streams — so the parser finds the
/// first balanced JSON object, repairs trailing commas, and reads field-by-field with
/// type-tolerance. It NEVER throws: unparseable garbage returns null and the caller falls back
/// to deterministic output. MAUI-free.
/// </summary>
public static class AiResponseParser
{
    /// <summary>
    /// Parse a provider body into a response, or null when no JSON object can be salvaged.
    /// Accepts both a raw chat.completion envelope ({choices:[{message:{content}}]}) and a
    /// bare insight object (the shape after SSE accumulation).
    /// </summary>
    public static AiInsightResponse? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            // 1) If the body is a full chat.completion envelope, dig out the message content
            //    (which is itself expected to be the insight JSON). Fall through when absent.
            var inner = TryExtractCompletionContent(body);
            var candidate = ExtractFirstJsonObject(inner ?? body);
            if (candidate is null) return null;
            using var doc = JsonDocument.Parse(RepairJson(candidate), JsonOptions);
            return FromElement(doc.RootElement);
        }
        catch (JsonException) { return null; }
        catch (Exception) { return null; }   // never throw into the app — contract of the layer
    }

    /// <summary>
    /// Accumulate an SSE body: consume `data:` lines, concatenate choice deltas, stop at
    /// [DONE]. Lines without a parseable JSON payload are skipped, never fatal. Public so the
    /// transport and tests share ONE SSE implementation.
    /// </summary>
    public static string? AccumulateSse(string sseBody)
    {
        if (string.IsNullOrWhiteSpace(sseBody) || !sseBody.Contains("data:", StringComparison.Ordinal))
            return null;

        var sb = new StringBuilder();
        bool any = false;
        foreach (var rawLine in sseBody.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            if (payload == "[DONE]") break;
            any = true;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var delta = doc.RootElement
                    .GetPropertyOrEmpty("choices")
                    .FirstArrayElementOrEmpty()
                    .GetPropertyOrEmpty("delta")
                    .GetPropertyOrEmpty("content");
                if (delta.ValueKind == JsonValueKind.String) sb.Append(delta.GetString());
                else
                {
                    // Some gateways put the final answer in message.content even on stream chunks.
                    var msg = doc.RootElement
                        .GetPropertyOrEmpty("choices")
                        .FirstArrayElementOrEmpty()
                        .GetPropertyOrEmpty("message")
                        .GetPropertyOrEmpty("content");
                    if (msg.ValueKind == JsonValueKind.String) sb.Append(msg.GetString());
                }
            }
            catch (JsonException) { /* a malformed chunk line is skipped, not fatal */ }
        }
        return any && sb.Length > 0 ? sb.ToString() : null;
    }

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Cheap gate the transport uses before trusting a 200 body: it must at least CONTAIN a
    /// JSON object start. HTML error pages from proxies, empty bodies, plain prose chatter —
    /// none of it is a completion, and none of it should reach the parser as "success".
    /// </summary>
    public static bool ContainsJsonStart(string? content) =>
        !string.IsNullOrWhiteSpace(content) && content.Contains('{');

    /// <summary>
    /// Single entry point the transport uses to turn a raw HTTP body into the assistant's
    /// answer text. Handles BOTH shapes this gateway emits: (1) SSE `data:` lines accumulated
    /// until [DONE] — the gateway streams even when stream:false; (2) a plain chat.completion
    /// envelope; (3) a bare body returned verbatim (already the insight JSON). Null = nothing
    /// usable.
    /// </summary>
    public static string? ExtractAssistantContent(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        var sse = AccumulateSse(body);
        if (sse is not null) return sse;
        return TryExtractCompletionContent(body) ?? body;
    }

    /// <summary>When the body is a chat.completion envelope, return message.content; else null.</summary>
    private static string? TryExtractCompletionContent(string body)
    {
        var candidate = ExtractFirstJsonObject(body);
        if (candidate is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(RepairJson(candidate), JsonOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var content = root
                .GetPropertyOrEmpty("choices")
                .FirstArrayElementOrEmpty()
                .GetPropertyOrEmpty("message")
                .GetPropertyOrEmpty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Strip markdown fences, then scan for the first '{' and take the object that starts there
    /// (string- and escape-aware). Prose before/after the braces is ignored. A truncated body
    /// is salvaged by closing every still-open { and [ in reverse order.
    /// </summary>
    internal static string? ExtractFirstJsonObject(string text)
    {
        var cleaned = StripFences(text);
        int start = cleaned.IndexOf('{');
        if (start < 0) return null;

        var stack = new List<char>();
        bool inStr = false, esc = false;
        for (int i = start; i < cleaned.Length; i++)
        {
            char c = cleaned[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': stack.Add('}'); break;
                case '[': stack.Add(']'); break;
                case '}' or ']':
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    if (stack.Count == 0 && c == '}') return cleaned[start..(i + 1)];
                    break;
            }
        }
        if (inStr) return null;   // unterminated string: unsalvageable
        // Truncated body: close whatever is still open, innermost first.
        return stack.Count == 0 ? null
            : cleaned[start..] + string.Concat(stack.AsEnumerable().Reverse());
    }

    private static string StripFences(string text)
    {
        if (!text.Contains("```", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder(text.Length);
        bool inFence = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;   // fence delimiters themselves are dropped
                continue;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Repair the classic model sins: trailing commas before } or ] (also tolerated natively by
    /// the parser options, but this lets the salvage path parse too), and stray single quotes
    /// are NOT repaired — they are the model's problem, garbage stays garbage.
    /// </summary>
    internal static string RepairJson(string json)
    {
        var sb = new StringBuilder(json.Length);
        bool inStr = false, esc = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inStr)
            {
                sb.Append(c);
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; sb.Append(c); continue; }
            if (c == ',')
            {
                int j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && (json[j] == '}' || json[j] == ']')) continue; // drop it
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static AiInsightResponse? FromElement(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var headlineKey = ReadString(root, "headlineKey") ?? string.Empty;
        var providerText = ReadString(root, "bodyText");
        var claims = ReadClaims(root);

        // Headline/body may be provider free-text: true when the model sent literal text where
        // a key belongs OR when bodyText is present (the prompt always uses the Provider keys).
        var resp = new AiInsightResponse
        {
            HeadlineKey = headlineKey,
            BodyKey = ReadString(root, "bodyKey") ?? string.Empty,
            ProviderText = providerText,
            Confidence = ReadDouble(root, "confidence") ?? 0,
            Claims = claims,
            ProposedActionKinds = ReadStringList(root, "proposedActionKinds"),
            BodyIsProviderText = providerText is { Length: > 0 },
            HeadlineIsProviderText = headlineKey.Length > 0 && !LooksLikeKey(headlineKey),
        };

        if (resp.HeadlineKey.Length == 0 && resp.BodyKey.Length == 0 && providerText is null)
            return null;   // an object with none of the schema fields is garbage, not a response
        return resp;
    }

    private static bool LooksLikeKey(string s) =>
        s.Length <= 64 && !s.Contains(' ') && s.Contains('.') &&
        char.IsUpper(s[0]);

    private static string? ReadString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var e)) return null;
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Number => e.ToString(),   // wrong type but readable: tolerate
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static double? ReadDouble(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var e)) return null;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d)) return d;
        if (e.ValueKind == JsonValueKind.String &&
            double.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var e) || e.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
        {
            var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
        }
        return list;
    }

    private static IReadOnlyList<NumericClaim> ReadClaims(JsonElement obj)
    {
        if (!obj.TryGetProperty("claims", out var e) || e.ValueKind != JsonValueKind.Array)
            return Array.Empty<NumericClaim>();
        var list = new List<NumericClaim>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var key = ReadString(item, "metricKey");
            var val = ReadDouble(item, "value");
            var unit = ReadString(item, "unit");
            if (key is null || val is null) continue;   // a claim without key+value is worthless
            if (double.IsNaN(val.Value) || double.IsInfinity(val.Value)) continue;
            list.Add(new NumericClaim(key.Trim(), val.Value, (unit ?? string.Empty).Trim()));
        }
        return list;
    }
}

/// <summary>Tiny JsonElement helpers so the parser reads like English instead of try/catch soup.</summary>
internal static class JsonElementExtensions
{
    /// <summary>A stand-in "no element" value: ValueKind Undefined, safe to chain on.</summary>
    public static readonly JsonElement Missing = default;

    public static JsonElement GetPropertyOrEmpty(this JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) ? v : Missing;

    public static JsonElement FirstArrayElementOrEmpty(this JsonElement el) =>
        el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0 ? el[0] : Missing;
}
