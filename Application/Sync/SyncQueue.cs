using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Sync;

/// <summary>
/// Wave 3c (lane 06): the durable outbox for local changes waiting on the (future) backend.
///
/// WHAT IS STORED — and what is NOT. Each queued entry carries only the frozen
/// <see cref="SyncEnvelope"/> facts (kind, id, version, <b>PayloadHash</b> = SHA-256 hex of the
/// entity's canonical JSON, changed-at) plus the payload size. The raw entity bytes never enter
/// the queue file: the entity itself lives durably in its own store, so a lost queue line can
/// always be rebuilt by rescanning that store, and wiping data never has to chase copies through a
/// second file. (Hash + size is exactly what a future backend needs for conflict detection without
/// us leaking content into a transport buffer or its logs.)
///
/// HONESTY CONTRACT — the part the UI reads:
/// <list type="bullet">
///   <item>No transport (or one reporting <see cref="ISyncTransport.IsConfigured"/> = false, the
///     only real case today): <see cref="DrainAsync"/> pushes nothing and every entry STAYS
///     Pending. The queue never marks Synced on a fake "success".</item>
///   <item><c>SyncState.Synced</c> is set only after a configured transport returned
///     Success for that exact batch.</item>
///   <item>A batch that comes back with conflicts moves its entries to Conflict — and there they
///     sit, untouched, until the user calls <see cref="ResolveConflictAsync"/> explicitly. This
///     class never resolves anything on its own, and a second drain does not re-push a Conflict
///     entry: divergence needs a human, not a retry loop.</item>
/// </list>
///
/// DURABILITY / CAP: an append-only journal (<c>livora_sync_queue.jsonl</c>) replayed at load;
/// FIFO cap of <see cref="Capacity"/> entries where the overflow drops the OLDEST queued lines and
/// increments <c>DroppedCount</c>. Dropped lines are a transport-buffer diagnostic only —
/// the entities they described keep living in their stores with their meta row still Pending, so no
/// user data is destroyed, ever. Reads after a drop stay honest because the meta index (the source
/// of truth for "is this synced?") is untouched.
/// </summary>
public sealed class SyncQueue : IDisposable
{
    /// <summary>FIFO cap for queued change-records (overflow drops the oldest QUEUES, not entities).</summary>
    public const int Capacity = 10_000;

    /// <summary>Max envelopes handed to one <see cref="ISyncTransport.PushAsync"/> call.</summary>
    public const int BatchSize = 200;

    /// <summary>Journal file name inside the queue directory.</summary>
    public const string JournalFileName = "livora_sync_queue.jsonl";

    private const string ReasonTransportNotConfigured = "Sync.Reason.TransportNotConfigured";

    private readonly string _dir;
    private readonly string _journalPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MetaIndex? _meta;
    private readonly Func<DateTime> _clock;

    // FIFO order = insertion order of keys; values are the live entry states.
    private readonly List<string> _order = new();
    private readonly Dictionary<string, QueueEntry> _entries = new(StringComparer.Ordinal);
    private long _dropped;
    private int _journalLines;
    private StreamWriter? _journal;
    private bool _disposed;

    private static readonly JsonSerializerOptions LineOptions = BuildLineOptions();

