using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: the release-gate harness — the three §3 commands from CONTRACT-P1 executed as real
///          child processes in order, each with a parsed verdict and an on-disk evidence log. The
///          point is that a human (or the lead's integration script) can ask "is Wave 4 shippable"
///          and get machine answers with receipts, not vibes.
/// OWNER: Agent 16 (QA/release gate).
/// PROVIDES: <see cref="RunAsync"/> (ordered gate results), <see cref="GateVerdict"/>.
/// INVARIANTS:
///   - ONE live execution per process: a static gate + cached Task means parallel test
///     collection, retries, or a second caller can never stack four-minute builds on the CPU
///     (the exact contention that makes the client perf budgets flake — FLAKE-STORAGE-BUDGET.md)
///   - recursion guard: the server gate runs THIS assembly, so children get LIVORA_GATE_CHILD=1;
///     a gate call inside a child returns BLOCKED-with-reason instead of forking another run —
///     honest "we did not execute", never a fake PASS
///   - the known client perf flake has a NARROW, documented retry: a client-gate failure is
///     retried exactly once if and only if every failing test belongs to
///     StoragePerformanceBudgetTests (the class P1-F owns); any other failure is a hard FAIL.
///     The retry exists because the budget files are FROZEN for this lane; the real fix
///     (serialise that category) is requested of the lead in REQUESTS-P1F.md.
///   - nothing here mutates the repo: evidence is written under the OS temp path, never into
///     the worktree (a test that dirties git status is a gate that lies about the worktree)
/// </summary>
public sealed class Wave4Gate
{
    public const string ChildGuardEnvVar = "LIVORA_GATE_CHILD";

    private static readonly SemaphoreSlim GateLock = new(1, 1);
    private static Task<IReadOnlyList<GateResult>>? _running;

    /// <summary>Run the three gates in contract order (build → server tests → client tests).
    /// Concurrent callers share ONE execution; the env-var recursion guard is per-process.
    /// NOTE for external callers (the lead's integration script): the server-tests gate runs with
    /// --no-build because the in-repo caller IS this assembly's testhost (locked bin DLLs). When
    /// you call RunAsync from outside a test run, build the solution first — the build gate does
    /// exactly that for the backend projects, but the client and test assemblies must be current.</summary>
    public static Task<IReadOnlyList<GateResult>> RunAsync(string repoRoot, TimeSpan? perGateTimeout = null)
    {
        lock (GateLock)
        {
            // Reuse the in-flight (or completed) run so a parallel collection can never double-run.
            // A new repoRoot after a completed run is not expected in-test and reuses the cache
            // deliberately: the gate is a process-level fact, not a per-call fact.
            _running ??= RunCoreAsync(repoRoot, perGateTimeout ?? TimeSpan.FromMinutes(15));
            return _running;
        }
    }

    private static async Task<IReadOnlyList<GateResult>> RunCoreAsync(string repoRoot, TimeSpan perGateTimeout)
    {
        if (Environment.GetEnvironmentVariable(ChildGuardEnvVar) == "1")
        {
            // Inside a gate child: executing the gates here would fork an unbounded process tree.
            return Definitions(null).Select(d => GateResult.Blocked(d,
                "recursion guard: this process was started BY the gate harness and must not re-run it")).ToList();
        }

        var runStamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var evidenceDir = Path.Combine(Path.GetTempPath(), "livora-wave4-gates", runStamp);
        Directory.CreateDirectory(evidenceDir);
        // Build output for the build gate is redirected OUT of the repo's bin/ dirs: this harness
        // is executed BY the server test assembly (Wave4GateTests), whose testhost has those DLLs
        // loaded — a child build copying onto them self-deadlocks on file locks (measured: MSB3027
        // "locked by testhost"). A redirected OutDir also makes the verdict a true clean compile
        // from source, not an incremental no-op on timestamps.
        var gateOut = Path.Combine(Path.GetTempPath(), "livora-wave4-gate-out", runStamp);

        var results = new List<GateResult>();
        await GateLock.WaitAsync();
        try
        {
            foreach (var def in Definitions(gateOut))
                results.Add(await RunGateAsync(def, repoRoot, evidenceDir, perGateTimeout));
        }
        finally
        {
            GateLock.Release();
        }
        return results;
    }

