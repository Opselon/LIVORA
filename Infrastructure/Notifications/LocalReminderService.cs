using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.Reminders;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using Plugin.LocalNotification;
using PluginLocalNotification = Plugin.LocalNotification;
using PluginModels = Plugin.LocalNotification.Core.Models;

namespace LIVORA.Infrastructure.Notifications;

/// <summary>
/// Local notifications on Plugin.LocalNotification 14.1.2 (lane 09).
///
/// HONESTY CONTRACT (product law):
/// <list type="bullet">
///   <item><see cref="GetGrantStateAsync"/> never returns <see cref="NotificationGrantState.Allowed"/>
///     unless the plugin's real status call said notifications are enabled. Any platform error maps to
///     <see cref="NotificationGrantState.Unknown"/> — never a guessed "allowed" or "denied".</item>
///   <item>The "asked once" flag lives in <c>livora_reminders.json</c>, so "never asked" and
///     "you said no" stay distinguishable across restarts.</item>
///   <item><see cref="ScheduleTestAsync"/> returns the plugin's real <c>Show()</c> result. A true means
///     "handed to the OS scheduler", NOT "the user saw it" — the UI wording must say that (and does).</item>
///   <item>Per-day dedupe: engine-driven immediate nudges record the fired day BEFORE showing, so a
///     failure cannot re-nag, and a success never fires twice the same local day.</item>
/// </list>
///
/// Scheduling model: every enabled setting gets one OS request per selected weekday of its
/// <see cref="ReminderSetting.DaysMask"/> (Weekly repeat at the setting's time), so the OS handles
/// firing while the app is closed. The platform API used here (Show/Cancel/permission status) has
/// NOT been exercised on a physical device in this lane — see lane report NOTES.
/// </summary>
public sealed class LocalReminderService : IReminderService, IReminderLedger, IReminderEvaluator
{
    private const int TestNotificationId = 987_654;

    private readonly ReminderStore _store;
    private readonly ILocalizationService _loc;
    private readonly IDateTimeProvider _clock;
    private readonly IUserStateService _state;
    private readonly IRepository<Habit> _habits;
    private readonly IRepository<Bootcamp> _bootcamps;
    private readonly IManualEntryService _manual;
    private readonly SessionState _session;
    private bool _tapHooked;
    private readonly object _gate = new();

    public LocalReminderService(
        ReminderStore store,
        ILocalizationService loc,
        IDateTimeProvider clock,
        IUserStateService state,
        IRepository<Habit> habits,
        IRepository<Bootcamp> bootcamps,
        IManualEntryService manual,
        SessionState session)
    {
        _store = store;
        _loc = loc;
        _clock = clock;
        _state = state;
        _habits = habits;
        _bootcamps = bootcamps;
        _manual = manual;
        _session = session;
    }

    private static PluginLocalNotification.INotificationService Api => PluginLocalNotification.LocalNotificationCenter.Current;

    // ---- settings persistence -------------------------------------------------

