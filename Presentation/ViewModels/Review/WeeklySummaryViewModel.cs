using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Insights;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;

namespace LIVORA.Presentation;
public sealed class TrendLineViewModel
{
    public required string DomainText { get; init; }
    public required string TrendText { get; init; }
    public required string TrendColorKey { get; init; }
}

/// <summary>One WeekProgress-style bar: a labelled value plus the honest sample size behind it.
/// Built by the VM from persisted history so the Review page never renders a zeroed chart out of
/// an empty window.</summary>
public sealed class WeeklyBarViewModel
{
    public required string Label { get; init; }
    public required string ValueText { get; init; }
    /// <summary>0..1 bar length. With HasValue=false the row renders "no data" instead of a bar.</summary>
    public double Fraction { get; init; }
    public bool HasValue { get; init; }
    public required string ColorKey { get; init; }
    /// <summary>e.g. "from 5 of 7 days" — the sample size the bar is based on.</summary>
    public required string Note { get; init; }
    public bool HasNote => Note.Length > 0;
}

/// <summary>
/// Weekly review (GitHub issue #1): reachable from Today (quick action + the existing modal
/// button) and from Profile (lane 06's entry, route `review`), with week navigation — this week /
/// last week / older — and an honest insufficient-data state.
///
/// Division of labour, deliberately strict:
/// <list type="bullet">
///   <item>The narrative (trend arrows, improvement/decline bullets, next focus) comes ONLY from
///     <see cref="IWeeklySummaryService.BuildLastWeekAsync"/> — the frozen service that returns
///     null below 3 days of history. This VM never reimplements it and never shows its arrows for
///     a window the service did not approve.</item>
///   <item>The bars are computed here from <see cref="IHistoryRepository"/> for whichever week is
///     selected, each labeled with its real day count, and the whole page falls back to the empty
///     state below the same 3-day floor the service uses.</item>
///   <item>The in-progress week is labeled as in-progress: what was logged so far, no verdict.</item>
/// </list>
/// </summary>
public sealed class WeeklySummaryViewModel : ObservableObject
{
    /// <summary>Same floor WeeklySummaryService uses — one number, shared by both views of the week.</summary>
    public const int MinHonestDays = 3;
    /// <summary>How far back navigation may step (history retains 120 days upstream, so 4 weeks is honest).</summary>
    public const int MaxWeeksBack = 4;

    private readonly IWeeklySummaryService _weekly;
    private readonly IFormatService _format;
    private readonly ILocalizationService _loc;
    private readonly IHistoryRepository _history;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly IDateTimeProvider _clock;

    /// <summary>-1 = the closed week the service describes; 0 = this (in-progress) week;
    /// -2..-4 = older closed weeks.</summary>
    private int _weekOffset = -1;

    public WeeklySummaryViewModel(
        IWeeklySummaryService weekly,
        IFormatService format,
        ILocalizationService loc,
        IHistoryRepository history,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        IDateTimeProvider clock)
    {
        _weekly = weekly;
        _format = format;
        _loc = loc;
        _history = history;
        _habits = habits;
        _goals = goals;
        _clock = clock;
        SubscribeLanguage();
        RefreshCommand = new Command(async () => await LoadAsync());
        CloseCommand = new Command(() => CloseRequested?.Invoke());
        PreviousWeekCommand = new Command(async () =>
        {
            if (!CanGoOlder) return;
            _weekOffset--;
            await LoadAsync();
        });
        NextWeekCommand = new Command(async () =>
        {
            if (!CanGoNewer) return;
            _weekOffset++;
            await LoadAsync();
        });
        ThisWeekCommand = new Command(async () => { await SelectWeekAsync(0); });
        LastWeekCommand = new Command(async () => { await SelectWeekAsync(-1); });
    }

    public ICommand RefreshCommand { get; }