    /// <summary>
    /// The gate ladder as the contract states it. <see cref="GateDefinition.Command"/> is ALWAYS the
    /// §3 line verbatim (the meta-test pins it); <see cref="GateDefinition.Arguments"/> is how the
    /// harness must EXECUTE it from inside the server test process without self-deadlocking:
    ///   - server-build: + -p:OutDir=&lt;temp&gt; (see RunCoreAsync note; same project, same compiler,
    ///     warnings-as-errors intact — the verdict is still "does the backend build clean")
    ///   - server-tests: + --no-build (the testhost running this harness holds its own bin DLLs
    ///     locked; a child rebuild would copy onto them and fail with a spurious MSB3027. Freshness
    ///     is guaranteed by construction: the only in-repo caller of RunAsync is a test that exists
    ///     because the outer `dotnet test` just built this very assembly from current sources. An
    ///     external caller must build first — stated on RunAsync.)
    /// </summary>
    private static IReadOnlyList<GateDefinition> Definitions(string? gateOut) =>
    [
        new("server-build",
            "dotnet", "dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo",
            ["build", "server/src/Livora.Server/Livora.Server.csproj", "-v", "q", "--nologo",
             .. (gateOut is null
                     ? Array.Empty<string>()
                     : new[] { $"-p:OutDir={gateOut}\\" })],
            Expect.SucceededLine()),
        new("server-tests",
            "dotnet", "dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q",
            ["test", "server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj", "--nologo", "-v", "q",
             "--no-build"],
            Expect.TestPassedLine(minPassed: 21)),
        new("client-tests",
            "dotnet", "dotnet test Tests/LIVORA.Tests.csproj --nologo -v q",
            ["test", "Tests/LIVORA.Tests.csproj", "--nologo", "-v", "q"],
            Expect.TestPassedLine(minPassed: 1188), AllowKnownPerfRetry: true),
    ];

    /// <summary>Gate names + verbatim commands, for the meta-test and reports.</summary>
    public static IReadOnlyList<string> GateNames() => Definitions(null).Select(d => d.Name).ToList();
    public static IReadOnlyList<string> GateCommands() => Definitions(null).Select(d => d.Command).ToList();
    public static IReadOnlyList<IReadOnlyList<string>> GateArguments() =>
        Definitions(null).Select(d => (IReadOnlyList<string>)d.Arguments).ToList();

    private static async Task<GateResult> RunGateAsync(GateDefinition def, string repoRoot,
        string evidenceDir, TimeSpan timeout, int attempt = 1)
    {
        var sw = Stopwatch.StartNew();
        var (exitCode, output) = await ExecuteAsync(def, repoRoot, timeout);
        sw.Stop();

        var evidencePath = Path.Combine(evidenceDir,
            $"{def.Name}{(attempt > 1 ? $".retry{attempt}" : "")}.log");
        File.WriteAllText(evidencePath,
            $"$ {def.Command}\n(cwd {repoRoot})\nexit {exitCode}\n\n{output}");

        var (verdict, detail) = def.Evaluate(exitCode, output);

        // Narrow known-flake retry — see the class header. Only the perf class, only once.
        if (def.AllowKnownPerfRetry && verdict == GateVerdict.Fail && attempt == 1
            && OnlyKnownPerfFailures(output))
        {
            var retried = await RunGateAsync(def, repoRoot, evidenceDir, timeout, attempt + 1);
            if (retried.Verdict == GateVerdict.Pass)
                return retried with
                {
                    Detail = $"retried once under the documented StoragePerformanceBudgetTests " +
                             $"flake policy (FLAKE-STORAGE-BUDGET.md); first attempt failed: {detail}",
                };
            return retried with
            {
                Detail = $"retry also failed; first: {detail}; retry: {retried.Detail}",
            };
        }

        return new GateResult(def.Name, def.Command, verdict, sw.ElapsedMilliseconds, evidencePath, detail);
    }

