namespace LIVORA.Application.Abstractions;

/// <summary>
/// The seam ViewModels use to hand control to the main app surface (the Shell) and to re-apply
/// the language's flow direction — instead of constructing <c>AppShell</c> or poking
/// <c>Application.Current.Windows</c> themselves. Window plumbing belongs to the app layer
/// (implemented in <c>App.xaml.cs</c>, registered at the <c>WAVE3-DI</c> marker), not to VMs.
///
/// Guarantees (lane 04):
///   • <see cref="NavigateToMainApp"/> runs on the UI thread (commands do) and creates a fresh
///     Shell with the active direction already applied, so first paint is correct in both
///     languages.
///   • <see cref="ReapplyDirection"/> is a safe no-op when no window exists yet — during startup
///     <c>MauiProgram</c> calls it before any window is built; what guarantees first-launch
///     correctness is <c>App.CreateWindow</c> setting direction at creation instead.
///
/// This file is deliberately free of any Microsoft.Maui type so the plain-net10 test project
/// keeps compiling <c>Application/**</c>.
/// </summary>
public interface INavigateToMainApp
{
    /// <summary>Swap the current window's root page to the main Shell (post-onboarding, restart flow).</summary>
    void NavigateToMainApp();

    /// <summary>Push the active language's flow direction onto every open window and page.</summary>
    void ReapplyDirection();
}
