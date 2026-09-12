using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Presentation.Views;
using LIVORA.Presentation.Components;

namespace LIVORA.Presentation;

public sealed class DataSourceRow
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public bool IsMock { get; init; }
    public Color DotColor => IsMock ? Theme.Caution : Theme.Positive;
}

public sealed class PrivacyRowViewModel
{
    public required string Name { get; init; }
    public required string OriginText { get; init; }
    public required string LocationText { get; init; }
}

public sealed class ProfileViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IFormatService _format;
    private readonly IRepository<UserProfile> _profileRepo;
    private readonly IPermissionService _permissions;
    private readonly IPrivacyService _privacy;
    private readonly SessionState _session;

    public ProfileViewModel(
        ILocalizationService loc,
        ISettingsService settings,
        IFormatService format,
        IRepository<UserProfile> profileRepo,
        IPermissionService permissions,
        IPrivacyService privacy,
        SessionState session)
    {
        _loc = loc;
        _settings = settings;
        _format = format;
        _profileRepo = profileRepo;
        _permissions = permissions;
        _privacy = privacy;
        _session = session;
        SubscribeLanguage();

        SaveNameCommand = new Command(async () => await SaveProfileAsync());
        SetEnglishCommand = new Command(() => SetLanguage(AppLanguage.English));
        SetPersianCommand = new Command(() => SetLanguage(AppLanguage.Persian));
        DeleteAllDataCommand = new Command(async () => await DeleteAllDataAsync());
    }

    public ICommand SaveNameCommand { get; }
    public ICommand SetEnglishCommand { get; }
    public ICommand SetPersianCommand { get; }
    public ICommand DeleteAllDataCommand { get; }

    public string Title => L("Profile.Title");
    public string NameLabel => L("Profile.Name");
    public string FocusAreasLabel => L("Profile.PrimaryGoals");
    public string ActivityLabel => L("Profile.ActivityLevel");
    public string ScheduleLabel => L("Profile.Schedule");
    public string BedtimeLabel => L("Onboarding.Bedtime");
    public string WakeLabel => L("Onboarding.WakeTime");
    public string LanguageLabel => L("Profile.Language");
    public string LanguageNote => L("Profile.LanguageNote");
    public string DataSourcesLabel => L("Profile.DataSource.Sample");
    public string ConnectionCenterTitle => L("Profile.ConnectionCenter");
    public string SourcesNote => L("Profile.SourcesNote");
    public string RealComingSoon => L("Profile.DataSource.RealComingSoon");
    public string PrivacyTitle => L("Privacy.Title");
    public string PrivacyNote => L("Privacy.Note");
    public string DeleteAllText => L("Privacy.DeleteAll");
    public string NotificationsLabel => L("Profile.Notifications");
    public string NotificationsSoon => L("Profile.NotificationsSoon");
    public string AppearanceLabel => L("Profile.Appearance");
    public string AppearanceSoon => L("Profile.AppearanceSoon");
    public string AboutLabel => L("Profile.About");
    public string VersionLabel => L("Profile.Version", AppInfo.Current.VersionString);
    public string SoonBadge => L("Profile.SoonBadge");
    public string SaveText => L("Common.Save");
    public string EnglishText => L("Onboarding.Language.English");
    public string PersianText => L("Onboarding.Language.Persian");

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    public string FocusAreasText => string.Join(" · ", Profile.FocusAreas.Select(k => L("Onboarding.Goal." + ToGoalKey(k))));

    private static string ToGoalKey(string area) => area switch
    {
        "sleep" => "BetterSleep",
        "energy" => "MoreEnergy",
        "fitness" => "Fitness",
        "focus" => "Focus",
        "stress" => "Stress",
        "learning" => "Learning",
        _ => "BetterSleep",
    };

    public List<string> ActivityOptions => Enum.GetValues<ActivityLevel>().Select(a => L("Enum.ActivityLevel." + a)).ToList();

    public int ActivityIndex
    {
        get => (int)Profile.ActivityLevel;
        set
        {
            if (value >= 0 && value != (int)Profile.ActivityLevel)
            {
                Profile.ActivityLevel = (ActivityLevel)value;
                Raise();
                _ = SaveProfileAsync();
            }
        }
    }

    public TimeSpan Bedtime
    {
        get => Profile.PreferredBedtime;
        set { Profile.PreferredBedtime = value; Raise(); _ = SaveProfileAsync(); }
    }

    public TimeSpan WakeTime
    {
        get => Profile.PreferredWakeTime;
        set { Profile.PreferredWakeTime = value; Raise(); _ = SaveProfileAsync(); }
    }

    public bool IsEnglish => _loc.CurrentLanguage == AppLanguage.English;
    public bool IsPersian => _loc.CurrentLanguage == AppLanguage.Persian;

    // Language buttons render as a segmented control: active = filled, inactive = soft.
    public Color EnglishButtonBackground => IsEnglish ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color EnglishButtonText => IsEnglish ? Colors.White : Theme.TextSecondary;
    public Color PersianButtonBackground => IsPersian ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color PersianButtonText => IsPersian ? Colors.White : Theme.TextSecondary;

    public List<DataSourceRow> Sources { get; private set; } = new();
    public List<DataSourceRow> ConnectionCenter { get; private set; } = new();
    public List<PrivacyRowViewModel> PrivacyRows { get; private set; } = new();

    private UserProfile Profile => _session.CurrentProfile;

    public async Task LoadAsync()
    {
        Name = Profile.Name;
        Sources = new List<DataSourceRow>
        {
            new() { Name = L("Profile.DataSource.Sample"), Status = L("Profile.Status.MockActive"), IsMock = true },
        };
        ConnectionCenter = new List<DataSourceRow>
        {
            new() { Name = L("Profile.Source.Health"), Status = L("Profile.Status.MockActive"), IsMock = true },
            new() { Name = L("Profile.Source.Activity"), Status = L("Profile.Status.NotConnected"), IsMock = false },
            new() { Name = L("Profile.Source.Calendar"), Status = L("Profile.Status.NotConnected"), IsMock = false },
            new() { Name = L("Profile.Source.Tasks"), Status = L("Profile.Status.NotConnected"), IsMock = false },
            new() { Name = L("Profile.Source.ScreenTime"), Status = L("Profile.Status.NotConnected"), IsMock = false },
        };
        PrivacyRows = (await _privacy.DescribeStoredDataAsync())
            .Select(c => new PrivacyRowViewModel
            {
                Name = L(c.Key),
                OriginText = c.Origin == DataOrigin.Manual ? L("Privacy.Origin.Manual") : L("Privacy.Origin.Mock"),
                LocationText = L(c.StorageLocationKey),
            }).ToList();
        RaiseAll();
    }

    private void RaiseAll()
    {
        Raise(nameof(Name));
        Raise(nameof(FocusAreasText));
        Raise(nameof(ActivityOptions));
        Raise(nameof(ActivityIndex));
        Raise(nameof(Bedtime));
        Raise(nameof(WakeTime));
        Raise(nameof(Sources));
        Raise(nameof(ConnectionCenter));
        Raise(nameof(PrivacyRows));
        Raise(nameof(IsEnglish));
        Raise(nameof(IsPersian));
        RaiseLanguageButtons();
    }

    private void RaiseLanguageButtons()
    {
        Raise(nameof(EnglishButtonBackground));
        Raise(nameof(EnglishButtonText));
        Raise(nameof(PersianButtonBackground));
        Raise(nameof(PersianButtonText));
    }

    private async Task SaveProfileAsync()
    {
        Profile.Name = Name.Trim();
        await _profileRepo.SaveAsync(Profile);
    }

    private void SetLanguage(AppLanguage lang)
    {
        _loc.SetLanguage(lang);
        App.ApplyFlowDirection();
    }

    private async Task DeleteAllDataAsync()
    {
        // Local-only destructive op: single confirm, then wipe + restart flow.
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        bool confirm = page is not null && await page.DisplayAlertAsync(
            L("Privacy.DeleteAll"), L("Privacy.Note"), L("Common.Yes"), L("Common.No"));
        if (!confirm) return;
        await _privacy.DeleteAllLocalDataAsync();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(NameLabel));
        Raise(nameof(FocusAreasLabel));
        Raise(nameof(ActivityLabel));
        Raise(nameof(ScheduleLabel));
        Raise(nameof(BedtimeLabel));
        Raise(nameof(WakeLabel));
        Raise(nameof(LanguageLabel));
        Raise(nameof(LanguageNote));
        Raise(nameof(DataSourcesLabel));
        Raise(nameof(ConnectionCenterTitle));
        Raise(nameof(SourcesNote));
        Raise(nameof(RealComingSoon));
        Raise(nameof(PrivacyTitle));
        Raise(nameof(PrivacyNote));
        Raise(nameof(DeleteAllText));
        Raise(nameof(NotificationsLabel));
        Raise(nameof(NotificationsSoon));
        Raise(nameof(AppearanceLabel));
        Raise(nameof(AppearanceSoon));
        Raise(nameof(AboutLabel));
        Raise(nameof(VersionLabel));
        Raise(nameof(SoonBadge));
        Raise(nameof(SaveText));
        Raise(nameof(EnglishText));
        Raise(nameof(PersianText));
        RaiseLanguageButtons();
        _ = LoadAsync();
    }
}
