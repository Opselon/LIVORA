using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.State;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;
public sealed class BootcampItemViewModel
{
    public required Bootcamp Bootcamp { get; init; }
    public required string TitleText { get; init; }
    public required string DescriptionText { get; init; }
    public required string CategoryText { get; init; }
    public required string DifficultyText { get; init; }
    public required string ProgressText { get; init; }
    public required string DayText { get; init; }
    public required string TodayPlanText { get; init; }
    public required string TodayPlanDetail { get; init; }
    public required bool WasAdapted { get; init; }
    /// <summary>Why today's day changed (structured rule -> localized sentence).</summary>
    public required string AdaptationReason { get; init; }
    public required string CreatorText { get; init; }
}

public sealed class ProgramsViewModel : ObservableObject
{
    private readonly IRepository<Bootcamp> _repo;
    private readonly IFormatService _format;
    private readonly ILocalizationService _loc;
    private readonly ProgramAdapter _adapter;
    private readonly IUserStateService _stateService;
    private readonly IRuleEngine _rules;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;

    public ProgramsViewModel(
        IRepository<Bootcamp> repo,
        IFormatService format,
        ILocalizationService loc,
        ProgramAdapter adapter,
        IUserStateService stateService,
        IRuleEngine rules,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        IHistoryRepository history,
        SessionState session)
    {
        _repo = repo;
        _format = format;
        _loc = loc;
        _adapter = adapter;
        _stateService = stateService;
        _rules = rules;
        _habits = habits;
        _goals = goals;
        _history = history;
        _session = session;
        SubscribeLanguage();
        LoadCommand = new Command(async () => await LoadAsync());
        EnrollCommand = new Command<BootcampItemViewModel>(async b => await EnrollAsync(b));
        LeaveCommand = new Command<BootcampItemViewModel>(async b => await LeaveAsync(b));
        CompleteDayCommand = new Command<BootcampItemViewModel>(async b => await CompleteDayAsync(b));
    }

    public ICommand LoadCommand { get; }
    public ICommand EnrollCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand CompleteDayCommand { get; }

    public string Title => L("Programs.Title");
    public string AdaptiveNote => L("Programs.AdaptiveNote");
    public string EnrolledTitle => L("Programs.Enrolled");
    public string AvailableTitle => L("Programs.Available");
    public string EnrollText => L("Programs.Enroll");
    public string LeaveText => L("Programs.Leave");
    public string PlanForTodayText => L("Programs.PlanForToday");
    public string AdjustedPlanText => L("Programs.AdjustedPlan");
    public string AdaptedNoteText => L("Programs.AdaptedToday");
    public string CompleteDayText => L("Common.Complete");

    public ObservableCollection<BootcampItemViewModel> Enrolled { get; } = new();
    public ObservableCollection<BootcampItemViewModel> Available { get; } = new();
    public bool HasEnrolled => Enrolled.Count > 0;

    public async Task LoadAsync()
    {
        // Bootcamps, the derived state and the habit/goal snapshots are independent reads, so they
        // are started together and awaited as one group: with the Phase 2 synchronous store this is
        // readability, with an async store it overlaps four round-trips that used to serialize.
        var profile = _session.CurrentProfile;
        var allTask = _repo.GetAllAsync();
        var stateTask = _stateService.GetStateAsync(DataRefreshMode.Resume);
        var habitsTask = _habits.GetAllAsync();
        var goalsTask = _goals.GetAllAsync();
        await Task.WhenAll(allTask, stateTask, habitsTask, goalsTask);

        var all = allTask.Result;
        var state = stateTask.Result;
        var habits = habitsTask.Result;
        var goals = goalsTask.Result.Where(g => !g.IsArchived).ToList();

        Enrolled.Clear();
        Available.Clear();
        foreach (var b in all)
            (b.IsEnrolled ? Enrolled : Available).Add(ToItem(b, state, profile, goals, habits));
        Raise(nameof(HasEnrolled));
    }

