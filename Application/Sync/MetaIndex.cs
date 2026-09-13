using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Sync;

/// <summary>
/// Wave 3c (lane 06): the sidecar metadata store. ONE json file per store, mapping
/// <c>kind:id</c> → <see cref="EntityMeta"/>. It deliberately does not live inside the entity
/// files: a goal's stored JSON keeps exactly the shape every other layer reads, and the sync
/// bookkeeping is joined in at read time (Rule 21 — the metadata model grows by sidecar, not by
/// mutating persisted entities).
///
/// Honesty: an entity with no row here reads back as <see cref="EntityMeta.Absent"/>
/// (Version 0, <see cref="SyncState.Clean"/>) — legacy data is not silently re-labelled "pending"
/// just because the metadata layer arrived later. Only <see cref="BumpAsync"/> creates a row, and
/// only a real transport confirmation may move a row to Synced.
///
/// Thread-safety: a single <see cref="SemaphoreSlim"/> serializes load→mutate→save, so two writes
/// on different threads cannot lose one. File IO is fully async (no <c>.Wait()</c> anywhere) and
/// every save is atomic (temp + <c>File.Replace</c> keeping a .bak), so a crash mid-write
/// cannot leave a half-written index.
/// </summary>
public sealed class MetaIndex
{
    /// <summary>Default file name for the single app-wide index (kinds share one file by design).</summary>
    public const string DefaultFileName = "livora_meta.json";

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, EntityMeta> _rows;
    private bool _loaded;

