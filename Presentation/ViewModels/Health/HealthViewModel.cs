using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.State;
using LIVORA.Infrastructure.Persistence;

namespace LIVORA.Presentation;
public sealed class HealthSectionRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
    public double Fraction { get; init; }
    /// <summary>Baseline delta chip; empty when a personal baseline isn't trustworthy yet.</summary>
    public required string DeltaText { get; init; }
    public bool HasDelta => DeltaText.Length > 0;
    /// <summary>True when the value is honest-history (stale/missing) rather than fresh.</summary>
    public bool IsStale { get; init; }
    /// <summary>Localized "stale" badge, resolved at row construction.</summary>
    public required string StaleBadge { get; init; }
}

/// <summary>Health overview now consumes PersonalState (derived) — never raw provider values.</summary>
public sealed class HealthViewModel : ObservableObject
{
    private readonly IUserStateService _stateService;
    private readonly IFormatService _format;
    private readonly ILocalizationService _loc;
    private PersonalState? _state;

    public HealthViewModel(IUserStateService stateService, IFormatService format, ILocalizationService loc)
    {
        _stateService = stateService;
        _format = format;
        _loc = loc;
        SubscribeLanguage();
    }

    public string Title => L("Health.Title");
    public string SleepTitle => L("Health.Sleep");
    public string ActivityTitle => L("Health.Activity");
    public string RecoveryTitle => L("Health.Recovery");
    public string WellnessTitle => L("Health.Wellness");
    public string SourceBadge => L("Health.SourceMock");
    public string PlaceholderNote => L("Health.PlaceholderNote");
    public string BaselineNote => L("Health.BaselineNote");
    public string TrendNote => L("Health.TrendNote");

    public string SleepTrendText => TrendLine("Health.Sleep", Metrics.SleepMinutes, higherIsBetter: true);
    public string ActivityTrendText => TrendLine("Health.Activity", Metrics.Steps, higherIsBetter: true);
    public string RecoveryTrendText => TrendLine("Health.Recovery", Metrics.RecoveryScore, higherIsBetter: true);

    private List<HealthSectionRow> _sleepRows = new();
    public List<HealthSectionRow> SleepRows { get => _sleepRows; private set => Set(ref _sleepRows, value); }

    private List<HealthSectionRow> _activityRows = new();
    public List<HealthSectionRow> ActivityRows { get => _activityRows; private set => Set(ref _activityRows, value); }

    private List<HealthSectionRow> _recoveryRows = new();
    public List<HealthSectionRow> RecoveryRows { get => _recoveryRows; private set => Set(ref _recoveryRows, value); }

    private List<HealthSectionRow> _wellnessRows = new();
    public List<HealthSectionRow> WellnessRows { get => _wellnessRows; private set => Set(ref _wellnessRows, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var state = _state = await _stateService.GetStateAsync(DataRefreshMode.Resume);
            var m = state.Metrics;

            SleepRows = new List<HealthSectionRow>
            {
                Row(L("Health.Duration"), m[Metrics.SleepMinutes], minutesFmt: true),
                Row(L("Health.Quality"), m[Metrics.SleepQuality]),
                Row(L("Health.Consistency"), m[Metrics.SleepConsistency]),
            };
            ActivityRows = new List<HealthSectionRow>
            {
                Row(L("Health.Steps"), m[Metrics.Steps], stepsFmt: true),
                Row(L("Health.ActiveMinutes"), m[Metrics.ActiveMinutes], minutesFmt: true),
            };
            RecoveryRows = new List<HealthSectionRow>
            {
                Row(L("Health.RecoveryScore"), m[Metrics.RecoveryScore]),
                new() { Label = L("Health.RestingHeartRate"), Value = L("Health.NotAvailable"), Fraction = 0, DeltaText = string.Empty, IsStale = false, StaleBadge = string.Empty },
                new() { Label = L("Health.HRV"), Value = L("Health.NotAvailable"), Fraction = 0, DeltaText = string.Empty, IsStale = false, StaleBadge = string.Empty },
            };
            WellnessRows = new List<HealthSectionRow>
            {
                Row(L("Health.Stress"), m[Metrics.Stress]),
                Row(L("Health.Mood"), m[Metrics.Mood]),
                Row(L("Health.Energy"), m[Metrics.Energy]),
            };
            Raise(nameof(SleepTrendText));
            Raise(nameof(ActivityTrendText));
            Raise(nameof(RecoveryTrendText));
        }
        finally { IsBusy = false; }
    }

    private HealthSectionRow Row(string label, MetricState ms, bool minutesFmt = false, bool stepsFmt = false)
    {
        string value = minutesFmt ? _format.Duration(ms.Value / 60.0, ms.Value % 60.0)
                     : stepsFmt ? _format.Number((long)ms.Value)
                     : _format.Percent(ms.Value);
        string delta = string.Empty;
        if (ms.RelativeDeviation is { } dev && ms.BaselineConfidence != BaselineConfidence.None && Math.Abs(dev) >= 0.06)
        {
            bool good = ms.HigherIsBetter ? dev > 0 : dev < 0;
            delta = L(good ? "Today.Delta.Above" : "Today.Delta.Below", _format.Percent(Math.Abs(dev)));
        }
        double frac = ms.BaselineValue is > 0 ? Math.Clamp(ms.Value / ms.BaselineValue.Value, 0, 1.4) : 0;
        return new HealthSectionRow
        {
            Label = label, Value = value, DeltaText = delta, Fraction = Math.Clamp(frac, 0, 1),
            IsStale = ms.Quality == DataQuality.Stale, StaleBadge = L("Health.StaleBadge"),
        };
    }

    private string TrendLine(string domainKey, string metricKey, bool higherIsBetter)
    {
        if (_state is null) return string.Empty;
        // Trend from baseline sample quality + today's level — kept deliberately simple & honest.
        var ms = _state.Metrics.GetValueOrDefault(metricKey);
        if (ms is null || ms.BaselineConfidence == BaselineConfidence.None) return L(domainKey) + " · " + L("Health.Trend.Learning");
        var level = ms.Level switch
        {
            StateLevel.AboveBaseline => higherIsBetter ? "Health.Trend.Above" : "Health.Trend.Below",
            StateLevel.BelowBaseline => higherIsBetter ? "Health.Trend.Below" : "Health.Trend.Above",
            _ => "Health.Trend.Normal",
        };
        return L(domainKey) + " · " + L(level);
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(SleepTitle));
        Raise(nameof(ActivityTitle));
        Raise(nameof(RecoveryTitle));
        Raise(nameof(WellnessTitle));
        Raise(nameof(SourceBadge));
        Raise(nameof(PlaceholderNote));
        Raise(nameof(BaselineNote));
        Raise(nameof(TrendNote));
        _ = LoadAsync();
    }
}
