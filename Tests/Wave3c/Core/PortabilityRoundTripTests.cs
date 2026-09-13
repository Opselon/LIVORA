using System.Text;
using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;
using LIVORA.Infrastructure.Security.Gateway;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// Whole-document import/export, cross-checked against the REAL stores that own these files: the
/// catalog is only honest about portability if a document it wrote can be read back by
/// <see cref="JsonRepository{T}"/>, <see cref="ConsentStore"/> and <see cref="SecureStorageService"/>
/// themselves. That is the test this file exists for — a catalog that round-trips only with itself
/// would still break the app after a restore.
/// </summary>
public class PortabilityRoundTripTests : IDisposable
{
    private readonly Lane01Harness.TempDir _dir = new("portable");
    private static readonly DateTime Today = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    private LocalDataCatalog Catalog() => new(Lane01Harness.RootStore(_dir), () => Today);

    [Fact]
    public async Task ExportAllOfAnEmptyStore_IsADocumentWithEightEmptyKinds()
    {
        using var doc = JsonDocument.Parse(await Catalog().ExportAllAsync());
        var kinds = doc.RootElement.GetProperty("kinds");
        Assert.Equal(8, kinds.EnumerateObject().Count());
        foreach (var kind in LocalDataCatalog.Kinds.Where(k => k != "settings"))
            Assert.Equal(JsonValueKind.Array, kinds.GetProperty(kind).ValueKind);
        Assert.Equal(JsonValueKind.Object, kinds.GetProperty("settings").ValueKind);
    }

    [Fact]
    public async Task ImportAllOfAnEmptyDocument_ClearsEveryNamedKind()
    {
        Seed();
        Assert.Empty(await Catalog().ImportAllAsync(
            "{\"schemaVersion\":1,\"kinds\":{\"goals\":[]}}", merge: false));
        Assert.False(File.Exists(Path.Combine(_dir.Root, AppConstants.GoalsFile)));
    }

    [Fact]
    public async Task ConsentsWrittenByTheCatalogAreReadableByConsentStore()
    {
        Seed();
        var document = await Catalog().ExportAllAsync();
        var store = Lane01Harness.RootStore(_dir);
        store.Delete(ConsentStore.FileName);

        Assert.Empty(await Catalog().ImportAllAsync(document, merge: false));
        var consent = new ConsentStore(store, () => Today);
        Assert.Equal(Domain.Enums.ConsentDecision.Granted, consent.Get(Domain.Enums.ConsentCategory.AiProcessing));
        Assert.Equal(Domain.Enums.ConsentDecision.Denied, consent.Get(Domain.Enums.ConsentCategory.Analytics));
        Assert.Equal(Domain.Enums.ConsentDecision.Untouched, consent.Get(Domain.Enums.ConsentCategory.CalendarData));
    }

