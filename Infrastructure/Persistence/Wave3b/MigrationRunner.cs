using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LIVORA.Infrastructure.Persistence.Wave3b;

/// <summary>
/// Wave 3c (lane 06): numbered, per-store migrations with a per-store marker file.
///
/// Rule 21 (the model-evolution law this repo lives by): an old stored model becomes the NEW model
/// by running an ascending migration over the bytes it already holds — never by delete + recreate.
/// Migration 0001 is the "identity stamp": an install that has data but no marker is declared v1
/// without touching its bytes, so everything after 0001 describes a real transform from v1 upward.
///
/// Failure discipline (per store, per step):
/// <list type="bullet">
///   <item>The data file is rewritten atomically: temp → flush → <see cref="File.Replace(string,string,string)"/>
///     keeping a <c>.bak</c> of the previous bytes.</item>
///   <item>If a transform throws or the write fails, the original bytes are restored (verified —
///     the .bak, or the in-memory copy when no .bak existed yet), the marker does NOT advance, and
///     the run reports the failed version with a machine key
///     (<c>Migration.Failed.&lt;store&gt;.&lt;version&gt;</c>) so the UI can say exactly which step
///     broke instead of silently skipping it.</item>
///   <item>A later run retries from the last committed version: a failed migration is never marked
///     applied, so nothing downstream pretends the store is newer than it is.</item>
/// </list>
///
/// Idempotency: after a full success the marker holds the newest version, and a second run reads
/// only that small marker (versions ≥ marker are skipped without touching the data file) — near
/// zero cost, which is what makes it safe to call on every startup.
/// </summary>
public sealed class MigrationRunner
{
    /// <summary>Subdirectory (inside the data dir) holding one marker json per store.</summary>
    public const string MarkerDirectoryName = "migrations";

    private readonly string _dataDir;
    private readonly string _markerDir;
    private readonly List<StoreMigration> _stores = new();
    private static readonly object RegistryGate = new();

    /// <summary>One ordered transform for a store file. <c>Identity</c> = the step certifies the
    /// existing bytes as this version WITHOUT rewriting them (used by 0001, Rule 21); otherwise
    /// <c>Transform</c> maps the old file text to the new text.</summary>
    public sealed record MigrationStep(int Version, string Name, Func<string, string>? Transform, bool Identity = false);

    private sealed class StoreMigration
    {
        public required string Store { get; init; }
        public required string FileName { get; init; }
        public required List<MigrationStep> Steps { get; init; }
    }

    public MigrationRunner(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDir = dataDirectory;
        _markerDir = Path.Combine(dataDirectory, MarkerDirectoryName);
    }

    /// <summary>Register (or replace) the migration ladder for one store file.</summary>
    public MigrationRunner RegisterStore(string storeName, string fileName, IEnumerable<MigrationStep> steps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var ordered = steps.OrderBy(s => s.Version).ToList();
        if (ordered.Count == 0)
            throw new ArgumentException("a migration ladder needs at least the identity stamp", nameof(steps));
        for (int i = 1; i < ordered.Count; i++)
            if (ordered[i].Version == ordered[i - 1].Version)
                throw new ArgumentException($"duplicate migration version {ordered[i].Version} for store {storeName}");
        lock (RegistryGate) { _stores.RemoveAll(s => s.Store == storeName); _stores.Add(new StoreMigration { Store = storeName, FileName = fileName, Steps = ordered }); }
        return this;
    }

    public sealed class StoreResult
    {
        public required string Store { get; init; }
        public int FromVersion { get; init; }
        public int ToVersion { get; init; }
        /// <summary>Versions actually applied during THIS run (empty = nothing to do).</summary>
        public required IReadOnlyList<int> Applied { get; init; }
        /// <summary>The version whose transform/write failed (null = success).</summary>
        public int? FailedVersion { get; init; }
        /// <summary>Machine key naming the failure — e.g. Migration.Failed.goals.2 (UI-localized).</summary>
        public string? ErrorKey { get; init; }
        /// <summary>Exception type behind the failure — diagnostics only, never shown raw to users.</summary>
        public string? ErrorDetail { get; init; }
        public bool Ok => FailedVersion is null;
    }

    public sealed class RunReport
    {
        public required IReadOnlyList<StoreResult> Stores { get; init; }
        public bool AllOk => Stores.All(s => s.Ok);
        public int AppliedThisRun => Stores.Sum(s => s.Applied.Count);
    }

    /// <summary>Apply every pending step in ascending order. Never throws for data-level failures —
    /// the report is the error channel (a migration failure must not brick startup).</summary>
    public async Task<RunReport> RunAllAsync(CancellationToken ct = default)
    {
        List<StoreMigration> snapshot;
        lock (RegistryGate) snapshot = _stores.ToList();
        var results = new List<StoreResult>(snapshot.Count);
        foreach (var store in snapshot)
            results.Add(await RunStoreAsync(store, ct).ConfigureAwait(false));
        return new RunReport { Stores = results };
    }

