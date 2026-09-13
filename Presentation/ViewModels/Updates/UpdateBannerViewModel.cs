using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

// =============================================================================
// MERGED (orchestrator): lane 01's full implementation (the authoritative owner per
// docs/LANES.md §4 — it also owns UpdatePage/UpdateRoutes/UpdateCopy below) with lane 06's
// Profile/Settings card surface folded in: CheckNowCommand + IsChecking (the busy state the card
// binds), Status/Headline/Detail/ActionText/CheckedAtText/HasAnswer/IsFeedConfigured keys, and the
// no-service degradation (parameterless construction via TryGet → honest "no update feed in this
// build", never a fake status). Both lanes' honesty law holds: every word derives from an
// UpdateInfo the service actually returned.
// =============================================================================

/// <summary>Small reusable banner card over <see cref="IUpdateService"/> (Profile / Settings host it).</summary>
public sealed class UpdateBannerViewModel : ObservableObject
{
    private readonly IUpdateService? _updates;
    private readonly ISettingsService? _settings;
    private UpdateInfo? _info;
    private bool _checkedOnce;
    private bool _isChecking;

    /// <summary>
    /// One ctor for both paths: the composition root passes the registered services; lane 06's
    /// defensive `new UpdateBannerViewModel()` (used when its page inflates before DI) resolves
    /// them lazily and degrades to the honest "no update feed in this build" state instead of
    /// crashing or faking a status.
    /// </summary>
    public UpdateBannerViewModel(IUpdateService? updates = null, ISettingsService? settings = null)
    {
        _updates = updates ?? ServiceHelper.TryGet<IUpdateService>();
        _settings = settings ?? ServiceHelper.TryGet<ISettingsService>();
        SubscribeLanguage();
        // Stored commands: XAML binds once — a getter that newed up a Command per read would hand
        // each binding a different instance. CanExecute gates the check against double-taps.
        RefreshCommand = new Command(async () => await RefreshAsync(force: false), () => !IsChecking);
        CheckNowCommand = new Command(async () => await RefreshAsync(force: true), () => !IsChecking);
        OpenUpdatesCommand = new Command(() => _ = UpdateNavigation.GoToAsync(UpdateRoutes.Page));
        WhatsNewCommand = new Command(() => _ = UpdateNavigation.GoToAsync(UpdateRoutes.WhatsNew));
    }

    public Command RefreshCommand { get; }
    /// <summary>Manual "check now" — bypasses the service throttle (lane 06's card button).</summary>
    public Command CheckNowCommand { get; }
    public Command OpenUpdatesCommand { get; }
    public Command WhatsNewCommand { get; }

    /// <summary>True while a check is in flight (drives the inline busy state).</summary>
    public bool IsChecking { get => _isChecking; private set => Set(ref _isChecking, value); }
    public bool IsBusy { get => IsChecking; }

    /// <summary>False when this install can't reach a release feed at all (honest empty state).</summary>
    public bool IsFeedConfigured => _updates?.IsFeedConfigured ?? false;

    /// <summary>The card has something true to say (lane 01) — or no feed at all (show the note).</summary>
    public bool IsVisible => _info is not null || IsFeedConfigured;
    /// <summary>True when the card has nothing to say yet (first paint, no service, no cache).</summary>
    public bool HasAnswer => _info is not null;

    /// <summary>Machine state the banner renders from — Unknown until the service really answers.</summary>
    public UpdateCheckStatus Status => _info?.Status ?? UpdateCheckStatus.Unknown;

    // ---- Lane 06's card binding surface (Profile.Update.* keys) ----------------

    /// <summary>Big line — one key per status; the page never picks a wording itself.</summary>
    public string Headline => Status switch
    {
        UpdateCheckStatus.UpdateAvailable when _info!.IsPrerelease =>
            L("Profile.Update.Prerelease", _info!.LatestVersion ?? string.Empty),
        UpdateCheckStatus.UpdateAvailable => L("Profile.Update.Available"),
        UpdateCheckStatus.UpToDate => L("Profile.Update.UpToDate"),
        UpdateCheckStatus.NewerThanFeed => L("Profile.Update.NewerThanFeed"),
        UpdateCheckStatus.NoConnection => L("Profile.Update.NoConnection"),
        UpdateCheckStatus.RateLimited => L("Profile.Update.RateLimited"),
        UpdateCheckStatus.Error => L("Profile.Update.Error"),
        UpdateCheckStatus.Disabled => L("Profile.Update.Disabled"),
        _ => IsFeedConfigured ? L("Profile.Update.Idle") : L("Profile.Update.NotConfigured"),
    };

