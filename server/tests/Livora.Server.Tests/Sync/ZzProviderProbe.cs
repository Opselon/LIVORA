using Livora.Server.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Tests.Sync;

public sealed class ZzProviderProbe : LivoraApiTest
{
    public ZzProviderProbe(LivoraWebFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Probe()
    {
        var snap = await Fixture.GetAsync<Livora.Server.Modules.Platform.CapabilitySnapshot>(
            "/api/v1/platform/capabilities");
        Console.WriteLine("DBPROVIDER:" + snap.Database.Capabilities["provider"] + " state=" + snap.Database.State);
        Assert.True(true);
    }
}