    private static JsonSerializerOptions BuildLineOptions()
    {
        var o = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { JsonPropertySort.SortByName },
            },
        };
        o.MakeReadOnly();
        return o;
    }

    /// <summary>One journal line: op + the transport-relevant fields only (never raw payload).</summary>
    private sealed class JournalLine
    {
        /// <summary>"e" enqueue / "s" state-change / "d" dropped-one diagnostic.</summary>
        public string Op { get; set; } = "e";
        public string? Key { get; set; }
        public string? Kind { get; set; }
        public string? Id { get; set; }
        public string? State { get; set; }
        public long Version { get; set; }
        public string? Hash { get; set; }
        public long Bytes { get; set; }
        public DateTime ChangedAtUtc { get; set; }
    }

    private sealed class QueueEntry
    {
        public required string Key { get; init; }
        public required string Kind { get; init; }
        public required string Id { get; init; }
        public required long Version { get; set; }
        public required string Hash { get; set; }
        public required long Bytes { get; set; }
        public required DateTime ChangedAtUtc { get; set; }
        public SyncState State { get; set; } = SyncState.Pending;

        public SyncEnvelope ToEnvelope() => new()
        {
            EntityKind = Kind,
            EntityId = Id,
            LocalState = State,
            LocalVersion = Version,
            PayloadHash = Hash,
            ChangedAtUtc = ChangedAtUtc,
        };
    }

    /// <param name="directoryPath">Dedicated directory for the journal (tests: a temp dir the
    /// caller owns and disposes; app: a subfolder of the data dir). Created on demand.</param>
    /// <param name="metaIndex">Optional link so queue transitions keep the sidecar metadata honest
    /// (Synced/Conflict land on the entity's meta row too). Null = queue-only bookkeeping.</param>
    /// <param name="clock">Injectable for deterministic tests; defaults to UTC.</param>
    public SyncQueue(string directoryPath, MetaIndex? metaIndex = null, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _dir = directoryPath;
        _journalPath = Path.Combine(directoryPath, JournalFileName);
        _meta = metaIndex;
        _clock = clock ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(directoryPath);
        ReplayJournal();
    }

    public string JournalPath => _journalPath;

    /// <summary>
    /// Read the journal while it is being appended to. The appender holds the file with
    /// FileShare.Read, which a plain <c>File.ReadAllLines</c> (share = ReadWrite) refuses, so live
    /// inspection — the diagnostics screen, a backup pass, tests — goes through this instead:
    /// open for read with FileShare.ReadWrite (never delete/truncate), read to the end, decode
    /// line by line. A half-written trailing line (flush racing the reader) is dropped, matching the
    /// replay path's tolerance for torn lines.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReadJournalLinesAsync(CancellationToken ct = default)
    {
        var lines = new List<string>();
        await using var fs = new FileStream(_journalPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1 << 15, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        string? line;
        while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            lines.Add(line);
        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]) && !lineLooksComplete(lines[^1]))
            lines.RemoveAt(lines.Count - 1); // a reader racing an append is not a corrupt journal
        return lines;

        static bool lineLooksComplete(string s) => s.EndsWith('}');
    }

    // ---- snapshot reads (UI-visible counters) ---------------------------------

    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.Count(e => e.Value.State == SyncState.Pending); }
        finally { _gate.Release(); }
    }

    public async Task<int> ConflictCountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.Count(e => e.Value.State == SyncState.Conflict); }
        finally { _gate.Release(); }
    }

    public async Task<int> SyncedCountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.Count(e => e.Value.State == SyncState.Synced); }
        finally { _gate.Release(); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.Count; }
        finally { _gate.Release(); }
    }

    /// <summary>Queued records dropped by the FIFO cap (diagnostic — never entity data).</summary>
    public async Task<long> DroppedCountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _dropped; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SyncEnvelope>> SnapshotPendingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _order.Where(k => _entries[k].State == SyncState.Pending).Select(k => _entries[k].ToEnvelope()).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<SyncState?> StateOfAsync(string entityKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return _entries.TryGetValue(entityKey, out var e) ? e.State : null; }
        finally { _gate.Release(); }
    }

    // ---- enqueue ---------------------------------------------------------------

    /// <summary>
    /// Queue one local change from its envelope (the frozen <see cref="SyncEnvelope"/> carries
    /// kind/id/version/PayloadHash — never a raw payload, so nothing here could store one).
    /// <paramref name="payloadBytes"/> records the size for diagnostics only.
    /// Re-enqueuing a key that a user just edited again refreshes that entry in place: a fresh
    /// local edit legitimately re-arms the push (Pending). That is the write path speaking, not
    /// the queue resolving anything — the queue itself never leaves <see cref="SyncState.Conflict"/>
    /// except through <see cref="ResolveConflictAsync"/> or a new user edit.
    /// </summary>
    public Task<SyncEnvelope> EnqueueAsync(SyncEnvelope envelope, long payloadBytes = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return EnqueueAsync(envelope.EntityKind, envelope.EntityId, envelope.LocalVersion,
            envelope.PayloadHash, payloadBytes, ct);
    }

    public async Task<SyncEnvelope> EnqueueAsync(
        string entityKind, string entityId, long localVersion, string payloadHash,
        long payloadBytes, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        return await EnqueueCoreAsync(new QueueEntry
        {
            Key = EntityMeta.KeyOf(entityKind, entityId),
            Kind = entityKind,
            Id = entityId,
            Version = localVersion,
            Hash = payloadHash,
            Bytes = payloadBytes,
            ChangedAtUtc = _clock(),
            State = SyncState.Pending,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Convenience for callers that already hold the entity: hashes canonical JSON here.</summary>
    public Task<SyncEnvelope> EnqueueEntityAsync(string entityKind, string entityId, object entityPayload, CancellationToken ct = default)
    {
        var canonical = CanonicalJson.Serialize(entityPayload);
        return EnqueueAsync(entityKind, entityId,
            localVersion: 0, payloadHash: CanonicalJson.Sha256HexOfCanonical(canonical),
            payloadBytes: Encoding.UTF8.GetByteCount(canonical), ct);
    }

    private async Task<SyncEnvelope> EnqueueCoreAsync(QueueEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            if (_entries.TryGetValue(entry.Key, out var existing))
            {
                existing.Version = entry.Version;
                existing.Hash = entry.Hash;
                existing.Bytes = entry.Bytes;
                existing.ChangedAtUtc = entry.ChangedAtUtc;
                existing.State = SyncState.Pending;
                await AppendLineAsync(new JournalLine { Op = "e", Key = entry.Key, Kind = entry.Kind, Id = entry.Id, State = nameof(SyncState.Pending), Version = entry.Version, Hash = entry.Hash, Bytes = entry.Bytes, ChangedAtUtc = entry.ChangedAtUtc }, ct).ConfigureAwait(false);
                return existing.ToEnvelope();
            }

            while (_entries.Count >= Capacity)
            {
                // FIFO: the OLDEST queued change record leaves first (its entity is untouched in
                // its own store and its meta row stays Pending — see class docs).
                var oldest = _order[0];
                _order.RemoveAt(0);
                _entries.Remove(oldest);
                _dropped++;
                // The dropped KEY goes in the line: a replay must rebuild exactly the post-drop
                // state, or a restart would resurrect records the cap had already evicted.
                await AppendLineAsync(new JournalLine { Op = "d", Key = oldest, ChangedAtUtc = _clock() }, ct).ConfigureAwait(false);
            }

            _order.Add(entry.Key);
            _entries[entry.Key] = entry;
            await AppendLineAsync(new JournalLine { Op = "e", Key = entry.Key, Kind = entry.Kind, Id = entry.Id, State = nameof(SyncState.Pending), Version = entry.Version, Hash = entry.Hash, Bytes = entry.Bytes, ChangedAtUtc = entry.ChangedAtUtc }, ct).ConfigureAwait(false);
            return entry.ToEnvelope();
        }
        finally { _gate.Release(); }
    }

    // ---- drain -----------------------------------------------------------------

    /// <summary>
    /// Push everything Pending, in batches of <see cref="BatchSize"/>. With no configured
    /// transport this is a visible no-op: nothing is pushed, nothing changes state, and the report
    /// says why (<see cref="DrainReport.TransportConfigured"/> = false + reason key) so the UI can
    /// say "still pending — no sync backend exists" instead of pretending.
    /// </summary>
    public async Task<DrainReport> DrainAsync(ISyncTransport? transport, CancellationToken ct = default)
    {
        var report = new DrainReport();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            var pendingKeys = _order.Where(k => _entries[k].State == SyncState.Pending).ToList();
            report.PendingAtStart = pendingKeys.Count;

            if (transport is null || !transport.IsConfigured)
            {
                report.TransportConfigured = false;
                report.ReasonKey = ReasonTransportNotConfigured;
                report.RemainedPending = pendingKeys.Count;
                return report; // honest no-op: everything keeps Pending
            }
            report.TransportConfigured = true;

            foreach (var chunk in pendingKeys.Chunk(BatchSize))
            {
                ct.ThrowIfCancellationRequested();
                var batch = chunk.Select(k => _entries[k]).ToList();
                SyncPushResult result;
                try
                {
                    result = await transport.PushAsync(batch.Select(e => e.ToEnvelope()).ToList(), ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    report.FailedBatches++;
                    report.ErrorCategory = ex.GetType().Name;
                    break; // stays Pending on every entry of this and later batches
                }

                if (!result.Success)
                {
                    report.FailedBatches++;
                    report.ErrorCategory = result.ErrorCategory ?? "unknown";
                    break; // no point hammering a failing backend inside one drain
                }

                // Frozen contract: SyncPushResult carries ConflictKinds without entity ids, so a
                // conflicted batch cannot attribute a conflict to one entry. Honest reading: the
                // WHOLE batch diverged → its entries move to Conflict together; only a clean
                // success marks Synced.
                bool conflicted = result.Conflicts.Count > 0;
                foreach (var e in batch)
                {
                    var next = conflicted ? SyncState.Conflict : SyncState.Synced;
                    e.State = next;
                    report.Pushed++;
                    if (conflicted) report.Conflicted += 1; else report.Synced += 1;
                    await AppendLineAsync(new JournalLine { Op = "s", Key = e.Key, State = next.ToString(), ChangedAtUtc = _clock() }, ct).ConfigureAwait(false);
                    // The sidecar must tell the same story as the queue — the UI reads meta, not the journal.
                    if (_meta is not null)
                        await _meta.SetStateAsync(e.Kind, e.Id, next, ct).ConfigureAwait(false);
                }
            }
            report.RemainedPending = _entries.Count(e => e.Value.State == SyncState.Pending);
            return report;
        }
        finally { _gate.Release(); }
    }

    // ---- explicit conflict resolution -------------------------------------------

    /// <summary>
    /// Resolve ONE conflicted entity — only ever called from an explicit user choice.
    /// <c>keepLocal = true</c>: the local edit wins; the entry goes back to Pending so the next
    /// drain re-pushes the local version. <c>keepLocal = false</c>: remote wins; the local queued
    /// change is discarded and the entity's meta moves to Clean (the user's data file itself is
    /// NOT touched here — a remote-wins resolution means "replace local with what the backend
    /// sends", which only a real backend can do; until then this marks intent, and the queue stops
    /// pushing).
    /// Returns false when the key has no Conflict entry (nothing implicit, nothing invented).
    /// </summary>
    public async Task<bool> ResolveConflictAsync(string entityKey, bool keepLocal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityKey);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            if (!_entries.TryGetValue(entityKey, out var entry) || entry.State != SyncState.Conflict)
                return false;

            if (keepLocal)
            {
                entry.State = SyncState.Pending;
                await AppendLineAsync(new JournalLine { Op = "s", Key = entityKey, State = nameof(SyncState.Pending), ChangedAtUtc = _clock() }, ct).ConfigureAwait(false);
                if (_meta is not null)
                    await _meta.SetStateAsync(entry.Kind, entry.Id, SyncState.Pending, ct).ConfigureAwait(false);
            }
            else
            {
                _order.Remove(entityKey);
                _entries.Remove(entityKey);
                await AppendLineAsync(new JournalLine { Op = "x", Key = entityKey, State = nameof(SyncState.Clean), ChangedAtUtc = _clock() }, ct).ConfigureAwait(false);
                if (_meta is not null)
                    await _meta.SetStateAsync(entry.Kind, entry.Id, SyncState.Clean, ct).ConfigureAwait(false);
            }
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drop every queued record for one entity (entity deleted / privacy wipe of one row).</summary>
    public async Task<bool> RemoveAsync(string entityKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            if (!_entries.Remove(entityKey)) return false;
            _order.Remove(entityKey);
            await AppendLineAsync(new JournalLine { Op = "x", Key = entityKey, State = nameof(SyncState.Clean), ChangedAtUtc = _clock() }, ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Erase the whole queue (privacy wipe): journal file + in-memory state.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            _entries.Clear();
            _order.Clear();
            _dropped = 0;
            _journalLines = 0;
            _journal?.Dispose();
            _journal = null;
            SidecarIo.Delete(_journalPath);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Push everything Pending WITHOUT the queue changing state — read-only probe used by the
    /// advanced-mode UI to answer "what would go out?" (the envelopes it shows are the same ones
    /// DrainAsync would push; no side effects).
    /// </summary>
    public async Task<IReadOnlyList<IReadOnlyList<SyncEnvelope>>> PeekBatchesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            return _order.Where(k => _entries[k].State == SyncState.Pending)
                .Select(k => _entries[k].ToEnvelope())
                .Chunk(BatchSize)
                .Select(c => (IReadOnlyList<SyncEnvelope>)c.ToList())
                .ToList();
        }
        finally { _gate.Release(); }
    }

    // ---- journal plumbing (all under _gate except ctor-replay) ------------------

    /// <summary>Enqueue lines ride a bounded buffer (flush every N); state-transition lines always
    /// flush — see class docs' durability note for why that asymmetry is honest, not lossy.</summary>
    internal const int EnqueueFlushInterval = 1000;
    private int _sinceFlush;

    private async Task AppendLineAsync(JournalLine line, CancellationToken ct)
    {
        _journal ??= OpenAppender();
        await _journal.WriteLineAsync(JsonSerializer.Serialize(line, LineOptions).AsMemory(), ct).ConfigureAwait(false);
        // Flush discipline: transitions ("s"/"x"/"d"/"c") are rare and answer the UI's "what happened"
        // question, so they always reach the disk before we return. Plain enqueues ("e") are the hot
        // path (10k during one session is normal) and ride a bounded buffer: at most
        // <see cref="EnqueueFlushInterval"/> unflushed enqueue lines exist at any moment, and the
        // loss window is bookkeeping only — the entity's meta row already says Pending on disk, and
        // a queue record is reconstructible by rescanning the stores (class docs).
        bool mustFlush = line.Op != "e" || ++_sinceFlush >= EnqueueFlushInterval;
        if (mustFlush)
        {
            _sinceFlush = 0;
            await _journal.FlushAsync(ct).ConfigureAwait(false);
        }
        if (++_journalLines > Capacity * 4 + 1000)
            await CompactAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Force every buffered enqueue line to disk (checkpoint before backgrounding / tests).</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedThrow();
            if (_journal is not null && _sinceFlush > 0)
            {
                _sinceFlush = 0;
                await _journal.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private StreamWriter OpenAppender()
    {
        // FileShare.ReadWrite|Delete: the journal stays inspectable (backup tools, the advanced-mode
        // diagnostics screen, tests) and deletable by ResetAll while the appender is open; append
        // serialization is the _gate's job, not the OS share mask's.
        // Readers get FileShare.Read semantics through a share-deny-none window: use
        // FileShare.ReadWrite | FileShare.Delete so the journal stays inspectable while we append
        // (backup tools, the advanced-mode diagnostics screen and tests all read it live); append
        // serialization is the _gate's job, not the OS share mask's.
        var fs = new FileStream(_journalPath, FileMode.Append, FileAccess.Write,
            FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous);
        return new StreamWriter(fs, new UTF8Encoding(false));
    }

    private async Task CompactAsync(CancellationToken ct)
    {
        // Rewrite the journal as enqueue/state lines for live entries only (dropped markers and
        // superseded transitions vanish). Atomic: write temp, then replace.
        var tmp = _journalPath + ".compact-" + Guid.NewGuid().ToString("N")[..8];
        _journal?.Flush();
        _journal?.Dispose();
        _journal = null;
        try
        {
            await using (var w = new StreamWriter(new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous), new UTF8Encoding(false)))
            {
                foreach (var key in _order)
                {
                    var e = _entries[key];
                    await w.WriteLineAsync(JsonSerializer.Serialize(new JournalLine { Op = "e", Key = e.Key, Kind = e.Kind, Id = e.Id, State = nameof(SyncState.Pending), Version = e.Version, Hash = e.Hash, Bytes = e.Bytes, ChangedAtUtc = e.ChangedAtUtc }, LineOptions)).ConfigureAwait(false);
                    if (e.State != SyncState.Pending)
                        await w.WriteLineAsync(JsonSerializer.Serialize(new JournalLine { Op = "s", Key = e.Key, State = e.State.ToString(), ChangedAtUtc = _clock() }, LineOptions)).ConfigureAwait(false);
                }
                if (_dropped > 0)
                    await w.WriteLineAsync(JsonSerializer.Serialize(new JournalLine { Op = "c", Version = _dropped, ChangedAtUtc = _clock() }, LineOptions)).ConfigureAwait(false);
                await w.FlushAsync().ConfigureAwait(false);
            }
            if (File.Exists(_journalPath)) File.Replace(tmp, _journalPath, null);
            else File.Move(tmp, _journalPath);
            _journalLines = _order.Count * 2 + 1;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    private void ReplayJournal()
    {
        if (!File.Exists(_journalPath)) return;
        // Load runs single-threaded in the ctor, before any _gate contention exists.
        foreach (var line in File.ReadLines(_journalPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalLine? rec;
            try { rec = JsonSerializer.Deserialize<JournalLine>(line, LineOptions); }
            catch (JsonException) { continue; } // one torn line must not kill the whole queue
            if (rec is null) continue;
            switch (rec.Op)
            {
                case "e":
                    // A line that parses but lacks the identity fields cannot be replayed; skipping it
                    // is honest (the entity's meta row still says Pending — nothing is lost but a
                    // transport buffer record), crashing the load would be worse.
                    if (string.IsNullOrEmpty(rec.Key) || string.IsNullOrEmpty(rec.Kind)
                        || string.IsNullOrEmpty(rec.Id) || string.IsNullOrEmpty(rec.Hash)) break;
                    var entry = new QueueEntry { Key = rec.Key, Kind = rec.Kind, Id = rec.Id, Version = rec.Version, Hash = rec.Hash, Bytes = rec.Bytes, ChangedAtUtc = rec.ChangedAtUtc, State = SyncState.Pending };
                    if (!_entries.ContainsKey(entry.Key)) _order.Add(entry.Key);
                    _entries[entry.Key] = entry;
                    break;
                case "s":
                    if (_entries.TryGetValue(rec.Key!, out var cur) && Enum.TryParse<SyncState>(rec.State, out var st))
                        cur.State = st;
                    break;
                case "x":
                    if (_entries.Remove(rec.Key!)) _order.Remove(rec.Key!);
                    break;
                case "d":
                    _dropped++;
                    if (!string.IsNullOrEmpty(rec.Key) && _entries.Remove(rec.Key!))
                        _order.Remove(rec.Key!);
                    break;
                case "c":
                    _dropped = rec.Version;
                    break;
            }
            _journalLines++;
        }
    }

    private void ObjectDisposedThrow()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncQueue));
    }

    public void Dispose()
    {
        // StreamWriter.Dispose flushes the managed buffer, so buffered enqueue lines land before close.
        _journal?.Dispose();
        _journal = null;
        _disposed = true;
    }
}

/// <summary>What one <see cref="SyncQueue.DrainAsync"/> actually did — the UI shows exactly this.</summary>
public sealed class DrainReport
{
    public bool TransportConfigured { get; internal set; }
    /// <summary>Localization key explaining why nothing moved (set when no transport exists).</summary>
    public string? ReasonKey { get; internal set; }
    public int PendingAtStart { get; internal set; }
    public int Pushed { get; internal set; }
    public int Synced { get; internal set; }
    public int Conflicted { get; internal set; }
    public int FailedBatches { get; internal set; }
    public string? ErrorCategory { get; internal set; }
    public int RemainedPending { get; internal set; }
    /// <summary>True when this drain changed nothing because no backend exists (never call it success).</summary>
    public bool IsHonestNoOp => !TransportConfigured || (Pushed == 0 && FailedBatches == 0);
}
