using System.ComponentModel.DataAnnotations;

namespace Livora.Server.Application;

/// <summary>
/// PURPOSE: the one pagination/filter request shape every list endpoint accepts, so 13 lanes cannot
///          each invent their own and the client cannot special-case them.
/// OWNER: Agent 01 (contract, frozen). Handlers read it; they do not extend it ad hoc.
/// PROVIDES: bounded page size, cursor-free offset paging, a stable default sort contract.
/// INVARIANTS:
///   - <see cref="Limit"/> is clamped, never trusted: an unbounded list request is a DoS and a cost
///     leak (Wave 4 §52), so a huge value becomes <see cref="MaxLimit"/> rather than an error.
///   - offset paging only on ordered, bounded sets; anything scanning unbounded history must use the
///     time-window fields instead of a deep OFFSET.
/// </summary>
public sealed class PageRequest
{
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;

    [Range(0, 1_000_000)]
    public int Offset { get; init; }

    [Range(1, MaxLimit)]
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Optional ISO-8601 window; required by history-style endpoints.</summary>
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Free-text exact/prefix match. Semantic ranking is a separate concern (search module).</summary>
    [StringLength(200)]
    public string? Query { get; init; }

    /// <summary>Clamp without throwing: a client asking for 5000 gets 100, not a 400.</summary>
    public int SafeLimit => Limit is <= 0 or > MaxLimit ? DefaultLimit : Limit;
}

/// <summary>Envelope every list endpoint returns. Never a bare array (no room to say "more exist").</summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Offset,
    int Limit,
    long TotalCount,
    bool HasMore)
{
    public static PagedResult<T> Of(IReadOnlyList<T> page, PageRequest req, long total)
        => new(page, req.Offset, req.SafeLimit, total, req.Offset + page.Count < total);
}
