using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Planning;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;

/// <summary>
/// Wave 3 (lane 07): the real goal editor. New and edit modes share one implementation, reachable
/// both by DI ctor and by Shell route (<c>goal-editor?mode=new</c> / <c>goal-editor?id=…</c>).
///
/// Validation lives in <see cref="GoalEditorRules"/> (pure, MAUI-free, covered by lane 10): the VM
/// only turns issue codes into localized messages and maps fields onto the model. The layer rule
/// holds — Application emits codes, Presentation owns the language.
/// </summary>
public sealed class GoalEditorViewModel : ObservableObject
{
    private readonly IRepository<Goal> _repo;
    private readonly IFormatService _format;
    private readonly IDateTimeProvider _clock;
    private readonly SessionState _session;
    private Goal? _editing;
    private List<Goal> _allGoals = new();

    public GoalEditorViewModel(
        IRepository<Goal> repo,
        IFormatService format,
        IDateTimeProvider clock,
        SessionState session)
    {
        _repo = repo;
        _format = format;
        _clock = clock;
        _session = session;
        SubscribeLanguage();
        SaveCommand = new Command(async () => await SaveAsync());
        ArchiveCommand = new Command(async () => await ArchiveAsync());
        CancelCommand = new Command(async () => await CloseAsync());
        RebuildOptions();
        ApplySuggestions();
        Revalidate();
    }

    public ICommand SaveCommand { get; }
    public ICommand ArchiveCommand { get; }
    public ICommand CancelCommand { get; }

    // ---- Options (rebuilt on language change; index == enum value) ----
    public List<string> CategoryOptions { get; private set; } = new();
    public List<string> UnitOptions { get; private set; } = new();
    public List<string> PeriodOptions { get; private set; } = new();
    public List<string> MeasurementOptions { get; private set; } = new();
    public List<string> MetricOptions { get; private set; } = new();
    public IReadOnlyList<string> MetricKeys { get; } = GoalEditorRules.KnownMetricKeys;

    // ---- Editable fields ----
    private string _name = string.Empty;
    public string Name { get => _name; set { if (Set(ref _name, value)) Revalidate(); } }

    private string _description = string.Empty;
    public string Description { get => _description; set => Set(ref _description, value); }

    private int _categoryIndex = (int)GoalCategory.Custom;
    public int CategoryIndex
    {
        get => _categoryIndex;
        set { if (Set(ref _categoryIndex, value)) Revalidate(); }
    }

    private string _targetText = string.Empty;
    public string TargetText { get => _targetText; set { if (Set(ref _targetText, value)) Revalidate(); } }

    private int _unitIndex = (int)GoalUnit.Sessions;
    public int UnitIndex
    {
        get => _unitIndex;
        set { if (Set(ref _unitIndex, value)) { Revalidate(); Raise(nameof(UnitHintText)); } }
    }

    private int _periodIndex = (int)GoalPeriod.Week;
    public int PeriodIndex { get => _periodIndex; set { if (Set(ref _periodIndex, value)) Raise(nameof(PeriodSuffixText)); } }

    private bool _hasDeadline;
    public bool HasDeadline
    {
        get => _hasDeadline;
        set { if (Set(ref _hasDeadline, value)) { Revalidate(); Raise(nameof(ShowDeadlinePicker)); } }
    }
    public bool ShowDeadlinePicker => HasDeadline;

    private DateTime _deadline = DateTime.Today;
    public DateTime Deadline { get => _deadline; set { if (Set(ref _deadline, value)) Revalidate(); } }
    public DateTime MinimumDeadlineDate => _clock.Today;

    private int _measurementIndex = (int)GoalMeasurement.ManualCounter;
    public int MeasurementIndex
    {
        get => _measurementIndex;
        set { if (Set(ref _measurementIndex, value)) { Raise(nameof(ShowMetricPicker)); Raise(nameof(MetricNote)); Revalidate(); } }
    }
    public bool ShowMetricPicker => GoalEditorRules.IsMetricMeasured((GoalMeasurement)MeasurementIndex);

    private int _metricIndex = -1;
    public int MetricIndex
    {
        get => _metricIndex;
        set { if (Set(ref _metricIndex, value)) { Revalidate(); Raise(nameof(MetricNote)); } }
    }

    /// <summary>Honesty: a metric-measured goal reads from health data, and that data is still the
    /// sample provider. Say so instead of implying a live measurement.</summary>
    public string MetricNote => ShowMetricPicker ? L("Editor.MetricNote") : string.Empty;

