using System.Diagnostics;
using LIVORA.Application.Abstractions;
using LIVORA.Presentation;
using LIVORA.Presentation.Views;

namespace LIVORA;
public partial class App : Microsoft.Maui.Controls.Application, INavigateToMainApp
{
    // One in-flight error dialog for the whole app: burst protection so a storm of
    // unhandled exceptions can never stack alerts or re-enter the handler (no rethrow loops).
    private static int _errorDialogInFlight;

    public App()
    {
        InitializeComponent();

        // Global safety net. These can fire off the UI thread depending on platform, so every
        // path through the handlers is wrapped — the net itself must never throw.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        var settings = ServiceHelper.Get<ISettingsService>();

        var onboardingDone = settings.OnboardingCompleted;
        var page = onboardingDone ? (Page)new AppShell() : new OnboardingPage();
        // The Shell itself (tabs/flyout chrome) needs the direction set at creation —
        // child pages set their own in BaseContentPage.
        page.FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var window = new Window(page);
        // The Window is the inheritance root of the whole visual tree (verified headless against
        // Microsoft.Maui.Controls 10.0.101: Window is a flow-direction source and the change
        // propagates down through Shell/pages to labels). Setting the page alone is NOT enough for
        // the native tab strip — see ApplyFlowDirection's doc-comment for the guarantee map.
        window.FlowDirection = page.FlowDirection;

        // WAVE3-APP: window/theme bootstrap hook (lane 04's block, applied once by the
        // orchestrator). Needs the window instance, hence it sits after the page/window
        // creation above rather than at the top of the method.
        ConfigureDesktopWindow(window);
        WireThemeSync();
        // WAVE3-APP-END
        return window;
    }

    /// <summary>
    /// Desktop-first window behaviour on Windows: a usable minimum (never a fixed size — the user
    /// still owns the window) so the responsive layouts have room to show their Wide buckets.
    /// Other platforms get this via their native window management; MAUI's Window sizing knobs are
    /// only honoured on the Windows head today.
    /// </summary>
    private static void ConfigureDesktopWindow(Window window)
    {
#if WINDOWS
        window.MinimumWidth = 1100;
        window.MinimumHeight = 800;
#endif
    }

    /// <summary>
    /// Keeps the persisted theme mode authoritative across OS light/dark flips. The frozen
    /// IThemeService contract (Wave 3) exposes <see cref="IThemeService.Apply"/> as its only
    /// inbound seam, so "notify" means "re-apply": the implementation re-reads the mode, pushes
    /// UserAppTheme, and raises ThemeChanged for whoever watches it. Null-safe via TryGet — the
    /// registration lives behind another lane's DI marker and the app must still run without it.
    /// </summary>
    private static void WireThemeSync()
    {
        ServiceHelper.TryGet<IThemeService>()?.Apply();

        if (_themeSyncWired) return;
        _themeSyncWired = true;
        Microsoft.Maui.Controls.Application.Current!.RequestedThemeChanged += (_, _) =>
        {
            try { ServiceHelper.TryGet<IThemeService>()?.Apply(); }
            catch (Exception ex) { Debug.WriteLine($"[LIVORA.theme] re-apply failed: {ex.Message}"); }
        };
    }

    private static bool _themeSyncWired;

    // ---- INavigateToMainApp (lane 04): the app owns window plumbing, VMs do not ----------

    void INavigateToMainApp.NavigateToMainApp()
    {
        if (Current?.Windows.FirstOrDefault() is not { } window) return;
        var shell = new AppShell();
        // Direction first, before the chrome builds: a fresh Shell created in the right
        // direction is the only path that is guaranteed correct on every platform (some native
        // tab strips mirror at creation time but not mid-flight).
        shell.FlowDirection = CurrentFlowDirection;
        window.FlowDirection = CurrentFlowDirection;
        window.Page = shell;
    }

    void INavigateToMainApp.ReapplyDirection() => ApplyFlowDirection();

    /// <summary>
    /// DI adapter: the container holds this while the real implementation is the running
    /// <c>Application</c> instance itself, which does not exist at registration time. Register at
    /// the WAVE3-DI marker (lane 04's APPEND block).
    /// </summary>
    public sealed class MainAppNavigator : INavigateToMainApp
    {
        private static INavigateToMainApp Target =>
            Microsoft.Maui.Controls.Application.Current as INavigateToMainApp
            ?? throw new InvalidOperationException("App is not running yet.");

