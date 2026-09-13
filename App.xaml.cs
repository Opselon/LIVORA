using System.Diagnostics;
using LIVORA.Application.Abstractions;
using LIVORA.Presentation;
using LIVORA.Presentation.Views;

namespace LIVORA;
public partial class App : Microsoft.Maui.Controls.Application
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
        // WAVE3-APP: window/theme bootstrap hook (lane 04 supplies the block: Windows default
        // size, IThemeService.Apply, RequestedThemeChanged). Applied once by the orchestrator.
        // WAVE3-APP-END
        var loc = ServiceHelper.Get<ILocalizationService>();
        var settings = ServiceHelper.Get<ISettingsService>();

        var onboardingDone = settings.OnboardingCompleted;
        var page = onboardingDone ? (Page)new AppShell() : new OnboardingPage();
        // The Shell itself (tabs/flyout chrome) needs the direction set at creation —
        // child pages set their own in BaseContentPage.
        page.FlowDirection = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        return new Window(page);
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
    /// Applies the active language's flow direction to the running app (Shell + all open pages).
    /// Called once at startup and again whenever the language changes — no restart required.
    /// </summary>
    public static void ApplyFlowDirection()
    {
        var loc = ServiceHelper.Get<ILocalizationService>();
        var flow = loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        if (Microsoft.Maui.Controls.Application.Current?.Windows is { } windows)
        {
            foreach (var w in windows)
            {
                if (w.Page is not null) w.Page.FlowDirection = flow;
            }
        }
        // Defensive: also set the static default so newly opened pages inherit it.
        Microsoft.Maui.Controls.Application.Current?.Resources["AppFlowDirection"] = flow;
    }
}
