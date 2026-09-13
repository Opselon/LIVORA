using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models.History;
using LIVORA.Domain.Models.State;

namespace LIVORA.Presentation;

// =============================================================================
// Lane 03 — Log tab: recent check-ins + the 14-night sleep chart model.
// The editor itself is a sibling (LogEntryViewModel) hosted inline via
// CheckInView; this VM owns the list, the chart model and the provenance honesty.
// =============================================================================

/// <summary>One row in the recent check-ins list. Rows are the user's OWN entries;
/// sample data never appears here (it lives in the chart, labeled as sample).</summary>
public sealed class SleepEntryRow
{
    public required DateTime Date { get; init; }
    /// <summary>Localized day label (Today/Yesterday/n-days-ago) — resolved at build time.</summary>
    public required string DayLabel { get; init; }
    /// <summary>Localized short date (Jalali + Persian digits in fa).</summary>
    public required string DateText { get; init; }
    /// <summary>Localized sleep duration, or "not set" when that night has no sleep value.</summary>
    public required string SleepText { get; init; }
    /// <summary>Localized steps, or empty when absent (the chip hides itself).</summary>
    public required string StepsText { get; init; }
    /// <summary>Row tap → edit that day in the inline editor above.</summary>
    public required ICommand EditCommand { get; init; }
    /// <summary>Trash tap → delete the manual entry after confirmation.</summary>
    public required ICommand DeleteCommand { get; init; }
    /// <summary>Delete-button wording (resolved by the shared Log string owner, so it matches the editor's).</summary>
    public required string DeleteLabel { get; init; }
}

/// <summary>
/// Log tab content: the inline daily check-in editor (hosted <c>CheckInView</c> bound to
/// <see cref="Editor"/>), the 14-night chart model, and the recent-entries list with
/// tap-to-edit + delete. Reads through the app's own seams: durable history for the series,
/// the manual store for provenance and deletes, the baseline engine for the dashed reference
/// line. Never fabricates: missing nights stay gaps, sample nights stay labeled sample.
/// </summary>
public sealed class LogViewModel : ObservableObject
{
    private const int WindowDays = 14;

    private readonly IHistoryRepository _history;
    private readonly IManualEntryService _entries;
    private readonly IBaselineService _baselines;
    private readonly IFormatService _format;
    private readonly IDateTimeProvider _clock;
    private readonly SessionState _session;
    private readonly LogEntryViewModel _editor;

    public LogViewModel(
        IHistoryRepository history,
        IManualEntryService entries,
        IBaselineService baselines,
        IFormatService format,
        IDateTimeProvider clock,
        SessionState session,
        LogEntryViewModel editor)
    {
        _history = history;
        _entries = entries;
        _baselines = baselines;
        _format = format;
        _clock = clock;
        _session = session;
        _editor = editor;
        SubscribeLanguage();

        LoadCommand = new Command(async () => await LoadAsync());
        NewEntryCommand = new Command(() => { editor.SelectDate(clock.Today); EditorFocusRequested?.Invoke(); });
        // A save/delete in the editor refreshes the list + chart immediately.
        editor.EntrySaved += () => _ = LoadAsync();
    }

    public ICommand LoadCommand { get; }
    public ICommand NewEntryCommand { get; }

    /// <summary>
    /// Raised when a row tap or the empty-state CTA moves a day into the inline editor: the Log page
    /// scrolls the editor into view. Without it, tap-to-edit on a long page looks like a dead button —
    /// the values change above the fold and nothing seems to happen.
    /// </summary>
    public event Action? EditorFocusRequested;

    /// <summary>
    /// The check-in editor hosted inline by CheckInView — the same VM type the pushed
    /// log-entry route resolves, so both flows share one implementation.
    /// </summary>
    public LogEntryViewModel Editor => _editor;

    // ---- Static labels --------------------------------------------------------

    // All wording comes from LogUiStrings so the tab and the pushed editor can never
    // show two different sentences for the same idea.
    public string Title => LogUiStrings.Title;
    public string Subtitle => LogUiStrings.Subtitle;
    public string RecentTitle => LogUiStrings.RecentTitle;
    public string ChartTitle => LogUiStrings.ChartTitle;
    public string EmptyText => LogUiStrings.EmptyText;
    public string EmptySub => LogUiStrings.EmptySub;
    public string NewEntryText => LogUiStrings.NewEntry;
    public string NoDataNote => LogUiStrings.ChartNoData;
    public string LegendManual => LogUiStrings.LegendManual;
    public string LegendMock => LogUiStrings.LegendMock;
    public string LegendBaseline => LogUiStrings.LegendBaseline;
    public string CheckInSectionTitle => LogUiStrings.CheckInTitle(LogHeadingStyle.Tab);

