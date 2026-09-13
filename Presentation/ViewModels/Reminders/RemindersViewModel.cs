using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Reminders;
using LIVORA.Domain.Enums;

namespace LIVORA.Presentation;

/// <summary>One row in the reminders list: a persisted <see cref="ReminderSetting"/> shaped for the
/// page. Writes go straight back through <see cref="IReminderService"/> so a switch/tap is durable
/// and reaches the OS scheduler immediately.</summary>
public sealed class ReminderRowViewModel : ObservableObject
{
    private readonly IReminderService _service;
    private readonly IFormatService _format;
    private ReminderSetting _setting;
    private bool _saveInFlight;

    public ReminderRowViewModel(IReminderService service, ReminderSetting setting, IFormatService format)
    {
        _service = service;
        _format = format;
        _setting = setting;
        // The Switch binds IsToggled TwoWay (and the TimePicker binds Time TwoWay): the property
        // setters below are the ONLY write path, each persisting once per real change. No
        // extra Command is attached — a Switch with both Command and TwoWay IsToggled writes twice.
    }

    /// <summary>Build the seven weekday chips (index 0 = Sunday, matching DaysMask bit order) and
    /// wire each to this row so XAML binds only to the chip itself — no x:Reference in templates.</summary>
    public void BuildDayChips()
    {
        DayLabels.Clear();
        for (int i = 0; i < 7; i++)
        {
            int index = i;
            DayLabels.Add(new WeekdayChipViewModel
            {
                Index = index,
                IsOn = (DaysMask & (1 << index)) != 0,
                Toggle = new Command(async () => await ToggleDayAsync(index)),
            });
        }
    }

    public ReminderSetting Setting => _setting;

    public string Kind => _setting.Kind;
    public string Title => L("Reminders.Kind." + _setting.Kind);
    public string Description => L("Reminders.Describe." + _setting.Kind);

    public bool Enabled
    {
        get => _setting.Enabled;
        set
        {
            // TwoWay Switch binding lands here. Save only when the value actually moved, so a
            // programmatic re-raise from Apply() cannot write back to the service.
            if (_setting.Enabled == value) return;
            _setting = _setting with { Enabled = value };
            Raise();
            _ = SaveSafeAsync();
        }
    }

    public TimeSpan Time
    {
        get => _setting.TimeOfDay;
        set
        {
            if (_setting.TimeOfDay == value) return;
            _setting = _setting with { TimeOfDay = value };
            Raise();
            Raise(nameof(TimeText));
            _ = SaveSafeAsync();   // the TimePicker writes here (TwoWay) — persist exactly once
        }
    }

    /// <summary>Locale digits (۲۱:۱۵ in Persian) — same formatter the rest of the app uses.</summary>
    public string TimeText => _format.Time(Time);

    public int DaysMask
    {
        get => _setting.DaysMask;
        private set { _setting = _setting with { DaysMask = value }; Raise(); }
    }

    /// <summary>Inline write failure (never silent).</summary>
    private string _saveError = string.Empty;
    public string SaveError { get => _saveError; private set => Set(ref _saveError, value); }

    /// <summary>Seven weekday chips (index 0 = Sunday, matching DaysMask bit order).</summary>
    public ObservableCollection<WeekdayChipViewModel> DayLabels { get; } = new();

    private async Task ToggleDayAsync(int dow)
    {
        if (dow is < 0 or > 6) return;
        int mask = DaysMask ^ (1 << dow);
        // An all-off week is a reminder that can never fire — worse than none at all, so the
        // switch (not the chips) remains the control that turns everything off.
        if (mask == 0) return;
        DaysMask = mask;
        foreach (var chip in DayLabels) chip.IsOn = (mask & (1 << chip.Index)) != 0;
        await SaveSafeAsync();
    }

    /// <summary>Replace the backing setting after a service reload without triggering a write.</summary>
    public void Apply(ReminderSetting fresh)
    {
        _setting = fresh;
        SaveError = string.Empty;
        Raise(nameof(Enabled));
        Raise(nameof(Time));
        Raise(nameof(TimeText));
        Raise(nameof(DaysMask));
        Raise(nameof(Title));
        Raise(nameof(Description));
        foreach (var chip in DayLabels) chip.IsOn = (fresh.DaysMask & (1 << chip.Index)) != 0;
    }

