using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Sync;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.Persistence.Wave3b;

/// <summary>
/// Wave 3c (lane 06): the local data catalog — inspect/edit/export/import/wipe for EVERY stored
/// entity kind through ONE seam (<see cref="ILocalDataCatalogService"/>), on top of the durable
/// V2 store conventions (<see cref="JsonFileStoreV2"/> + <see cref="MetaIndex"/> + <see cref="SyncQueue"/>).
///
/// KIND REGISTRY: each kind is (name, file, shape, id-extractor) — data-driven, so the editor UI,
/// the exporter and the wipe all read the same table and cannot drift apart. History/manual key on
/// Date (their natural identity), reminder rows on Id; single-object stores (profile, settings,
/// consents) carry one synthetic row.
///
/// WRITE PATH = the same one every editor uses: validate-before-write (rejects return machine
/// error keys and touch NOTHING) → atomic save → meta Bump (Version+1, Pending) → queue Enqueue
/// (hash + size only). A write can therefore never be half-applied, and never claims sync progress
/// it did not earn.
///
/// MERGE NOTE (for the integrator): lane 01 owns a parallel catalog effort per the master plan; no
/// such file exists in this copy (see NOTES), so this implementation stands alone against the
/// frozen contract. If lane 01's lands, the kind registry is the piece to reconcile — everything
/// else here (store/meta/queue/migration discipline) is layer 06 regardless of which catalog wins.
/// </summary>
public sealed class LocalDataCatalogService : ILocalDataCatalogService
{
    /// <summary>File-name of the manual-entry store — duplicated const (see class remarks): the
    /// real owner is <c>ManualEntryStore.EntriesFileName</c>, which lives in a file the test
    /// assembly does not compile, so this layer cannot reference it without coupling the suites.</summary>
    public const string ManualFileName = "livora_manual_entries.json";
    /// <summary>Consent store file — reserved for the privacy lane's IConsentService backing store;
    /// reading it here keeps the wipe/export inventory complete even before that lane lands.</summary>
    public const string ConsentsFileName = "livora_consents.json";
    /// <summary>Written by <see cref="ResetAllAsync"/> so a wipe is provable AFTER the data is gone.</summary>
    public const string ResetMarkerFileName = "reset-marker.json";

    /// <summary>Wire shape of the reset marker: {WipedAtUtc, AppVersion} exactly — no personal data.</summary>
    public sealed record ResetMarker(DateTime WipedAtUtc, string AppVersion);

    private sealed record CatalogKind(
        string Name,
        string FileName,
        // null = the root IS the entity array; non-null = array lives at that property of a root object.
        string? ArrayProperty,
        // Property carrying the row identity; Date-valued ids are normalized to yyyy-MM-dd.
        string? IdProperty,
        // True when the whole file is a single entity (profile/settings/consents).
        bool Singleton = false);

    private static readonly CatalogKind[] Registry =
    {
        new("goals",      AppConstants.GoalsFile,     ArrayProperty: null,          "Id"),
        new("habits",     AppConstants.HabitsFile,    ArrayProperty: null,          "Id"),
        new("bootcamps",  AppConstants.BootcampsFile, ArrayProperty: null,          "Id"),
        new("profile",    AppConstants.ProfileFile,   ArrayProperty: null,          "Id", Singleton: true),
        new("history",    AppConstants.HistoryFile,   ArrayProperty: "Records",     "Date"),
        new("manual",     ManualFileName,             ArrayProperty: null,          "Date"),
        new("settings",   AppConstants.SettingsFileName, ArrayProperty: null,       "Key", Singleton: true),
        new("consents",   ConsentsFileName,           ArrayProperty: null,          "Key", Singleton: true),
        new("reminders",  "livora_reminders.json",    ArrayProperty: "Reminders",   "Id"),
    };

    private readonly JsonFileStoreV2 _store;
    private readonly MetaIndex _meta;
    private readonly SyncQueue _queue;
    private readonly Func<string> _appVersion;
    private readonly Func<DateTime> _clock;

