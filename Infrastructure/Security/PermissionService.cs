using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Infrastructure.Security;
/// <summary>
/// Phase 2 permission abstraction. NO real permission requests are made yet — the point is
/// that feature ViewModels talk to this interface instead of platform APIs, so Wave 3
/// integrations drop in without touching UI. Statuses are honest: "UnavailableInPhase".
///
/// SECURITY MODEL
/// <list type="bullet">
///   <item>Never prompts. <see cref="RequestAsync"/> is deliberately a no-op that returns the
///     current status: no platform permission dialog, no Health/Activity/Calendar/Location
///     API call, no system prompt. Requesting a permission with no integration behind it is a
///     trust debt, so the honest answer is "unavailable in this phase" — do not "fix" this into
///     prompting before Wave 3.</item>
///   <item>Local-only, in-memory state. The status table below is the whole state of this
///     service: it is not persisted, not read from the device, and not sent anywhere. Nothing
///     here touches the network (the app has no client), no logging, and no file writes, so it
///     cannot leak user-identifying data by construction.</item>
///   <item>No data grant follows a status. A status here does not unlock any data path: the only
///     data the app can read today is mock/sample data (see <c>SampleHealthProvider</c>) and
///     values the user typed themselves. Wiping that data is
///     <see cref="PrivacyService.DeleteAllLocalDataAsync"/>, which is independent of permissions.</item>
///   <item>Manifest hygiene matches the code: no health/activity/calendar/location permissions
///     are declared in any platform manifest either, so the OS never sees a request the app
///     would not honour.</item>
/// </list>
/// </summary>
public sealed class PermissionService : IPermissionService
{
    /// <summary>
    /// The complete, hardcoded status table. Unlisted permissions report
    /// <see cref="PermissionState.NotDetermined"/> — an honest "nothing has been asked or
    /// granted", never an implied grant.
    /// </summary>
    private static readonly Dictionary<AppPermission, PermissionState> Initial = new()
    {
        [AppPermission.Health] = PermissionState.UnavailableInPhase,
        [AppPermission.Activity] = PermissionState.UnavailableInPhase,
        [AppPermission.Calendar] = PermissionState.UnavailableInPhase,
        [AppPermission.Location] = PermissionState.NotDetermined,
        [AppPermission.Notifications] = PermissionState.NotDetermined,
    };

    /// <summary>Reads the static status table only; no platform call, no side effect.</summary>
    public PermissionState GetStatus(AppPermission permission) =>
        Initial.TryGetValue(permission, out var s) ? s : PermissionState.NotDetermined;

    /// <summary>
    /// Does NOT request anything. Returns the current status unchanged — by design in Wave 2.
    /// Wave 3 replaces the body with real platform calls; callers must keep treating any
    /// non-<see cref="PermissionState.Granted"/> result as "no data available".
    /// </summary>
    public Task<PermissionState> RequestAsync(AppPermission permission)
    {
        // Wave 2 policy: never prompt for a permission we cannot yet use. This keeps us
        // from asking users for Health access with no integration behind it (trust debt).
        return Task.FromResult(GetStatus(permission));
    }
}
