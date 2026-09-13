using LIVORA.Domain.Enums;

namespace LIVORA.Application.Sync;

/// <summary>
/// Wave 3c (lane 06): the sync metadata that travels WITH one stored entity but is deliberately
/// kept OUT of the entity's own JSON (a sidecar — <see cref="MetaIndex"/>). Storing it separately
/// means adding sync bookkeeping never changes the bytes of a user's goal/history record, so
/// existing files keep loading in every layer that reads them (Rule 21: evolve by migration, not by
/// rewriting the model).
///
/// Semantics the UI depends on:
/// <list type="bullet">
///   <item><see cref="Version"/> is a LOCAL monotonic counter, not a backend revision. Absent meta
///     reads as version 0 / <see cref="SyncState.Clean"/> (see <see cref="Absent"/>) — legacy files
///     are never treated as dirty just because they predate the metadata layer.</item>
///   <item><see cref="Bump"/> is what every write path calls: it stamps UpdatedAtUtc, +1s Version
///     and moves the record to <see cref="SyncState.Pending"/>. Nothing else may set Pending.</item>
///   <item><see cref="SyncState.Synced"/> may only be set after a transport actually confirmed the
///     push (see <see cref="SyncQueue.DrainAsync"/>). No transport => state stays Pending, forever,
///     and the UI shows that.</item>
/// </list>
/// </summary>
public sealed record EntityMeta
{
    /// <summary>When this entity was first seen by the metadata layer (UTC).</summary>
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>When it last changed locally (UTC).</summary>
    public DateTime UpdatedAtUtc { get; init; }

    /// <summary>Local change counter. 0 means "never bumped by this layer" (legacy data).</summary>
    public long Version { get; init; }

    /// <summary>Lifecycle vs the (future) backend.</summary>
    public SyncState SyncState { get; init; }

    /// <summary>
    /// The value a store returns for an entity that has no metadata row: version 0, Clean. This is
    /// the "legacy file" reading — pre-wave-3c data is intact and quiet, not pending.
    /// </summary>
    public static EntityMeta Absent { get; } = new()
    {
        CreatedAtUtc = DateTime.MinValue,
        UpdatedAtUtc = DateTime.MinValue,
        Version = 0,
        SyncState = SyncState.Clean,
    };

    /// <summary>True when no metadata row exists for the entity (so stores can skip writing).</summary>
    public bool IsAbsent => Version == 0 && SyncState == SyncState.Clean && CreatedAtUtc == DateTime.MinValue;

    /// <summary>
    /// The write-path helper: stamp "changed now", advance the local version, and mark the entity
    /// Pending. CreatedAtUtc survives (first write keeps the original creation stamp).
    /// </summary>
    public EntityMeta Bump(DateTime nowUtc) => this with
    {
        CreatedAtUtc = CreatedAtUtc == DateTime.MinValue ? nowUtc : CreatedAtUtc,
        UpdatedAtUtc = nowUtc,
        Version = Version + 1,
        SyncState = SyncState.Pending,
    };

    /// <summary>
    /// Set the sync lifecycle without touching the version (used by a drain that confirmed or
    /// conflicted a push — the local version counts local edits, not transport attempts).
    /// </summary>
    public EntityMeta WithState(SyncState state) => this with { SyncState = state };

    /// <summary>Sidecar key grammar: <c>kind:id</c>. One place so no caller can drift on format.</summary>
    public static string KeyOf(string kind, string id) =>
        $"{kind ?? throw new ArgumentNullException(nameof(kind))}:{id ?? throw new ArgumentNullException(nameof(id))}";
}
