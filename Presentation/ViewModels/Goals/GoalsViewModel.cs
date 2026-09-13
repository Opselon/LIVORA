using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Planning;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;

/// <summary>
/// The one place a goal/habit row's DISPLAY name is resolved.
///
/// MERGE: NameKey — the orchestrator is fixing the seeder (DemoDataSeeder currently stores
/// translated text in Goal.Name/Habit.Name, which is why seeded rows stay in the language they
/// were created in after a live EN↔FA switch). When `NameKey` lands on the models, the two
/// bodies below flip to `x.NameKey is {Length: > 0} k ? l(k) : x.Name` here and nowhere else.
/// User-created goals/habits never carry a NameKey: their literal typed name is already
/// language-independent, and this helper must not touch it.
/// </summary>
internal static class GoalNames
{
    // MERGE: NameKey
    public static string Of(Goal goal, Func<string, string> l) => goal.Name;
    // MERGE: NameKey
    public static string Of(Habit habit, Func<string, string> l) => habit.Name;
}

/// <summary>Presentation row for one goal card (used by Goals + Today pages).</summary>
public sealed class GoalItemViewModel
{
    public required Goal Goal { get; init; }
    /// <summary>Localized display name — see <see cref="GoalNames"/>. Bind this, not Goal.Name.</summary>
    public required string NameText { get; init; }
    public required string CategoryText { get; init; }
    public required string StatusText { get; init; }
    public required Color StatusColor { get; init; }
    public required string ProgressText { get; init; }
    public required double Fraction { get; init; }

    // ---- Wave 3 (lane 07): the real editor flow needs per-row affordances ----
    /// <summary>Manual-counter goals can be bumped; metric goals are computed, never faked.</summary>
    public required bool CanLogProgress { get; init; }
    /// <summary>True when the goal measures itself from health data (honesty: not live yet).</summary>
    public required bool IsMetricMeasured { get; init; }
    public required bool HasDeadline { get; init; }
    public required string DeadlineText { get; init; }
    /// <summary>"Archive" when there is logged progress, "Delete" when there is nothing to keep.</summary>
    public required string ArchiveOrDeleteText { get; init; }
    public string PeriodHintText { get; init; } = string.Empty;

    public static GoalItemViewModel FromGoal(Goal g, IFormatService format, Func<string, string> l, string? deadlineText = null)
    {
        string statusKey = "Goals." + (g.Status switch
        {
            GoalStatus.OnTrack => "OnTrack",
            GoalStatus.AtRisk => "AtRisk",
            GoalStatus.Behind => "Behind",
            _ => "Completed",
        });
        return new GoalItemViewModel
        {
            Goal = g,
            NameText = GoalNames.Of(g, l),
            CategoryText = l("Enum.GoalCategory." + g.Category),
            StatusText = l(statusKey),
            StatusColor = Theme.ForGoalStatus(g.Status),
            Fraction = g.Fraction,
            ProgressText = $"{format.Number((long)g.ProgressValue)} / {format.Number((long)g.TargetValue)}",
            CanLogProgress = GoalEditorRules.AllowsManualProgress(g) && g.ProgressValue < g.TargetValue,
            IsMetricMeasured = GoalEditorRules.IsMetricMeasured(g),
            HasDeadline = g.Deadline is not null,
            DeadlineText = deadlineText ?? string.Empty,
            ArchiveOrDeleteText = l(GoalEditorRules.DeleteOrArchiveActionKey(g)),
            PeriodHintText = l(GoalEditorRules.PeriodSuffixKey(g.Period)),
        };
    }
}

/// <summary>Presentation row for one habit card, driven by the real completion log.</summary>
public sealed class HabitItemViewModel
{
    public required Habit Habit { get; init; }
    /// <summary>Localized display name — see <see cref="GoalNames"/>. Bind this, not Habit.Name.</summary>
    public required string Name { get; init; }
    public required string FrequencyText { get; init; }
    public required bool DoneToday { get; init; }
    public required string ToggleText { get; init; }
    public required bool HasStreak { get; init; }
    public required string StreakText { get; init; }
    public required string WeekText { get; init; }
    /// <summary>0..1 — completions this week against the habit's own cadence, not a flat 7.</summary>
    public required double WeekFraction { get; init; }
    public required bool HasWeekProgress { get; init; }
}

