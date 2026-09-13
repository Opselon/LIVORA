using System.Net;
using LIVORA.Application.Abstractions;
using Microsoft.Extensions.Logging;
namespace LIVORA.Infrastructure.Updates;
public interface IReleaseFeed
{
    bool IsConfigured { get; }
    Task<FeedAnswer> FetchAsync(CancellationToken ct = default);
}
public sealed class GitHubReleaseFeed : IReleaseFeed
{
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };
    private readonly Uri? _feedUri;
    private readonly ILogger<GitHubReleaseFeed> _logger;
    public GitHubReleaseFeed(ILogger<GitHubReleaseFeed> logger)
        : this(ReleaseFeedParser.DefaultFeedUrl, logger)
    {
    }
    internal GitHubReleaseFeed(string? feedUrl, ILogger<GitHubReleaseFeed> logger)
    {
        _logger = logger;
        _feedUri = Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri)
                 && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;
    }
    public bool IsConfigured => _feedUri is not null;
    public async Task<FeedAnswer> FetchAsync(CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        if (_feedUri is null)
            return FeedAnswer.Failed(UpdateCheckFailure.NotConfigured, UpdateEvaluator.KeyErrorFeed, nowUtc);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(RequestTimeout);
        string? body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _feedUri);
            request.Headers.UserAgent.ParseAdd("LIVORA-app");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return FeedAnswer.Failed(UpdateCheckFailure.RateLimited, UpdateEvaluator.KeyErrorRateLimited, nowUtc);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogInformation("Update feed returned 404 for {Feed}", _feedUri);
                return FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorNoRelease, nowUtc);
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Update feed returned HTTP {Status}", (int)response.StatusCode);
                return FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorFeed, nowUtc);
            }
            body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogInformation("Update feed timed out after {Seconds}s", RequestTimeout.TotalSeconds);
            return FeedAnswer.Failed(UpdateCheckFailure.NoConnection, UpdateEvaluator.KeyErrorNoConnection, nowUtc);
        }
        catch (OperationCanceledException)
        {
            return FeedAnswer.Failed(UpdateCheckFailure.NoConnection, UpdateEvaluator.KeyErrorNoConnection, DateTime.UtcNow);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogInformation("Update feed unreachable: {Message}", ex.Message);
            return FeedAnswer.Failed(UpdateCheckFailure.NoConnection, UpdateEvaluator.KeyErrorNoConnection, nowUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected failure reading the update feed");
            return FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorFeed, nowUtc);
        }
        var releases = ReleaseFeedParser.ParseReleases(body);
        if (releases is null)
            return FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorFeed, nowUtc);
        var best = ReleaseFeedParser.SelectBest(releases, out var rule);
        if (best is null)
            return FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorNoRelease, nowUtc);
        _logger.LogDebug("Update feed picked {Tag} by rule {Rule}", best.TagName, rule);
        return FeedAnswer.Reached(best, releases.Count, rule, nowUtc);
    }
}
