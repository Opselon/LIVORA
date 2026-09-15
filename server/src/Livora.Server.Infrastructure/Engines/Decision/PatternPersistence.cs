using Microsoft.EntityFrameworkCore;

namespace Livora.Server.Infrastructure.Engines.Decision;

// ============================================================================
// PURPOSE: the thin persistence ADAPTER for the intelligence engines. The engines
//          themselves have zero EF dependency (product law + lane rule); this file is the only
//          place patterns/dismissals touch the database, and it is deliberately small: rows in,
//          rows out, no logic.
// OWNER: Agent 10+11 (lane w4-p1e-engines). Entity classes live in THIS folder by contract;
//        the IModelContribution that attaches them to LivoraDbContext lives in the host module.
// INVARIANTS:
//   - text GUID("N") keys, UTC *AtUtc columns, explicit max lengths (core conventions)
//   - a dismissal is a SOFT delete of trust, not of data: the pattern itself is recomputed, so
//     the row only records "the user said no to this one" — deleting the row must bring the
//     pattern back (patterns are revisable and DELETABLE, product law)
// ============================================================================

/// <summary>"User dismissed this pattern finding" — keyed by the deterministic pattern id.</summary>
public sealed class PatternDismissalRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string UserId { get; set; } = "";
    /// <summary>Deterministic id the scanner emits (pat:&lt;kind&gt;:&lt;from&gt;..&lt;to&gt;).</summary>
    public string PatternId { get; set; } = "";
    /// <summary>PatternKind name, kept so a dismissal can be listed without parsing the id.</summary>
    public string Kind { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Hard-delete only via the account-deletion path; null otherwise (audit row).</summary>
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

/// <summary>Dismissal access. In-memory implementation exists so pure tests never need a DB.</summary>
public interface IPatternDismissalStore
{
    Task AddAsync(string userId, string patternId, string kind, CancellationToken ct = default);
    /// <summary>True when a row was removed. The pattern reappears on the next scan.</summary>
    Task<bool> RemoveAsync(string userId, string patternId, CancellationToken ct = default);
    Task<IReadOnlyList<PatternDismissalRecord>> ListAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// EF adapter over the shared context. NO business logic here — filtering and re-scanning belong
/// to the pure engines; this class only round-trips rows. It is typed against the base
/// <see cref="DbContext"/> on purpose: the host injects the real LivoraDbContext, while the
/// lane's EnsureCreated tests inject their own probe context that applies the SAME model
/// contributions — one adapter, honestly testable on both. (LivoraDbContext is frozen: no DbSet
/// property, so <c>Set&lt;T&gt;()</c> is the sanctioned access path for contribution types.)
/// </summary>
public sealed class EfPatternDismissalStore(DbContext db) : IPatternDismissalStore
{
    private DbSet<PatternDismissalRecord> Rows => db.Set<PatternDismissalRecord>();

    public async Task AddAsync(string userId, string patternId, string kind, CancellationToken ct = default)
    {
        var exists = await Rows.AnyAsync(
            p => p.UserId == userId && p.PatternId == patternId && p.DeletedAtUtc == null, ct);
        if (exists) return; // idempotent: dismissing twice is one dismissal
        Rows.Add(new PatternDismissalRecord { UserId = userId, PatternId = patternId, Kind = kind });
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveAsync(string userId, string patternId, CancellationToken ct = default)
    {
        var row = await Rows.SingleOrDefaultAsync(
            p => p.UserId == userId && p.PatternId == patternId && p.DeletedAtUtc == null, ct);
        if (row is null) return false;
        Rows.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PatternDismissalRecord>> ListAsync(string userId, CancellationToken ct = default)
        => await Rows
            .Where(p => p.UserId == userId && p.DeletedAtUtc == null)
            .OrderBy(p => p.CreatedAtUtc)
            .ToListAsync(ct);
}

/// <summary>Process-local store for tests and no-DB deployments (same semantics, no EF).</summary>
public sealed class InMemoryPatternDismissalStore : IPatternDismissalStore
{
    private readonly List<PatternDismissalRecord> _rows = [];
    private readonly object _gate = new();

    public Task AddAsync(string userId, string patternId, string kind, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.Any(r => r.UserId == userId && r.PatternId == patternId))
                _rows.Add(new PatternDismissalRecord { UserId = userId, PatternId = patternId, Kind = kind });
        }
        return Task.CompletedTask;
    }

    public Task<bool> RemoveAsync(string userId, string patternId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var row = _rows.SingleOrDefault(r => r.UserId == userId && r.PatternId == patternId);
            if (row is null) return Task.FromResult(false);
            _rows.Remove(row);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PatternDismissalRecord>> ListAsync(string userId, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<PatternDismissalRecord>>(
                _rows.Where(r => r.UserId == userId).OrderBy(r => r.CreatedAtUtc).ToList());
    }
}
