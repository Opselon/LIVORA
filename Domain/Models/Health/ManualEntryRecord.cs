using LIVORA.Domain.Enums;

namespace LIVORA.Domain.Models.Health;
/// <summary>
/// Wave 3: the persisted shape of ONE user-entered day (manual entry pipeline, lane 02).
/// Pure data — no formatting, no provider logic, no Application references (layer rule:
/// Domain depends on nothing). Nullable value = the user did not report that metric.
///
/// Provenance law: a record is DataOrigin.Manual forever. It is self-reported input, never a
/// measurement, and the UI labels it as such downstream (Privacy.Origin.Manual).
/// </summary>
public sealed class ManualEntryRecord
{
    /// <summary>The calendar day the values describe (normalized to midnight by the store).</summary>
    public DateTime Date { get; set; }

    public int? SleepMinutes { get; set; }
    public int? Steps { get; set; }
    public int? ActiveMinutes { get; set; }

    /// <summary>0..1 self-reported sleep quality.</summary>
    public double? SleepQuality { get; set; }
    /// <summary>0..1 self-reported mood.</summary>
    public double? Mood { get; set; }
    /// <summary>0..1 self-reported energy.</summary>
    public double? Energy { get; set; }
    /// <summary>0..1 self-reported stress.</summary>
    public double? Stress { get; set; }

    /// <summary>Free-text note. Displayed back to its author only — never parsed or analyzed.</summary>
    public string? Note { get; set; }

    /// <summary>When the entry was last saved (UTC). Stamped by the store, not the caller.
    /// Nullable so a hand-edited or legacy file without a stamp reads back as "unknown" instead
    /// of a fake 0001-01-01 date.</summary>
    public DateTime? SavedAtUtc { get; set; }

    /// <summary>Written explicitly into the JSON so on-disk data states its own provenance.
    /// Manual is the only legal value for this record type.</summary>
    public DataOrigin Origin { get; set; } = DataOrigin.Manual;
}
