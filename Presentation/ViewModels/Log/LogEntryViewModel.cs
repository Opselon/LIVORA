using System.Globalization;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData;

namespace LIVORA.Presentation;

// =============================================================================
// Lane 03 — the daily check-in editor VM. Shared verbatim by the Log tab (hosted
// inside CheckInView) and the pushed log-entry route (LogEntryPage), so both
// flows are one implementation. Validation/clamping lives in
// Application/HealthData/LogEntryRules.cs; this class holds editor state,
// localizes, and talks to IManualEntryService — nothing else.
//
// Honesty model: a blank field means "not provided" (null), never zero. A slider
// with no value shows "not set" and parks its thumb mid-track; touching it is the
// moment a self-report exists.
// =============================================================================

/// <summary>One-day manual check-in: date chips + picker, sleep/steps/active fields, 0..1 sliders, note, save/delete.</summary>
public sealed class LogEntryViewModel : ObservableObject, IQueryAttributable
{
    private readonly IManualEntryService _entries;
    private readonly IFormatService _format;
    private readonly IDateTimeProvider _clock;

    private bool _loadingDay;      // re-entrancy guard for the date-change → load path
    private bool _hasStoredEntry;  // does the selected day already have a saved manual entry?

    public LogEntryViewModel(IManualEntryService entries, IFormatService format, IDateTimeProvider clock)
    {
        _entries = entries;
        _format = format;
        _clock = clock;
        _date = clock.Today;
        SubscribeLanguage();

        SelectTodayCommand = new Command(() => SelectDate(_clock.Today));
        SelectYesterdayCommand = new Command(() => SelectDate(_clock.Today.AddDays(-1)));
        SaveCommand = new Command(async () => await SaveAsync());
        DeleteCommand = new Command(async () => await DeleteAsync());
        // Pushed-flow chrome only: the log-entry page wires it, the Log tab never shows it.
        CloseCommand = new Command(() => CloseRequested?.Invoke());
        ClearSleepQualityCommand = new Command(() => { SleepQuality = null; Raise(nameof(SleepQualitySlider)); });
        ClearMoodCommand = new Command(() => { Mood = null; Raise(nameof(MoodSlider)); });
        ClearEnergyCommand = new Command(() => { Energy = null; Raise(nameof(EnergySlider)); });
        ClearStressCommand = new Command(() => { Stress = null; Raise(nameof(StressSlider)); });
    }

    /// <summary>Raised after a successful save or delete so hosts (Log tab) can refresh list + chart.</summary>
    public event Action? EntrySaved;

    /// <summary>Raised by the pushed log-entry page's close button (the tab host never wires it).</summary>
    public event Action? CloseRequested;

    /// <summary>Close-button label, resolved through the shared Log string owner.</summary>
    public string CloseText => LogUiStrings.BackToLog;

    public ICommand SelectTodayCommand { get; }
    public ICommand SelectYesterdayCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ClearSleepQualityCommand { get; }
    public ICommand ClearMoodCommand { get; }
    public ICommand ClearEnergyCommand { get; }
    public ICommand ClearStressCommand { get; }

    // ---- Static labels (re-raised on language change) -------------------------

    // Every wording below is resolved by LogUiStrings — the ONE place the Log feature's
    // sentences live, so the inline tab editor and the pushed editor can never drift apart.

    public string DateLabel => LogUiStrings.DateLabel;
    public string TodayText => LogUiStrings.DayLabel(0, _format);
    public string YesterdayText => LogUiStrings.DayLabel(1, _format);
    public string SleepSection => LogUiStrings.SectionSleep;
    public string ActivitySection => LogUiStrings.SectionActivity;
    public string FeelingsSection => LogUiStrings.SectionFeelings;
    public string HoursLabel => LogUiStrings.Hours;
    public string MinutesLabel => LogUiStrings.Minutes;
    public string StepsLabel => LogUiStrings.Steps;
    public string ActiveLabel => LogUiStrings.ActiveMinutes;
    public string SleepQualityLabel => LogUiStrings.SleepQuality;
    public string MoodLabel => LogUiStrings.Mood;
    public string EnergyLabel => LogUiStrings.Energy;
    public string StressLabel => LogUiStrings.Stress;
    public string NoteLabel => LogUiStrings.NoteLabel;
    public string OptionalHint => LogUiStrings.OptionalHint;
    public string HonestyNote => LogUiStrings.HonestyNote;
    public string SaveText => LogUiStrings.Save;
    public string DeleteText => LogUiStrings.DeleteEntry;
    public string ClearText => LogUiStrings.Clear;
    public string MinutesExampleHint => LogUiStrings.HintMinutes;
    public string SleepExampleHint => LogUiStrings.HintSleep;
    public string StepsExampleHint => LogUiStrings.HintSteps;
    public string ActiveExampleHint => LogUiStrings.HintActive;
    /// <summary>Heading of the pushed one-day page (the tab's own heading is a sibling key).</summary>
    public string CheckInTitle => LogUiStrings.CheckInTitle(LogHeadingStyle.Page);
    /// <summary>Long localized date of the selected day (Jalali + Persian digits in fa).</summary>
    public string SelectedDateText => _format.LongDate(_date);

