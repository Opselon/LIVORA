using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Insights;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using LIVORA.Domain.Models.State;
using LIVORA.Infrastructure.Persistence;
using StateMetrics = LIVORA.Domain.Models.State.Metrics;

namespace LIVORA.Presentation;

/// <summary>Presentation item for a recommendation: text + structured explanation (why / benefit /
/// confidence come from the engine's ExplainKey / ExpectedBenefitKey / Confidence — never invented
/// prose), plus the Wave 3 expand + snooze behaviour.</summary>
public sealed class RecommendationItemViewModel : ObservableObject
{
    /// <summary>Stable identity across reloads (rules are deterministic, Guids are not):
    /// producing rule + text key. This is what the snooze store persists.</summary>
    public required string StableId { get; init; }
    public required string Text { get; init; }
    public required string Explanation { get; init; }
    public required string Benefit { get; init; }
    public required RecommendationPriority Priority { get; init; }
    public int DurationMinutes { get; init; }
    public required string DurationText { get; init; }
    public double Confidence { get; init; }
    // Raw percent digits (machine format): the localized label lives in ConfidenceLabel.
    public string ConfidencePercentText => ((int)Math.Round(Confidence * 100)) + "%";
    public required string ConfidenceLabel { get; init; }   // "Confidence: 68%" localized
    public required string WhyLabel { get; init; }
    public required string BenefitLabel { get; init; }
    public required string SnoozeLabel { get; init; }
    public required string ExpandHint { get; init; }
    public bool HasDetail => Explanation.Length > 0 || Benefit.Length > 0;

    private bool _isExpanded;
    public bool IsExpanded => _isExpanded;
    /// <summary>Detail lines only render when the user opens the card (Wave 3: tappable why).</summary>
    public bool ShowDetail => IsExpanded && HasDetail;
    public string ExpandChevron => IsExpanded ? "⌃" : "⌄";

    /// <summary>Assigned by the VM right after construction, closing over THIS item — so a tap
    /// on one card can never expand another (and XAML needs no CommandParameter plumbing).</summary>
    public ICommand? ToggleCommand { get; set; }
    public ICommand? SnoozeCommand { get; set; }

    /// <summary>Card tap entry point (the command lives on THIS item, so expansion can't leak
    /// into the parent VM's state and the setter stays private to the item).</summary>
    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        Raise(nameof(IsExpanded));
        Raise(nameof(ShowDetail));
        Raise(nameof(ExpandChevron));
    }
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

/// <summary>One Today quick action: localized label + the route it opens. Navigation itself is
/// defensive — an unregistered route (a lane whose shell line hasn't landed) reports an honest
/// "not available yet" line instead of a silent no-op or a crash.</summary>
public sealed class QuickActionViewModel
{
    public required string Label { get; init; }
    public required string Route { get; init; }
    public required string Icon { get; init; }
    public ICommand? Command { get; init; }
}

