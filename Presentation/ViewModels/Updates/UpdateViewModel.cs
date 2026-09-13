using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Updates; // ReleaseFeedParser.FileNameOf for asset file names
namespace LIVORA.Presentation;
public sealed class UpdateNoteRow
{
    public required string Text { get; init; }
    public bool IsBullet { get; init; }
    public string Glyph => IsBullet ? "•" : string.Empty;
}
public sealed class UpdateDownloadRow
{
    public required string Url { get; init; }
    public required string Label { get; init; }
    public required string DownloadText { get; init; }
    public string FileName { get; init; } = string.Empty;
    public bool IsForThisDevice { get; init; }
    internal bool IsReleasePage { get; init; }
    public ICommand? Command { get; init; }
}
public sealed class UpdateViewModel : ObservableObject, IQueryAttributable
{
    private readonly IUpdateService _updates;
    private readonly ISettingsService _settings;
    private readonly IFormatService _format;
    private readonly Func<string, Task> _openUrl;
    private readonly Func<string> _platformKey;
    private UpdateInfo? _info;
    private DateTime? _checkedAtLocal;
    private bool _whatsNewRequested;
    private bool _loaded;
    public UpdateViewModel(IUpdateService updates, ISettingsService settings, IFormatService format)
        : this(updates, settings, format, OpenInBrowserAsync, CurrentPlatformKey)
    {
    }
    internal UpdateViewModel(
        IUpdateService updates,
        ISettingsService settings,
        IFormatService format,
        Func<string, Task> openUrl,
        Func<string> platformKey)
    {
        _updates = updates;
        _settings = settings;
        _format = format;
        _openUrl = openUrl;
        _platformKey = platformKey;
        SubscribeLanguage();
        CheckAgainCommand = new Command(async () => await CheckAsync(force: true), () => !IsBusy);
        OpenReleasePageCommand = new Command(async () => await OpenAsync(_info?.ReleaseUrl));
        PrimaryDownloadCommand = new Command(async () => await OpenAsync(PrimaryDownload?.Url));
        CloseCommand = new Command(() => _ = UpdateNavigation.CloseAsync());
    }
    private static Task OpenInBrowserAsync(string url) => Browser.OpenAsync(url);
    private static string CurrentPlatformKey()
    {
        try { return DeviceInfo.Current.Platform.ToString().ToLowerInvariant(); }
        catch (Exception) { return string.Empty; }
    }
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue(UpdateRoutes.ModeQueryKey, out var value) &&
            string.Equals(value?.ToString(), UpdateRoutes.WhatsNewMode, StringComparison.OrdinalIgnoreCase))
        {
            _whatsNewRequested = true;
        }
    }
    public ICommand CheckAgainCommand { get; }
    public ICommand OpenReleasePageCommand { get; }
    public ICommand PrimaryDownloadCommand { get; }
    public ICommand CloseCommand { get; }
    public string Title => L("Update.Title");
    public string WhatsNewTitle => L("Update.WhatsNew.Title");
    public string ReleasePageText => L("Update.OpenReleasePage");
    public string DownloadActionText => L("Update.Download.Action");
    public string PageTitle => IsWhatsNew ? WhatsNewTitle : Title;
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) (CheckAgainCommand as Command)?.ChangeCanExecute();
        }
    }
    private bool _isWhatsNew;
    public bool IsWhatsNew
    {
        get => _isWhatsNew;
        private set
        {
            if (Set(ref _isWhatsNew, value))
            {
                Raise(nameof(PageTitle));
                Raise(nameof(ShowWhatsNewChip));
            }
        }
    }
    public bool ShowWhatsNewChip => !IsWhatsNew;
    public string StatusText { get; private set; } = string.Empty;
    public string HeadlineText { get; private set; } = string.Empty;
    public string StatusColorKey { get; private set; } = "TextSecondary";
    public string InstalledText { get; private set; } = string.Empty;
    public string PublishedText { get; private set; } = string.Empty;
    public string LastCheckedText { get; private set; } = string.Empty;
    public string DetailText { get; private set; } = string.Empty;
    public bool HasDetail { get; private set; }
    public bool IsPrereleaseChannel => _info?.IsPrerelease == true;
    public bool IsSourceInstall => _info?.IsNewerThanFeed == true;
    public bool FeedUnavailable => !_updates.IsFeedConfigured || _info?.Status == UpdateCheckStatus.Disabled;
    public bool HasReleaseUrl => !string.IsNullOrWhiteSpace(_info?.ReleaseUrl);
    public string DeviceText { get; private set; } = string.Empty;
    public List<UpdateNoteRow> Notes { get; private set; } = new();
    public List<UpdateDownloadRow> Downloads { get; private set; } = new();
    public bool HasNotes => Notes.Count > 0;
    public bool HasDownloads => Downloads.Count > 0;
    private UpdateDownloadRow? PrimaryDownload { get; set; }
    public bool HasPrimaryDownload => PrimaryDownload is not null;
    public string PrimaryDownloadText => PrimaryDownload?.DownloadText ?? DownloadActionText;
    public string PrimaryDownloadFile => PrimaryDownload?.FileName ?? string.Empty;
    public bool HasPrimaryDownloadFile => !string.IsNullOrWhiteSpace(PrimaryDownloadFile);
    public async Task LoadAsync()
    {
        if (_loaded) return; // a page re-appear must not re-request; "Check again" is the only forced path
        _loaded = true;
        if (_whatsNewRequested)
        {
            IsWhatsNew = true;
            _whatsNewRequested = false;
        }
        var cached = await SafePeekAsync();
        Project(cached);
        await CheckAsync(force: false);
    }
    private async Task<UpdateInfo?> SafePeekAsync()
    {
        try { return await _updates.PeekCachedAsync(); }
        catch (Exception) { return null; }
    }
    public async Task CheckAsync(bool force)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var info = await _updates.CheckAsync(force);
            Project(info); // records the seen version itself, once the notes are really on screen
        }
        catch (Exception)
        {
            Project(null);
            SetDetail(L("Update.Error.Feed"));
        }
        finally
        {
            IsBusy = false;
        }
    }
    private void Project(UpdateInfo? info)
    {
        _info = info;
        HasDetail = false;
        _checkedAtLocal = info?.CheckedAtUtc is DateTime utc && utc != default
            ? utc.ToLocalTime()
            : null;
        (StatusText, HeadlineText, StatusColorKey) = UpdateCopy.For(info, (k, a) => L(k, a));
        InstalledText = L("Profile.Version", info?.CurrentVersion ?? L("Update.VersionUnknown"));
        PublishedText = info?.LatestVersion is { Length: > 0 } latest
            ? L("Update.PublishedVersion", latest)
            : L("Update.LatestUnknown");
        LastCheckedText = _checkedAtLocal is DateTime whenUtc
            ? L("Update.LastChecked", _format.LongDate(whenUtc), _format.Time(whenUtc.TimeOfDay))
            : L("Update.NeverChecked");
        DetailText = ResolveDetail(info);
        HasDetail = !string.IsNullOrWhiteSpace(DetailText);
        DeviceText = PlatformLabel(DevicePlatformKey());
        Notes = (info?.Notes ?? Array.Empty<string>())
            .Select(n => new UpdateNoteRow
            {
                Text = n.StartsWith("•", StringComparison.Ordinal) ? n[1..].TrimStart() : n,
                IsBullet = n.StartsWith("•", StringComparison.Ordinal),
            })
            .ToList();
        Downloads = BuildDownloads(info);
        PrimaryDownload = PickPrimary(Downloads);
        if (Notes.Count > 0) RecordSeenVersion(info);
        RaiseAll();
    }
    private string ResolveDetail(UpdateInfo? info)
    {
        if (info is null) return L("Update.Headline.Unknown");
        switch (info.Status)
        {
            case UpdateCheckStatus.NoConnection:
            case UpdateCheckStatus.RateLimited:
            case UpdateCheckStatus.Error:
                return string.IsNullOrWhiteSpace(info.ErrorDetail)
                    ? L("Update.Headline.Error")
                    : info.ErrorDetail!.Trim();
            case UpdateCheckStatus.Disabled:
                return L("Update.Note.Disabled");
            case UpdateCheckStatus.NewerThanFeed:
                return L("Update.Note.SourceInstall");
            case UpdateCheckStatus.UpdateAvailable when info.IsPrerelease:
                return L("Update.Note.Prerelease");
            default:
                return info.FromCache ? L("Update.Note.FromCache") : string.Empty;
        }
    }
    private List<UpdateDownloadRow> BuildDownloads(UpdateInfo? info)
    {
        var rows = new List<UpdateDownloadRow>();
        if (info is null) return rows;
        var device = DevicePlatformKey();
        foreach (var key in PlatformOrder)
        {
            if (!info.DownloadLinks.TryGetValue(key, out var url) || string.IsNullOrWhiteSpace(url)) continue;
            rows.Add(NewRow(url, PlatformLabel(key), string.Equals(key, device, StringComparison.Ordinal), isReleasePage: false));
        }
        if (rows.Count == 0 && !string.IsNullOrWhiteSpace(info.ReleaseUrl))
        {
            rows.Add(NewRow(info.ReleaseUrl!, ReleasePageText, isForThisDevice: true, isReleasePage: true));
        }
        return rows;
    }
    private UpdateDownloadRow NewRow(string url, string label, bool isForThisDevice, bool isReleasePage)
    {
        var open = url;
        return new UpdateDownloadRow
        {
            Url = url,
            Label = label,
            DownloadText = isReleasePage ? label : L("Update.Download", label),
            FileName = ReleaseFeedParser.FileNameOf(url),
            IsForThisDevice = isForThisDevice,
            IsReleasePage = isReleasePage,
            Command = new Command(async () => await OpenAsync(open)),
        };
    }
    private static UpdateDownloadRow? PickPrimary(List<UpdateDownloadRow> rows)
        => rows.FirstOrDefault(r => r.IsForThisDevice && !r.IsReleasePage)
           ?? rows.FirstOrDefault(r => !r.IsReleasePage)
           ?? rows.FirstOrDefault();
    private static readonly string[] PlatformOrder = { "android", "ios", "windows", "macos", "all" };
    private string DevicePlatformKey()
    {
        var raw = SafePlatformKey();
        if (raw.Contains("android")) return "android";
        if (raw.Contains("ios") || raw.Contains("iphone")) return "ios";
        if (raw.Contains("win")) return "windows";
        if (raw.Contains("maccatalyst") || raw.Contains("macos") || raw.Contains("osx") || raw.Contains("mac")) return "macos";
        return "unknown";
    }
    private string SafePlatformKey()
    {
        try { return _platformKey()?.ToLowerInvariant() ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }
    private string PlatformLabel(string key) => key switch
    {
        "android" => L("Update.Platform.Android"),
        "ios" => L("Update.Platform.iOS"),
        "windows" => L("Update.Platform.Windows"),
        "macos" => L("Update.Platform.MacOS"),
        "all" => L("Update.Platform.All"),
        _ => L("Update.Platform.Unknown"),
    };
    public async Task OpenAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            SetDetail(L("Update.Error.NoDownload"));
            return;
        }
        try
        {
            await _openUrl(url);
            SetDetail(L("Update.Note.OpenedBrowser"));
        }
        catch (Exception)
        {
            var name = ReleaseFeedParser.FileNameOf(url);
            SetDetail(L("Update.Error.OpenLink", string.IsNullOrWhiteSpace(name) ? url : name));
        }
    }
    private void SetDetail(string text)
    {
        DetailText = text;
        HasDetail = true;
        Raise(nameof(DetailText));
        Raise(nameof(HasDetail));
    }
    public void RecordSeenVersion(UpdateInfo? info)
    {
        var candidate = _settings.LastSeenVersion;
        if (AppVersion.TryParse(info?.LatestVersion, out var latest) &&
            (!AppVersion.TryParse(candidate, out var seen) || latest > seen))
        {
            candidate = info!.LatestVersion;
        }
        if (AppVersion.TryParse(info?.CurrentVersion, out var current) &&
            (!AppVersion.TryParse(candidate, out var seen2) || current > seen2))
        {
            candidate = info!.CurrentVersion;
        }
        if (string.IsNullOrWhiteSpace(candidate)) return;
        try
        {
            if (!string.Equals(_settings.LastSeenVersion, candidate, StringComparison.Ordinal))
                _settings.LastSeenVersion = candidate;
        }
        catch (Exception) { /* a preferences write must never break the page */ }
    }
    protected override void OnLanguageChanged()
    {
        Project(_info);
    }
    private void RaiseAll()
    {
        Raise(nameof(StatusText));
        Raise(nameof(HeadlineText));
        Raise(nameof(StatusColorKey));
        Raise(nameof(InstalledText));
        Raise(nameof(PublishedText));
        Raise(nameof(LastCheckedText));
        Raise(nameof(DetailText));
        Raise(nameof(HasDetail));
        Raise(nameof(IsPrereleaseChannel));
        Raise(nameof(IsSourceInstall));
        Raise(nameof(FeedUnavailable));
        Raise(nameof(HasReleaseUrl));
        Raise(nameof(DeviceText));
        Raise(nameof(Notes));
        Raise(nameof(Downloads));
        Raise(nameof(HasNotes));
        Raise(nameof(HasDownloads));
        Raise(nameof(HasPrimaryDownload));
        Raise(nameof(PrimaryDownloadText));
        Raise(nameof(PrimaryDownloadFile));
        Raise(nameof(HasPrimaryDownloadFile));
        Raise(nameof(IsWhatsNew));
        Raise(nameof(ShowWhatsNewChip));
        Raise(nameof(PageTitle));
        Raise(nameof(Title));
        Raise(nameof(WhatsNewTitle));
        Raise(nameof(ReleasePageText));
        Raise(nameof(DownloadActionText));
    }
}
