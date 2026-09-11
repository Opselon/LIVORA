using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Core.Enums;
using LIVORA.Core.Interfaces;
using LIVORA.Core.Models;

namespace LIVORA.Services.Persistence;

/// <summary>
/// Phase 1 persistence: JSON files in the app data directory. All persistence sits behind
/// IRepository/ISettingsService — the UI never sees file APIs.
/// </summary>
public sealed class JsonFileStore
{
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonFileStore(string? directoryOverride = null)
    {
        _dir = directoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LIVORA");
        Directory.CreateDirectory(_dir);
    }

    public async Task<IReadOnlyList<T>> LoadListAsync<T>(string fileName)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dir, fileName);
            if (!File.Exists(path)) return Array.Empty<T>();
            var json = await File.ReadAllTextAsync(path);
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<T>();
            return JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveListAsync<T>(string fileName, IReadOnlyList<T> items)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dir, fileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(items.ToList(), Options));
        }
        finally { _gate.Release(); }
    }

    public async Task SaveObjectAsync<T>(string fileName, T item)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dir, fileName);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(item, Options));
        }
        finally { _gate.Release(); }
    }

    public async Task<T?> LoadObjectAsync<T>(string fileName)
    {
        await _gate.WaitAsync();
        try
        {
            var path = Path.Combine(_dir, fileName);
            if (!File.Exists(path)) return default;
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        finally { _gate.Release(); }
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

    public Task<IReadOnlyList<T>> GetAllAsync() => _store.LoadListAsync<T>(_file);

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
        if (idx >= 0) ((List<T>)all)[idx] = item;
        else ((List<T>)all).Add(item);
        await _store.SaveListAsync(_file, all);
    }

    public async Task DeleteAsync(string id)
    {
        var all = await _store.LoadListAsync<T>(_file);
        ((List<T>)all).RemoveAll(x => _keySelector(x) == id);
        await _store.SaveListAsync(_file, all);
    }

    public async Task<bool> IsEmptyAsync()
    {
        var all = await _store.LoadListAsync<T>(_file);
        return all.Count == 0;
    }
}

/// <summary>JSON-file-backed settings. Persists language choice and onboarding state.</summary>
public sealed class JsonSettingsService : ISettingsService
{
    private readonly JsonFileStore _store;
    private SettingsData _data = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed class SettingsData
    {
        public string Language { get; set; } = string.Empty;
        public bool LanguageExplicitlySet { get; set; }
        public bool OnboardingCompleted { get; set; }
        public string? ProfileId { get; set; }
    }

    public JsonSettingsService(JsonFileStore store) => _store = store;

    public async Task LoadAsync()
    {
        var d = await _store.LoadObjectAsync<SettingsData>(AppConstants.SettingsFileName);
        if (d is not null) _data = d;
    }

    public AppLanguage PreferredLanguage
    {
        get => Enum.TryParse<AppLanguage>(_data.Language, out var l) ? l : AppLanguage.English;
        set
        {
            _data.Language = value.ToString();
            _ = _store.SaveObjectAsync(AppConstants.SettingsFileName, _data);
        }
    }

    public bool LanguageExplicitlySet
    {
        get => _data.LanguageExplicitlySet;
        set { _data.LanguageExplicitlySet = value; _ = _store.SaveObjectAsync(AppConstants.SettingsFileName, _data); }
    }

    public bool OnboardingCompleted
    {
        get => _data.OnboardingCompleted;
        set { _data.OnboardingCompleted = value; _ = _store.SaveObjectAsync(AppConstants.SettingsFileName, _data); }
    }

    public string? ProfileId
    {
        get => _data.ProfileId;
        set { _data.ProfileId = value; _ = _store.SaveObjectAsync(AppConstants.SettingsFileName, _data); }
    }
}