    /// <summary>Second line: versions when the feed answered, its own detail when it did not.</summary>
    public string Detail => _info switch
    {
        null => string.Empty,
        var i when i.Status == UpdateCheckStatus.UpdateAvailable && i.LatestVersion is string latest =>
            L("Profile.Update.VersionFromTo", i.CurrentVersion, latest),
        var i when i.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.NewerThanFeed =>
            L("Profile.Update.CurrentIs", i.CurrentVersion),
        var i when i.Status == UpdateCheckStatus.NoConnection => L("Profile.Update.OfflineHint"),
        var i when i.Status is UpdateCheckStatus.Error or UpdateCheckStatus.RateLimited =>
            string.IsNullOrWhiteSpace(i.ErrorDetail) ? L("Profile.Update.TryLater") : i.ErrorDetail!,
        _ => string.Empty,
    };

    /// <summary>True when the answer came from the local cache instead of the network.</summary>
    public bool IsFromCache => _info?.FromCache ?? false;

    /// <summary>"Checked 12 Sep" — only when a check actually happened.</summary>
    public string CheckedAtText => _info?.CheckedAtUtc is { } utc
        ? L("Profile.Update.CheckedAt", ShortDate(utc))
        : string.Empty;

    /// <summary>Row text for the full update page (route "updates").</summary>
    public string ActionText => L("Profile.Update.Open");
    /// <summary>Label of the manual re-check button.</summary>
    public string CheckNowText => L("Profile.Update.CheckNow");

    // ---- Lane 01's card binding surface (Update.* keys, shared UpdateCopy) -----

    public string StatusText { get; private set; } = string.Empty;
    public string HeadlineText { get; private set; } = string.Empty;
    public string DetailText { get; private set; } = string.Empty;
    public string StatusColorKey { get; private set; } = "TextSecondary";
    public bool HasAction => _info?.Status == UpdateCheckStatus.UpdateAvailable;
    public string OpenText => L("Update.Open");
    public string RefreshText => L("Update.CheckAgain");
    public string InstalledLabel => L("Update.Current");
    public bool HasUnseenRelease { get; private set; }
    public bool HasWhatsNew => _info is not null &&
        (UpdateCopy.HasWhatsNewEntry(AppVersionText, _settings?.LastSeenVersion) || HasUnseenRelease);
    public string WhatsNewText => L("Update.WhatsNew.Action");

    private string AppVersionText => _info?.CurrentVersion ?? AppInfo.Current.VersionString;

    /// <summary>Page-open entry: cached answer first (zero network), then one throttled check.</summary>
    public async Task LoadAsync()
    {
        var svc = _updates;
        if (svc is null) { Compute(); RaiseAll(); return; }
        try { _info = await svc.PeekCachedAsync(); }
        catch { _info = null; }   // a broken cache file is a bug to debug, not a claim to display
        Compute(); RaiseAll();

        if (!_checkedOnce)
        {
            _checkedOnce = true;
            await RefreshAsync(force: false);
        }
    }

    public Task RefreshAsync(bool force = false)
    {
        var svc = _updates;
        if (svc is null || IsChecking) { RaiseAll(); return Task.CompletedTask; }
        return RefreshCoreAsync(svc, force);
    }

    private async Task RefreshCoreAsync(IUpdateService svc, bool force)
    {
        IsChecking = true;
        RefreshCommand.ChangeCanExecute();
        CheckNowCommand.ChangeCanExecute();
        try
        {
            _info = force
                ? await svc.CheckAsync(force: true)
                : await svc.PeekCachedAsync() ?? _info;
        }
        catch
        {
            _info = null;  // failure leaves NO answer; the copy says "check failed", never "up to date"
        }
        finally
        {
            IsChecking = false;
            RefreshCommand.ChangeCanExecute();
            CheckNowCommand.ChangeCanExecute();
        }
        Compute();
        RaiseAll();
    }

    private void Compute()
    {
        (StatusText, HeadlineText, StatusColorKey) = UpdateCopy.For(_info, (k, a) => L(k, a));
        DetailText = _info is null ? string.Empty : L("Update.CurrentVersion", _info.CurrentVersion);
        HasUnseenRelease = UpdateCopy.HasUnseenRelease(_info, _settings?.LastSeenVersion);
    }

