using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;

namespace LIVORA.Application.Activities;

/// <summary>
/// Wave 3b (lane 03): the foundation workout source — a deterministic, seeded generator of
/// sample sessions. It is a SEAM, not a feature: no UI consumes it yet (lane 13 / the merge
/// wires it behind the same honesty rules as SampleHealthProvider).
///
/// Honesty contract (product law §0.4): every session carries Provenance{Source="sample",
/// Origin=DataOrigin.Mock}, the source reports ConnectionState.Mock, and nothing it produces may
/// ever be presented as device-measured. The mock does NOT expose a per-session intensity figure
/// (no HR stream exists), so Intensity stays null — unknown, never 0.
///
/// Determinism: sessions are derived from (Seed, ReferenceDate) alone — same inputs produce a
/// byte-identical session list, field for field. ReferenceDate defaults to "today" at
/// construction (the test harness injects a fixed date; production DI gets the default).
/// </summary>
public sealed class SampleWorkoutSource : IWorkoutSource
{
    /// <summary>Machine id of this source in provenance records.</summary>
    public const string SourceId = "sample";

    /// <summary>Default lookback window in days requested by the composition root / UI.</summary>
    public const int DefaultHorizonDays = 60;

    private readonly int _seed;
    private readonly DateTime _referenceDate;
    private readonly DateTime _importedAtUtc;

    public SampleWorkoutSource(
        int seed = 20260913,
        int horizonDays = DefaultHorizonDays,
        DateTime? referenceDate = null,
        DateTime? importedAtUtc = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(horizonDays, 1);
        var reference = (referenceDate ?? DateTime.Today).Date;
        if (horizonDays > 3650)
        {
            // Clamp instead of throwing: an absurd horizon degrades to 10 years of history
            // rather than a crash or a silently huge loop.
            horizonDays = 3650;
        }
        _seed = seed;
        _referenceDate = reference;
        HorizonDays = horizonDays;
        // Ingestion time is real wall-clock UTC unless a test pins it (determinism of tests).
        _importedAtUtc = (importedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
    }

    public int Seed => _seed;
    public int HorizonDays { get; }

    public DataSourceCapabilities Capabilities =>
        // The sample emits duration + (sometimes) distance + estimated energy, and it is honest
        // about which of those are estimates through WorkoutQuality. It does not do heart rate.
        DataSourceCapabilities.Workout | DataSourceCapabilities.Calories;

    public ConnectionState State => ConnectionState.Mock;

    /// <summary>Sessions whose local calendar day falls in [from.Date, to.Date] (inclusive).</summary>
    public Task<IReadOnlyList<WorkoutSession>> GetSessionsAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var lo = from.Date;
        var hi = to.Date;
        if (hi < lo) return Task.FromResult<IReadOnlyList<WorkoutSession>>(Array.Empty<WorkoutSession>());

        // Generate the FULL horizon from a fixed per-day seed, then filter, so the session
        // sequence never depends on the query window (same seed -> identical output).
        var sessions = new List<WorkoutSession>();
        for (var back = HorizonDays - 1; back >= 0; back--)
        {
            ct.ThrowIfCancellationRequested();
            GenerateDay(_referenceDate.AddDays(-back), sessions);
        }

        sessions.RemoveAll(s => s.StartLocal.Date < lo || s.StartLocal.Date > hi);
        return Task.FromResult<IReadOnlyList<WorkoutSession>>(sessions);
    }

    private void GenerateDay(DateTime day, List<WorkoutSession> into)
    {
        unchecked
        {
            int h = 17;
            h = h * 31 + _seed;
            h = h * 31 + day.Year;
            h = h * 31 + day.Month;
            h = h * 31 + day.Day;
            var rnd = new Random(h);

            // Weekly rhythm: more sessions mid-week, a walk more likely at the weekend.
            bool weekday = day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday
                or DayOfWeek.Thursday or DayOfWeek.Friday;
            double p = (weekday ? 0.70 : 0.45) + 0.10 * Math.Sin(day.DayOfYear * 0.9);

            var slots = new List<(WorkoutType Type, int BaseHour, int MinMinutes, int MaxMinutes, double BaseMet, double[] Speeds)>
            {
                (WorkoutType.Walk, 18, 20, 45, 3.5, new[] { 70.0, 85.0, 100.0 }),
                (WorkoutType.Run, 7, 25, 55, 9.8, new[] { 140.0, 160.0, 180.0 }),
                (WorkoutType.Cycle, 18, 35, 90, 7.5, new[] { 200.0, 250.0, 300.0 }),
                (WorkoutType.Gym, 19, 30, 60, 5.0, Array.Empty<double>()),
            };

            foreach (var (type, baseHour, minMin, maxMin, baseMet, speeds) in slots)
            {
                double bias = type == WorkoutType.Walk ? 0.12 : 0.0; // walks are the friendliest habit
                if (rnd.NextDouble() >= p + bias) continue;

                int minutes = rnd.Next(minMin, maxMin + 1);
                int startHour = weekday ? baseHour : Math.Min(20, baseHour + 2);
                var start = day.AddHours(startHour).AddMinutes(rnd.Next(0, 30));
                var end = start.AddMinutes(minutes);

                double? distance = null;
                if (speeds.Length > 0 && rnd.NextDouble() < 0.7)
                {
                    double speed = speeds[rnd.Next(speeds.Length)] * (0.9 + rnd.NextDouble() * 0.2);
                    distance = Math.Round(speed * minutes);
                }

                // Quality decides whether energy is present, and whether "distance" is real.
                double roll = rnd.NextDouble();
                WorkoutQuality quality;
                double? energy;
                if (roll < 0.75)
                {
                    quality = WorkoutQuality.Complete;
                    energy = Math.Round(baseMet * minutes * 60.0 * 70.0 / 6000.0); // METs x 70kg
                }
                else if (roll < 0.85)
                {
                    quality = WorkoutQuality.Partial; // session cut short: energy genuinely unknown
                    energy = null;
                }
                else if (roll < 0.97)
                {
                    quality = WorkoutQuality.Estimated; // device extrapolated, not measured
                    energy = Math.Round(baseMet * minutes * 60.0 * 70.0 / 6000.0 * (1.1 + rnd.NextDouble() * 0.2));
                }
                else
                {
                    quality = WorkoutQuality.Suspect; // corrupt record: keep duration, drop everything else
                    energy = null;
                    distance = null;
                }

                if (quality is WorkoutQuality.Estimated or WorkoutQuality.Suspect && rnd.NextDouble() < 0.5)
                    distance = null; // an estimated session may equally have no route data

                into.Add(new WorkoutSession
                {
                    Id = $"sample-{day:yyyyMMdd}-{(int)type}",
                    Type = type,
                    StartLocal = start,
                    EndLocal = end,
                    DistanceMeters = distance,
                    EnergyKcal = energy,
                    Intensity = null, // no HR stream in the mock — unknown, never 0
                    Quality = quality,
                    Provenance = new Provenance
                    {
                        Source = SourceId,
                        SourceRecordId = $"sample-{day:yyyyMMdd}-{(int)type}",
                        ImportedAtUtc = _importedAtUtc,
                        Origin = DataOrigin.Mock,
                    },
                });
            }
        }
    }
}
