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
using Microsoft.Extensions.Logging;

namespace LIVORA;
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
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
        builder.Services.AddSingleton<SampleHealthProvider>();
        builder.Services.AddSingleton<IDataProvider>(sp => sp.GetRequiredService<SampleHealthProvider>());
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
        // The orchestrator applies each lane's `APPEND MauiProgram.cs // WAVE3-DI:` block here
        // exactly once. Do not register a concrete type anywhere else.
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
