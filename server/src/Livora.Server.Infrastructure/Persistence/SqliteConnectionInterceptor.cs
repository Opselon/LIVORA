using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Livora.Server.Infrastructure.Persistence;

/// <summary>
/// PURPOSE: SQLite ignores FK constraints unless each connection asks. This turns them on and
///          enables WAL so the API can read while a sync batch writes.
/// OWNER: Agent 02.
/// INVARIANTS: runs on connection open (per-connection pragma), so it holds for pooled connections.
/// </summary>
public sealed class SqliteConnectionInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        Prepare(cmd);
        cmd.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        Prepare(cmd);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Prepare(DbCommand cmd)
    {
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
    }
}
