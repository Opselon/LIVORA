using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using Microsoft.Extensions.Logging;
namespace LIVORA.Infrastructure.Updates;
public sealed class UpdateService : IUpdateService
{
    internal static readonly TimeSpan ReCheckThrottle = TimeSpan.FromHours(6);
    private readonly IReleaseFeed _feed;
    private readonly UpdateFeedCache _cache;
    private readonly ILogger<UpdateService> _logger;
    private readonly Func<string, string> _localizer;
    private readonly Func<string> _installedVersion;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _fetchGate = new(1, 1);
    private UpdateInfo? _sessionAnswer;
    private Task<UpdateInfo>? _inFlight;
    private readonly object _flightLock = new();
    public UpdateService(
        IReleaseFeed feed,
        UpdateFeedCache cache,
        ILogger<UpdateService> logger,
        ILocalizationService localization)
        : this(feed, cache, logger, key => localization[key], () => AppInfo.Current.VersionString, () => DateTime.UtcNow)
    {
    }
    internal UpdateService(
        IReleaseFeed feed,
        UpdateFeedCache cache,
        ILogger<UpdateService> logger,
        Func<string, string> localizer,
        Func<string> installedVersion,
        Func<DateTime>? utcNow = null)
    {
        _feed = feed;
        _cache = cache;
        _logger = logger;
        _localizer = localizer;
        _installedVersion = installedVersion;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }
    public bool IsFeedConfigured => _feed.IsConfigured;
    private string SafeVersion()
    {
        try
        {
            var v = _installedVersion();
            return string.IsNullOrWhiteSpace(v) ? "0.0" : v.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the installed app version");
            return "0.0";
        }
    }
    public Task<UpdateInfo> CheckAsync(bool force, CancellationToken ct = default)
    {
        lock (_flightLock)
        {
            if (!force && _inFlight is { } running) return running;
            _inFlight = CheckCoreAsync(force, ct);
            return _inFlight;
        }
    }
    private async Task<UpdateInfo> CheckCoreAsync(bool force, CancellationToken ct)
    {
        try
        {
            var installed = SafeVersion();
            var now = _utcNow();
            if (!_feed.IsConfigured)
            {
                var disabled = UpdateEvaluator.Project(
                    FeedAnswer.Failed(UpdateCheckFailure.NotConfigured, UpdateEvaluator.KeyErrorFeed, now),
                    installed, _localizer, fromCache: false);
                _sessionAnswer = disabled;
                return disabled;
            }
            var cached = _cache.Load();
            if (!force && LastAttemptedRecently(cached, now))
            {
                FeedAnswer? cachedAnswer = cached is null ? null : UpdateFeedCache.ToAnswer(cached);
                var throttled = _sessionAnswer is not null &&
                               string.Equals(_sessionAnswer.CurrentVersion, installed, StringComparison.Ordinal)
                    ? _sessionAnswer
                    : cachedAnswer is null
                        ? null
                        : UpdateEvaluator.Project(cachedAnswer, installed, _localizer, fromCache: true);
                if (throttled is not null)
                {
                    _logger.LogDebug("Update check throttled; serving the cached answer");
                    return throttled.FromCache ? throttled : throttled with { FromCache = true };
                }
            }
            await _fetchGate.WaitAsync(ct).ConfigureAwait(false);
            FeedAnswer answer;
            try
            {
                answer = await _feed.FetchAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _fetchGate.Release();
            }
            var record = UpdateFeedRecord.FromAnswer(answer, installed, _utcNow(), cached);
            await _cache.SaveAsync(record).ConfigureAwait(false);
            var info = UpdateEvaluator.Project(answer, installed, _localizer, fromCache: false);
            _sessionAnswer = info;
            return info;
        }
        catch (OperationCanceledException)
        {
            var info = await PeekCachedAsync(CancellationToken.None).ConfigureAwait(false);
            return info ?? Unchecked(SafeVersion());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed unexpectedly");
            var failed = FeedAnswer.Failed(UpdateCheckFailure.UnderstoodNothing, UpdateEvaluator.KeyErrorFeed, _utcNow());
            var info = UpdateEvaluator.Project(failed, SafeVersion(), _localizer, fromCache: false);
            _sessionAnswer = info;
            return info;
        }
        finally
        {
            lock (_flightLock) _inFlight = null;
        }
    }
    private static bool LastAttemptedRecently(UpdateFeedRecord? record, DateTime nowUtc)
        => record?.LastAttemptUtc is { } attempt && nowUtc - attempt < ReCheckThrottle;
    public Task<UpdateInfo?> PeekCachedAsync(CancellationToken ct = default)
    {
        var installed = SafeVersion();
        if (_sessionAnswer is { } session &&
            string.Equals(session.CurrentVersion, installed, StringComparison.Ordinal))
            return Task.FromResult<UpdateInfo?>(session);
        try
        {
            var record = _cache.Load();
            var answer = UpdateFeedCache.ToAnswer(record);
            if (answer is null) return Task.FromResult<UpdateInfo?>(null);
            var info = UpdateEvaluator.Project(answer, installed, _localizer, fromCache: true);
            return Task.FromResult<UpdateInfo?>(info);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the cached update answer");
            return Task.FromResult<UpdateInfo?>(null);
        }
    }
    public Task ClearCacheAsync()
    {
        _sessionAnswer = null;
        return _cache.ClearAsync();
    }
    internal static UpdateInfo Unchecked(string installed) => new()
    {
        Status = UpdateCheckStatus.Unknown,
        CurrentVersion = installed,
    };
}
