using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Livora.Server.Infrastructure.Persistence;

/// <summary>
/// PURPOSE: let `dotnet ef migrations add|database update` work without booting the host, using a
///          local SQLite file. Design-time only: the running app always builds options via DI.
/// OWNER: Agent 02 + lead.
/// INVARIANTS: the design-time connection string is a LOCAL FILE and never a hosted DB — a
///             `dotnet ef database update` from a developer machine must not touch production.
/// NOTE: model contributions are registered by the HOST's design-time factory
///       (server/src/Livora.Server/DesignTimeContributions.cs), because the feature contributions
///       live in the host assembly and Infrastructure must not reference it. If both factories
///       exist for the same context, dotnet-ef asks for --context; keep this one as the fallback
///       for a pure-schema run (core tables only).
/// </summary>
public sealed class LivoraDbContextFactory : IDesignTimeDbContextFactory<LivoraDbContext>
{
    public LivoraDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LivoraDbContext>()
            .UseSqlite("Data Source=livora-designtime.db")
            .Options;
        return new LivoraDbContext(options);
    }
}
