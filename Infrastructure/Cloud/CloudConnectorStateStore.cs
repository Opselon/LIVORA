using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Cloud;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Infrastructure.Cloud;

/// <summary>
/// WAVE 4 P1-D — the durable bookkeeping the connector state machine projects from: the last
/// round-trip facts and the incremental-pull watermark. One JSON file per concern under the same
/// <c>cloud/</c> directory the settings file uses (<see cref="CloudApiOptions.CloudDirName"/>).
///
/// WHAT MAY LIVE HERE (and what may not): timestamps, booleans, §5c machine codes, correlation ids,
/// revision watermarks. NEVER tokens, emails, entity payloads, or server prose — the file shapes are
/// the <see cref="ConnectorRecordFile"/> / <see cref="SyncCursorFile"/> types below and the mirror
/// test asserts their property names contain no credential-shaped field, so a future addition cannot
/// quietly break product law §0.5 (correlation ids yes; content no) at the storage boundary.
///
/// Restart semantics: the record survives, so "the server confirmed at 14:02" stays true across a
/// launch (erasing it would be its own small lie); the in-memory capabilities snapshot deliberately
/// does NOT survive, so a restarted app must re-probe before it renders anything about modules.
/// </summary>
public sealed class CloudConnectorStateStore
{
    /// <summary>The connector's round-trip record file.</summary>
    public const string RecordFileName = "connector-record.json";
    /// <summary>The pull watermark file.</summary>
    public const string CursorFileName = "sync-cursor.json";

    private readonly LocalJsonStore _store;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    public CloudConnectorStateStore(LocalJsonStore store, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = timeProvider ?? TimeProvider.System;
    }

    // ---- shapes (property names are the on-disk contract; keep them credential-free) ----------

    /// <summary>What the connector remembers about the last attempt. Public so the schema is checkable.</summary>
    public sealed class ConnectorRecordFile
    {
        public DateTimeOffset? LastAttemptAtUtc { get; set; }
        public bool LastAttemptReachedServer { get; set; }
        public string? LastErrorReasonKey { get; set; }
        public string? LastCode { get; set; }
        public string? LastCorrelationId { get; set; }
        public DateTimeOffset? LastGoodAnswerAtUtc { get; set; }
        public DateTimeOffset? LastConfirmedAtUtc { get; set; }
    }

    /// <summary>The incremental-pull watermark.</summary>
    public sealed class SyncCursorFile
    {
        public long? LatestRevision { get; set; }
        public DateTimeOffset? LastPullAtUtc { get; set; }
    }

    // ---- record ---------------------------------------------------------------

    public CloudConnectorRecord ReadRecord()
    {
        lock (_gate)
        {
            var f = Read<ConnectorRecordFile>(RecordFileName);
            return new CloudConnectorRecord
            {
                LastAttemptAtUtc = f?.LastAttemptAtUtc,
                LastAttemptReachedServer = f?.LastAttemptReachedServer ?? false,
                LastErrorReasonKey = f?.LastErrorReasonKey,
                LastCorrelationId = f?.LastCorrelationId,
                LastGoodAnswerAtUtc = f?.LastGoodAnswerAtUtc,
                LastConfirmedAtUtc = f?.LastConfirmedAtUtc,
                LastCode = f?.LastCode,
            };
        }
    }

    /// <summary>The record's file shape, straight off disk (the mirror test reads the property names).</summary>
    public ConnectorRecordFile ReadRecordFile()
    {
        lock (_gate) return Read<ConnectorRecordFile>(RecordFileName) ?? new ConnectorRecordFile();
    }

    public SyncCursorFile ReadCursorFile()
    {
        lock (_gate) return Read<SyncCursorFile>(CursorFileName) ?? new SyncCursorFile();
    }

    /// <summary>
    /// Record the outcome of one completed attempt. <paramref name="httpStatus"/> is 0 when nothing
    /// answered (the offline case), which is precisely what separates Offline from Failed.
    /// </summary>
    public CloudConnectorRecord RecordAttempt(
        bool succeeded, string? code, string? reasonKey, string? correlationId, int httpStatus,
        bool confirmedOperations = false)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var f = Read<ConnectorRecordFile>(RecordFileName) ?? new ConnectorRecordFile();
            f.LastAttemptAtUtc = now;
            f.LastAttemptReachedServer = httpStatus > 0;
            f.LastCorrelationId = succeeded ? null : correlationId;
            f.LastCode = succeeded ? null : code;
            f.LastErrorReasonKey = succeeded ? null : reasonKey;
            if (succeeded) f.LastGoodAnswerAtUtc = now;      // the host answered this device, well
            if (confirmedOperations) f.LastConfirmedAtUtc = now;   // ...and it confirmed QUEUED work
            Write(RecordFileName, f);
        }
        return ReadRecord();
    }

    // ---- cursor ---------------------------------------------------------------

    public CloudSyncCursor ReadCursor()
    {
        lock (_gate)
        {
            var f = Read<SyncCursorFile>(CursorFileName);
            return new CloudSyncCursor { LatestRevision = f?.LatestRevision, LastPullAtUtc = f?.LastPullAtUtc };
        }
    }

    public CloudSyncCursor SaveCursor(long latestRevision)
    {
        lock (_gate)
        {
            var f = Read<SyncCursorFile>(CursorFileName) ?? new SyncCursorFile();
            // The watermark only ever moves forward: a server bug or clock skew cannot rewind it into
            // re-pulling (and re-counting) history this device already saw.
            if (f.LatestRevision is null || latestRevision > f.LatestRevision.Value)
                f.LatestRevision = latestRevision;
            f.LastPullAtUtc = _time.GetUtcNow();
            Write(CursorFileName, f);
        }
        return ReadCursor();
    }

    /// <summary>
    /// Forget everything (sign-out / account deletion / privacy wipe): a new account on this device
    /// must not inherit "last confirmed at" or the previous account's pull watermark.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _store.Delete(RecordFileName);
            _store.Delete(CursorFileName);
        }
    }

    /// <summary>Short hash of the two bookkeeping files (diagnostics; never the content itself).</summary>
    public string StateFingerprint()
    {
        lock (_gate)
        {
            var raw = (_store.ReadRaw(RecordFileName) ?? "") + "|" + (_store.ReadRaw(CursorFileName) ?? "");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..12].ToLowerInvariant();
        }
    }

    // ---- io ---------------------------------------------------------------------

    private T? Read<T>(string file) where T : class
    {
        var raw = _store.ReadRaw(file);
        if (raw is null) return null;
        try { return JsonSerializer.Deserialize<T>(raw, Options); }
        catch (JsonException) { return null; }   // corrupt bookkeeping = "never happened", repaired on next write
    }

    private void Write<T>(string file, T value) =>
        _store.WriteRawAtomic(file, JsonSerializer.Serialize(value, Options));

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
};
}
