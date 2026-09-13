using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.Planning;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;
/// <summary>
/// One calendar cell on the program page. State (completed / today / adapted / upcoming) is decided
/// by <see cref="BootcampProgress"/> from real persisted flags; the view only paints what it is told.
/// Colors and strokes are referenced by TOKEN KEY, never by value (see §2 of the design rules).
/// </summary>
public sealed class BootcampDayCellViewModel : ObservableObject
{
    private readonly BootcampDetailViewModel _owner;

    internal BootcampDayCellViewModel(BootcampDetailViewModel owner, BootcampDayState state, int dayNumber,
        string numberText, string planText, string minutesText, string statusText, bool isAdapted)
    {
        _owner = owner;
        State = state;
        DayNumber = dayNumber;
        NumberText = numberText;
        PlanText = planText;
        MinutesText = minutesText;
        StatusText = statusText;
        IsAdapted = isAdapted;
        SelectCommand = new Command(() => _owner.SelectDay(DayNumber));
    }

    public BootcampDayState State { get; }
    public int DayNumber { get; }
    public string NumberText { get; }
    public string PlanText { get; }
    public string MinutesText { get; }
    /// <summary>Localized status word ("Completed", "Today", …) — the cell's accessibility text.</summary>
    public string StatusText { get; }
    public bool IsAdapted { get; }

    public bool IsCompleted => State is BootcampDayState.Completed or BootcampDayState.CompletedToday;
    public bool IsToday => State is BootcampDayState.Today or BootcampDayState.AdaptedToday or BootcampDayState.CompletedToday;
    public bool IsUpcoming => State == BootcampDayState.Upcoming;
    /// <summary>Today AND not yet done — the day the primary action points at.</summary>
    public bool IsOpenToday => State is BootcampDayState.Today or BootcampDayState.AdaptedToday;

    private bool _selected;
    /// <summary>Which cell the plan card below the calendar is showing (the VM owns selection).</summary>
    public bool IsSelected { get => _selected; internal set => Set(ref _selected, value); }

    // ---- Token keys (resolved by BrushByKeyConverter / ColorByKey in XAML) ----
    public string FillKey => IsAdapted && IsToday ? "MetricActivitySoftBrush"
        : IsCompleted ? "AccentSoftBrush"
        : IsToday ? "SurfaceBrush"
        : "OverlayBrush";
    public string StrokeKey => IsAdapted && IsToday ? "MetricActivityBrush"
        : IsToday ? "AccentBrush"
        : IsCompleted ? "AccentSoftBrush"
        : string.Empty;
    public double StrokeThickness => IsToday ? 2 : 1;
    public string SemanticText => $"{NumberText} · {StatusText}";
    public ICommand SelectCommand { get; }
}

/// <summary>
/// Program detail page: what the program is, honest progress from Days[].IsCompleted, the day
/// calendar, and today's plan with the real reason it changed (the same <see cref="ProgramAdapter"/>
/// + <c>IRuleEngine</c> that produce recommendations — so the explanation can never disagree with
/// the adaptation). Enrollment and day completion mutate through <see cref="BootcampProgress"/>;
/// the shared <see cref="IRepository{T}"/> + <see cref="IHistoryRepository"/> are the only writers.
/// </summary>
public sealed class BootcampDetailViewModel : ObservableObject
{
    private readonly IRepository<Bootcamp> _repo;
    private readonly IFormatService _format;
    private readonly ProgramAdapter _adapter;
    private readonly IUserStateService _stateService;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;

    private Bootcamp? _bootcamp;
    private BootcampProgressSnapshot _progress = Empty;
    private List<Goal> _goalsSnapshot = new();
    private List<Habit> _habitsSnapshot = new();
    private Domain.Models.State.PersonalState? _state;
    private int _selectedDay;

    static readonly BootcampProgressSnapshot Empty = new()
    {
        Cells = Array.Empty<BootcampDayCell>(),
        TotalDays = 0, CompletedDays = 0, CurrentDay = 0, DaysRemaining = 0, AdaptedDays = 0,
        StreakDays = 0, CompletionFraction = 0, NotStarted = true, Finished = false,
        DaysPlayed = 0, PacePerDay = 0, NextMilestoneDay = 0, NextMilestonePercent = 0,
        PlannedMinutesRemaining = 0,
    };