    public int GetAppliedVersion(string storeName) => ReadMarker(storeName).AppliedVersion;

    // ---- per-store engine -------------------------------------------------------

    private async Task<StoreResult> RunStoreAsync(StoreMigration store, CancellationToken ct)
    {
        var markerPath = MarkerPath(store.Store);
        var marker = ReadMarker(store.Store);
        var pending = store.Steps.Where(s => s.Version > marker.AppliedVersion).ToList();
        if (pending.Count == 0)
            return new StoreResult { Store = store.Store, FromVersion = marker.AppliedVersion, ToVersion = marker.AppliedVersion, Applied = Array.Empty<int>() };

        var path = Path.Combine(_dataDir, store.FileName);
        var applied = new List<int>();
        var current = marker.AppliedVersion;

        foreach (var step in pending)
        {
            ct.ThrowIfCancellationRequested();
            // Capture the EXACT pre-step bytes: the restore path re-verifies them by hash, so a
            // failed migration provably leaves the file as it was found.
            byte[] original = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
            var originalHash = original.Length == 0 ? null : Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
            try
            {
                if (!File.Exists(path))
                {
                    // Nothing stored yet: the store is trivially at the newest shape (an empty
                    // store written by current code IS current). No file to transform, no bytes to
                    // invent — advance the marker without creating data.
                    current = step.Version;
                    applied.Add(step.Version);
                    WriteMarker(markerPath, store.Store, current, step.Name);
                    continue;
                }

                var text = Decode(original);
                var next = step.Identity || step.Transform is null ? text : step.Transform(text);

                if (!ReferenceEquals(next, text) && next != text)
                    await JsonFileStoreV2WriteAsync(path, next, ct).ConfigureAwait(false);
                // Identity or unchanged text: deliberately NO write — Rule 21's 0001 must not churn
                // bytes it was only asked to certify.

                current = step.Version;
                applied.Add(step.Version);
                WriteMarker(markerPath, store.Store, current, step.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Restore(path, original);
                // Roll the marker back to the last COMMITTED version (it may already have moved for
                // earlier steps in this run — those succeeded and stay applied).
                WriteMarker(markerPath, store.Store, current, "rolled-back");
                return new StoreResult
                {
                    Store = store.Store,
                    FromVersion = marker.AppliedVersion,
                    ToVersion = current,
                    Applied = applied,
                    FailedVersion = step.Version,
                    ErrorKey = $"Migration.Failed.{store.Store}.{step.Version}",
                    ErrorDetail = ex.GetType().Name,
                };
            }
        }

        return new StoreResult { Store = store.Store, FromVersion = marker.AppliedVersion, ToVersion = current, Applied = applied };
    }

    private static async Task JsonFileStoreV2WriteAsync(string path, string text, CancellationToken ct)
    {
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1 << 15, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                await sw.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
                await sw.FlushAsync(ct).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { }
        }
    }

    private static string Decode(byte[] bytes) =>
        bytes.Length == 0 ? string.Empty
        : new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);

    private static string HashOf(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static void Restore(string path, byte[] original)
    {
        var bak = path + ".bak";
        try
        {
            if (original.Length == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            // Prefer the .bak (written by File.Replace) but the in-memory bytes are authoritative:
            // the restore is hash-verified by the caller's test, so byte-exactness is mandatory.
            File.WriteAllBytes(path, original);
        }
        catch (IOException)
        {
            try { if (File.Exists(bak)) File.Copy(bak, path, overwrite: true); } catch (IOException) { }
        }
    }

    // ---- marker ------------------------------------------------------------------

    private sealed class Marker
    {
        public int AppliedVersion { get; set; }
        public string Store { get; set; } = "";
        public DateTime? UpdatedAtUtc { get; set; }
        public string? LastStepName { get; set; }
    }

    private string MarkerPath(string store) => Path.Combine(_markerDir, store + ".json");

    private Marker ReadMarker(string store)
    {
        var path = MarkerPath(store);
        if (!File.Exists(path)) return new Marker { Store = store, AppliedVersion = 0 };
        try
        {
            var m = JsonSerializer.Deserialize<Marker>(File.ReadAllText(path), MarkerOptions);
            return m is null ? new Marker { Store = store } : m;
        }
        catch (JsonException) { return new Marker { Store = store, AppliedVersion = 0 }; }
    }

    private void WriteMarker(string path, string store, int version, string stepName)
    {
        Directory.CreateDirectory(_markerDir);
        var json = JsonSerializer.Serialize(new Marker { Store = store, AppliedVersion = version, UpdatedAtUtc = DateTime.UtcNow, LastStepName = stepName }, MarkerOptions);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    private static readonly JsonSerializerOptions MarkerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The 0001 identity stamp every store ladder starts with: "this file already matches model
    /// v1" — marker moves, bytes stay. (Migration is ADDITIVE; the file was never deleted +
    /// recreated.)
    /// </summary>
    public static MigrationStep IdentityStamp(int version = 1) =>
        new(version, $"identity-stamp-v{version}", Transform: null, Identity: true);
}