        public void NavigateToMainApp() => Target.NavigateToMainApp();
        public void ReapplyDirection() => Target.ReapplyDirection();
    }

    private static FlowDirection CurrentFlowDirection
    {
        get
        {
            var loc = ServiceHelper.TryGet<ILocalizationService>();
            return loc is not null && loc.IsRightToLeft
                ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
    }

    private static void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
        => ReportUnhandled(e.ExceptionObject as Exception);

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportUnhandled(e.Exception);
        try { e.SetObserved(); } catch { /* never rethrow from the finalizer path */ }
    }

    /// <summary>Logs an unhandled exception and shows one friendly, localized alert at a time.</summary>
    private static void ReportUnhandled(Exception? ex)
    {
        try
        {
            Debug.WriteLine($"[LIVORA.fatal] {ex?.GetType().Name}: {ex?.Message}");
#if DEBUG
            // Debug-build crash forensics (Wave 3 merge): a file the tester can read when the
            // process dies inside native XAML before any dialog can show. Debug-only by design.
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(
                        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                        "livora_crash.log"),
                    DateTime.Now.ToString("O") + " FATAL " + ex?.GetType().FullName + ": " + ex?.Message + "\n" + ex?.ToString() + "\n----\n");
            }
            catch { }
#endif
        }
        catch { /* logging must never take the app down */ }

        if (Interlocked.CompareExchange(ref _errorDialogInFlight, 1, 0) != 0) return;

        try
        {
            var page = Current?.Windows?.FirstOrDefault()?.Page;
            if (page?.Dispatcher is null)
            {
                Interlocked.Exchange(ref _errorDialogInFlight, 0);
                return;
            }

            // AppResources.Culture is kept in sync by LocalizationService, so these resolve in the
            // active language; the literals are the invariant-culture safety net if DI isn't ready yet.
            string title, body, dismiss;
            try
            {
                title = LIVORA.Resources.Localization.AppResources.ErrorDialog_Title;
                body = LIVORA.Resources.Localization.AppResources.ErrorDialog_Body;
                var loc = ServiceHelper.TryGet<ILocalizationService>();
                dismiss = loc?["Common.Done"] ?? "OK";
            }
            catch
            {
                title = "Something went wrong";
                body = "LIVORA hit an unexpected error. Your data is safe on this device.";
                dismiss = "OK";
            }

            page.Dispatcher.Dispatch(async () =>
            {
                try { await page.DisplayAlertAsync(title, body, dismiss); }
                catch { /* last resort: a failed dialog must not escalate */ }
                finally { Interlocked.Exchange(ref _errorDialogInFlight, 0); }
            });
        }
        catch
        {
            Interlocked.Exchange(ref _errorDialogInFlight, 0);
        }
    }

    /// <summary>
    /// WHAT GUARANTEES WHAT:
    ///   • First launch: <see cref="CreateWindow"/> sets FlowDirection on the root page at
    ///     creation — this is the one path every platform definitely honors, because the Shell
    ///     chrome is built with the direction already in place.
    ///   • Live language switch: this method pushes the direction onto every Window *and* its
    ///     Page. Verified headless against Microsoft.Maui.Controls 10.0.101 (net10.0 ref):
    ///     Window is itself a flow-direction source and the property change propagates down the
    ///     logical tree (Shell → sections → pages → labels), so content mirrors for sure.
    ///   • Native tab-bar chrome: mirroring the physical order of the strip relies on each
    ///     platform renderer reacting to that change (Android honors supportsRtl=true in the
    ///     manifest; WinUI's NavigationView maps FlowDirection on the realized platform view).
    ///     NOT verified on-device from this workspace — if some platform's strip keeps its LTR
    ///     order until rebuild, the INavigateToMainApp path (fresh Shell created in the right
    ///     direction) and the next cold start correct it. No fake claim here.
    ///   • Called before any window exists (MauiProgram startup): iterates an empty Windows
    ///     collection — a deliberate no-op; see first bullet for what covers that case.
    /// </summary>
    public static void ApplyFlowDirection()
    {
        if (Microsoft.Maui.Controls.Application.Current?.Windows is not { } windows) return;
        var flow = CurrentFlowDirection;
        foreach (var w in windows)
        {
            // Window first: its flow is the inheritance root for everything below it, including
            // the Shell chrome that is NOT the Page.
            w.FlowDirection = flow;
            if (w.Page is not null) w.Page.FlowDirection = flow;
        }
    }
}
