using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;

namespace LIVORA.Presentation;
public sealed class TrendLineViewModel
{
    public required string DomainText { get; init; }
    public required string TrendText { get; init; }
    public required string TrendColorKey { get; init; }
}

/// <summary>Weekly review screen: consumes WeeklySummaryService, localizes trend keys.</summary>
public sealed class WeeklySummaryViewModel : ObservableObject
{
    private readonly IWeeklySummaryService _weekly;
    private readonly IFormatService _format;
    private readonly ILocalizationService _loc;

    public WeeklySummaryViewModel(IWeeklySummaryService weekly, IFormatService format, ILocalizationService loc)
    {
        _weekly = weekly;
        _format = format;
        _loc = loc;
        SubscribeLanguage();
        RefreshCommand = new Command(async () => await LoadAsync());
        CloseCommand = new Command(() => CloseRequested?.Invoke());
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

    private bool _hasSummary;
    public bool HasSummary { get => _hasSummary; private set { Set(ref _hasSummary, value); Raise(nameof(IsEmpty)); } }
    public bool IsEmpty => !_hasSummary;

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

    public async Task LoadAsync()
    {
        var summary = await _weekly.BuildLastWeekAsync();
        if (summary is null)
        {
            HasSummary = false;
            return;
        }
        HasSummary = true;
        Subtitle = L("WeeklySummary.Subtitle", _format.ShortDate(summary.WeekStart), _format.ShortDate(summary.WeekEnd));

        Trends = new List<TrendLineViewModel>
        {
            Line("Health.Sleep", summary.SleepTrend),
            Line("Health.Activity", summary.ActivityTrend),
            Line("Health.Recovery", summary.RecoveryTrend),
            Line("Health.Stress", summary.StressTrend),
        };
        Improvements = summary.ImprovementKeys.Select(k => _loc[k]).ToList();
        Declines = summary.DeclineKeys.Select(k => _loc[k]).ToList();
        Raise(nameof(HasImprovements));
        Raise(nameof(HasDeclines));
        HabitConsistencyText = _format.Percent(summary.HabitConsistency);
        GoalProgressText = _format.Percent(summary.GoalProgressFraction);
        NextFocusText = L(summary.FocusKey, summary.FocusArgs);
        ConfidenceText = L("WeeklySummary.Confidence", _format.Number(7));

        Raise(nameof(Subtitle));
        Raise(nameof(Trends));
        Raise(nameof(Improvements));
        Raise(nameof(Declines));
        Raise(nameof(HabitConsistencyText));
        Raise(nameof(GoalProgressText));
        Raise(nameof(NextFocusText));
        Raise(nameof(ConfidenceText));
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
        _ = LoadAsync();
    }
}
