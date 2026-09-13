using System.Collections.ObjectModel;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Discovery;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;

/// <summary>One week bar as drawn: resolved label + caption + token key (never prose from Application).</summary>
public sealed class WeekBarRowViewModel
{
    public required string Label { get; init; }
    public required string Caption { get; init; }
    public required double Value { get; init; }
    public required string ValueText { get; init; }
    /// <summary>Theme color KEY (Positive/Caution/Negative/Accent) — mapped by ColorByKey in the view.</summary>
    public required string ColorKey { get; init; }
}

/// <summary>
/// View-model behind <c>WeekProgressView</c>. It asks the pure <see cref="WeekProgress"/> service
/// for the numbers and refuses to draw bars when the data cannot support them: with fewer than
/// <see cref="WeekProgress.InsufficientDataDays"/> days inside the week the bars stay hidden and
/// the view states how little history there is (product law — no verdicts from a two-day sample).
/// </summary>
public sealed class WeekProgressViewModel : ObservableObject
{
    private readonly IHistoryRepository _history;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Goal> _goals;
    private readonly IRepository<Bootcamp> _bootcamps;
    private readonly IFormatService _format;

    private WeekProgressResult? _result;

    public WeekProgressViewModel(
        IHistoryRepository history,
        IRepository<Habit> habits,
        IRepository<Goal> goals,
        IRepository<Bootcamp> bootcamps,
        IFormatService format)
    {
        _history = history;
        _habits = habits;
        _goals = goals;
        _bootcamps = bootcamps;
        _format = format;
        SubscribeLanguage();
    }

    public string Title { get; private set; } = string.Empty;
    public string Subtitle { get; private set; } = string.Empty;
    public string HabitsHeader { get; private set; } = string.Empty;
    public string GoalsHeader { get; private set; } = string.Empty;
    public string ProgramsHeader { get; private set; } = string.Empty;
    public string StreakHeader { get; private set; } = string.Empty;
    public string StreakValue { get; private set; } = string.Empty;
    public string HonestyNote { get; private set; } = string.Empty;
    public string InsufficientText { get; private set; } = string.Empty;
    public string EmptyText { get; private set; } = string.Empty;
    public string SectionEmptyHabits { get; private set; } = string.Empty;
    public string SectionEmptyGoals { get; private set; } = string.Empty;
    public string SectionEmptyPrograms { get; private set; } = string.Empty;

    public bool HasHonestyNote { get; private set; }
    public bool ShowEmpty { get; private set; }
    public bool ShowInsufficient { get; private set; }
    /// <summary>Bars are only drawn when the week carries enough days of history.</summary>
    public bool ShowBars { get; private set; }
    public bool ShowStreak { get; private set; }
    public bool NoHabitRows { get; private set; }
    public bool NoGoalRows { get; private set; }
    public bool NoProgramRows { get; private set; }

    public ObservableCollection<WeekBarRowViewModel> HabitRows { get; } = new();
    public ObservableCollection<WeekBarRowViewModel> GoalRows { get; } = new();
    public ObservableCollection<WeekBarRowViewModel> ProgramRows { get; } = new();

    /// <summary>referenceDay = any day inside the week to show (today by default).</summary>
    public async Task LoadAsync(DateTime referenceDay)
    {
        _result = await WeekProgress.BuildAsync(_history, _habits, _goals, _bootcamps, referenceDay);
        Rebuild();
    }

