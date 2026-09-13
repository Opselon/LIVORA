using System.Text.Json;
using LIVORA.Application.Sync;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Persistence.Wave3b;

namespace LIVORA.Tests.Wave3c;

/// <summary>
/// Wave 3c (lane 06): the local data catalog — the one seam the advanced-mode editor, the privacy
/// export and the wipe all go through. Pinned here: validate-before-write (a reject touches no file
/// and no meta), write ⇒ meta bump + queue record (hash only), export/import round-trips at
/// schemaVersion 2, all-or-nothing imports, and the privacy wipe leaving ZERO user bytes plus the
/// honest reset marker.
/// </summary>
public class LocalDataCatalogTests : IAsyncLifetime, IDisposable
{
    private readonly Scavenger _temp = new();
    private string _dir = "";
    private JsonFileStoreV2 _store = null!;
    private MetaIndex _meta = null!;
    private SyncQueue _queue = null!;
    private LocalDataCatalogService _catalog = null!;

    private static readonly DateTime FixedNow = new(2026, 9, 13, 9, 30, 0, DateTimeKind.Utc);

    public Task InitializeAsync()
    {
        _dir = _temp.Dir("catalog");
        Build();
        return Task.CompletedTask;
    }

    private void Build() => Build(_dir);

    private void Build(string dir)
    {
        // Build() also runs after a wipe inside one test — release the previous queue's journal
        // handle first so the rebuild (and the temp scavenger) never races an open append stream.
        _queue?.Dispose();
        _store = new JsonFileStoreV2(dir);
        _meta = new MetaIndex(dir, "catalog_meta.json");
        _queue = new SyncQueue(Path.Combine(dir, "sync"), _meta);
        _catalog = new LocalDataCatalogService(_store, _meta, _queue,
            appVersion: () => "9.9-test", clock: () => FixedNow);
    }

    public void Dispose() => _queue?.Dispose();

    public Task DisposeAsync()
    {
        // xunit calls DisposeAsync BEFORE Dispose: release the journal appender first, or the
        // scavenger races an open append stream and the "all temp dirs disposed" budget trips.
        _queue?.Dispose();
        _queue = null!;
        return _temp.DisposeAsync();
    }

    private const string GoalJson = """{"Id":"g-1","Name":"Sleep 8h","TargetValue":8,"ProgressValue":3}""";
    private const string HistoryJson = """{"Date":"2026-09-01","Origin":"Mock","Completeness":1,"SleepMinutes":450,"Steps":8000}""";

    // ---- registry ---------------------------------------------------------------

    [Fact]
    public async Task Catalog_ExposesEveryRequiredKind()
    {
        var kinds = await _catalog.GetKindsAsync();
        foreach (var required in new[] { "goals", "habits", "bootcamps", "profile", "history", "manual", "settings", "consents", "reminders" })
            Assert.Contains(required, kinds);
    }

    // ---- entity export/import ----------------------------------------------------

    [Fact]
    public async Task Catalog_ImportThenExport_RoundTripsOneEntity()
    {
        var errors = await _catalog.ImportEntityAsync("goals", GoalJson);
        Assert.Empty(errors);

        var exported = await _catalog.ExportEntityAsync("goals", "g-1");
        using var doc = JsonDocument.Parse(exported);
        Assert.Equal("Sleep 8h", doc.RootElement.GetProperty("Name").GetString());
    }

