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
using SkiaSharp.Views.Maui.Controls.Hosting;
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
            // Wave 3 (lane 03): the Log tab hosts a SkiaSharp chart (SKCanvasView). Without the
            // handler registration the control throws at render time on Windows and the process
            // dies inside native XAML (0xc000027b) — verified during the merge's launch walk.
            .UseSkiaSharp()
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
        // NOTE (wave3c): IIntelligenceProvider is registered once in the WAVE3B-DI region below,
        // wired to the AiOrchestratorProvider whose deterministic fallback IS SampleIntelligenceProvider.
        // The legacy `AddSingleton<IIntelligenceProvider, SampleIntelligenceProvider>()` was removed
        // to avoid a silent last-wins duplicate (see NoServiceInterface_IsRegisteredTwiceInMauiProgram).
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
        // WAVE3B-DI: Wave 3b (master) registrations — AI providers, consent, persistence metadata,
        // normalization, patterns, activity. Merged by the orchestrator from lane APPEND blocks.

        // ---- WAVE3C LANE 01: security + data portability (MAUI-free classes, composition root
        // resolves them; same app-data layout as the existing stores — no second data root). ----
        var livoraDataDir = System.IO.Path.Combine(FileSystem.AppDataDirectory, "LIVORA");
        var secureDirStore = new System.Lazy<LIVORA.Infrastructure.Persistence.LocalJsonStore>(
            () => new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Security.SecureStorageService.StoreDirName)));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Persistence.LocalJsonStore(livoraDataDir));
        // At-rest box: real DPAPI (CurrentUser) on Windows; elsewhere a private app-data file with
        // IsPlatformHardwareBacked=false — the UI can never claim a cipher this head does not have.
        builder.Services.AddSingleton<LIVORA.Infrastructure.Security.IPlatformSecureBox>(
#if WINDOWS
            _ => new LIVORA.Infrastructure.Security.WindowsPlatformSecureBox());
#else
            _ => new LIVORA.Infrastructure.Security.PrivateFileSecureBox());
