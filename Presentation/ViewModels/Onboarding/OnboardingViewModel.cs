using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IRepository<UserProfile> _profileRepo;
    private readonly SessionState _session;

    public OnboardingViewModel(
        ILocalizationService loc,
        ISettingsService settings,
        IRepository<UserProfile> profileRepo,
        SessionState session)
    {
        _loc = loc;
        _settings = settings;
        _profileRepo = profileRepo;
        _session = session;
        SubscribeLanguage();

        FinishCommand = new Command(async () => await FinishAsync());
        SetEnglishCommand = new Command(() => SetLanguage(AppLanguage.English));
        SetPersianCommand = new Command(() => SetLanguage(AppLanguage.Persian));
    }

    public ICommand FinishCommand { get; }
    public ICommand SetEnglishCommand { get; }
    public ICommand SetPersianCommand { get; }

    public string WelcomeTitle => L("Onboarding.Welcome.Title");
    public string WelcomeSubtitle => L("Onboarding.Welcome.Subtitle");
    public string NamePrompt => L("Onboarding.Step.Name");
    public string NamePlaceholder => L("Onboarding.Name.Placeholder");
    public string ActivityPrompt => L("Onboarding.Step.Activity");
    public string SchedulePrompt => L("Onboarding.Step.Schedule");
    public string LanguagePrompt => L("Onboarding.Step.Language");
    public string BedtimeLabel => L("Onboarding.Bedtime");
    public string WakeLabel => L("Onboarding.WakeTime");
    public string FinishNote => L("Onboarding.Finish.Note");
    public string GetStartedText => L("Common.GetStarted");
    public string EnglishText => L("Onboarding.Language.English");
    public string PersianText => L("Onboarding.Language.Persian");

    public List<string> ActivityOptions => Enum.GetValues<ActivityLevel>().Select(a => L("Enum.ActivityLevel." + a)).ToList();

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    private int _activityIndex = (int)ActivityLevel.Moderate;
    public int ActivityIndex { get => _activityIndex; set => Set(ref _activityIndex, value); }

    private TimeSpan _bedtime = new(23, 0, 0);
    public TimeSpan Bedtime { get => _bedtime; set => Set(ref _bedtime, value); }

    private TimeSpan _wakeTime = new(7, 0, 0);
    public TimeSpan WakeTime { get => _wakeTime; set => Set(ref _wakeTime, value); }

    public bool IsEnglish => _loc.CurrentLanguage == AppLanguage.English;
    public bool IsPersian => _loc.CurrentLanguage == AppLanguage.Persian;

    public Color EnglishButtonBackground => IsEnglish ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color EnglishButtonText => IsEnglish ? Colors.White : Theme.TextSecondary;
    public Color PersianButtonBackground => IsPersian ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color PersianButtonText => IsPersian ? Colors.White : Theme.TextSecondary;

    private void SetLanguage(AppLanguage lang)
    {
        _loc.SetLanguage(lang);
        App.ApplyFlowDirection();
        RaiseLanguageProps();
    }

    private void RaiseLanguageProps()
    {
        Raise(nameof(IsEnglish));
        Raise(nameof(IsPersian));
        Raise(nameof(EnglishButtonBackground));
        Raise(nameof(EnglishButtonText));
        Raise(nameof(PersianButtonBackground));
        Raise(nameof(PersianButtonText));
    }

    private async Task FinishAsync()
    {
        var profile = _session.CurrentProfile;
        profile.Name = Name.Trim();
        profile.ActivityLevel = (ActivityLevel)ActivityIndex;
        profile.PreferredBedtime = Bedtime;
        profile.PreferredWakeTime = WakeTime;
        profile.OnboardingCompleted = true;
        await _profileRepo.SaveAsync(profile);

        _settings.OnboardingCompleted = true;
        App.ApplyFlowDirection();
        if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault() is { } w)
            w.Page = new AppShell();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(WelcomeTitle));
        Raise(nameof(WelcomeSubtitle));
        Raise(nameof(NamePrompt));
        Raise(nameof(NamePlaceholder));
        Raise(nameof(ActivityPrompt));
        Raise(nameof(SchedulePrompt));
        Raise(nameof(LanguagePrompt));
        Raise(nameof(BedtimeLabel));
        Raise(nameof(WakeLabel));
        Raise(nameof(FinishNote));
        Raise(nameof(GetStartedText));
        Raise(nameof(EnglishText));
        Raise(nameof(PersianText));
        Raise(nameof(ActivityOptions));
        RaiseLanguageProps();
    }
}
