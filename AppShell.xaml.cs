using System.Collections.Generic;
using System.Linq;
using LIVORA.Application.Abstractions;
using LIVORA.Presentation;

namespace LIVORA;
public partial class AppShell : Shell
{
    /// <summary>
    /// Content factory for the 6th tab (Log). Lane 03's <c>LogPage</c> lives in another lane's
    /// copy, so a static XAML reference here would break the lane-isolated build gate; the
    /// composition root fills this hook at the <c>// WAVE3-DI:</c> marker with
    /// <c>() =&gt; new LIVORA.Presentation.Views.LogPage()</c>. Left null (Log lane not merged),
    /// the shell honestly shows five tabs — never a fake sixth tab.
    /// </summary>
    public static System.Func<Page>? LogTabContent { get; set; }

    private ShellContent? _logItem;

    public AppShell()
    {
        InitializeComponent();
        // WAVE3-SHELL: Routing.RegisterRoute calls for pushed pages (updates, settings, reminders,
        // log-entry, goal-editor, habit-editor, bootcamp-detail) — one line per lane page, applied
        // by the orchestrator so a missing page never silently breaks the shell.
        // WAVE3-SHELL-END
        AddLogTab();
        var loc = ServiceHelper.Get<ILocalizationService>();
        loc.LanguageChanged += RefreshTitles;
        RefreshTitles();
    }

    /// <summary>Inserts the Log tab between Health and Goals (LANES.md §Lane 04 tab order).</summary>
    private void AddLogTab()
    {
        if (LogTabContent is null) return;

        _logItem = new ShellContent
        {
            ContentTemplate = new DataTemplate(LogTabContent),
            Icon = ImageSource.FromFile("tab_log.svg"),
            Route = "Log",
            // Title deliberately unset: RefreshTitles() (right after this in the ctor) applies
            // the localized one before the shell ever renders — zero hardcoded strings.
        };

        // Position off the *route*, never a hardcoded index: other lanes may land extra shell
        // edits in parallel, and Log must sit after Health whatever the list already holds.
        var sections = MainTabBar.Items.ToList();
        var health = sections.FindIndex(s => s.Items.FirstOrDefault()?.Route == "Health");
        var target = health >= 0 ? health + 1 : sections.Count;
        MainTabBar.Items.Insert(target, _logItem);
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
        if (_logItem is not null) _logItem.Title = loc["Tab.Log"];
    }
}