    private static readonly JsonSerializerOptions FileOptions = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { JsonPropertySort.SortByName },
            },
        };
        o.MakeReadOnly();
        return o;
    }

    /// <summary>On-disk shape: a plain map so a hand-inspection shows kind:id keys directly.</summary>
    private sealed class IndexFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, EntityMeta> Rows { get; set; } = new(StringComparer.Ordinal);
    }

    /// <param name="directoryPath">Directory that holds the index (injected: tests pass a temp dir,
    /// the composition root passes the app data directory — this layer never touches MAUI APIs).</param>
    /// <param name="fileName">Index file name; one per store, default <see cref="DefaultFileName"/>.</param>
    public MetaIndex(string directoryPath, string fileName = DefaultFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, fileName);
        _rows = new Dictionary<string, EntityMeta>(StringComparer.Ordinal);
    }

    public string FilePath => _path;

    /// <summary>Read one entity's metadata. Missing file / missing row / corrupt file → Absent (Version 0, Clean).</summary>
    public async Task<EntityMeta> GetAsync(string kind, string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _rows.TryGetValue(EntityMeta.KeyOf(kind, id), out var meta) ? meta : EntityMeta.Absent;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// The write hook: bump version, stamp UpdatedAtUtc, mark Pending, persist. This is the ONLY
    /// path that puts an entity into Pending, and it is called by every catalog/store write.
    /// </summary>
    public async Task<EntityMeta> BumpAsync(string kind, string id, DateTime? nowUtc = null, CancellationToken ct = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var key = EntityMeta.KeyOf(kind, id);
            var next = (_rows.TryGetValue(key, out var prev) ? prev : EntityMeta.Absent).Bump(now);
            _rows[key] = next;
            await PersistAsync(ct).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Set the lifecycle state without touching Version (drain confirmation / conflict / resolution).</summary>
    public async Task<EntityMeta> SetStateAsync(string kind, string id, SyncState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var key = EntityMeta.KeyOf(kind, id);
            var current = _rows.TryGetValue(key, out var prev) ? prev : EntityMeta.Absent;
            var next = current.WithState(state);
            if (current.IsAbsent && state == SyncState.Clean)
            {
                // A legacy entity that was never bumped stays legacy: writing a row here would
                // invent metadata for data this layer never saw change.
                return current;
            }
            _rows[key] = next;
            await PersistAsync(ct).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drop metadata for one entity (delete-by-id path: no orphan rows may outlive the data).</summary>
    public async Task<bool> RemoveAsync(string kind, string id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            if (!_rows.Remove(EntityMeta.KeyOf(kind, id))) return false;
            await PersistAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Drop metadata for every entity of one kind (whole-store wipe).</summary>
    public async Task<int> RemoveKindAsync(string kind, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var prefix = kind + ":";
            var doomed = _rows.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in doomed) _rows.Remove(k);
            if (doomed.Count > 0) await PersistAsync(ct).ConfigureAwait(false);
            return doomed.Count;
        }
        finally { _gate.Release(); }
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedAsync(ct).ConfigureAwait(false); return _rows.Count; }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyDictionary<string, EntityMeta>> SnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            return new Dictionary<string, EntityMeta>(_rows, StringComparer.Ordinal);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Erase the whole index (privacy wipe). Removes the file AND the .bak so nothing lingers.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _rows.Clear();
            _loaded = true; // an intentional wipe is a known-empty state, not a read failure
            SidecarIo.Delete(_path);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        _loaded = true;
        var text = await SidecarIo.ReadIfExistsAsync(_path, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var file = JsonSerializer.Deserialize<IndexFile>(text, FileOptions);
            if (file?.Rows is null) return;
            foreach (var (k, v) in file.Rows)
                if (v is not null) _rows[k] = v;
        }
        catch (JsonException)
        {
            // Corrupt index: quarantine (bytes preserved) and start clean. Metadata is bookkeeping —
            // losing it degrades to "Version 0, Clean", it never destroys a user's entity data.
            SidecarIo.Quarantine(_path);
            _rows.Clear();
        }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        var payload = new IndexFile { Rows = new Dictionary<string, EntityMeta>(_rows, StringComparer.Ordinal) };
        var json = JsonSerializer.Serialize(payload, FileOptions);
        await SidecarIo.WriteAtomicAsync(_path, json, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// The tiny shared file primitives for the Application-side sync stores (a sidecar may not depend
/// on Infrastructure/Persistence — layer rule — so these few lines live here; the app-wide
/// equivalent with the identical discipline is <c>JsonFileStoreV2</c>).
/// </summary>
internal static class SidecarIo
{
    public static async Task<string?> ReadIfExistsAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>temp write → File.Replace (keeps a .bak on Windows) → or plain move for a first write.</summary>
    public static async Task WriteAtomicAsync(string path, string text, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                await sw.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
                await sw.FlushAsync(ct).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            if (File.Exists(path))
                ReplaceWithRetry(tmp, path, path + ".bak");
            else
                File.Move(tmp, path);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>Windows: File.Replace keeps a .bak (AV-handle retries). POSIX: overwrite-move is an
    /// atomic rename, so durability holds without the sidecar (File.Replace is unsupported there).</summary>
    private static void ReplaceWithRetry(string source, string destination, string backup)
    {
        if (!OperatingSystem.IsWindows())
        {
            try { File.Move(source, destination, overwrite: true); return; }
            catch (IOException) { throw; }
        }
        for (int attempt = 0; ; attempt++)
        {
            try { File.Replace(source, destination, backup); return; }
            catch (IOException) when (attempt < 3)
            {
                System.Threading.Thread.Sleep(20 * (attempt + 1));
                if (!File.Exists(destination)) { try { File.Move(source, destination, overwrite: true); } catch (IOException) { } return; }
            }
        }
    }

    /// <summary>Preserve the unreadable bytes instead of overwriting them (forensics, not silence).</summary>
    public static void Quarantine(string path)
    {
        try
        {
            var target = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            if (!File.Exists(target)) File.Move(path, target);
        }
        catch (IOException) { /* quarantine is best-effort; the read path already degraded safely */ }
        catch (UnauthorizedAccessException) { }
    }

    public static void Delete(string path)
    {
        TryDelete(path);
        TryDelete(path + ".bak");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
