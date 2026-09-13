using System.Text.Json;
using System.Text.Json.Serialization;

namespace LIVORA.Infrastructure.Persistence;

/// <summary>
/// Wave 3c (lane 01): the MAUI-free twin of <see cref="JsonFileStore"/>, used by the security and
/// data-portability services that must also compile (and be tested) in the plain <c>net10.0</c>
/// test project. The difference is exactly one thing: the root directory is REQUIRED and injected,
/// so no platform API (<c>FileSystem.AppDataDirectory</c>) appears in the class. The composition
/// root passes the very same directory the rest of persistence uses
/// (<c>FileSystem.AppDataDirectory/LIVORA</c>), so both stores read and write the same files —
/// there is no parallel data layout, only a platform-free entry point to the existing one.
///
/// Serialization options are copied verbatim from <see cref="JsonFileStore"/> (indented, nulls
/// omitted, enums as strings) so a file written by either class reads identically in the other.
///
/// IO is synchronous for the same Phase-1 reason documented on <see cref="JsonFileStore"/>: these
/// files are tiny and the call sites include pre-message-pump startup, where async .Wait() would
/// deadlock. Task-returning signatures keep the async UI layer unchanged.
/// </summary>
public sealed class LocalJsonStore
{
    private readonly string _dir;
    private readonly object _gate = new();

    /// <summary>Identical to JsonFileStore.Options — the on-disk contract must not drift.</summary>
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The resolved data directory this store owns (created on construction).</summary>
    public string Directory => _dir;

    public LocalJsonStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _dir = Path.GetFullPath(directory);
        // `Directory` is also a member name on this class (the resolved path), so the
        // namespace has to be spelled out here or the property shadows System.IO.Directory.
        System.IO.Directory.CreateDirectory(_dir);
    }

    public string PathOf(string fileName) => Path.Combine(_dir, fileName);

    public bool Exists(string fileName) => File.Exists(PathOf(fileName));

    /// <summary>Raw text of a file, or null when it is absent/empty. Never throws for a missing file.</summary>
    public string? ReadRaw(string fileName)
    {
        lock (_gate)
        {
            var path = PathOf(fileName);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
    }

    /// <summary>
    /// Write via temp file + <c>File.Replace</c>: a crash mid-write can never leave a half-written
    /// data file, which matters doubly for the whole-store import path.
    /// </summary>
    public void WriteRawAtomic(string fileName, string text)
    {
        lock (_gate)
        {
            var path = PathOf(fileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path))
            {
                // File.Replace needs a destination that exists; the backup slot keeps one rollback copy.
                File.Replace(tmp, path, path + ".bak");
            }
            else
            {
                File.Move(tmp, path);
                var backup = path + ".bak";
                if (File.Exists(backup)) File.Delete(backup);
            }
        }
    }

    public void Delete(string fileName)
    {
        lock (_gate)
        {
            var path = PathOf(fileName);
            if (File.Exists(path)) File.Delete(path);
            foreach (var side in new[] { path + ".bak", path + ".tmp" })
                if (File.Exists(side)) File.Delete(side);
        }
    }

    public List<T> LoadList<T>(string fileName)
    {
        try
        {
            var json = ReadRaw(fileName);
            if (json is null) return new List<T>();
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>();
        }
        catch
        {
            // Corrupt file reads as empty, exactly like JsonFileStore — never crash a page.
            return new List<T>();
        }
    }

    public T? LoadObject<T>(string fileName)
    {
        try
        {
            var json = ReadRaw(fileName);
            if (json is null) return default;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch
        {
            return default;
        }
    }

    public void SaveList<T>(string fileName, IReadOnlyList<T> items) =>
        WriteRawAtomic(fileName, JsonSerializer.Serialize(items.ToList(), Options));

    public void SaveObject<T>(string fileName, T item) =>
        WriteRawAtomic(fileName, JsonSerializer.Serialize(item, Options));
}