    // ---- Labels ----
    public string Title => L(_editing is null ? "Editor.GoalTitle" : "Goals.EditGoal");
    public string NameLabel => L("Goals.Name");
    public string DescriptionLabel => L("Goals.Description");
    public string CategoryLabel => L("Goals.Category");
    public string TargetLabel => L("Goals.Target");
    public string UnitLabel => L("Goals.Unit");
    public string PeriodLabel => L("Goals.Period");
    public string DeadlineLabel => L("Editor.DeadlineLabel");
    public string MeasurementLabel => L("Editor.MeasurementLabel");
    public string MetricLabel => L("Editor.MetricLabel");
    public string SaveText => L("Common.Save");
    public string CancelText => L("Common.Cancel");
    public string ArchiveText => L("Editor.ArchiveAction");
    public bool IsEditing => _editing is not null;
    public bool ShowArchive => IsEditing && !GoalEditorRules.ShouldHardDelete(_editing);
    public string UnitHintText => BuildUnitHint();
    public string PeriodSuffixText => L(GoalEditorRules.PeriodSuffixKey((GoalPeriod)PeriodIndex));
    public string NameHintText => L("Editor.NameHint", _format.Number(GoalEditorRules.MaxNameLength));
    public string TargetPlaceholder => L("Editor.TargetPlaceholder");
    public string DescriptionPlaceholder => L("Editor.DescriptionPlaceholder");

    /// <summary>Messages under the fields: blocking errors first, the advisory duplicate-name
    /// notice last (two goals may legitimately share a name).</summary>
    public ObservableCollection<string> Messages { get; } = new();
    public bool HasMessages => Messages.Count > 0;
    public bool CanSave => _validation.IsValid;

    /// <summary>Validation copy only appears once the user has tried to save (or a load problem
    /// needs telling) — a pristine form must never open scolding them for an empty name.</summary>
    private bool _hasAttemptedSave;
    private string? _loadNote;

    private GoalEditValidation _validation = GoalEditValidation.Valid;

    private string BuildUnitHint()
    {
        var unit = (GoalUnit)UnitIndex;
        var (min, max) = GoalEditorRules.TargetBounds(unit);
        return L(GoalEditorRules.NumberBoundsHintKey,
            _format.Number((long)min), _format.Number((long)max), L("Enum.GoalUnit." + unit));
    }

    /// <summary>Defaults suggested from the profile's focus areas (lane 07 spec) — editable fields,
    /// never a hidden auto-created goal.</summary>
    private void ApplySuggestions()
    {
        var draft = GoalEditorRules.SuggestedDraft(_session.CurrentProfile);
        _categoryIndex = (int)draft.Category;
        _unitIndex = (int)draft.Unit;
        _periodIndex = (int)draft.Period;
        _measurementIndex = (int)draft.Measurement;
        _metricIndex = -1;
        _hasDeadline = false;
        _deadline = _clock.Today;
        _name = string.Empty;
        _description = string.Empty;
        _targetText = FormatTarget(draft.TargetValue);
        RaiseAll();
    }

    /// <summary>Called by the page from IQueryAttributable (Shell route); works for a
    /// ctor-created page too: mode=new keeps the suggestions, id=… loads the stored goal.</summary>
    public async Task ApplyQueryAsync(IReadOnlyDictionary<string, string> query)
    {
        query.TryGetValue("id", out var id);
        await LoadAsync(id);
    }

    public async Task LoadAsync(string? goalId)
    {
        _loadNote = null;
        _allGoals = (await _repo.GetAllAsync()).ToList();
        if (string.IsNullOrWhiteSpace(goalId))
        {
            _editing = null;
            ApplySuggestions();
            Revalidate();
            return;
        }

        var goal = _allGoals.FirstOrDefault(g => g.Id == goalId) ?? await _repo.GetAsync(goalId);
        if (goal is null)
        {
            _editing = null;
            ApplySuggestions();
            _loadNote = L("Editor.NotFound");
            Revalidate();
            return;
        }

        _editing = goal;
        _name = goal.Name;
        _description = goal.Description;
        _categoryIndex = (int)goal.Category;
        _unitIndex = (int)goal.Unit;
        _periodIndex = (int)goal.Period;
        _measurementIndex = (int)goal.Measurement;
        _metricIndex = goal.MetricKey is { Length: > 0 } mk ? IndexOfKey(MetricKeys, mk) : -1;
        _hasDeadline = goal.Deadline is not null;
        _deadline = goal.Deadline ?? _clock.Today;
        _targetText = FormatTarget(goal.TargetValue);
        RaiseAll();
        Revalidate();
    }