    // Was async with zero awaits inside: each call allocated a state machine + Task that the caller
    // then awaited. The rule-engine work it triggers is synchronous, so it is now a plain method.
    private BootcampItemViewModel ToItem(Bootcamp b, Domain.Models.State.PersonalState state,
        UserProfile profile, IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits)
    {
        var planned = b.Today;
        var display = planned;
        string adaptationReason = string.Empty;
        bool adapted = false;
        if (b.IsEnrolled && planned is not null)
        {
            var (day, ruleKey) = _adapter.AdaptDay(b, planned, state, profile, goals, habits, DateTime.Now);
            display = day;
            adapted = day.IsAdapted;
            if (ruleKey is not null)
                adaptationReason = L("Plan.Adapted.Reason", L("Rule.Why." + ruleKey));
        }

        return new BootcampItemViewModel
        {
            Bootcamp = b,
            TitleText = L(b.TitleKey),
            DescriptionText = L(b.DescriptionKey),
            CategoryText = L("Enum.BootcampCategory." + b.Category),
            DifficultyText = L("Enum.BootcampDifficulty." + b.Difficulty),
            ProgressText = _format.Percent(b.CompletionFraction),
            DayText = b.IsEnrolled
                ? L("Common.DayXofY", _format.Number(Math.Max(b.CurrentDay, 1)), _format.Number(b.DurationDays))
                : $"{_format.Number(b.DurationDays)} · " + L("Enum.BootcampDifficulty." + b.Difficulty),
            TodayPlanText = display is null ? string.Empty : L(display.PlanTitleKey),
            TodayPlanDetail = display?.PlanDescriptionKey is null ? string.Empty : L(display.PlanDescriptionKey),
            WasAdapted = adapted,
            AdaptationReason = adaptationReason,
            CreatorText = L("Programs.Creator", b.CreatorName),
        };
    }

    private async Task EnrollAsync(BootcampItemViewModel item)
    {
        var b = item.Bootcamp;
        b.IsEnrolled = true;
        if (b.CurrentDay < 1) b.CurrentDay = 1;
        b.Days = EnsureDays(b);
        await _repo.SaveAsync(b);
        await LoadAsync();
    }

    private async Task LeaveAsync(BootcampItemViewModel item)
    {
        var b = item.Bootcamp;
        b.IsEnrolled = false;
        await _repo.SaveAsync(b);
        await LoadAsync();
    }

    private async Task CompleteDayAsync(BootcampItemViewModel item)
    {
        var b = item.Bootcamp;
        var today = DateTime.Today;
        if (b.CurrentDay >= 1 && b.CurrentDay <= b.Days.Count)
            b.Days[b.CurrentDay - 1].IsCompleted = true;
        b.CurrentDay = Math.Min(b.CurrentDay + 1, b.DurationDays);
        b.WasAdaptedToday = item.WasAdapted;
        await _repo.SaveAsync(b);

        var rec = (await _history.GetAllAsync()).FirstOrDefault(r => r.Date == today);
        if (rec is not null)
        {
            rec.BootcampId = b.Id;
            rec.BootcampDayNumber = Math.Max(b.CurrentDay - 1, 1);
            rec.BootcampDayWasAdapted = item.WasAdapted;
            await _history.UpsertAsync(rec);
        }
        await LoadAsync();
    }

    private static List<BootcampDay> EnsureDays(Bootcamp b)
    {
        if (b.Days.Count > 0) return b.Days;
        return Enumerable.Range(1, b.DurationDays).Select(i => new BootcampDay
        {
            DayNumber = i,
            PlanTitleKey = "Bootcamp.Plan.Walk",
            PlanDescriptionKey = "Bootcamp.Plan.Walk.Desc",
            TargetMinutes = 20,
        }).ToList();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(AdaptiveNote));
        Raise(nameof(EnrolledTitle));
        Raise(nameof(AvailableTitle));
        Raise(nameof(EnrollText));
        Raise(nameof(LeaveText));
        Raise(nameof(PlanForTodayText));
        Raise(nameof(AdjustedPlanText));
        Raise(nameof(AdaptedNoteText));
        Raise(nameof(CompleteDayText));
        _ = LoadAsync();
    }
}