    /// <summary>Whole-catalog write gate: a kind file's load→edit→save must be one unit (the
    /// store's per-path lock only covers a single read or write). Cross-kind writes contend here
    /// too, which is fine: catalog edits are user-paced, not a hot path.</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>Parse is order-insensitive (JsonNode keeps the file's own ordering), so the only
    /// option worth stating is comment tolerance for hand-edited backups.</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public LocalDataCatalogService(
        JsonFileStoreV2 store,
        MetaIndex metaIndex,
        SyncQueue queue,
        Func<string>? appVersion = null,
        Func<DateTime>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _meta = metaIndex ?? throw new ArgumentNullException(nameof(metaIndex));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _appVersion = appVersion ?? (() => DefaultAppVersion);
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Fallback only: the composition root passes the real AppInfo version (this layer is
    /// MAUI-free). Matches LIVORA.csproj ApplicationDisplayVersion; flagged in NOTES.</summary>
    public const string DefaultAppVersion = "1.1";

    // ---- ILocalDataCatalogService -------------------------------------------------

    public Task<IReadOnlyList<string>> GetKindsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Registry.Select(k => k.Name).ToList());

    public async Task<string> ExportEntityAsync(string kind, string id, CancellationToken ct = default)
    {
        var def = FindKind(kind);
        if (def is null || string.IsNullOrWhiteSpace(id)) return string.Empty;
        var root = await LoadRootAsync(def, ct).ConfigureAwait(false);
        if (root is null) return string.Empty;
        var entity = def.Singleton ? root : FindEntity(def, root, id);
        return entity?.ToJsonString(WriteIndentedJson) ?? string.Empty;
    }

