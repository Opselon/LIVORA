using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
namespace LIVORA.Infrastructure.Updates;
public static class UpdateEvaluator
{
    public const string KeyErrorNoConnection = "Update.Error.NoConnection";
    public const string KeyErrorRateLimited = "Update.Error.RateLimited";
    public const string KeyErrorFeed = "Update.Error.Feed";
    public const string KeyErrorNoRelease = "Update.Error.NoRelease";
    public const string KeyErrorVersionUnreadable = "Update.Error.VersionUnreadable";
    public static UpdateInfo Project(
        FeedAnswer answer,
        string installedVersion,
        Func<string, string> localizer,
        bool fromCache)
    {
        var baseInfo = new UpdateInfo
        {
            CurrentVersion = installedVersion,
            CheckedAtUtc = answer.FetchedAtUtc,
            FromCache = fromCache,
        };
        if (!answer.FeedReached)
        {
            return answer.Failure switch
            {
                UpdateCheckFailure.NotConfigured => baseInfo with
                {
                    Status = UpdateCheckStatus.Disabled,
                    ErrorDetail = localizer(KeyErrorFeed),
                },
                UpdateCheckFailure.NoConnection => baseInfo with
                {
                    Status = UpdateCheckStatus.NoConnection,
                    ErrorDetail = localizer(KeyErrorNoConnection),
                },
                UpdateCheckFailure.RateLimited => baseInfo with
                {
                    Status = UpdateCheckStatus.RateLimited,
                    ErrorDetail = localizer(KeyErrorRateLimited),
                },
                _ => baseInfo with
                {
                    Status = UpdateCheckStatus.Error,
                    ErrorDetail = localizer(answer.ErrorKey ?? KeyErrorFeed),
                },
            };
        }
        var release = answer.Release;
        if (release is null)
        {
            return baseInfo with
            {
                Status = UpdateCheckStatus.Error,
                ErrorDetail = localizer(KeyErrorNoRelease),
            };
        }
        int? cmp = CompareWithinFeedPrecision(installedVersion, release.Version);
        if (cmp is null)
        {
            return baseInfo with
            {
                Status = UpdateCheckStatus.Error,
                LatestVersion = release.Version,
                LatestName = DisplayName(release),
                Notes = release.NoteLines,
                DownloadLinks = release.DownloadLinks,
                ReleaseUrl = release.HtmlUrl,
                IsPrerelease = release.IsPreRelease,
                ErrorDetail = localizer(KeyErrorVersionUnreadable),
            };
        }
        if (cmp < 0)
        {
            return baseInfo with
            {
                Status = UpdateCheckStatus.UpdateAvailable,
                LatestVersion = release.Version,
                LatestName = DisplayName(release),
                Notes = release.NoteLines,
                DownloadLinks = release.DownloadLinks,
                ReleaseUrl = release.HtmlUrl,
                IsPrerelease = release.IsPreRelease,
            };
        }
        if (cmp > 0)
        {
            return baseInfo with
            {
                Status = UpdateCheckStatus.NewerThanFeed,
                LatestVersion = release.Version,
                LatestName = DisplayName(release),
                Notes = release.NoteLines,
                DownloadLinks = release.DownloadLinks,
                ReleaseUrl = release.HtmlUrl,
                IsPrerelease = release.IsPreRelease,
                IsNewerThanFeed = true,
            };
        }
        return baseInfo with
        {
            Status = UpdateCheckStatus.UpToDate,
            LatestVersion = release.Version,
            LatestName = DisplayName(release),
            Notes = release.NoteLines,
            DownloadLinks = release.DownloadLinks,
            ReleaseUrl = release.HtmlUrl,
            IsPrerelease = release.IsPreRelease,
        };
    }
    public static int? CompareWithinFeedPrecision(string? installed, string? feedVersion)
    {
        if (!AppVersion.TryParse(installed, out var a) || !AppVersion.TryParse(feedVersion, out var b)) return null;
        int named = NamedComponents(feedVersion);
        if (named <= 0) return null;
        var installedParts = new[] { a.Major, a.Minor, a.Build, a.Revision };
        var feedParts = new[] { b.Major, b.Minor, b.Build, b.Revision };
        for (int i = 0; i < named; i++)
        {
            int x = Math.Max(installedParts[i], 0);
            int y = Math.Max(feedParts[i], 0);
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
    public static int NamedComponents(string? feedVersion)
    {
        if (string.IsNullOrWhiteSpace(feedVersion)) return 0;
        var core = feedVersion.Trim();
        int dash = core.IndexOf('-');
        if (dash >= 0) core = core[..dash];
        core = core.TrimStart('v', 'V');
        int count = 0;
        foreach (var part in core.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length == 0 || !char.IsDigit(part[0])) break;
            count++;
        }
        return count;
    }
    public static string? DisplayName(ReleaseEntry release)
        => string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name!.Trim();
}
