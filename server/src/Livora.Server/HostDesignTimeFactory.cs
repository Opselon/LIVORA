using Livora.Server.Infrastructure.Engines.Pipeline.Persistence;
using Livora.Server.Infrastructure.Identity;
using Livora.Server.Infrastructure.Persistence;
using Livora.Server.Infrastructure.Sync;
using Livora.Server.Modules.Intelligence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Livora.Server;

/// <summary>
/// PURPOSE: design-time DbContext creation for `dotnet ef` from the HOST project, so the generated
///          migration contains the SAME model the running host has: the feature lanes' model
///          contributions register during host service registration, which design-time never runs.
///          Without this file, `migrations add` silently produces an empty migration and lane tables
///          never exist (caught at Wave 4 P1 integration when the sync/engines DB tests failed).
/// OWNER: lead (integration). Contribution list must mirror each module's ConfigureServices.
/// PROVIDES: dotnet ef --project server/src/Livora.Server.Infrastructure --startup-project server/src/Livora.Server
/// INVARIANTS: contributions added exactly once, registry frozen after, local file DB only.
/// </summary>
public sealed class HostDesignTimeFactory : IDesignTimeDbContextFactory<LivoraDbContext>
{
    public LivoraDbContext CreateDbContext(string[] args)
    {
        ModelContributionRegistry.ResetForTests(); // design-time process, nothing built yet
        ModelContributionRegistry.Add(new SyncModelContribution());
        IdentityModelContribution.EnsureRegistered();          // lane-provided idempotent gate (R-p1c-1 fold-in)
        ModelContributionRegistry.Add(new EnginesModelContribution());
        ModelContributionRegistry.Add(new IntelligenceModelContribution());
        ModelContributionRegistry.Add(new VerificationModelContribution());
        ModelContributionRegistry.Freeze();

        var options = new DbContextOptionsBuilder<LivoraDbContext>()
            .UseSqlite("Data Source=livora-designtime-host.db")
            .Options;
        return new LivoraDbContext(options);
    }
}
