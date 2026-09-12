using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using LIVORA.Infrastructure.Persistence;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Presentation;

/// <summary>Presentation item for a recommendation: text + structured explanation.</summary>
public sealed class RecommendationItemViewModel
{
    public required string Text { get; init; }
    public required string Explanation { get; init; }
    public required string Benefit { get; init; }
    public required RecommendationPriority Priority { get; init; }
    public int DurationMinutes { get; init; }
    public required string DurationText { get; init; }
    public double Confidence { get; init; }
    public string ConfidencePercentText => ((int)Math.Round(Confidence * 100)) + "%";
    public bool HasDetail => Explanation.Length > 0 || Benefit.Length > 0;
}

public sealed class PlanItemViewModel
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string TimeWindow { get; init; }
    public required string CategoryText { get; init; }
    public required bool WasAdapted { get; init; }
    public required Color AccentColor { get; init; }
}

/// <summary>
/// Wave 2 Today: pure coordinator. PersonalState -> DailyPlan -> Recommendations -> Insight,
/// all through services; the VM only localizes and shapes presentation.
/// </summary>
public sealed class TodayViewModel : ObservableObject
{
    private readonly IUserStateService _stateService;
    private readonly IDailyPlanService _planService;
    private readonly IRecommendationService _recommendations;
    private readonly IIntelligenceService _intelligence;
    private readonly IFormatService _format;
    private readonly IDateTimeProvider _clock;
    private readonly IRepository<Habit> _habitRepo;
    private readonly IRepository<Goal> _goalRepo;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;
    private PersonalState? _state;

    public TodayViewModel(
        IUserStateService stateService,
        IDailyPlanService planService,
        IRecommendationService recommendations,
        IIntelligenceService intelligence,
        IFormatService format,
        IDateTimeProvider clock,
        IRepository<Habit> habitRepo,
        IRepository<Goal> goalRepo,
        IHistoryRepository history,
        SessionState session)
    {
        _stateService = stateService;
        _planService = planService;
        _recommendations = recommendations;
        _intelligence = intelligence;
        _format = format;
        _clock = clock;
        _habitRepo = habitRepo;
        _goalRepo = goalRepo;
        _history = history;
        _session = session;
        SubscribeLanguage();
        ToggleHabitCommand = new Command<HabitRowViewModel>(async row => await ToggleHabitAsync(row));
        ReviewWeekCommand = new Command(async () =>
        {
            if (OpenWeeklyReview is { } open) await open();
        });
    }

    public ICommand ToggleHabitCommand { get; }

    /// <summary>Set by TodayPage to open the weekly review modally.</summary>
    public Func<Task>? OpenWeeklyReview { get; set; }
    public ICommand ReviewWeekCommand { get; private set; }

    // ---- Static labels ----
    public string Greeting => BuildGreeting();
    public string DateLabel => _format.LongDate(_clock.Today);
    public string SampleNote => L("Today.SampleDataNote");
    public string DailyScoreTitle => L("Today.DailyScore");
    public string ConfidenceTitle => L("Today.Confidence");
    public string CompletenessTitle => L("Today.Completeness");
    public string IntelligenceTitle => L("Intelligence.CardTitle");
    public string IntelligenceBadge => L("Intelligence.MockBadge");
    public string PlanTitle => L("Today.Plan");
    public string PlanAdaptedNote => L("Today.PlanAdapted");
    public string RecommendedTitle => L("Today.Recommended");
    public string HabitsTitle => L("Today.Habits");
    public string GoalsTitle => L("Today.ActiveGoals");
    public string ReviewWeekText => L("Today.ViewWeek");

    // ---- Daily score ----
    private double _dailyScore;
    public double DailyScore
    {
        get => _dailyScore;
        private set { Set(ref _dailyScore, value); Raise(nameof(DailyScoreDisplay)); }
    }
    public string DailyScoreDisplay => _format.Percent(_dailyScore);

    // ---- Insight ----
    private string _insightTitle = string.Empty;
    public string InsightTitle { get => _insightTitle; private set => Set(ref _insightTitle, value); }

    private string _insightSummary = string.Empty;
    public string InsightSummary { get => _insightSummary; private set => Set(ref _insightSummary, value); }

