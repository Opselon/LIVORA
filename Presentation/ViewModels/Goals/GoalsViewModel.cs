using System.Collections.ObjectModel;
using System.Windows.Input;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;

namespace LIVORA.Presentation;
public sealed class GoalItemViewModel
{
    public required Goal Goal { get; init; }
    public required string CategoryText { get; init; }
    public required string StatusText { get; init; }
    public required Color StatusColor { get; init; }
    public required string ProgressText { get; init; }
    public required double Fraction { get; init; }

    public static GoalItemViewModel FromGoal(Goal g, IFormatService format, Func<string, string> l)
    {
        string statusKey = "Goals." + (g.Status switch
        {
            GoalStatus.OnTrack => "OnTrack",
            GoalStatus.AtRisk => "AtRisk",
            GoalStatus.Behind => "Behind",
            _ => "Completed",
        });
        return new GoalItemViewModel
        {
            Goal = g,
            CategoryText = l("Enum.GoalCategory." + g.Category),
            StatusText = l(statusKey),
            StatusColor = Theme.ForGoalStatus(g.Status),
            Fraction = g.Fraction,
            ProgressText = $"{format.Number((long)g.ProgressValue)} / {format.Number((long)g.TargetValue)}",
        };
    }
}

public sealed class GoalsViewModel : ObservableObject
{
    private readonly IRepository<Goal> _repo;
    private readonly IFormatService _format;

    public GoalsViewModel(IRepository<Goal> repo, IFormatService format)
    {
        _repo = repo;
        _format = format;
        SubscribeLanguage();
        LoadCommand = new Command(async () => await LoadAsync());
        NewGoalCommand = new Command(async () => await CreateGoalAsync());
        EditGoalCommand = new Command<GoalItemViewModel>(async g => await BumpGoalAsync(g));
        DeleteGoalCommand = new Command<GoalItemViewModel>(async g => await DeleteAsync(g));
    }

    public ICommand LoadCommand { get; }
    public ICommand NewGoalCommand { get; }
    public ICommand EditGoalCommand { get; }
    public ICommand DeleteGoalCommand { get; }

    public string Title => L("Goals.Title");
    public string NewGoalText => L("Goals.NewGoal");
    public string EmptyText => L("Goals.Empty");
    public string AddProgressText => L("Goals.AddProgress");
    public string DeleteText => L("Common.Delete");

    public ObservableCollection<GoalItemViewModel> Goals { get; } = new();
    public bool HasGoals => Goals.Count > 0;
    public bool IsEmpty => !HasGoals;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => Set(ref _isBusy, value); }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var all = (await _repo.GetAllAsync()).Where(g => !g.IsArchived).ToList();
            Goals.Clear();
            foreach (var g in all) Goals.Add(GoalItemViewModel.FromGoal(g, _format, key => Loc[key]));
            Raise(nameof(HasGoals));
            Raise(nameof(IsEmpty));
        }
        finally { IsBusy = false; }
    }

    private int _newCount;
    private async Task CreateGoalAsync()
    {
        _newCount++;
        var goal = new Goal
        {
            Name = $"{L("Goals.NewGoal")} {_format.Number(_newCount)}",
            Description = L("Goals.Description"),
            Category = GoalCategory.Custom,
            TargetValue = 4,
            ProgressValue = 0,
            Period = GoalPeriod.Week,
            Unit = GoalUnit.Sessions,
        };
        await _repo.SaveAsync(goal);
        await LoadAsync();
    }
    private async Task BumpGoalAsync(GoalItemViewModel item)
    {
        if (item.Goal.ProgressValue < item.Goal.TargetValue)
        {
            item.Goal.ProgressValue += 1;
            await _repo.SaveAsync(item.Goal);
            await LoadAsync();
        }
    }

    private async Task DeleteAsync(GoalItemViewModel item)
    {
        // Phase 1: delete is direct (demo content); confirmation dialogs land with the
        // user-created-goals flow in Phase 2.
        await _repo.DeleteAsync(item.Goal.Id);
        await LoadAsync();
    }

    protected override void OnLanguageChanged()
    {
        Raise(nameof(Title));
        Raise(nameof(NewGoalText));
        Raise(nameof(EmptyText));
        Raise(nameof(AddProgressText));
        Raise(nameof(DeleteText));
        _ = LoadAsync();
    }
}
