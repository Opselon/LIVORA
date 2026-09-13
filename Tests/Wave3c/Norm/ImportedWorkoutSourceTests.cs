using System.Text.Json;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Activities;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;

namespace LIVORA.Tests.Wave3c.Norm;

/// <summary>
/// Wave 3c (lane 03): the REAL user-data path. Reads exported JSON out of an inbox directory —
/// nothing simulated, nothing back-filled. This suite owns its own temp dir and deletes it.
/// </summary>
public class ImportedWorkoutSourceTests : IDisposable
{
    private readonly string _inbox;
    private static readonly DateTime WindowFrom = new(2026, 9, 1);
    private static readonly DateTime WindowTo = new(2026, 9, 30);

    public ImportedWorkoutSourceTests()
    {
        _inbox = Path.Combine(Path.GetTempPath(), "livora-w3c-inbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_inbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_inbox)) Directory.Delete(_inbox, recursive: true); }
        catch (IOException) { /* temp sweep will get it; never throw from Dispose */ }
    }

    private ImportedWorkoutSource Source(double? weight = 70) =>
        new(_inbox, new UnitConverter(), weight);

    private void Write(string name, string json) =>
        File.WriteAllText(Path.Combine(_inbox, name), json);

    // ---- round trip -------------------------------------------------------------

    [Fact]
    public async Task WrittenJsonFile_RoundTripsThroughTheSource()
    {
        Write("export1.json", """
        [
          { "type": "walk", "startLocal": "2026-09-10T08:00:00", "endLocal": "2026-09-10T09:00:00",
            "distance": 5.0, "distanceUnit": "km", "sourceId": "u-1" },
          { "type": "HKWorkoutActivityTypeRunning", "startLocal": "2026-09-11T07:30:00",
            "endLocal": "2026-09-11T08:15:00", "energy": 420, "energyUnit": "kcal", "sourceId": "u-2" }
        ]
        """);

        var sessions = await Source().GetSessionsAsync(WindowFrom, WindowTo);

        Assert.Equal(2, sessions.Count);
        var walk = sessions.Single(s => s.Type == WorkoutType.Walk);
        var run = sessions.Single(s => s.Type == WorkoutType.Run);

        Assert.Equal(5000, walk.DistanceMeters!.Value, 6);   // km → meters through the table
        Assert.Equal(245, walk.EnergyKcal!.Value, 1);        // no energy given → MET estimate (3.5×1h×70kg)
        Assert.Equal(WorkoutQuality.Estimated, walk.Quality);
        Assert.Equal(420, run.EnergyKcal!.Value, 6);         // reported energy survives verbatim
        Assert.Equal(DataOrigin.Imported, run.Provenance.Origin);
        Assert.Equal("import", run.Provenance.Source);
        Assert.Equal("u-2", run.Provenance.SourceRecordId);
    }

    [Fact]
    public async Task DatesOutsideWindow_AreNotReturned_PullIsScoped()
    {
        Write("old.json", """
        [ { "type": "walk", "start": "2025-01-05T08:00:00", "end": "2025-01-05T09:00:00", "sourceId": "ancient" } ]
        """);
        var sessions = await Source().GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Empty(sessions);
    }

    // ---- honesty: empty and corrupt ---------------------------------------------------

    [Fact]
    public async Task EmptyDirectory_HonestEmptyList_NotSimulatedData()
    {
        var sessions = await Source().GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task MissingDirectory_HonestEmptyAndDisconnected()
    {
        var ghost = new ImportedWorkoutSource(
            Path.Combine(_inbox, "never-created"), new UnitConverter());
        Assert.Equal(ConnectionState.Disconnected, ghost.State);
        var sessions = await ghost.GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task CorruptFile_IsSkippedWhole_WithRejectCount_GoodFileStillReads()
    {
        Write("broken.json", "[ { \"type\": \"walk\", \"startLocal\": \"2026-09-05T08:00:00\", "); // truncated
        Write("good.json", """
        [ { "type": "run", "startLocal": "2026-09-06T08:00:00", "endLocal": "2026-09-06T08:30:00", "sourceId": "g-1" } ]
        """);

        var source = Source();
        var sessions = await source.GetSessionsAsync(WindowFrom, WindowTo);

        Assert.Single(sessions);                                   // the corrupt file shipped NOTHING
        Assert.Equal(1, source.LastScanRejectCount);               // and that is counted, not silent
        Assert.Equal(1, source.LastScanFileCount);                 // only one file parsed
    }

    [Fact]
    public async Task RejectedRecordsInsideAGoodFile_AreCounted()
    {
        Write("mixed.json", """
        [
          { "type": "walk", "startLocal": "2026-09-10T08:00:00", "endLocal": "2026-09-10T09:00:00", "sourceId": "ok-1" },
          { "type": "run",  "startLocal": "2026-09-11T08:00:00", "endLocal": "2026-09-11T08:00:00", "sourceId": "zero-duration" }
        ]
        """);
        var source = Source();
        var sessions = await source.GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Single(sessions);
        Assert.Equal(1, source.LastScanRejectCount);
    }

    [Fact]
    public async Task DuplicatesAcrossFiles_FoldedBySourceId()
    {
        var rec = """
        { "type": "walk", "startLocal": "2026-09-10T08:00:00", "endLocal": "2026-09-10T09:00:00",
          "energy": 250, "energyUnit": "kcal", "sourceId": "same-id" }
        """;
        Write("a.json", "[" + rec + "]");
        Write("b.json", "[" + rec + "]"); // the user imported the same file twice

        var source = Source();
        var sessions = await source.GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Single(sessions);
        Assert.Equal(1, source.LastScanRejectCount);
    }

    [Fact]
    public async Task NonJsonFiles_AreIgnoredEntirely()
    {
        File.WriteAllText(Path.Combine(_inbox, "notes.txt"), "not a workout export");
        var sessions = await Source().GetSessionsAsync(WindowFrom, WindowTo);
        Assert.Empty(sessions);
    }

    // ---- contract surface ---------------------------------------------------------------

    [Fact]
    public void ImplementsTheFrozenSourceContract()
    {
        var source = Source();
        Assert.IsAssignableFrom<IWorkoutSource>(source);
        Assert.True(source.Capabilities.HasFlag(DataSourceCapabilities.Workout));
    }

    [Fact]
    public void ComposeInboxPath_EndsInTheInboxFolder()
    {
        var path = ImportedWorkoutSource.ComposeInboxPath(
            Path.Combine("C:", "Users", "someone", "AppData", "Local", "LIVORA"));
        Assert.EndsWith(ImportedWorkoutSource.InboxFolderName, path);
        Assert.Contains("LIVORA", path);
    }

    [Fact]
    public void RecordWireShape_MatchesItsOwnContract()
    {
        // sanity: the record round-trips through System.Text.Json with the documented names
        var json = """
            { "type":"cycle","startLocal":"2026-09-12T07:00:00","endLocal":"2026-09-12T07:45:00",
              "distance":12.5,"distanceUnit":"km","sourceId":"z" }
            """;
        var rec = JsonSerializer.Deserialize<ImportedWorkoutRecord>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("cycle", rec.Type);
        Assert.Equal(12.5, rec.Distance!.Value, 6);
        Assert.Equal(new DateTime(2026, 9, 12, 7, 0, 0), rec.StartLocal!.Value);
    }
}
