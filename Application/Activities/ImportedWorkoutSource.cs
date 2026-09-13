using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Normalization;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Activities;

// =============================================================================
// Wave 3c (lane 03) — the REAL imported-workout path.
//
// This reads the user's own exported workout files (JSON arrays of session
// records) from an inbox directory on disk. It never simulates, never
// fabricates, never back-fills: no files → an honest empty list; a corrupt
// file → skipped whole with a reject count (never half-parsed — a truncated
// array must not ship as "the sessions that happened to parse"); a record the
// normalizer rejects → counted and dropped. What this class returns, the user
// actually imported.
// =============================================================================

/// <summary>
/// Pull-based <see cref="IWorkoutSource"/> over a directory of exported JSON files.
/// Files are read fresh on every <see cref="GetSessionsAsync"/> call (pull-only — there is no
/// watcher, no timer, no polling loop; the state engine asks when it asks).
///
/// Directory resolution: the ctor takes an explicit override (tests, custom folders). With null,
/// <see cref="InboxDirectory"/> stays null and the source reports itself honestly Disconnected
/// with an empty answer — the production path is composed by the integrator in MauiProgram from
/// the platform app-data folder via <see cref="ComposeInboxPath"/>.
/// </summary>
public sealed class ImportedWorkoutSource : IWorkoutSource
{
    /// <summary>Folder name under the app data root the integrator points this source at.</summary>
    public const string InboxFolderName = "workouts_inbox";

    /// <summary>Only these files are read (the user drops exports in; anything else is not ours).</summary>
    public const string FilePattern = "*.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string? _inboxDir;
    private readonly IUnitConverter _converter;
    private readonly double? _weightKg;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Rejects counted in the most recent scan: corrupt files + records that failed
    /// normalization. 0 before the first scan — never claimed without reading.</summary>
    public int LastScanRejectCount { get; private set; }

    /// <summary>Files successfully parsed in the most recent scan (diagnostics/UI honesty).</summary>
    public int LastScanFileCount { get; private set; }

    /// <summary>The inbox this instance reads, or null when the integrator hasn't composed a path.</summary>
    public string? InboxDirectory => _inboxDir;

    public ImportedWorkoutSource(
        string? inboxDirectory = null,
        IUnitConverter? converter = null,
        double? weightKg = null,
        Func<DateTime>? utcNow = null)
    {
        _inboxDir = string.IsNullOrWhiteSpace(inboxDirectory) ? null : inboxDirectory;
        _converter = converter ?? new UnitConverter();
        _weightKg = weightKg is { } w && w > 0 ? w : null;
        _utcNow = utcNow ?? StaticUtcNow;
    }

    private static DateTime StaticUtcNow() => DateTime.UtcNow;

    /// <summary>Production path the integrator composes: appDataRoot/livora/workouts_inbox.</summary>
    public static string ComposeInboxPath(string appDataRoot) =>
        Path.Combine(appDataRoot, "livora", InboxFolderName);

    public DataSourceCapabilities Capabilities =>
        DataSourceCapabilities.Workout | DataSourceCapabilities.Calories;

    /// <summary>Honest state: the folder exists (even if empty) = Connected — there is nowhere
    /// to read from without it, and we never claim a source we cannot see.</summary>
    public ConnectionState State =>
        _inboxDir is { } dir && Directory.Exists(dir) ? ConnectionState.Connected : ConnectionState.Disconnected;

    /// <summary>
    /// Read + normalize every workout file in the inbox, keep sessions whose local start falls
    /// in [from, to), dedupe. Files process in name order (ordinal) so the result is identical
    /// scan to scan. A parse failure on one file never truncates the others.
    /// </summary>
    public Task<IReadOnlyList<WorkoutSession>> GetSessionsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var sessions = new List<WorkoutSession>();
        LastScanRejectCount = 0;
        LastScanFileCount = 0;

        if (_inboxDir is null || !Directory.Exists(_inboxDir))
            return Task.FromResult<IReadOnlyList<WorkoutSession>>(sessions); // honest empty

        var files = Directory.GetFiles(_inboxDir, FilePattern)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            List<ImportedWorkoutRecord> records;
            try
            {
                var json = File.ReadAllText(file);
                records = JsonSerializer.Deserialize<List<ImportedWorkoutRecord>>(json, JsonOptions)
                          ?? new List<ImportedWorkoutRecord>();
            }
            catch (JsonException) { LastScanRejectCount++; continue; }   // corrupt JSON: skip whole file
            catch (IOException) { LastScanRejectCount++; continue; }     // unreadable (lock/race): same
            catch (UnauthorizedAccessException) { LastScanRejectCount++; continue; }
            LastScanFileCount++;

            foreach (var rec in records)
            {
                var raw = rec.ToRaw(_weightKg);
                var result = WorkoutNormalizer.Normalize(raw, _converter, _utcNow(), _weightKg);
                if (result.RejectKeys.Count > 0 && result.Session is null)
                {
                    LastScanRejectCount++;
                    continue;
                }
                if (result.Session is { } s) sessions.Add(s);
            }
        }

        var windowed = sessions
            .Where(s => s.StartLocal >= from && s.StartLocal < to)
            .ToList();
        var deduped = WorkoutNormalizer.Dedupe(windowed);
        LastScanRejectCount += deduped.DuplicatesRemoved;
        IReadOnlyList<WorkoutSession> final = deduped.Sessions
            .OrderBy(s => s.StartLocal.Ticks)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(final);
    }
}

/// <summary>
/// The wire shape of one record in the user's export (property names are matched
/// case-insensitively; dates are ISO-8601 local wall time, which is what every export writes).
/// Unknown extra fields the export carries (heart-rate arrays, GPS points) are ignored — the
/// user's file stays valid without us modeling all of it.
/// </summary>
public sealed class ImportedWorkoutRecord
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("typeAlias")] public string? TypeAlias { get; set; }
    [JsonPropertyName("start")] public DateTime? Start { get; set; }
    [JsonPropertyName("startLocal")] public DateTime? StartLocal { get; set; }
    [JsonPropertyName("end")] public DateTime? End { get; set; }
    [JsonPropertyName("endLocal")] public DateTime? EndLocal { get; set; }
    [JsonPropertyName("distance")] public double? Distance { get; set; }
    [JsonPropertyName("distanceUnit")] public string? DistanceUnit { get; set; }
    [JsonPropertyName("energy")] public double? Energy { get; set; }
    [JsonPropertyName("energyUnit")] public string? EnergyUnit { get; set; }
    [JsonPropertyName("calories")] public double? Calories { get; set; }
    [JsonPropertyName("intensity")] public double? Intensity { get; set; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("weightKg")] public double? WeightKg { get; set; }

    /// <summary>Missing start/end makes the record unusable — surfaced as a duration reject.</summary>
    public RawWorkoutSession ToRaw(double? defaultWeightKg) => new()
    {
        TypeAlias = TypeAlias ?? Type ?? string.Empty,
        StartLocal = StartLocal ?? Start ?? DateTime.MinValue,
        EndLocal = EndLocal ?? End ?? DateTime.MinValue,
        Distance = Distance,
        DistanceUnit = DistanceUnit ?? string.Empty,
        Energy = Energy ?? Calories,
        EnergyUnit = (Energy is not null ? EnergyUnit : string.Empty) ?? string.Empty,
        Intensity = Intensity,
        SourceId = SourceId ?? Id ?? string.Empty,
        Origin = DataOrigin.Imported,
        WeightKg = WeightKg ?? defaultWeightKg,
    };
}