/// <summary>
/// Wave 2/3 Today: pure coordinator. PersonalState -> DailyPlan -> Recommendations -> Insight,
/// all through services; the VM only localizes and shapes presentation. Wave 3 adds:
/// pull-to-refresh, the "log today" nudge (manual-entry check through IManualEntryService),
/// a quick-actions row, expandable/snoozable recommendation cards, and honest inline empty
/// states so a section with nothing never just disappears.
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
    private readonly IManualEntryService _manual;
    private readonly ISnoozeStore _snoozes;
    private PersonalState? _state;
    private int _hiddenBySnooze;

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
        SessionState session,
        IManualEntryService manual,
        ISnoozeStore snoozes)
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
        _manual = manual;
        _snoozes = snoozes;
        SubscribeLanguage();
        ToggleHabitCommand = new Command<HabitRowViewModel>(async row => await ToggleHabitAsync(row));
        ReviewWeekCommand = new Command(async () =>
        {
            if (OpenWeeklyReview is { } open) await open();
        });
        RefreshCommand = new Command(async () =>
        {
            try { await LoadAsync(DataRefreshMode.ManualRefresh); }
            finally { IsRefreshing = false; }
        });
        GoalsTabCommand = new Command(() => GoSafe("//Goals", "Goals.Title"));
        ShowSnoozedAgainCommand = new Command(async () =>
        {
            await _snoozes.ClearAllAsync();
            await LoadAsync(DataRefreshMode.ManualRefresh);
        });
        RebuildQuickActions();
    }

    public ICommand ToggleHabitCommand { get; }
    public ICommand ReviewWeekCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand GoalsTabCommand { get; }
    public ICommand ShowSnoozedAgainCommand { get; }

    /// <summary>Set by TodayPage to open the weekly review modally.</summary>
    public Func<Task>? OpenWeeklyReview { get; set; }
    /// <summary>Set by TodayPage: navigate to a route; returns false when the route is unknown
    /// in this build (MAUI versions differ between throwing and logging-and-ignoring, so the page
    /// checks BOTH the exception and whether the location actually moved).</summary>
    public Func<string, Task<bool>>? OpenRoute { get; set; }

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

    // ---- Wave 3 Today chrome ----
    public string PullToRefreshHint => L("Today.PullToRefresh");
    public string LogNudgeText => L("Today.LogNudge.Body");
    public string LogNudgeAction => L("Today.LogNudge.Action");
    public string QuickActionsTitle => L("Today.QuickActions");
    public string EmptyRecommendations => L("Today.Empty.Recommendations");
    public string EmptyPlan => L("Today.Empty.Plan");
    public string EmptyHabits => L("Today.Empty.Habits");
    public string EmptyHabitsCta => L("Today.Empty.Habits.Cta");
    public string EmptyGoals => L("Today.Empty.Goals");
    public string EmptyGoalsCta => L("Today.Empty.Goals.Cta");
    public string HiddenBySnoozeNote => _hiddenBySnooze > 0
        ? L("Today.SnoozedHidden", _format.Number(_hiddenBySnooze)) : string.Empty;
    public string ShowSnoozedAgain => L("Today.Snoozed.ShowAgain");

    // ---- Log-today nudge (manual-entry honesty: only about USER data, never the mock feed) ----
    private bool _showLogNudge;
    public bool ShowLogNudge { get => _showLogNudge; private set => Set(ref _showLogNudge, value); }

    /// <summary>True once the manual-entry check has actually answered (nag appears from truth,
    /// never from a default).</summary>
    private bool _logCheckDone;
    public bool LogCheckDone { get => _logCheckDone; private set => Set(ref _logCheckDone, value); }

    // ---- Quick actions + defensive navigation ----
    public ObservableCollection<QuickActionViewModel> QuickActions { get; } = new();
    /// <summary>Explicit tiles (compiled bindings can't index a collection in XAML safely).</summary>
    public QuickActionViewModel LogAction { get; private set; } = null!;
    public QuickActionViewModel RemindersAction { get; private set; } = null!;
    public QuickActionViewModel UpdatesAction { get; private set; } = null!;
    public QuickActionViewModel ReviewAction { get; private set; } = null!;

    private string _routeNotice = string.Empty;
    /// <summary>Transient honest line after a quick action whose route is not available in this build.</summary>
    public string RouteNotice { get => _routeNotice; private set { Set(ref _routeNotice, value); Raise(nameof(HasRouteNotice)); } }
    public bool HasRouteNotice => RouteNotice.Length > 0;

    public ICommand LogTodayCommand => _logToday ??= new Command(() => GoSafe("log-entry", "Today.QA.Log"));
    private ICommand? _logToday;

    private void RebuildQuickActions()
    {
        LogAction = new QuickActionViewModel
        {
            Label = L("Today.QA.Log"), Route = "log-entry", Icon = "＋", Command = LogTodayCommand,
        };
        RemindersAction = new QuickActionViewModel
        {
            Label = L("Today.QA.Reminders"), Route = "reminders", Icon = "🔔",
            Command = new Command(() => GoSafe("reminders", "Today.QA.Reminders")),
        };
        UpdatesAction = new QuickActionViewModel
        {
            Label = L("Today.QA.Updates"), Route = "updates", Icon = "↑",
            Command = new Command(() => GoSafe("updates", "Today.QA.Updates")),
        };
        ReviewAction = new QuickActionViewModel
        {
            Label = L("Today.QA.Review"), Route = "review", Icon = "↺", Command = ReviewWeekCommand,
        };
        QuickActions.Clear();
        QuickActions.Add(LogAction);
        QuickActions.Add(RemindersAction);
        QuickActions.Add(UpdatesAction);
        QuickActions.Add(ReviewAction);
        Raise(nameof(LogAction));
        Raise(nameof(RemindersAction));
        Raise(nameof(UpdatesAction));
        Raise(nameof(ReviewAction));
    }

    /// <summary>Navigate or admit failure. Routes are registered by other lanes; until (or unless)
    /// a line lands, the tap must say so honestly instead of doing nothing.</summary>
    private async void GoSafe(string route, string nameKey)
    {
        RouteNotice = string.Empty;
        if (OpenRoute is not { } open)
        {
            // No host wiring at all (e.g. the page forgot to set the hook): admit it, don't lie
            // with a silent no-op.
            RouteNotice = L("Today.RouteUnavailable", L(nameKey));
            return;
        }
        bool moved;
        try { moved = await open(route); }
        catch { moved = false; }
        if (!moved) RouteNotice = L("Today.RouteUnavailable", L(nameKey));
    }

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

    /// <summary>RefreshView's own flag. Deliberately separate from IsBusy: RefreshView sets
    /// IsRefreshing=true BEFORE executing the command, and LoadAsync's IsBusy guard would then
    /// swallow the pull and leave the spinner running forever.</summary>
    private bool _isRefreshing;
    public bool IsRefreshing { get => _isRefreshing; set => Set(ref _isRefreshing, value); }

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

            // Wave 3 side checks — manual entry (nudge) and snoozes (hidden recommendations).
            var snoozedTask = SafeSnoozesAsync();
            var loggedTask = SafeLoggedTodayAsync();
            await Task.WhenAll(snoozedTask, loggedTask);
            var snoozed = await snoozedTask;
            bool loggedToday = await loggedTask;
            // Evening only (no morning nagging), and only after the real check answered above —
            // ShowLogNudge is never set from a default, so an unanswered store cannot nag.
            ShowLogNudge = !loggedToday && _clock.Now.Hour >= 19;

            DailyScore = ComputeDailyScore(state);
            InsightTitle = L(insight.TitleKey);
            InsightSummary = FormatInsight(insight);
            InsightConfidence = insight.Confidence;
            Raise(nameof(StateConfidenceLabel));
            Raise(nameof(DataCompletenessLabel));

            _hiddenBySnooze = 0;
            Recommendations.Clear();
            foreach (var r in recs)
            {
                var stable = (r.ProducedByRule ?? r.ActionKind.ToString()) + "|" + r.TextKey;
                if (snoozed.Contains(stable)) { _hiddenBySnooze++; continue; }
                var card = new RecommendationItemViewModel
                {
                    StableId = stable,
                    Text = L(r.TextKey, FormatArgs(r.TextArgs)),
                    Explanation = r.ExplainKey.Length > 0 ? L(r.ExplainKey, FormatArgs(r.ExplainArgs)) : string.Empty,
                    Benefit = r.ExpectedBenefitKey.Length > 0 ? L(r.ExpectedBenefitKey) : string.Empty,
                    Priority = r.ScoredPriority,
                    DurationMinutes = r.DurationMinutes,
                    DurationText = r.DurationMinutes > 0 ? _format.DurationFromMinutes(r.DurationMinutes) : string.Empty,
                    Confidence = r.Confidence,
                    ConfidenceLabel = L("Today.Recommend.Confidence", _format.Percent(r.Confidence)),
                    WhyLabel = L("Today.Recommend.Why"),
                    BenefitLabel = L("Today.Recommend.Benefit"),
                    SnoozeLabel = L("Today.Snooze"),
                    ExpandHint = L("Today.Recommend.TapToExpand"),
                };
                card.ToggleCommand = new Command(card.ToggleExpanded);
                card.SnoozeCommand = new Command(async () =>
                {
                    await _snoozes.SnoozeAsync(card.StableId, TimeSpan.FromHours(24));
                    Recommendations.Remove(card);
                    _hiddenBySnooze++;
                    Raise(nameof(HasRecommendations));
                    Raise(nameof(HiddenBySnoozeNote));
                });
                Recommendations.Add(card);
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
            Raise(nameof(HiddenBySnoozeNote));
        }
        finally { IsBusy = false; }
    }

    /// <summary>Snooze IO must never blank Today: on failure, nothing is hidden (fail open, the
    /// recommendations are the safer fallback than losing the section).</summary>
    private async Task<HashSet<string>> SafeSnoozesAsync()
    {
        try
        {
            var items = await _snoozes.GetActiveAsync(DateTime.UtcNow);
            return items.Select(i => i.ItemId).ToHashSet(StringComparer.Ordinal);
        }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    /// <summary>Same honesty in the other direction: if the store cannot answer, do NOT nag.</summary>
    private async Task<bool> SafeLoggedTodayAsync()
    {
        try
        {
            var entry = await _manual.GetForDayAsync(_clock.Today);
            return entry is not null && !entry.IsEmpty;
        }
        catch { return true; }
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
        Raise(nameof(PullToRefreshHint));
        Raise(nameof(LogNudgeText)); Raise(nameof(LogNudgeAction));
        Raise(nameof(QuickActionsTitle)); Raise(nameof(RouteNotice));
        Raise(nameof(EmptyRecommendations)); Raise(nameof(EmptyPlan));
        Raise(nameof(EmptyHabits)); Raise(nameof(EmptyHabitsCta));
        Raise(nameof(EmptyGoals)); Raise(nameof(EmptyGoalsCta));
        Raise(nameof(HiddenBySnoozeNote)); Raise(nameof(ShowSnoozedAgain));
        RebuildQuickActions();
        Raise(nameof(QuickActionsTitle));
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
