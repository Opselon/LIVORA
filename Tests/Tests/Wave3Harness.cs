using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Shared scaffolding for the Wave 3 suites: locates the repo root from the test assembly's
/// output directory and parses the two resx files from disk (never from the compiled resources —
/// the integrity checks must be able to see what the merge actually wrote).
/// Everything degrades to "skip" instead of failing when a file is genuinely absent, so the suite
/// stays honest on a trimmed checkout.
/// </summary>
internal static class Wave3Harness
{
    /// <summary>Repo root, or null when the source tree is not next to the test binaries.</summary>
    public static readonly string? RepoRoot = FindRoot();

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LIVORA.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    public static string ResxPath(string suffix) =>
        RepoRoot is null ? "" : Path.Combine(RepoRoot, "Resources", "Localization", $"AppResources{suffix}.resx");

    /// <summary>key -> value, document order preserved in <paramref name="order"/>.</summary>
    public static bool TryReadResx(string suffix, out Dictionary<string, string> values, out List<string> order)
    {
        values = new();
        order = new();
        var path = ResxPath(suffix);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        var doc = XDocument.Load(path);
        foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
        {
            var name = data.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            values[name] = data.Element("value")?.Value ?? string.Empty;
            order.Add(name);
        }
        return true;
    }

    /// <summary>Read the two resx files, or return null (caller skips) when they are not present.</summary>
    public static ResxPair? ReadBoth()
    {
        if (!TryReadResx("", out var en, out var enOrder)) return null;
        if (!TryReadResx(".fa", out var fa, out var faOrder)) return null;
        return new ResxPair(en, fa, enOrder, faOrder);
    }

    internal sealed record ResxPair(
        Dictionary<string, string> En,
        Dictionary<string, string> Fa,
        List<string> EnOrder,
        List<string> FaOrder);

    /// <summary>True when the text holds at least one Persian/Arabic-script codepoint.</summary>
    public static bool HasPersianCodepoint(string text) =>
        text.Any(c => c is >= '\u0600' and <= '\u06FF');

    /// <summary>Placeholder indices used by a format string ("{0}" / "{1:...}" → 0,1).</summary>
    public static int[] Placeholders(string format) =>
        System.Text.RegularExpressions.Regex.Matches(format ?? "", @"\{(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .OrderBy(i => i)
            .ToArray();

    /// <summary>Source text with comments and preprocessor lines removed (for prose scanning).</summary>
    public static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        int i = 0;
        bool inLine = false, inBlock = false, inStr = false, inChar = false, inVerbatim = false;
        while (i < source.Length)
        {
            char c = source[i];
            if (inLine)
            {
                if (c == '\n') { inLine = false; sb.Append(c); }
                i++;
                continue;
            }
            if (inBlock)
            {
                if (c == '*' && i + 1 < source.Length && source[i + 1] == '/') { inBlock = false; i += 2; continue; }
                i++;
                continue;
            }
            if (inStr)
            {
                sb.Append(c);
                if (inVerbatim && c == '"' && i + 1 < source.Length && source[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                if (c == '\\' && !inVerbatim && i + 1 < source.Length) { sb.Append(source[i + 1]); i += 2; continue; }
                if (c == '"') inStr = false;
                i++;
                continue;
            }
            if (inChar)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == '\'') inChar = false;
                i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/') { inLine = true; i += 2; continue; }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*') { inBlock = true; i += 2; continue; }
            if (c == '"')
            {
                inStr = true;
                // @$"/@"/$" prefixes sit before the quote
                inVerbatim = i >= 2 && (source[i - 1] == '@' || (source[Math.Max(0, i - 2)] == '@'));
                sb.Append(c);
                i++;
                continue;
            }
            if (c == '\'') { inChar = true; i++; continue; }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>All string literals in a source file (comment-stripped), de-escaped.</summary>
    public static IEnumerable<string> Literals(string source)
    {
        var clean = StripComments(source);
        var matches = System.Text.RegularExpressions.Regex
            .Matches(clean, @"""((?:\\.|[^""\\\n])*)""");
        foreach (System.Text.RegularExpressions.Match m in matches)
            yield return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
    }

    /// <summary>Enumerate *.cs under a repo-relative folder (empty when the folder is absent).</summary>
    public static IEnumerable<(string Path, string Source)> Sources(string relativeDir)
    {
        if (RepoRoot is null) yield break;
        var dir = Path.Combine(RepoRoot, relativeDir);
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string text;
            try { text = File.ReadAllText(f); } catch { continue; }
            yield return (f, text);
        }
    }
}

/// <summary>
/// xunit's collection so every test that mutates process-global culture state (LocalizationService
/// sets AppResources.Culture + CultureInfo.CurrentCulture) runs against itself, never racing another
/// class mid-flight.
/// </summary>
[CollectionDefinition("LocalizationState")]
public class LocalizationStateCollection : ICollectionFixture<object>
{
    // Marker only: no fixture instance is needed, the definition just serializes the members.
}
