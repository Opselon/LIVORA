using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Discovery;
using LIVORA.Application.HealthData;
using LIVORA.Application.Planning;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;
/// <summary>
/// One program as a card. Strings are resolved at load time (and re-resolved on language change),
/// so the model never carries a half-translated snapshot.
/// </summary>
public sealed class BootcampItemViewModel
{
    public required Bootcamp Bootcamp { get; init; }
    public required string Id { get; init; }
    public required string TitleText { get; init; }
    public required string DescriptionText { get; init; }
    public required string CategoryText { get; init; }
    public required string DifficultyText { get; init; }
    public required string ProgressText { get; init; }
    public required double ProgressFraction { get; init; }
    public required string DayText { get; init; }
    public required string StatusText { get; init; }
    public required string CompletedText { get; init; }
    public required string TodayPlanText { get; init; }
    public required string TodayPlanDetail { get; init; }
    public required string TodayMinutesText { get; init; }
    /// <summary>The unadapted plan name, shown as "Original plan" only when today was actually changed.</summary>
    public required string OriginalPlanText { get; init; }
    public required bool WasAdapted { get; init; }
    /// <summary>Why today's day changed (structured rule -> localized sentence). Empty when nothing fired.</summary>
    public required string AdaptationReason { get; init; }
    public required string CreatorText { get; init; }
    /// <summary>Why discovery surfaced this program (recommended rail); empty on normal cards.</summary>
    public string RecommendReason { get; init; } = string.Empty;
    /// <summary>The "Original plan: …" line only earns space when a rule really changed today.</summary>
    public bool ShowOriginalPlan => WasAdapted && OriginalPlanText.Length > 0;
}

public sealed class ProgramsViewModel : ObservableObject
{
    private readonly IRepository<Bootcamp> _repo;
    private readonly IFormatService _format;
    private readonly ProgramAdapter _adapter;
    private readonly IUserStateService _stateService;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly IHistoryRepository _history;
    private readonly SessionState _session;