    public BootcampDetailViewModel(
        IRepository<Bootcamp> repo,
        IFormatService format,
        ILocalizationService loc,
        ProgramAdapter adapter,
        IUserStateService stateService,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        IHistoryRepository history,
        SessionState session)
    {
        _repo = repo;
        _format = format;
        _adapter = adapter;
        _stateService = stateService;
        _habits = habits;
        _goals = goals;
        _history = history;
        _session = session;
        SubscribeLanguage();
        EnrollCommand = new Command(async () => await EnrollAsync());
        LeaveCommand = new Command(async () => await LeaveAsync());
        CompleteDayCommand = new Command(async () => await CompleteDayAsync(), () => CanCompleteToday);
        BackCommand = new Command(async () => await Shell.Current.GoToAsync(".."));
    }

    public ICommand EnrollCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand CompleteDayCommand { get; }
    public ICommand BackCommand { get; }

    /// <summary>Set from the route query (<c>bootcamp-detail?id=…</c>) before the first load.</summary>
    public string? ProgramId { get; set; }

    // ---- Static labels (the view resolves these with {localize:Tr …}); the properties below are
    // the composed strings the VM must build because they carry numbers. ----
    public string OverviewLabel => L("Bootcamp.Overview");
    public string CalendarLabel => L("Bootcamp.Calendar");
    public string PlanForTodayLabel => L("Programs.PlanForToday");
    public string AdaptedBadge => L("Programs.AdjustedPlan");
    public string OriginalPlanLabel => L("Programs.OriginalPlan");
    public string CompletedLabel => L("Bootcamp.Progress.Completed");
    public string StreakLabel => L("Bootcamp.Progress.Streak");
    public string RemainingLabel => L("Bootcamp.Progress.Remaining");
    public string MinutesLabel => L("Bootcamp.Progress.MinutesLeft");
    public string SampleNoteText => L("Bootcamp.SampleNote");

    public string EnrollText => L("Programs.Enroll");
    public string LeaveText => L("Programs.Leave");
    public string CompleteDayText => L("Common.Complete");
    public string BackText => L("Common.Back");
    public string NotFoundText => L("Bootcamp.NotFound");

    private bool _isLoading = true;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    public bool HasProgram => _bootcamp is not null;
    public bool IsEnrolled => _bootcamp?.IsEnrolled == true;
    public bool NotEnrolled => _bootcamp is not null && _bootcamp.IsEnrolled == false;
    public bool Finished => _progress.Finished;
    public bool ShowEmptyToday => IsEnrolled && !Finished && TodayPlan is null;
    /// <summary>A day is scheduled so the plan card can render it. When not enrolled this is a preview.</summary>
    public bool HasTodayPlan => TodayPlan is not null;
    /// <summary>Before enrollment the card previews day 1 — the view labels it with PreviewLabel.</summary>
    public bool IsPreview => !IsEnrolled && TodayPlan is not null;
    /// <summary>"Preview of day 1" caption (Bootcamp.Today.Preview).</summary>
    public string PreviewLabel => L("Bootcamp.Today.Preview");

    public string TitleText { get; private set; } = string.Empty;
    public string DescriptionText { get; private set; } = string.Empty;
    public string CategoryText { get; private set; } = string.Empty;
    public string DifficultyText { get; private set; } = string.Empty;
    public string DurationText { get; private set; } = string.Empty;
    public string CreatorText { get; private set; } = string.Empty;
    public string StatusText { get; private set; } = string.Empty;
    public string ProgressPercentText { get; private set; } = string.Empty;
    public double ProgressFraction { get; private set; }
    public string CompletedText { get; private set; } = string.Empty;
    public string StreakText { get; private set; } = string.Empty;
    public bool HasStreak { get; private set; }
    public string RemainingText { get; private set; } = string.Empty;
    public string AdaptedLabel => L("Bootcamp.Progress.Adapted");
    public string AdaptedText { get; private set; } = string.Empty;
    public bool ShowAdaptedStat => _progress.AdaptedDays > 0;
    public string MinutesText { get; private set; } = string.Empty;
    public string MilestoneText { get; private set; } = string.Empty;
    public bool ShowMilestone => _progress.NextMilestoneDay > 0 && !_progress.Finished;
    public string FinishEstimateText { get; private set; } = string.Empty;
    public string DayPointerText { get; private set; } = string.Empty;

