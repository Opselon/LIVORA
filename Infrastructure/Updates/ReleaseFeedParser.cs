using System.Text.Json;
using System.Text.RegularExpressions;
using LIVORA.Application.Abstractions;
namespace LIVORA.Infrastructure.Updates;
public static class ReleaseFeedParser
{
    public const int PageSize = 10;
    public const string DefaultFeedUrl =
        "https://api.github.com/repos/Opselon/LIVORA/releases?per_page=10";
    public const string RuleStable = "stable";
    public const string RulePrerelease = "prerelease";
    public const string RuleNone = "none";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
    public static List<ReleaseEntry>? ParseReleases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<ReleaseEntry>>(json, JsonOptions);
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
    public static ReleaseEntry? SelectBest(IEnumerable<ReleaseEntry>? releases, out string rule)
    {
        var usable = (releases ?? Enumerable.Empty<ReleaseEntry>())
            .Where(r => r is { IsDraft: false })
            .Where(r => AppVersion.TryParse(r.TagName, out _))
            .ToList();
        if (usable.Count == 0)
        {
            rule = RuleNone;
            return null;
        }
        var stable = usable.Where(r => !r.IsPreRelease).ToList();
        if (stable.Count > 0)
        {
            rule = RuleStable;
            return Newest(stable);
        }
        rule = RulePrerelease;
        return Newest(usable);
    }
    private static ReleaseEntry Newest(List<ReleaseEntry> candidates)
    {
        var best = candidates[0];
        for (int i = 1; i < candidates.Count; i++)
            if (IsNewer(candidates[i], best)) best = candidates[i];
        return best;
    }
    private static bool IsNewer(ReleaseEntry a, ReleaseEntry b)
    {
        int? cmp = AppVersion.Compare(a.TagName, b.TagName);
        if (cmp is { } c && c != 0) return c > 0;
        var pa = a.PublishedAtUtc ?? DateTime.MinValue;
        var pb = b.PublishedAtUtc ?? DateTime.MinValue;
        if (pa != pb) return pa > pb;
        return string.CompareOrdinal(a.TagName, b.TagName) > 0;
    }
    private static readonly Regex MarkdownLink = new(@"\[(?<text>[^\]]*)\]\((?<url>[^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*\d+[.)]\s+", RegexOptions.Compiled);
    private static readonly Regex UrlStripper = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex HtmlPair = new(@"</?(?:br|p|div|span|b|i|em|strong|code|pre|ul|ol|li|a|img|h[1-6])\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HtmlOther = new(@"<[^>]{0,200}>", RegexOptions.Compiled);
    public static IReadOnlyList<string> CleanseBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        var lines = new List<string>();
        bool lastAddedWasTableRow = false;
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw;
            line = line.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                       .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&nbsp;", " ");
            line = Image.Replace(line, " ");
            line = MarkdownLink.Replace(line, m =>
            {
                var text = m.Groups["text"].Value.Trim();
                var url = m.Groups["url"].Value.Trim();
                if (text.Length == 0) return " ";
                return UriLooksLikeLink(text, url) ? " " : text;
            });
            line = HtmlPair.Replace(line, " ");
            line = HtmlOther.Replace(line, " ");
            line = Heading.Replace(line, "");
            line = Numbered.Replace(line, "• ");
            line = Bullet.Replace(line, "• ");
            line = line.Replace("`", "").Replace("**", "").Replace("__", "");
            line = line.Replace(@"\*", "*").Replace(@"\_", "_");
            bool hadRawUrl = UrlStripper.IsMatch(line);
            line = UrlStripper.Replace(line, " ");
            line = CollapseSpaces(line);
            if (hadRawUrl && (line.Length == 0 || line.EndsWith(":", StringComparison.Ordinal))) continue;
            var isTableLine = line.StartsWith("|", StringComparison.Ordinal);
            if (isTableLine && IsTableSeparator(line))
            {
                if (lastAddedWasTableRow && lines.Count > 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                    lastAddedWasTableRow = false;
                }
                continue;
            }
            if (isTableLine)
            {
                var cells = line.Split('|', StringSplitOptions.TrimEntries)
                                .Where(cell => cell.Length > 0 && !cell.All(ch => ch is '-' or ':'))
                                .ToList();
                line = CollapseSpaces(string.Join(" · ", cells));
            }
            if (line.Length == 0 || line is "•" or "-" or "*" or "_" or "|") continue;
            if (IsDecoration(line)) continue;
            if (line.TrimEnd(' ', '•', '-', '|', ':', '·').Length == 0) continue;
            lines.Add(Ellipsis(line, 300));
            lastAddedWasTableRow = isTableLine;
            if (lines.Count >= 24) break;
        }
        return lines;
    }
    private static bool IsTableSeparator(string line)
    {
        if (!line.Contains('-')) return false;
        var cells = line.Split('|', StringSplitOptions.TrimEntries).Where(c => c.Length > 0);
        return cells.All(c => c.All(ch => ch is '-' or ':'));
    }
    private static bool UriLooksLikeLink(string text, string url)
        => text.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(text, url, StringComparison.OrdinalIgnoreCase) ||
           text.Length == 0;
    private static bool IsDecoration(string line)
    {
        int letters = line.Count(char.IsLetterOrDigit);
        if (letters == 0) return true;
        if (letters < 4 && line.Any(c => c is '<' or '>' or '=' or ']' or '[' or '!')) return true;
        return false;
    }
    private static string CollapseSpaces(string s)
    {
        var spans = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = string.Join(' ', spans);
        if (joined.StartsWith("• ", StringComparison.Ordinal)) joined = joined[2..].TrimStart();
        if (joined.StartsWith("•") && joined.Length > 1 && joined[1] == ' ') joined = "• " + joined[2..].TrimStart();
        return joined.Trim();
    }
    private static string Ellipsis(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    public static IReadOnlyDictionary<string, string> BuildLinks(ReleaseEntry? release)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var bestRank = new Dictionary<string, int>(StringComparer.Ordinal);
        if (release?.Assets is not { Count: > 0 } assets) return map;
        foreach (var asset in assets)
        {
            var url = asset.BrowserDownloadUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            var key = DetectPlatform(asset.Name) ?? DetectPlatform(url);
            if (key is null) continue;
            int rank = SimulatorRank(asset.Name);
            bool better = !map.TryGetValue(key, out var existing)
                || rank < bestRank[key]
                || (rank == bestRank[key] && SizeOf(assets, existing) < asset.SizeBytes);
            if (better)
            {
                map[key] = url!;
                bestRank[key] = rank;
            }
        }
        if (!map.ContainsKey("all"))
        {
            var generic = assets.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl) &&
                DetectPlatform(a.Name) is null && DetectPlatform(a.BrowserDownloadUrl) is null);
            if (generic is not null) map["all"] = generic.BrowserDownloadUrl!;
        }
        return map;
    }
    public static int SimulatorRank(string? assetName)
    {
        var s = (assetName ?? string.Empty).ToLowerInvariant();
        return s.Contains("simulator") || s.Contains("emulator") || s.Contains("-sim") ? 1 : 0;
    }
    private static long SizeOf(List<ReleaseAsset> assets, string url)
        => assets.FirstOrDefault(a => string.Equals(a.BrowserDownloadUrl, url, StringComparison.Ordinal))?.SizeBytes ?? 0;
    public static string? DetectPlatform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.ToLowerInvariant();
        if (Contains(s, "android", ".apk", ".aab")) return "android";
        if (Contains(s, "ios", ".ipa", "iphone")) return "ios";
        if (Contains(s, "maccatalyst", "macos", "osx", "mac", ".dmg", "-mac-")) return "macos";
        if (Contains(s, "windows", "win32", "-win", ".msix", ".msi", ".exe", "win-arm64", "win-x64", "win-x86")) return "windows";
        if (Contains(s, "universal", "-all", "all-")) return "all";
        return null;
    }
    private static bool Contains(string haystack, params string[] needles)
    {
        foreach (var n in needles)
            if (haystack.Contains(n, StringComparison.Ordinal)) return true;
        return false;
    }
    public static string FileNameOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            var uri = new Uri(url, UriKind.RelativeOrAbsolute);
            var path = uri.IsAbsoluteUri ? uri.AbsolutePath : url;
            var tail = path.Split('/', '?', '#').Where(p => p.Length > 0).LastOrDefault();
            return string.IsNullOrEmpty(tail) ? url : Uri.UnescapeDataString(tail);
        }
        catch (Exception)
        {
            return url;
        }
    }
}