/// <summary>Which slice of the Goals tab is showing. Values are presentation-only.</summary>
public enum GoalsSegment { Goals = 0, Habits = 1, Archived = 2 }

public sealed class GoalsViewModel : ObservableObject
{
    private readonly IRepository<Goal> _repo;
    private readonly IRepository<Habit> _habitRepo;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;
    private readonly IDateTimeProvider _clock;
    private readonly IFormatService _format;

    // Undo lives in memory only: closing the app discards it, and that is honest — we never
    // claim a restore that persistence could not honor.
    private Goal? _undoGoal;
    private bool _undoWasRestore;   // archived -> un-archive; deleted -> re-insert
    private Habit? _undoHabit;

    public GoalsViewModel(
        IRepository<Goal> repo,
        IRepository<Habit> habitRepo,
        IHistoryRepository history,
        SessionState session,
        IDateTimeProvider clock,
        IFormatService format)
    {
        _repo = repo;
        _habitRepo = habitRepo;
        _history = history;
        _session = session;
        _clock = clock;
        _format = format;
        SubscribeLanguage();
        LoadCommand = new Command(async () => await LoadAsync());
        NewItemCommand = new Command(async () => await NewItemAsync());
        OpenGoalEditorCommand = new Command<GoalItemViewModel>(async g => await GoToGoalEditorAsync(g?.Goal.Id));
        LogProgressCommand = new Command<GoalItemViewModel>(async g => await LogProgressAsync(g));
        ArchiveGoalCommand = new Command<GoalItemViewModel>(async g => await ArchiveOrDeleteGoalAsync(g));
        RestoreGoalCommand = new Command<GoalItemViewModel>(async g => await RestoreGoalAsync(g));
        EditHabitCommand = new Command<HabitItemViewModel>(async h => await GoToHabitEditorAsync(h?.Habit.Id));
        ToggleHabitCommand = new Command<HabitItemViewModel>(async h => await ToggleHabitAsync(h));
        DeleteHabitCommand = new Command<HabitItemViewModel>(async h => await DeleteHabitAsync(h));
        SelectSegmentCommand = new Command<object>(o => SelectSegment(o));
        UndoCommand = new Command(async () => await UndoAsync());
    }

    public ICommand LoadCommand { get; }
    public ICommand NewItemCommand { get; }
    public ICommand OpenGoalEditorCommand { get; }
    public ICommand LogProgressCommand { get; }
    public ICommand ArchiveGoalCommand { get; }
    public ICommand RestoreGoalCommand { get; }
    public ICommand EditHabitCommand { get; }
    public ICommand ToggleHabitCommand { get; }
    public ICommand DeleteHabitCommand { get; }
    public ICommand SelectSegmentCommand { get; }
    public ICommand UndoCommand { get; }

    // ---- Static labels ----
    public string Title => L("Goals.Title");
    public string EmptyText => _segment switch
    {
        GoalsSegment.Habits => L("Habits.Empty"),
        GoalsSegment.Archived => L("Goals.ArchivedEmpty"),
        _ => L("Goals.Empty"),
    };
    public string NewActionText => _segment == GoalsSegment.Habits ? L("Habits.NewHabit") : L("Goals.NewGoal");
    public string LogProgressText => L("Goals.LogProgress");
    public string MetricPendingNote => L("Goals.MetricNote");
    public string SegmentGoalsText => L("Goals.Title");
    public string SegmentHabitsText => L("Habits.Title");
    public string SegmentArchivedText => L("Goals.Archived");
    public string UndoText => L("Common.Undo");

    // ---- Segmented filter (Goals / Habits / Archived) ----
    private GoalsSegment _segment = GoalsSegment.Goals;
    public GoalsSegment Segment { get => _segment; private set { Set(ref _segment, value); RaiseSegmentViews(); } }

    // Every list is suppressed while loading — the skeleton is the only story then (same rule
    // the pre-Wave-3 page used for its single list).
    public bool ShowGoals => !IsBusy && _segment == GoalsSegment.Goals;
    public bool ShowHabits => !IsBusy && _segment == GoalsSegment.Habits;
    public bool ShowArchived => !IsBusy && _segment == GoalsSegment.Archived;
    public bool ShowEmpty => !IsBusy && CurrentCount == 0;
    private int CurrentCount => _segment switch
    {
        GoalsSegment.Habits => Habits.Count,
        GoalsSegment.Archived => ArchivedGoals.Count,
        _ => Goals.Count,
    };

