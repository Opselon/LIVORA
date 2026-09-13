using System.Diagnostics;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.IntelligenceProviders;
using LIVORA.Infrastructure.Localization;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;
using LIVORA.Presentation;
using Plugin.LocalNotification;
using Microsoft.Extensions.Logging;

namespace LIVORA;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            // Wave 3 (lane 09): local reminders. Must be configured before the notification
            // center is first touched (registration order is enforced by this call site).
            .UseLocalNotification()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                fonts.AddFont("Vazirmatn-Regular.ttf", "VazirmatnRegular");
                fonts.AddFont("Vazirmatn-SemiBold.ttf", "VazirmatnSemiBold");
                fonts.AddFont("Vazirmatn-Bold.ttf", "VazirmatnBold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // ---- Persistence layer -------------------------------------------------
        builder.Services.AddSingleton(new JsonFileStore());
        builder.Services.AddSingleton<ISettingsService, PreferencesSettingsService>();
        builder.Services.AddSingleton<IRepository<Goal>>(sp =>
            new JsonRepository<Goal>(sp.GetRequiredService<JsonFileStore>(), AppConstants.GoalsFile, g => g.Id));
        builder.Services.AddSingleton<IRepository<Habit>>(sp =>
            new JsonRepository<Habit>(sp.GetRequiredService<JsonFileStore>(), AppConstants.HabitsFile, h => h.Id));
        builder.Services.AddSingleton<IRepository<Bootcamp>>(sp =>
            new JsonRepository<Bootcamp>(sp.GetRequiredService<JsonFileStore>(), AppConstants.BootcampsFile, b => b.Id));
        builder.Services.AddSingleton<IRepository<UserProfile>>(sp =>
            new JsonRepository<UserProfile>(sp.GetRequiredService<JsonFileStore>(), AppConstants.ProfileFile, p => p.Id));

        // ---- Data layer (Wave 2: provider -> normalizer -> history) ------------
        // Wave 3: the mock feed stays resolvable as its concrete type (ManualEntryStore's
        // backfill source), but NOTHING registers it as IDataProvider anymore — lane 02's
        // ManualOverlayProvider owns that seam (registered in the WAVE3-DI region below), so
        // history backfill, state derivation and the UI all see manual values on one code path.
        builder.Services.AddSingleton<SampleHealthProvider>();
        builder.Services.AddSingleton<IDataNormalizer, DataNormalizer>();
        builder.Services.AddSingleton<DailyHistoryStore>();
        builder.Services.AddSingleton<IHistoryRepository>(sp => sp.GetRequiredService<DailyHistoryStore>());

        // ---- Core services -----------------------------------------------------
        builder.Services.AddSingleton<IDateTimeProvider>(SystemDateTimeProvider.Instance);
        builder.Services.AddSingleton<ILocalizationService, LocalizationService>();
        builder.Services.AddSingleton<IFormatService>(sp => (LocalizationService)sp.GetRequiredService<ILocalizationService>());
        builder.Services.AddSingleton<SessionState>();
        builder.Services.AddSingleton(sp => new DemoDataSeeder(
            sp.GetRequiredService<IRepository<Goal>>(),
            sp.GetRequiredService<IRepository<Habit>>(),
            sp.GetRequiredService<IRepository<Bootcamp>>(),
            sp.GetRequiredService<ISettingsService>(),
            key => sp.GetRequiredService<ILocalizationService>()[key]));

        // ---- Intelligence (deterministic core + swappable interpretation) ------
        builder.Services.AddSingleton<ITrendService, TrendService>();
        builder.Services.AddSingleton<IBaselineService, BaselineService>();
        builder.Services.AddSingleton<IUserStateService, UserStateService>();
        builder.Services.AddSingleton<IRuleEngine, RuleEngine>();
        builder.Services.AddSingleton<IRecommendationService, RecommendationService>();
        builder.Services.AddSingleton<IIntelligenceProvider, SampleIntelligenceProvider>();
        builder.Services.AddSingleton<IDailyPlanService, DailyPlanService>();
        builder.Services.AddSingleton<IIntelligenceService, IntelligenceOrchestrator>();
        builder.Services.AddSingleton<WeeklySummaryService>();
        builder.Services.AddSingleton<IWeeklySummaryService>(sp => sp.GetRequiredService<WeeklySummaryService>());
        builder.Services.AddSingleton<ProgramAdapter>();

        // ---- Platform abstractions (permissions/privacy) ------------------------
        builder.Services.AddSingleton<IPermissionService, PermissionService>();
        builder.Services.AddSingleton<IPrivacyService, PrivacyService>();

        // WAVE3-DI: Wave 3 service registrations (updates, manual entry, reminders, theme).
        // Merged by the orchestrator from the lane APPEND blocks; single source of truth for the
        // concrete types (no route or page resolves implementations on its own).
        // ---- Wave 3: updates, manual entry, reminders, theme (lane registrations, merged) ----
        // Updates (lane 01). Keyless public feed; UpdateService caches to update_feed.json.
        builder.Services.AddSingleton<LIVORA.Infrastructure.Updates.GitHubReleaseFeed>();
        builder.Services.AddSingleton<LIVORA.Infrastructure.Updates.IReleaseFeed>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Updates.GitHubReleaseFeed>());
        builder.Services.AddSingleton<LIVORA.Infrastructure.Updates.UpdateFeedCache>();
        builder.Services.AddSingleton<IUpdateService, LIVORA.Infrastructure.Updates.UpdateService>();
        builder.Services.AddTransient<LIVORA.Presentation.UpdateViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.UpdateBannerViewModel>();

        // Manual entry (lane 02): the store is the source of truth for user-logged days and the
        // overlay provider is what the whole pipeline (history backfill included) resolves as
        // IDataProvider. Func<> factories break the store<->history<->provider cycle at boot.
        builder.Services.AddSingleton<IManualEntryService>(sp => new LIVORA.Infrastructure.Persistence.ManualEntryStore(
            sp.GetRequiredService<JsonFileStore>(),
            () => sp.GetRequiredService<IDataProvider>(),
            () => sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<SessionState>(),
            sp.GetRequiredService<IDateTimeProvider>()));
        builder.Services.AddSingleton<ManualOverlayProvider>(sp => new ManualOverlayProvider(
            sp.GetRequiredService<SampleHealthProvider>(),
            sp.GetRequiredService<IManualEntryService>()));
        // REPLACE the Wave 2 pass-through registration: every consumer of IDataProvider — history
        // backfill, state service, UI — now sees manual values on top of the mock feed.
        builder.Services.AddSingleton<IDataProvider>(sp => sp.GetRequiredService<ManualOverlayProvider>());

        // Reminders + notifications (lane 09, Plugin.LocalNotification; wired in builder above).
        builder.Services.AddSingleton<LIVORA.Infrastructure.Notifications.ReminderStore>();
        builder.Services.AddSingleton<LIVORA.Infrastructure.Notifications.SnoozeStore>();
        builder.Services.AddSingleton<LIVORA.Application.Insights.ISnoozeStore>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Notifications.SnoozeStore>());
        builder.Services.AddSingleton<LIVORA.Infrastructure.Notifications.LocalReminderService>(sp =>
            new LIVORA.Infrastructure.Notifications.LocalReminderService(
                sp.GetRequiredService<LIVORA.Infrastructure.Notifications.ReminderStore>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IDateTimeProvider>(),
                sp.GetRequiredService<IUserStateService>(),
                sp.GetRequiredService<IRepository<Habit>>(),
                sp.GetRequiredService<IRepository<Bootcamp>>(),
                sp.GetRequiredService<IManualEntryService>(),
                sp.GetRequiredService<SessionState>()));
        builder.Services.AddSingleton<IReminderService>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Notifications.LocalReminderService>());
        builder.Services.AddSingleton<LIVORA.Application.Reminders.IReminderLedger>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Notifications.LocalReminderService>());
        builder.Services.AddSingleton<LIVORA.Application.Reminders.IReminderEvaluator>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Notifications.LocalReminderService>());
        builder.Services.AddTransient<LIVORA.Presentation.RemindersViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.Views.RemindersPage>();

        // Theme (lane 05): persists ThemeMode, maps domain AppThemeKind <-> MAUI AppTheme.
        builder.Services.AddSingleton<IThemeService, LIVORA.Infrastructure.Settings.ThemeService>();

        // Navigation seam (lane 04): keeps ViewModels off `new AppShell()`/Application.Current.
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.INavigateToMainApp,
            LIVORA.App.MainAppNavigator>();

        // Log check-in (lane 03): the editor VM is a dependency of the Log tab VM, so it is
        // registered before the page that resolves it; pages keep the ServiceHelper convention.
        builder.Services.AddTransient<LIVORA.Presentation.LogEntryViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.LogViewModel>();

        // Editors (lane 07) + program detail (lane 08) view models.
        builder.Services.AddTransient<LIVORA.Presentation.GoalEditorViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.HabitEditorViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.BootcampDetailViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.SettingsViewModel>();

        // Lane 04's shell hook: the 6th tab materializes LogPage through the composition root so
        // the XAML never hard-references a lane page type.
        AppShell.LogTabContent = () => new LIVORA.Presentation.Views.LogPage();

        // Routes for pushed pages (lanes 01/06/07/08/09; one line per page).
        Routing.RegisterRoute("updates", typeof(LIVORA.Presentation.Views.UpdatePage));
        Routing.RegisterRoute("log-entry", typeof(LIVORA.Presentation.Views.LogEntryPage));
        Routing.RegisterRoute("goal-editor", typeof(LIVORA.Presentation.Views.GoalEditorPage));
        Routing.RegisterRoute("habit-editor", typeof(LIVORA.Presentation.Views.HabitEditorPage));
        Routing.RegisterRoute("bootcamp-detail", typeof(LIVORA.Presentation.Views.BootcampDetailPage));
        Routing.RegisterRoute("settings", typeof(LIVORA.Presentation.Views.SettingsPage));
        Routing.RegisterRoute("reminders", typeof(LIVORA.Presentation.Views.RemindersPage));
        Routing.RegisterRoute("review", typeof(LIVORA.Presentation.Views.Review.WeeklySummaryPage));
        // WAVE3-DI-END

        // ---- ViewModels --------------------------------------------------------
        builder.Services.AddTransient<TodayViewModel>();
        builder.Services.AddTransient<HealthViewModel>();
        builder.Services.AddTransient<GoalsViewModel>();
        builder.Services.AddTransient<ProgramsViewModel>();
        builder.Services.AddTransient<ProfileViewModel>();
        builder.Services.AddTransient<OnboardingViewModel>();
        builder.Services.AddTransient<WeeklySummaryViewModel>();

        var app = builder.Build();

        ServiceHelper.Initialize(app.Services);

        // Load persisted profile + seed first-launch demo content in the active language.
        //
        // PERFORMANCE NOTE (measured, not assumed): these three calls run on the main thread
        // inside CreateMauiAppBuilder.Build()'s follow-up, i.e. BEFORE the first window exists,
        // so every millisecond here is added directly to cold-start time-to-first-frame.
        // The IO itself is tiny (profile + goals/habits/bootcamps JSON = a few KB, all reads and
        // writes are synchronous by design — see JsonFileStore's comment about the pre-message-pump
        // deadlock hazard), which is exactly why it is NOT worth threading off: an async continuation
        // would still have to complete before App.CreateWindow can read SessionState.CurrentProfile,
        // so it would only add scheduler hops and a determinism risk to seed ordering
        // (DemoDataSeeder.SeedIfEmptyAsync must run in the ACTIVE language, which only exists once
        // LocalizationService has applied the stored preference).
        // The Stopwatch below turns that reasoning into a number on every DEBUG launch instead of
        // a claim; Release pays nothing for it.
#if DEBUG
        var bootSw = Stopwatch.StartNew();
#endif
        var session = app.Services.GetRequiredService<SessionState>();
        var profileRepo = app.Services.GetRequiredService<IRepository<UserProfile>>();
        var profile = (profileRepo.GetAllAsync().GetAwaiter().GetResult()).FirstOrDefault();
        if (profile is null)
        {
            profile = new UserProfile();
            profileRepo.SaveAsync(profile).GetAwaiter().GetResult();
        }
        session.CurrentProfile = profile;

        var seed = app.Services.GetRequiredService<DemoDataSeeder>();
        seed.SeedIfEmptyAsync(DateTime.Today).GetAwaiter().GetResult();
#if DEBUG
        bootSw.Stop();
        Debug.WriteLine($"[LIVORA.boot] profile-load + demo-seed: {bootSw.ElapsedMilliseconds} ms");
#endif

        App.ApplyFlowDirection();
        return app;
    }
}
