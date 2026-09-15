using Livora.Server.Modules.Platform;

namespace Livora.Server.Tests.Sync;

/// <summary>
/// PURPOSE: one live assertion about the provider the Sync suites actually run on — the sync
///          guarantees (unique indexes, transactions) are only as real as the provider underneath,
///          and the frozen fixture switched from the in-memory provider to a per-test SQLite FILE.
///          This test PINS that fact so a future fixture edit that quietly drops back to
///          <c>inmemory</c> turns the sync suite red here instead of silently un-enforcing
///          constraints elsewhere. (It replaced a scratch probe that printed and asserted nothing.)
/// OWNER: Agent 02 (lane w4-p1b-platform) / repair lane R2.
/// INVARIANTS: no fake OK — the state asserted is the state the platform probe reports.
/// </summary>
public sealed class SyncProviderPinTests : LivoraApiTest
{
    public SyncProviderPinTests(LivoraWebFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Sync_tests_run_on_the_sqlite_provider_the_platform_probe_reports()
    {
        var snap = await Fixture.GetAsync<CapabilitySnapshot>("/api/v1/platform/capabilities");

        Assert.Equal("sqlite", snap.Database.Capabilities["provider"].ToLowerInvariant());
        Assert.Equal("ok", snap.Database.State);   // probed, not claimed: the DB answered a query
    }
}
