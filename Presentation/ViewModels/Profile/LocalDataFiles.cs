using LIVORA.Domain.Constants;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;

/// <summary>
/// Per-file truth for the privacy inventory: which files the app actually has on disk right now,
/// and the residual Wave 3 wipe that the frozen <c>IPrivacyService</c> cannot reach on its own.
///
/// WHY THIS LIVES IN THE PROFILE FEATURE, NOT Infrastructure: <c>DescribeStoredDataAsync()</c>
/// returns four coarse categories with no file names and no presence, and
/// <c>DeleteAllLocalDataAsync()</c> only ever deletes <c>PrivacyService.WipedFiles</c> — the six
/// Wave 2 files. Both files are read-only for this lane (docs/LANES.md §0.3/§0.2), so the report
/// asks for an <c>APPEND:</c> block that extends the wipe; this class is the standalone-safe half
/// of that design: it reads the real disk state for the inventory, and after the core wipe it
/// deletes whatever Wave 3 stores are still there. Once the APPEND lands, the core wipe removes
/// those files first and <see cref="WipeResidualStoresAsync"/> becomes a measured no-op — the
/// duplicated path is deliberate, because an inventory that promises "delete all" must never be
/// one merge order away from lying.
///
/// Honesty rules carried from PrivacyService: fixed file names only (no user value ever enters a
/// path), no network, no file contents read or shown — presence is <c>File.Exists</c>, and a count
/// is a top-level JSON array length or null (never a faked 0).
/// </summary>
public sealed class LocalDataFiles
{
    /// <summary>Why a piece of local data exists. Drives the origin chip (localized by key).</summary>
    public enum Provenance { UserEntered, Sample, AppGenerated }

    /// <summary>One file as the inventory sees it — presence is measured, never assumed.</summary>
    public sealed record Entry(string NameKey, string FileName, Provenance Source, bool Exists, int? Count);

    // Wave 3 file names exactly as their owning lanes specified them (§3): lane 02 manual
    // entries, lane 01 update cache, lane 09 reminders + snoozes.
    public const string ManualEntriesFile = "livora_manual_entries.json";
    public const string UpdateFeedFile = "update_feed.json";
    public const string RemindersFile = "livora_reminders.json";
    public const string SnoozedFile = "livora_snoozed.json";

    /// <summary>Wave 3 files the core wipe does not know about yet (see the class comment).</summary>
    internal static readonly string[] ResidualStoreFiles =
    {
        ManualEntriesFile, UpdateFeedFile, RemindersFile, SnoozedFile,
    };

    private readonly JsonFileStore _store;
    private readonly string _dir;

    /// <param name="store">The shared file store — the only deleter, so wipes stay idempotent.</param>
    /// <param name="directoryOverride">
    /// Must match the directory <paramref name="store"/> was built with; the default mirrors
    /// <see cref="JsonFileStore"/>'s own (<c>FileSystem.AppDataDirectory/LIVORA</c>). Tests pass
    /// the same temp dir to both so presence checks and the wipe stay in lockstep.
    /// </param>
    public LocalDataFiles(JsonFileStore store, string? directoryOverride = null)
    {
        _store = store;
        _dir = directoryOverride ?? Path.Combine(FileSystem.AppDataDirectory, "LIVORA");
    }

    /// <summary>
    /// The Wave 3 stores, each with its real on-disk state. Keys only — contents never leave here.
    /// </summary>
    public IReadOnlyList<Entry> DescribeStores() => new List<Entry>
    {
        new("Privacy.Item.ManualEntries", ManualEntriesFile, Provenance.UserEntered,
            Exists(ManualEntriesFile), CountJsonArray(ManualEntriesFile)),
        new("Privacy.Item.UpdateFeed", UpdateFeedFile, Provenance.AppGenerated,
            Exists(UpdateFeedFile), null),
        new("Privacy.Item.Reminders", RemindersFile, Provenance.UserEntered,
            Exists(RemindersFile), CountJsonArray(RemindersFile)),
        new("Privacy.Item.Snoozed", SnoozedFile, Provenance.UserEntered,
            Exists(SnoozedFile), CountJsonArray(SnoozedFile)),
    };

    /// <summary>
    /// Maps an <c>IPrivacyService</c> category key to the file(s) behind it, so the coarse
    /// categories get the same measured presence as the store rows. Null when a category has no
    /// known backing file — the UI then omits the presence chip instead of guessing.
    /// </summary>
    public bool? PresenceForCategory(string categoryKey)
    {
        var files = FilesForCategory(categoryKey);
        if (files.Count == 0) return null;
        return files.Any(Exists);
    }

    /// <summary>File-name hint for a category row ("" when unknown; never localized).</summary>
    public string CategoryFilesHint(string categoryKey) => string.Join(" · ", FilesForCategory(categoryKey));

    private static IReadOnlyList<string> FilesForCategory(string categoryKey) => categoryKey switch
    {
        "Privacy.Item.Profile" => new[] { AppConstants.ProfileFile },
        "Privacy.Item.GoalsHabits" => new[] { AppConstants.GoalsFile, AppConstants.HabitsFile },
        "Privacy.Item.HealthHistory" => new[] { AppConstants.HistoryFile },
        "Privacy.Item.Programs" => new[] { AppConstants.BootcampsFile },
        _ => Array.Empty<string>(),
    };

    /// <summary>The six files the core IPrivacyService wipe owns (presence is measured per file).</summary>
    public static IReadOnlyList<string> CoreFiles { get; } = new[]
    {
        AppConstants.ProfileFile, AppConstants.GoalsFile, AppConstants.HabitsFile,
        AppConstants.BootcampsFile, AppConstants.HistoryFile, AppConstants.SettingsFileName,
    };

    /// <summary>Every inventoried file: the six core files plus the four Wave 3 stores.</summary>
    public IReadOnlyList<string> AllFiles =>
        CoreFiles.Concat(ResidualStoreFiles).ToList();

    /// <summary>How many inventoried files actually hold data right now (count line, {0}).</summary>
    public int StoredFileCount => AllFiles.Count(Exists);

    /// <summary>How many files the inventory covers at all (count line, {1}).</summary>
    public int TotalFileCount => AllFiles.Count;

    /// <summary>
    /// Delete the Wave 3 stores that survived the core wipe. Missing files are skipped, so this
    /// is safe to call on every wipe and costs nothing once the core wipe covers them.
    /// Destructive: only ever called from behind the user's explicit confirmation.
    /// </summary>
    public async Task<int> WipeResidualStoresAsync(CancellationToken ct = default)
    {
        int removed = 0;
        foreach (var file in ResidualStoreFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!Exists(file)) continue;
            await _store.DeleteFileAsync(file);
            removed++;
        }
        return removed;
    }

    private bool Exists(string file)
    {
        try
        {
            return File.Exists(Path.Combine(_dir, file));
        }
        catch
        {
            return false; // an unreadable path reports "nothing stored", never crashes a view
        }
    }

    /// <summary>Top-level JSON array length, or null when absent / corrupt / not an array.</summary>
    private int? CountJsonArray(string file)
    {
        string json;
        try
        {
            var path = Path.Combine(_dir, file);
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : null;
        }
        catch
        {
            return null; // corrupt file: honest "no count", never a crash inside an inventory view
        }
    }
}