    /// <summary>True only when EVERY failing test named in the output is a member of the class
    /// P1-F owns the flake for. One unexpected name anywhere in the failure list → no retry.</summary>
    internal static bool OnlyKnownPerfFailures(string output)
    {
        var failing = Regex.Matches(output, @"(\S+)\s\[FAIL\]")
            .Select(m => m.Groups[1].Value)
            .Where(n => n.StartsWith("LIVORA.Tests", StringComparison.Ordinal))
            .Distinct()
            .ToList();
        return failing.Count > 0 && failing.All(n => n.StartsWith(
            "LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests", StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, string Output)> ExecuteAsync(GateDefinition def,
        string repoRoot, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = WhichDotnet(),
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in def.Arguments) psi.ArgumentList.Add(a);
        psi.Environment[ChildGuardEnvVar] = "1";

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{psi.FileName}'");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, $"TIMEOUT after {timeout.TotalMinutes:F1} min");
        }
        return (proc.ExitCode, $"{await stdout}\n{await stderr}");
    }

    /// <summary>dotnet may not be on PATH in every launch context (CI shells, IDE hosts); resolve
    /// it the way the SDK resolver does before giving up.</summary>
    private static string WhichDotnet()
    {
        var fromPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists);
        if (fromPath is not null) return fromPath;
        var candidates = OperatingSystem.IsWindows()
            ? new[] { @"C:\Program Files\dotnet\dotnet.exe" }
            : new[] { "/usr/share/dotnet/dotnet", "/usr/local/bin/dotnet" };
        return candidates.FirstOrDefault(File.Exists) ?? "dotnet";
    }

    private static class Expect
    {
        public static Func<int, string, (GateVerdict, string)> SucceededLine() => (code, output) =>
            code == 0 && output.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase)
                ? (GateVerdict.Pass, "0 warnings / 0 errors (TreatWarningsAsErrors on server/)")
                : (GateVerdict.Fail, Tail(output, "error"));

        public static Func<int, string, (GateVerdict, string)> TestPassedLine(int minPassed) => (code, output) =>
        {
            var m = Regex.Match(output, @"Passed!\s+-\s+Failed:\s+(?<f>\d+),\s+Passed:\s+(?<p>\d+)");
            if (code == 0 && m.Success && int.Parse(m.Groups["p"].Value) >= minPassed)
                return (GateVerdict.Pass,
                    $"passed {m.Groups["p"].Value} (baseline {minPassed}), failed {m.Groups["f"].Value}");
            var fm = Regex.Match(output, @"Failed!\s+-\s+Failed:\s+(?<f>\d+)");
            return (GateVerdict.Fail,
                fm.Success ? $"{fm.Groups["f"].Value} failing — {Tail(output, "[FAIL]")}" : Tail(output));
        };
    }

    private static string Tail(string output, string? contains = null)
    {
        var lines = output.Split('\n')
            .Where(l => contains is null || l.Contains(contains, StringComparison.OrdinalIgnoreCase))
            .TakeLast(6);
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.Trim()).Append(" | ");
        return sb.Length == 0 ? "no output" : sb.ToString(0, Math.Min(sb.Length - 3, 600));
    }
}

public enum GateVerdict { Pass, Fail, Blocked, NotImplemented }

/// <summary>One gate's outcome. The fields are the report rows the lead pastes into the release log.</summary>
public sealed record GateResult(
    string Name,
    string Command,
    GateVerdict Verdict,
    long DurationMs,
    string EvidencePath,
    string Detail)
{
    public static GateResult Blocked(GateDefinition def, string reason) =>
        new(def.Name, def.Command, GateVerdict.Blocked, 0, "", reason);
}

/// <summary>A gate: name, executable, the VERBATIM §3 command (reports + meta-test), the actual
/// execution arguments (which may add harness-necessary flags — see Wave4Gate.Definations doc),
/// and the verdict evaluator. <param
/// name="AllowKnownPerfRetry">true ONLY for the client suite, and even then the retry fires only
/// when every failure is in the one flaky class (Wave4Gate.OnlyKnownPerfFailures).</param></summary>
public sealed record GateDefinition(
    string Name,
    string Executable,
    string Command,
    IReadOnlyList<string> Arguments,
    Func<int, string, (GateVerdict, string)> Evaluate,
    bool AllowKnownPerfRetry = false);
