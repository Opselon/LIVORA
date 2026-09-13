using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;

/// <summary>
/// Multi-step first-run flow: welcome/name → focus areas (real multi-select, persisted into
/// <see cref="UserProfile.FocusAreas"/> as machine tags) → activity → schedule → language.
///
/// Wave 3 fix (orchestrator addendum): the Wave 2 page collected everything EXCEPT focus areas —
/// the model shipped a hardcoded single "sleep" tag that nothing ever wrote, while the step's
/// resx keys sat unused and four decorative dots promised steps that didn't exist. Now the step
/// indicator reflects the actual step (and is the only honest progress bar this flow has), the
/// chips write real tags, and every step's data reaches the profile repository on finish.
///
/// The tags are the contract with lane 08's program recommender: exactly
/// "sleep" | "energy" | "fitness" | "focus" | "stress" | "learning" — never localized text.
/// Language remains switchable live on every step (the language card rides along on the last
/// step and the header), because a user who can't yet read the app must still be able to change it.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IRepository<UserProfile> _profileRepo;
    private readonly SessionState _session;

    /// <summary>Step ids in flow order (machine values, never displayed).</summary>
    public const int StepCount = 5;
    private const int WelcomeStep = 0, GoalsStep = 1, ActivityStep = 2, ScheduleStep = 3, LanguageStep = 4;

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

        NextCommand = new Command(async () => await NextAsync());
        BackCommand = new Command(() => { if (StepIndex > 0) StepIndex--; });
        // No skip on purpose: skipping past a step is how Wave 2 lost the focus areas.
        FinishCommand = new Command(async () => await FinishAsync());
        SetEnglishCommand = new Command(() => SetLanguage(AppLanguage.English));
        SetPersianCommand = new Command(() => SetLanguage(AppLanguage.Persian));

        // Edit directly against the live session profile: a mid-flow crash must not lose
        // what the user already typed, and the model default ("sleep") is a sane starting pick.
        var profile = _session.CurrentProfile;
        _name = profile.Name;
        _activityIndex = (int)profile.ActivityLevel;
        _bedtime = profile.PreferredBedtime;
        _wakeTime = profile.PreferredWakeTime;
        RebuildGoalChips();
    }

    public Command NextCommand { get; }
    public Command BackCommand { get; }
    public Command FinishCommand { get; }
    public Command SetEnglishCommand { get; }
    public Command SetPersianCommand { get; }

    /// <summary>Raised when the flow completes and the shell may replace the window root.</summary>
    public event Action? Completed;

    // ---- Step machine -----------------------------------------------------------

    private int _stepIndex;
    public int StepIndex
    {
        get => _stepIndex;
        private set
        {
            var clamped = Math.Clamp(value, 0, StepCount - 1);
            if (!Set(ref _stepIndex, clamped)) return;
            RaiseStepFlags();
        }
    }

    public bool IsWelcomeStep => StepIndex == WelcomeStep;
    // Indicator segments: reached = the step has been started (accent), not reached = overlay.
    public bool Step0Reached => StepIndex >= WelcomeStep;
    public bool Step1Reached => StepIndex >= GoalsStep;
    public bool Step2Reached => StepIndex >= ActivityStep;
    public bool Step3Reached => StepIndex >= ScheduleStep;
    public bool Step4Reached => StepIndex >= LanguageStep;
    public bool IsGoalsStep => StepIndex == GoalsStep;
    public bool IsActivityStep => StepIndex == ActivityStep;
    public bool IsScheduleStep => StepIndex == ScheduleStep;
    public bool IsLanguageStep => StepIndex == LanguageStep;
    public bool CanGoBack => StepIndex > WelcomeStep;
    public bool IsLastStep => StepIndex == LanguageStep;
    public string StepCounter => L("Onboarding.StepCounter", _formatNumber(StepIndex + 1), _formatNumber(StepCount));

    /// <summary>Forward button copy: "Get started" only on the last step.</summary>
    public string NextText => IsLastStep ? L("Common.GetStarted") : L("Common.Next");
    public string BackText => L("Common.Back");

    // The forward path holds only one real gate: at least one focus area (the state + program
    // engines read the set). The name is deliberately optional — the app greets "you" without it.
    private async Task NextAsync()
    {
        if (IsGoalsStep && SelectedAreas.Count == 0)
        {
            _validationKey = "Onboarding.Goals.Required";
            RaiseValidation();
            return;
        }
        ClearValidation();
        if (IsLastStep) { await FinishAsync(); return; }
        StepIndex++;
    }

    private string _validationKey = string.Empty;
    /// <summary>The step's inline refusal (min/max/required), kept as a KEY so it re-localizes.</summary>
    public string ValidationHint => _validationKey.Length > 0 ? L(_validationKey) : string.Empty;
    public bool HasValidationHint => _validationKey.Length > 0;

    private void RaiseValidation() { Raise(nameof(ValidationHint)); Raise(nameof(HasValidationHint)); }
    private void ClearValidation() { if (_validationKey.Length > 0) { _validationKey = string.Empty; RaiseValidation(); } }

    private void RaiseStepFlags()
    {
        Raise(nameof(IsWelcomeStep));
        Raise(nameof(Step0Reached));
        Raise(nameof(Step1Reached));
        Raise(nameof(Step2Reached));
        Raise(nameof(Step3Reached));
        Raise(nameof(Step4Reached));
        Raise(nameof(IsGoalsStep));
        Raise(nameof(IsActivityStep));
        Raise(nameof(IsScheduleStep));
        Raise(nameof(IsLanguageStep));
        Raise(nameof(CanGoBack));
        Raise(nameof(IsLastStep));
        Raise(nameof(NextText));
        Raise(nameof(StepCounter));
    }

    // ---- Step copy --------------------------------------------------------------

    public string WelcomeTitle => L("Onboarding.Welcome.Title");
    public string WelcomeSubtitle => L("Onboarding.Welcome.Subtitle");
    public string NamePrompt => L("Onboarding.Step.Name");
    public string NamePlaceholder => L("Onboarding.Name.Placeholder");
    public string GoalsPrompt => L("Onboarding.Step.Goals");
    public string GoalsHint => L("Onboarding.Goals.Hint");
    public string ActivityPrompt => L("Onboarding.Step.Activity");
    public string SchedulePrompt => L("Onboarding.Step.Schedule");
    public string BedtimeLabel => L("Onboarding.Bedtime");
    public string WakeLabel => L("Onboarding.WakeTime");
    public string LanguagePrompt => L("Onboarding.Step.Language");
    public string FinishNote => L("Onboarding.Finish.Note");
    public string EnglishText => L("Onboarding.Language.English");
    public string PersianText => L("Onboarding.Language.Persian");

    // ---- Fields ------------------------------------------------------------------

    private string _name;
    /// <summary>Written live to the session profile so nothing typed survives only until a crash.</summary>
    public string Name { get => _name; set { if (Set(ref _name, value)) _session.CurrentProfile.Name = value.Trim(); } }

    private int _activityIndex;
    public int ActivityIndex { get => _activityIndex; set { if (Set(ref _activityIndex, value)) _session.CurrentProfile.ActivityLevel = (ActivityLevel)value; } }
    public List<string> ActivityOptions => Enum.GetValues<ActivityLevel>().Select(a => L("Enum.ActivityLevel." + a)).ToList();

    private TimeSpan _bedtime;
    public TimeSpan Bedtime { get => _bedtime; set { if (Set(ref _bedtime, value)) _session.CurrentProfile.PreferredBedtime = value; } }

    private TimeSpan _wakeTime;
    public TimeSpan WakeTime { get => _wakeTime; set { if (Set(ref _wakeTime, value)) _session.CurrentProfile.PreferredWakeTime = value; } }

    // ---- Focus areas (the missing Wave 2 feature) ---------------------------------

    /// <summary>Machine tags in lane 08's exact vocabulary; persisted verbatim.</summary>
    public static readonly string[] AllFocusAreas = { "sleep", "energy", "fitness", "focus", "stress", "learning" };
    public const int MinFocusAreas = 1;
    public const int MaxFocusAreas = 3;

    /// <summary>Localized chip labels (never stored; stored values are the tags above).</summary>
    public static string GoalLabelKey(string area) => "Onboarding.Goal." + area switch
    {
        "sleep" => "BetterSleep",
        "energy" => "MoreEnergy",
        "fitness" => "Fitness",
        "focus" => "Focus",
        "stress" => "Stress",
        "learning" => "Learning",
        _ => "BetterSleep",
    };

    public List<string> SelectedAreas => _session.CurrentProfile.FocusAreas;

    /// <summary>One-row-per-chip list for the ItemsStackLayout template.</summary>
    public List<GoalChip> GoalChips { get; private set; } = new();

    /// <summary>What the user picked, rendered in the active language (summary chips + review).</summary>
    public string SelectedSummary => string.Join(" · ", SelectedAreas.Select(a => L(GoalLabelKey(a))));

    public void ToggleGoal(string area)
    {
        var list = SelectedAreas;
        if (list.Contains(area))
        {
            if (list.Count <= MinFocusAreas) { _validationKey = "Onboarding.Goals.Min"; RaiseValidation(); return; }
            list.Remove(area);
        }
        else
        {
            if (list.Count >= MaxFocusAreas) { _validationKey = "Onboarding.Goals.Max"; RaiseValidation(); return; }
            list.Add(area);
        }
        ClearValidation();
        RebuildGoalChips();
        Raise(nameof(SelectedSummary));
    }

    private void RebuildGoalChips()
    {
        GoalChips = AllFocusAreas.Select(area => new GoalChip
        {
            Area = area,
            Text = L(GoalLabelKey(area)),
            Selected = SelectedAreas.Contains(area),
            Enabled = SelectedAreas.Contains(area) || SelectedAreas.Count < MaxFocusAreas,
            Toggle = new Command(() => ToggleGoal(area)),
        }).ToList();
        Raise(nameof(GoalChips));
    }

    // ---- Language -----------------------------------------------------------------

    public bool IsEnglish => _loc.CurrentLanguage == AppLanguage.English;
    public bool IsPersian => _loc.CurrentLanguage == AppLanguage.Persian;

    public Color EnglishButtonBackground => SegmentLook.Background(IsEnglish);
    public Color EnglishButtonText => SegmentLook.Foreground(IsEnglish);
    public Color PersianButtonBackground => SegmentLook.Background(IsPersian);
    public Color PersianButtonText => SegmentLook.Foreground(IsPersian);

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

    private string _formatNumber(int n)
    {
        try { return ServiceHelper.TryGet<IFormatService>()?.Number(n) ?? n.ToString(); }
        catch { return n.ToString(); }
    }

    // ---- Finish -------------------------------------------------------------------

    private async Task FinishAsync()
    {
        var profile = _session.CurrentProfile;
        profile.Name = Name.Trim();
        profile.ActivityLevel = (ActivityLevel)ActivityIndex;
        profile.PreferredBedtime = Bedtime;
        profile.PreferredWakeTime = WakeTime;
        // FocusAreas was written live by ToggleGoal — machine tags, min 1 (enforced by the
        // step guard), max 3 (enforced by the chip itself). Persist the whole profile once here.
        if (profile.FocusAreas.Count == 0) profile.FocusAreas = new List<string> { "sleep" };
        profile.OnboardingCompleted = true;
        await _profileRepo.SaveAsync(profile);

        _settings.OnboardingCompleted = true;
        App.ApplyFlowDirection();
        Completed?.Invoke();
    }

    protected override void OnLanguageChanged()
    {
        foreach (var key in new[]
        {
            nameof(WelcomeTitle), nameof(WelcomeSubtitle), nameof(NamePrompt), nameof(NamePlaceholder),
            nameof(GoalsPrompt), nameof(GoalsHint), nameof(ActivityPrompt), nameof(SchedulePrompt),
            nameof(BedtimeLabel), nameof(WakeLabel), nameof(LanguagePrompt), nameof(FinishNote),
            nameof(NextText), nameof(BackText), nameof(EnglishText),
            nameof(PersianText), nameof(ActivityOptions), nameof(SelectedSummary), nameof(StepCounter),
        })
            Raise(key);
        if (HasValidationHint) RaiseValidation(); // hint text re-resolves from its stored key
        RebuildGoalChips();
        RaiseLanguageProps();
        RaiseStepFlags();
    }
}