#endif
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ISecureStorageService>(sp =>
            new LIVORA.Infrastructure.Security.SecureStorageService(secureDirStore.Value,
                sp.GetRequiredService<LIVORA.Infrastructure.Security.IPlatformSecureBox>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IConsentService>(sp =>
            new LIVORA.Infrastructure.Security.ConsentStore(
                sp.GetRequiredService<LIVORA.Infrastructure.Persistence.LocalJsonStore>()));
        // The AI-gateway seam: user-entered key beats the obfuscated embedded fallback; ships
        // DISABLED (plain-HTTP endpoint), so nothing is sent anywhere until the user opts in.
        // Register the concrete type once; IGatewayConfigService aliases it (no duplicate entry).
        builder.Services.AddSingleton<LIVORA.Infrastructure.Security.Gateway.GatewayConfigService>(sp =>
            new LIVORA.Infrastructure.Security.Gateway.GatewayConfigService(
                new LIVORA.Infrastructure.Persistence.LocalJsonStore(System.IO.Path.Combine(
                    livoraDataDir, LIVORA.Infrastructure.Security.Gateway.GatewayConfigService.GatewayDirName)),
                sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IGatewayConfigService>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Security.Gateway.GatewayConfigService>());
        // Catalog arbitration (integration decision, per PR#5 risk 2): lane 01's LocalDataCatalog
        // binds the frozen ILocalDataCatalogService for the UI; lane 06's richer
        // LocalDataCatalogService (meta bumps + sync-queue enqueue on every write) stays
        // resolvable by concrete type for the storage lanes' own consumers.
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ILocalDataCatalogService>(sp =>
            new LIVORA.Infrastructure.Persistence.LocalDataCatalog(
                sp.GetRequiredService<LIVORA.Infrastructure.Persistence.LocalJsonStore>()));
        builder.Services.AddSingleton<LIVORA.Infrastructure.Persistence.Wave3b.LocalDataCatalogService>();
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ILocalPasscodeService>(sp =>
            new LIVORA.Infrastructure.Security.LocalPasscodeService(
                sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>(),
                () => sp.GetRequiredService<SessionState>().CurrentProfile?.Id));
        // ---- WAVE4-DI: the REAL cloud seam (P1-B server + P1-D client classes) replaces Wave 3c's
        // honest placeholders. With no base URL provisioned (cloud/cloud-settings.json absent) the
        // bridge defers to the NoopSyncTransport shape byte-for-byte — queue entries stay Pending,
        // the connector card says "not configured", nothing is claimed that did not happen. Point it
        // at a deployed Livora.Server and the same objects push/pull for real. The placeholder
        // registrations are deleted (not wrapped): deleting them was the designed only-path to a
        // Synced word (CLIENT-CONTRACT-P1 §4).
        builder.Services.AddSingleton<LIVORA.Application.Cloud.ICloudApiOptions>(sp =>
            new LIVORA.Infrastructure.Cloud.CloudApiOptions(
                new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                    System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Cloud.CloudApiOptions.CloudDirName))));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.CloudTokenStore(
            sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>(),
            () => (sp.GetRequiredService<LIVORA.Application.Abstractions.ISecureStorageService>()
                as LIVORA.Infrastructure.Security.SecureStorageService)?.IsPlatformHardwareBacked == true));
        // The port needs an auth context, and the session manager needs the port: the deferred hop
        // breaks that construction cycle, resolving the real manager lazily on first network use.
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.DeferredCloudAuthContext(
            () => sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSessionManager>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.LivoraApiPort(
            sp.GetRequiredService<LIVORA.Application.Cloud.ICloudApiOptions>(),
            null,
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.DeferredCloudAuthContext>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.CloudSessionManager(
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.LivoraApiPort>(),
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudTokenStore>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ICloudAuthService>(sp =>
            new LIVORA.Infrastructure.Cloud.SessionCloudAuthService(
                () => sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSessionManager>(),
                sp.GetRequiredService<LIVORA.Application.Cloud.ICloudApiOptions>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.CatalogSyncPayloadSource(
            () => sp.GetRequiredService<LIVORA.Application.Abstractions.ILocalDataCatalogService>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.CloudSyncTransport(
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.LivoraApiPort>(),
            sp.GetRequiredService<LIVORA.Application.Cloud.ICloudApiOptions>(),
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSessionManager>(),
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CatalogSyncPayloadSource>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Cloud.CloudSyncBridge(
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.LivoraApiPort>(),
            sp.GetRequiredService<LIVORA.Application.Cloud.ICloudApiOptions>(),
            sp.GetRequiredService<LIVORA.Application.Sync.SyncQueue>(),
            new LIVORA.Infrastructure.Cloud.CloudConnectorStateStore(
                new LIVORA.Infrastructure.Persistence.LocalJsonStore(
                    System.IO.Path.Combine(livoraDataDir, LIVORA.Infrastructure.Cloud.CloudApiOptions.CloudDirName))),
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSessionManager>(),
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSyncTransport>(),
            restoreSession: () => sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSessionManager>()
                .RestoreAsync()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.ISyncTransport>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSyncBridge>());
        builder.Services.AddSingleton<LIVORA.Application.Cloud.ICloudConnector>(sp =>
            sp.GetRequiredService<LIVORA.Infrastructure.Cloud.CloudSyncBridge>());
        // WAVE4-DI-END

        // ---- WAVE3C LANE 02: real AI orchestration (OpenAI-compatible gateway) + safety -------
        // Order: gate (consent + enabled + configured) -> minimal context -> provider -> tolerant
        // parse -> safety validator -> interpretation; ANY failure falls back to the deterministic
        // SampleIntelligenceProvider below. SSE-even-when-not-streaming handled by the provider.
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IUnitConverter,
            LIVORA.Application.Normalization.UnitConverter>();
        builder.Services.AddSingleton<LIVORA.Application.Normalization.PayloadValidator>();
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IPayloadValidator>(sp =>
            sp.GetRequiredService<LIVORA.Application.Normalization.PayloadValidator>());
        builder.Services.AddSingleton<LIVORA.Application.Normalization.ProviderDayMapper>();
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IContextBuilder>(sp =>
            new LIVORA.Application.Intelligence.ContextBuilder(
                () => sp.GetRequiredService<LIVORA.Application.Abstractions.ILocalizationService>()
                        .CurrentLanguage == LIVORA.Domain.Enums.AppLanguage.Persian ? "fa" : "en"));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IAiOutputValidator,
            LIVORA.Application.Intelligence.AiSafetyValidator>();
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.IntelligenceProviders.Wave3c.OpenAiCompatibleChatProvider(
            sp.GetRequiredService<LIVORA.Application.Abstractions.IGatewayConfigService>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IIntelligenceProviderRegistry>(sp =>
            new LIVORA.Infrastructure.IntelligenceProviders.Wave3c.ProviderRegistry(
                new LIVORA.Application.Abstractions.IIntelligenceChatProvider[]
                {
                    sp.GetRequiredService<LIVORA.Infrastructure.IntelligenceProviders.Wave3c.OpenAiCompatibleChatProvider>(),
                },
                sp.GetRequiredService<LIVORA.Application.Abstractions.IConsentService>(),
                sp.GetRequiredService<LIVORA.Application.Abstractions.IGatewayConfigService>()));
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IIntelligenceProvider>(sp =>
            new LIVORA.Application.Intelligence.AiOrchestratorProvider(
                sp.GetRequiredService<LIVORA.Application.Abstractions.IContextBuilder>(),
                () => sp.GetRequiredService<LIVORA.Application.Abstractions.IIntelligenceProviderRegistry>().PickEffective(),
                sp.GetRequiredService<LIVORA.Application.Abstractions.IAiOutputValidator>(),
                // deterministic fallback = the shipped Wave 2 provider (rule-driven phrasing),
                // rebuilt here from the live rule engine (the single IIntelligenceProvider
                // registration above replaced the legacy Sample one; this instance is only the
                // worst-case phrasing source when the AI path fails or is opted out).
                new LIVORA.Infrastructure.IntelligenceProviders.SampleIntelligenceProvider(
                    sp.GetRequiredService<IRuleEngine>()),
                sp.GetRequiredService<LIVORA.Application.Abstractions.IConsentService>(),
                sp.GetRequiredService<LIVORA.Application.Abstractions.IGatewayConfigService>()));
        // "Connected" may only ever render after a real round-trip: the tester records its verdict
        // through the ONE writer of probe state (GatewayConfigService.RecordProbeResult).
        LIVORA.Presentation.AiSettingsViewModel.ConnectionTester = async ct =>
        {
            var chat = ServiceHelper.Get<LIVORA.Infrastructure.IntelligenceProviders.Wave3c.OpenAiCompatibleChatProvider>();
            var gateway = ServiceHelper.Get<LIVORA.Infrastructure.Security.Gateway.GatewayConfigService>();
            var text = await chat.CompleteStructuredAsync(
                "Reply with exactly {\"ok\":true} and nothing else.", "{}", TimeSpan.FromSeconds(20), ct)
                .ConfigureAwait(false);
            var ok = text is not null && text.Contains("ok", StringComparison.OrdinalIgnoreCase);
            gateway.RecordProbeResult(ok, ok ? null : "AiSettings.Test.Fail");
            return ok;
        };

        // ---- WAVE3C LANE 05: adaptive plan engine + ranked decision layer ----------------------
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IPlanAdaptationEngine,
            LIVORA.Application.Planning.Adaptive.PlanAdaptationEngine>();
        builder.Services.AddSingleton<LIVORA.Application.Planning.Wave3b.RecommendationRanker>();
        builder.Services.AddSingleton<LIVORA.Application.Planning.Wave3b.DecisionLog>();

        // ---- WAVE3C LANE 06: durable local-first storage + sync queue + migrations -------------
        builder.Services.AddSingleton(sp => new LIVORA.Application.Sync.MetaIndex(livoraDataDir));
        builder.Services.AddSingleton(sp => new LIVORA.Application.Sync.SyncQueue(
            livoraDataDir, sp.GetRequiredService<LIVORA.Application.Sync.MetaIndex>()));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Persistence.Wave3b.JsonFileStoreV2(livoraDataDir));
        builder.Services.AddSingleton(sp => new LIVORA.Infrastructure.Persistence.Wave3b.MigrationRunner(livoraDataDir));

        // ---- WAVE3C LANE 04/06 consumers: pattern cache (pure, lock-guarded LRU) --------------
        builder.Services.AddSingleton<LIVORA.Application.Patterns.PatternCache>();

        // ---- WAVE3B LANE 02/03: Health Connect foundation + workouts ---------------------------
        // The Health Connect record client is NOT bundled this wave (no new NuGet allowed), so the
        // bridge honestly probes Unavailable/ApiNotBundled and the provider returns null days —
        // IDataProvider deliberately STAYS ManualOverlayProvider; swapping it would blank the
        // pipeline in exchange for an unsupported connection (see lane02 report).
        builder.Services.AddSingleton<LIVORA.Application.HealthData.Wave3bHealth.IHealthPlatformBridge>(
            _ => LIVORA.Infrastructure.HealthProviders.HealthConnectProvider.DefaultBridge());
        builder.Services.AddSingleton<LIVORA.Infrastructure.HealthProviders.HealthConnectProvider>(sp =>
            new LIVORA.Infrastructure.HealthProviders.HealthConnectProvider(
                sp.GetRequiredService<LIVORA.Application.HealthData.Wave3bHealth.IHealthPlatformBridge>(),
                new LIVORA.Application.HealthData.Wave3bHealth.PermissionFlow(
                    sp.GetRequiredService<LIVORA.Application.HealthData.Wave3bHealth.IHealthPlatformBridge>()),
                sp.GetRequiredService<IDateTimeProvider>()));
        builder.Services.AddSingleton<LIVORA.Application.HealthData.Wave3bHealth.IHealthDataProviderRegistry>(sp =>
            LIVORA.Application.HealthData.Wave3bHealth.HealthDataProviderRegistry.Live(() =>
            {
                var provider = sp.GetRequiredService<LIVORA.Infrastructure.HealthProviders.HealthConnectProvider>();
                return new[]
                {
                    LIVORA.Application.HealthData.Wave3bHealth.HealthDataProviderRegistry.For(
                        provider,
                        sp.GetRequiredService<LIVORA.Application.HealthData.Wave3bHealth.IHealthPlatformBridge>(),
                        provider.Permissions.State),
                };
            }));
        // Real user-data path: workouts the user exported into app-data/livora/workouts_inbox
        // themselves (Origin=Imported). No files => honest empty list; nothing is simulated.
        builder.Services.AddSingleton<LIVORA.Application.Abstractions.IWorkoutSource>(sp =>
            new LIVORA.Application.Activities.ImportedWorkoutSource(
                LIVORA.Application.Activities.ImportedWorkoutSource.ComposeInboxPath(
                    System.IO.Path.Combine(FileSystem.AppDataDirectory, "LIVORA")),
                sp.GetRequiredService<LIVORA.Application.Abstractions.IUnitConverter>()));

        // ---- WAVE3C LANE 07: wave-3c surfaces (pages/VMs/routes; VMs resolve services lazily via
        // ServiceHelper.TryGet, so each row renders an honest not-available state until its
        // backing service is registered — never a crash, never a dead button). ----
        builder.Services.AddTransient<LIVORA.Presentation.AiSettingsViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.DataStudioViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.LockViewModel>();
        builder.Services.AddTransient<LIVORA.Presentation.Views.AiSettingsPage>();
        builder.Services.AddTransient<LIVORA.Presentation.Views.DataStudioPage>();
        builder.Services.AddTransient<LIVORA.Presentation.Views.LockPage>();
        Routing.RegisterRoute("ai-settings", typeof(LIVORA.Presentation.Views.AiSettingsPage));
        Routing.RegisterRoute("data-studio", typeof(LIVORA.Presentation.Views.DataStudioPage));
        Routing.RegisterRoute("lock", typeof(LIVORA.Presentation.Views.LockPage));
        // WAVE3B-DI-END

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
