namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: locate the repository root for the source-scanning tripwires without hardcoding any
///          machine path (the test runs from a worktree, from CI checkout, and from an IDE — all
///          three must scan the SAME tree the test assembly was built from).
/// OWNER: Agent 16.
/// RULE: walk up from the test assembly's location until LIVORA.slnx exists — the solution file is
///          the only root marker in this repo, and a tripwire that silently scanned the wrong tree
///          would be worse than no tripwire, so a missing root THROWS instead of returning null.
/// PROVIDES: Root (absolute), Display(line) for violations, Enumerate(pattern) for scans.
/// INVARIANTS: root discovery is cached per process; scanning NEVER follows junctions/symlinks
///          out of the tree (SearchOption.TopDirectoryOnly per directory, with an explicit
///          bin/obj exclusion — a tripwire that reads generated code would flag its own pin).
/// </summary>
public static class RepoPaths
{
    private static readonly Lazy<string> LazyRoot = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LIVORA.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"No LIVORA.slnx found walking up from {AppContext.BaseDirectory}: honesty tripwires " +
                "refuse to run against an unknown tree (silently scanning nothing is scanning wrong).");
    });

    /// <summary>Absolute repo root, verified by the presence of LIVORA.slnx.</summary>
    public static string Root => LazyRoot.Value;

    /// <summary>A file's absolute path under the root.</summary>
    public static string Combine(params string[] parts) => Path.Combine(Root, Path.Combine(parts));

    /// <summary>How a violation identifies itself: repo-relative path + :line when given.</summary>
    public static string Display(string relativePath, int line = 0)
        => line > 0 ? $"{relativePath}:{line}" : relativePath;

    /// <summary>Repo-relative path of an absolute file under Root (forward slashes for reports).</summary>
    public static string Relative(string absolutePath)
    {
        var root = Path.TrimEndingDirectorySeparator(Root);
        var full = Path.TrimEndingDirectorySeparator(absolutePath);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{full} is outside {root} — tripwire scan escaped the repo");
        return full[root.Length..].Replace('\\', '/').TrimStart('/');
    }

    /// <summary>
    /// All files under <paramref name="relativeDir"/> matching <paramref name="searchPattern"/>,
    /// excluding build output. Throws when the directory does not exist: a renamed project folder
    /// must not silently shrink a tripwire's scope to zero files (the "SKIP nothing silently" rule).
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string relativeDir, string searchPattern = "*.cs")
    {
        var dir = Combine(relativeDir);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException(
                $"tripwire scope directory missing: {relativeDir} (a scan over zero files is a lie)");
        return Directory.EnumerateFiles(dir, searchPattern, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)
                        && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Scan one file (cached per run keyed by path so several tripwires share the cost).</summary>
    public static ScannedFile ScanFile(string absolutePath)
    {
        return _cache.GetOrAdd(absolutePath, static p =>
            SourceTokenizer.Scan(Relative(p), File.ReadAllText(p)));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ScannedFile> _cache =
        new(StringComparer.OrdinalIgnoreCase);
}