    public string Title => L("WeeklySummary.Title");
    public string Subtitle { get; private set; } = string.Empty;
    public string ImprovementsHeader => L("WeeklySummary.Improvements");
    public string DeclinesHeader => L("WeeklySummary.Declines");
    public string HabitsHeader => L("WeeklySummary.Habits");
    public string GoalsHeader => L("WeeklySummary.Goals");
    public string NextFocusHeader => L("WeeklySummary.NextFocus");
    public string ConfidenceText { get; private set; } = string.Empty;
    public string EmptyText => L("WeeklySummary.Empty");
    public string BackText => L("Weekly.Summary.Back");

    // ---- Wave 3: navigation + honest states ----
    public string ThisWeekLabel => L("WeeklySummary.Tab.ThisWeek");
    public string LastWeekLabel => L("WeeklySummary.Tab.LastWeek");
    public string BarsHeader => L("WeeklySummary.Bars");
    public string TrendsHeader => L("WeeklySummary.Trends");
    public string OlderHint => L("WeeklySummary.OlderHint");
    public string DaysCountText { get; private set; } = string.Empty;
    public string WeekRangeText { get; private set; } = string.Empty;
    /// <summary>Honest in-progress marker for the current week — its own label, no glue chars.</summary>
    public string InProgressNote => IsCurrentWeek ? L("WeeklySummary.InProgress") : string.Empty;
    public bool IsCurrentWeek => _weekOffset == 0;
    public bool CanGoOlder => _weekOffset > -MaxWeeksBack;
    public bool CanGoNewer => _weekOffset < 0;
    public bool HasNewer => _weekOffset < 0;
    public ICommand PreviousWeekCommand { get; }
    public ICommand NextWeekCommand { get; }
    public ICommand ThisWeekCommand { get; }
    public ICommand LastWeekCommand { get; }
    public ObservableCollection<WeeklyBarViewModel> Bars { get; } = new();
    public bool HasBars => Bars.Count > 0;

