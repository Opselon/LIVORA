using LIVORA.Application.Abstractions;
using LIVORA.Presentation;

namespace LIVORA;
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
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
