using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Constants;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Domain.Models.History;
using LIVORA.Infrastructure.Security;

namespace LIVORA.Infrastructure.Persistence;

/// <summary>
/// Wave 3c (lane 01) — the local data catalog: the ONE seam that inspects, exports, imports and
/// deletes every stored entity by kind, reading and writing the SAME files the rest of persistence
/// uses (see <see cref="FileForKind"/>). No parallel database, no second copy of the user's data:
/// portability is a view over the existing stores, so an edit made here is the edit the UI sees.
///
/// MAUI-free by construction — the data directory arrives as an injected <see cref="LocalJsonStore"/>,
/// so the plain-net10 test head exercises the real export/import code, not a stub.
///
/// STRICT INPUT. Every payload goes through <c>JsonDocument</c> and is rejected unless it is exactly
/// what that store's own schema allows:
/// <list type="bullet">
///   <item>unknown field (any name that is not a property of the domain type) → <c>UnknownField</c>;</item>
///   <item>literal NaN / Infinity, or a number that is not finite → <c>NotFinite</c>;</item>
///   <item>out-of-range or impossible value (sleep &gt; 1440 min, ratio &gt; 1, a completion in the
///     future, current day past program length, day count ≠ DurationDays) → <c>Range</c> / <c>Type</c>;</item>
///   <item>inverted date pair (a manual entry saved before the day it describes; a date before 2000)
///     → <c>DateInverted</c>;</item>
///   <item>payload over 1 MB → <c>TooLarge</c>.</item>
/// </list>
/// The allow-list is READ FROM THE DOMAIN TYPES by reflection, so a new <c>Goal</c> property becomes
/// importable the moment it exists and there is no second schema to keep in sync (the drift that
/// makes "strict validation" files rot).
///
/// WHY DERIVED PROPERTIES ARE ACCEPTED BUT NOT TRUSTED: the stores serialize read-only members
/// (<c>Fraction</c>, <c>Status</c>, <c>CurrentStreak</c>, <c>Today</c>, <c>CompletionFraction</c>).
/// Rejecting them would break round-tripping the app's own exports; validating them would be theatre,
/// because the model recomputes them from the fields above on load (System.Text.Json ignores a
/// setter-less property). So an import cannot forge <c>Completed</c> onto a goal — and a hand-written
/// <c>Origin: "Garmin"</c> on a manual entry CAN be forged, which is why that one field is
/// cross-checked against the provenance law (<see cref="KeyImpossibleProvenance"/>).
///
/// HONEST ERRORS: the return value is localization KEYS plus a machine-only suffix (kind/field name),
/// never prose — the UI localizes them (lane 07's DataStudio error rows) and no user-supplied text is
/// ever echoed into a key. New keys ship in <c>wave3c-keys/lane01.*.keys.xml</c>.
///
/// WHOLE-STORE IMPORT IS TWO-PHASE: phase 1 validates every kind and touches no file; any error
/// aborts with nothing written (byte-identical stores — asserted by test). Only a clean phase 1
/// reaches phase 2, where every file goes out through <see cref="LocalJsonStore.WriteRawAtomic"/>
/// (temp file + <c>File.Replace</c>), so a crash mid-write cannot leave a half-written store.
/// <c>merge=false</c> replaces the kinds the document mentions and leaves the others alone;
/// <c>merge=true</c> upserts by id on top of what is already there.
/// </summary>
public sealed class LocalDataCatalog : ILocalDataCatalogService
{
    /// <summary>Bump only on a deliberate breaking change to the portable document shape.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Hard cap for one entity payload and for one whole-store document (UTF-8 bytes).</summary>
    public const int MaxPayloadBytes = 1024 * 1024;

    /// <summary>Anything before this year in a data file is corruption, not history.</summary>
    internal static readonly DateTime FloorDate = new(2000, 1, 1);

    // ---- kind tokens (machine identifiers; the UI localizes them by key) ------------
    public const string KindGoals = "goals";
    public const string KindHabits = "habits";
    public const string KindBootcamps = "bootcamps";
    public const string KindHistory = "history";
    public const string KindManual = "manual";
    public const string KindSettings = "settings";
    public const string KindConsents = "consents";
    public const string KindReminders = "reminders";

    /// <summary>Stable order for the advanced screen and for export.</summary>
    public static readonly IReadOnlyList<string> Kinds =
        new[] { KindGoals, KindHabits, KindBootcamps, KindHistory, KindManual, KindSettings, KindConsents, KindReminders };

    /// <summary>The key/value settings bag holds one entity, addressed by this id.</summary>
    public const string SettingsEntityId = "settings";

    /// <summary>Reminder rows live inside lane 09's object file under this property.</summary>
    internal const string ReminderListProperty = "Reminders";

    /// <summary>Named by lane 09's <c>ReminderStore.FileName</c>, which this file must not depend on.</summary>
    public const string ReminderStoreFileName = "livora_reminders.json";