    private double _insightConfidence;
    public double InsightConfidence
    {
        get => _insightConfidence;
        private set { Set(ref _insightConfidence, value); Raise(nameof(InsightConfidenceLabel)); }
    }
    public string InsightConfidenceLabel => _format.Percent(_insightConfidence);

    public string StateConfidenceLabel => _state is null ? "—" : _format.Percent(_state.Confidence);
    public string DataCompletenessLabel => _state is null ? "—" : _format.Percent(_state.DataCompleteness);

    // ---- Collections ----
    public ObservableCollection<RecommendationItemViewModel> Recommendations { get; } = new();
    public ObservableCollection<PlanItemViewModel> PlanItems { get; } = new();
    public ObservableCollection<MiniMetricViewModel> DailyMetrics { get; } = new();
    public ObservableCollection<HabitRowViewModel> Habits { get; } = new();
    public ObservableCollection<GoalItemViewModel> ActiveGoals { get; } = new();

    // ---- Flags ----
    private bool _planWasAdapted;
    public bool PlanWasAdapted { get => _planWasAdapted; private set { Set(ref _planWasAdapted, value); Raise(nameof(PlanStandardNote)); } }
    public string PlanStandardNote => PlanWasAdapted ? string.Empty : L("Plan.NotAdapted");

    public bool HasRecommendations => Recommendations.Count > 0;
    public bool HasPlan => PlanItems.Count > 0;
    public bool HasGoals => ActiveGoals.Count > 0;
    public bool HasHabits => Habits.Count > 0;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public async Task LoadAsync(DataRefreshMode mode = DataRefreshMode.InitialLoad)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var profile = _session.CurrentProfile;
            var state = _state = await _stateService.GetStateAsync(mode);
            // Two independent repositories, started together. With today's synchronous
            // JsonFileStore both tasks are already complete when they are created, so this is
            // correctness-neutral readability — the payoff lands when storage moves async and
            // the two fetches overlap instead of serializing (see review notes; no .Wait() risk:
            // Task.WhenAll is awaited, results are read only after it completes).
            var habitsTask = _habitRepo.GetAllAsync();
            var goalsTask = _goalRepo.GetAllAsync();
            await Task.WhenAll(habitsTask, goalsTask);
            var habits = habitsTask.Result;
            var goals = goalsTask.Result.Where(g => !g.IsArchived).ToList();

            var plan = await _planService.BuildPlanAsync(state, profile, goals, habits, _clock.Now);
            var recs = _recommendations.BuildRecommendations(state, profile, goals, habits, plan, _clock.Now);
            var insight = await _intelligence.GenerateDailyInsightAsync(state, profile, plan, goals, habits);

            DailyScore = ComputeDailyScore(state);
            InsightTitle = L(insight.TitleKey);
            InsightSummary = FormatInsight(insight);
            InsightConfidence = insight.Confidence;
            Raise(nameof(StateConfidenceLabel));
            Raise(nameof(DataCompletenessLabel));

            Recommendations.Clear();
            foreach (var r in recs)
            {
                Recommendations.Add(new RecommendationItemViewModel
                {
                    Text = L(r.TextKey, FormatArgs(r.TextArgs)),
                    Explanation = r.ExplainKey.Length > 0 ? L(r.ExplainKey, FormatArgs(r.ExplainArgs)) : string.Empty,
                    Benefit = r.ExpectedBenefitKey.Length > 0 ? L(r.ExpectedBenefitKey) : string.Empty,
                    Priority = r.ScoredPriority,
                    DurationMinutes = r.DurationMinutes,
                    DurationText = r.DurationMinutes > 0 ? _format.DurationFromMinutes(r.DurationMinutes) : string.Empty,
                    Confidence = r.Confidence,
                });
            }