    private void RaiseAll()
    {
        Raise(nameof(Name)); Raise(nameof(Description));
        Raise(nameof(CategoryIndex)); Raise(nameof(UnitIndex)); Raise(nameof(PeriodIndex));
        Raise(nameof(MeasurementIndex)); Raise(nameof(MetricIndex));
        Raise(nameof(TargetText)); Raise(nameof(HasDeadline)); Raise(nameof(Deadline));
        Raise(nameof(ShowDeadlinePicker)); Raise(nameof(ShowMetricPicker)); Raise(nameof(MetricNote));
        Raise(nameof(Title)); Raise(nameof(IsEditing)); Raise(nameof(ShowArchive));
        Raise(nameof(UnitHintText)); Raise(nameof(PeriodSuffixText)); Raise(nameof(CanSave));
    }

    private static int IndexOfKey(IReadOnlyList<string> keys, string value)
    {
        for (int i = 0; i < keys.Count; i++)
            if (string.Equals(keys[i], value, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void RebuildOptions()
    {
        CategoryOptions = Enum.GetValues<GoalCategory>().Select(c => L("Enum.GoalCategory." + c)).ToList();
        UnitOptions = Enum.GetValues<GoalUnit>().Select(u => L("Enum.GoalUnit." + u)).ToList();
        PeriodOptions = Enum.GetValues<GoalPeriod>().Select(p => L("Enum.GoalPeriod." + p)).ToList();
        MeasurementOptions = Enum.GetValues<GoalMeasurement>().Select(m => L(GoalEditorRules.MeasurementLabelKey(m))).ToList();
        MetricOptions = MetricKeys.Select(k => L(GoalEditorRules.MetricKeyLocalizationKey(k))).ToList();
        Raise(nameof(CategoryOptions)); Raise(nameof(UnitOptions)); Raise(nameof(PeriodOptions));
        Raise(nameof(MeasurementOptions)); Raise(nameof(MetricOptions));
    }

    private void Revalidate()
    {
        _validation = GoalEditorRules.ValidateGoal(
            Name, TargetText, (GoalUnit)UnitIndex, (GoalMeasurement)MeasurementIndex,
            ShowMetricPicker && MetricIndex >= 0 ? MetricKeys[MetricIndex] : null,
            HasDeadline ? Deadline : null,
            _clock.Today, _allGoals, _editing?.Id, Loc.FormatCulture);

        // Quiet until asked: an untouched form never scolds the user for an empty name. Messages
        // appear after the first save attempt (or when a load could not find the item).
        Messages.Clear();
        if (_hasAttemptedSave || _loadNote is not null)
        {
            if (_loadNote is { } note) Messages.Add(note);
            if (_hasAttemptedSave)
                foreach (var issue in GoalEditorRules.OrderedIssues(_validation))
                    Messages.Add(MessageFor(issue));
        }
        Raise(nameof(HasMessages));
        Raise(nameof(CanSave));
    }

    /// <summary>Issue code → localized message. Argument counts match in EN and FA.</summary>
    private string MessageFor(GoalEditIssue issue)
    {
        var unit = (GoalUnit)UnitIndex;
        var (min, max) = GoalEditorRules.TargetBounds(unit);
        return issue switch
        {
            GoalEditIssue.NameEmpty => L("Editor.Error.NameEmpty"),
            GoalEditIssue.NameTooLong => L("Editor.Error.NameTooLong", _format.Number(GoalEditorRules.MaxNameLength)),
            GoalEditIssue.NameDuplicate => L("Editor.Warn.NameDuplicate"),
            GoalEditIssue.TargetNotNumeric => L("Editor.Error.TargetNotNumeric"),
            GoalEditIssue.TargetNotWhole => L("Editor.Error.TargetNotWhole", L("Enum.GoalUnit." + unit)),
            GoalEditIssue.TargetOutOfRange => L("Editor.Error.TargetRange", _format.Number((long)min), _format.Number((long)max)),
            GoalEditIssue.DeadlinePast => L("Editor.Error.DeadlinePast"),
            GoalEditIssue.MetricKeyMissing => L("Editor.Error.MetricKey"),
            _ => string.Empty,
        };
    }

    /// <summary>Locale-aware display of a target: whole numbers go through IFormatService (Persian
    /// digits + separators); fractional ones (7.5 hours) keep the active culture's decimal sign with
    /// mapped digits, so an English UI sees "7.5" and a Persian UI "۷/۵".</summary>
    private string FormatTarget(double value)
    {
        var ci = Loc.FormatCulture;
        if (Math.Abs(value - Math.Round(value)) < 1e-9) return _format.Number((long)Math.Round(value));
        var native = SafeNativeDigits(ci);
        if (native is null) return value.ToString("0.##", ci);
        var s = value.ToString("0.##", CultureInfo.InvariantCulture);
        var sb = new StringBuilder(s.Length + 2);
        foreach (var ch in s)
        {
            if (char.IsAsciiDigit(ch)) sb.Append(native[ch - '0']);
            else if (ch == '.') sb.Append(ci.NumberFormat.NumberDecimalSeparator);
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string[]? SafeNativeDigits(CultureInfo ci)
    {
        try
        {
            var native = ci.NumberFormat.NativeDigits;
            // Invariant/trimmed cultures report ASCII digits — treat that as "no mapping needed".
            return native is { Length: 10 } && native[0] != "0" ? native : null;
        }
        catch (Exception) { return null; }
    }

    private Goal Build(Goal? into)
    {
        var goal = into ?? new Goal();
        goal.Name = GoalEditorRules.CleanName(Name);
        goal.Description = Description.Trim();
        goal.Category = (GoalCategory)CategoryIndex;
        goal.Unit = (GoalUnit)UnitIndex;
        goal.Period = (GoalPeriod)PeriodIndex;
        goal.Measurement = (GoalMeasurement)MeasurementIndex;
        goal.MetricKey = ShowMetricPicker && MetricIndex >= 0 ? MetricKeys[MetricIndex] : null;
        goal.Deadline = HasDeadline ? Deadline.Date : null;
        if (GoalEditorRules.TryParseTarget(TargetText, Loc.FormatCulture, out var target))
            goal.TargetValue = target;
        return goal;
    }

    private async Task SaveAsync()
    {
        _hasAttemptedSave = true;
        Revalidate();
        if (_validation.BlocksSave) return; // the on-screen messages already say why

        var goal = Build(_editing);
        // A brand-new manual goal starts at zero; a metric-measured one has no manual progress at
        // all (Wave 5 computes it) — the editor never fakes a number. Editing keeps stored progress.
        if (_editing is null) goal.ProgressValue = 0;
        await _repo.SaveAsync(goal);
        await CloseAsync();
    }

    private async Task ArchiveAsync()
    {
        if (_editing is not { } goal) return;
        var page = CurrentPage;
        bool confirm = page is not null && await page.DisplayAlertAsync(
            L("Editor.Confirm.Archive.Title"),
            L(GoalEditorRules.ConfirmBodyKey(goal)),
            L("Common.Yes"), L("Common.No"));
        if (!confirm) return;
        GoalEditorRules.Archive(goal);
        await _repo.SaveAsync(goal);
        await CloseAsync();
    }

    private static Page? CurrentPage
        => Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page;

    /// <summary>Close: prefer the Shell route stack ("../" pops the pushed page); fall back to the
    /// page's own Navigation so an editor pushed without Shell still closes.</summary>
    private async Task CloseAsync()
    {
        var page = CurrentPage;
        try
        {
            var shell = Microsoft.Maui.Controls.Shell.Current;
            if (shell is not null && shell.Navigation.NavigationStack.Count > 1
                && page is not null && shell.Navigation.NavigationStack.Contains(page))
            {
                await shell.GoToAsync("../");
                return;
            }
        }
        catch (Exception) { /* not in a shell navigation context — fall through */ }

        if (page?.Navigation is { } nav)
        {
            if (nav.NavigationStack.Count > 1) await nav.PopAsync();
            else if (nav.ModalStack.Count > 0) await nav.PopModalAsync();
        }
    }

    protected override void OnLanguageChanged()
    {
        RebuildOptions();
        // Locale-sensitive re-render of the target field, WITHOUT ever rewriting text the user is
        // mid-edit: only re-format when the field still holds exactly what we put there (stored
        // goal) or when the typed text still parses cleanly (re-express it in the new locale).
        // Anything else stays untouched — a lost edit is worse than a foreign digit shape.
        if (_editing is { } g && TargetText == FormatTarget(g.TargetValue))
            _targetText = FormatTarget(g.TargetValue);
        else if (GoalEditorRules.TryParseTarget(TargetText, Loc.FormatCulture, out var parsed))
            _targetText = FormatTarget(parsed);
        Raise(nameof(Title)); Raise(nameof(NameLabel)); Raise(nameof(DescriptionLabel));
        Raise(nameof(CategoryLabel)); Raise(nameof(TargetLabel)); Raise(nameof(UnitLabel));
        Raise(nameof(PeriodLabel)); Raise(nameof(DeadlineLabel)); Raise(nameof(MeasurementLabel));
        Raise(nameof(MetricLabel)); Raise(nameof(SaveText)); Raise(nameof(CancelText));
        Raise(nameof(ArchiveText));
        Raise(nameof(NameHintText)); Raise(nameof(TargetPlaceholder)); Raise(nameof(DescriptionPlaceholder));
        Raise(nameof(UnitHintText)); Raise(nameof(PeriodSuffixText)); Raise(nameof(MetricNote));
        Revalidate();
    }
}