    [Fact]
    public async Task Catalog_Import_BumpsMetaAndEnqueuesHashOnly()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);

        var meta = await _meta.GetAsync("goals", "g-1");
        Assert.Equal(1, meta.Version);
        Assert.Equal(SyncState.Pending, meta.SyncState);
        Assert.Equal(FixedNow, meta.UpdatedAtUtc);

        var pending = await _queue.SnapshotPendingAsync();
        var env = Assert.Single(pending);
        Assert.Equal("goals", env.EntityKind);
        Assert.Equal("g-1", env.EntityId);
        Assert.Equal(1, env.LocalVersion);
        Assert.Matches("^[0-9a-f]{64}$", env.PayloadHash);
        // The queue must be able to rebuild from the store: nothing but bookkeeping is in it.
        // Read through the queue's own inspector: the appender is still open, so a plain
        // File.ReadAllText would fight its share mask.
        var journal = string.Join('\n', await _queue.ReadJournalLinesAsync());
        Assert.DoesNotContain("Sleep 8h", journal);
    }

    [Fact]
    public async Task Catalog_ReimportOfSameId_ReBumpsInsteadOfDuplicating()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        var updated = GoalJson.Replace("\"ProgressValue\":3", "\"ProgressValue\":5");
        Assert.Empty(await _catalog.ImportEntityAsync("goals", updated));

        var meta = await _meta.GetAsync("goals", "g-1");
        Assert.Equal(2, meta.Version);
        var exported = await _catalog.ExportEntityAsync("goals", "g-1");
        using var doc = JsonDocument.Parse(exported);
        Assert.Equal(5, doc.RootElement.GetProperty("ProgressValue").GetInt32());
        // Still ONE row in the store (upsert, not append).
        var list = await _store.LoadListAsync<JsonElement>(AppConstants.GoalsFile);
        Assert.Single(list);
    }

    [Fact]
    public async Task Catalog_ValidationReject_TouchesNoFileNoMetaNoQueue()
    {
        var bad = """{"Id":"g-bad","Name":"","TargetValue":99999999}""";
        var errors = await _catalog.ImportEntityAsync("goals", bad);

        Assert.Contains("Error.Catalog.Missing.goals.Name", errors);
        Assert.Contains("Error.Catalog.Range.goals.TargetValue", errors);
        Assert.False(_store.Exists(AppConstants.GoalsFile));
        Assert.True((await _meta.GetAsync("goals", "g-bad")).IsAbsent);
        Assert.Equal(0, await _queue.PendingCountAsync());
    }

    [Fact]
    public async Task Catalog_HistoryKeysOnNormalizedDate()
    {
        Assert.Empty(await _catalog.ImportEntityAsync("history", HistoryJson));
        var exported = await _catalog.ExportEntityAsync("history", "2026-09-01");
        Assert.Contains("450", exported);

        // Same day spelled with a time component addresses the SAME row.
        var sameDay = HistoryJson.Replace("\"2026-09-01\"", "\"2026-09-01T00:00:00\"");
        Assert.Empty(await _catalog.ImportEntityAsync("history", sameDay));
        var file = await _store.LoadObjectAsync<JsonElement>(AppConstants.HistoryFile);
        Assert.Single(file.GetProperty("Records").EnumerateArray());
    }

    [Fact]
    public async Task Catalog_Delete_RemovesEntityMetaAndQueueRow()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        var errors = await _catalog.DeleteEntityAsync("goals", new[] { "g-1" });
        Assert.Empty(errors);

        Assert.Equal("", await _catalog.ExportEntityAsync("goals", "g-1"));
        Assert.True((await _meta.GetAsync("goals", "g-1")).IsAbsent);
        Assert.Equal(0, await _queue.PendingCountAsync());
    }

    [Fact]
    public async Task Catalog_DeleteUnknownId_ReturnsNotFoundKey()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        var errors = await _catalog.DeleteEntityAsync("goals", new[] { "ghost" });
        Assert.Contains("Error.Catalog.NotFound.ghost", errors);
        Assert.NotEmpty(await _catalog.ExportEntityAsync("goals", "g-1"));
    }

    // ---- whole-catalog export/import ---------------------------------------------

    [Fact]
    public async Task Catalog_ExportAll_CarriesSchemaVersion2AndEveryKind()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        await _catalog.ImportEntityAsync("history", HistoryJson);

        using var doc = JsonDocument.Parse(await _catalog.ExportAllAsync());
        Assert.Equal(2, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("9.9-test", doc.RootElement.GetProperty("appVersion").GetString());
        var kinds = doc.RootElement.GetProperty("kinds");
        Assert.Equal(9, kinds.EnumerateObject().Count());
        Assert.Equal(1, kinds.GetProperty("goals").GetProperty("entities").GetArrayLength());
        Assert.Equal(1, kinds.GetProperty("history").GetProperty("entities").GetArrayLength());
    }

    [Fact]
    public async Task Catalog_ImportAllMerge_KeepsUntouchedRowsAndReplacesSameId()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        await _catalog.ImportEntityAsync("habits", """{"Id":"h-1","Name":"Read","TimesPerWeek":3}""");

        var backup = """
        {"schemaVersion":2,"kinds":{"goals":{"entities":[{"Id":"g-1","Name":"Sleep 9h","TargetValue":9,"ProgressValue":0}]},
        "bootcamps":{"entities":[{"Id":"b-1","Name":"Reset","DurationDays":30,"CurrentDay":0}]}}}
        """;
        Assert.Empty(await _catalog.ImportAllAsync(backup, merge: true));

        using (var doc = JsonDocument.Parse(await _catalog.ExportEntityAsync("goals", "g-1")))
            Assert.Equal("Sleep 9h", doc.RootElement.GetProperty("Name").GetString());
        Assert.Contains("Read", await _catalog.ExportEntityAsync("habits", "h-1")); // merge did not clear other rows
        Assert.Contains("Reset", await _catalog.ExportEntityAsync("bootcamps", "b-1"));
    }

    [Fact]
    public async Task Catalog_ImportAllReplace_DropsRowsNotInBackup()
    {
        await _catalog.ImportEntityAsync("habits", """{"Id":"h-1","Name":"Read","TimesPerWeek":3}""");
        var backup = """{"schemaVersion":2,"kinds":{"habits":{"entities":[{"Id":"h-2","Name":"Run","TimesPerWeek":5}]}}}""";

        Assert.Empty(await _catalog.ImportAllAsync(backup, merge: false));

        Assert.Equal("", await _catalog.ExportEntityAsync("habits", "h-1"));
        Assert.Contains("Run", await _catalog.ExportEntityAsync("habits", "h-2"));
    }

    [Fact]
    public async Task Catalog_ImportAllInvalid_IsAllOrNothing()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        // habits row valid, goals row invalid (range) → NOTHING may change.
        var backup = """
        {"schemaVersion":2,"kinds":{
          "habits":{"entities":[{"Id":"h-9","Name":"Meditate","TimesPerWeek":2}]},
          "goals":{"entities":[{"Id":"g-2","Name":"Broken","TimesPerWeek":0,"TargetValue":-5}]}}}
        """;
        var errors = await _catalog.ImportAllAsync(backup, merge: true);

        Assert.Contains(errors, e => e.StartsWith("Error.Catalog.Range.goals", StringComparison.Ordinal));
        Assert.Equal("", await _catalog.ExportEntityAsync("habits", "h-9"));   // phase 1 rejected → nothing applied
        var meta = await _meta.GetAsync("habits", "h-9");
        Assert.True(meta.IsAbsent);
        // ...and the original goal is untouched.
        Assert.Contains("Sleep 8h", await _catalog.ExportEntityAsync("goals", "g-1"));
    }

    [Fact]
    public async Task Catalog_ImportAll_RejectsGarbageAndFutureSchema()
    {
        Assert.Contains("Error.Catalog.InvalidJson", await _catalog.ImportAllAsync("{{{", merge: true));
        Assert.Contains("Error.Catalog.InvalidJson", await _catalog.ImportAllAsync(null!, merge: true));
        Assert.Contains("Error.Catalog.SchemaVersionTooNew",
            await _catalog.ImportAllAsync("""{"schemaVersion":99,"kinds":{}}""", merge: true));
    }

    // ---- privacy wipe -------------------------------------------------------------

    [Fact]
    public async Task Catalog_ResetAll_LeavesZeroUserDataAndWritesHonestMarker()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        await _catalog.ImportEntityAsync("history", HistoryJson);
        await _store.WriteAllTextAtomicAsync("livora_profile.json", """{"Id":"user-1","Name":"Sara"}""");

        // Force a corrupt-quarantine copy + a .bak so the wipe has to chase every shadow too.
        await File.WriteAllTextAsync(_store.PathOf("livora_goals.json.corrupt-TEST"), "secret leftovers");
        await _store.SaveObjectAsync("livora_settings.json", new { Theme = "dark" });
        await _store.SaveObjectAsync("livora_settings.json", new { Theme = "light" }); // creates .bak

        var marker = await _catalog.ResetAllAsync();

        Assert.Equal(FixedNow, marker.WipedAtUtc);
        Assert.Equal("9.9-test", marker.AppVersion);

        // Every registered data file — and every copy of one — is gone.
        foreach (var file in new[]
                 {
                     AppConstants.GoalsFile, AppConstants.HabitsFile, AppConstants.BootcampsFile,
                     AppConstants.ProfileFile, AppConstants.HistoryFile,
                     LocalDataCatalogService.ManualFileName, AppConstants.SettingsFileName,
                     LocalDataCatalogService.ConsentsFileName, "livora_reminders.json",
                 })
        {
            Assert.False(_store.Exists(file), $"{file} survived the wipe");
            Assert.False(File.Exists(_store.PathOf(file) + ".bak"), $"{file}.bak survived the wipe");
        }
        Assert.Empty(_store.QuarantineFilesOf(AppConstants.GoalsFile));
        Assert.False(File.Exists(_store.PathOf("livora_goals.json.corrupt-TEST")));

        // queue + meta are gone too (a wipe that leaves "pending changes" would lie).
        Assert.Equal(0, await _queue.CountAsync());
        Assert.Equal(0, await _meta.CountAsync());
        Assert.False(File.Exists(_queue.JournalPath));
        Assert.False(File.Exists(_meta.FilePath));

        // The marker itself is the only survivor and holds no user data.
        var resetJson = await File.ReadAllTextAsync(_store.PathOf(LocalDataCatalogService.ResetMarkerFileName));
        Assert.Contains("WipedAtUtc", resetJson);
        Assert.Contains("9.9-test", resetJson);
        Assert.DoesNotContain("Sara", resetJson);
        Assert.DoesNotContain("Sleep 8h", resetJson);

        // And the app honestly restarts empty on top of the marker.
        Build();
        Assert.Equal("", await _catalog.ExportEntityAsync("goals", "g-1"));
        using var fresh = JsonDocument.Parse(await _catalog.ExportAllAsync());
        Assert.Equal(0, fresh.RootElement.GetProperty("kinds").GetProperty("goals").GetProperty("entities").GetArrayLength());
    }

    [Fact]
    public async Task Catalog_ResetAll_RemovesMigrationMarkers()
    {
        await _store.WriteAllTextAtomicAsync(AppConstants.GoalsFile, "[]");
        await new MigrationRunner(_dir)
            .RegisterStore("goals", AppConstants.GoalsFile, new[] { MigrationRunner.IdentityStamp() })
            .RunAllAsync();
        var markerPath = Path.Combine(_dir, MigrationRunner.MarkerDirectoryName, "goals.json");
        Assert.True(File.Exists(markerPath));

        await _catalog.ResetAllAsync();

        Assert.False(Directory.Exists(Path.Combine(_dir, MigrationRunner.MarkerDirectoryName)));
    }

    [Fact]
    public async Task Catalog_WipeThenLegacyReadsAreQuietAgain()
    {
        await _catalog.ImportEntityAsync("goals", GoalJson);
        Assert.Equal(SyncState.Pending, (await _meta.GetAsync("goals", "g-1")).SyncState);
        await _catalog.ResetAllAsync();

        Assert.True((await _meta.GetAsync("goals", "g-1")).IsAbsent); // Version 0, Clean — no ghosts
        Assert.Equal(0, await _queue.PendingCountAsync());
    }
}