    public async Task<IReadOnlyList<string>> ImportEntityAsync(string kind, string json, CancellationToken ct = default)
    {
        var def = FindKind(kind);
        if (def is null) return new[] { "Error.Catalog.UnknownKind" };
        JsonNode? node;
        try { node = JsonNode.Parse(json ?? string.Empty, documentOptions: ReadOptions); }
        catch (JsonException) { return new[] { "Error.Catalog.InvalidJson" }; }
        if (node is null) return new[] { "Error.Catalog.InvalidJson" };

        var id = EntityId(def, node);
        if (string.IsNullOrWhiteSpace(id)) return new[] { "Error.Catalog.MissingIdentity" };
        var problems = Validate(def, node, id);
        if (problems.Count > 0) return problems;

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await LoadRootAsync(def, ct).ConfigureAwait(false);
            if (def.Singleton)
            {
                await SaveRootAsync(def, node, ct).ConfigureAwait(false);
            }
            else if (root is null)
            {
                // First entity of the store: the file keeps its array/wrapper shape — importing one
                // row never turns the whole file into a bare object.
                var fresh = BuildArrayTarget(def, new List<(string, JsonNode)> { (id, node) });
                await SaveRootAsync(def, fresh, ct).ConfigureAwait(false);
            }
            else
            {
                UpsertInto(def, root!, id, node);
                await SaveRootAsync(def, root!, ct).ConfigureAwait(false);
            }
            await AfterWriteAsync(def, id, node, ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
        return Array.Empty<string>();
    }

    public async Task<IReadOnlyList<string>> DeleteEntityAsync(string kind, IEnumerable<string> ids, CancellationToken ct = default)
    {
        var def = FindKind(kind);
        if (def is null) return new[] { "Error.Catalog.UnknownKind" };
        var wanted = (ids ?? Array.Empty<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
        var errors = new List<string>();

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = await LoadRootAsync(def, ct).ConfigureAwait(false);
            if (root is null)
            {
                foreach (var id in wanted) errors.Add($"Error.Catalog.NotFound.{id}");
                return errors;
            }
            if (def.Singleton)
            {
                // A single-entity store deletes as a file: only the store's own row can match.
                await _store.DeleteFileAsync(def.FileName, ct).ConfigureAwait(false);
                foreach (var id in wanted) { await _meta.RemoveAsync(def.Name, id, ct).ConfigureAwait(false); await _queue.RemoveAsync(EntityMeta.KeyOf(def.Name, id), ct).ConfigureAwait(false); }
                return errors;
            }
            foreach (var id in wanted)
            {
                if (!RemoveFrom(def, root, id))
                {
                    errors.Add($"Error.Catalog.NotFound.{id}");
                    continue;
                }
                await _meta.RemoveAsync(def.Name, id, ct).ConfigureAwait(false);
                await _queue.RemoveAsync(EntityMeta.KeyOf(def.Name, id), ct).ConfigureAwait(false);
            }
            await SaveRootAsync(def, root, ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
        return errors;
    }

    public async Task<string> ExportAllAsync(CancellationToken ct = default)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion,
            ["exportedAtUtc"] = _clock().ToString("O"),
            ["appVersion"] = _appVersion(),
            ["kinds"] = new JsonObject(),
        };
        var kinds = (JsonObject)root["kinds"]!;
        foreach (var def in Registry)
        {
            var entities = new JsonArray();
            var file = await LoadRootAsync(def, ct).ConfigureAwait(false);
            if (file is not null)
            {
                if (def.Singleton)
                {
                    if (File.Exists(_store.PathOf(def.FileName)))
                        entities.Add(Clone(file));
                }
                else foreach (var (_, el) in EnumerateEntities(def, file))
                    entities.Add(Clone(el));
            }
            kinds[def.Name] = new JsonObject { ["file"] = def.FileName, ["entities"] = entities };
        }
        return root.ToJsonString(WriteIndentedJson);
    }

    public const int SchemaVersion = 2;

    public async Task<IReadOnlyList<string>> ImportAllAsync(string json, bool merge, CancellationToken ct = default)
    {
        // ---------- phase 1: validate EVERYTHING before touching anything ----------
        JsonNode? doc;
        try { doc = JsonNode.Parse(json ?? string.Empty, documentOptions: ReadOptions); }
        catch (JsonException) { return new[] { "Error.Catalog.InvalidJson" }; }
        if (doc is not JsonObject obj) return new[] { "Error.Catalog.InvalidJson" };

        var schema = obj["schemaVersion"]?.GetValue<int>() ?? 0;
        if (schema > SchemaVersion) return new[] { "Error.Catalog.SchemaVersionTooNew" };

        var staged = new List<(CatalogKind Def, List<(string Id, JsonNode Node)> Rows, bool Whole)>();
        var errors = new List<string>();
        if (obj["kinds"] is not JsonObject kinds) return new[] { "Error.Catalog.MissingKinds" };

        foreach (var (kindName, payload) in kinds)
        {
            var def = FindKind(kindName);
            if (def is null) continue; // forward-compatible: unknown kinds in a future backup are skipped, not failed
            if (payload is not JsonObject kp) { errors.Add($"Error.Catalog.InvalidKind.{kindName}"); continue; }
            if (kp["entities"] is not JsonArray arr) { errors.Add($"Error.Catalog.InvalidKind.{kindName}"); continue; }
            var rows = new List<(string, JsonNode)>();
            foreach (var el in arr)
            {
                if (el is null) { errors.Add($"Error.Catalog.InvalidJson"); continue; }
                var id = EntityId(def, el);
                if (string.IsNullOrWhiteSpace(id)) { errors.Add($"Error.Catalog.MissingIdentity.{kindName}"); continue; }
                var problems = Validate(def, el, id);
                if (problems.Count > 0) { errors.AddRange(problems); continue; }
                rows.Add((id, Clone(el)));
            }
            staged.Add((def, rows, Whole: !merge));
        }

        if (errors.Count > 0)
            return errors.Distinct(StringComparer.Ordinal).ToList(); // nothing was written — all-or-nothing

        // ---------- phase 2: apply ----------
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (def, rows, whole) in staged)
            {
                JsonNode target;
                if (def.Singleton)
                {
                    if (rows.Count == 0) continue;
                    target = rows[0].Node;
                }
                else if (whole)
                {
                    target = BuildArrayTarget(def, rows);
                }
                else
                {
                    var existing = await LoadRootAsync(def, ct).ConfigureAwait(false);
                    if (existing is null) { target = BuildArrayTarget(def, rows); }
                    else
                    {
                        foreach (var (id, node) in rows) UpsertInto(def, existing, id, Clone(node));
                        target = existing;
                    }
                }
                await SaveRootAsync(def, target, ct).ConfigureAwait(false);
                foreach (var (id, node) in rows)
                    await AfterWriteAsync(def, id, node, ct).ConfigureAwait(false);
            }
        }
        finally { _writeGate.Release(); }
        return Array.Empty<string>();
    }

    // ---- privacy wipe (destructive; UI must confirm first — IPrivacyService contract) ----------

    /// <summary>
    /// Erase every registered data file (plus .bak and .corrupt- quarantine copies — they hold the
    /// same personal data), the metadata sidecar, the sync queue journal, and every migration
    /// marker; then write <c>reset-marker.json</c> = {WipedAtUtc, AppVersion} as the honest proof
    /// that a wipe happened AFTER the data is gone (the marker is not a backup, it names no user
    /// and holds no values).
    /// </summary>
    public async Task<ResetMarker> ResetAllAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var def in Registry)
                await _store.DeleteFileAsync(def.FileName, ct).ConfigureAwait(false);

            await _meta.ClearAsync(ct).ConfigureAwait(false);
            await _queue.ClearAsync(ct).ConfigureAwait(false);

            // Quarantine + backup stragglers: a .corrupt-* copy of a data file is still user data.
            foreach (var def in Registry)
                foreach (var q in _store.QuarantineFilesOf(def.FileName))
                    await _store.DeleteFileAsync(q, ct).ConfigureAwait(false);

            var markerDir = Path.Combine(_store.DirectoryPath, MigrationRunner.MarkerDirectoryName);
            if (Directory.Exists(markerDir)) Directory.Delete(markerDir, recursive: true);

            var marker = new ResetMarker(_clock(), _appVersion());
            await _store.SaveObjectAsync(ResetMarkerFileName, marker, ct).ConfigureAwait(false);
            return marker;
        }
        finally { _writeGate.Release(); }
    }

    // ---- registry helpers ---------------------------------------------------------

    private static CatalogKind? FindKind(string kind) =>
        Registry.FirstOrDefault(k => string.Equals(k.Name, kind, StringComparison.OrdinalIgnoreCase));

    private async Task<JsonNode?> LoadRootAsync(CatalogKind def, CancellationToken ct)
    {
        var bytes = await _store.ReadAllBytesAsync(def.FileName, ct).ConfigureAwait(false);
        if (bytes.Length == 0) return null;
        try { return JsonNode.Parse(Encoding.UTF8.GetString(bytes), documentOptions: ReadOptions); }
        catch (JsonException) { return null; } // a corrupt file cannot be exported/edited; the V2 result API quarantines on typed reads
    }

    private Task SaveRootAsync(CatalogKind def, JsonNode root, CancellationToken ct) =>
        _store.WriteAllTextAtomicAsync(def.FileName, root.ToJsonString(WriteIndentedJson), ct);

    private static JsonArray GetArray(CatalogKind def, JsonNode root)
    {
        if (def.ArrayProperty is null)
            return root as JsonArray ?? throw new InvalidOperationException($"store {def.Name} is not an array");
        var obj = root as JsonObject ?? throw new InvalidOperationException($"store {def.Name} is not an object");
        if (obj[def.ArrayProperty] is not JsonArray arr)
        {
            arr = new JsonArray();
            obj[def.ArrayProperty] = arr;
        }
        return arr;
    }

    private JsonNode BuildArrayTarget(CatalogKind def, List<(string Id, JsonNode Node)> rows)
    {
        var arr = new JsonArray();
        foreach (var (_, node) in rows) arr.Add(Clone(node));
        if (def.ArrayProperty is null) return arr;
        return new JsonObject { [def.ArrayProperty] = arr };
    }

    private static IEnumerable<(string Id, JsonNode El)> EnumerateEntities(CatalogKind def, JsonNode root)
    {
        if (def.Singleton) { yield return (EntityId(def, root) ?? def.Name, root); yield break; }
        foreach (var el in GetArray(def, root))
        {
            if (el is null) continue;
            var id = EntityId(def, el);
            if (!string.IsNullOrWhiteSpace(id)) yield return (id!, el);
        }
    }

    private static JsonNode? FindEntity(CatalogKind def, JsonNode root, string id)
    {
        if (def.Singleton) return string.Equals(EntityId(def, root), id, StringComparison.OrdinalIgnoreCase) ? root : null;
        foreach (var el in GetArray(def, root))
            if (el is not null && string.Equals(EntityId(def, el), id, StringComparison.OrdinalIgnoreCase))
                return el;
        return null;
    }

    private static void UpsertInto(CatalogKind def, JsonNode root, string id, JsonNode entity)
    {
        var arr = GetArray(def, root);
        for (int i = 0; i < arr.Count; i++)
        {
            if (arr[i] is { } el && string.Equals(EntityId(def, el), id, StringComparison.OrdinalIgnoreCase))
            {
                arr[i] = Clone(entity);
                return;
            }
        }
        arr.Add(Clone(entity));
        if (def.Name == "history") SortArrayBy(arr, "Date");
    }

    private static bool RemoveFrom(CatalogKind def, JsonNode root, string id)
    {
        var arr = GetArray(def, root);
        for (int i = 0; i < arr.Count; i++)
            if (arr[i] is { } el && string.Equals(EntityId(def, el), id, StringComparison.OrdinalIgnoreCase))
            {
                arr.RemoveAt(i);
                return true;
            }
        return false;
    }

    private static void SortArrayBy(JsonArray arr, string prop)
    {
        var sorted = arr.OrderBy(n => n?[prop]?.ToString(), StringComparer.Ordinal).ToList();
        arr.Clear();
        foreach (var n in sorted) arr.Add(n);
    }

    /// <summary>Row identity as a machine string; Date ids normalize so hand-editing "2026-09-13T00:00:00"
    /// and "2026-09-13" address the SAME row.</summary>
    private static string? EntityId(CatalogKind def, JsonNode entity)
    {
        if (def.IdProperty is null) return def.Name;
        var raw = entity[def.IdProperty]?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
            return def.Singleton ? def.Name : null;
        if (def.IdProperty == "Date" && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return d.ToString("yyyy-MM-dd");
        return raw;
    }

    /// <summary>After a successful entity write: bump the sidecar meta and queue the change record
    /// (hash + size only — the payload bytes stay in the entity file).</summary>
    private async Task AfterWriteAsync(CatalogKind def, string id, JsonNode entity, CancellationToken ct)
    {
        var bumped = await _meta.BumpAsync(def.Name, id, _clock(), ct).ConfigureAwait(false);
        var json = entity.ToJsonString();
        await _queue.EnqueueAsync(def.Name, id, bumped.Version,
            CanonicalJson.Sha256HexOfCanonical(json),
            Encoding.UTF8.GetByteCount(json), ct).ConfigureAwait(false);
    }

    // ---- validation (rejects return machine KEYS, never prose) --------------------

    private static IReadOnlyList<string> Validate(CatalogKind def, JsonNode node, string id)
    {
        var errors = new List<string>();
        switch (def.Name)
        {
            case "goals":
                RequireString(node, def, errors, "Id");
                RequireString(node, def, errors, "Name");
                RequireRange(node, def, errors, "TargetValue", 0, 1_000_000);
                RequireRange(node, def, errors, "ProgressValue", 0, 1_000_000);
                break;
            case "habits":
                RequireString(node, def, errors, "Id");
                RequireString(node, def, errors, "Name");
                RequireRange(node, def, errors, "TimesPerWeek", 1, 7);
                break;
            case "bootcamps":
                RequireString(node, def, errors, "Id");
                RequireRange(node, def, errors, "DurationDays", 1, 3650);
                RequireRange(node, def, errors, "CurrentDay", 0, 3650);
                break;
            case "reminders":
                RequireString(node, def, errors, "Id");
                RequireString(node, def, errors, "Kind");
                RequireString(node, def, errors, "TextKey");
                break;
            case "history":
                RequireDate(node, def, errors, id);
                Range01(node, def, errors, "Completeness");
                Range01(node, def, errors, "SleepQuality");
                Range01(node, def, errors, "Stress");
                Range01(node, def, errors, "Mood");
                Range01(node, def, errors, "Energy");
                RequireRange(node, def, errors, "SleepMinutes", 0, 1440);
                RequireRange(node, def, errors, "Steps", 0, 1_000_000);
                RequireRange(node, def, errors, "ActiveMinutes", 0, 1440);
                break;
            case "manual":
                RequireDate(node, def, errors, id);
                Range01(node, def, errors, "SleepQuality");
                Range01(node, def, errors, "Stress");
                Range01(node, def, errors, "Mood");
                Range01(node, def, errors, "Energy");
                if (node["SleepMinutes"] is { } sm && sm.GetValueKind() != JsonValueKind.Null && (sm.GetValue<double>() < 0 || sm.GetValue<double>() > 1440)) errors.Add($"Error.Catalog.Range.{def.Name}.SleepMinutes");
                if (node["Steps"] is { } st && st.GetValueKind() != JsonValueKind.Null && (st.GetValue<double>() < 0 || st.GetValue<double>() > 1_000_000)) errors.Add($"Error.Catalog.Range.{def.Name}.Steps");
                break;
            // profile/settings/consents: single-object stores — shape is checked structurally
            // (must be an object); their rich validation lives with the owning lane's service.
            default:
                break;
        }
        if (node is not JsonObject) errors.Add($"Error.Catalog.Shape.{def.Name}");
        return errors;
    }

    private static void RequireString(JsonNode node, CatalogKind def, List<string> errors, string prop)
    {
        var v = node[prop];
        if (v is null || v.GetValueKind() != JsonValueKind.String || string.IsNullOrWhiteSpace(v.ToString()))
            errors.Add($"Error.Catalog.Missing.{def.Name}.{prop}");
    }

    private static void RequireRange(JsonNode node, CatalogKind def, List<string> errors, string prop, double lo, double hi)
    {
        var v = node[prop];
        if (v is null || v.GetValueKind() == JsonValueKind.Null) return; // absent = model default, legal
        if (v.GetValueKind() != JsonValueKind.Number) { errors.Add($"Error.Catalog.Shape.{def.Name}.{prop}"); return; }
        var d = v.GetValue<double>();
        if (d < lo || d > hi || double.IsNaN(d) || double.IsInfinity(d))
            errors.Add($"Error.Catalog.Range.{def.Name}.{prop}");
    }

    private static void Range01(JsonNode node, CatalogKind def, List<string> errors, string prop) =>
        RequireRange(node, def, errors, prop, 0, 1);

    private static void RequireDate(JsonNode node, CatalogKind def, List<string> errors, string id)
    {
        var v = node["Date"];
        if (v is null || v.GetValueKind() != JsonValueKind.String)
        {
            errors.Add($"Error.Catalog.Missing.{def.Name}.Date");
            return;
        }
        if (!DateTime.TryParse(v.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out _))
            errors.Add($"Error.Catalog.BadDate.{def.Name}");
    }

    private static JsonNode Clone(JsonNode node) => JsonNode.Parse(node.ToJsonString())!;

    private static readonly JsonSerializerOptions WriteIndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