    private List<BootcampItemViewModel> _all = new();
    private List<ProgramSuggestion> _suggestions = new();

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
        _adapter = adapter;
        _stateService = stateService;
        _habits = habits;
        _goals = goals;
        _history = history;
        _session = session;
        SubscribeLanguage();
        LoadCommand = new Command(async () => await LoadAsync());
        EnrollCommand = new Command<BootcampItemViewModel>(async b => await EnrollAsync(b));
        LeaveCommand = new Command<BootcampItemViewModel>(async b => await LeaveAsync(b));
        CompleteDayCommand = new Command<BootcampItemViewModel>(async b => await CompleteDayAsync(b));
        OpenDetailCommand = new Command<BootcampItemViewModel>(async item => await OpenDetailAsync(item));
        ClearSearchCommand = new Command(() => SearchText = string.Empty);
    }

    public ICommand LoadCommand { get; }
    public ICommand EnrollCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand CompleteDayCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand ClearSearchCommand { get; }

    // ---- Static labels on this page resolve through {localize:Tr …} in XAML. The strings below
    // are the ones that must be built in code because they carry numbers or user content. ----

    // ---- Search + category filter (both applied to the same materialized snapshot) ----
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public const string FilterAllKey = "all";

    private string _selectedFilterKey = FilterAllKey;
    public string SelectedFilterKey
    {
        get => _selectedFilterKey;
        private set
        {
            if (_selectedFilterKey == value) return;
            _selectedFilterKey = value;
            ApplyFilter();
            foreach (var f in Filters) f.RaiseSelected();
            Raise(nameof(IsFiltering));
        }
    }

    public void SelectFilter(string? key) => SelectedFilterKey = string.IsNullOrEmpty(key) ? FilterAllKey : key!;

    public List<FilterChipViewModel> Filters { get; private set; } = new();
    public bool HasFilters => Filters.Count > 0;

    public ObservableCollection<BootcampItemViewModel> Enrolled { get; } = new();
    public ObservableCollection<BootcampItemViewModel> Available { get; } = new();
    public ObservableCollection<BootcampItemViewModel> Recommended { get; } = new();
    public bool HasEnrolled => Enrolled.Count > 0;
    public bool HasRecommended => Recommended.Count > 0;
    public bool HasAvailable => Available.Count > 0;
    public bool HasResults => Enrolled.Count > 0 || Available.Count > 0;
    /// <summary>True when a search string or a category chip is narrowing the list.</summary>
    public bool IsFiltering => SelectedFilterKey != FilterAllKey || !string.IsNullOrWhiteSpace(SearchText);
    public bool IsEmpty => !HasResults;
    public string EmptyText => string.IsNullOrWhiteSpace(SearchText) && SelectedFilterKey == FilterAllKey
        ? L("Programs.Empty")
        : L("Programs.Empty.Filtered");

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

        _all = all.Select(b => ToItem(b, state, profile, goals, habits)).ToList();
        _suggestions = ProgramDiscovery.Recommend(all, profile, state).ToList();
        BuildFilters(all);
        BuildRecommended();
        ApplyFilter();
    }

    // Was async with zero awaits inside: each call allocated a state machine + Task that the caller
    // then awaited. The rule-engine work it triggers is synchronous, so it is now a plain method.
    private BootcampItemViewModel ToItem(Bootcamp b, Domain.Models.State.PersonalState state,
        UserProfile profile, IReadOnlyList<Goal> goals, IReadOnlyList<Habit> habits)
    {
        var view = BootcampDayDisplay.BuildView(b);
        var planned = view.FirstOrDefault(d => d.DayNumber == b.CurrentDay);
        var display = planned;
        string adaptationReason = string.Empty;
        string originalPlan = string.Empty;
        bool adapted = false;
        if (b.IsEnrolled && planned is not null)
        {
            var (day, ruleKey) = _adapter.AdaptDay(b, planned, state, profile, goals, habits, DateTime.Now);
            display = day;
            adapted = day.IsAdapted;
            if (ruleKey is not null)
            {
                // The explanation is produced by the SAME rule that changed the day — no invented cause.
                adaptationReason = L("Plan.Adapted.Reason", L("Rule.Why." + ruleKey));
                originalPlan = L(planned.PlanTitleKey);   // honest "what it was before we touched it"
            }
        }

        var progress = BootcampProgress.Compute(b, DateTime.Today, adapted ? display : null);
        return new BootcampItemViewModel
        {
            Bootcamp = b,
            Id = b.Id,
            TitleText = L(b.TitleKey),
            DescriptionText = L(b.DescriptionKey),
            CategoryText = L("Enum.BootcampCategory." + b.Category),
            DifficultyText = L("Enum.BootcampDifficulty." + b.Difficulty),
            ProgressText = _format.Percent(progress.CompletionFraction),
            ProgressFraction = progress.CompletionFraction,
            DayText = b.IsEnrolled && progress.TotalDays > 0
                ? L("Common.DayXofY", _format.Number(Math.Max(progress.CurrentDay, 1)), _format.Number(progress.TotalDays))
                : L("Bootcamp.Length", _format.Number(b.DurationDays)),
            StatusText = L(progress.StatusKey),
            CompletedText = L("Bootcamp.Progress.CompletedDays", _format.Number(progress.CompletedDays), _format.Number(progress.TotalDays)),
            TodayPlanText = display is null ? string.Empty : L(display.PlanTitleKey),
            TodayPlanDetail = display?.PlanDescriptionKey is null or "" ? string.Empty : L(display.PlanDescriptionKey),
            TodayMinutesText = display is null ? string.Empty : L("Bootcamp.Minutes", _format.Number(display.TargetMinutes)),
            OriginalPlanText = originalPlan,
            WasAdapted = adapted,
            AdaptationReason = adaptationReason,
            CreatorText = L("Programs.Creator", b.CreatorName),
        };
    }

    // ---- Filters + recommended rail ---------------------------------------------

    void BuildFilters(IReadOnlyList<Bootcamp> all)
    {
        var present = all.Select(b => b.Category).Distinct().OrderBy(c => (int)c).ToList();
        var chips = present.Count <= 1
            ? new List<FilterChipViewModel>()   // one category only: filtering would be a fake control
            : new List<FilterChipViewModel> { new(this, FilterAllKey, L("Programs.Filter.All")) };
        chips.AddRange(present.Select(c => new FilterChipViewModel(this, c.ToString(), L("Enum.BootcampCategory." + c))));
        if (chips.Count > 0 && chips.All(c => c.Key != _selectedFilterKey)) _selectedFilterKey = FilterAllKey;
        Filters = chips;
        Raise(nameof(Filters));
        Raise(nameof(HasFilters));
    }

    void BuildRecommended()
    {
        Recommended.Clear();
        foreach (var s in _suggestions)
        {
            var item = _all.FirstOrDefault(i => i.Id == s.Program.Id);
            if (item is null) continue;
            Recommended.Add(new BootcampItemViewModel
            {
                Bootcamp = item.Bootcamp,
                Id = item.Id,
                TitleText = item.TitleText,
                DescriptionText = item.DescriptionText,
                CategoryText = item.CategoryText,
                DifficultyText = item.DifficultyText,
                ProgressText = item.ProgressText,
                ProgressFraction = item.ProgressFraction,
                DayText = item.DayText,
                StatusText = item.StatusText,
                CompletedText = item.CompletedText,
                TodayPlanText = item.TodayPlanText,
                TodayPlanDetail = item.TodayPlanDetail,
                TodayMinutesText = item.TodayMinutesText,
                OriginalPlanText = item.OriginalPlanText,
                WasAdapted = item.WasAdapted,
                AdaptationReason = item.AdaptationReason,
                CreatorText = item.CreatorText,
                RecommendReason = ReasonFor(s),
            });
        }
    }

    /// <summary>Composes the deterministic discovery reason into a sentence. The Application layer
    /// emitted keys + args; the sentence lives only in resources.</summary>
    string ReasonFor(ProgramSuggestion s) => s.ReasonKind switch
    {
        ProgramReasonKind.FocusArea => L("Programs.Recommend.ForGoal", L(s.PhraseKey)),
        ProgramReasonKind.StateSignal => L("Programs.Recommend.Reason", L(s.PhraseKey)),
        _ => L("Programs.Recommend.Starter"),
    };

    void ApplyFilter()
    {
        var q = SearchText.Trim();
        BootcampCategory? cat =
            SelectedFilterKey != FilterAllKey && Enum.TryParse<BootcampCategory>(SelectedFilterKey, out var c) ? c : null;

        Enrolled.Clear();
        Available.Clear();
        foreach (var i in _all
                     .Where(i => (cat is null || i.Bootcamp.Category == cat) &&
                                 (q.Length == 0 ||
                                  Folds(i.TitleText).Contains(Folds(q)) ||
                                  Folds(i.DescriptionText).Contains(Folds(q)) ||
                                  Folds(i.CategoryText).Contains(Folds(q))))
                     .OrderByDescending(i => i.Bootcamp.IsEnrolled)
                     .ThenByDescending(i => i.Bootcamp.IsEnrolled ? i.Bootcamp.CurrentDay : 0)
                     .ThenBy(i => i.Bootcamp.DurationDays)
                     .ThenBy(i => i.Id, StringComparer.Ordinal))
            (i.Bootcamp.IsEnrolled ? Enrolled : Available).Add(i);

        Raise(nameof(HasEnrolled));
        Raise(nameof(HasRecommended));
        Raise(nameof(HasAvailable));
        Raise(nameof(HasResults));
        Raise(nameof(IsEmpty));
        Raise(nameof(IsFiltering));
        Raise(nameof(SelectedFilterKey));
        Raise(nameof(EmptyText));
    }

    /// <summary>Case- and diacritic-insensitive folding so Persian search tolerates ZWNJ/ی-ك variants.</summary>
    static string Folds(string s) => s.Trim().ToLowerInvariant()
        .Replace('\u200c', ' ')   // ZWNJ becomes a space: "می‌کنم" matches "می کنم"
        .Replace('ي', 'ی').Replace('ك', 'ک');

    private async Task OpenDetailAsync(BootcampItemViewModel? item)
    {
        if (item is null) return;
        try { await Shell.Current.GoToAsync($"bootcamp-detail?id={Uri.EscapeDataString(item.Id)}"); }
        catch { /* route not registered in this build — the card's own actions keep working */ }
    }

    // ---- Enroll / leave / complete: every mutation funnels through BootcampProgress in the
    // Application layer (Domain is frozen), so this page and the detail page cannot drift. ----

    private async Task EnrollAsync(BootcampItemViewModel? item)
    {
        if (item is null) return;
        var b = item.Bootcamp;
        BootcampProgress.Enroll(b);
        await _repo.SaveAsync(b);
        await LoadAsync();
    }

    private async Task LeaveAsync(BootcampItemViewModel? item)
    {
        if (item is null) return;
        var b = item.Bootcamp;
        BootcampProgress.Leave(b);
        await _repo.SaveAsync(b);
        await LoadAsync();
    }

    private async Task CompleteDayAsync(BootcampItemViewModel? item)
    {
        if (item is null) return;
        var b = item.Bootcamp;
        int dayNumber = BootcampProgress.CompleteToday(b, item.WasAdapted);
        await _repo.SaveAsync(b);
        if (dayNumber > 0)
            await BootcampJournal.LogDayAsync(_history, b, dayNumber, item.WasAdapted, DateTime.Today);
        await LoadAsync();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(EmptyText));
        _ = LoadAsync();   // rebuilds cards + chips in the new language
    }
}