    // ---- Chart model -------------------------------------------------------------

    private SleepChartModel _chart = SleepChartModel.Empty(string.Empty);
    public SleepChartModel Chart { get => _chart; private set => Set(ref _chart, value); }

    private string _chartCaption = string.Empty;
    /// <summary>"14 nights shown · 3 self-reported · 11 sample" — counts, never guesses.</summary>
    public string ChartCaption { get => _chartCaption; private set => Set(ref _chartCaption, value); }

    private bool _chartReady;
    /// <summary>False until the first load produced a chart — the view keeps its skeleton until then.</summary>
    public bool ChartReady { get => _chartReady; private set => Set(ref _chartReady, value); }

    private bool _hasChart;
    /// <summary>True when at least one night in the window carries a sleep value.</summary>
    public bool HasChart { get => _hasChart; private set => Set(ref _hasChart, value); }

    /// <summary>
    /// The honest no-data note: only after the first load has actually answered, and only when the
    /// window really holds no sleep values. Before that the skeleton is showing, and a note under a
    /// skeleton reads as a lie about data that may still arrive.
    /// </summary>
    public bool ShowNoData => ChartReady && !HasChart;

    private string? _baselineText;
    /// <summary>Localized baseline caption; null while the personal normal is still learning.</summary>
    public string? BaselineText { get => _baselineText; private set => Set(ref _baselineText, value); }
    public bool HasBaseline => !string.IsNullOrEmpty(BaselineText);

    // ---- Recent entries -----------------------------------------------------------

    public ObservableCollection<SleepEntryRow> Entries { get; } = new();
    public bool HasEntries => Entries.Count > 0;
    public bool IsEmpty => !HasEntries;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    private int _manualCount;
    public int ManualCount
    {
        get => _manualCount;
        private set { if (Set(ref _manualCount, value)) { Raise(nameof(EntryCount)); Raise(nameof(HasEntryCount)); } }
    }

    /// <summary>Localized "{0} days logged" counter chip.</summary>
    public string EntryCount => LogUiStrings.EntryCount(ManualCount, _format);
    public bool HasEntryCount => ManualCount > 0;

    // ---- Load ----------------------------------------------------------------------

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var today = _clock.Today;
            var from = today.AddDays(-(WindowDays - 1));

            IReadOnlyList<DailyHistoryRecord> history;
            try
            {
                await _history.EnsureLoadedAsync(_session.CurrentProfile, today);
                history = await _history.GetAllAsync();
            }
            catch (Exception)
            {
                history = Array.Empty<DailyHistoryRecord>();
            }

            var inWindow = history
                .Where(r => r.Date.Date >= from && r.Date.Date <= today)
                .ToList();

            // Which days of the window hold real self-reported values (provenance source of truth).
            List<ManualEntryDraft> drafts;
            try { drafts = (await _entries.GetEntriesAsync(from, today)).ToList(); }
            catch (Exception) { drafts = new(); }

            var manualSleep = new Dictionary<DateTime, int>();
            var manualDays = new HashSet<DateTime>();
            foreach (var d in drafts)
            {
                if (d.IsEmpty) continue;
                manualDays.Add(d.Date.Date);
                if (d.SleepMinutes is { } sm) manualSleep[d.Date.Date] = sm;
            }

            await BuildChartAsync(inWindow, manualSleep, manualDays, from, today);
            BuildEntries(drafts);

            try { ManualCount = await _entries.CountEntriesAsync(); }
            catch (Exception) { ManualCount = 0; }