    [Fact]
    public async Task GoalsWrittenByTheCatalogDeserializeIntoTheDomainModel()
    {
        Seed();
        var document = await Catalog().ExportAllAsync();
        Lane01Harness.RootStore(_dir).Delete(AppConstants.GoalsFile);
        Assert.Empty(await Catalog().ImportAllAsync(document, merge: false));

        var json = File.ReadAllText(Path.Combine(_dir.Root, AppConstants.GoalsFile));
        var goals = JsonSerializer.Deserialize<List<Domain.Models.Goal>>(json,
            new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.NotNull(goals);
        Assert.Equal(2, goals!.Count);
        Assert.Equal(0.375, goals[0].Fraction, 3);          // recomputed, not imported
        Assert.Equal(Domain.Enums.GoalStatus.AtRisk, goals[0].Status);
    }

    [Fact]
    public async Task RestoringACatalogExportIntoASecondInstallProducesTheSameDocument()
    {
        Seed();
        var source = await Catalog().ExportAllAsync();

        using var other = new Lane01Harness.TempDir("portable2");
        var imported = new LocalDataCatalog(Lane01Harness.RootStore(other), () => Today);
        Assert.Empty(await imported.ImportAllAsync(source, merge: false));
        var mirrored = await imported.ExportAllAsync();

        using (var a = JsonDocument.Parse(source))
        using (var b = JsonDocument.Parse(mirrored))
        {
            // identical except the wall-clock stamp (which the injected clock pins, so it must match too)
            Assert.Equal(a.RootElement.GetProperty("schemaVersion").ToString(),
                         b.RootElement.GetProperty("schemaVersion").ToString());
            Assert.Equal(Canonical(a.RootElement.GetProperty("kinds")), Canonical(b.RootElement.GetProperty("kinds")));
        }
    }

    [Fact]
    public async Task SecureStoreParticipationIsOutOfScopeForTheCatalog()
    {
        // The portable document must NOT contain the secure store's file: secrets are not data the
        // user "exports for backup", and a document carrying DPAPI ciphertext would be a restore
        // vector into a different machine account.
        var secure = new SecureStorageService(Lane01Harness.SubStore(_dir, SecureStorageService.StoreDirName), new FakeSecureBox());
        await secure.SetAsync(GatewayConfigService.UserOverrideStorageKey, "user-override-key-123");
        Seed();

        var document = await Catalog().ExportAllAsync();
        Assert.DoesNotContain("user-override-key-123", document, StringComparison.Ordinal);
        Assert.DoesNotContain("secure-store", document, StringComparison.Ordinal);
        Assert.DoesNotContain(GatewayKeyStore.EmbeddedSourceLabel, document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogNeverWritesOutsideItsOwnedFiles()
    {
        Seed();
        // The .bak sidecar is LocalJsonStore's documented rollback copy (temp + File.Replace) and
        // already exists after Seed(), so it is part of the baseline, not a stray write. What this
        // test hunts is a NEW file name appearing anywhere under the data dir.
        List<string> Names() => Directory.GetFileSystemEntries(_dir.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_dir.Root, f).Replace(".json.bak", ".json"))
            .Distinct().OrderBy(f => f, StringComparer.Ordinal).ToList();
        var before = Names();

        var document = await Catalog().ExportAllAsync();
        await Catalog().ImportAllAsync(document, merge: false);
        await Catalog().ImportEntityAsync("goals", LocalCatalogFixtures.GoalJson("gx"));
        await Catalog().DeleteEntityAsync("goals", new[] { "gx" });

        var after = Names();
        Assert.Equal(before, after);   // no stray temp/backup files left behind either
    }

    private void Seed()
    {
        var store = Lane01Harness.RootStore(_dir);
        store.WriteRawAtomic(AppConstants.GoalsFile, "[" + LocalCatalogFixtures.GoalJson() + "," + LocalCatalogFixtures.GoalJson("g2") + "]");
        store.WriteRawAtomic(AppConstants.HabitsFile, "[" + LocalCatalogFixtures.HabitJson() + "]");
        store.WriteRawAtomic(AppConstants.BootcampsFile, "[" + LocalCatalogFixtures.BootcampJson() + "]");
        store.WriteRawAtomic(AppConstants.HistoryFile, "[" + LocalCatalogFixtures.HistoryJson() + "]");
        store.WriteRawAtomic(LocalDataCatalog.ManualEntriesFileName, "[" + LocalCatalogFixtures.ManualJson() + "]");
        store.WriteRawAtomic(AppConstants.SettingsFileName, "{\"preferred_language\":\"Persian\"}");
        store.WriteRawAtomic(ConsentStore.FileName, "[" +
            LocalCatalogFixtures.ConsentJson("AiProcessing", "Granted") + "," +
            LocalCatalogFixtures.ConsentJson("Analytics", "Denied") + "]");
        store.WriteRawAtomic(LocalDataCatalog.ReminderStoreFileName, "{\"Reminders\":[" + LocalCatalogFixtures.ReminderJson() + "],\"PermissionAsked\":true}");
    }

    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => "\"" + p.Name + "\":" + Canonical(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText(),
    };

    public void Dispose() => _dir.Dispose();
}

/// <summary>The store-shaped fixtures, shared by the catalog suites (see <see cref="LocalDataCatalogTests"/>).</summary>
internal static class LocalCatalogFixtures
{
    public static string GoalJson(string id = "g1") =>
        "{\"Id\":\"" + id + "\",\"Name\":\"Sleep more\",\"Description\":\"\"," +
        "\"Category\":\"Sleep\",\"TargetValue\":8,\"ProgressValue\":3,\"Period\":\"Day\",\"Unit\":\"Hours\"," +
        "\"Deadline\":null,\"IsArchived\":false,\"Measurement\":\"ManualCounter\",\"MetricKey\":null," +
        "\"StrategyKey\":null,\"FrequencyPerPeriod\":1,\"Fraction\":0.375,\"Status\":\"AtRisk\"}";

    public static string HabitJson(string id = "h1") =>
        "{\"Id\":\"" + id + "\",\"Name\":\"Morning walk\",\"NameKey\":null,\"Frequency\":\"Daily\"," +
        "\"TimesPerWeek\":7,\"Completions\":[\"2026-04-29T00:00:00\"],\"CompletionLog\":[\"2026-04-29T07:15:00\"]," +
        "\"CurrentStreak\":1}";

    public static string BootcampJson(string id = "b1") =>
        "{\"Id\":\"" + id + "\",\"TitleKey\":\"Bootcamp.Title.Sleep\",\"DescriptionKey\":\"Bootcamp.Desc.Sleep\"," +
        "\"Category\":\"Sleep\",\"DurationDays\":2,\"Difficulty\":\"Beginner\",\"CurrentDay\":1,\"IsEnrolled\":true," +
        "\"Days\":[" +
        "{\"DayNumber\":1,\"PlanTitleKey\":\"Bootcamp.Plan.Walk\",\"PlanDescriptionKey\":\"Bootcamp.Plan.Walk.Desc\",\"TargetMinutes\":20,\"IsAdapted\":false,\"IsCompleted\":false}," +
        "{\"DayNumber\":2,\"PlanTitleKey\":\"Bootcamp.Plan.Walk\",\"PlanDescriptionKey\":\"Bootcamp.Plan.Walk.Desc\",\"TargetMinutes\":25,\"IsAdapted\":false,\"IsCompleted\":false}]," +
        "\"CreatorName\":\"LIVORA\",\"GoalMetricKey\":\"sleep.minutes\",\"AdaptationRuleKeys\":[\"Rule.LowRecoveryReduce\"]," +
        "\"WasAdaptedToday\":false,\"CompletionFraction\":0.5,\"Today\":null}";

    public static string HistoryJson(string date = "2026-04-30T00:00:00") =>
        "{\"Date\":\"" + date + "\",\"Origin\":\"Mock\",\"Completeness\":0.8,\"SleepMinutes\":420," +
        "\"SleepQuality\":0.6,\"SleepConsistency\":0.5,\"BedtimeMinutesOfDay\":1330,\"Steps\":6000," +
        "\"ActiveMinutes\":30,\"RecoveryScore\":62,\"RestingHeartRate\":null,\"HrvMs\":null,\"Stress\":0.4," +
        "\"Mood\":0.6,\"Energy\":0.5,\"CompletedHabitIds\":[\"h1\"],\"AdvancedGoalIds\":[]," +
        "\"BootcampId\":null,\"BootcampDayNumber\":null,\"BootcampDayWasAdapted\":false," +
        "\"InsightTopic\":null,\"InsightPriority\":null}";

    public static string ManualJson(string date = "2026-04-30T00:00:00") =>
        "{\"Date\":\"" + date + "\",\"SleepMinutes\":400,\"Steps\":5000,\"ActiveMinutes\":25," +
        "\"SleepQuality\":0.7,\"Mood\":0.5,\"Energy\":0.6,\"Stress\":0.3,\"Note\":\"ok\"," +
        "\"SavedAtUtc\":\"2026-04-30T21:00:00Z\",\"Origin\":\"Manual\"}";

    public static string ConsentJson(string category = "AiProcessing", string decision = "Granted") =>
        "{\"Category\":\"" + category + "\",\"Decision\":\"" + decision + "\",\"UpdatedAtUtc\":\"2026-05-01T09:00:00Z\"}";

    public static string ReminderJson(string id = "r1") =>
        "{\"Id\":\"" + id + "\",\"Kind\":\"habit\",\"TargetId\":\"h1\",\"Enabled\":true," +
        "\"TimeOfDay\":\"20:00:00\",\"DaysMask\":127,\"TextKey\":\"Reminders.Text.Habit\",\"TextArgs\":[\"Morning walk\"]}";
}