    private void SetSegment(int index)
    {
        var next = (GoalsSegment)Math.Clamp(index, 0, 2);
        if (next == _segment) return;
        Segment = next;
    }

    /// <summary>XAML CommandParameters arrive as strings or ints depending on the platform
    /// binding path, so accept both rather than silently doing nothing.</summary>
    private void SelectSegment(object? parameter)
    {
        switch (parameter)
        {
            case int i: SetSegment(i); break;
            case string s when int.TryParse(s, out var p): SetSegment(p); break;
            case null: break;
            default: SetSegment(System.Convert.ToInt32(parameter, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }

    private void RaiseSegmentViews()
    {
        Raise(nameof(Segment));
        Raise(nameof(ShowGoals));
        Raise(nameof(ShowHabits));
        Raise(nameof(ShowArchived));
        Raise(nameof(ShowEmpty));
        Raise(nameof(EmptyText));
        Raise(nameof(NewActionText));
        RaiseSegmentButtons();
    }

    /// <summary>Segment button chrome (same idiom as the language buttons — Color-typed props,
    /// never brushes): active = accent, idle = neutral overlay.</summary>
    public Color SegGoalsBackground => _segment == GoalsSegment.Goals ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color SegGoalsText => _segment == GoalsSegment.Goals ? Colors.White : Theme.TextSecondary;
    public Color SegHabitsBackground => _segment == GoalsSegment.Habits ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color SegHabitsText => _segment == GoalsSegment.Habits ? Colors.White : Theme.TextSecondary;
    public Color SegArchivedBackground => _segment == GoalsSegment.Archived ? Theme.Accent : Color.FromArgb("#E7E5E0");
    public Color SegArchivedText => _segment == GoalsSegment.Archived ? Colors.White : Theme.TextSecondary;

    private void RaiseSegmentButtons()
    {
        Raise(nameof(SegGoalsBackground)); Raise(nameof(SegGoalsText));
        Raise(nameof(SegHabitsBackground)); Raise(nameof(SegHabitsText));
        Raise(nameof(SegArchivedBackground)); Raise(nameof(SegArchivedText));
    }

    // ---- Collections ----
    public ObservableCollection<GoalItemViewModel> Goals { get; } = new();
    public ObservableCollection<GoalItemViewModel> ArchivedGoals { get; } = new();
    public ObservableCollection<HabitItemViewModel> Habits { get; } = new();

    // ---- Undo banner ----
    private bool _undoVisible;
    public bool UndoVisible { get => _undoVisible; private set => Set(ref _undoVisible, value); }

    private string _undoMessage = string.Empty;
    public string UndoMessage { get => _undoMessage; private set => Set(ref _undoMessage, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                Raise(nameof(ShowEmpty));
                Raise(nameof(ShowGoals));
                Raise(nameof(ShowHabits));
                Raise(nameof(ShowArchived));
            }
        }
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var all = (await _repo.GetAllAsync()).ToList();
            Goals.Clear();
            foreach (var g in all.Where(g => !g.IsArchived)
                         .OrderBy(g => g.Status == GoalStatus.Completed ? 1 : 0)
                         .ThenByDescending(g => g.Fraction))
                Goals.Add(ToGoalItem(g));

            ArchivedGoals.Clear();
            foreach (var g in all.Where(g => g.IsArchived))
                ArchivedGoals.Add(ToGoalItem(g));

            var habits = await _habitRepo.GetAllAsync();
            Habits.Clear();
            foreach (var h in habits) Habits.Add(ToHabitItem(h));

            Raise(nameof(ShowEmpty));
        }
        finally { IsBusy = false; }
    }

    private GoalItemViewModel ToGoalItem(Goal g) => GoalItemViewModel.FromGoal(
        g, _format, key => Loc[key],
        g.Deadline is { } d ? L("Goals.Deadline", _format.ShortDate(d.Date)) : null);

    private HabitItemViewModel ToHabitItem(Habit h)
    {
        int expected = GoalEditorRules.WeeklyExpectation(h);
        int thisWeek = h.CompletionsThisWeek(_clock.Today);
        int streak = h.CurrentStreak;
        bool done = h.IsCompletedOn(_clock.Today);
        return new HabitItemViewModel
        {
            Habit = h,
            Name = GoalNames.Of(h, key => Loc[key]),
            FrequencyText = L(GoalEditorRules.FrequencyLabelKey(h.Frequency)),
            DoneToday = done,
            ToggleText = done ? L("Habits.Undo") : L("Habits.MarkDone"),
            HasStreak = streak > 0,
            StreakText = L("Common.StreakDays", _format.Number(streak)),
            WeekText = L("Common.ThisWeekCount", _format.Number(thisWeek), _format.Number(expected)),
            WeekFraction = expected > 0 ? Math.Clamp((double)thisWeek / expected, 0, 1) : 0,
            HasWeekProgress = thisWeek > 0,
        };
    }

    // ---- Navigation to the real editors (routes land via APPEND) ----

    private async Task NewItemAsync()
    {
        if (_segment == GoalsSegment.Habits) await GoToHabitEditorAsync(null);
        else await GoToGoalEditorAsync(null);
    }

    private async Task GoToGoalEditorAsync(string? goalId)
        => await NavigateAsync(goalId is null ? "goal-editor?mode=new" : $"goal-editor?id={goalId}");

    private async Task GoToHabitEditorAsync(string? habitId)
        => await NavigateAsync(habitId is null ? "habit-editor?mode=new" : $"habit-editor?id={habitId}");

    private async Task NavigateAsync(string route)
    {
        // MAUI's GoToAsync is an instance method on the current Shell; Shell.Current is null while
        // the app still shows onboarding (no shell yet) — report that instead of throwing.
        var shell = Microsoft.Maui.Controls.Shell.Current;
        if (shell is null)
        {
            await AlertAsync(L("Goals.Title"), L("Editor.NavigationFailed"), L("Common.Done"));
            return;
        }
        try
        {
            // MAUI's GoToAsync resolves asynchronously and leaves the page in place when the route
            // is unknown (the route lines land with the orchestrator's APPEND merge). We check the
            // navigation stack afterwards so a failed jump is reported, never a silent no-op.
            int depthBefore = shell.Navigation.NavigationStack.Count;
            await shell.GoToAsync(route);
            if (shell.Navigation.NavigationStack.Count == depthBefore)
                await AlertAsync(L("Goals.Title"), L("Editor.NavigationFailed"), L("Common.Done"));
        }
        catch (Exception)
        {
            await AlertAsync(L("Goals.Title"), L("Editor.NavigationFailed"), L("Common.Done"));
        }
    }

    // ---- Goal actions ----

    private async Task LogProgressAsync(GoalItemViewModel? item)
    {
        if (item is null) return;
        var goal = item.Goal;
        if (!GoalEditorRules.AllowsManualProgress(goal)) return;
        goal.ProgressValue = GoalEditorRules.NextManualProgress(goal);
        await _repo.SaveAsync(goal);
        // Same seam the state engine uses, so daily history stays the single source of truth.
        await _history.EnsureLoadedAsync(_session.CurrentProfile, _clock.Today);
        await _history.RecordGoalProgressAsync(_clock.Today, goal.Id);
        await LoadAsync();
    }

    /// <summary>Archive (progress exists) or hard delete (nothing was ever logged) — the rule, not
    /// a checkbox, so logged effort can never be destroyed by one tap. Undo offered in both cases.</summary>
    private async Task ArchiveOrDeleteGoalAsync(GoalItemViewModel? item)
    {
        if (item is null) return;
        var goal = item.Goal;
        bool hard = GoalEditorRules.ShouldHardDelete(goal);

        string title = L(hard ? "Editor.Confirm.Delete.Title" : "Editor.Confirm.Archive.Title");
        string body = L(GoalEditorRules.ConfirmBodyKey(goal));
        if (!await ConfirmAsync(title, body)) return;

        if (hard)
        {
            await _repo.DeleteAsync(goal.Id);
            _undoGoal = goal; _undoWasRestore = false;
            UndoMessage = L("Editor.Undo.Deleted", item.NameText);
        }
        else
        {
            GoalEditorRules.Archive(goal);
            await _repo.SaveAsync(goal);
            _undoGoal = goal; _undoWasRestore = true;
            UndoMessage = L("Editor.Undo.Archived", item.NameText);
        }
        UndoVisible = true;
        await LoadAsync();
    }

    private async Task RestoreGoalAsync(GoalItemViewModel? item)
    {
        if (item is null) return;
        GoalEditorRules.Restore(item.Goal);
        await _repo.SaveAsync(item.Goal);
        ClearUndo();
        await LoadAsync();
    }

    private async Task UndoAsync()
    {
        if (_undoGoal is { } g)
        {
            if (_undoWasRestore) { GoalEditorRules.Restore(g); await _repo.SaveAsync(g); }
            else await _repo.SaveAsync(g); // hard delete: the snapshot goes straight back into the store
        }
        else if (_undoHabit is { } h)
        {
            await _habitRepo.SaveAsync(h);
            // Undo must undo the history write too, or a habit completed today would come back
            // reading "not done today" in the state engine while its own log says otherwise.
            await _history.EnsureLoadedAsync(_session.CurrentProfile, _clock.Today);
            await _history.RecordHabitCompletionAsync(_clock.Now.Date, h.Id, h.IsCompletedOn(_clock.Today));
            _undoHabit = null;
        }
        ClearUndo();
        await LoadAsync();
    }

    private void ClearUndo()
    {
        _undoGoal = null; _undoHabit = null; _undoWasRestore = false;
        UndoVisible = false;
        UndoMessage = string.Empty;
    }

    // ---- Habit actions ----

    private async Task ToggleHabitAsync(HabitItemViewModel? item)
    {
        if (item is null) return;
        var h = item.Habit;
        var now = _clock.Now;
        if (h.IsCompletedOn(now.Date)) h.Uncomplete(now.Date);
        else h.Complete(now);
        await _habitRepo.SaveAsync(h);
        await _history.EnsureLoadedAsync(_session.CurrentProfile, _clock.Today);
        await _history.RecordHabitCompletionAsync(now.Date, h.Id, h.IsCompletedOn(now.Date));
        await LoadAsync();
    }

    private async Task DeleteHabitAsync(HabitItemViewModel? item)
    {
        if (item is null) return;
        var h = item.Habit;
        if (!await ConfirmAsync(L("Editor.Confirm.DeleteHabit.Title"), L("Editor.Confirm.DeleteHabit.Body")))
            return;
        await _habitRepo.DeleteAsync(h.Id);
        // Same write path TodayViewModel uses for toggles — the habit must vanish from today's
        // history row too, or the state engine keeps counting a habit that no longer exists.
        await _history.EnsureLoadedAsync(_session.CurrentProfile, _clock.Today);
        await _history.RecordHabitCompletionAsync(_clock.Now.Date, h.Id, completed: false);
        // A habit reminder without its habit must not keep firing (when lane 09's service exists).
        if (ServiceHelper.TryGet<IReminderService>() is { } reminders)
        {
            await reminders.DeleteAsync(GoalEditorRules.HabitReminderId(h.Id));
            await reminders.SyncAsync();
        }
        _undoHabit = h;
        UndoMessage = L("Editor.Undo.Deleted", item.Name);
        UndoVisible = true;
        await LoadAsync();
    }

    // ---- Dialog bridge (page-level UI, same idiom as ProfileViewModel) ----

    private static Page? CurrentPage
        => Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        var page = CurrentPage;
        return page is not null && await page.DisplayAlertAsync(title, body, L("Common.Yes"), L("Common.No"));
    }

    private async Task AlertAsync(string title, string body, string dismiss)
    {
        if (CurrentPage is { } page) await page.DisplayAlertAsync(title, body, dismiss);
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(EmptyText));
        Raise(nameof(NewActionText));
        Raise(nameof(LogProgressText));
        Raise(nameof(MetricPendingNote));
        Raise(nameof(SegmentGoalsText));
        Raise(nameof(SegmentHabitsText));
        Raise(nameof(SegmentArchivedText));
        Raise(nameof(UndoText));
        _ = LoadAsync();
    }
}
