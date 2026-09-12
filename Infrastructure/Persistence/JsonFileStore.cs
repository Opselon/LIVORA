using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.Persistence;
/// <summary>
/// Phase 1 persistence: JSON files in the app data directory. All persistence sits behind
/// IRepository/ISettingsService — the UI never sees file APIs.
///
/// File IO is intentionally synchronous here: Phase 1 files are tiny (a few KB) and startup
/// runs on the main thread before the message pump exists, so async .Wait() there would
/// deadlock (a real hazard we hit in development). Task-returning signatures are kept so
/// the async UI layer never notices if storage moves to a database in Phase 2.
/// </summary>
public sealed class JsonFileStore
{
    private readonly string _dir;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileStore(string? directoryOverride = null)
    {
        _dir = directoryOverride ?? Path.Combine(FileSystem.AppDataDirectory, "LIVORA");
        Directory.CreateDirectory(_dir);
    }

    public Task<List<T>> LoadListAsync<T>(string fileName)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_dir, fileName);
                if (!File.Exists(path)) return Task.FromResult(new List<T>());
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return Task.FromResult(new List<T>());
                return Task.FromResult(JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>());
            }
            catch
            {
                // Corrupt file: treat as empty rather than crash the app.
                return Task.FromResult(new List<T>());
            }
        }
    }

    public Task SaveListAsync<T>(string fileName, IReadOnlyList<T> items)
    {
        lock (_gate)
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(items.ToList(), Options));
        }
        return Task.CompletedTask;
    }

    public Task SaveObjectAsync<T>(string fileName, T item)
    {
        lock (_gate)
        {
            var path = Path.Combine(_dir, fileName);
            File.WriteAllText(path, JsonSerializer.Serialize(item, Options));
        }
        return Task.CompletedTask;
    }

    public Task<T?> LoadObjectAsync<T>(string fileName)
    {
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_dir, fileName);
                if (!File.Exists(path)) return Task.FromResult<T?>(default);
                var json = File.ReadAllText(path);
                return Task.FromResult(JsonSerializer.Deserialize<T>(json, Options));
            }
            catch
            {
                return Task.FromResult<T?>(default);
            }
        }
    }

    public Task DeleteFileAsync(string fileName)
    {
        lock (_gate)
        {
            var path = Path.Combine(_dir, fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }
}

public sealed class JsonRepository<T> : IRepository<T> where T : class
{
    private readonly JsonFileStore _store;
    private readonly string _file;
    private readonly Func<T, string> _keySelector;

    public JsonRepository(JsonFileStore store, string fileName, Func<T, string> keySelector)
    {
        _store = store;
        _file = fileName;
        _keySelector = keySelector;
    }

    public async Task<IReadOnlyList<T>> GetAllAsync() => await _store.LoadListAsync<T>(_file);

    public async Task<T?> GetAsync(string id)
    {
        var all = await _store.LoadListAsync<T>(_file);
        return all.FirstOrDefault(x => _keySelector(x) == id);
    }

    public async Task SaveAsync(T item)
    {
        var all = await _store.LoadListAsync<T>(_file);
        var key = _keySelector(item);
        var idx = all.FindIndex(x => _keySelector(x) == key);
        if (idx >= 0) all[idx] = item;
        else all.Add(item);
        await _store.SaveListAsync(_file, all);
    }

    public async Task DeleteAsync(string id)
    {
        var all = await _store.LoadListAsync<T>(_file);
        all.RemoveAll(x => _keySelector(x) == id);
        await _store.SaveListAsync(_file, all);
    }

    public async Task<bool> IsEmptyAsync()
    {
        var all = await _store.LoadListAsync<T>(_file);
        return all.Count == 0;
    }
}