    public async Task<IReadOnlyList<ReminderSetting>> GetRemindersAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        // Seed any MISSING built-in kind (not just an empty file): habit-bound rows written by
        // lane 07's editor can already be here, and the settings page must still show the four
        // in-app controls the client asked for. Enabled by default is safe — nothing can fire
        // until the OS grant says so, and the grant banner states exactly that.
        bool added = false;
        foreach (var kind in ReminderEngine.BuiltInKinds)
        {
            if (file.Reminders.Any(r => r.Kind == kind && r.TargetId is null)) continue;
            file.Reminders.Add(new ReminderSetting
            {
                Id = "builtin-" + kind,
                Kind = kind,
                TargetId = null,
                Enabled = true,
                TimeOfDay = ReminderEngine.DefaultTimeFor(kind),
                DaysMask = 0b1111111,
                TextKey = ReminderEngine.TextKeyFor(kind),
            });
            added = true;
        }
        if (added) await _store.SaveAsync(file, ct);
        return file.Reminders;
    }

    public async Task SaveAsync(ReminderSetting reminder, CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        int idx = file.Reminders.FindIndex(r => r.Id == reminder.Id);
        if (idx >= 0) file.Reminders[idx] = reminder;
        else file.Reminders.Add(reminder);
        await _store.SaveAsync(file, ct);
        await SyncAsync(ct);
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        file.Reminders.RemoveAll(r => r.Id == id);
        if (file.ScheduledIds.TryGetValue(id, out var ids))
        {
            Try(() => Api.Cancel(ids.ToArray()));
            file.ScheduledIds.Remove(id);
        }
        await _store.SaveAsync(file, ct);
    }

    // ---- fired map (per-day dedupe) -------------------------------------------

    public async Task<IReadOnlyDictionary<string, DateTime>> GetFiredDaysAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        return file.FiredDays;
    }

    public Task MarkFiredAsync(string fireKey, DateTime localDay, CancellationToken ct = default) =>
        _store.MarkFiredAsync(fireKey, localDay, ct);

    // ---- grant state: exactly what the platform answered -----------------------

    public async Task<NotificationGrantState> GetGrantStateAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        return await ReadGrantAsync(file, ct);
    }

    private async Task<NotificationGrantState> ReadGrantAsync(ReminderFile file, CancellationToken ct)
    {
        try
        {
            if (!Api.IsSupported) return NotificationGrantState.Unsupported;
            var status = await Api.GetNotificationPermissionStatus();
            if (status is null) return NotificationGrantState.Unknown;
            if (status.IsEnabled || status.IsAlertEnabled) return NotificationGrantState.Allowed;
            // Not enabled: distinguish "we never asked" from "asked and the answer was no".
            return file.PermissionAsked ? NotificationGrantState.Denied : NotificationGrantState.NotRequested;
        }
        catch
        {
            // Platform refused to answer — the honest answer is Unknown, not Denied/Allowed.
            return NotificationGrantState.Unknown;
        }
    }

    public async Task<NotificationGrantState> RequestGrantAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        try
        {
            if (!Api.IsSupported) return NotificationGrantState.Unsupported;
            var permission = new PluginModels.NotificationPermission
            {
                AskPermission = true,
                // Exact alarms are an OPTIONAL Android power-user permission (property name verified
                // against Plugin.LocalNotification.Core 1.1.2); we schedule ordinary notifications and
                // must not ask for more than the feature needs.
                Android = { RequestPermissionToScheduleExactAlarm = false },
            };
            await Api.RequestNotificationPermission(permission);
            file.PermissionAsked = true;
            await _store.SaveAsync(file, ct);
            // Re-read the REAL state afterwards — the request call's bool is coarse; the status call
            // is what the banner reports.
            return await ReadGrantAsync(file, ct);
        }
        catch
        {
            file.PermissionAsked = true;
            await _store.SaveAsync(file, ct);
            return NotificationGrantState.Unknown;
        }
    }

    // ---- startup hook ------------------------------------------------------------

    /// <summary>
    /// Called once from the composition root (WAVE3-DI block): pushes persisted reminders into
    /// the OS scheduler at every launch and installs the tap→Today hook. Safe when notifications
    /// are denied — the recurring schedule still belongs to the OS (the user may have granted it
    /// while the app was closed), and the late-day nudge pass simply no-ops on a non-Allowed grant.
    /// Never throws: a platform that refuses to answer must not take the app down at boot.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            await SyncAsync(ct);
            // Cold start from a tap: the live event only fires while the process is alive, so a
            // notification that LAUNCHED the app is read from the plugin's launch details instead.
            try
            {
                if (PluginLocalNotification.LocalNotificationCenter.LaunchNotificationDetails
                        ?.DidNotificationLaunchApp == true)
                    NavigateToToday();
            }
            catch { /* head without launch details — Today is the default tab anyway */ }
        }
        catch { /* boot must never fail because the notification stack refused to answer */ }
    }

    // ---- scheduling ------------------------------------------------------------

    /// <summary>
    /// Idempotent push of every enabled reminder into the OS scheduler: cancel this file's own ids
    /// (never other apps'), re-schedule one Weekly request per masked weekday at the setting's time,
    /// then let the pure engine post AT MOST ONE late-day nudge for what is due right now and has
    /// not fired today (per-day dedupe; requires an Allowed grant).
    /// </summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        HookTapNavigation();
        var file = await _store.LoadAsync(ct);
        var now = _clock.Now;

        // 1) Wipe this app's scheduled notifications before re-seeding. CancelAll is deliberate:
        // every OS notification LIVORA schedules belongs to this reminder pipeline (per-id cancel
        // would orphan rows after a privacy wipe deletes the JSON that tracked the ids), and the
        // plugin only ever holds what SyncAsync/test put there.
        Try(() => Api.CancelAll());
        file.ScheduledIds.Clear();

        // 2) Re-schedule every enabled setting (recurring, per weekday in DaysMask).
        if (Api.IsSupported)
        {
            foreach (var s in file.Reminders.Where(r => r.Enabled))
            {
                var ids = new List<int>();
                for (int dow = 0; dow < 7; dow++)
                {
                    if (!ReminderEngine.DayMatches(s.DaysMask, (DayOfWeek)dow)) continue;
                    var first = NextDateFor(now, (DayOfWeek)dow, s.TimeOfDay);
                    int id = StableId(s.Id, dow);
                    var request = BuildRequest(id, s, first, PluginModels.NotificationRepeat.Weekly);
                    if (await SafeShowAsync(request)) ids.Add(id);
                }
                if (ids.Count > 0) file.ScheduledIds[s.Id] = ids;
            }
        }

        // 3) Late-day engine pass: a due-right-now candidate fires once per day, and only when the
        //    OS actually allowed notifications. Record-before-show so a crash mid-pass cannot nag.
        var grant = await ReadGrantAsync(file, ct);
        if (grant == NotificationGrantState.Allowed && Api.IsSupported)
        {
            var candidates = await EvaluateNowAsync(file, ct);
            var due = candidates.FirstOrDefault(c => c.FireAt <= now);
            if (due is not null)
            {
                // Record on THIS copy of the file (the single save at the end persists it). Calling
                // MarkFiredAsync here would write a fresh copy that the final SaveAsync would then
                // overwrite — losing the dedupe mark and allowing a second nag the same day.
                file.FiredDays[ReminderEngine.FireKey(due.Kind, due.TargetId)] = now.Date;
                var body = BuildLocalizedBody(due.TextKey, due.TextArgs);
                var req = new PluginModels.NotificationRequest
                {
                    NotificationId = StableId("catchup|" + due.Kind + "|" + (due.TargetId ?? "*"), 0),
                    Title = _loc["Notification.Title"],
                    Description = body,
                    ReturningData = TapPayload(due.Kind),
                    Silent = false,
                };
                await SafeShowAsync(req);
            }
        }

        await _store.SaveAsync(file, ct);
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        var all = file.ScheduledIds.Values.SelectMany(v => v).ToList();
        Try(() => Api.Cancel(all.ToArray()));
        Try(() => Api.Cancel(TestNotificationId, null));
        file.ScheduledIds.Clear();
        file.FiredDays.Clear();
        await _store.SaveAsync(file, ct);
    }

    public async Task<bool> ScheduleTestAsync(TimeSpan delay, string textKey, CancellationToken ct = default)
    {
        try
        {
            var file = await _store.LoadAsync(ct);
            Try(() => Api.Cancel(new[] { TestNotificationId }));  // re-test replaces the old test row
            var req = new PluginModels.NotificationRequest
            {
                NotificationId = TestNotificationId,
                Title = _loc["Notification.Title"],
                Description = _loc[textKey],
                ReturningData = TapPayload("test"),
                Schedule =
                {
                    NotifyTime = new DateTimeOffset(_clock.Now + delay),
                    RepeatType = PluginModels.NotificationRepeat.No,
                },
            };
            var granted = await Api.Show(req);
            // Show() returning true means "accepted by the platform scheduler", not "displayed".
            return granted;
        }
        catch
        {
            return false; // the UI reports the failure verbatim — honesty rule
        }
    }

    // ---- engine helpers exposed for the Reminders page (in-app preview list) ----

    /// <summary>Pure-engine evaluation with settings + fired map + live inputs. Public so the
    /// Reminders/Today UI can show what will notify the user, explained by key.</summary>
    public async Task<IReadOnlyList<ReminderCandidate>> EvaluateNowAsync(CancellationToken ct = default)
    {
        var file = await _store.LoadAsync(ct);
        return await EvaluateNowAsync(file, ct);
    }

    private async Task<IReadOnlyList<ReminderCandidate>> EvaluateNowAsync(ReminderFile file, CancellationToken ct)
    {
        var now = _clock.Now;
        Domain.Models.State.PersonalState? state = null;
        try { state = await _state.GetStateAsync(DataRefreshMode.Resume, ct); }
        catch { state = null; }   // no state ⇒ state-gated kinds stay silent (never fabricate)
        var habits = await _habits.GetAllAsync();
        var bootcamps = await _bootcamps.GetAllAsync();
        bool loggedToday = await HasLoggedTodayAsync(ct);
        return ReminderEngine.Evaluate(
            file.Reminders, file.FiredDays, state, habits, bootcamps, loggedToday, now);
    }

    private async Task<bool> HasLoggedTodayAsync(CancellationToken ct)
    {
        try
        {
            var entry = await _manual.GetForDayAsync(_clock.Today, ct);
            return entry is not null && !entry.IsEmpty;
        }
        catch { return true; } // cannot know ⇒ do NOT nag (fail silent, not fail annoying)
    }

    // ---- tap → open Today --------------------------------------------------------

    /// <summary>Wired once: tapping any LIVORA reminder lands the user on Today. Defensive: the
    /// event can fire during cold start before Shell exists, and navigation must never crash the
    /// notification pipeline.</summary>
    public void HookTapNavigation()
    {
        lock (_gate)
        {
            if (_tapHooked) return;
            _tapHooked = true;
        }
        try
        {
            Api.NotificationActionTapped += _ => NavigateToToday();
        }
        catch { /* unsupported head — reminders simply don't deep-link there */ }
    }

    private static void NavigateToToday()
    {
        // `Application` alone resolves to the LIVORA.Application namespace here — fully qualify.
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                if (Shell.Current is not null) await Shell.Current.GoToAsync("//Today");
                else if (Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault()?.Page is not null)
                { /* launched cold onto the default page (Today tab) — nothing to do */ }
            }
            catch { /* a failed deep link must never crash the app */ }
        });
    }

    // ---- plumbing ------------------------------------------------------------------

    private PluginModels.NotificationRequest BuildRequest(
        int id, ReminderSetting s, DateTimeOffset notifyAt, PluginModels.NotificationRepeat repeat)
    {
        return new PluginModels.NotificationRequest
        {
            NotificationId = id,
            Title = _loc["Notification.Title"],
            Description = BuildLocalizedBody(s.TextKey, s.TextArgs),
            ReturningData = TapPayload(s.Kind),
            Schedule =
            {
                NotifyTime = notifyAt,
                RepeatType = repeat,
            },
        };
    }

    /// <summary>Body text resolved at scheduling time in the CURRENT app language. Re-syncing on
    /// language change (the Reminders page does it) refreshes the wording.</summary>
    private string BuildLocalizedBody(string textKey, object[] args)
    {
        try { return args.Length == 0 ? _loc[textKey] : _loc.T(textKey, args); }
        catch { return _loc[textKey]; }
    }

    /// <summary>Small payload so a tap could be attributed to a kind without re-reading files.</summary>
    private string TapPayload(string kind) => "livora.reminder." + kind + "." + _session.CurrentProfile.Id;

    /// <summary>Next date at or after <paramref name="now"/> whose day-of-week and time match.</summary>
    private static DateTimeOffset NextDateFor(DateTime now, DayOfWeek dow, TimeSpan time)
    {
        for (int i = 0; i <= 7; i++)
        {
            var day = now.Date.AddDays(i);
            if (day.DayOfWeek != dow) continue;
            var at = day + time;
            if (at > now) return new DateTimeOffset(at);
        }
        return new DateTimeOffset(now.Date.AddDays(7) + time);
    }

    /// <summary>Deterministic positive notification id for (setting, weekday): FNV-1a folded below
    /// the test/catch-up ranges. Stored per setting in the JSON file, so a future hash change can
    /// never orphan rows (cancel uses the stored list).</summary>
    private static int StableId(string settingId, int dow)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in settingId) { h ^= c; h *= 16777619; }
            h ^= (uint)(dow * 2654435761);
            return 1000 + (int)(h % 500_000);   // 1_000 .. 500_999 — avoids the fixed ids
        }
    }

    /// <summary>Show() is async and platform-flaky: awaited here so an unobserved Task can never
    /// swallow a failure, and the stored id list only contains what the platform actually accepted.</summary>
    private static async Task<bool> SafeShowAsync(PluginModels.NotificationRequest request)
    {
        try { return await Api.Show(request); }
        catch { return false; }
    }

    /// <summary>Platform calls return bool / throw on some heads; a failure here must never take
    /// the sync path down. Swallowed deliberately — the banner + test button are where truth is shown.</summary>
    private static bool Try(Func<bool> call)
    {
        try { return call(); }
        catch { return false; }
    }

    private static bool Try(Action call)
    {
        try { call(); return true; }
        catch { return false; }
    }
}
