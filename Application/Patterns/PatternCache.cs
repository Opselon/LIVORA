using LIVORA.Application.Abstractions;
using LIVORA.Domain.Models.History;

namespace LIVORA.Application.Patterns;

/// <summary>
/// Result-set cache for pattern scans (Wave 3c lane 04). A pattern scan walks the whole
/// history several times (7 detectors); re-running it on every screen refresh is wasted CPU
/// and — worse — makes findings flicker. The cache keys on data IDENTITY
/// (<see cref="KeyFor"/>: record count + newest record date): while the key is unchanged the
/// repository is not read again and the stored result set is returned verbatim.
///
/// Bounded: at most <see cref="MaxResultSets"/> result sets live at once (LRU — the least
/// recently used key is evicted when a sixth distinct key arrives). Thread-safe: every state
/// mutation happens under one lock; the compute itself is pure CPU and runs inside the lock
/// ONLY for the duration of one scan (no async work is held under the lock — repository reads
/// happen outside it before the key is known).
/// </summary>
public sealed class PatternCache
{
    /// <summary>Hard bound on simultaneously cached result sets.</summary>
    public const int MaxResultSets = 5;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Cached>> _index = new();
    private readonly LinkedList<Cached> _lru = new();   // head = most recently used
    private readonly Dictionary<string, Task<PatternScanResult>> _inFlight = new();

    private sealed record Cached(string Key, PatternScanResult Result);

    /// <summary>Number of LRU evictions so far (tests pin this — silent eviction hides bugs).</summary>
    public int EvictionCount { get; private set; }

    /// <summary>Keys currently cached, most-recent-first (diagnostics + tests).</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return _lru.Select(c => c.Key).ToList(); }
    }

    /// <summary>The canonical cache key: record count + newest record date (UTC ticks, stable).
    /// Deliberately NOT the whole content hash — the same data shape the engines key their
    /// day-windows off. A same-day edit that changes neither count nor newest date reuses the
    /// cached set; that is the documented staleness contract of this seam.</summary>
    public static string KeyFor(IReadOnlyList<DailyHistoryRecord> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        DateTime newest = history.Count == 0
            ? default
            : history.Select(r => r.Date).Max().ToUniversalTime();
        return $"{history.Count}|{newest:O}";
    }

    /// <summary>
    /// Return the scan for <paramref name="key"/>, computing it from the repository only on a
    /// miss. On a hit the repository is NEVER touched — zero GetAllAsync calls (the counter
    /// fake in the tests proves it).
    /// </summary>
    public async Task<PatternScanResult> GetOrComputeAsync(
        string key,
        IHistoryRepository history,
        IReadOnlyList<Domain.Models.Habit>? habits = null,
        IReadOnlyList<GoalProgressSample>? goalProgress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(history);

        Task<PatternScanResult>? joined = null;
        TaskCompletionSource<PatternScanResult>? starter = null;
        lock (_gate)
        {
            if (_index.TryGetValue(key, out var node))
            {
                // Hit: touch to most-recent and return — repository untouched, zero reads.
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Result;
            }
            // Same-key stampede (several VMs refreshing at once): the first caller owns the
            // compute, everyone else joins its in-flight task — one read per key, per wave.
            // Same coalescing idea UserStateService uses for the state pipeline.
            if (_inFlight.TryGetValue(key, out joined)) { }
            else
            {
                starter = new TaskCompletionSource<PatternScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _inFlight[key] = starter.Task;
            }
        }
        if (joined is not null) return await joined.WaitAsync(ct);

        try
        {
            // Miss: read OUTSIDE the lock (a repository implementation may await real IO).
            var records = await history.GetAllAsync();
            ct.ThrowIfCancellationRequested();
            var asOf = records.Count == 0 ? default : records.Select(r => r.Date.Date).Max();
            var input = new PatternInput(asOf, records,
                habits ?? Array.Empty<Domain.Models.Habit>(),
                goalProgress ?? Array.Empty<GoalProgressSample>());
            var result = PatternEngine.Scan(input);

            lock (_gate)
            {
                _inFlight.Remove(key);
                StoreLocked(key, result);
            }
            starter!.TrySetResult(result);
            return result;
        }
        catch (Exception ex)
        {
            lock (_gate) _inFlight.Remove(key);
            starter!.TrySetException(ex);   // joiners see the same failure the starter does
            throw;
        }
    }

    /// <summary>Drop every stored set (profile switch / sign-out path).</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index.Clear();
            _lru.Clear();
        }
    }

    private void StoreLocked(string key, PatternScanResult result)
    {
        var node = _lru.AddFirst(new Cached(key, result));
        _index[key] = node;
        while (_index.Count > MaxResultSets)
        {
            var oldest = _lru.Last!;
            _lru.RemoveLast();
            _index.Remove(oldest.Value.Key);
            EvictionCount++;
        }
    }
}
