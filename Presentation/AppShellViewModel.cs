using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Domain.Enums;
using LIVORA.Presentation;

namespace LIVORA;
/// <summary>
/// ViewModel backing the Shell: holds the ordered tab list and refreshes tab titles on language
/// switch. Titles are localized and re-resolved live through ILocalizationService.
/// </summary>
public sealed class AppShellViewModel : ObservableObject
{
    public static AppShellViewModel? Instance { get; set; }

    public AppShellViewModel()
    {
        Instance = this;
        SubscribeLanguage();
    }

    public List<TabItem> Tabs { get; } = new()
    {
        new() { Route = "Today", Icon = "today", Key = "Tab.Today" },
        new() { Route = "Health", Icon = "heart", Key = "Tab.Health" },
        new() { Route = "Goals", Icon = "target", Key = "Tab.Goals" },
        new() { Route = "Programs", Icon = "programs", Key = "Tab.Programs" },
        new() { Route = "Profile", Icon = "person", Key = "Tab.Profile" },
    };

    public string TitleFor(string route)
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        var item = Tabs.FirstOrDefault(t => t.Route == route);
        return item is null ? route : loc[item.Key];
    }

    public static void RefreshTabs()
    {
        Instance?.Raise(nameof(Tabs));
        // Re-apply flow direction in case shell pages need it.
        App.ApplyFlowDirection();
    }
}

public sealed class TabItem
{
    public required string Route { get; init; }
    public required string Icon { get; init; }
    public required string Key { get; init; }
}