    /// <summary>Today's adapted plan (null = nothing planned).</summary>
    public BootcampDay? TodayPlan { get; private set; }
    /// <summary>Rule key that changed today's day — only when a rule really fired.</summary>
    public string? AdaptationRuleKey { get; private set; }
    public bool WasAdapted => TodayPlan?.IsAdapted == true;
    public string TodayTitleText { get; private set; } = string.Empty;
    public string TodayDetailText { get; private set; } = string.Empty;
    public string TodayMinutesText { get; private set; } = string.Empty;
    public string AdaptationReasonText { get; private set; } = string.Empty;
    public string OriginalPlanText { get; private set; } = string.Empty;
    public bool ShowOriginalPlan => WasAdapted && !string.IsNullOrEmpty(OriginalPlanText);

    public ObservableCollection<BootcampDayCellViewModel> Days { get; } = new();

    // Selected day card (defaults to today, or day 1 before enrolling).
    public string SelectedDayTitleText { get; private set; } = string.Empty;
    public string SelectedDayDetailText { get; private set; } = string.Empty;
    public string SelectedDayMinutesText { get; private set; } = string.Empty;
    public string SelectedDayStatusText { get; private set; } = string.Empty;
    public string SelectedDayLabel { get; private set; } = string.Empty;

    public bool CanCompleteToday => _bootcamp is { IsEnrolled: true } && TodayPlan is not null && !Finished;

    public async Task LoadAsync()
    {
        var id = ProgramId;
        var profile = _session.CurrentProfile;
        var repoTask = _repo.GetAllAsync();
        var stateTask = _stateService.GetStateAsync(DataRefreshMode.Resume);
        var habitsTask = _habits.GetAllAsync();
        var goalsTask = _goals.GetAllAsync();
        await Task.WhenAll(repoTask, stateTask, habitsTask, goalsTask);
        IsLoading = false;

        var catalog = repoTask.Result;
        _bootcamp = id is null ? null : catalog.FirstOrDefault(b => b.Id == id);
        _state = stateTask.Result;
        _habitsSnapshot = habitsTask.Result.ToList();
        _goalsSnapshot = goalsTask.Result.Where(g => !g.IsArchived).ToList();

        if (_bootcamp is null)
        {
            RaiseAll();
            return;
        }

        // Which day the user is looking at survives a reload (enroll / complete / language switch)
        // unless it no longer exists — the calendar then falls back to today.
        int keep = _selectedDay;
        BuildAdaptationAndProgress(keep);
        RaiseAll();
    }

    /// <summary>
    /// The one place today's adaptation is computed. Runs the SAME ProgramAdapter the recommendation
    /// engine uses, so the "why" line and the plan can never disagree, and prints nothing when no
    /// rule fired (an unadapted day is not an event).
    /// </summary>
    void BuildAdaptationAndProgress(int keepSelected)
    {
        var b = _bootcamp!;
        // Not enrolled yet? The card previews day 1 — the same template the program really starts
        // with, labeled as a preview below. Adaptation only ever runs on an enrolled program.
        int pointer = b.IsEnrolled ? b.CurrentDay : 1;
        BootcampDay? planned = BootcampDayDisplay.BuildView(b).FirstOrDefault(d => d.DayNumber == pointer);
        BootcampDay? display = planned;
        string? ruleKey = null;
        if (b.IsEnrolled && planned is not null && _state is not null)
        {
            var (day, key) = _adapter.AdaptDay(b, planned, _state, _session.CurrentProfile,
                _goalsSnapshot, _habitsSnapshot, DateTime.Now);
            display = day;
            ruleKey = key;
        }
        TodayPlan = display;
        AdaptationRuleKey = ruleKey;

        _progress = BootcampProgress.Compute(b, DateTime.Today, display);

        int selected = keepSelected > 0 && _progress.Cell(keepSelected) is not null
            ? keepSelected
            : Math.Max(_progress.CurrentDay, 1);
        ShowDay(selected);
    }