    private bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set { Set(ref _hasSummary, value); Raise(nameof(IsEmpty)); Raise(nameof(HasNarrative)); } }
    public bool IsEmpty => !_hasSummary && !HasBars;
    /// <summary>Narrative block only ever renders for the closed week the service approved.</summary>
    public bool HasNarrative => HasSummary;

    public List<TrendLineViewModel> Trends { get; private set; } = new();
    public List<string> Improvements { get; private set; } = new();
    public List<string> Declines { get; private set; } = new();
    public bool HasImprovements => Improvements.Count > 0;
    public bool HasDeclines => Declines.Count > 0;
    public string HabitConsistencyText { get; private set; } = string.Empty;
    public string GoalProgressText { get; private set; } = string.Empty;
    public string NextFocusText { get; private set; } = string.Empty;

    public event Action? CloseRequested;
    /// <summary>
    /// Single instance. It used to construct a fresh Command on every get, and a bound element (plus
    /// every CanExecute/Execute probe) paid an allocation each time it read the property.
    /// </summary>
    public ICommand CloseCommand { get; }

    private async Task SelectWeekAsync(int offset)
    {
        _weekOffset = offset;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        var (start, end) = ResolveWindow();
        var all = await _history.GetAllAsync();
        var records = all.Where(r => r.Date.Date >= start && r.Date.Date <= end).OrderBy(r => r.Date).ToList();
        int days = records.Count;

        Bars.Clear();
        if (days >= MinHonestDays)
            foreach (var bar in await BuildBarsAsync(records, start, end, days)) Bars.Add(bar);

        WeekRangeText = _format.ShortDate(start) + " – " + _format.ShortDate(end);
        DaysCountText = L("WeeklySummary.DaysCount", _format.Number(days), _format.Number(DaySpan(start, end)));

        // Narrative: only the service may write it, and only about its own window.
        Trends = new List<TrendLineViewModel>();
        Improvements = new List<string>();
        Declines = new List<string>();
        HabitConsistencyText = string.Empty;
        GoalProgressText = string.Empty;
        NextFocusText = string.Empty;
        ConfidenceText = string.Empty;
        bool narrative = false;
        Subtitle = WeekRangeText;

        if (_weekOffset == -1)
        {
            var summary = await _weekly.BuildLastWeekAsync();
            if (summary is not null)
            {
                narrative = true;
                Subtitle = L("WeeklySummary.Subtitle",
                    _format.ShortDate(summary.WeekStart), _format.ShortDate(summary.WeekEnd));
                Trends = new List<TrendLineViewModel>
                {
                    Line("Health.Sleep", summary.SleepTrend),
                    Line("Health.Activity", summary.ActivityTrend),
                    Line("Health.Recovery", summary.RecoveryTrend),
                    Line("Health.Stress", summary.StressTrend),
                };
                Improvements = summary.ImprovementKeys.Select(k => _loc[k]).ToList();
                Declines = summary.DeclineKeys.Select(k => _loc[k]).ToList();
                HabitConsistencyText = _format.Percent(summary.HabitConsistency);
                GoalProgressText = _format.Percent(summary.GoalProgressFraction);
                NextFocusText = L(summary.FocusKey, summary.FocusArgs);
                ConfidenceText = L("WeeklySummary.Confidence", _format.Number(7));
            }
        }

        HasSummary = narrative;
        Raise(nameof(Subtitle));
        Raise(nameof(WeekRangeText));
        Raise(nameof(DaysCountText));
        Raise(nameof(InProgressNote));
        Raise(nameof(IsCurrentWeek));
        Raise(nameof(CanGoOlder));
        Raise(nameof(CanGoNewer));
        Raise(nameof(HasNewer));
        Raise(nameof(HasBars));
        Raise(nameof(HasImprovements));
        Raise(nameof(HasDeclines));
        Raise(nameof(Trends));
        Raise(nameof(Improvements));
        Raise(nameof(Declines));
        Raise(nameof(HabitConsistencyText));
        Raise(nameof(GoalProgressText));
        Raise(nameof(NextFocusText));
        Raise(nameof(ConfidenceText));
        Raise(nameof(IsEmpty));
    }

    /// <summary>The selected 7-day window. -1 is exactly the closed window the service uses
    /// (yesterday back 6 days), so the narrative and the bars underneath describe the SAME days.</summary>
    private (DateTime Start, DateTime End) ResolveWindow()
    {
        var today = _clock.Today;
        if (_weekOffset == 0) return (today.AddDays(-6), today);          // in progress
        var end = today.AddDays(-1 + 7 * (_weekOffset + 1));              // -1 -> yesterday, -2 -> 8 days ago
        return (end.AddDays(-6), end);
    }

    private static int DaySpan(DateTime start, DateTime end) => (end.Date - start.Date).Days + 1;

    /// <summary>Bars for the selected window: daily averages from persisted history, plus the
    /// habit/goal rows. A metric with no value in the window renders "no data", never a 0% bar.</summary>
    private async Task<IEnumerable<WeeklyBarViewModel>> BuildBarsAsync(
        IReadOnlyList<DailyHistoryRecord> records, DateTime start, DateTime end, int days)
    {
        var bars = new List<WeeklyBarViewModel>();

        bars.Add(AvgBar("WeeklySummary.Bars.Sleep",
            records.Where(r => r.SleepMinutes > 0).Select(r => r.SleepMinutes).ToList(),
            v => _format.Duration(v / 60.0, v % 60.0), target: 450, color: "MetricSleep", total: days));

        bars.Add(AvgBar("WeeklySummary.Bars.Activity",
            records.Where(r => r.Steps > 0).Select(r => (double)r.Steps).ToList(),
            v => _format.Number((long)v), target: 8000, color: "MetricActivity", total: days));

        bars.Add(AvgBar("WeeklySummary.Bars.ActiveMinutes",
            records.Where(r => r.ActiveMinutes > 0).Select(r => (double)r.ActiveMinutes).ToList(),
            v => _format.DurationFromMinutes((int)v), target: 30, color: "MetricActivity", total: days));

        bars.Add(AvgBar("WeeklySummary.Bars.Recovery",
            records.Where(r => r.RecoveryScore > 0).Select(r => r.RecoveryScore).ToList(),
            v => _format.Percent(v), target: 1, color: "MetricRecovery", total: days));

        bars.Add(AvgBar("WeeklySummary.Bars.Stress",
            records.Where(r => r.Stress > 0).Select(r => r.Stress).ToList(),
            v => _format.Percent(v), target: 1, color: "MetricWellness", total: days, invert: true));

        // Habit + goal rows are window state, not daily averages — and both can legitimately be
        // empty, which says "no data yet", never a fake 0%.
        var habits = await _habits.GetAllAsync();
        int habitDone = habits.Sum(h => h.Completions.Count(d => d.Date >= start && d.Date <= end));
        int habitSlots = habits.Count * DaySpan(start, end);
        bars.Add(new WeeklyBarViewModel
        {
            Label = _loc["WeeklySummary.Habits"],
            ValueText = habits.Count == 0
                ? _loc["Health.NotAvailable"]
                : L("WeeklySummary.BarHabitsDone", _format.Number(habitDone), _format.Number(habitSlots)),
            Fraction = habitSlots == 0 ? 0 : Math.Clamp((double)habitDone / habitSlots, 0, 1),
            HasValue = habits.Count > 0,
            ColorKey = "Accent",
            Note = habits.Count == 0 ? string.Empty : L("WeeklySummary.BarActiveHabits", _format.Number(habits.Count)),
        });

        var goals = (await _goals.GetAllAsync()).Where(g => !g.IsArchived).ToList();
        double goalFrac = goals.Count == 0 ? 0 : goals.Average(g => g.Fraction);
        bars.Add(new WeeklyBarViewModel
        {
            Label = _loc["WeeklySummary.Goals"],
            ValueText = goals.Count == 0 ? _loc["Health.NotAvailable"] : _format.Percent(Math.Clamp(goalFrac, 0, 1)),
            Fraction = Math.Clamp(goalFrac, 0, 1),
            HasValue = goals.Count > 0,
            ColorKey = "Accent",
            Note = goals.Count == 0 ? string.Empty : L("WeeklySummary.BarActiveGoals", _format.Number(goals.Count)),
        });
        return bars;
    }

    private WeeklyBarViewModel AvgBar(string key, List<double> values, Func<double, string> fmt,
        double target, string color, int total, bool invert = false)
    {
        if (values.Count == 0)
            return new WeeklyBarViewModel
            {
                Label = _loc[key],
                ValueText = _loc["Health.NotAvailable"],
                Fraction = 0,
                HasValue = false,
                ColorKey = color,
                Note = L("WeeklySummary.BarDays", _format.Number(0), _format.Number(total)),
            };
        double avg = values.Average();
        double frac = Math.Clamp(avg / Math.Max(target, 1e-6), 0, 1);
        if (invert) frac = 1 - frac;   // stress reads better as headroom: low stress => long bar
        return new WeeklyBarViewModel
        {
            Label = _loc[key],
            ValueText = fmt(avg),
            Fraction = frac,
            HasValue = true,
            ColorKey = color,
            Note = L("WeeklySummary.BarDays", _format.Number(values.Count), _format.Number(total)),
        };
    }

    private TrendLineViewModel Line(string domainKey, TrendDirection t) => new()
    {
        DomainText = _loc[domainKey],
        TrendText = t switch
        {
            TrendDirection.Improving => _loc["Health.Trend.Above"],
            TrendDirection.Declining => _loc["Health.Trend.Below"],
            TrendDirection.Stable => _loc["Health.Trend.Normal"],
            _ => _loc["Health.Trend.Learning"],
        },
        TrendColorKey = t switch
        {
            TrendDirection.Improving => "Positive",
            TrendDirection.Declining => "Caution",
            _ => "Accent",
        },
    };

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(ImprovementsHeader));
        Raise(nameof(DeclinesHeader));
        Raise(nameof(HabitsHeader));
        Raise(nameof(GoalsHeader));
        Raise(nameof(NextFocusHeader));
        Raise(nameof(EmptyText));
        Raise(nameof(BackText));
        Raise(nameof(BarsHeader));
        Raise(nameof(TrendsHeader));
        Raise(nameof(OlderHint));
        Raise(nameof(ThisWeekLabel));
        Raise(nameof(LastWeekLabel));
        _ = LoadAsync();
    }
}
