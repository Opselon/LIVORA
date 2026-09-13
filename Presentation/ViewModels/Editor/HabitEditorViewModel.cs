using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Planning;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;

/// <summary>
/// Wave 3 (lane 07): the real habit editor. Name, cadence (<see cref="HabitFrequencyKind"/>),
/// times-per-week, and an optional reminder time that upserts a <see cref="ReminderSetting"/>
/// (Kind = "habit", TargetId = habit.Id) through <see cref="IReminderService"/>.
///
/// Reminder honesty: the service is lane 09's and may not be composed into this build yet. The
/// editor therefore reports what it actually did — "saved, but no notification scheduler is
/// available in this build" — and never implies a notification that cannot fire.
/// </summary>
public sealed class HabitEditorViewModel : ObservableObject
{
    private readonly IRepository<Habit> _repo;
    private readonly IFormatService _format;
    private readonly SessionState _session;
    private Habit? _editing;
    private List<Habit> _allHabits = new();

    public HabitEditorViewModel(
        IRepository<Habit> repo,
        IFormatService format,
        SessionState session)
    {
        _repo = repo;
        _format = format;
        _session = session;
        SubscribeLanguage();
        SaveCommand = new Command(async () => await SaveAsync());
        CancelCommand = new Command(async () => await CloseAsync());
        RebuildOptions();
        ApplySuggestions();
        Revalidate();
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    // ---- Options ----
    public List<string> FrequencyOptions { get; private set; } = new();

    // ---- Editable fields ----
    private string _name = string.Empty;
    public string Name { get => _name; set { if (Set(ref _name, value)) Revalidate(); } }

    private int _frequencyIndex = (int)HabitFrequencyKind.Daily;
    public int FrequencyIndex
    {
        get => _frequencyIndex;
        set
        {
            if (Set(ref _frequencyIndex, value))
            {
                Raise(nameof(ShowTimesPicker));
                Revalidate();
            }
        }
    }

    /// <summary>Daily / Weekdays carry their own cadence; only TimesPerWeek asks for a number.</summary>
    public bool ShowTimesPicker => GoalEditorRules.ExpectedTimesPerWeek((HabitFrequencyKind)FrequencyIndex) == 0;

    private string _timesText = "3";
    public string TimesText { get => _timesText; set { if (Set(ref _timesText, value)) Revalidate(); } }

    private bool _reminderEnabled;
    public bool ReminderEnabled
    {
        get => _reminderEnabled;
        set { if (Set(ref _reminderEnabled, value)) { Raise(nameof(ShowTimePicker)); Revalidate(); } }
    }
    public bool ShowTimePicker => ReminderEnabled;

    private TimeSpan _reminderTime = new(20, 0, 0);
    public TimeSpan ReminderTime { get => _reminderTime; set => Set(ref _reminderTime, value); }

    // ---- Labels ----
    public string Title => L(_editing is null ? "Editor.HabitTitle" : "Habits.EditHabit");
    public string NameLabel => L("Habits.Name");
    public string FrequencyLabel => L("Habits.Frequency");
    public string TimesLabel => L("Habits.TimesPerWeek");
    public string TimesHintText => L("Editor.Hint.TimesRange", _format.Number(1), _format.Number(GoalEditorRules.MaxTimesPerWeek));
    public string ReminderLabel => L("Habits.Reminder");
    public string ReminderNote => L("Habits.ReminderNote");
    public string SaveText => L("Common.Save");
    public string CancelText => L("Common.Cancel");
    public bool IsEditing => _editing is not null;
    public string NameHintText => L("Editor.NameHint", _format.Number(GoalEditorRules.MaxNameLength));

    public ObservableCollection<string> Messages { get; } = new();
    public bool HasMessages => Messages.Count > 0;
    public bool CanSave => _validation.IsValid;

    /// <summary>Validation copy only appears once the user has tried to save (or a load problem
    /// needs telling) — a pristine form must never open scolding them for an empty name.</summary>
    private bool _hasAttemptedSave;
    private string? _loadNote;

    private HabitEditValidation _validation = HabitEditValidation.Valid;

    private void ApplySuggestions()
    {
        var draft = GoalEditorRules.SuggestedHabitDraft(_session.CurrentProfile);
        _frequencyIndex = (int)draft.Frequency;
        _timesText = _format.Number(draft.TimesPerWeek);
        _reminderEnabled = false;
        _reminderTime = new TimeSpan(20, 0, 0);
        Raise(nameof(FrequencyIndex)); Raise(nameof(TimesText));
        Raise(nameof(ShowTimesPicker)); Raise(nameof(ReminderEnabled)); Raise(nameof(ShowTimePicker));
    }

    public async Task ApplyQueryAsync(IReadOnlyDictionary<string, string> query)
    {
        query.TryGetValue("id", out var id);
        await LoadAsync(id);
    }

    public async Task LoadAsync(string? habitId)
    {
        _loadNote = null;
        _allHabits = (await _repo.GetAllAsync()).ToList();
        if (string.IsNullOrWhiteSpace(habitId))
        {
            _editing = null;
            _name = string.Empty;
            ApplySuggestions();
            Revalidate();
            return;
        }

        var habit = _allHabits.FirstOrDefault(h => h.Id == habitId) ?? await _repo.GetAsync(habitId);
        if (habit is null)
        {
            _editing = null;
            ApplySuggestions();
            _loadNote = L("Editor.NotFound");
            Revalidate();
            return;
        }

        _editing = habit;
        _name = habit.Name;
        _frequencyIndex = (int)habit.Frequency;
        _timesText = _format.Number(habit.TimesPerWeek);
        // Show the reminder as ON only when a real stored reminder exists for this habit — never a
        // guess (honesty rule). The service may be absent in this build; then it reads as off.
        _reminderEnabled = await HasStoredReminderAsync(habit.Id);
        _reminderTime = await StoredReminderTimeAsync(habit.Id);
        RaiseAll();
        Revalidate();
    }

    private void RaiseAll()
    {
        Raise(nameof(Name)); Raise(nameof(FrequencyIndex)); Raise(nameof(TimesText));
        Raise(nameof(ShowTimesPicker)); Raise(nameof(ReminderEnabled)); Raise(nameof(ReminderTime));
        Raise(nameof(ShowTimePicker)); Raise(nameof(Title)); Raise(nameof(IsEditing)); Raise(nameof(CanSave));
    }

    private void RebuildOptions()
    {
        FrequencyOptions = Enum.GetValues<HabitFrequencyKind>()
            .Select(f => L(GoalEditorRules.FrequencyLabelKey(f))).ToList();
        Raise(nameof(FrequencyOptions));
    }

    private void Revalidate()
    {
        _validation = GoalEditorRules.ValidateHabit(
            Name, (HabitFrequencyKind)FrequencyIndex, TimesText, Loc.FormatCulture, _allHabits, _editing?.Id);
        // Quiet until asked: an untouched form never scolds the user for an empty name. Messages
        // appear after the first save attempt (or when a load could not find the item).
        Messages.Clear();
        if (_hasAttemptedSave || _loadNote is not null)
        {
            if (_loadNote is { } note) Messages.Add(note);
            if (_hasAttemptedSave)
                foreach (var issue in GoalEditorRules.OrderedIssues(_validation))
                    Messages.Add(MessageFor(issue));
        }
        Raise(nameof(HasMessages));
        Raise(nameof(CanSave));
    }

    private string MessageFor(HabitEditIssue issue) => issue switch
    {
        HabitEditIssue.NameEmpty => L("Editor.Error.HabitNameEmpty"),
        HabitEditIssue.NameTooLong => L("Editor.Error.NameTooLong", _format.Number(GoalEditorRules.MaxNameLength)),
        HabitEditIssue.NameDuplicate => L("Editor.Warn.NameDuplicate"),
        HabitEditIssue.TimesPerWeekOutOfRange => L("Editor.Error.TimesRange", _format.Number(1), _format.Number(GoalEditorRules.MaxTimesPerWeek)),
        HabitEditIssue.TimesPerWeekBelowFrequency => L("Editor.Error.TimesFixed"),
        _ => string.Empty,
    };

    // ---- Reminder service (lane 09's; absent in a partial build → honest "not wired" note) ----

    private static IReminderService? Reminders => ServiceHelper.TryGet<IReminderService>();

    private static async Task<bool> HasStoredReminderAsync(string habitId)
    {
        if (Reminders is not { } svc) return false;
        var list = await svc.GetRemindersAsync();
        return list.Any(r => r.Kind == GoalEditorRules.HabitReminderKind && r.TargetId == habitId && r.Enabled);
    }

    private static async Task<TimeSpan> StoredReminderTimeAsync(string habitId)
    {
        if (Reminders is not { } svc) return new TimeSpan(20, 0, 0);
        var list = await svc.GetRemindersAsync();
        return list.FirstOrDefault(r => r.Kind == GoalEditorRules.HabitReminderKind && r.TargetId == habitId)?.TimeOfDay
               ?? new TimeSpan(20, 0, 0);
    }

    private async Task SaveAsync()
    {
        _hasAttemptedSave = true;
        Revalidate();
        if (_validation.BlocksSave) return;

        var habit = _editing ?? new Habit();
        habit.Name = GoalEditorRules.CleanName(Name);
        habit.Frequency = (HabitFrequencyKind)FrequencyIndex;
        habit.TimesPerWeek = GoalEditorRules.ResolveTimesPerWeek(habit.Frequency, TimesText, Loc.FormatCulture);
        await _repo.SaveAsync(habit);

        string? note = await SyncReminderAsync(habit);
        if (note is not null)
        {
            // Stay open and report exactly what happened with the notification side — the model
            // saved, so this is not a failure, but claiming a reminder "is set" when the scheduler
            // isn't in this build would be.
            Messages.Clear();
            Messages.Add(note);
            Raise(nameof(HasMessages));
            return;
        }
        await CloseAsync();
    }

    /// <summary>Upsert / clear the habit's reminder. Returns a localized status note when the user
    /// should be told something before the page closes; null = nothing to report.</summary>
    private async Task<string?> SyncReminderAsync(Habit habit)
    {
        var svc = Reminders;
        if (svc is null)
            return ReminderEnabled ? L("Habits.ReminderUnavailable") : null;

        var existing = (await svc.GetRemindersAsync())
            .FirstOrDefault(r => r.Kind == GoalEditorRules.HabitReminderKind && r.TargetId == habit.Id);

        if (!ReminderEnabled)
        {
            if (existing is not null)
            {
                await svc.DeleteAsync(existing.Id);
                await svc.SyncAsync();
            }
            return null;
        }

        var id = existing?.Id ?? GoalEditorRules.HabitReminderId(habit.Id);
        await svc.SaveAsync(new ReminderSetting
        {
            Id = id,
            Kind = GoalEditorRules.HabitReminderKind,
            TargetId = habit.Id,
            Enabled = true,
            TimeOfDay = ReminderTime,
            DaysMask = DaysMaskFor(habit.Frequency),
            TextKey = GoalEditorRules.HabitReminderTextKey,
            TextArgs = new object[] { habit.Name },
        });
        await svc.SyncAsync();

        // Only report a problem. When the OS says notifications are allowed the reminder really
        // is scheduled, so the habit is saved and the page closes like any other save.
        var grant = await svc.GetGrantStateAsync();
        return grant switch
        {
            NotificationGrantState.Allowed => null,
            NotificationGrantState.Unsupported => L("Habits.ReminderUnsupported"),
            NotificationGrantState.Denied => L("Habits.ReminderDenied"),
            _ => L("Habits.ReminderNeedsPermission"),
        };
    }

    /// <summary>7-bit weekday mask, bit0 = Sunday (the contract's convention). Weekdays = Mon–Fri;
    /// anything else fires every day (TimesPerWeek is a target, not a guarantee of which days).</summary>
    public static int DaysMaskFor(HabitFrequencyKind frequency) => frequency switch
    {
        HabitFrequencyKind.Weekdays => 0b0111110,
        _ => 0b1111111,
    };

    private static Page? CurrentPage
        => Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    private async Task CloseAsync()
    {
        var page = CurrentPage;
        try
        {
            var shell = Microsoft.Maui.Controls.Shell.Current;
            if (shell is not null && shell.Navigation.NavigationStack.Count > 1
                && page is not null && shell.Navigation.NavigationStack.Contains(page))
            {
                await shell.GoToAsync("../");
                return;
            }
        }
        catch (Exception) { /* not in a shell navigation context — fall through */ }

        if (page?.Navigation is { } nav)
        {
            if (nav.NavigationStack.Count > 1) await nav.PopAsync();
            else if (nav.ModalStack.Count > 0) await nav.PopModalAsync();
        }
    }

    protected override void OnLanguageChanged()
    {
        RebuildOptions();
        Raise(nameof(Title)); Raise(nameof(NameLabel)); Raise(nameof(FrequencyLabel));
        Raise(nameof(TimesLabel)); Raise(nameof(TimesHintText)); Raise(nameof(ReminderLabel));
        Raise(nameof(ReminderNote)); Raise(nameof(SaveText)); Raise(nameof(CancelText));
        Raise(nameof(NameHintText));
        Revalidate();
    }
}