    void ShowDay(int dayNumber)
    {
        _selectedDay = dayNumber;
        var cell = _progress.Cell(dayNumber);
        SelectedDayTitleText = cell is null ? string.Empty : L(cell.PlanTitleKey);
        SelectedDayDetailText = cell is null || string.IsNullOrWhiteSpace(cell.PlanDescriptionKey)
            ? string.Empty : L(cell.PlanDescriptionKey);
        SelectedDayMinutesText = cell is null ? string.Empty : L("Bootcamp.Minutes", _format.Number(cell.TargetMinutes));
        SelectedDayStatusText = cell is null ? string.Empty : L(cell.StatusKey);
        SelectedDayLabel = L("Common.DayXofY", _format.Number(dayNumber), _format.Number(Math.Max(_progress.TotalDays, dayNumber)));
        foreach (var c in Days) c.IsSelected = c.DayNumber == dayNumber;
        Raise(nameof(SelectedDayTitleText));
        Raise(nameof(SelectedDayDetailText));
        Raise(nameof(SelectedDayMinutesText));
        Raise(nameof(SelectedDayStatusText));
        Raise(nameof(SelectedDayLabel));
    }

    /// <summary>Taps from the calendar: shows that day in the plan card without changing the pointer.</summary>
    public void SelectDay(int dayNumber) => ShowDay(dayNumber);

    void RaiseAll()
    {
        var b = _bootcamp;
        var p = _progress;
        TitleText = b is null ? L("Bootcamp.NotFound") : L(b.TitleKey);
        DescriptionText = b is null ? string.Empty : L(b.DescriptionKey);
        CategoryText = b is null ? string.Empty : L("Enum.BootcampCategory." + b.Category);
        DifficultyText = b is null ? string.Empty : L("Enum.BootcampDifficulty." + b.Difficulty);
        DurationText = b is null ? string.Empty : L("Bootcamp.Length", _format.Number(b.DurationDays));
        CreatorText = b is null ? string.Empty : L("Programs.Creator", b.CreatorName);
        StatusText = L(p.StatusKey);
        ProgressFraction = p.CompletionFraction;
        ProgressPercentText = _format.Percent(p.CompletionFraction);
        CompletedText = L("Bootcamp.Progress.CompletedDays", _format.Number(p.CompletedDays), _format.Number(p.TotalDays));
        StreakText = L("Common.StreakDays", _format.Number(p.StreakDays));
        HasStreak = p.StreakDays > 0;
        RemainingText = L("Bootcamp.Progress.RemainingDays", _format.Number(p.DaysRemaining));
        AdaptedText = L("Bootcamp.Progress.AdaptedDays", _format.Number(p.AdaptedDays));
        MinutesText = p.PlannedMinutesRemaining > 0
            ? _format.DurationFromMinutes(p.PlannedMinutesRemaining) : L("Bootcamp.Progress.AllPlanned");
        MilestoneText = p.NextMilestoneDay > 0
            ? L("Bootcamp.Progress.NextMilestone", _format.Number(p.NextMilestoneDay), _format.Percent(p.NextMilestonePercent / 100.0))
            : string.Empty;
        // Honest projection: only when the user's own pace gives evidence (>= 1 played day and a
        // non-zero pace). Before that we say we have no estimate instead of extrapolating.
        FinishEstimateText = p.Finished
            ? L("Bootcamp.Progress.Finished")
            : p.ProjectedFinishDate is { } when_ && p.DaysPlayed > 0 && p.PacePerDay > 0
                ? L("Bootcamp.Progress.FinishDate", _format.LongDate(when_))
                : L("Bootcamp.Progress.NoEstimate");
        DayPointerText = b is { IsEnrolled: true } && p.TotalDays > 0
            ? L("Common.DayXofY", _format.Number(Math.Max(p.CurrentDay, 1)), _format.Number(p.TotalDays))
            : DurationText;

        TodayTitleText = TodayPlan is null ? L("Bootcamp.Today.None") : L(TodayPlan.PlanTitleKey);
        TodayDetailText = TodayPlan?.PlanDescriptionKey is null or "" ? string.Empty : L(TodayPlan.PlanDescriptionKey);
        TodayMinutesText = TodayPlan is null ? string.Empty : L("Bootcamp.Minutes", _format.Number(TodayPlan.TargetMinutes));
        AdaptationReasonText = WasAdapted && AdaptationRuleKey is not null
            ? L("Plan.Adapted.Reason", L("Rule.Why." + AdaptationRuleKey)) : string.Empty;
        OriginalPlanText = WasAdapted ? L(BootcampDayDisplay.OriginalPlanKey(_bootcamp!, _progress.CurrentDay)) : string.Empty;

        RebuildCalendar();
        ((Command)CompleteDayCommand).ChangeCanExecute();

        foreach (var n in new[]
        {
            nameof(HasProgram), nameof(IsEnrolled), nameof(NotEnrolled), nameof(Finished),
            nameof(ShowEmptyToday), nameof(HasTodayPlan), nameof(IsPreview), nameof(PreviewLabel),
            nameof(TitleText), nameof(DescriptionText), nameof(CategoryText),
            nameof(DifficultyText), nameof(DurationText), nameof(CreatorText), nameof(StatusText),
            nameof(ProgressFraction), nameof(ProgressPercentText), nameof(CompletedText), nameof(StreakText),
            nameof(HasStreak), nameof(RemainingText), nameof(AdaptedText), nameof(ShowAdaptedStat),
            nameof(MinutesText), nameof(MilestoneText), nameof(ShowMilestone), nameof(FinishEstimateText),

            nameof(DayPointerText), nameof(TodayTitleText), nameof(TodayDetailText), nameof(TodayMinutesText),
            nameof(WasAdapted), nameof(AdaptationReasonText), nameof(OriginalPlanText), nameof(ShowOriginalPlan),
            nameof(OverviewLabel), nameof(CalendarLabel), nameof(PlanForTodayLabel), nameof(AdaptedBadge),
            nameof(OriginalPlanLabel), nameof(CompletedLabel), nameof(StreakLabel),
            nameof(RemainingLabel), nameof(AdaptedLabel), nameof(MinutesLabel),            nameof(SampleNoteText), nameof(EnrollText), nameof(LeaveText), nameof(CompleteDayText),
            nameof(BackText), nameof(NotFoundText),
        }) Raise(n);
    }