            Raise(nameof(IsEmpty));
            Raise(nameof(HasEntries));
        }
        finally { IsBusy = false; }
    }

    private async Task BuildChartAsync(List<DailyHistoryRecord> inWindow, Dictionary<DateTime, int> manualSleep,
        HashSet<DateTime> manualDays, DateTime from, DateTime today)
    {
        var byDate = inWindow.GroupBy(r => r.Date.Date).ToDictionary(g => g.Key, g => g.Last());

        var points = new List<SleepChartPoint>(WindowDays);
        var labels = new List<string>(WindowDays);
        int manualNights = 0, dataNights = 0;
        for (int i = 0; i < WindowDays; i++)
        {
            var day = from.AddDays(i);
            labels.Add(_format.ShortDate(day));
            if (!byDate.TryGetValue(day, out var rec))
            {
                points.Add(new SleepChartPoint(day, null, DataOrigin.Mock, IsToday: day == today.Date));
                continue;
            }

            bool manual = manualDays.Contains(day) ||
                          string.Equals(rec.Origin, nameof(DataOrigin.Manual), StringComparison.OrdinalIgnoreCase);
            DataOrigin origin = manual ? DataOrigin.Manual : DataOrigin.Mock;
            // Manual wins per the lane-02 merge rule the whole app follows, so value and
            // provenance agree even before the overlay provider reaches history rows.
            int minutes = manualSleep.TryGetValue(day, out var m) ? m : (int)Math.Round(rec.SleepMinutes);
            bool valid = minutes > 0;
            if (valid) dataNights++;
            if (manual && valid) manualNights++;
            points.Add(new SleepChartPoint(day, valid ? minutes : null, origin, IsToday: day == today.Date));
        }

        double? baseline = null;
        try
        {
            var baselines = await _baselines.GetBaselinesAsync();
            if (baselines.TryGetValue(Metrics.SleepMinutes, out var b) &&
                b.Confidence != BaselineConfidence.None && b.Value > 0)
                baseline = b.Value;
        }
        catch (Exception) { baseline = null; }

        // Only label the dashed line when it is actually drawn (learning users get no fake normal).
        BaselineText = baseline is { } bl
            ? LogUiStrings.Baseline(_format.DurationFromMinutes((int)Math.Round(bl)))
            : null;
        Raise(nameof(HasBaseline));

        Chart = SleepChartModel.Build(points, labels, baseline,
            h => LogUiStrings.AxisHour(h, _format),
            h => _format.Number(h),
            LogUiStrings.ChartPlaceholder);
        // The three numbers add up: only nights that actually carry a value are counted, so the
        // caption can never imply data that the chart does not plot.
        ChartCaption = LogUiStrings.Caption(dataNights, manualNights, Math.Max(0, dataNights - manualNights), _format);
        ChartReady = true;
        HasChart = dataNights > 0;
        Raise(nameof(ShowNoData));
    }

    private void BuildEntries(List<ManualEntryDraft> drafts)
    {
        Entries.Clear();
        // Newest first. Days with a manual entry come straight from the store (it owns the truth);
        // sample-only days deliberately do NOT appear here — the chart labels them instead.
        foreach (var d in drafts.OrderByDescending(x => x.Date))
        {
            if (d.IsEmpty) continue;
            var date = d.Date.Date;
            Entries.Add(new SleepEntryRow
            {
                Date = date,
                DayLabel = DayLabel(date),
                DateText = _format.ShortDate(date),
                SleepText = d.SleepMinutes is { } sm ? _format.DurationFromMinutes(sm) : LogUiStrings.NotSet,
                StepsText = d.Steps is { } st && st > 0 ? _format.Number(st) : string.Empty,
                EditCommand = new Command(() => EditDay(date)),
                DeleteCommand = new Command(async () => await DeleteDayAsync(date)),
                DeleteLabel = LogUiStrings.DeleteEntry,
            });
        }
    }

    private string DayLabel(DateTime date) =>
        LogUiStrings.DayLabel(LogEntryRules.DaysAgo(date, _clock.Today), _format);

    // ---- Row actions ---------------------------------------------------------------

    internal void EditDay(DateTime date)
    {
        // Tap-to-edit loads the day into the inline editor above the list — both entry points
        // (Log tab, pushed log-entry route) drive the same LogEntryViewModel implementation.
        _editor.SelectDate(date);
        EditorFocusRequested?.Invoke();
    }

    internal async Task DeleteDayAsync(DateTime date)
    {
        var page = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;
        if (page is null) return;
        bool confirm = await page.DisplayAlertAsync(
            LogUiStrings.DeleteConfirmTitle,
            LogUiStrings.DeleteConfirmBody(_format.ShortDate(date)),
            Loc["Common.Delete"], Loc["Common.Cancel"]);
        if (!confirm) return;
        try
        {
            await _entries.DeleteAsync(date);
            await LoadAsync();
        }
        catch (Exception)
        {
            // Honesty: a failed delete is reported as a failure, never silently swallowed.
            Editor.ShowDeleteFailure();
        }
    }

    // ---- Language ---------------------------------------------------------------------

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title)); Raise(nameof(Subtitle)); Raise(nameof(RecentTitle)); Raise(nameof(ChartTitle));
        Raise(nameof(EmptyText)); Raise(nameof(EmptySub));
        Raise(nameof(NewEntryText)); Raise(nameof(NoDataNote));
        Raise(nameof(LegendManual)); Raise(nameof(LegendMock)); Raise(nameof(LegendBaseline));
        Raise(nameof(CheckInSectionTitle));
        Raise(nameof(EntryCount));
        // Rows/caption/chart labels were built in the previous language — rebuild them.
        _ = LoadAsync();
    }
}
