using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace LIVORA
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Diagnostic net: a MAUI binding/render exception can surface as a bare
            // "Exception thrown: NullReferenceException" in the VS output with no stack.
            // Mirror every unhandled managed exception to logcat under LIVORA.CRASH so a
            // device crash is never silent. Remove once the app is stable.
            System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Android.Util.Log.WriteLine(Android.Util.LogPriority.Assert, "LIVORA.CRASH",
                    "UNHANDLED: " + (e.ExceptionObject?.ToString() ?? "null"));
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
                Android.Util.Log.WriteLine(Android.Util.LogPriority.Assert, "LIVORA.CRASH",
                    "UNOBSERVED-TASK: " + e.Exception?.ToString());
            base.OnCreate(savedInstanceState);
        }
    }
}