    void RebuildCalendar()
    {
        Days.Clear();
        foreach (var cell in _progress.Cells)
        {
            Days.Add(new BootcampDayCellViewModel(this, cell.State, cell.DayNumber,
                _format.Number(cell.DayNumber), L(cell.PlanTitleKey),
                L("Bootcamp.Minutes", _format.Number(cell.TargetMinutes)),
                L(cell.StatusKey), cell.WasAdapted));
        }
        if (_selectedDay > 0 && _progress.Cell(_selectedDay) is not null)
        {
            foreach (var c in Days) c.IsSelected = c.DayNumber == _selectedDay;
        }
        Raise(nameof(Days));
    }

    protected override void OnLanguageChanged()
    {
        if (_bootcamp is null) { RaiseAll(); return; }
        BuildAdaptationAndProgress(_selectedDay);
        RaiseAll();
    }

    // ---- Mutations go through Application (BootcampProgress) — the same seam ProgramsViewModel uses. ----

    private async Task EnrollAsync()
    {
        var b = _bootcamp;
        if (b is null || b.IsEnrolled) return;
        BootcampProgress.Enroll(b);
        await _repo.SaveAsync(b);
        BuildAdaptationAndProgress(0);
        RaiseAll();
    }

    private async Task LeaveAsync()
    {
        var b = _bootcamp;
        if (b is null || !b.IsEnrolled) return;
        BootcampProgress.Leave(b);
        await _repo.SaveAsync(b);
        BuildAdaptationAndProgress(_selectedDay);
        RaiseAll();
    }

    private async Task CompleteDayAsync()
    {
        var b = _bootcamp;
        if (b is null) return;
        int closed = BootcampProgress.CompleteToday(b, WasAdapted);
        await _repo.SaveAsync(b);
        if (closed > 0)
            await BootcampJournal.LogDayAsync(_history, b, closed, WasAdapted, DateTime.Today);
        // After the pointer moves, follow it — the card should show the next open day.
        BuildAdaptationAndProgress(0);
        RaiseAll();
    }
}
