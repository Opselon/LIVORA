using System.Text.Json;
using System.Text.Json.Serialization;
using LIVORA.Application.Abstractions;
namespace LIVORA.Infrastructure.Updates;
public sealed class UpdateFeedRecord
{
    public const int CurrentSchema = 1;
    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("installed_version_at_check")] public string? InstalledVersionAtCheck { get; set; }
    [JsonPropertyName("last_successful_check_utc")] public DateTime? LastSuccessfulCheckUtc { get; set; }
    [JsonPropertyName("last_attempt_utc")] public DateTime? LastAttemptUtc { get; set; }
    [JsonPropertyName("selection_rule")] public string? SelectionRule { get; set; }
    [JsonPropertyName("release_count")] public int ReleaseCount { get; set; }
    [JsonPropertyName("release")] public CachedRelease? Release { get; set; }
    [JsonPropertyName("error")] public CachedError? Error { get; set; }
    public static UpdateFeedRecord FromAnswer(
        FeedAnswer answer,
        string installedVersion,
        DateTime attemptUtc,
        UpdateFeedRecord? previous)
    {
        if (answer.FeedReached && answer.Release is { } release)
        {
            return new UpdateFeedRecord
            {
                InstalledVersionAtCheck = installedVersion,
                LastSuccessfulCheckUtc = answer.FetchedAtUtc,
                LastAttemptUtc = attemptUtc,
                SelectionRule = answer.SelectionRule,
                ReleaseCount = answer.ReleaseCount,
                Release = CachedRelease.From(release),
                Error = null,
            };
        }
        return new UpdateFeedRecord
        {
            InstalledVersionAtCheck = installedVersion,
            LastSuccessfulCheckUtc = previous?.LastSuccessfulCheckUtc,
            LastAttemptUtc = attemptUtc,
            SelectionRule = previous?.SelectionRule,
            ReleaseCount = previous?.ReleaseCount ?? 0,
            Release = previous?.Release,
            Error = new CachedError
            {
                Kind = answer.Failure.ToString(),
                MessageKey = answer.ErrorKey ?? UpdateEvaluator.KeyErrorFeed,
                AtUtc = answer.FetchedAtUtc,
            },
        };
    }
}
public sealed class CachedRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAtUtc { get; set; }
    [JsonPropertyName("assets")] public List<CachedAsset> Assets { get; set; } = new();
    public static CachedRelease From(ReleaseEntry entry) => new()
    {
        TagName = entry.TagName,
        Name = entry.Name,
        Body = entry.Body,
        HtmlUrl = entry.HtmlUrl,
        Prerelease = entry.IsPreRelease,
        PublishedAtUtc = entry.PublishedAtUtc,
        Assets = entry.Assets
            .Where(a => !string.IsNullOrWhiteSpace(a.BrowserDownloadUrl))
            .Select(a => new CachedAsset
            {
                Name = a.Name,
                BrowserDownloadUrl = a.BrowserDownloadUrl,
                SizeBytes = a.SizeBytes,
            })
            .ToList(),
    };
    public ReleaseEntry ToEntry() => new()
    {
        TagName = TagName,
        Name = Name,
        Body = Body,
        HtmlUrl = HtmlUrl,
        IsPreRelease = Prerelease,
        PublishedAtUtc = PublishedAtUtc,
        IsDraft = false,
        Assets = Assets.Select(a => new ReleaseAsset
        {
            Name = a.Name,
            BrowserDownloadUrl = a.BrowserDownloadUrl,
            SizeBytes = a.SizeBytes,
        }).ToList(),
    };
}
public sealed class CachedAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long SizeBytes { get; set; }
}
public sealed class CachedError
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("message_key")] public string? MessageKey { get; set; }
    [JsonPropertyName("at_utc")] public DateTime AtUtc { get; set; }
    public FeedAnswer ToFailedAnswer()
    {
        Enum.TryParse<UpdateCheckFailure>(Kind, out var kind);
        return FeedAnswer.Failed(kind, string.IsNullOrWhiteSpace(MessageKey) ? UpdateEvaluator.KeyErrorFeed : MessageKey!, AtUtc);
    }
}
public sealed class UpdateFeedCache
{
    public const string FileName = "update_feed.json";
    internal const string PrivacyKey = "Privacy.Item.UpdateFeed";
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly string _dir;
    private readonly object _gate = new();
    public UpdateFeedCache() : this(null)
    {
    }
    internal UpdateFeedCache(string? directoryOverride)
    {
        _dir = string.IsNullOrEmpty(directoryOverride)
            ? Path.Combine(FileSystem.AppDataDirectory, "LIVORA")
            : directoryOverride!;
    }
    internal string FilePath => Path.Combine(_dir, FileName);
    public UpdateFeedRecord? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                var json = File.ReadAllText(FilePath);
                if (string.IsNullOrWhiteSpace(json)) return null;
                var record = JsonSerializer.Deserialize<UpdateFeedRecord>(json, Options);
                if (record is null || record.Schema > UpdateFeedRecord.CurrentSchema) return null;
                return record;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
    public Task SaveAsync(UpdateFeedRecord record)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception)
            {
            }
        }
        return Task.CompletedTask;
    }
    public Task ClearAsync()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                var temp = FilePath + ".tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception) { /* nothing user-visible depends on this succeeding */ }
        }
        return Task.CompletedTask;
    }
    public static FeedAnswer? ToAnswer(UpdateFeedRecord? record)
    {
        if (record is null) return null;
        if (record.Release is { } release && AppVersion.TryParse(release.TagName, out _))
        {
            return new FeedAnswer
            {
                FeedReached = true,
                Release = release.ToEntry(),
                ReleaseCount = record.ReleaseCount,
                SelectionRule = string.IsNullOrWhiteSpace(record.SelectionRule)
                    ? (release.Prerelease ? ReleaseFeedParser.RulePrerelease : ReleaseFeedParser.RuleStable)
                    : record.SelectionRule!,
                FetchedAtUtc = record.LastSuccessfulCheckUtc ?? release.PublishedAtUtc ?? record.Error?.AtUtc ?? default,
            };
        }
        return record.Error is { } error ? error.ToFailedAnswer() : null;
    }
}
