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
/// R3 SELF-REFERENCE POLICY (documented decision, 14 Sep): the server-tests gate runs THIS very
///   assembly, so the harness can never be its own green check — a red Engines test in another
///   lane makes the child server-tests gate Fail while the Quality suite itself is fine, and the
///   parent would then fail for a reason it cannot honestly fix (the ownership rules forbid this
///   lane from editing other lanes' files). Chosen remedy — NOT dropping the gate: the harness
///   still RUNS all three §3 commands and reports every child verdict verbatim with receipts; the
///   parent test asserts (a) server-build and client-tests PASS absolutely, and (b) for
///   server-tests, PASS or — when Fail — failures confined to tests OUTSIDE Quality/**, which are
///   enumerated on the output as gate findings for the owning lanes. Any failure INSIDE
///   Quality/** (this lane's own scope, including the tripwires and the security checklist) is a
///   hard FAIL. The authoritative whole-suite verdict remains CI's separate server-tests step
///   (docs/quality/wave4/CI-SERVER-JOB.md gate 2), which has no self-reference. This is the
///   "report child-verdicts without self-failing" option from the R3 brief.
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

        // (a) The two gates that CANNOT be contaminated by self-reference must be PASS, full stop.
        foreach (var r in results.Where(r => r.Name is "server-build" or "client-tests"))
            Assert.True(r.Verdict == GateVerdict.Pass, $"gate {r.Name} verdict {r.Verdict}: {r.Detail}");

        // (b) server-tests: PASS, or Fail whose failures live entirely OUTSIDE this lane's scope —
        //     enumerated here so the report is a finding list, never a silent pass.
        var server = results.Single(r => r.Name == "server-tests");
        if (server.Verdict != GateVerdict.Pass)
        {
            var failing = FailingTestsIn(server.EvidencePath);
            Assert.NotEmpty(failing); // Fail with no parsable failing test = the harness lied; that IS a failure
            var own = failing.Where(n => n.Contains("Livora.Server.Tests.Quality", StringComparison.Ordinal)).ToList();
            Assert.Empty(own); // this lane's own scope must be spotless — no outsourcing that check
            var foreign = failing.Except(own).ToList();
            _out.WriteLine($"GATE server-tests: {server.Verdict} — SELF-REFERENCE POLICY: {foreign.Count} " +
                           "failing test(s) OUTSIDE Quality/** (other lanes' ownership, reported not fixed here):\n  " +
                           string.Join("\n  ", foreign));
        }

        _out.WriteLine($"full gate ladder completed in {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>Distinct failing test names from a gate evidence log (xunit's [FAIL] marker lines),
    /// parsed from the RECEIPT, not re-derived by hand — the harness wrote it, this reads it.</summary>
    private static List<string> FailingTestsIn(string evidencePath)
    {
        if (!File.Exists(evidencePath)) return [];
        return System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(evidencePath),
                @"\]\s+(Livora\.Server\.Tests\.[^\s\[]+)\s*\[FAIL\]")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
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
