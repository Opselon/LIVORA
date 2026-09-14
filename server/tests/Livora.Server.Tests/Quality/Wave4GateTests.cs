using System.Diagnostics;
using Xunit.Abstractions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: the release-gate harness test. It runs <see cref="Wave4Gate"/> ONCE per process
///          (single-flight inside the harness: a class fixture alone cannot stop xunit's parallel
///          COLLECTION from re-entering a static, so the gate + cached Task inside RunAsync is what
///          guarantees the three four-minute-class commands never stack on the CPU — the exact
///          contention that makes the client perf budgets flake, see FLAKE-STORAGE-BUDGET.md).
/// OWNER: Agent 16 (QA/release gate).
/// INVARIANTS:
///   - a child process started BY the harness (LIVORA_GATE_CHILD=1) does not re-run the gates —
///     the server-tests gate would otherwise fork an endless ladder of dotnet-test processes;
///     the child says so in its output (nothing skipped silently) and exits green
///   - the wall-clock budget is generous on purpose: a cold build inside a test can take minutes;
///     the harness owns per-command timeouts, this test owns the verdicts and the receipts
///   - the test asserts what the harness reported; it never re-derives a verdict by hand
/// </summary>
[Collection(Wave4SerialCollection.Name)]
public sealed class Wave4GateTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    [Fact]
    public async Task GateHarness_runs_the_three_contract_gates_once_and_reports_receipts()
    {
        if (Environment.GetEnvironmentVariable(Wave4Gate.ChildGuardEnvVar) == "1")
        {
            _out.WriteLine("WAVE4 GATE NOT RE-RUN IN CHILD: this testhost was spawned by the gate " +
                           "harness (recursion guard); the parent run already asserted the verdicts.");
            return;
        }

        var sw = Stopwatch.StartNew();
        var results = await Wave4Gate.RunAsync(RepoPaths.Root);
        sw.Stop();

        foreach (var r in results)
            _out.WriteLine($"GATE {r.Name}: {r.Verdict} in {r.DurationMs} ms — {r.Detail} — evidence: {r.EvidencePath}");

        Assert.Equal(3, results.Count);
        Assert.Equal(["server-build", "server-tests", "client-tests"], results.Select(r => r.Name).ToArray());

        // Every gate must have either an evidence receipt on disk or a documented Blocked reason.
        foreach (var r in results)
        {
            if (r.Verdict == GateVerdict.Blocked) continue;
            Assert.True(File.Exists(r.EvidencePath), $"gate {r.Name} reported {r.Verdict} with no evidence file");
            Assert.True(r.DurationMs > 0, $"gate {r.Name} recorded zero duration — it did not execute");
        }

        // The receipts must say PASS — a red lane cannot self-declare green; that is the whole point.
        foreach (var r in results)
            Assert.True(r.Verdict == GateVerdict.Pass, $"gate {r.Name} verdict {r.Verdict}: {r.Detail}");

        _out.WriteLine($"full gate ladder completed in {sw.Elapsed.TotalSeconds:F1} s");
    }

    // ---- pure logic around the narrow flake-retry policy (no processes) ----------------------------

    [Fact]
    public void Flake_retry_policy_fires_only_for_the_one_owned_class()
    {
        Assert.True(Wave4Gate.OnlyKnownPerfFailures(
            "[xUnit.net] LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests.Allocation_100Loads_X [FAIL]"));
        Assert.True(Wave4Gate.OnlyKnownPerfFailures(
            "LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests.SyncQueue_10000Enqueues_Y [FAIL]\n" +
            "LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests.Allocation_100Loads_X [FAIL]"));
    }

    [Fact]
    public void Flake_retry_policy_refuses_when_any_other_test_fails()
    {
        Assert.False(Wave4Gate.OnlyKnownPerfFailures(
            "LIVORA.Tests.Wave3c.Perf.StoragePerformanceBudgetTests.Allocation_100Loads_X [FAIL]\n" +
            "LIVORA.Tests.Tests.SomeRealBugTests.Regression [FAIL]"));
        Assert.False(Wave4Gate.OnlyKnownPerfFailures("Passed! - Failed: 0")); // nothing failed → no retry
    }

    [Fact]
    public void Harness_defines_exactly_the_three_contract_commands_verbatim()
    {
        Assert.Equal(["server-build", "server-tests", "client-tests"], Wave4Gate.GateNames());
        // the §3 commands verbatim — the harness must not silently test something weaker
        Assert.Contains("dotnet build server/src/Livora.Server/Livora.Server.csproj -v q --nologo",
            Wave4Gate.GateCommands());
        Assert.Contains("dotnet test server/tests/Livora.Server.Tests/Livora.Server.Tests.csproj --nologo -v q",
            Wave4Gate.GateCommands());
        Assert.Contains("dotnet test Tests/LIVORA.Tests.csproj --nologo -v q", Wave4Gate.GateCommands());
    }
}