    private async Task SaveSafeAsync()
    {
        if (_saveInFlight) return;   // coalesce a rapid toggle+chip burst into one scheduler sync
        _saveInFlight = true;
        try
        {
            await _service.SaveAsync(_setting);
            SaveError = string.Empty;
        }
        catch
        {
            // A failed write must be visible, not swallowed: reminders that silently don't persist
            // are worse than an error line.
            SaveError = L("Reminders.SaveFailed");
        }
        finally { _saveInFlight = false; }
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(Description));
        Raise(nameof(TimeText));
    }
}

/// <summary>One weekday chip: Sunday..Saturday short label (localized key per index) + on/off
/// state; its Toggle command is built by the owning row so taps persist through IReminderService.</summary>
public sealed class WeekdayChipViewModel : ObservableObject
{
    public required int Index { get; init; }
    public string Label => L("Reminders.Weekday.Short." + Index);

    /// <summary>Tap handler owned by this chip (built by the row) — XAML binds it directly.</summary>
    public ICommand? Toggle { get; set; }

    private bool _isOn;
    public bool IsOn { get => _isOn; set => Set(ref _isOn, value); }

    protected override void OnLanguageChanged() => Raise(nameof(Label));
}

/// <summary>
/// Reminders screen (lane 09): the four built-in reminder kinds as real controls (switch,
/// TimePicker, weekday chips) plus the OS grant banner and an honest "test reminder (+1 min)".
/// Everything the user flips is persisted and pushed to the platform scheduler immediately, and
/// the banner reports EXACTLY what the platform answered — never an assumed "allowed".
/// </summary>
public sealed class RemindersViewModel : ObservableObject
{
    private readonly IReminderService _service;
    private readonly IReminderEvaluator _evaluator;
    private readonly IFormatService _format;
    private readonly IDateTimeProvider _clock;

    public RemindersViewModel(
        IReminderService service,
        IReminderEvaluator evaluator,
        IFormatService format,
        IDateTimeProvider clock)
    {
        _service = service;
        _evaluator = evaluator;
        _format = format;
        _clock = clock;
        SubscribeLanguage();
        RequestGrantCommand = new Command(async () => await RequestGrantAsync());
        OpenSystemSettingsCommand = new Command(() =>
        {
            try { AppInfo.ShowSettingsUI(); }
            catch { GrantDetail = L("Reminders.OpenSettingsFailed"); }
        });
        TestReminderCommand = new Command(async () => await TestReminderAsync());
        RefreshCommand = new Command(async () => await LoadAsync());
    }

    public ObservableCollection<ReminderRowViewModel> Rows { get; } = new();
    public ObservableCollection<PreviewItemViewModel> Preview { get; } = new();

    // ---- header labels ----
    public string TitleText => L("Reminders.Title");
    public string SubtitleText => L("Reminders.Subtitle");
    public string GrantTitle => L("Notification.Grant.Title");
    public string TestButton => L("Reminders.Test.Button");
    public string PreviewHeader => L("Reminders.Preview.Header");
    public string PreviewEmpty => L("Reminders.Preview.Empty");
    public string HoursHint => L("Reminders.HoursHint");
    public string ScheduleSection => L("Reminders.Schedule");

    // ---- grant state (exactly what the platform said) ----
    private NotificationGrantState _grant = NotificationGrantState.Unknown;
    public NotificationGrantState Grant
    {
        get => _grant;
        private set
        {
            if (!Set(ref _grant, value)) return;
            Raise(nameof(GrantText));
            Raise(nameof(GrantIsAllowed));
            Raise(nameof(CanRequestGrant));
            Raise(nameof(CanOpenSettings));
        }
    }
    public string GrantText => L("Notification.Grant." + Grant);
    public bool GrantIsAllowed => Grant == NotificationGrantState.Allowed;
    /// <summary>Ask the OS now: only while a prompt is still possible on this platform.</summary>
    public bool CanRequestGrant => Grant is NotificationGrantState.NotRequested or NotificationGrantState.Unknown;
    /// <summary>Denied (or silent-Unknown): the only door left is the system settings page.</summary>
    public bool CanOpenSettings => Grant is NotificationGrantState.Denied or NotificationGrantState.Unknown;

    private string _grantDetail = string.Empty;
    public string GrantDetail { get => _grantDetail; private set { Set(ref _grantDetail, value); Raise(nameof(HasGrantDetail)); } }
    public bool HasGrantDetail => GrantDetail.Length > 0;

    // ---- test reminder result (what actually happened, nothing more) ----
    private string _testResult = string.Empty;
    public string TestResult { get => _testResult; private set { Set(ref _testResult, value); Raise(nameof(HasTestResult)); } }
    public bool HasTestResult => TestResult.Length > 0;