    /// <summary>Same literal as <c>ManualEntryStore.EntriesFileName</c>, restated because that class
    /// sits behind the MAUI-only <c>JsonFileStore</c> and this file must compile in the plain
    /// net10.0 test head. The <c>OwnedFileNamesMatchTheStoreOwners</c> test pins equality.</summary>
    public const string ManualEntriesFileName = "livora_manual_entries.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private readonly LocalJsonStore _root;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();

    public LocalDataCatalog(LocalJsonStore rootStore, Func<DateTime>? utcNow = null)
    {
        _root = rootStore ?? throw new ArgumentNullException(nameof(rootStore));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Kind → the file that actually holds it. One mapping, shared with the file owners.</summary>
    public static string FileForKind(string kind) => kind switch
    {
        KindGoals => AppConstants.GoalsFile,
        KindHabits => AppConstants.HabitsFile,
        KindBootcamps => AppConstants.BootcampsFile,
        KindHistory => AppConstants.HistoryFile,
        KindManual => ManualEntriesFileName,
        KindSettings => AppConstants.SettingsFileName,
        KindConsents => ConsentStore.FileName,
        KindReminders => ReminderStoreFileName,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown entity kind."),
    };

    /// <summary>Every file the catalog can reach (the wipe test asserts coverage through this).</summary>
    public static IReadOnlyList<string> FilesForAllKinds =>
        Kinds.Select(FileForKind).Distinct(StringComparer.Ordinal).ToList();

    public Task<IReadOnlyList<string>> GetKindsAsync(CancellationToken ct = default) => Task.FromResult(Kinds);

    // ============================== single entity ==============================

    /// <summary>Pretty JSON for one entity. Throws <see cref="InvalidOperationException"/> carrying a
    /// rejection key when the kind/id is unknown — the contract has no error channel of its own.</summary>
    public Task<string> ExportEntityAsync(string kind, string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!Kinds.Contains(kind, StringComparer.Ordinal)) throw Reject(KeyUnknownKind, kind);
            if (string.IsNullOrWhiteSpace(id)) throw Reject(KeyEmpty, kind);

            if (kind == KindSettings)
            {
                if (id != SettingsEntityId) throw Reject(KeyNotFound, kind);
                var raw = _root.ReadRaw(FileForKind(kind));
                if (raw is null) throw Reject(KeyNotFound, kind);   // absent bag = not found, not "{}"
                var bag = ParseNode(raw) as JsonObject;
                if (bag is null) throw Reject(KeyStoreCorrupt, kind);
                return Task.FromResult(Render(bag));
            }

            var items = ReadKindRaw(kind, out bool corrupt);
            if (corrupt) throw Reject(KeyStoreCorrupt, kind);
            var match = items.FirstOrDefault(e => IdOf(kind, e) == id.Trim());
            if (match is null) throw Reject(KeyNotFound, kind);
            return Task.FromResult(Render(match));
        }
    }

    public Task<IReadOnlyList<string>> ImportEntityAsync(string kind, string json, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var errors = ValidatePayload(kind, json);
            if (errors.Count > 0) return Task.FromResult<IReadOnlyList<string>>(errors);

            var incoming = PayloadNodes(kind, json!);

            if (kind == KindSettings)
            {
                _root.WriteRawAtomic(FileForKind(kind), Render(incoming[0]));
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var current = ReadKindRaw(kind, out bool corrupt);
            if (corrupt) return Task.FromResult<IReadOnlyList<string>>(new[] { Prefix(KeyStoreCorrupt, kind) });
            foreach (var item in incoming) Upsert(kind, current, item);
            WriteKind(kind, current);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    public Task<IReadOnlyList<string>> DeleteEntityAsync(string kind, IEnumerable<string> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            if (!Kinds.Contains(kind, StringComparer.Ordinal))
                return Task.FromResult<IReadOnlyList<string>>(new[] { Prefix(KeyUnknownKind, kind) });

            var wanted = ids.Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
            if (wanted.Count == 0)
                return Task.FromResult<IReadOnlyList<string>>(new[] { Prefix(KeyEmpty, kind) });

            if (kind == KindSettings)
            {
                if (!wanted.Contains(SettingsEntityId, StringComparer.Ordinal))
                    return Task.FromResult<IReadOnlyList<string>>(new[] { Prefix(KeyNotFound, kind) });
                _root.Delete(FileForKind(kind));
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var items = ReadKindRaw(kind, out bool corrupt);
            if (corrupt) return Task.FromResult<IReadOnlyList<string>>(new[] { Prefix(KeyStoreCorrupt, kind) });

            var errors = new List<string>();
            foreach (var id in wanted)
                if (!items.Any(e => IdOf(kind, e) == id)) errors.Add(Prefix(KeyNotFound, kind + "." + id));

            items.RemoveAll(e => wanted.Contains(IdOf(kind, e), StringComparer.Ordinal));
            if (items.Count == 0) _root.Delete(FileForKind(kind));
            else WriteKind(kind, items);

            return Task.FromResult<IReadOnlyList<string>>(Distinct(errors));
        }
    }

    // ============================== whole store ==============================

    public Task<string> ExportAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var kinds = new JsonObject();
            foreach (var kind in Kinds)
            {
                if (kind == KindSettings)
                {
                    kinds[kind] = ParseNode(_root.ReadRaw(FileForKind(kind))) as JsonObject ?? new JsonObject();
                    continue;
                }
                var arr = new JsonArray();
                foreach (var item in ReadKindRaw(kind, out _)) arr.Add(item);
                kinds[kind] = arr;
            }

            var doc = new JsonObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["appName"] = AppConstants.AppName,
                ["exportedAtUtc"] = _utcNow().ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ["kinds"] = kinds,
            };
            return Task.FromResult(Render(doc));
        }
    }

    /// <summary>
    /// Two-phase whole-store import (see the class comment). Returns every rejection key found in
    /// phase 1 and writes NOTHING when it returns any key.
    /// </summary>
    public Task<IReadOnlyList<string>> ImportAllAsync(string json, bool merge, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var errors = ValidateDocument(json);
            if (errors.Count > 0) return Task.FromResult<IReadOnlyList<string>>(errors);

            using var doc = Parse(json)!;
            var kindsNode = doc.RootElement.GetProperty("kinds");

            // ---- phase 1: validate every kind's payload; touch no file ----------
            var prepared = new List<(string Kind, List<JsonNode> Items)>();
            foreach (var entry in kindsNode.EnumerateObject())
            {
                var payload = entry.Value.GetRawText();
                var kindErrors = ValidatePayload(entry.Name, payload);
                if (kindErrors.Count > 0) { errors.AddRange(kindErrors); continue; }
                prepared.Add((entry.Name, PayloadNodes(entry.Name, payload)));
            }
            if (errors.Count > 0) return Task.FromResult<IReadOnlyList<string>>(Distinct(errors));

            // ---- phase 2: write, each file atomically ---------------------------
            foreach (var (kind, items) in prepared)
            {
                if (kind == KindSettings)
                {
                    _root.WriteRawAtomic(FileForKind(kind), Render(items[0]));
                    continue;
                }
                List<JsonNode> final;
                if (merge)
                {
                    final = ReadKindRaw(kind, out _);
                    foreach (var item in items) Upsert(kind, final, item);
                }
                else final = items;
                if (final.Count == 0) _root.Delete(FileForKind(kind));   // named-but-empty = cleared
                else WriteKind(kind, final);
            }
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    // ============================== validation ==============================

    /// <summary>Validate one payload against one kind's schema. Returns rejection keys (empty = accept).</summary>
    private List<string> ValidatePayload(string kind, string? json)
    {
        var errors = new List<string>();
        if (!Kinds.Contains(kind, StringComparer.Ordinal)) { errors.Add(Prefix(KeyUnknownKind, kind)); return errors; }
        if (string.IsNullOrWhiteSpace(json)) { errors.Add(Prefix(KeyEmpty, kind)); return errors; }
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes) { errors.Add(Prefix(KeyTooLarge, kind)); return errors; }
        if (HasNonFiniteToken(json)) { errors.Add(Prefix(KeyNotFinite, kind)); return errors; }

        using var doc = Parse(json);
        if (doc is null) { errors.Add(Prefix(KeyMalformed, kind)); return errors; }
        var root = doc.RootElement;

        if (kind == KindSettings)
        {
            if (root.ValueKind != JsonValueKind.Object) errors.Add(Prefix(KeyNotObject, kind));
            else foreach (var p in root.EnumerateObject()) ValidateSettingsValue(p, errors);
            return Distinct(errors);
        }

        var elements = EntityElements(root, kind);
        if (elements.Count == 0 && root.ValueKind is not JsonValueKind.Array)
        {
            errors.Add(Prefix(KeyNotObject, kind));
            return Distinct(errors);
        }
        // An empty array is a legal (if pointless) payload: nothing to validate.

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in elements)
        {
            if (el.ValueKind != JsonValueKind.Object) { errors.Add(Prefix(KeyNotObject, kind)); continue; }
            var allowed = AllowedFields(kind);
            foreach (var prop in el.EnumerateObject())
                if (!allowed.Contains(prop.Name)) errors.Add(Prefix(KeyUnknownField, kind + "." + prop.Name));

            var id = IdOf(kind, el);
            if (string.IsNullOrEmpty(id)) errors.Add(Prefix(KeyMissingId, kind));
            else if (!seen.Add(id)) errors.Add(Prefix(KeyDuplicateId, kind + "." + id));

            SpecFor(kind)(el, errors);
        }
        return Distinct(errors);
    }

    private List<string> ValidateDocument(string? json)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) { errors.Add(Prefix(KeyEmpty, "document")); return errors; }
        if (Encoding.UTF8.GetByteCount(json) > MaxPayloadBytes) { errors.Add(Prefix(KeyTooLarge, "document")); return errors; }
        if (HasNonFiniteToken(json)) { errors.Add(Prefix(KeyNotFinite, "document")); return errors; }

        using var doc = Parse(json);
        if (doc is null) { errors.Add(Prefix(KeyMalformed, "document")); return errors; }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            errors.Add(Prefix(KeyNotObject, "document"));
            return errors;
        }

        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Name is not ("schemaVersion" or "kinds" or "appName" or "exportedAtUtc"))
                errors.Add(Prefix(KeyUnknownField, "document." + prop.Name));

        if (doc.RootElement.TryGetProperty("schemaVersion", out var sv))
        {
            if (sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var v)) errors.Add(Prefix(KeyType, "schemaVersion"));
            else if (v > SchemaVersion) errors.Add(KeySchemaTooNew);
            else if (v < 1) errors.Add(Prefix(KeyRange, "schemaVersion"));
        }
        else errors.Add(Prefix(KeyMissingField, "schemaVersion"));

        if (doc.RootElement.TryGetProperty("kinds", out var kinds))
        {
            if (kinds.ValueKind != JsonValueKind.Object) errors.Add(Prefix(KeyType, "kinds"));
            else foreach (var k in kinds.EnumerateObject())
                    if (!Kinds.Contains(k.Name, StringComparer.Ordinal)) errors.Add(Prefix(KeyUnknownKind, k.Name));
        }
        else errors.Add(Prefix(KeyMissingField, "kinds"));

        return Distinct(errors);
    }

    // ---- per-kind field rules (physical walls + schema, nothing invented) ------

    private Action<JsonElement, List<string>> SpecFor(string kind) => kind switch
    {
        KindGoals => ValidateGoal,
        KindHabits => ValidateHabit,
        KindBootcamps => ValidateBootcamp,
        KindHistory => ValidateHistory,
        KindManual => ValidateManual,
        KindConsents => ValidateConsentRow,
        KindReminders => ValidateReminderRow,
        _ => (_, _) => { },
    };

    private void ValidateGoal(JsonElement e, List<string> errors)
    {
        Req(e, KindGoals, "Id", errors);
        Str(e, KindGoals, "Id", 80, errors); Str(e, KindGoals, "Name", 200, errors);
        Str(e, KindGoals, "Description", 4000, errors);
        Str(e, KindGoals, "NameKey", 120, errors); Str(e, KindGoals, "MetricKey", 120, errors);
        Str(e, KindGoals, "StrategyKey", 120, errors);
        EnumOf(e, KindGoals, "Category", errors, typeof(GoalCategory));
        EnumOf(e, KindGoals, "Period", errors, typeof(GoalPeriod));
        EnumOf(e, KindGoals, "Unit", errors, typeof(GoalUnit));
        EnumOf(e, KindGoals, "Measurement", errors, typeof(GoalMeasurement));
        Num(e, KindGoals, "TargetValue", 0, 1_000_000, errors);
        Num(e, KindGoals, "ProgressValue", 0, 1_000_000, errors);
        Num(e, KindGoals, "FrequencyPerPeriod", 1, 1000, errors);
        Flag(e, KindGoals, "IsArchived", errors);
        Date(e, KindGoals, "Deadline", errors, futureOk: true);
    }

    private void ValidateHabit(JsonElement e, List<string> errors)
    {
        Req(e, KindHabits, "Id", errors);
        Str(e, KindHabits, "Id", 80, errors); Str(e, KindHabits, "Name", 200, errors);
        Str(e, KindHabits, "NameKey", 120, errors);
        EnumOf(e, KindHabits, "Frequency", errors, typeof(HabitFrequencyKind));
        Num(e, KindHabits, "TimesPerWeek", 1, 7, errors);
        DateList(e, KindHabits, "Completions", errors);
        DateList(e, KindHabits, "CompletionLog", errors);
    }

    private void ValidateBootcamp(JsonElement e, List<string> errors)
    {
        Req(e, KindBootcamps, "Id", errors);
        Str(e, KindBootcamps, "Id", 80, errors); Str(e, KindBootcamps, "TitleKey", 120, errors);
        Str(e, KindBootcamps, "DescriptionKey", 120, errors); Str(e, KindBootcamps, "CreatorName", 120, errors);
        Str(e, KindBootcamps, "GoalMetricKey", 120, errors);
        EnumOf(e, KindBootcamps, "Category", errors, typeof(BootcampCategory));
        EnumOf(e, KindBootcamps, "Difficulty", errors, typeof(BootcampDifficulty));
        Flag(e, KindBootcamps, "IsEnrolled", errors); Flag(e, KindBootcamps, "WasAdaptedToday", errors);
        var duration = Num(e, KindBootcamps, "DurationDays", 1, 365, errors);
        var current = Num(e, KindBootcamps, "CurrentDay", 0, 365, errors);
        if (duration.HasValue && current.HasValue && current.Value > duration.Value)
            errors.Add(Prefix(KeyRange, "bootcamp.CurrentDay>DurationDays"));

        if (!e.TryGetProperty("Days", out var days))
        {
            errors.Add(Prefix(KeyMissingField, "bootcamp.Days"));
        }
        else if (days.ValueKind != JsonValueKind.Array)
        {
            errors.Add(Prefix(KeyType, "bootcamp.Days"));
        }
        else
        {
            var dayFields = AllowedFieldsOf(typeof(BootcampDayShape));
            var numbers = new List<int>();
            if (duration.HasValue && days.GetArrayLength() > 0 && days.GetArrayLength() != (int)duration.Value)
                errors.Add(Prefix(KeyRange, "bootcamp.Days!=DurationDays"));
            foreach (var day in days.EnumerateArray())
            {
                if (day.ValueKind != JsonValueKind.Object) { errors.Add(Prefix(KeyNotObject, "bootcamp.Days")); continue; }
                foreach (var p in day.EnumerateObject())
                    if (!dayFields.Contains(p.Name)) errors.Add(Prefix(KeyUnknownField, "bootcamp.Day." + p.Name));
                var n = Num(day, "bootcamp", "DayNumber", 1, 365, errors);
                if (n.HasValue) numbers.Add((int)n.Value);
                Str(day, "bootcamp", "PlanTitleKey", 120, errors);
                Str(day, "bootcamp", "PlanDescriptionKey", 120, errors);
                Num(day, "bootcamp", "TargetMinutes", 0, 1440, errors);
                Flag(day, "bootcamp", "IsAdapted", errors);
                Flag(day, "bootcamp", "IsCompleted", errors);
            }
            if (numbers.Count != numbers.Distinct().Count())
                errors.Add(Prefix(KeyDuplicateId, "bootcamp.DayNumber"));
        }
        StrList(e, KindBootcamps, "AdaptationRuleKeys", errors);
    }

    private void ValidateHistory(JsonElement e, List<string> errors)
    {
        Req(e, KindHistory, "Date", errors);
        Date(e, KindHistory, "Date", errors, futureOk: false);
        Str(e, KindHistory, "Origin", 40, errors);
        Num(e, KindHistory, "Completeness", 0, 1, errors);
        Num(e, KindHistory, "SleepMinutes", 0, 1440, errors);
        Num(e, KindHistory, "SleepQuality", 0, 1, errors);
        Num(e, KindHistory, "SleepConsistency", 0, 1, errors);
        Num(e, KindHistory, "BedtimeMinutesOfDay", 0, 1440, errors);
        Num(e, KindHistory, "Steps", 0, 200_000, errors);
        Num(e, KindHistory, "ActiveMinutes", 0, 1440, errors);
        Num(e, KindHistory, "RecoveryScore", 0, 100, errors);
        Num(e, KindHistory, "RestingHeartRate", 20, 250, errors);
        Num(e, KindHistory, "HrvMs", 0, 500, errors);
        Num(e, KindHistory, "Stress", 0, 1, errors);
        Num(e, KindHistory, "Mood", 0, 1, errors);
        Num(e, KindHistory, "Energy", 0, 1, errors);
        StrList(e, KindHistory, "CompletedHabitIds", errors);
        StrList(e, KindHistory, "AdvancedGoalIds", errors);
        Str(e, KindHistory, "BootcampId", 80, errors);
        Num(e, KindHistory, "BootcampDayNumber", 1, 365, errors);
        Flag(e, KindHistory, "BootcampDayWasAdapted", errors);
        EnumOf(e, KindHistory, "InsightTopic", errors, typeof(InsightTopic));
        EnumOf(e, KindHistory, "InsightPriority", errors, typeof(RecommendationPriority));
    }

    private void ValidateManual(JsonElement e, List<string> errors)
    {
        var hasDay = Req(e, KindManual, "Date", errors);
        var day = Date(e, KindManual, "Date", errors, futureOk: false);
        var saved = Date(e, KindManual, "SavedAtUtc", errors, futureOk: true);
        if (hasDay && day.HasValue && saved.HasValue && saved.Value < day.Value)
            errors.Add(Prefix(KeyDateInverted, "manual.SavedAtUtc<Date"));

        Num(e, KindManual, "SleepMinutes", 0, 1440, errors);
        Num(e, KindManual, "Steps", 0, 200_000, errors);
        Num(e, KindManual, "ActiveMinutes", 0, 1440, errors);
        Num(e, KindManual, "SleepQuality", 0, 1, errors);
        Num(e, KindManual, "Mood", 0, 1, errors);
        Num(e, KindManual, "Energy", 0, 1, errors);
        Num(e, KindManual, "Stress", 0, 1, errors);
        Str(e, KindManual, "Note", 4000, errors);
        EnumOf(e, KindManual, "Origin", errors, typeof(DataOrigin));
        if (e.TryGetProperty("Origin", out var origin) && origin.ValueKind == JsonValueKind.String
            && !string.Equals(origin.GetString(), nameof(DataOrigin.Manual), StringComparison.Ordinal))
            errors.Add(Prefix(KeyImpossibleProvenance, "manual.Origin"));
    }

    private void ValidateConsentRow(JsonElement e, List<string> errors)
    {
        Req(e, KindConsents, "Category", errors);
        Req(e, KindConsents, "Decision", errors);
        EnumOf(e, KindConsents, "Category", errors, typeof(ConsentCategory));
        EnumOf(e, KindConsents, "Decision", errors, typeof(ConsentDecision));
        if (e.TryGetProperty("Decision", out var d) && d.ValueKind == JsonValueKind.String
            && string.Equals(d.GetString(), nameof(ConsentDecision.Untouched), StringComparison.Ordinal))
            errors.Add(Prefix(KeyUntouchableState, "consents.Decision"));
        Str(e, KindConsents, "UpdatedAtUtc", 40, errors);
    }

    private void ValidateReminderRow(JsonElement e, List<string> errors)
    {
        Req(e, KindReminders, "Id", errors);
        Req(e, KindReminders, "Kind", errors);
        Req(e, KindReminders, "TextKey", errors);
        Str(e, KindReminders, "Id", 80, errors); Str(e, KindReminders, "Kind", 40, errors);
        Str(e, KindReminders, "TargetId", 80, errors); Str(e, KindReminders, "TextKey", 120, errors);
        Flag(e, KindReminders, "Enabled", errors);
        Num(e, KindReminders, "DaysMask", 0, 127, errors);
        if (e.TryGetProperty("TimeOfDay", out var tod))
        {
            if (tod.ValueKind != JsonValueKind.String
                || !TimeSpan.TryParse(tod.GetString(), CultureInfo.InvariantCulture, out var span)
                || span < TimeSpan.Zero || span >= TimeSpan.FromDays(1))
                errors.Add(Prefix(KeyType, "reminders.TimeOfDay"));
        }
        else errors.Add(Prefix(KeyMissingField, "reminders.TimeOfDay"));
        if (e.TryGetProperty("TextArgs", out var args) && args.ValueKind != JsonValueKind.Array)
            errors.Add(Prefix(KeyType, "reminders.TextArgs"));
    }

    private void ValidateSettingsValue(JsonProperty p, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) { errors.Add(Prefix(KeyMissingField, "settings")); return; }
        switch (p.Value.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
                break;
            case JsonValueKind.Number:
                if (!p.Value.TryGetDouble(out var d) || !double.IsFinite(d))
                    errors.Add(Prefix(KeyNotFinite, "settings." + p.Name));
                else if (Math.Abs(d) > 1e12) errors.Add(Prefix(KeyRange, "settings." + p.Name));
                break;
            default: // null / array / object: the legacy bag holds scalars only
                errors.Add(Prefix(KeyType, "settings." + p.Name));
                break;
        }
    }

    // ---- scalar field helpers --------------------------------------------------

    private static bool Req(JsonElement e, string kind, string field, List<string> errors)
    {
        if (e.TryGetProperty(field, out var v) && v.ValueKind != JsonValueKind.Null) return true;
        errors.Add(Prefix(KeyMissingField, kind + "." + field));
        return false;
    }

    private static void Str(JsonElement e, string kind, string field, int maxLen, List<string> errors)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return;
        if (v.ValueKind != JsonValueKind.String) { errors.Add(Prefix(KeyType, kind + "." + field)); return; }
        if ((v.GetString()?.Length ?? 0) > maxLen) errors.Add(Prefix(KeyTooLong, kind + "." + field));
    }

    private static void Flag(JsonElement e, string kind, string field, List<string> errors)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            errors.Add(Prefix(KeyType, kind + "." + field));
    }

    private static void EnumOf(JsonElement e, string kind, string field, List<string> errors, Type enumType)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return;
        if (v.ValueKind == JsonValueKind.String)
        {
            var name = v.GetString();
            if (name is not null && Enum.TryParse(enumType, name, ignoreCase: false, out var parsed)
                && parsed is not null && Enum.IsDefined(enumType, parsed)) return;
            errors.Add(Prefix(KeyUnknownEnum, kind + "." + field));
            return;
        }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var raw) && Enum.IsDefined(enumType, raw)) return;
        errors.Add(Prefix(KeyType, kind + "." + field));
    }

    private static double? Num(JsonElement e, string kind, string field, double min, double max, List<string> errors)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d))
        {
            errors.Add(Prefix(KeyType, kind + "." + field));
            return null;
        }
        if (!double.IsFinite(d)) { errors.Add(Prefix(KeyNotFinite, kind + "." + field)); return null; }
        if (d < min || d > max) { errors.Add(Prefix(KeyRange, kind + "." + field)); return null; }
        return d;
    }

    private DateTime? Date(JsonElement e, string kind, string field, List<string> errors, bool futureOk)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String
            || !DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            errors.Add(Prefix(KeyType, kind + "." + field));
            return null;
        }
        if (parsed.Date < FloorDate.Date) { errors.Add(Prefix(KeyDateInverted, kind + "." + field)); return null; }
        if (!futureOk && parsed.Date > _utcNow().Date.AddDays(1))
        {
            errors.Add(Prefix(KeyDateInverted, kind + ".future." + field));
            return null;
        }
        return parsed.Date;
    }

    private void DateList(JsonElement e, string kind, string field, List<string> errors)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return;
        if (v.ValueKind != JsonValueKind.Array) { errors.Add(Prefix(KeyType, kind + "." + field)); return; }
        if (v.GetArrayLength() > 10_000) { errors.Add(Prefix(KeyTooLarge, kind + "." + field)); return; }
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !DateTime.TryParse(item.GetString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var d))
            { errors.Add(Prefix(KeyType, kind + "." + field)); return; }
            if (d.Date < FloorDate.Date || d.Date > _utcNow().Date.AddDays(1))
            { errors.Add(Prefix(KeyDateInverted, kind + "." + field)); return; }
        }
    }

    private static void StrList(JsonElement e, string kind, string field, List<string> errors)
    {
        if (!e.TryGetProperty(field, out var v) || v.ValueKind == JsonValueKind.Null) return;
        if (v.ValueKind != JsonValueKind.Array) { errors.Add(Prefix(KeyType, kind + "." + field)); return; }
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || (item.GetString()?.Length ?? 0) > 200)
            { errors.Add(Prefix(KeyType, kind + "." + field)); return; }
    }

    // ---- id / allow-list / node plumbing --------------------------------------

    /// <summary>Identity of one entity: an Id for the id-keyed stores, the calendar day for the
    /// date-keyed ones (history, manual) and the category for consents.</summary>
    internal string IdOf(string kind, JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return string.Empty;
        return kind switch
        {
            KindSettings => SettingsEntityId,
            KindConsents => RawString(e, "Category"),
            KindHistory or KindManual => DayKey(e, "Date"),
            _ => RawString(e, "Id"),
        };
    }

    internal string IdOf(string kind, JsonNode node) =>
        node is JsonObject ? IdOf(kind, JsonDocument.Parse(node.ToJsonString()).RootElement.Clone()) : string.Empty;

    private static string RawString(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string DayKey(JsonElement e, string field) =>
        e.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    private static readonly Dictionary<string, HashSet<string>> AllowListCache = new(StringComparer.Ordinal);

    /// <summary>The field names this store may legitimately contain, read off the domain type.</summary>
    private static HashSet<string> AllowedFields(string kind) => kind switch
    {
        KindGoals => AllowedFieldsOf(typeof(Goal)),
        KindHabits => AllowedFieldsOf(typeof(Habit)),
        KindBootcamps => AllowedFieldsOf(typeof(Bootcamp)),
        KindHistory => AllowedFieldsOf(typeof(DailyHistoryRecord)),
        KindManual => AllowedFieldsOf(typeof(ManualEntryRecord)),
        KindConsents => AllowedFieldsOf(typeof(ConsentStore.ConsentEntry)),
        KindReminders => AllowedFieldsOf(typeof(ReminderRowShape)),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    private static HashSet<string> AllowedFieldsOf(Type type)
    {
        lock (AllowListCache)
        {
            var name = type.FullName ?? type.Name;
            if (AllowListCache.TryGetValue(name, out var cached)) return cached;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                set.Add(p.Name);   // settable AND derived (read-only) both appear in a real export
            }
            AllowListCache[name] = set;
            return set;
        }
    }

    /// <summary>
    /// Reminder rows are lane 09's <c>ReminderSetting</c>; the shape is mirrored structurally here so
    /// the catalog does not compile against the notifications namespace, which owns the real type.
    /// Same for <see cref="BootcampDayShape"/> (lane 09/day rows inside a bootcamp).
    /// </summary>
    private sealed class ReminderRowShape
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? TargetId { get; set; }
        public bool Enabled { get; set; }
        public TimeSpan TimeOfDay { get; set; }
        public int DaysMask { get; set; }
        public string TextKey { get; set; } = string.Empty;
        public object[] TextArgs { get; set; } = Array.Empty<object>();
    }

    private sealed class BootcampDayShape
    {
        public int DayNumber { get; set; }
        public string PlanTitleKey { get; set; } = string.Empty;
        public string PlanDescriptionKey { get; set; } = string.Empty;
        public int TargetMinutes { get; set; }
        public bool IsAdapted { get; set; }
        public bool IsCompleted { get; set; }
    }

    private List<JsonNode> ReadKindRaw(string kind, out bool corrupt)
    {
        corrupt = false;
        var raw = _root.ReadRaw(FileForKind(kind));
        if (raw is null) return new List<JsonNode>();
        using var doc = Parse(raw);
        if (doc is null) { corrupt = true; return new List<JsonNode>(); }
        return EntityElements(doc.RootElement, kind).Select(el => JsonNode.Parse(el.GetRawText())!).ToList();
    }

    /// <summary>The entity elements of one file, whatever the file's wrapper shape is.</summary>
    private static List<JsonElement> EntityElements(JsonElement root, string kind)
    {
        var list = new List<JsonElement>();
        if (kind == KindSettings)
        {
            if (root.ValueKind == JsonValueKind.Object) list.Add(root.Clone());
            return list;
        }
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) list.Add(item.Clone());
            return list;
        }
        if (root.ValueKind == JsonValueKind.Object)
        {
            // reminders wrap their rows in lane 09's object file; anything else object-shaped is one row
            if (kind == KindReminders && root.TryGetProperty(ReminderListProperty, out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray()) list.Add(item.Clone());
                return list;
            }
            list.Add(root.Clone());
        }
        return list;
    }

    /// <summary>Parsed + validated payload as entity nodes (settings yields exactly one node).</summary>
    private static List<JsonNode> PayloadNodes(string kind, string json)
    {
        using var doc = Parse(json)!;
        return EntityElements(doc.RootElement, kind).Select(el => JsonNode.Parse(el.GetRawText())!).ToList();
    }

    private void WriteKind(string kind, List<JsonNode> items)
    {
        var file = FileForKind(kind);
        if (kind == KindSettings)
        {
            _root.WriteRawAtomic(file, Render(items.Count > 0 ? items[0] : new JsonObject()));
            return;
        }
        if (kind == KindReminders)
        {
            // Preserve lane 09's sibling properties (fired map, permission flag, scheduled ids):
            // only the reminder list is replaced, so an import cannot silently reset dedupe state.
            var wrapper = new JsonObject();
            var raw = _root.ReadRaw(file);
            if (ParseNode(raw) is JsonObject existing)
                foreach (var (name, value) in existing)
                    if (!string.Equals(name, ReminderListProperty, StringComparison.Ordinal))
                        wrapper[name] = value?.DeepClone();
            var arr = new JsonArray();
            foreach (var item in items) arr.Add(item.DeepClone());
            wrapper[ReminderListProperty] = arr;
            _root.WriteRawAtomic(file, Render(wrapper));
            return;
        }
        var list = new JsonArray();
        foreach (var item in items) list.Add(item.DeepClone());
        _root.WriteRawAtomic(file, Render(list));
    }

    private void Upsert(string kind, List<JsonNode> target, JsonNode incoming)
    {
        var id = IdOf(kind, incoming);
        var idx = target.FindIndex(e => IdOf(kind, e) == id);
        if (idx >= 0) target[idx] = incoming.DeepClone();
        else target.Add(incoming.DeepClone());
    }

    // ---- json plumbing ---------------------------------------------------------

    private static JsonDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException) { return null; }
    }

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static string Render(JsonNode node) =>
        // Indented, ordinal-stable property order (insertion order = the order the elements were
        // added), so two exports of identical data are byte-identical — which is what makes the
        // export/import identity tests meaningful.
        node.ToJsonString(Pretty);

    /// <summary>True when the raw text carries a non-finite number literal (JsonDocument rejects NaN/Infinity, so this is the pre-check that turns it into a KEY instead of a generic parse failure).</summary>
    internal static bool HasNonFiniteToken(string json) =>
        json.Contains("NaN", StringComparison.Ordinal) || json.Contains("Infinity", StringComparison.Ordinal);

    private InvalidOperationException Reject(string key, string detail) =>
        new(Prefix(key, detail));

    // ---- rejection keys (shipped in wave3c-keys/lane01.*.keys.xml) --------------

    /// <summary>Bare key (no suffix) for every rejection this catalog can emit — the keys-manifest
    /// test asserts each of these exists in both language manifests.</summary>
    public static readonly IReadOnlyList<string> RejectionKeys = new[]
    {
        KeyMalformed, KeyNotObject, KeyTooLarge, KeyEmpty, KeyUnknownKind, KeyNotFound, KeyStoreCorrupt,
        KeyUnknownField, KeyMissingField, KeyMissingId, KeyDuplicateId, KeyType, KeyNotFinite, KeyRange,
        KeyTooLong, KeyUnknownEnum, KeyDateInverted, KeySchemaTooNew, KeyImpossibleProvenance, KeyUntouchableState,
    };

    private const string KeyMalformed = "Norm.Reject.MalformedJson";
    private const string KeyNotObject = "Norm.Reject.NotObject";
    private const string KeyTooLarge = "Norm.Reject.TooLarge";
    private const string KeyEmpty = "Norm.Reject.Empty";
    private const string KeyUnknownKind = "Norm.Reject.UnknownKind";
    private const string KeyNotFound = "Norm.Reject.NotFound";
    private const string KeyStoreCorrupt = "Norm.Reject.StoreCorrupt";
    private const string KeyUnknownField = "Norm.Reject.UnknownField";
    private const string KeyMissingField = "Norm.Reject.MissingField";
    private const string KeyMissingId = "Norm.Reject.MissingId";
    private const string KeyDuplicateId = "Norm.Reject.DuplicateId";
    private const string KeyType = "Norm.Reject.Type";
    private const string KeyNotFinite = "Norm.Reject.NotFinite";
    private const string KeyRange = "Norm.Reject.Range";
    private const string KeyTooLong = "Norm.Reject.StringTooLong";
    private const string KeyUnknownEnum = "Norm.Reject.UnknownEnum";
    private const string KeyDateInverted = "Norm.Reject.DateInverted";
    private const string KeySchemaTooNew = "Norm.Reject.SchemaTooNew";
    private const string KeyImpossibleProvenance = "Norm.Reject.ImpossibleProvenance";
    private const string KeyUntouchableState = "Norm.Reject.UntouchableState";

    /// <summary>key + machine suffix (kind/field), so the UI can name the offending row without the
    /// payload ever being echoed.</summary>
    internal static string Prefix(string key, string detail) =>
        string.IsNullOrEmpty(detail) ? key : key + ":" + detail;

    private static List<string> Distinct(List<string> errors) =>
        errors.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList();
}
