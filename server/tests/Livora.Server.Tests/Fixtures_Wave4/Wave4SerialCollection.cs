namespace Livora.Server.Tests;

/// <summary>
/// PURPOSE: one serial xunit collection for everything that must own the CPU while it runs: the
///          Wave 4 performance budgets (latency sampling) and the release-gate harness (spawns real
///          dotnet build/test child processes). xunit parallelises COLLECTIONS, never classes
///          inside one collection — that is the mechanism this fixture exists to exploit.
/// OWNER: Agent 16 (P1-F, shared fixtures dir). Phase-2 lane perf/gate classes join with
///          [Collection(Wave4SerialCollection.Name)]; nothing else may serialise the suite —
///          over-serialising is its own performance bug, so joining requires a measured reason.
/// PROVIDES: <see cref="Name"/> (the collection key) and the <see cref="LivoraWebFixture"/> binding
///          (a fresh real host, in-memory provider, one per collection — the same semantics every
///          lane test already gets from IClassFixture, shared instead of cloned).
/// INVARIANTS:
///   - a dotnet build competing for the same cores inflates latency measurements — measured, see
///     docs/quality/wave4/FLAKE-STORAGE-BUDGET.md §2c — so a gate harness may never run alongside a
///     timing test, and two timing classes may never overlap
///   - the definition has NO members on purpose: the attributes ARE the behaviour
/// </summary>
[CollectionDefinition(Wave4SerialCollection.Name)]
public sealed class Wave4SerialCollection : ICollectionFixture<LivoraWebFixture>
{
    public const string Name = "wave4-serial";
}
