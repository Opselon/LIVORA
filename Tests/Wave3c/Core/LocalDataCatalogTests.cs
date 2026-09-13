using System.Text.Json;
using LIVORA.Domain.Constants;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// The local data catalog (lane 01): strict input, identity-preserving export/import, and a whole
/// store that can be wiped and restored. The rejected shapes below are what a hostile or corrupt
/// payload actually looks like — a hand-edit in a file manager, a truncated download, a backup from
/// a newer build — and each must cost the user NOTHING (no bytes written) while naming a
/// localization key the UI can render.
///
/// Fixtures are written in the exact byte shape the owning stores produce (System.Text.Json,
/// enums as strings, derived read-only members present), because the catalog is a view over those
/// files: a fixture that only looks like the real thing tests nothing.
/// </summary>
public class LocalDataCatalogTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("catalog");

    /// <summary>Fixed "today" so the date-range rules are deterministic (the catalog takes a clock).</summary>
    private static readonly DateTime Today = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private LocalDataCatalog Catalog() => new(Lane01Harness.RootStore(_dir), () => Today);

    // ---- fixtures in the exact shape the stores write -----------------------------

    private static string GoalJson(string id = "g1") =>
        "{" +
        "\"Id\":\"" + id + "\"," +
        "\"Name\":\"Sleep more\",\"Description\":\"\"," +
        "\"Category\":\"Sleep\",\"TargetValue\":8,\"ProgressValue\":3,\"Period\":\"Day\",\"Unit\":\"Hours\"," +
        "\"Deadline\":null,\"IsArchived\":false,\"Measurement\":\"ManualCounter\",\"MetricKey\":null," +
        "\"StrategyKey\":null,\"FrequencyPerPeriod\":1,\"Fraction\":0.375,\"Status\":\"AtRisk\"}";

    private static string HabitJson(string id = "h1") =>
        "{\"Id\":\"" + id + "\",\"Name\":\"Morning walk\",\"NameKey\":null,\"Frequency\":\"Daily\"," +
        "\"TimesPerWeek\":7,\"Completions\":[\"2026-04-29T00:00:00\"],\"CompletionLog\":[\"2026-04-29T07:15:00\"]," +
        "\"CurrentStreak\":1}";

    private static string BootcampJson(string id = "b1") =>
        "{\"Id\":\"" + id + "\",\"TitleKey\":\"Bootcamp.Title.Sleep\",\"DescriptionKey\":\"Bootcamp.Desc.Sleep\"," +
        "\"Category\":\"Sleep\",\"DurationDays\":2,\"Difficulty\":\"Beginner\",\"CurrentDay\":1,\"IsEnrolled\":true," +
        "\"Days\":[" +
        "{\"DayNumber\":1,\"PlanTitleKey\":\"Bootcamp.Plan.Walk\",\"PlanDescriptionKey\":\"Bootcamp.Plan.Walk.Desc\",\"TargetMinutes\":20,\"IsAdapted\":false,\"IsCompleted\":false}," +
        "{\"DayNumber\":2,\"PlanTitleKey\":\"Bootcamp.Plan.Walk\",\"PlanDescriptionKey\":\"Bootcamp.Plan.Walk.Desc\",\"TargetMinutes\":25,\"IsAdapted\":false,\"IsCompleted\":false}]," +
        "\"CreatorName\":\"LIVORA\",\"GoalMetricKey\":\"sleep.minutes\",\"AdaptationRuleKeys\":[\"Rule.LowRecoveryReduce\"]," +
        "\"WasAdaptedToday\":false,\"CompletionFraction\":0.5,\"Today\":null}";

    private static string HistoryJson(string date = "2026-04-30T00:00:00") =>
        "{\"Date\":\"" + date + "\",\"Origin\":\"Mock\",\"Completeness\":0.8,\"SleepMinutes\":420," +
        "\"SleepQuality\":0.6,\"SleepConsistency\":0.5,\"BedtimeMinutesOfDay\":1330,\"Steps\":6000," +
        "\"ActiveMinutes\":30,\"RecoveryScore\":62,\"RestingHeartRate\":null,\"HrvMs\":null,\"Stress\":0.4," +
        "\"Mood\":0.6,\"Energy\":0.5,\"CompletedHabitIds\":[\"h1\"],\"AdvancedGoalIds\":[]," +
        "\"BootcampId\":null,\"BootcampDayNumber\":null,\"BootcampDayWasAdapted\":false," +
        "\"InsightTopic\":null,\"InsightPriority\":null}";

    private static string ManualJson(string date = "2026-04-30T00:00:00") =>
        "{\"Date\":\"" + date + "\",\"SleepMinutes\":400,\"Steps\":5000,\"ActiveMinutes\":25," +
        "\"SleepQuality\":0.7,\"Mood\":0.5,\"Energy\":0.6,\"Stress\":0.3,\"Note\":\"ok\"," +
        "\"SavedAtUtc\":\"2026-04-30T21:00:00Z\",\"Origin\":\"Manual\"}";

    private static string ConsentJson(string category = "AiProcessing", string decision = "Granted") =>
        "{\"Category\":\"" + category + "\",\"Decision\":\"" + decision + "\",\"UpdatedAtUtc\":\"2026-05-01T09:00:00Z\"}";

    private static string ReminderJson(string id = "r1") =>
        "{\"Id\":\"" + id + "\",\"Kind\":\"habit\",\"TargetId\":\"h1\",\"Enabled\":true," +
        "\"TimeOfDay\":\"20:00:00\",\"DaysMask\":127,\"TextKey\":\"Reminders.Text.Habit\",\"TextArgs\":[\"Morning walk\"]}";

    private static string SettingsJson() =>
        "{\"preferred_language\":\"Persian\",\"onboarding_completed\":true,\"theme_mode\":2}";

    private void SeedAll()
    {
        var store = Lane01Harness.RootStore(_dir);
        store.WriteRawAtomic(AppConstants.GoalsFile, "[" + GoalJson() + "," + GoalJson("g2") + "]");
        store.WriteRawAtomic(AppConstants.HabitsFile, "[" + HabitJson() + "]");
        store.WriteRawAtomic(AppConstants.BootcampsFile, "[" + BootcampJson() + "]");
        store.WriteRawAtomic(AppConstants.HistoryFile, "[" + HistoryJson() + "]");
        store.WriteRawAtomic(LocalDataCatalog.ManualEntriesFileName, "[" + ManualJson() + "]");
        store.WriteRawAtomic(AppConstants.SettingsFileName, SettingsJson());
        store.WriteRawAtomic(ConsentStore.FileName, "[" + ConsentJson() + "]");
        store.WriteRawAtomic(LocalDataCatalog.ReminderStoreFileName,
            "{\"Reminders\":[" + ReminderJson() + "],\"FiredDays\":{\"habit|h1\":\"2026-04-30T00:00:00\"}," +
            "\"PermissionAsked\":true,\"ScheduledIds\":{}}");
    }

    // ---- kinds + export ---------------------------------------------------------

    [Fact]
    public async Task GetKinds_ListsTheEightDocumentedKinds()
    {
        Assert.Equal(
            new[] { "goals", "habits", "bootcamps", "history", "manual", "settings", "consents", "reminders" },
            await Catalog().GetKindsAsync());
    }

    [Fact]
    public async Task ExportEntity_IsPrettyJsonOfTheStoredEntity()
    {
        SeedAll();
        var json = await Catalog().ExportEntityAsync("goals", "g1");
        Assert.Contains("\n", json);                       // pretty, not one line
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Sleep more", doc.RootElement.GetProperty("Name").GetString());
        Assert.Equal("g1", doc.RootElement.GetProperty("Id").GetString());
    }

    [Fact]
    public async Task ExportEntity_UnknownIdFailsWithAKey_NotProse()
    {
        SeedAll();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Catalog().ExportEntityAsync("goals", "missing"));
        Assert.StartsWith("Norm.Reject.NotFound", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAll_CarriesSchemaVersionAndEveryKind()
    {
        SeedAll();
        var json = await Catalog().ExportAllAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(LocalDataCatalog.SchemaVersion, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("LIVORA", doc.RootElement.GetProperty("appName").GetString());
        Assert.EndsWith("Z", doc.RootElement.GetProperty("exportedAtUtc").GetString());
        var kinds = doc.RootElement.GetProperty("kinds");
        foreach (var kind in LocalDataCatalog.Kinds)
            Assert.True(kinds.TryGetProperty(kind, out _), "export missing " + kind);
        Assert.Equal(2, kinds.GetProperty("goals").GetArrayLength());
        Assert.Contains("\n", json);   // a portable document a human can actually read
    }

    [Fact]
    public void CatalogFileNamesMatchTheStoreOwners()
    {
        // The catalog is a VIEW over the existing files: a name that drifts from its owner means the
        // catalog silently reads a file nothing writes — and the privacy wipe keeps that data.
        Assert.Equal(AppConstants.GoalsFile, LocalDataCatalog.FileForKind("goals"));
        Assert.Equal(AppConstants.HabitsFile, LocalDataCatalog.FileForKind("habits"));
        Assert.Equal(AppConstants.BootcampsFile, LocalDataCatalog.FileForKind("bootcamps"));
        Assert.Equal(AppConstants.HistoryFile, LocalDataCatalog.FileForKind("history"));
        Assert.Equal(AppConstants.SettingsFileName, LocalDataCatalog.FileForKind("settings"));
        Assert.Equal(ManualEntryStoreFileNameValue(), LocalDataCatalog.FileForKind("manual"));
        Assert.Equal(ReminderStoreFileNameValue(), LocalDataCatalog.FileForKind("reminders"));
        Assert.Equal(ConsentStore.FileName, LocalDataCatalog.FileForKind("consents"));
        Assert.Equal(8, LocalDataCatalog.FilesForAllKinds.Count);
    }

    /// <summary><c>ManualEntryStore</c>/<c>ReminderStore</c> sit behind the MAUI-only JsonFileStore and
    /// are not compiled into this head, so their literals are read off DISK — pinned, not assumed.</summary>
    private static string LiteralFromDisk(string relativeFile, string constantName)
    {
        var parts = new List<string> { Lane01Repo.Root ?? "" };
        parts.AddRange(relativeFile.Split('/'));
        var path = Path.Combine(parts.ToArray());
        if (!File.Exists(path))
            throw new Xunit.Sdk.XunitException(relativeFile + " not found — the catalog's file mapping is unpinned");
        var line = File.ReadAllLines(path).FirstOrDefault(l => l.Contains(constantName + " ="));
        Assert.NotNull(line);
        return line!.Split('"')[1];
    }

    private static string ManualEntryStoreFileNameValue() =>
        LiteralFromDisk("Infrastructure/Persistence/ManualEntryStore.cs", "EntriesFileName");

    private static string ReminderStoreFileNameValue() =>
        LiteralFromDisk("Infrastructure/Notifications/ReminderStore.cs", "FileName");

    // ---- rejection: the garbage shapes ------------------------------------------

    [Theory]
    // 1 unknown field
    [InlineData("goals", "{\"Id\":\"g1\",\"Name\":\"x\",\"Evil\":1}", "Norm.Reject.UnknownField")]
    // 2 NaN literal
    [InlineData("goals", "{\"Id\":\"g1\",\"Name\":\"x\",\"TargetValue\":NaN}", "Norm.Reject.NotFinite")]
    // 3 Infinity literal
    [InlineData("goals", "{\"Id\":\"g1\",\"Name\":\"x\",\"TargetValue\":Infinity}", "Norm.Reject.NotFinite")]
    // 4 impossible range: a day has 1440 minutes
    [InlineData("manual", "{\"Date\":\"2026-04-30T00:00:00\",\"SleepMinutes\":90000,\"Origin\":\"Manual\"}", "Norm.Reject.Range")]
    // 5 ratio outside 0..1
    [InlineData("manual", "{\"Date\":\"2026-04-30T00:00:00\",\"Mood\":7.5,\"Origin\":\"Manual\"}", "Norm.Reject.Range")]
    // 6 inverted date pair (saved before the day it describes)
    [InlineData("manual", "{\"Date\":\"2026-04-30T00:00:00\",\"SavedAtUtc\":\"2020-01-01T00:00:00Z\",\"Origin\":\"Manual\"}", "Norm.Reject.DateInverted")]
    // 7 forged provenance on a manual entry
    [InlineData("manual", "{\"Date\":\"2026-04-30T00:00:00\",\"Steps\":1,\"Origin\":\"Garmin\"}", "Norm.Reject.ImpossibleProvenance")]
    // 8 enum value the app does not know
    [InlineData("goals", "{\"Id\":\"g1\",\"Name\":\"x\",\"Category\":\"Teleport\"}", "Norm.Reject.UnknownEnum")]
    // 9 wrong type (a name is not an array)
    [InlineData("goals", "{\"Id\":\"g1\",\"Name\":[\"a\",\"b\"]}", "Norm.Reject.Type")]
    // 10 missing id
    [InlineData("goals", "{\"Name\":\"x\"}", "Norm.Reject.MissingId")]
    // 11 duplicate ids inside one payload
    [InlineData("goals", "[{\"Id\":\"g1\",\"Name\":\"a\"},{\"Id\":\"g1\",\"Name\":\"b\"}]", "Norm.Reject.DuplicateId")]
    // 12 not JSON at all
    [InlineData("goals", "this is not json", "Norm.Reject.MalformedJson")]
    // 13 JSON scalar where an object is required
    [InlineData("goals", "42", "Norm.Reject.NotObject")]
    // 14 empty payload
    [InlineData("goals", "   ", "Norm.Reject.Empty")]
    // 15 completion logged in the future
    [InlineData("habits", "{\"Id\":\"h1\",\"Name\":\"x\",\"Completions\":[\"2030-01-01T00:00:00\"]}", "Norm.Reject.DateInverted")]
    // 16 date below the supported floor
    [InlineData("history", "{\"Date\":\"1999-01-01T00:00:00\",\"Origin\":\"Mock\"}", "Norm.Reject.DateInverted")]
    // 17 day list disagrees with the declared program length
    [InlineData("bootcamps", "{\"Id\":\"b1\",\"TitleKey\":\"k\",\"DurationDays\":3,\"CurrentDay\":1,\"Days\":[{\"DayNumber\":1,\"PlanTitleKey\":\"k\",\"PlanDescriptionKey\":\"k\",\"TargetMinutes\":10,\"IsAdapted\":false,\"IsCompleted\":false}]}", "Norm.Reject.Range")]
    // 18 current day past the program length
    [InlineData("bootcamps", "{\"Id\":\"b1\",\"TitleKey\":\"k\",\"DurationDays\":2,\"CurrentDay\":9,\"Days\":[]}", "Norm.Reject.Range")]
    // 19 a consent row cannot record "never asked" as a decision
    [InlineData("consents", "{\"Category\":\"HealthData\",\"Decision\":\"Untouched\",\"UpdatedAtUtc\":\"2026-05-01T09:00:00Z\"}", "Norm.Reject.UntouchableState")]
    // 20 impossible reminder time
    [InlineData("reminders", "{\"Id\":\"r1\",\"Kind\":\"habit\",\"TextKey\":\"k\",\"TimeOfDay\":\"99:00:00\",\"DaysMask\":127}", "Norm.Reject.Type")]
    // 21 weekday mask wider than 7 bits
    [InlineData("reminders", "{\"Id\":\"r1\",\"Kind\":\"habit\",\"TextKey\":\"k\",\"TimeOfDay\":\"08:00:00\",\"DaysMask\":9999}", "Norm.Reject.Range")]
    // 22 the settings bag holds a nested object
    [InlineData("settings", "{\"a\":{\"b\":1}}", "Norm.Reject.Type")]
    // 23 history day count is a string
    [InlineData("history", "{\"Date\":\"2026-04-30T00:00:00\",\"Steps\":\"many\"}", "Norm.Reject.Type")]
    public async Task ImportEntity_RejectsGarbage_WithTheRightKey_AndWritesNothing(string kind, string payload, string expectedKey)
    {
        SeedAll();
        var before = SnapshotBytes();
        var errors = await Catalog().ImportEntityAsync(kind, payload);
        Assert.Contains(errors, e => e.StartsWith(expectedKey, StringComparison.Ordinal));
        Assert.Equal(before, SnapshotBytes());   // a rejected payload must not touch a single byte
    }

    [Fact]
    public async Task ImportEntity_RejectsAnOversizedPayload()
    {
        SeedAll();
        var big = "{\"Id\":\"g1\",\"Name\":\"" + new string('x', 1_100_000) + "\",\"Description\":\"y\"}";
        var errors = await Catalog().ImportEntityAsync("goals", big);
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.TooLarge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportEntity_UnknownKindIsRejected()
    {
        var errors = await Catalog().ImportEntityAsync("bank-accounts", "{\"Id\":\"x\"}");
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.UnknownKind", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportEntity_RejectsACorruptExistingFile_InsteadOfOverwritingIt()
    {
        SeedAll();
        var store = Lane01Harness.RootStore(_dir);
        store.WriteRawAtomic(AppConstants.HabitsFile, "not json");
        var errors = await Catalog().ImportEntityAsync("habits", HabitJson("h9"));
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.StoreCorrupt", StringComparison.Ordinal));
        Assert.Equal("not json", File.ReadAllText(Path.Combine(_dir.Root, AppConstants.HabitsFile)));
    }

    // ---- accept paths for the app's own shapes ----------------------------------

    [Fact]
    public async Task ImportEntity_AcceptsDerivedReadOnlyFieldsBecauseTheModelRecomputesThem()
    {
        SeedAll();
        Assert.Empty(await Catalog().ImportEntityAsync("goals", GoalJson("g9")));
        var json = await Catalog().ExportEntityAsync("goals", "g9");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0.375, doc.RootElement.GetProperty("Fraction").GetDouble());
    }

    [Fact]
    public async Task ImportEntity_UpsertsByKindKey_WithoutDisturbingSiblings()
    {
        SeedAll();
        var edited = GoalJson("g2").Replace("Sleep more", "Sleep much more");
        Assert.Empty(await Catalog().ImportEntityAsync("goals", edited));
        var goals = File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile));
        Assert.Contains("Sleep much more", goals, StringComparison.Ordinal);
        Assert.Contains("\"g1\"", goals, StringComparison.Ordinal);        // untouched neighbour
        Assert.Contains("Sleep more\",", goals, StringComparison.Ordinal);  // g1 keeps its own name
        Assert.DoesNotContain("Shame", goals, StringComparison.Ordinal);    // nothing else was merged in
    }

    [Fact]
    public async Task ImportEntity_AcceptsAWellFormedReminderRow_AndKeepsTheLedgersSiblings()
    {
        SeedAll();
        Assert.Empty(await Catalog().ImportEntityAsync("reminders", ReminderJson("r2")));

        var raw = File.ReadAllText(Path.Combine(_dir.Root, LocalDataCatalog.ReminderStoreFileName));
        Assert.Contains("\"r2\"", raw, StringComparison.Ordinal);
        Assert.Contains("FiredDays", raw, StringComparison.Ordinal);        // lane 09's dedupe map survives
        Assert.Contains("PermissionAsked", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteEntity_RemovesTheRow_ReportsAMissingId_AndKeepsTheRest()
    {
        SeedAll();
        Assert.Empty(await Catalog().DeleteEntityAsync("goals", new[] { "g1" }));
        var errors = await Catalog().DeleteEntityAsync("goals", new[] { "g1" });
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.NotFound", StringComparison.Ordinal));
        Assert.Contains("g2", await Catalog().ExportEntityAsync("goals", "g2"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteEntity_ByKeyWorksForTheDateAndCategoryStores()
    {
        SeedAll();
        Assert.Empty(await Catalog().DeleteEntityAsync("manual", new[] { "2026-04-30" }));
        Assert.Empty(await Catalog().DeleteEntityAsync("consents", new[] { "AiProcessing" }));
        Assert.Empty(await Catalog().DeleteEntityAsync("history", new[] { "2026-04-30" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Catalog().ExportEntityAsync("manual", "2026-04-30"));
    }

    [Fact]
    public async Task DeleteSettingsBag_ThenExportReportsNotFound()
    {
        SeedAll();
        Assert.Empty(await Catalog().DeleteEntityAsync("settings", new[] { LocalDataCatalog.SettingsEntityId }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Catalog().ExportEntityAsync("settings", LocalDataCatalog.SettingsEntityId));
    }

    // ---- identity per kind --------------------------------------------------------

    [Theory]
    [InlineData("goals", "g1")]
    [InlineData("habits", "h1")]
    [InlineData("bootcamps", "b1")]
    [InlineData("history", "2026-04-30")]
    [InlineData("manual", "2026-04-30")]
    [InlineData("consents", "AiProcessing")]
    [InlineData("reminders", "r1")]
    [InlineData("settings", "settings")]
    public async Task ExportThenReimport_IsIdentityPerKind(string kind, string id)
    {
        SeedAll();
        var catalog = Catalog();
        var exported = await catalog.ExportEntityAsync(kind, id);
        Assert.Empty(await catalog.ImportEntityAsync(kind, exported));
        var again = await catalog.ExportEntityAsync(kind, id);
        Assert.Equal(Canonical(exported), Canonical(again));
        Assert.Equal(exported, again);   // byte-identical: the writer is stable, too
    }

    [Fact]
    public async Task ExportAll_ThenWipe_ThenImportAll_RestoresEveryEntity()
    {
        SeedAll();
        var catalog = Catalog();
        var document = await catalog.ExportAllAsync();

        var ids = new[]
        {
            ("goals", "g1"), ("goals", "g2"), ("habits", "h1"), ("bootcamps", "b1"),
            ("history", "2026-04-30"), ("manual", "2026-04-30"),
            ("consents", "AiProcessing"), ("reminders", "r1"), ("settings", "settings"),
        };
        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (kind, id) in ids)
            before[kind + "/" + id] = await catalog.ExportEntityAsync(kind, id);

        var store = Lane01Harness.RootStore(_dir);
        foreach (var file in LocalDataCatalog.FilesForAllKinds) store.Delete(file);
        foreach (var file in LocalDataCatalog.FilesForAllKinds)
            Assert.False(File.Exists(Path.Combine(_dir.Root, file)), file + " survived the delete");

        Assert.Empty(await catalog.ImportAllAsync(document, merge: false));
        foreach (var (kind, id) in ids)
            Assert.Equal(before[kind + "/" + id], await catalog.ExportEntityAsync(kind, id));
    }

    // ---- whole-store import semantics -------------------------------------------

    [Fact]
    public async Task ImportAll_WithOneBadKind_WritesNothing_AtAll()
    {
        SeedAll();
        var catalog = Catalog();
        var before = SnapshotBytes();

        var document = "{\"schemaVersion\":1,\"kinds\":{" +
            "\"goals\":[" + GoalJson("gnew") + "]," +
            "\"habits\":[" + HabitJson() + "]," +
            "\"manual\":[{\"Date\":\"2026-04-30T00:00:00\",\"SleepMinutes\":90000,\"Origin\":\"Manual\"}]}}";

        var errors = await catalog.ImportAllAsync(document, merge: false);
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.Range", StringComparison.Ordinal));
        Assert.Equal(before, SnapshotBytes());   // the GOOD kinds were not written either: all-or-nothing

        var goals = File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile));
        Assert.DoesNotContain("gnew", goals, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportAll_CleanDocument_WritesEveryKindItNames()
    {
        SeedAll();
        var document = "{\"schemaVersion\":1,\"kinds\":{\"goals\":[" + GoalJson("gz") + "]}}";
        Assert.Empty(await Catalog().ImportAllAsync(document, merge: false));
        Assert.Contains("gz", File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\": 99, \"kinds\": {\"goals\": []}}", "Norm.Reject.SchemaTooNew")]
    [InlineData("{\"schemaVersion\":1,\"kinds\":{\"bank-accounts\":[]}}", "Norm.Reject.UnknownKind")]
    [InlineData("{\"schemaVersion\":1,\"kinds\":{\"goals\":[]},\"totallyFine\":true}", "Norm.Reject.UnknownField")]
    [InlineData("{\"schemaVersion\":1}", "Norm.Reject.MissingField")]
    [InlineData("{\"kinds\":{}}", "Norm.Reject.MissingField")]
    [InlineData("{\"schemaVersion\":\"one\",\"kinds\":{}}", "Norm.Reject.Type")]
    [InlineData("[]", "Norm.Reject.NotObject")]
    public async Task ImportAll_RejectsMalformedDocuments(string document, string expectedKey)
    {
        SeedAll();
        var before = SnapshotBytes();
        var errors = await Catalog().ImportAllAsync(document, merge: false);
        Assert.Contains(errors, e => e.StartsWith(expectedKey, StringComparison.Ordinal));
        Assert.Equal(before, SnapshotBytes());
    }

    [Fact]
    public async Task ImportAll_OverSizeIsRejectedBeforeAnyWrite()
    {
        SeedAll();
        var before = SnapshotBytes();
        var big = "{\"schemaVersion\":1,\"kinds\":{\"goals\":[]},\"pad\":\"" + new string('p', 1_100_000) + "\"}";
        var errors = await Catalog().ImportAllAsync(big, merge: false);
        Assert.Contains(errors, e => e.StartsWith("Norm.Reject.TooLarge", StringComparison.Ordinal));
        Assert.Equal(before, SnapshotBytes());
    }

    [Fact]
    public async Task ImportAll_MergeUpserts_ReplaceDropsTheRest_AndNeitherTouchesUnmentionedKinds()
    {
        SeedAll();
        var catalog = Catalog();
        var document = "{\"schemaVersion\":1,\"kinds\":{\"goals\":[" + GoalJson("g3") + "]}}";

        Assert.Empty(await catalog.ImportAllAsync(document, merge: true));
        var goals = File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile));
        Assert.Contains("g1", goals, StringComparison.Ordinal);   // existing rows kept
        Assert.Contains("g3", goals, StringComparison.Ordinal);   // new row added

        Assert.Empty(await catalog.ImportAllAsync(document, merge: false));
        goals = File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile));
        Assert.DoesNotContain("g1", goals, StringComparison.Ordinal);  // replaced
        Assert.Contains("g3", goals, StringComparison.Ordinal);

        Assert.Contains("h1", File.ReadAllText(Path.Combine(_dir.Root, AppConstants.HabitsFile)), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRejectionKeyIsInBothLanguageManifests()
    {
        // A key the UI cannot localize renders as "[Norm.Reject.UnknownField]" — the same silent
        // failure the resx integrity suite hunts, so it is hunted here, from the manifest files.
        var en = Manifest(Path.Combine("wave3c-keys", "lane01.en.keys.xml"));
        var fa = Manifest(Path.Combine("wave3c-keys", "lane01.fa.keys.xml"));
        Assert.NotEmpty(en);
        Assert.Equal(en.Keys.ToList(), fa.Keys.ToList());   // identical key set, identical order
        foreach (var key in LocalDataCatalog.RejectionKeys)
            Assert.True(en.ContainsKey(key), "missing from the EN manifest: " + key);
        foreach (var key in new[] { "Auth.Cloud.NotAvailable", "Sync.Reason.NoBackend", "Passcode.Locked" })
        {
            Assert.True(en.ContainsKey(key), "missing from the EN manifest: " + key);
            Assert.True(fa.ContainsKey(key), "missing from the FA manifest: " + key);
            Assert.True(fa[key].Any(c => c >= '؀' && c <= 'ۿ'), key + " has no Persian in the FA manifest");
        }
    }

    [Fact]
    public async Task CatalogEmitsNoKeyMaterial_AndNoUserTextInsideErrorKeys()
    {
        // Error keys carry a field/kind NAME only — a payload is never echoed back into a key, so an
        // error list cannot smuggle private text into a log or a support export.
        SeedAll();
        var payload = "{\"Id\":\"g1\",\"Name\":\"my most private goal name\",\"Shame\":\"I logged this\"}";
        var errors = await Catalog().ImportEntityAsync("goals", payload);
        Assert.Contains(errors, e => e.Contains("Norm.Reject.UnknownField", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("private goal name", StringComparison.Ordinal));
        Assert.DoesNotContain(errors, e => e.Contains("I logged this", StringComparison.Ordinal));
    }

    // ---- helpers ------------------------------------------------------------------

    /// <summary>Every catalog-owned file as (name → bytes), for byte-level before/after equality.</summary>
    private SortedDictionary<string, byte[]> SnapshotBytes()
    {
        var map = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in LocalDataCatalog.FilesForAllKinds)
        {
            var path = Path.Combine(_dir.Root, file);
            map[file] = File.Exists(path) ? File.ReadAllBytes(path) : Array.Empty<byte>();
        }
        return map;
    }

    private static string Canonical(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Walk(doc.RootElement);
    }

    private static string Walk(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => "\"" + p.Name + "\":" + Walk(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Walk)) + "]",
        _ => element.GetRawText(),
    };

    private static Dictionary<string, string> Manifest(string relative)
    {
        var root = Lane01Repo.Root;
        var path = root is null ? null : Path.Combine(root, relative);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (path is null || !File.Exists(path)) return map;
        var xml = System.Xml.Linq.XDocument.Load(path!);
        foreach (var data in xml.Root?.Elements("data") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
        {
            var name = data.Attribute("name")?.Value;
            if (!string.IsNullOrEmpty(name)) map[name!] = data.Element("value")?.Value ?? string.Empty;
        }
        return map;
    }

    public void Dispose() => _dir.Dispose();
}
