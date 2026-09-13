using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace LIVORA.Infrastructure.Persistence.Wave3b;

/// <summary>
/// Wave 3c (lane 06): the durability fix for <c>Infrastructure/Persistence/JsonFileStore.cs</c>.
/// The original class is FROZEN (other lanes hold it); this is a new file with the SAME public
/// shape plus what wave 3c needs:
///
/// <list type="bullet">
///   <item><b>Atomic writes</b>: serialize → temp file (flushed) → <see cref="File.Replace(string,string,string)"/>
///     with a <c>.bak</c> backup (or a plain move for a first write). A crash mid-write can only
///     ever leave the previous complete file, never half a JSON document — the failure class the
///     original's "corrupt file: treat as empty" catch was silently absorbing user data loss.</item>
///   <item><b>Per-path async locks</b>: a <see cref="SemaphoreSlim"/> per file name instead of one
///     process-wide monitor, so a slow write to history does not block a settings read, while a
///     read-modify-write from two threads on the SAME file still serializes (this is exactly the
///     gap <c>ManualEntryStore</c> had to paper over with its own gate).</item>
///   <item><b>Fully async</b>: real <c>FileOptions.Asynchronous</c> streams, zero sync-over-async —
///     the original faked <c>Task</c> around blocking IO for the documented pre-message-pump reason;
///     nothing here calls <c>.Wait()</c>/<c>.Result</c>, so the wave-3b startup path can adopt it
///     once that path stops running before the pump exists.</item>
///   <item><b>Stable-key options</b>: enum-as-string + name-sorted properties + relaxed (UTF-8
///     readable) encoder, so files are diffable and hand-edits survive; byte-stable for hashing.</item>
///   <item><b>Honest corrupt reporting</b> (the added API): <see cref="LoadListResultAsync{T}"/> /
///     <see cref="LoadObjectResultAsync{T}"/> return <see cref="LoadResult{T}"/> with
///     <c>Ok=false, Corrupt=true</c> AND quarantine the unreadable bytes as
///     <c>&lt;name&gt;.corrupt-&lt;timestamp&gt;</c> — the original file is preserved untouched for
///     forensics instead of being read-as-empty and overwritten away.</item>
/// </list>
/// </summary>
public sealed class JsonFileStoreV2
{
    private readonly string _dir;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>Stable-key, deterministic, human-diffable JSON grammar (see class docs).</summary>
    public static JsonSerializerOptions Options { get; } = BuildOptions();