            PlanItems.Clear();
            foreach (var p in plan.Items)
            {
                PlanItems.Add(new PlanItemViewModel
                {
                    Title = L(p.TitleKey, FormatArgs(p.DetailArgs)),
                    Detail = p.DetailKey is null ? string.Empty : L(p.DetailKey, Array.Empty<object>()),
                    TimeWindow = p.PreferredWindowStart is { } ws
                        ? $"{_format.Time(ws)} – {_format.Time(ws + TimeSpan.FromMinutes(Math.Max(p.PlannedMinutes, 30)))}"
                        : string.Empty,
                    CategoryText = L("Enum.RecommendationCategory." + p.Category),
                    WasAdapted = p.WasAdapted,
                    AccentColor = CategoryColor(p.Category),
                });
            }
            PlanWasAdapted = plan.WasAdapted;

            var m = state.Metrics;
            DailyMetrics.Clear();
            DailyMetrics.Add(MetricRow(L("Today.SleepCard"), m[StateMetrics.SleepMinutes], v => _format.Duration(v / 60.0, v % 60.0), Theme.MetricSleep));
            DailyMetrics.Add(MetricRow(L("Today.ActivityCard"), m[StateMetrics.Steps], v => _format.Number((long)v), Theme.MetricActivity));
            DailyMetrics.Add(MetricRow(L("Today.RecoveryCard"), m[StateMetrics.RecoveryScore], v => _format.Percent(v), Theme.MetricRecovery));
            DailyMetrics.Add(MetricRow(L("Today.FocusCard"), m[StateMetrics.FocusEstimate], v => _format.Percent(v), Theme.MetricWellness));

            Habits.Clear();
            foreach (var h in habits) Habits.Add(ToHabitRow(h));
            ActiveGoals.Clear();
            foreach (var g in goals.Take(3)) ActiveGoals.Add(GoalItemViewModel.FromGoal(g, _format, key => Loc[key]));

