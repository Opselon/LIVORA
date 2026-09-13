using System.Text.Json.Serialization;
namespace LIVORA.Infrastructure.Updates;
public sealed class ReleaseAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long SizeBytes { get; set; }
}
public sealed class ReleaseEntry
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("draft")] public bool IsDraft { get; set; }
    [JsonPropertyName("prerelease")] public bool IsPreRelease { get; set; }
    [JsonPropertyName("published_at")] public DateTime? PublishedAtUtc { get; set; }
    [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = new();
    [JsonIgnore] public string Version => TagName ?? string.Empty;
    [JsonIgnore] public IReadOnlyList<string> NoteLines => ReleaseFeedParser.CleanseBody(Body);
    [JsonIgnore] public IReadOnlyDictionary<string, string> DownloadLinks => ReleaseFeedParser.BuildLinks(this);
}
public sealed record FeedAnswer
{
    public required bool FeedReached { get; init; }
    public UpdateCheckFailure Failure { get; init; } = UpdateCheckFailure.UnderstoodNothing;
    public string? ErrorKey { get; init; }
    public ReleaseEntry? Release { get; init; }
    public int ReleaseCount { get; init; }
    public DateTime FetchedAtUtc { get; init; }
    public string SelectionRule { get; init; } = ReleaseFeedParser.RuleNone;
    public static FeedAnswer Failed(UpdateCheckFailure failure, string errorKey, DateTime fetchedAtUtc)
        => new()
        {
            FeedReached = false,
            Failure = failure,
            ErrorKey = errorKey,
            FetchedAtUtc = fetchedAtUtc,
            SelectionRule = ReleaseFeedParser.RuleNone,
        };
    public static FeedAnswer Reached(ReleaseEntry release, int releaseCount, string rule, DateTime fetchedAtUtc)
        => new()
        {
            FeedReached = true,
            Release = release,
            ReleaseCount = releaseCount,
            SelectionRule = rule,
            FetchedAtUtc = fetchedAtUtc,
        };
}
public enum UpdateCheckFailure
{
    NoConnection = 0,
    RateLimited = 1,
    UnderstoodNothing = 2,
    NotConfigured = 3,
}