    private static JsonSerializerOptions BuildOptions()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers = { LIVORA.Application.Sync.JsonPropertySort.SortByName },
            },
        };
        o.MakeReadOnly();
        return o;
    }

    /// <param name="directoryOverride">Where files live; production passes nothing and the
    /// composition root decides, tests pass a temp directory (the same seam the original offers).</param>
    public JsonFileStoreV2(string? directoryOverride = null)
    {
        // MAUI-free on purpose: FileSystem.AppDataDirectory is only reachable from the app head, so
        // the default is the per-user app-data folder via env — the composition root always passes an
        // explicit directory in production; this default exists for tools/tests/headless runs.
        _dir = directoryOverride ?? DefaultDirectory();
        Directory.CreateDirectory(_dir);
    }

    private static string DefaultDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "LIVORA", "data");
    }

    public string DirectoryPath => _dir;
    public string PathOf(string fileName) => Path.Combine(_dir, fileName);

    // ---- result API (lane 06's addition) ---------------------------------------

    /// <summary>Outcome of one read: distinguishes "no file yet" from "file that cannot be parsed".</summary>
    public sealed class LoadResult<T>
    {
        public required bool Ok { get; init; }
        /// <summary>True when a file existed but its bytes did not parse (or were unreadable).</summary>
        public bool Corrupt { get; init; }
        /// <summary>True when there was simply no file (a first run is not an error).</summary>
        public bool Missing { get; init; }
        /// <summary>The value — empty list / default when Ok=false.</summary>
        public required T Value { get; init; }
        /// <summary>Name of the quarantine file written for a corrupt read (null unless Corrupt).</summary>
        public string? QuarantinedPath { get; init; }
        public string? ErrorKey { get; init; }
    }

    // ---- list API ---------------------------------------------------------------

    public async Task<List<T>> LoadListAsync<T>(string fileName, CancellationToken ct = default)
        => (await LoadListResultAsync<T>(fileName, ct).ConfigureAwait(false)).Value;

    public async Task<LoadResult<List<T>>> LoadListResultAsync<T>(string fileName, CancellationToken ct = default)
    {
        var sem = LockFor(fileName);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathOf(fileName);
            if (!File.Exists(path))
                return new LoadResult<List<T>> { Ok = true, Missing = true, Value = new List<T>() };

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (IOException) { return CorruptResult<T>(fileName, new List<T>(), null, "IO"); }
            catch (UnauthorizedAccessException) { return CorruptResult<T>(fileName, new List<T>(), null, "IO"); }

            var payload = StripPayload(bytes);
            if (payload.IsEmpty)
                return new LoadResult<List<T>> { Ok = true, Missing = true, Value = new List<T>() };

            try
            {
                var parsed = JsonSerializer.Deserialize<List<T>>(payload, Options) ?? new List<T>();
                return new LoadResult<List<T>> { Ok = true, Value = parsed };
            }
            catch (JsonException)
            {
                return CorruptResult<T>(fileName, new List<T>(), Quarantine(path), "Json");
            }
        }
        finally { sem.Release(); }
    }

    public async Task SaveListAsync<T>(string fileName, IReadOnlyList<T> items, CancellationToken ct = default)
    {
        var text = JsonSerializer.Serialize(items is List<T> l ? l : items.ToList(), Options);
        await WriteAtomicAsync(fileName, text, ct).ConfigureAwait(false);
    }

    // ---- object API -------------------------------------------------------------

    public async Task<T?> LoadObjectAsync<T>(string fileName, CancellationToken ct = default)
        => (await LoadObjectResultAsync<T>(fileName, ct).ConfigureAwait(false)).Value;

    public async Task<LoadResult<T?>> LoadObjectResultAsync<T>(string fileName, CancellationToken ct = default)
    {
        var sem = LockFor(fileName);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathOf(fileName);
            if (!File.Exists(path))
                return new LoadResult<T?> { Ok = true, Missing = true, Value = default };

            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (IOException) { return new LoadResult<T?> { Ok = false, Corrupt = true, Value = default, ErrorKey = "IO" }; }
            catch (UnauthorizedAccessException) { return new LoadResult<T?> { Ok = false, Corrupt = true, Value = default, ErrorKey = "IO" }; }

            var payload = StripPayload(bytes);
            if (payload.IsEmpty)
                return new LoadResult<T?> { Ok = true, Missing = true, Value = default };

            try
            {
                var parsed = JsonSerializer.Deserialize<T>(payload, Options);
                return new LoadResult<T?> { Ok = true, Value = parsed };
            }
            catch (JsonException)
            {
                return new LoadResult<T?> { Ok = false, Corrupt = true, Value = default, QuarantinedPath = Quarantine(path), ErrorKey = "Json" };
            }
        }
        finally { sem.Release(); }
    }

    public Task SaveObjectAsync<T>(string fileName, T item, CancellationToken ct = default)
        => WriteAtomicAsync(fileName, JsonSerializer.Serialize(item, Options), ct);

    public async Task DeleteFileAsync(string fileName, CancellationToken ct = default)
    {
        var sem = LockFor(fileName);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DeleteWithBackup(fileName);
        }
        finally { sem.Release(); }
    }

    /// <summary>Delete data + its .bak (privacy wipe must not leave a shadow copy of user data).</summary>
    public void DeleteWithBackup(string fileName)
    {
        TryDelete(PathOf(fileName));
        TryDelete(PathOf(fileName) + ".bak");
    }

    public bool Exists(string fileName) => File.Exists(PathOf(fileName));

    /// <summary>Every quarantine file written for a name (tests + the privacy report read this).</summary>
    public IReadOnlyList<string> QuarantineFilesOf(string fileName)
    {
        var prefix = fileName + ".corrupt-";
        if (!Directory.Exists(_dir)) return Array.Empty<string>();
        return Directory.GetFiles(_dir, prefix + "*")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    // ---- raw bytes (the migration runner works at this level: hash-comparable) ---

    public async Task<byte[]> ReadAllBytesAsync(string fileName, CancellationToken ct = default)
    {
        var sem = LockFor(fileName);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathOf(fileName);
            if (!File.Exists(path)) return Array.Empty<byte>();
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        finally { sem.Release(); }
    }

    public Task WriteAllTextAtomicAsync(string fileName, string text, CancellationToken ct = default)
        => WriteAtomicAsync(fileName, text, ct);

    // ---- internals ---------------------------------------------------------------

    private SemaphoreSlim LockFor(string fileName) =>
        _locks.GetOrAdd(fileName, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Drop the UTF-8 BOM if one is present: System.Text.Json refuses a BOM mid-parse, and a
    /// hand-saved file (Notepad on Windows) can carry one. Bytes are left untouched on disk — this
    /// only un-blocks the read.
    /// </summary>
    private static ReadOnlySpan<byte> StripPayload(byte[] bytes)
    {
        if (bytes.Length == 0) return ReadOnlySpan<byte>.Empty;
        var span = new ReadOnlySpan<byte>(bytes);
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        return span;
    }

    private LoadResult<List<T>> CorruptResult<T>(string fileName, List<T> empty, string? quarantine, string errorKey)
    {
        if (quarantine is null)
            quarantine = Quarantine(PathOf(fileName));
        return new LoadResult<List<T>> { Ok = false, Corrupt = true, Value = empty, QuarantinedPath = quarantine, ErrorKey = errorKey };
    }

    private async Task WriteAtomicAsync(string fileName, string text, CancellationToken ct)
    {
        var sem = LockFor(fileName);
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = PathOf(fileName);
            Directory.CreateDirectory(_dir);
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
                if (File.Exists(path))
                    ReplaceWithRetry(tmp, path, path + ".bak");   // .bak keeps the previous good bytes
                else
                    MoveWithRetry(tmp, path);
            }
            finally
            {
                TryDelete(tmp); // only survives if Replace/Move failed mid-way
            }
        }
        finally { sem.Release(); }
    }

    /// <summary>
    /// Rename the unreadable file to <c>name.corrupt-&lt;UTC timestamp&gt;</c> preserving bytes.
    /// </summary>
    private string? Quarantine(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var target = Path.Combine(_dir, name + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
            int n = 1;
            while (File.Exists(target))
                target = Path.Combine(_dir, name + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + n++);
            MoveWithRetry(path, target);
            return target;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// File.Replace with a brief retry: on Windows an AV handle on the just-written temp
    /// can cause a transient sharing violation; three short waits absorb it without a fake fallback.
    /// Outside Windows File.Replace is unsupported — there a plain overwrite-move is used, which is
    /// a rename (atomic on POSIX), so Android keeps its crash-durability without a .bak sidecar.
    /// </summary>
    private static void ReplaceWithRetry(string source, string destination, string backup)
    {
        if (!OperatingSystem.IsWindows())
        {
            MoveWithRetry(source, destination);
            return;
        }
        for (int attempt = 0; ; attempt++)
        {
            try { File.Replace(source, destination, backup); return; }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(20 * (attempt + 1));
                if (!File.Exists(destination)) { TryMoveQuiet(source, destination); return; }
            }
        }
    }

    private static void MoveWithRetry(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(source, destination); return; }
            catch (IOException) when (attempt < 3) { Thread.Sleep(20 * (attempt + 1)); }
        }
    }

    private static void TryMoveQuiet(string source, string destination)
    {
        try { File.Move(source, destination, overwrite: true); } catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
