#if ANDROID
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Runtime.Versioning;
#endif
using LIVORA.Application.HealthData.Wave3bHealth;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.HealthProviders;

/// <summary> Wave 3b (lane 02): the Android-side <see cref="IHealthPlatformBridge"/> SHELL. </summary>
public sealed class HealthConnectAndroidBridge : IHealthPlatformBridge
{
    /// <summary>The Health Connect app package (Google's reference implementation on Android).</summary>
    public const string HealthConnectPackage = "com.google.android.apps.healthdata";

    /// <summary>Health Connect requires Android 8.0 (API 26) or newer.</summary>
    public const int MinimumSdk = 26;

    /// <summary>
    /// False until the AndroidX.Health.Connect client is compiled into this build (this wave forbids
    /// new NuGet packages, §0.10). Every read path must consult it: it is the difference between
    /// "the platform is there" and "we can actually read records", and the probe reports
    /// <see cref="BridgeErrorCategory.ApiNotBundled"/> while it stays false.
    /// </summary>
    public static readonly bool ApiBundled = false;

    // Dot-free machine tags for each branch of the probe (diagnostics; never rendered raw).
    public const string TagRecordsApiAbsent = "records-api-absent";
    public const string TagNonAndroidHead = "non-android-head";
    public const string TagPackageMissing = "health-connect-package-missing";
    public const string TagPermissionsMissing = "health-read-permissions-missing";
    public const string TagApiTooOld = "android-api-too-old";
    public const string TagNoContext = "no-android-context";

    /// <summary>
    /// The three Health Connect read permissions this integration needs. Declaring them in the
    /// Android manifest is a follow-up (Platforms/** is frozen this wave): while they are undeclared
    /// the OS reports the grant as undetermined forever, so the probe must not present that as a
    /// refusal by the user.
    /// </summary>
    public static readonly string[] ReadPermissions =
    {
        "android.permission.health.READ_SLEEP",
        "android.permission.health.READ_STEPS",
        "android.permission.health.READ_ACTIVE_MINUTES",
    };

    public string ProviderId => HealthConnectSource.Id;

    /// <summary>
    /// Records would be Health Connect records. No records are produced while
    /// <see cref="ApiBundled"/> is false, so this never labels data that does not exist.
    /// </summary>
    public DataOrigin RowOrigin => DataOrigin.HealthConnect;

    public BridgeProbeResult Probe()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        if (context is null)
            return new BridgeProbeResult(BridgeAvailability.Unavailable, BridgeErrorCategory.TransportFailure, TagNoContext);

        // Literal (not the MinimumSdk const) so the CA1416 flow analysis credits the guard: with a
        // 26 floor below it, the API-23 runtime-permission model always exists, so no second check.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return new BridgeProbeResult(BridgeAvailability.Unavailable, BridgeErrorCategory.PlatformUnsupported, TagApiTooOld);

        var installed = context.PackageManager?.GetLaunchIntentForPackage(HealthConnectPackage) is not null;
        if (!installed)
            return new BridgeProbeResult(BridgeAvailability.NotInstalled, BridgeErrorCategory.None, TagPackageMissing);

        if (!AllReadPermissionsGranted(context))
            return new BridgeProbeResult(BridgeAvailability.NeedsPermission, BridgeErrorCategory.None, TagPermissionsMissing);

        // Platform + app + grant are real, but the record client is not compiled in. Reporting
        // Ready would advertise a feed this build cannot read, so the honest verdict is
        // Unavailable with a named category; the tag keeps the reason auditable.
        if (!ApiBundled)
            return new BridgeProbeResult(BridgeAvailability.Unavailable, BridgeErrorCategory.ApiNotBundled, TagRecordsApiAbsent);

        return BridgeProbeResult.Ready();
#else
        return new BridgeProbeResult(BridgeAvailability.Unavailable, BridgeErrorCategory.PlatformUnsupported, TagNonAndroidHead);
#endif
    }

    public PermissionState QueryPermission()
    {
#if ANDROID
        var context = Android.App.Application.Context;
        if (context is null) return PermissionState.NotDetermined;
        // Same literal floor as Probe: below API 26 there is no Health Connect to have granted.
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return PermissionState.NotDetermined;
        return AllReadPermissionsGranted(context) ? PermissionState.Granted : PermissionState.NotDetermined;
#else
        return PermissionState.NotDetermined;
#endif
    }

    /// <inheritdoc/> <remarks>Honest no-op until the client ships (see the class doc): returns the query, so the
    /// caller's budget is spent on nothing rather than on a dialog whose answer nobody observes.</remarks>
    public Task<PermissionState> RequestPermissionAsync(CancellationToken ct = default) =>
        Task.FromResult(QueryPermission());

    public Task<IReadOnlyList<HealthRawRow>> ReadRowsAsync(
        DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default) =>
        // Empty until the client is bundled (ApiBundled). The mapper's "no rows -> no day" rule is
        // what keeps this from ever becoming a silent zero in the UI.
        Task.FromResult<IReadOnlyList<HealthRawRow>>(Array.Empty<HealthRawRow>());

    /// <summary>Diagnostics tag for why a fully-granted platform still returns no records.</summary>
    public static string RecordsDiagnosticsTag => ApiBundled ? string.Empty : TagRecordsApiAbsent;

#if ANDROID
    [SupportedOSPlatform("android23.0")]
    private static bool AllReadPermissionsGranted(Context context)
    {
        foreach (var permission in ReadPermissions)
        {
            try
            {
                if (context.CheckSelfPermission(permission) != Permission.Granted) return false;
            }
            catch (Exception)
            {
                return false;   // an OS that rejects the query is a "not granted", never a grant
            }
        }
        return true;
    }
#endif
}

/// <summary>
/// This lane's Android state as data instead of a comment: which parts of the integration are live
/// and which are pending. <c>Wave3bHealthProviderTests</c> asserts the table, so the claim cannot
/// drift out of sync with the code the way a prose TODO would.
/// </summary>
public static class Wave3bProbeContract
{
    /// <summary>True while reads are structurally impossible in this build (client not bundled).</summary>
    public static bool ReadsPending => !HealthConnectAndroidBridge.ApiBundled;

    /// <summary>The category a pending-API Android probe must carry.</summary>
    public static BridgeErrorCategory PendingCategory => BridgeErrorCategory.ApiNotBundled;

    /// <summary>The permission category the request path will use once the grant UI is observable.</summary>
    public static AppPermission PermissionSlot => AppPermission.Health;
}