/// <summary>
/// One category chip. Selection is owned by the VM; chips only render + report taps. The visual
/// state is expressed as TOKEN KEYS (a *Brush key for the background, a FontAttributes value for
/// emphasis) so the view never needs a converter per state and no color is hardcoded here.
/// </summary>
public sealed class FilterChipViewModel : ObservableObject
{
    private readonly ProgramsViewModel _owner;
    public FilterChipViewModel(ProgramsViewModel owner, string key, string label)
    {
        _owner = owner;
        Key = key;
        Label = label;
        SelectCommand = new Command(() => _owner.SelectFilter(Key));
    }
    public string Key { get; }
    public string Label { get; }
    public ICommand SelectCommand { get; }
    public bool IsSelected => _owner.SelectedFilterKey == Key;
    /// <summary>Brush token: AccentSoft when active, Overlay when idle (Border.Background only).</summary>
    public string FillKey => IsSelected ? "AccentSoftBrush" : "OverlayBrush";
    /// <summary>Selected chip reads bold — a state cue that survives both scripts.</summary>
    public FontAttributes LabelEmphasis => IsSelected ? FontAttributes.Bold : FontAttributes.None;
    public void RaiseSelected()
    {
        Raise(nameof(IsSelected));
        Raise(nameof(FillKey));
        Raise(nameof(LabelEmphasis));
    }
}