            Raise(nameof(HasRecommendations));
            Raise(nameof(HasPlan));
            Raise(nameof(HasGoals));
            Raise(nameof(HasHabits));
            Raise(nameof(PlanStandardNote));
        }
        finally { IsBusy = false; }
    }

    private MiniMetricViewModel MetricRow(string title, MetricState ms, Func<double, string> fmt, Color accent)
    {
        double frac = ms.BaselineValue is > 0 ? Math.Clamp(ms.Value / ms.BaselineValue.Value, 0, 1) : Math.Clamp(ms.Value, 0, 1);
        return new MiniMetricViewModel
        {
            Title = title,
            Value = fmt(ms.Value),
            AccentColor = accent,
            Fraction = frac,
            DeltaText = Delta(ms),
        };
    }

    /// <summary>Baseline delta chip — the Wave 2 signature: deviations vs YOUR normal.</summary>
    private string Delta(MetricState ms)
    {
        if (ms.RelativeDeviation is null || ms.BaselineConfidence == BaselineConfidence.None) return string.Empty;
        var dev = ms.RelativeDeviation.Value;
        if (Math.Abs(dev) < 0.06) return L("Today.Delta.OnBaseline");
        bool good = ms.HigherIsBetter ? dev > 0 : dev < 0;
        return L(good ? "Today.Delta.Above" : "Today.Delta.Below", _format.Percent(Math.Abs(dev)));
    }

    private string FormatInsight(DailyInsight insight)
    {
        if (insight.SummaryArgs.Length == 0) return L(insight.SummaryKey);
        return L(insight.SummaryKey, insight.SummaryArgs.Select(NormalizeArg).ToArray());
    }

    /// <summary>Numeric engine args are minutes or 0..1 scores; present them locale-aware.</summary>
    private object NormalizeArg(object arg) => arg switch
    {
        double d when d > 30 => _format.DurationFromMinutes((int)d),
        double d when d >= 0 && d <= 1 => _format.Percent(d),
        _ => arg,
    };

    private object[] FormatArgs(object[] args) => args.Select(NormalizeArg).ToArray();

    private static double ComputeDailyScore(PersonalState state)
    {
        var m = state.Metrics;
        double sleepScore = Math.Clamp(m[StateMetrics.SleepMinutes].Value / (7.5 * 60.0), 0, 1);
        double recovery = Math.Clamp(m[StateMetrics.RecoveryScore].Value, 0, 1);
        double activityBase = m[StateMetrics.Steps].BaselineValue ?? 8000;
        double activity = Math.Clamp(m[StateMetrics.Steps].Value / Math.Max(activityBase, 1), 0, 1);
        return Math.Clamp(0.4 * sleepScore + 0.35 * recovery + 0.25 * activity, 0, 1);
    }

    private static Color CategoryColor(RecommendationCategory c) => c switch
    {
        RecommendationCategory.Sleep => Theme.MetricSleep,
        RecommendationCategory.Activity => Theme.MetricActivity,
        RecommendationCategory.Recovery => Theme.MetricRecovery,
        RecommendationCategory.Stress => Theme.MetricWellness,
        RecommendationCategory.Focus => Theme.MetricWellness,
        RecommendationCategory.Habit => Theme.MetricActivity,
        RecommendationCategory.Goal => Theme.MetricSleep,
        RecommendationCategory.Program => Theme.MetricRecovery,
        _ => Theme.Accent,
    };

    private HabitRowViewModel ToHabitRow(Habit h)
    {
        var done = h.IsCompletedOn(_clock.Today);
        return new HabitRowViewModel
        {
            Habit = h,
            Name = h.Name,
            IsDoneToday = done,
            ActionText = done ? L("Habits.Undo") : L("Habits.MarkDone"),
            StreakText = L("Common.StreakDays", _format.Number(h.CurrentStreak)),
            WeekText = L("Common.ThisWeekCount", _format.Number(h.CompletionsThisWeek(_clock.Today)), _format.Number(7)),
            ToggleCommand = new Command<HabitRowViewModel>(async row => await ToggleHabitAsync(row)),
        };
    }

    private async Task ToggleHabitAsync(HabitRowViewModel row)
    {
        var h = row.Habit;
        var now = _clock.Now;
        if (h.IsCompletedOn(now.Date)) h.Uncomplete(now.Date);
        else h.Complete(now);
        await _habitRepo.SaveAsync(h);
        await _history.RecordHabitCompletionAsync(now.Date, h.Id, h.IsCompletedOn(now.Date));
        await LoadAsync(DataRefreshMode.ManualRefresh);
    }

    private string BuildGreeting()
    {
        var hour = _clock.Now.Hour;
        var key = hour < 12 ? "Today.Greeting.Morning" : hour < 18 ? "Today.Greeting.Afternoon" : "Today.Greeting.Evening";
        var name = string.IsNullOrWhiteSpace(_session.CurrentProfile.Name) ? L("Common.You") : _session.CurrentProfile.Name;
        return L(key, name);
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Greeting));
        Raise(nameof(DateLabel));
        Raise(nameof(SampleNote));
        Raise(nameof(DailyScoreTitle));
        Raise(nameof(ConfidenceTitle));
        Raise(nameof(CompletenessTitle));
        Raise(nameof(IntelligenceTitle));
        Raise(nameof(IntelligenceBadge));
        Raise(nameof(PlanTitle));
        Raise(nameof(PlanAdaptedNote));
        Raise(nameof(RecommendedTitle));
        Raise(nameof(HabitsTitle));
        Raise(nameof(GoalsTitle));
        Raise(nameof(ReviewWeekText));
        Raise(nameof(InsightConfidenceLabel));
        Raise(nameof(StateConfidenceLabel));
        Raise(nameof(DataCompletenessLabel));
        Raise(nameof(PlanStandardNote));
        _ = LoadAsync(DataRefreshMode.ManualRefresh);
    }
}

public sealed class MiniMetricViewModel
{
    public required string Title { get; init; }
    public required string Value { get; init; }
    public required Color AccentColor { get; init; }
    public double Fraction { get; init; }
    /// <summary>Baseline delta text; empty when a trustworthy baseline doesn't exist yet.</summary>
    public required string DeltaText { get; init; }
    public bool HasDelta => DeltaText.Length > 0;
}

public sealed class HabitRowViewModel
{
    public required Habit Habit { get; init; }
    public string HabitId => Habit.Id;
    public required string Name { get; init; }
    public required bool IsDoneToday { get; init; }
    public required string ActionText { get; init; }
    public required string StreakText { get; init; }
    public required string WeekText { get; init; }
    public ICommand? ToggleCommand { get; init; }
}