    public ICommand RequestGrantCommand { get; }
    public ICommand OpenSystemSettingsCommand { get; }
    public ICommand TestReminderCommand { get; }
    public ICommand RefreshCommand { get; }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    private bool _loadFailed;
    public bool LoadFailed { get => _loadFailed; private set => Set(ref _loadFailed, value); }
    public string LoadFailedText => L("Reminders.LoadFailed");

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var settings = await _service.GetRemindersAsync();
            Grant = await _service.GetGrantStateAsync();

            // Update rows in place when the kind set is unchanged (common after a save) so the
            // TimePicker/Switch visuals don't flicker or resettle mid-edit.
            bool sameShape = Rows.Count == settings.Count;
            for (int i = 0; sameShape && i < Rows.Count; i++)
                if (Rows[i].Kind != settings[i].Kind) sameShape = false;
            if (sameShape)
            {
                for (int i = 0; i < Rows.Count; i++) Rows[i].Apply(settings[i]);
            }
            else
            {
                Rows.Clear();
                foreach (var s in settings)
                {
                    var row = new ReminderRowViewModel(_service, s, _format);
                    row.BuildDayChips();
                    Rows.Add(row);
                }
            }

            // Visiting this page is the explicit user moment to push the schedule to the OS
            // (SyncAsync is idempotent and re-syncs after every Save too). Only while allowed:
            // scheduling behind a denied grant would contradict the banner right above.
            if (Grant == NotificationGrantState.Allowed)
            {
                await _service.SyncAsync();
                Grant = await _service.GetGrantStateAsync();   // re-read truth, don't assume
            }

            Preview.Clear();
            var candidates = await _evaluator.EvaluateNowAsync();
            foreach (var c in candidates)
                Preview.Add(new PreviewItemViewModel
                {
                    KindText = L("Reminders.Kind." + c.Kind),
                    BodyText = L(c.TextKey, c.TextArgs),
                    AtText = _format.Time(c.FireAt.TimeOfDay) + " · " + _format.ShortDate(c.FireAt.Date),
                });
            Raise(nameof(HasPreview));
            LoadFailed = false;
        }
        catch
        {
            LoadFailed = true;   // an honest failure line, not an empty page pretending nothing happened
        }
        finally { IsBusy = false; }
    }

    public bool HasPreview => Preview.Count > 0;

    private async Task RequestGrantAsync()
    {
        Grant = await _service.RequestGrantAsync();
        // What the platform answered, phrased by state — never more than the state implies.
        GrantDetail = Grant switch
        {
            NotificationGrantState.Allowed => L("Reminders.Grant.Result.Allowed"),
            NotificationGrantState.Denied => L("Reminders.Grant.Result.Denied"),
            NotificationGrantState.NotRequested => L("Reminders.Grant.Result.NotAsked"),
            NotificationGrantState.SystemManaged => L("Reminders.Grant.Result.SystemManaged"),
            NotificationGrantState.Unsupported => L("Reminders.Grant.Result.Unsupported"),
            _ => L("Reminders.Grant.Result.Unknown"),
        };
        await LoadAsync();
    }

    private async Task TestReminderAsync()
    {
        // +1 minute. "true" from the service means "handed to the OS scheduler" — the line says
        // exactly that and nothing about what the user will actually see (honesty rule).
        TestResult = string.Empty;
        var ok = await _service.ScheduleTestAsync(TimeSpan.FromMinutes(1), "Notification.Test.Body");
        var dueAt = (_clock.Now + TimeSpan.FromMinutes(1)).TimeOfDay;
        TestResult = ok
            ? L("Reminders.Test.Scheduled", _format.Time(dueAt))
            : L("Reminders.Test.Failed", GrantText);
    }

    protected override void OnLanguageChanged()
    {
        foreach (var r in Rows) r.RefreshLocalized();
        foreach (var d in Rows.SelectMany(r => r.DayLabels)) d.RefreshLocalized();
        Raise(nameof(TitleText)); Raise(nameof(SubtitleText)); Raise(nameof(GrantTitle));
        Raise(nameof(TestButton)); Raise(nameof(PreviewHeader)); Raise(nameof(PreviewEmpty));
        Raise(nameof(HoursHint)); Raise(nameof(ScheduleSection));
        Raise(nameof(GrantText)); Raise(nameof(GrantDetail)); Raise(nameof(TestResult));
        Raise(nameof(LoadFailedText));
        _ = LoadAsync();
    }
}

/// <summary>Row of the "what would notify you right now" preview list.</summary>
public sealed class PreviewItemViewModel
{
    public required string KindText { get; init; }
    public required string BodyText { get; init; }
    public required string AtText { get; init; }
}