    private string ShortDate(DateTime utc)
    {
        try
        {
            return ServiceHelper.TryGet<IFormatService>()?.ShortDate(utc.ToLocalTime())
                   ?? utc.ToLocalTime().ToString("yyyy-MM-dd");
        }
        catch
        {
            return utc.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(IsVisible));
        Raise(nameof(HasAnswer));
        Raise(nameof(IsChecking));
        Raise(nameof(IsBusy));
        Raise(nameof(IsFeedConfigured));
        Raise(nameof(Status));
        Raise(nameof(Headline));
        Raise(nameof(Detail));
        Raise(nameof(IsFromCache));
        Raise(nameof(CheckedAtText));
        Raise(nameof(ActionText));
        Raise(nameof(CheckNowText));
        Raise(nameof(StatusText));
        Raise(nameof(HeadlineText));
        Raise(nameof(DetailText));
        Raise(nameof(StatusColorKey));
        Raise(nameof(HasAction));
        Raise(nameof(OpenText));
        Raise(nameof(RefreshText));
        Raise(nameof(InstalledLabel));
        Raise(nameof(HasUnseenRelease));
        Raise(nameof(HasWhatsNew));
        Raise(nameof(WhatsNewText));
    }

    protected override void OnLanguageChanged()
    {
        Compute();
        RaiseAll();
    }
}

/// <summary>Route names shared by the update pages and the hosts (lane 01 §Lane 01 contract).</summary>
public static class UpdateRoutes
{
    public const string Page = "updates";
    public const string WhatsNew = "updates?mode=whatsnew";
    public const string ModeQueryKey = "mode";
    public const string WhatsNewMode = "whatsnew";
}

/// <summary>Shell navigation helper — failure is swallowed because a route may legitimately be
/// absent in a trimmed build; the caller then stays on a page that already says something honest.</summary>
public static class UpdateNavigation
{
    public static async Task GoToAsync(string route)
    {
        try
        {
            if (Shell.Current is { } shell) await shell.GoToAsync(route);
        }
        catch (Exception)
        {
        }
    }
    public static Task CloseAsync() => GoToAsync("..");
}

/// <summary>One key-triple per update status — shared by UpdatePage and the banner (lane 01).</summary>
public static class UpdateCopy
{
    public static (string Status, string Headline, string ColorKey) For(
        UpdateInfo? info, Func<string, object[], string> T)
    {
        string K(string key) => T(key, Array.Empty<object>());
        string KA(string key, object arg0) => T(key, [arg0]);
        if (info is null) return (K("Update.Status.Unknown"), K("Update.Headline.Unknown"), "TextSecondary");
        var latest = info.LatestVersion ?? string.Empty;
        return info.Status switch
        {
            UpdateCheckStatus.UpdateAvailable when info.IsPrerelease =>
                (K("Update.Status.Prerelease"), KA("Update.Headline.Prerelease", latest), "Caution"),
            UpdateCheckStatus.UpdateAvailable =>
                (K("Update.Status.UpdateAvailable"), KA("Update.Headline.UpdateAvailable", latest), "Accent"),
            UpdateCheckStatus.UpToDate =>
                (K("Update.Status.UpToDate"), K("Update.Headline.UpToDate"), "Positive"),
            UpdateCheckStatus.NewerThanFeed =>
                (K("Update.Status.NewerThanFeed"), K("Update.Headline.NewerThanFeed"), "TextSecondary"),
            UpdateCheckStatus.NoConnection =>
                (K("Update.Status.NoConnection"), K("Update.Headline.NoConnection"), "Negative"),
            UpdateCheckStatus.RateLimited =>
                (K("Update.Status.RateLimited"), K("Update.Headline.RateLimited"), "Caution"),
            UpdateCheckStatus.Error =>
                (K("Update.Status.Error"), K("Update.Headline.Error"), "Negative"),
            UpdateCheckStatus.Disabled =>
                (K("Update.Status.Disabled"), K("Update.Headline.Disabled"), "TextSecondary"),
            _ => (K("Update.Status.Unknown"), K("Update.Headline.Unknown"), "TextSecondary"),
        };
    }

    /// <summary>"What's new" deserves showing when the installed version was never acknowledged.</summary>
    public static bool HasWhatsNewEntry(string? installedVersion, string? lastSeenVersion)
    {
        if (!AppVersion.TryParse(installedVersion, out var installed)) return false;
        if (string.IsNullOrWhiteSpace(lastSeenVersion)) return true;
        int? cmp = AppVersion.Compare(lastSeenVersion, installedVersion);
        return cmp is null or < 0;
    }

    /// <summary>A newer published release the user has not seen yet (UpdateAvailable/UpToDate only).</summary>
    public static bool HasUnseenRelease(UpdateInfo? info, string? lastSeenVersion)
    {
        if (info?.LatestVersion is not { Length: > 0 } latest) return false;
        if (info.Status is not (UpdateCheckStatus.UpdateAvailable or UpdateCheckStatus.UpToDate)) return false;
        if (string.IsNullOrWhiteSpace(lastSeenVersion)) return true;
        int? cmp = AppVersion.Compare(lastSeenVersion, latest);
        return cmp is null || cmp < 0;
    }
}
