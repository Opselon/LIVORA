using LIVORA.Application.Abstractions;
using LIVORA.Presentation;

namespace LIVORA;
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        // WAVE3-SHELL: Routing.RegisterRoute calls for pushed pages (updates, settings, reminders,
        // log-entry, goal-editor, habit-editor, bootcamp-detail) — one line per lane page, applied
        // by the orchestrator so a missing page never silently breaks the shell.
        // WAVE3-SHELL-END
        var loc = ServiceHelper.Get<ILocalizationService>();
        loc.LanguageChanged += RefreshTitles;
        RefreshTitles();
    }

    /// <summary>Shell tab titles live outside the normal binding tree — re-resolve them on switch.</summary>
    private void RefreshTitles()
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        TodayItem.Title = loc["Tab.Today"];
        HealthItem.Title = loc["Tab.Health"];
        GoalsItem.Title = loc["Tab.Goals"];
        ProgramsItem.Title = loc["Tab.Programs"];
        ProfileItem.Title = loc["Tab.Profile"];
    }
}