    // ---- Date selection ---------------------------------------------------------

    private int _selectedDay;
    /// <summary>0 = today, 1 = yesterday, -1 = some other day (picked in the calendar).</summary>
    public int SelectedDay { get => _selectedDay; private set => Set(ref _selectedDay, value); }

    public bool IsTodaySelected => SelectedDay == 0;
    public bool IsYesterdaySelected => SelectedDay == 1;
    public bool IsCustomDateSelected => SelectedDay < 0;

    private DateTime _date;
    /// <summary>The day being edited (clamped to the loggable window on every write).</summary>
    public DateTime Date
    {
        get => _date;
        set => SelectDate(value);
    }

    /// <summary>
    /// Public date entry point (row tap-to-edit, Today/Yesterday chips, the DatePicker, the
    /// route's ?date= arg all funnel here): clamp, mirror chips, reload the day.
    /// </summary>
    public void SelectDate(DateTime value)
    {
        var day = LogEntryRules.ClampDate(value, _clock.Today);
        bool changed = Set(ref _date, day);
        int chip = day == _clock.Today ? 0 : day == _clock.Today.AddDays(-1) ? 1 : -1;
        bool chipChanged = Set(ref _selectedDay, chip);
        if (chipChanged)
        {
            Raise(nameof(IsTodaySelected));
            Raise(nameof(IsYesterdaySelected));
            Raise(nameof(IsCustomDateSelected));
        }
        if (changed || chipChanged)
        {
            Raise(nameof(SelectedDateText));
            _ = LoadForDateAsync(day);
        }
    }

    public DateTime MinDate => _clock.Today.AddDays(-LogEntryLimits.MaxAgeDays);
    public DateTime MaxDate => _clock.Today;

    // ---- Numeric fields (text = user input; blank = "not provided", never zero) --

    private string _sleepHoursText = string.Empty;
    public string SleepHoursText { get => _sleepHoursText; set => Set(ref _sleepHoursText, value); }

    private string _sleepMinutesText = string.Empty;
    public string SleepMinutesText { get => _sleepMinutesText; set => Set(ref _sleepMinutesText, value); }

    private string _stepsText = string.Empty;
    public string StepsText { get => _stepsText; set => Set(ref _stepsText, value); }

    private string _activeText = string.Empty;
    public string ActiveText { get => _activeText; set => Set(ref _activeText, value); }

