using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;

/// <summary>
/// Profile is the account hub of the product: who you are (initials avatar, name, focus areas,
/// activity level, sleep schedule), how the app looks and speaks (theme mode + live language),
/// what it is connected to (connection center), what it stores (per-file privacy inventory with a
/// delete-all that really reaches the Wave 3 stores), and the entries into Settings, reminders,
/// updates and the weekly review.
///
/// Honesty: every status is read from a service or from disk. Sample data says sample,
/// self-reported logs say self-reported, an absent capability says "not available in this build",
/// and a row is only tappable when it actually leads somewhere.
///
/// Wave 3 collaborators (lane 01 updates, lane 05 theme, lane 02 manual entries, lane 09
/// reminders) resolve lazily through <see cref="ServiceHelper.TryGet{T}"/> and are never assumed:
/// the constructor keeps the Wave 2 shape so the existing DI line works unchanged and the hub
/// builds and runs in any merge state.
/// </summary>
public sealed class ProfileViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly ISettingsService _settings;
    private readonly IFormatService _format;
    private readonly IRepository<UserProfile> _profileRepo;
    private readonly IPrivacyService _privacy;
    private readonly SessionState _session;
    private readonly LocalDataFiles _files;

    private IThemeService? _theme;
    private IReminderService? _reminders;
    private IManualEntryService? _manual;

    private IThemeService? ThemeSvc => _theme ??= ServiceHelper.TryGet<IThemeService>();
    private IReminderService? ReminderSvc => _reminders ??= ServiceHelper.TryGet<IReminderService>();
    private IManualEntryService? ManualSvc => _manual ??= ServiceHelper.TryGet<IManualEntryService>();

    /// <summary>Focus-area machine tags (lane 08's recommender reads exactly these — never text).</summary>
    public const int MaxFocusAreas = 3;
    private static readonly string[] AllFocusAreas = { "sleep", "energy", "fitness", "focus", "stress", "learning" };

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
        _privacy = privacy;
        // `permissions` stays in the signature (the Wave 2 DI line resolves it) but is unused:
        // this build prompts for no permission — the connection center says so instead.
        _session = session;
        _files = new LocalDataFiles(ServiceHelper.TryGet<JsonFileStore>() ?? new JsonFileStore());
        SubscribeLanguage();

        // Prefer the DI-registered banner (lane 01 registers it); the direct construction is the
        // standalone fallback so the hub works before that DI line lands (see NOTES: if lane 01's
        // banner has no parameterless ctor, drop the ?? fallback and rely on DI).
        Banner = ServiceHelper.TryGet<UpdateBannerViewModel>() ?? new UpdateBannerViewModel();

        SaveNameCommand = new Command(async () => await SaveProfileAsync());
        SetEnglishCommand = new Command(() => SetLanguage(AppLanguage.English));
        SetPersianCommand = new Command(() => SetLanguage(AppLanguage.Persian));
        DeleteAllDataCommand = new Command(async () => await DeleteAllDataAsync());
        OpenSettingsCommand = new Command(() => NavigateRequested?.Invoke("settings"));
        OpenRemindersCommand = new Command(() => NavigateRequested?.Invoke("reminders"));
        OpenUpdatesCommand = new Command(() => NavigateRequested?.Invoke("updates"));
        OpenLogEntryCommand = new Command(() => NavigateRequested?.Invoke("log-entry"));
        OpenWeeklyReviewCommand = new Command(() => WeeklyReviewRequested?.Invoke());
        SetThemeSystemCommand = new Command(() => SetTheme(ThemeMode.System));
        SetThemeLightCommand = new Command(() => SetTheme(ThemeMode.Light));
        SetThemeDarkCommand = new Command(() => SetTheme(ThemeMode.Dark));
    }

    /// <summary>Lane 01's banner VM, hosted by the updates card (§Lane 06).</summary>
    public UpdateBannerViewModel Banner { get; }

    /// <summary>The page performs the shell navigation (routes belong to other lanes).</summary>
    public event Action<string>? NavigateRequested;
    /// <summary>Weekly review is a modal page (the Wave 2 flow Today uses), not a shell route.</summary>
    public event Action? WeeklyReviewRequested;
    /// <summary>Raised when the user chooses to run onboarding again after a confirmed wipe.</summary>
    public event Action? RestartOnboardingRequested;

    public Command SaveNameCommand { get; }
    public Command SetEnglishCommand { get; }
    public Command SetPersianCommand { get; }
    public Command DeleteAllDataCommand { get; }
    public Command OpenSettingsCommand { get; }
    public Command OpenRemindersCommand { get; }
    public Command OpenUpdatesCommand { get; }
    public Command OpenLogEntryCommand { get; }
    public Command OpenWeeklyReviewCommand { get; }
    public Command SetThemeSystemCommand { get; }
    public Command SetThemeLightCommand { get; }
    public Command SetThemeDarkCommand { get; }

    // ---- Localized labels. Static XAML text uses {localize:Tr …}; these getters exist for
    // ---- computed strings, template rows and SemanticProperties.Description bindings.

    public string Title => L("Profile.Title");
    public string IdentityHeader => L("Profile.Identity");
    public string NameLabel => L("Profile.Name");
    public string FocusAreasLabel => L("Profile.PrimaryGoals");
    public string FocusAreasHint => L("Profile.PrimaryGoalsHint");
    public string ActivityLabel => L("Profile.ActivityLevel");
    public string ScheduleLabel => L("Profile.Schedule");
    public string BedtimeLabel => L("Onboarding.Bedtime");
    public string WakeLabel => L("Onboarding.WakeTime");
    public string LanguageLabel => L("Profile.Language");
    public string LanguageNote => L("Profile.LanguageNote");
    public string ThemeLabel => L("Profile.Appearance");
    public string ThemeNote => L("Profile.ThemeNote");
    public string ThemeSystemText => L("Theme.System");
    public string ThemeLightText => L("Theme.Light");
    public string ThemeDarkText => L("Theme.Dark");
    public string ThemeUnavailableNote => L("Profile.ThemeUnavailable");
    public string ConnectionCenterTitle => L("Profile.ConnectionCenter");
    public string ConnectionCenterHint => L("Profile.ConnectionCenterHint");
    public string SourcesNote => L("Profile.SourcesNote");
    public string PrivacyTitle => L("Privacy.Title");
    public string PrivacyNote => L("Privacy.Note");
    public string PrivacyFileHeader => L("Privacy.FileHeader");
    public string PrivacyRetainedNote => L("Privacy.RetainedNote");
    public string DeleteAllText => L("Privacy.DeleteAll");
    public string DeleteConfirmBody => L("Privacy.DeleteConfirm");
    public string SettingsEntryTitle => L("Profile.SettingsEntry");
    public string SettingsEntryNote => L("Profile.SettingsEntryNote");
    public string RemindersEntryTitle => L("Profile.Notifications");
    public string RemindersEntryNote => L("Profile.RemindersEntryNote");
    public string UpdatesEntryTitle => L("Profile.UpdatesEntry");
    public string UpdatesEntryNote => L("Profile.UpdatesEntryNote");
    public string WeeklyReviewTitle => L("Profile.WeeklyReview");
    public string WeeklyReviewNote => L("Profile.WeeklyReviewNote");
    public string AboutLabel => L("Profile.About");
    public string VersionLabel => L("Profile.Version", AppInfo.Current.VersionString);
    public string SaveText => L("Common.Save");
    public string EnglishText => L("Onboarding.Language.English");
    public string PersianText => L("Onboarding.Language.Persian");
    public string OpenText => L("Common.Open");
    public string NotAvailable => L("Common.NotAvailable");
    public string UnavailableMessage => L("Profile.NavUnavailable");
    /// <summary>"n of m files hold data" — a disk measurement, not a category count.</summary>
    public string PrivacyFileCount =>
        L("Privacy.FileCount", _format.Number(_files.StoredFileCount), _format.Number(_files.TotalFileCount));

    // ---- Identity --------------------------------------------------------------

    /// <summary>Initials avatar: one letter for a single name, two for a full name.</summary>
    public string Initials
    {
        get
        {
            var name = (Profile.Name ?? string.Empty).Trim();
            if (name.Length == 0) return L("Profile.InitialsPlaceholder");
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string First(string s) => char.ToUpperInvariant(s[0]).ToString();
            return parts.Length >= 2 ? First(parts[0]) + First(parts[1]) : First(parts[0]);
        }
    }

    private string _name = string.Empty;
    public string Name { get => _name; set => Set(ref _name, value); }

    public string FocusAreasText =>
        Profile.FocusAreas.Count == 0
            ? L("Profile.PrimaryGoalsEmpty")
            : string.Join(" · ", Profile.FocusAreas.Select(k => L("Onboarding.Goal." + ToGoalKey(k))));

    public List<GoalChip> GoalChips { get; private set; } = new();

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

    public List<string> ActivityOptions =>
        Enum.GetValues<ActivityLevel>().Select(a => L("Enum.ActivityLevel." + a)).ToList();

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
        set { Profile.PreferredBedtime = value; Raise(); Raise(nameof(SleepWindowText)); _ = SaveProfileAsync(); }
    }

    public TimeSpan WakeTime
    {
        get => Profile.PreferredWakeTime;
        set { Profile.PreferredWakeTime = value; Raise(); Raise(nameof(SleepWindowText)); _ = SaveProfileAsync(); }
    }

    /// <summary>"23:00 – 07:00" in the active locale's digits — the schedule read back to the user.</summary>
    public string SleepWindowText => $"{_format.Time(Profile.PreferredBedtime)} – {_format.Time(Profile.PreferredWakeTime)}";

    /// <summary>
    /// Toggle one focus area (machine tag). At least one must stay — the state and program
    /// engines read the set — and at most <see cref="MaxFocusAreas"/> can be picked; a refused tap
    /// explains itself inline instead of silently doing nothing.
    /// </summary>
    public void ToggleGoal(string area)
    {
        var list = Profile.FocusAreas ??= new List<string>();
        if (list.Contains(area))
        {
            if (list.Count <= 1) { RefuseTap("Profile.PrimaryGoalsMin"); return; }
            list.Remove(area);
        }
        else
        {
            if (list.Count >= MaxFocusAreas) { RefuseTap("Profile.PrimaryGoalsMax"); return; }
            list.Add(area);
        }
        ClearRefusal();
        RebuildGoalChips();
        Raise(nameof(FocusAreasText));
        _ = SaveProfileAsync();
    }

    // The refusal is stored as a KEY so a live language switch re-resolves its text — resolved
    // strings would freeze in the language the tap happened in.
    private string _focusLimitKey = string.Empty;
    public string FocusLimitHint => _focusLimitKey.Length > 0 ? L(_focusLimitKey) : string.Empty;
    public bool HasFocusHint => _focusLimitKey.Length > 0;
    private void RefuseTap(string key) { _focusLimitKey = key; Raise(nameof(FocusLimitHint)); Raise(nameof(HasFocusHint)); }
    private void ClearRefusal() { if (_focusLimitKey.Length > 0) { _focusLimitKey = string.Empty; Raise(nameof(FocusLimitHint)); Raise(nameof(HasFocusHint)); } }

    private void RebuildGoalChips()
    {
        var selected = Profile.FocusAreas ?? new List<string>();
        GoalChips = AllFocusAreas.Select(area => new GoalChip
        {
            Area = area,
            Text = L("Onboarding.Goal." + ToGoalKey(area)),
            Selected = selected.Contains(area),
            Enabled = selected.Contains(area) || selected.Count < MaxFocusAreas,
            Toggle = new Command(() => ToggleGoal(area)),
        }).ToList();
        Raise(nameof(GoalChips));
    }

    // ---- Language (live + persisted) -------------------------------------------

    public bool IsEnglish => _loc.CurrentLanguage == AppLanguage.English;
    public bool IsPersian => _loc.CurrentLanguage == AppLanguage.Persian;

    // Segmented control: active segment filled with the brand accent, inactive on the overlay
    // surface. Both are Colors — Button.BackgroundColor/TextColor must never receive a brush.
    public Color EnglishButtonBackground => SegmentLook.Background(IsEnglish);
    public Color EnglishButtonText => SegmentLook.Foreground(IsEnglish);
    public Color PersianButtonBackground => SegmentLook.Background(IsPersian);
    public Color PersianButtonText => SegmentLook.Foreground(IsPersian);

    // ---- Theme (lane 05's service; when it is absent the card says so) ---------

    public bool HasThemeControl => ThemeSvc is not null;
    public bool IsThemeSystem => ThemeSvc?.Mode == ThemeMode.System;
    public bool IsThemeLight => ThemeSvc?.Mode == ThemeMode.Light;
    public bool IsThemeDark => ThemeSvc?.Mode == ThemeMode.Dark;
    public string CurrentThemeText => ThemeSvc?.Mode switch
    {
        ThemeMode.Light => L("Theme.Light"),
        ThemeMode.Dark => L("Theme.Dark"),
        ThemeMode.System => L("Theme.System"),
        _ => L("Common.NotAvailable"),
    };

    // Theme segment colors (Color-typed only — never brushes on Button.BackgroundColor/TextColor).
    public Color ThemeSystemBackground => SegmentLook.Background(IsThemeSystem);
    public Color ThemeSystemForeground => SegmentLook.Foreground(IsThemeSystem);
    public Color ThemeLightBackground => SegmentLook.Background(IsThemeLight);
    public Color ThemeLightForeground => SegmentLook.Foreground(IsThemeLight);
    public Color ThemeDarkBackground => SegmentLook.Background(IsThemeDark);
    public Color ThemeDarkForeground => SegmentLook.Foreground(IsThemeDark);

    private void SetTheme(ThemeMode mode)
    {
        var svc = ThemeSvc;
        if (svc is null) return;
        svc.SetMode(mode);
        _settings.ThemeMode = mode; // the persisted choice must survive a restart either way
        Raise(nameof(HasThemeControl));
        Raise(nameof(IsThemeSystem));
        Raise(nameof(IsThemeLight));
        Raise(nameof(IsThemeDark));
        Raise(nameof(CurrentThemeText));
        Raise(nameof(ThemeSystemBackground));
        Raise(nameof(ThemeSystemForeground));
        Raise(nameof(ThemeLightBackground));
        Raise(nameof(ThemeLightForeground));
        Raise(nameof(ThemeDarkBackground));
        Raise(nameof(ThemeDarkForeground));
    }

    // ---- Capability rows (measured, never assumed) ------------------------------

    public bool HasReminderService => ReminderSvc is not null;
    public bool HasManualEntryService => ManualSvc is not null;
    public string ReminderGrantText { get; private set; } = string.Empty;
    public Color ReminderGrantColor { get; private set; } = Theme.TextSecondary;

    public List<ConnectionRow> ConnectionCenter { get; private set; } = new();
    public List<PrivacyRowViewModel> PrivacyRows { get; private set; } = new();

    private UserProfile Profile => _session.CurrentProfile;

    // ---- Load -------------------------------------------------------------------

    public async Task LoadAsync()
    {
        Name = Profile.Name;
        RebuildGoalChips();
        // The banner first: the connection-center's updates row quotes its state, so building
        // the lists before the peek would freeze a stale "haven't checked yet" line.
        await Banner.LoadAsync();
        await LoadListsAsync();
        RaiseAll();
    }

    private async Task LoadListsAsync()
    {
        // --- self-reported log: the only real user data source in this build.
        int manualCount = 0;
        bool manualProbeFailed = false;
        var manualSvc = ManualSvc;
        if (manualSvc is not null)
        {
            try { manualCount = await manualSvc.CountEntriesAsync(); }
            catch { manualProbeFailed = true; }
        }
        string manualStatus = manualSvc is null
            ? NotAvailable
            : manualProbeFailed
                ? L("Profile.Status.CheckFailed")
                : manualCount > 0
                    ? L("Profile.Status.SelfReportedCount", _format.Number(manualCount))
                    : L("Profile.Status.SelfReportedNone");
        Color manualDot = manualSvc is not null && !manualProbeFailed && manualCount > 0
            ? Theme.Positive : Theme.TextSecondary;

        ConnectionCenter = new List<ConnectionRow>
        {
            new()
            {
                Name = L("Profile.Source.Health"),
                Status = L("Profile.Status.MockActive"),
                DotColor = Theme.Caution,
            },
            new()
            {
                Name = L("Profile.Source.Manual"),
                Status = manualStatus,
                DotColor = manualDot,
                ActionText = manualSvc is null ? string.Empty : L("Profile.AddYourData"),
                Action = manualSvc is null ? null : OpenLogEntryCommand,
            },
            new()
            {
                Name = L("Profile.Source.Updates"),
                Status = Banner.IsFeedConfigured ? Banner.Headline : L("Profile.Update.NotConfigured"),
                DotColor = Banner.Status switch
                {
                    UpdateCheckStatus.UpdateAvailable => Theme.Caution,
                    UpdateCheckStatus.UpToDate => Theme.Positive,
                    _ => Theme.TextSecondary,
                },
                ActionText = Banner.IsFeedConfigured ? OpenText : string.Empty,
                Action = Banner.IsFeedConfigured ? OpenUpdatesCommand : null,
            },
            new() { Name = L("Profile.Source.Activity"), Status = L("Profile.Status.NotConnected"), DotColor = Theme.TextSecondary },
            new() { Name = L("Profile.Source.Calendar"), Status = L("Profile.Status.NotConnected"), DotColor = Theme.TextSecondary },
            new() { Name = L("Profile.Source.Tasks"), Status = L("Profile.Status.NotConnected"), DotColor = Theme.TextSecondary },
            new() { Name = L("Profile.Source.ScreenTime"), Status = L("Profile.Status.NotConnected"), DotColor = Theme.TextSecondary },
        };
        Raise(nameof(ConnectionCenter));

        // --- privacy inventory: the Wave 2 categories (kept working), each with a measured
        // presence where its backing file is known, extended by the four Wave 3 stores.
        var rows = new List<PrivacyRowViewModel>();
        try
        {
            foreach (var c in await _privacy.DescribeStoredDataAsync())
            {
                var presence = _files.PresenceForCategory(c.Key);
                rows.Add(new PrivacyRowViewModel
                {
                    Name = L(c.Key),
                    OriginText = c.Origin == DataOrigin.Manual ? L("Privacy.Origin.Manual") : L("Privacy.Origin.Mock"),
                    LocationText = L(c.StorageLocationKey),
                    PresenceText = presence is null ? string.Empty : L(presence.Value ? "Privacy.Presence.Stored" : "Privacy.Presence.Empty"),
                    Exists = presence ?? true,
                    ShowPresence = presence is not null,
                    FileNameHint = _files.CategoryFilesHint(c.Key),
                });
            }
        }
        catch
        {
            // A failed category read must not hide the section: the store rows below still
            // report exactly what is on disk.
        }

        foreach (var s in _files.DescribeStores())
        {
            rows.Add(new PrivacyRowViewModel
            {
                Name = L(s.NameKey),
                OriginText = L(s.Source switch
                {
                    LocalDataFiles.Provenance.UserEntered => "Privacy.Origin.Manual",
                    LocalDataFiles.Provenance.Sample => "Privacy.Origin.Mock",
                    _ => "Privacy.Origin.AppGenerated",
                }),
                LocationText = L("Privacy.Location.Device"),
                PresenceText = s.Exists && s.Count is > 0
                    ? L("Privacy.Presence.StoredCount", _format.Number(s.Count.Value))
                    : L(s.Exists ? "Privacy.Presence.Stored" : "Privacy.Presence.Empty"),
                Exists = s.Exists,
                ShowPresence = true,
                FileNameHint = s.FileName,
            });
        }
        PrivacyRows = rows;
        Raise(nameof(PrivacyRows));
        Raise(nameof(PrivacyFileCount));

        // --- notification grant: whatever the OS told lane 09, stated verbatim.
        var rem = ReminderSvc;
        if (rem is null)
        {
            ReminderGrantText = NotAvailable;
            ReminderGrantColor = Theme.TextSecondary;
        }
        else
        {
            try
            {
                var state = await rem.GetGrantStateAsync();
                ReminderGrantText = L(NotificationGrantDisplay.KeyFor(state));
                ReminderGrantColor = NotificationGrantDisplay.ColorFor(state);
            }
            catch
            {
                ReminderGrantText = L("Profile.Status.CheckFailed");
                ReminderGrantColor = Theme.Caution;
            }
        }
    }

    private void RaiseAll()
    {
        Raise(nameof(Name));
        Raise(nameof(Initials));
        Raise(nameof(FocusAreasText));
        Raise(nameof(GoalChips));
        Raise(nameof(FocusLimitHint));
        Raise(nameof(HasFocusHint));
        Raise(nameof(ActivityOptions));
        Raise(nameof(ActivityIndex));
        Raise(nameof(Bedtime));
        Raise(nameof(WakeTime));
        Raise(nameof(SleepWindowText));
        Raise(nameof(ConnectionCenter));
        Raise(nameof(PrivacyRows));
        Raise(nameof(PrivacyFileCount));
        Raise(nameof(IsEnglish));
        Raise(nameof(IsPersian));
        Raise(nameof(HasThemeControl));
        Raise(nameof(IsThemeSystem));
        Raise(nameof(IsThemeLight));
        Raise(nameof(IsThemeDark));
        Raise(nameof(CurrentThemeText));
        Raise(nameof(ThemeSystemBackground));
        Raise(nameof(ThemeSystemForeground));
        Raise(nameof(ThemeLightBackground));
        Raise(nameof(ThemeLightForeground));
        Raise(nameof(ThemeDarkBackground));
        Raise(nameof(ThemeDarkForeground));
        Raise(nameof(HasReminderService));
        Raise(nameof(HasManualEntryService));
        Raise(nameof(ReminderGrantText));
        Raise(nameof(ReminderGrantColor));
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
        Raise(nameof(Initials));
        Raise(nameof(FocusAreasText));
    }

    private void SetLanguage(AppLanguage lang)
    {
        _loc.SetLanguage(lang);
        App.ApplyFlowDirection();
    }

    // ---- Delete all local data ---------------------------------------------------

    private async Task DeleteAllDataAsync()
    {
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;

        // One confirmation that says exactly what goes — including the Wave 3 stores, so the words
        // "all local data" are true rather than approximate.
        bool confirm;
        try
        {
            confirm = await page.DisplayAlertAsync(L("Privacy.DeleteAll"), L("Privacy.DeleteConfirm"),
                L("Common.Yes"), L("Common.No"));
        }
        catch { return; }
        if (!confirm) return;

        IsBusy = true;
        try
        {
            await _privacy.DeleteAllLocalDataAsync();          // the six core files
            await _files.WipeResidualStoresAsync();            // the four Wave 3 stores
            // The onboarding flag flips ONLY if the user chooses to set up again below — they
            // may equally keep using the emptied app, and a flag cleared behind that "No"
            // would send them through setup a second time against their answer.

            // Reset what the hub shows from memory (the files are gone; the session must not keep
            // pretending otherwise). The fresh profile is deliberately NOT written back: the
            // profile file stays absent until the user edits something, so "nothing stored yet"
            // stays honest. Sample goals/habits are not re-seeded either — see NOTES.
            var fresh = new UserProfile { Id = Profile.Id };
            _session.CurrentProfile = fresh;
            // The banner needs no explicit reset: LoadAsync() below re-peeks the cache, and
            // update_feed.json is gone with the wipe, so it honestly falls back to "not checked".
            await LoadAsync();

            // Feedback the wipe always owed the user, then the honest choice: set up again or
            // continue in an empty app. Nothing is silently repopulated.
            string done = L("Privacy.Deleted") + "\n" + L("Privacy.WipeSampleNote");
            bool redo;
            try { redo = await page.DisplayAlertAsync(L("Privacy.Title"), done, L("Privacy.RedoYes"), L("Privacy.RedoNo")); }
            catch { redo = false; }
            if (redo)
            {
                _settings.OnboardingCompleted = false;
                RestartOnboardingRequested?.Invoke();
            }
        }
        catch
        {
            try { await page.DisplayAlertAsync(L("Privacy.Title"), L("Privacy.DeleteFailed"), L("Common.Done")); }
            catch { /* a failed dialog must never escalate */ }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool _isBusy;
    /// <summary>Drives <c>BaseContentPage.IsLoading</c> (dim + spinner + input lock) during the wipe.</summary>
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); Raise(nameof(IsNotBusy)); } }
    public bool IsNotBusy => !_isBusy;

    // ---- Language rebuild ---------------------------------------------------------

    /// <summary>Every localized getter in one list — a language switch cannot miss a label.</summary>
    private static readonly string[] LocalizedProperties =
    {
        nameof(Title), nameof(IdentityHeader), nameof(NameLabel), nameof(FocusAreasLabel),
        nameof(FocusAreasHint), nameof(ActivityLabel), nameof(ScheduleLabel), nameof(BedtimeLabel),
        nameof(WakeLabel), nameof(LanguageLabel), nameof(LanguageNote), nameof(ThemeLabel),
        nameof(ThemeNote), nameof(ThemeSystemText), nameof(ThemeLightText), nameof(ThemeDarkText),
        nameof(ThemeUnavailableNote), nameof(ConnectionCenterTitle), nameof(ConnectionCenterHint),
        nameof(SourcesNote), nameof(PrivacyTitle), nameof(PrivacyNote), nameof(PrivacyFileHeader),
        nameof(PrivacyRetainedNote), nameof(DeleteAllText), nameof(DeleteConfirmBody),
        nameof(SettingsEntryTitle), nameof(SettingsEntryNote), nameof(RemindersEntryTitle),
        nameof(RemindersEntryNote), nameof(UpdatesEntryTitle), nameof(UpdatesEntryNote),
        nameof(WeeklyReviewTitle), nameof(WeeklyReviewNote), nameof(AboutLabel), nameof(VersionLabel),
        nameof(SaveText), nameof(EnglishText), nameof(PersianText), nameof(OpenText),
        nameof(NotAvailable), nameof(UnavailableMessage), nameof(FocusAreasText),
        nameof(SleepWindowText), nameof(CurrentThemeText), nameof(ReminderGrantText),
        nameof(Initials), nameof(FocusLimitHint),
    };

    protected override void OnLanguageChanged()
    {
        foreach (var key in LocalizedProperties) Raise(key);
        Raise(nameof(PrivacyFileCount));
        RebuildGoalChips();
        RaiseLanguageButtons();
        _ = LoadAsync();
    }
}
