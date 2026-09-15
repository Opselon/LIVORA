using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Livora.Server.Infrastructure.Observability;

/// <summary>
/// PURPOSE: real, in-process observability for the sync lane — an activity per batch plus counters
///          and a duration histogram a dashboard (or a test with ActivityListener/MeterListener) can
///          read. This is the observability slice of the lane: measurements of code that ran, never
///          a status light configured to look green.
/// OWNER: Agent 02 (platform lane, w4-p1b-platform).
/// CONSUMES: nothing (System.Diagnostics only — no package, no host reference; Infrastructure must
///           not reference the host assembly, which is why the source/meter NAME is repeated here as
///           a const and pinned by a test asserting it equals the host's FlivoraActivity.SourceName).
/// PROVIDES:
///   - ActivitySource "livora.server" span <c>sync.batch</c> carrying one tag per outcome
///     (sync.outcome ∈ applied|replay|key_mismatch|version_conflict|error) — the ActivitySource-
///     backed, trace-correlated metric surface;
///   - Counter "livora.sync.batches" (one increment per batch, tagged by that same outcome +
///     replayed) — one metric stream per sync outcome;
///   - Counter "livora.sync.operations" (tagged outcome ∈ applied|conflict|rejected|duplicate);
///   - Histogram "livora.sync.batch.duration" (ms) — what one request actually cost (Wave 4 §52:
///     observable bounds, not a rate limiter; limits live in the service).
/// INVARIANTS:
///   - names match the host's ActivitySource ("livora.server") so traces and metrics share one
///     filter key — pinned by SyncTelemetryTests
///   - tags are ids/enums/counts only: NO payload, NO email, NO token (product law 5)
///   - the correlation id rides the span when the caller has one (sync.correlation_id) — linking
///     the metric event to the request without any content
///   - Record* never throws: an observability failure must not break a sync request (law 3), and a
///     zero-listener process pays ~nothing (ActivitySource/Meter no-op until listened to)
/// EXTEND: another lane's metrics = its own file + its own instrument names ("livora.&lt;area&gt;.*").
/// </summary>
public static class SyncTelemetry
{
    /// <summary>Must equal the host's <c>FlivoraActivity.SourceName</c> (Platform/Correlation.cs);
    /// a test asserts the two assemblies agree — the assembly boundary forbids referencing it.</summary>
    public const string SourceName = "livora.server";

    private static readonly ActivitySource Source = new(SourceName, "1.0.0");
    private static readonly Meter Shared = new(SourceName, "1.0.0");

    private static readonly Counter<int> Batches =
        Shared.CreateCounter<int>("livora.sync.batches", description: "sync batches handled, by outcome");
    private static readonly Counter<int> Operations =
        Shared.CreateCounter<int>("livora.sync.operations", description: "sync operations decided, by outcome");
    private static readonly Histogram<double> BatchDuration =
        Shared.CreateHistogram<double>("livora.sync.batch.duration", unit: "ms",
            description: "wall time of a batch decision+persist");

    /// <summary>Instrument names this lane publishes — the ops/dashboard contract, asserted by tests.</summary>
    public static readonly IReadOnlyList<string> InstrumentNames =
        ["livora.sync.batches", "livora.sync.operations", "livora.sync.batch.duration"];

    /// <summary>Batch outcomes on the wire of observability (distinct from the per-operation
    /// outcomes): what the REQUEST-level machinery decided.</summary>
    public static class BatchOutcomes
    {
        public const string Applied = "applied";
        public const string Replay = "replay";
        public const string KeyMismatch = "key_mismatch";
        public const string VersionConflict = "version_conflict";
        public const string Error = "error";
    }

    /// <summary>
    /// One batch: opens a <c>sync.batch</c> activity (ended immediately — the counters are the
    /// measurement, the span links it into the request trace), increments the batch counter with
    /// the outcome tag, and records the duration.
    /// </summary>
    public static void RecordBatch(string outcome, double durationMs, string? correlationId = null)
    {
        outcome = string.IsNullOrWhiteSpace(outcome) ? BatchOutcomes.Error : outcome;
        var tags = new KeyValuePair<string, object?>[]
        {
            new("sync.outcome", outcome),
            new("replayed", outcome == BatchOutcomes.Replay),
            new("correlation_id", correlationId ?? "unknown"),
        };

        using var activity = Source.StartActivity("sync.batch", ActivityKind.Internal);
        if (activity is not null)
        {
            foreach (var (key, value) in tags)
                activity.SetTag(key, value);
            if (durationMs >= 0)
                activity.SetTag("sync.batch.duration_ms", Math.Round(durationMs, 3));
        }

        Batches.Add(1, tags);
        RecordBatchDurationMs(durationMs);
    }

    /// <summary>Replay-shaped call site kept compatible: outcome tag derives from the bool.</summary>
    public static void RecordBatch(bool replayed)
        => RecordBatch(replayed ? BatchOutcomes.Replay : BatchOutcomes.Applied, 0);

    /// <summary>Kept for the duration-only call sites; RecordBatch already calls it for batches.</summary>
    public static void RecordBatchDurationMs(double ms)
        => BatchDuration.Record(Math.Max(0, ms));

    /// <summary>Per-operation decisions. <paramref name="collided"/> marks rows counted on a path
    /// that threw the batch away on an index collision, so a retry storm is visible, not silent.</summary>
    public static void RecordOperations(string outcome, int count, bool collided = false)
    {
        if (count <= 0) return;
        Operations.Add(count,
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("collided", collided));
    }
}