    private string _noteText = string.Empty;
    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }

    // ---- Sliders (nullable: "no self-rating yet" is NOT 0%) ----------------------

    private double? _sleepQuality;
    public double? SleepQuality { get => _sleepQuality; set { if (Set(ref _sleepQuality, Clamp01(value))) RaiseQualityReaders(); } }

    private double? _mood;
    public double? Mood { get => _mood; set { if (Set(ref _mood, Clamp01(value))) RaiseMoodReaders(); } }

    private double? _energy;
    public double? Energy { get => _energy; set { if (Set(ref _energy, Clamp01(value))) RaiseEnergyReaders(); } }

    private double? _stress;
    public double? Stress { get => _stress; set { if (Set(ref _stress, Clamp01(value))) RaiseStressReaders(); } }

    private static double? Clamp01(double? v) => v is { } d && !double.IsNaN(d) ? Math.Clamp(d, 0d, 1d) : null;

    /// <summary>
    /// Slider bridges: unset sliders park mid-track WITHOUT claiming 0% (the percent label
    /// keeps saying "not set"); any actual drag commits a self-reported value.
    /// </summary>
    public double SleepQualitySlider
    {
        get => SleepQuality ?? 0.5;
        set { SleepQuality = value; Raise(nameof(SleepQualitySlider)); }
    }
    public double MoodSlider
    {
        get => Mood ?? 0.5;
        set { Mood = value; Raise(nameof(MoodSlider)); }
    }
    public double EnergySlider
    {
        get => Energy ?? 0.5;
        set { Energy = value; Raise(nameof(EnergySlider)); }
    }
    public double StressSlider
    {
        get => Stress ?? 0.5;
        set { Stress = value; Raise(nameof(StressSlider)); }
    }

    public string SleepQualityPercent => PercentText(SleepQuality);
    public string MoodPercent => PercentText(Mood);
    public string EnergyPercent => PercentText(Energy);
    public string StressPercent => PercentText(Stress);

    /// <summary>An unset slider reads "not set"; a set one reads its locale percent.</summary>
    private string PercentText(double? value) =>
        value is { } v ? _format.Percent(v) : LogUiStrings.NotSet;

    public bool HasSleepQuality => SleepQuality is not null;
    public bool HasMood => Mood is not null;
    public bool HasEnergy => Energy is not null;
    public bool HasStress => Stress is not null;

    private void RaiseQualityReaders()
    {
        Raise(nameof(SleepQualityPercent)); Raise(nameof(HasSleepQuality));
    }
    private void RaiseMoodReaders() { Raise(nameof(MoodPercent)); Raise(nameof(HasMood)); }
    private void RaiseEnergyReaders() { Raise(nameof(EnergyPercent)); Raise(nameof(HasEnergy)); }
    private void RaiseStressReaders() { Raise(nameof(StressPercent)); Raise(nameof(HasStress)); }

    // ---- Status / busy -------------------------------------------------------------

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private string? _statusText;
    public string? StatusText { get => _statusText; set { if (Set(ref _statusText, value)) Raise(nameof(HasStatus)); } }
    public bool HasStatus => !string.IsNullOrEmpty(StatusText);

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; set => Set(ref _statusIsError, value); }
    public bool StatusIsSuccess => !StatusIsError;

    private void ShowStatus(string text, bool error)
    {
        StatusIsError = error;
        StatusText = text;
        Raise(nameof(StatusIsSuccess));
        Raise(nameof(StatusOkVisible));
        Raise(nameof(StatusBadVisible));
    }

    /// <summary>Status slot visibility: only shown when there is actually a message.</summary>
    public bool StatusOkVisible => HasStatus && !StatusIsError;
    public bool StatusBadVisible => HasStatus && StatusIsError;

    /// <summary>Used by the Log tab when a row-level delete failed — one honest inline message.</summary>
    public void ShowDeleteFailure() => ShowStatus(LogUiStrings.DeleteFailed, error: true);

    private bool _hasStoredEntryFlag;
    /// <summary>True when the selected day already holds a stored manual entry (delete affordance).</summary>
    public bool HasStoredEntry { get => _hasStoredEntryFlag; private set { if (Set(ref _hasStoredEntryFlag, value)) { Raise(nameof(DeleteVisible)); Raise(nameof(ExistingEntryNoteVisible)); } } }
    public bool DeleteVisible => HasStoredEntry;
    /// <summary>Shows "you already logged this day — edit or remove it" for stored days.</summary>
    public bool ExistingEntryNoteVisible => HasStoredEntry;
    public string ExistingEntryNote => LogUiStrings.ExistingEntry;

    // ---- Load / save ---------------------------------------------------------------

    /// <summary>Loads the stored manual entry for <paramref name="date"/> into the editor fields.</summary>
    public async Task LoadForDateAsync(DateTime date)
    {
        if (_loadingDay) return;
        _loadingDay = true;
        try
        {
            var day = date.Date;
            ManualEntryDraft? stored = null;
            try { stored = await _entries.GetForDayAsync(day); }
            catch { /* unreadable store must not break the editor; a save failure surfaces honestly */ }

            HasStoredEntry = _hasStoredEntry = stored is not null;

            (SleepHoursText, SleepMinutesText) = LogUiStrings.SleepCells(stored?.SleepMinutes);
            StepsText = LogUiStrings.IntCell(stored?.Steps);
            ActiveText = LogUiStrings.IntCell(stored?.ActiveMinutes);
            NoteText = string.IsNullOrWhiteSpace(stored?.Note) ? string.Empty : stored!.Note!.Trim();
            SleepQuality = stored?.SleepQuality;
            Mood = stored?.Mood;
            Energy = stored?.Energy;
            Stress = stored?.Stress;

            ShowStatus(string.Empty, error: false);
            Raise(nameof(SleepQualitySlider)); Raise(nameof(MoodSlider)); Raise(nameof(EnergySlider)); Raise(nameof(StressSlider));
        }
        finally { _loadingDay = false; }
    }

    /// <summary>Builds the draft exactly as entered (blank stays null, no clamping yet).</summary>
    public ManualEntryDraft BuildDraft()
    {
        return new ManualEntryDraft
        {
            Date = Date,
            SleepMinutes = LogEntryRules.SleepTotalMinutes(SleepHoursText, SleepMinutesText),
            Steps = LogEntryRules.ParseInt(StepsText),
            ActiveMinutes = LogEntryRules.ParseInt(ActiveText),
            SleepQuality = SleepQuality,
            Mood = Mood,
            Energy = Energy,
            Stress = Stress,
            Note = string.IsNullOrWhiteSpace(NoteText) ? null : NoteText.Trim(),
        };
    }

    private async Task SaveAsync()
    {
        if (IsBusy) return;
        var draft = BuildDraft();

        // Validate the RAW input (a 25h night must surface as an error, not silently clamp to 24h),
        // persist only the sanitized draft once it passes.
        var verdict = LogEntryRules.Validate(draft, _clock.Today, _hasStoredEntry);
        if (verdict.Status == LogEntryValidity.NothingToSave)
        {
            ShowStatus(LogUiStrings.ErrorNothingToSave, error: true);
            return;
        }
        if (verdict.Status == LogEntryValidity.Invalid)
        {
            ShowStatus(LogUiStrings.Validation(verdict.MessageKey!), error: true);
            return;
        }

        IsBusy = true;
        try
        {
            await _entries.SaveAsync(LogEntryRules.Sanitize(draft));
            HasStoredEntry = _hasStoredEntry = true;
            ShowStatus(LogUiStrings.Saved(_format.ShortDate(draft.Date)), error: false);
            EntrySaved?.Invoke();
        }
        catch (Exception)
        {
            // Honesty: a failed write is reported as a failure, never as a success.
            ShowStatus(LogUiStrings.SaveFailed, error: true);
        }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync()
    {
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (IsBusy || !HasStoredEntry || page is null) return;
        bool confirm = await page.DisplayAlertAsync(
            LogUiStrings.DeleteConfirmTitle,
            LogUiStrings.DeleteConfirmBody(_format.ShortDate(Date)),
            Loc["Common.Delete"], Loc["Common.Cancel"]);
        if (!confirm) return;

        IsBusy = true;
        try
        {
            await _entries.DeleteAsync(Date);
            HasStoredEntry = _hasStoredEntry = false;
            await LoadForDateAsync(Date);   // back to "not provided" everywhere
            ShowStatus(LogUiStrings.Deleted(_format.ShortDate(Date)), error: false);
            EntrySaved?.Invoke();
        }
        catch (Exception)
        {
            ShowStatus(LogUiStrings.DeleteFailed, error: true);
        }
        finally { IsBusy = false; }
    }

    // ---- Route args (pushed flow: log-entry?date=2026-09-12) -----------------------

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("date", out var raw) && raw is string s &&
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            Date = parsed;
            return;   // the Date setter already triggered LoadForDateAsync
        }
        _ = LoadForDateAsync(Date);
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(DateLabel)); Raise(nameof(TodayText)); Raise(nameof(YesterdayText));
        Raise(nameof(SleepSection)); Raise(nameof(ActivitySection)); Raise(nameof(FeelingsSection));
        Raise(nameof(HoursLabel)); Raise(nameof(MinutesLabel)); Raise(nameof(StepsLabel)); Raise(nameof(ActiveLabel));
        Raise(nameof(SleepQualityLabel)); Raise(nameof(MoodLabel)); Raise(nameof(EnergyLabel)); Raise(nameof(StressLabel));
        Raise(nameof(NoteLabel)); Raise(nameof(OptionalHint)); Raise(nameof(HonestyNote));
        Raise(nameof(SaveText)); Raise(nameof(DeleteText)); Raise(nameof(ClearText));
        Raise(nameof(SleepExampleHint)); Raise(nameof(StepsExampleHint)); Raise(nameof(ActiveExampleHint));
        Raise(nameof(SelectedDateText)); Raise(nameof(ExistingEntryNote));
        Raise(nameof(MinutesExampleHint)); Raise(nameof(CheckInTitle)); Raise(nameof(CloseText));
        Raise(nameof(SleepQualityPercent)); Raise(nameof(MoodPercent)); Raise(nameof(EnergyPercent)); Raise(nameof(StressPercent));
    }
}