    void Rebuild()
    {
        var r = _result;
        Title = L("WeekProgress.Title");
        HabitsHeader = L("WeekProgress.Habits");
        GoalsHeader = L("WeekProgress.Goals");
        ProgramsHeader = L("WeekProgress.Programs");
        StreakHeader = L("WeekProgress.Streak");
        EmptyText = L("WeekProgress.Empty");
        SectionEmptyHabits = L("WeekProgress.Habits.Empty");
        SectionEmptyGoals = L("WeekProgress.Goals.Empty");
        SectionEmptyPrograms = L("WeekProgress.Programs.Empty");

        HabitRows.Clear();
        GoalRows.Clear();
        ProgramRows.Clear();

        if (r is null)
        {
            ShowEmpty = ShowInsufficient = ShowBars = ShowStreak = false;
            NoHabitRows = NoGoalRows = NoProgramRows = false;
            HasHonestyNote = false;
            Subtitle = StreakValue = HonestyNote = InsufficientText = string.Empty;
            RaiseAll();
            return;
        }

        Subtitle = L("WeekProgress.Period", _format.ShortDate(r.WeekStart), _format.ShortDate(r.WeekEnd));
        ShowEmpty = !r.HasAnything;
        ShowBars = r.ShowBars;
        ShowInsufficient = !r.ShowBars && r.HasAnything;
        StreakValue = L("Common.StreakDays", _format.Number(r.StreakDays));
        ShowStreak = r.ShowBars && r.StreakDays > 0;
        // Honest footer: exactly how many days of history these bars rest on.
        HasHonestyNote = r.ShowBars;
        HonestyNote = r.Status == WeekDataStatus.Early
            ? L("WeekProgress.Confidence.Early", _format.Number(r.DataDays))
            : L("WeekProgress.Confidence.Solid", _format.Number(r.DataDays));
        InsufficientText = L("WeekProgress.Insufficient",
            _format.Number(r.DataDays), _format.Number(WeekProgress.InsufficientDataDays));

        if (r.ShowBars)
        {
            foreach (var b in r.HabitBars) HabitRows.Add(Row(b));
            foreach (var b in r.GoalBars) GoalRows.Add(Row(b));
            foreach (var b in r.ProgramBars) ProgramRows.Add(Row(b));
        }
        NoHabitRows = r.ShowBars && HabitRows.Count == 0;
        NoGoalRows = r.ShowBars && GoalRows.Count == 0;
        NoProgramRows = r.ShowBars && ProgramRows.Count == 0;
        RaiseAll();
    }

    /// <summary>Resolves a pure WeekBar into display strings: label = user text or program title key,
    /// caption = localization key + locale-formatted counts.</summary>
    WeekBarRowViewModel Row(WeekBar b)
    {
        string label = !string.IsNullOrWhiteSpace(b.LabelText)
            ? b.LabelText
            : L(b.LabelKey ?? string.Empty);
        // Count captions are always (done, target); formatting through IFormatService keeps the
        // digits Persian in fa — the raw ints from Application must never reach string.Format.
        string caption = b.CaptionKey.Length == 0
            ? string.Empty
            : L(b.CaptionKey, _format.Number(b.ValueCount), _format.Number(b.TargetCount));
        return new WeekBarRowViewModel
        {
            Label = label,
            Caption = caption,
            Value = b.NoTarget ? 0 : b.Value,
            ValueText = b.NoTarget ? L("WeekProgress.NoTarget") : _format.Percent(b.Value),
            ColorKey = b.NoTarget ? "Accent" : b.ColorKey,
        };
    }

    void RaiseAll()
    {
        foreach (var n in new[]
        {
            nameof(Title), nameof(Subtitle), nameof(HabitsHeader), nameof(GoalsHeader), nameof(ProgramsHeader),
            nameof(StreakHeader), nameof(StreakValue), nameof(HonestyNote), nameof(InsufficientText),
            nameof(EmptyText), nameof(SectionEmptyHabits), nameof(SectionEmptyGoals), nameof(SectionEmptyPrograms),
            nameof(HasHonestyNote), nameof(ShowEmpty), nameof(ShowInsufficient), nameof(ShowBars), nameof(ShowStreak),
            nameof(NoHabitRows), nameof(NoGoalRows), nameof(NoProgramRows),
            nameof(HabitRows), nameof(GoalRows), nameof(ProgramRows),
        }) Raise(n);
    }

    protected override void OnLanguageChanged()
    {
        Rebuild();
        Raise(nameof(FlowDirection));
    }
}
