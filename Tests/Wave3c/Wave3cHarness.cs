using LIVORA.Application.Abstractions;
using LIVORA.Application.Sync;
using LIVORA.Domain.Enums;

namespace LIVORA.Tests.Wave3c;

/// <summary>
/// Wave 3c (lane 06): shared scaffolding for the storage/sync suites. Owns the temp directories it
/// creates and registers them for deletion — every lane-06 test runs inside a <see cref="Scavenger"/>
/// (IAsyncLifetime), so nothing here can leak a directory into %TEMP% (the perf suite's "all temp dirs
/// disposed" budget is enforced by this type, not just by convention).
/// </summary>
internal sealed class Scavenger : IAsyncLifetime
{
    private readonly List<string> _dirs = new();

    /// <summary>Create (or reuse) one owned temp subdirectory. Called with the test's own id so
    /// parallel xunit collections never share a path.</summary>
    public string Dir(string name = "data")
    {
        var root = Path.Combine(Path.GetTempPath(), "livora_w3c_lane06",
            Guid.NewGuid().ToString("N"), Safe(name));
        Directory.CreateDirectory(root);
        _dirs.Add(root);
        return root;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in _dirs)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (Directory.Exists(d)) Directory.Delete(d, recursive: true);
                    // The per-test guid parent may still hold the (now deleted) tree — prune it too.
                    var parent = Path.GetDirectoryName(d);
                    if (parent is not null && Directory.Exists(parent) &&
                        Directory.EnumerateFileSystemEntries(parent).Any() == false)
                        Directory.Delete(parent);
                    break;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(25); // a lingering AV handle on a just-deleted jsonl, not a leak
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    Thread.Sleep(25);
                }
            }
            // The lane-06 budget says "all temp dirs disposed" — enforce it instead of hoping.
            if (Directory.Exists(d))
                throw new IOException($"lane06 temp dir was not released: {d}");
        }
        _dirs.Clear();
        return Task.CompletedTask;
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}

/// <summary>
/// Fake transports for the queue tests — every one LABELED fake (honesty rule): none of these is a
/// backend, and the queue must treat all of them as it treats the real thing (Synced only after the
/// fake honestly reports Success).
/// </summary>
internal sealed class FakeSyncTransport : ISyncTransport
{
    private readonly Func<IReadOnlyList<SyncEnvelope>, SyncPushResult> _responder;

    public FakeSyncTransport(bool configured,
        Func<IReadOnlyList<SyncEnvelope>, SyncPushResult>? responder = null)
    {
        IsConfigured = configured;
        _responder = responder ?? (_ => new SyncPushResult { Success = true, Conflicts = Array.Empty<ConflictKind>() });
    }

    /// <summary>The only real transport today: not configured (NoopSyncTransport's shape).</summary>
    public static FakeSyncTransport NotConfigured() =>
        new(false, _ => new SyncPushResult { Success = false, Conflicts = Array.Empty<ConflictKind>(), ErrorCategory = "NotConfigured" });

    public static FakeSyncTransport AlwaysSucceeds() => new(true);

    public static FakeSyncTransport ConflictsOn(Func<IReadOnlyList<SyncEnvelope>, bool> predicate, ConflictKind kind) =>
        new(true, batch => predicate(batch)
            ? new SyncPushResult { Success = true, Conflicts = new[] { kind } }
            : new SyncPushResult { Success = true, Conflicts = Array.Empty<ConflictKind>() });

    public static FakeSyncTransport Failing(string category) =>
        new(true, _ => new SyncPushResult { Success = false, Conflicts = Array.Empty<ConflictKind>(), ErrorCategory = category });

    public bool IsConfigured { get; }
    public string GatewayLabel => "fake.test-transport";

    /// <summary>Batches actually handed to PushAsync — tests assert on this to prove "nothing pushed".</summary>
    public List<IReadOnlyList<SyncEnvelope>> PushedBatches { get; } = new();

    public int PushedEnvelopes => PushedBatches.Sum(b => b.Count);

    public Task<SyncPushResult> PushAsync(IReadOnlyList<SyncEnvelope> batch, CancellationToken ct = default)
    {
        PushedBatches.Add(batch);
        return Task.FromResult(_responder(batch));
    }
}

internal static class Wave3cSync
{
    /// <summary>Envelope for tests: canonical hash computed exactly like the production write path.</summary>
    public static SyncEnvelope Envelope(string kind, string id, long version, object? payload = null)
    {
        var json = CanonicalJson.Serialize(payload ?? new { Kind = kind, Id = id, V = version });
        return new SyncEnvelope
        {
            EntityKind = kind,
            EntityId = id,
            LocalState = SyncState.Pending,
            LocalVersion = version,
            PayloadHash = CanonicalJson.Sha256HexOfCanonical(json),
            ChangedAtUtc = new DateTime(2026, 9, 13, 8, 0, 0, DateTimeKind.Utc),
        };
    }
}
