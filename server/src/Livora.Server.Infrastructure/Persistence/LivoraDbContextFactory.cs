using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Livora.Server.Infrastructure.Persistence;

/// <summary>
/// PURPOSE: let `dotnet ef migrations add|database update` work without booting the host, using a
///          local SQLite file. Design-time only: the running app always builds options via DI.
/// OWNER: Agent 02.
/// INVARIANTS: the design-time connection string is a LOCAL FILE and never a hosted DB — a
///             `dotnet ef database update` from a developer machine must not touch production.
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
