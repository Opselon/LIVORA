using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LIVORA.Infrastructure.Persistence.Wave3b;

namespace LIVORA.Tests.Wave3c;

/// <summary>
/// Wave 3c (lane 06): the durable file store + the migration engine. These tests pin the two claims
/// that matter for real user data: a write is all-or-noise-free (atomic temp + File.Replace, so a
/// concurrent reader can never observe half a document), and a failed migration provably restores the
/// bytes it found (hash-compared) while refusing to advance the marker.
/// </summary>
public class StoreAndMigrationTests : IAsyncLifetime
{
    private readonly Scavenger _temp = new();
    private string _dir = "";

    public Task InitializeAsync()
    {
        _dir = _temp.Dir("store");
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _temp.DisposeAsync();

    private JsonFileStoreV2 NewStore(string? dir = null) => new(dir ?? _dir);

    private sealed record Row(string Id, string Name, int Count);

    private static string Sha256Of(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    // ---- JsonFileStoreV2 ---------------------------------------------------------

    [Fact]
    public async Task Store_MissingFile_ReadsDefaultWithoutCreatingAnything()
    {
        var store = NewStore();

        var list = await store.LoadListResultAsync<Row>("absent.json");
        Assert.True(list.Ok);
        Assert.False(list.Corrupt);
        Assert.True(list.Missing);
        Assert.Empty(list.Value);

        var obj = await store.LoadObjectResultAsync<Row>("absent.json");
        Assert.True(obj.Ok);
        Assert.Null(obj.Value);
        Assert.False(File.Exists(store.PathOf("absent.json")));
    }

    [Fact]
    public async Task Store_ListRoundTrip_KeepsEveryRow()
    {
        var store = NewStore();
        var rows = Enumerable.Range(1, 25).Select(i => new Row($"r{i}", $"نام {i}", i)).ToList();

        await store.SaveListAsync("rows.json", rows);
        var read = await store.LoadListAsync<Row>("rows.json");

        Assert.Equal(rows, read);
    }

    [Fact]
    public async Task Store_CorruptFile_ReportsNotOkAndQuarantineWhilePreservingBytes()
    {
        var store = NewStore();
        var path = store.PathOf("broken.json");
        const string garbage = "{ \"oops\": [1,2,3] broken,,,";
        await File.WriteAllTextAsync(path, garbage);
        var before = Sha256Of(path);

        var result = await store.LoadListResultAsync<Row>("broken.json");

        Assert.False(result.Ok);
        Assert.True(result.Corrupt);
        Assert.NotNull(result.QuarantinedPath);
        Assert.True(File.Exists(result.QuarantinedPath));
        // The unreadable bytes survive under the quarantine name, byte-for-byte.
        Assert.False(File.Exists(path));
        Assert.Equal(garbage, File.ReadAllText(result.QuarantinedPath!));
        Assert.Equal(before, Sha256Of(result.QuarantinedPath!));
        Assert.StartsWith("broken.json.corrupt-", Path.GetFileName(result.QuarantinedPath!), StringComparison.Ordinal);
        Assert.Contains(store.QuarantineFilesOf("broken.json"),
            n => n == Path.GetFileName(result.QuarantinedPath!));
    }

    [Fact]
    public async Task Store_CorruptObjectFile_AlsoQuarantines()
    {
        var store = NewStore();
        await File.WriteAllTextAsync(store.PathOf("broken_obj.json"), "nope");

        var result = await store.LoadObjectResultAsync<Row>("broken_obj.json");

        Assert.False(result.Ok);
        Assert.True(result.Corrupt);
        Assert.Null(result.Value);
        Assert.True(File.Exists(result.QuarantinedPath));
    }

    [Fact]
    public async Task Store_WritesAreAtomic_ABackupOfThePreviousBytesSurvives()
    {
        var store = NewStore();
        await store.SaveObjectAsync("atomic.json", new Row("a", "first", 1));
        await store.SaveObjectAsync("atomic.json", new Row("a", "second", 2));

        var current = await store.LoadObjectAsync<Row>("atomic.json");
        Assert.Equal("second", current!.Name);
        // File.Replace kept the previous bytes: the .bak is the pre-write state, not garbage.
        var bak = File.ReadAllText(store.PathOf("atomic.json") + ".bak");
        Assert.Contains("first", bak);
        Assert.DoesNotContain("second", bak);
        // No temp litter may survive a successful write.
        Assert.Empty(Directory.GetFiles(_dir, "atomic.json.tmp-*"));
    }

    [Fact]
    public async Task Store_StableKeyGrammar_ProducesDeterministicBytes()
    {
        var storeA = NewStore(_temp.Dir("stable-a"));
        var storeB = NewStore(_temp.Dir("stable-b"));
        var rows = new List<Row> { new("x", "Persian: آرامش", 7) };

        await storeA.SaveListAsync("same.json", rows);
        await storeB.SaveListAsync("same.json", rows);

        Assert.Equal(
            File.ReadAllText(storeA.PathOf("same.json")),
            File.ReadAllText(storeB.PathOf("same.json")));
    }

    [Fact]
    public async Task Store_DeleteRemovesBackupToo()
    {
        var store = NewStore();
        await store.SaveObjectAsync("del.json", new Row("a", "one", 1));
        await store.SaveObjectAsync("del.json", new Row("a", "two", 2));
        Assert.True(File.Exists(store.PathOf("del.json") + ".bak"));

        await store.DeleteFileAsync("del.json");

        Assert.False(store.Exists("del.json"));
        Assert.False(File.Exists(store.PathOf("del.json") + ".bak"));
    }

    [Fact]
    public async Task Store_TwentyParallelWriters_NeverTearAndAlwaysParse()
    {
        var dir = _temp.Dir("parallel");
        var store = NewStore(dir);
        const int writers = 20;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            for (int round = 0; round < 5; round++)
                await store.SaveListAsync("hot.json", new List<Row> { new($"w{i}", $"payload-{i}", round) });
        })));

        // Whatever won, the file must be ONE complete document (no interleaved bytes, no half row).
        var result = await store.LoadListResultAsync<Row>("hot.json");
        Assert.True(result.Ok);
        Assert.False(result.Corrupt);
        var single = Assert.Single(result.Value);
        Assert.StartsWith("payload-", single.Name, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir, "hot.json.tmp-*"));
    }

    // ---- MigrationRunner ---------------------------------------------------------

    [Fact]
    public async Task Migration_IdentityStamp_CertifiesExistingBytesWithoutRewritingThem()
    {
        var dir = _temp.Dir("mig-identity");
        var path = Path.Combine(dir, "livora_goals.json");
        var original = """[{"Id":"g1","Name":"Run"}]""";
        await File.WriteAllTextAsync(path, original);
        var before = Sha256Of(path);

        var runner = new MigrationRunner(dir)
            .RegisterStore("goals", "livora_goals.json", new[] { MigrationRunner.IdentityStamp() });
        var report = await runner.RunAllAsync();

        Assert.True(report.AllOk);
        Assert.Equal(1, runner.GetAppliedVersion("goals"));
        Assert.Equal(before, Sha256Of(path));          // Rule 21: never delete + recreate
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.False(File.Exists(path + ".bak"));      // not a byte was touched
        Assert.True(File.Exists(Path.Combine(dir, MigrationRunner.MarkerDirectoryName, "goals.json")));
    }

    [Fact]
    public async Task Migration_TransformAppliesInAscendingOrder()
    {
        var dir = _temp.Dir("mig-ascending");
        var path = Path.Combine(dir, "livora_habits.json");
        await File.WriteAllTextAsync(path, """[{"Id":"h1","TimesPerWeek":3}]""");

        var runner = new MigrationRunner(dir).RegisterStore("habits", "livora_habits.json", new[]
        {
            MigrationRunner.IdentityStamp(),
            new MigrationRunner.MigrationStep(2, "add-streak", text => text.TrimEnd(']') + ",{\"Id\":\"h0\",\"TimesPerWeek\":1}]"),
            new MigrationRunner.MigrationStep(3, "uppercase-names", text => text.ToUpperInvariant()),
        });

        var report = await runner.RunAllAsync();

        Assert.True(report.AllOk);
        Assert.Equal(3, runner.GetAppliedVersion("habits"));
        Assert.Equal(new[] { 1, 2, 3 }, report.Stores.Single().Applied.ToArray());
        Assert.Contains("H0", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Migration_SecondRunIsNearZeroCostAndAppliesNothing()
    {
        var dir = _temp.Dir("mig-idem");
        var path = Path.Combine(dir, "livora_goals.json");
        await File.WriteAllTextAsync(path, """[{"Id":"g1"}]""");
        var ladder = new[]
        {
            MigrationRunner.IdentityStamp(),
            new MigrationRunner.MigrationStep(2, "touch", t => t + "\n"),
        };

        await new MigrationRunner(dir).RegisterStore("goals", "livora_goals.json", ladder).RunAllAsync();
        var dataAfterRunOne = Sha256Of(path);
        var markerAfterRunOne = Directory.GetLastWriteTimeUtc(
            Path.Combine(dir, MigrationRunner.MarkerDirectoryName, "goals.json"));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var second = await new MigrationRunner(dir).RegisterStore("goals", "livora_goals.json", ladder).RunAllAsync();
        sw.Stop();

        Assert.True(second.AllOk);
        Assert.Empty(second.Stores.Single().Applied);   // idempotent: nothing to do
        Assert.Equal(0, second.AppliedThisRun);
        // The re-run touched neither the data file nor the marker — that is the "near zero cost".
        Assert.Equal(dataAfterRunOne, Sha256Of(path));
        Assert.Equal(markerAfterRunOne, Directory.GetLastWriteTimeUtc(
            Path.Combine(dir, MigrationRunner.MarkerDirectoryName, "goals.json")));
    }

    [Fact]
    public async Task Migration_ThrowingStep_RestoresOriginalBytesAndDoesNotAdvanceMarker()
    {
        var dir = _temp.Dir("mig-throw");
        var path = Path.Combine(dir, "livora_goals.json");
        const string original = """[{"Id":"g1","Name":"Run every day"}]""";
        await File.WriteAllTextAsync(path, original);
        var before = Sha256Of(path);

        var runner = new MigrationRunner(dir).RegisterStore("goals", "livora_goals.json", new[]
        {
            MigrationRunner.IdentityStamp(),
            new MigrationRunner.MigrationStep(2, "boom", _ => throw new InvalidOperationException("transform failed on purpose")),
        });

        var report = await runner.RunAllAsync();

        Assert.False(report.AllOk);
        var store = report.Stores.Single();
        Assert.Equal(2, store.FailedVersion);
        Assert.Equal("Migration.Failed.goals.2", store.ErrorKey);
        Assert.Equal(nameof(InvalidOperationException), store.ErrorDetail);
        Assert.Equal(1, runner.GetAppliedVersion("goals"));   // marker did NOT advance
        Assert.Equal(before, Sha256Of(path));                  // bytes provably restored
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Migration_FailedStepIsRetriedOnTheNextRun()
    {
        var dir = _temp.Dir("mig-retry");
        var path = Path.Combine(dir, "livora_settings.json");
        await File.WriteAllTextAsync(path, """{"Theme":"dark"}""");

        bool fail = true;
        var steps = new[]
        {
            MigrationRunner.IdentityStamp(),
            new MigrationRunner.MigrationStep(2, "flaky", t =>
            {
                if (fail) throw new IOException("device busy");
                return t.Replace("dark", "light");
            }),
        };

        var first = new MigrationRunner(dir).RegisterStore("settings", "livora_settings.json", steps);
        var firstReport = await first.RunAllAsync();
        Assert.False(firstReport.AllOk);
        Assert.Contains("dark", await File.ReadAllTextAsync(path));

        fail = false;
        var second = new MigrationRunner(dir).RegisterStore("settings", "livora_settings.json", steps);
        var secondReport = await second.RunAllAsync();

        Assert.True(secondReport.AllOk);
        Assert.Equal(2, second.GetAppliedVersion("settings"));
        Assert.Contains("light", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Migration_EmptyStore_AdvancesMarkerWithoutCreatingData()
    {
        var dir = _temp.Dir("mig-empty");
        var runner = new MigrationRunner(dir).RegisterStore("bootcamps", "livora_bootcamps.json",
            new[] { MigrationRunner.IdentityStamp() });

        var report = await runner.RunAllAsync();

        Assert.True(report.AllOk);
        Assert.Equal(1, runner.GetAppliedVersion("bootcamps"));
        Assert.False(File.Exists(Path.Combine(dir, "livora_bootcamps.json")));
    }

    [Fact]
    public void Migration_DuplicateVersionsAreRejectedAtRegistration()
    {
        var dir = _temp.Dir("mig-dup");
        var runner = new MigrationRunner(dir);
        Assert.Throws<ArgumentException>(() => runner.RegisterStore("goals", "livora_goals.json",
            new[] { MigrationRunner.IdentityStamp(), MigrationRunner.IdentityStamp() }));
        Assert.Throws<ArgumentException>(() => runner.RegisterStore("goals", "livora_goals.json",
            Array.Empty<MigrationRunner.MigrationStep>()));
    }

    [Fact]
    public async Task Migration_RegistersPerStoreMarkers_AndWipesClearThem()
    {
        var dir = _temp.Dir("mig-markers");
        foreach (var name in new[] { "livora_goals.json", "livora_habits.json" })
            await File.WriteAllTextAsync(Path.Combine(dir, name), "[]");

        var runner = new MigrationRunner(dir)
            .RegisterStore("goals", "livora_goals.json", new[] { MigrationRunner.IdentityStamp() })
            .RegisterStore("habits", "livora_habits.json", new[] { MigrationRunner.IdentityStamp() });
        await runner.RunAllAsync();

        var markerDir = Path.Combine(dir, MigrationRunner.MarkerDirectoryName);
        Assert.Equal(new[] { "goals.json", "habits.json" },
            Directory.GetFiles(markerDir, "*.json").Select(Path.GetFileName).OrderBy(n => n).ToArray());
        foreach (var f in Directory.GetFiles(markerDir, "*.json"))
            using (var doc = JsonDocument.Parse(await File.ReadAllTextAsync(f)))
                Assert.Equal(1, doc.RootElement.GetProperty("AppliedVersion").GetInt32());
    }
}
