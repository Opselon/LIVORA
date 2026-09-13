using System.Text;
using LIVORA.Domain.Models;

namespace LIVORA.Application.Planning.Wave3b;

/// <summary>
/// One decision: what the ranker saw, what it chose, what it dropped, and the numeric scores.
/// Structured ids only — NO user prose and no recommendation text is ever carried here.
/// </summary>
public sealed record DecisionEntry(
    string CorrelationId,
    DateTime NowUtc,
    int CandidateCount,
    IReadOnlyList<string> ChosenIds,
    IReadOnlyList<string> DroppedIds,
    // JSON object of recommendation-id -> score (numbers only; ids are machine keys).
    string ScoresJson);

/// <summary>
/// Wave 3c (lane 05): the debug/observability ring behind the ranked decision layer.
///
/// Bounded (Capacity entries, oldest evicted) and SAFE BY CONSTRUCTION: the only strings that
/// enter an entry are the CorrelationId and the recommendation Ids the caller passes in, plus
/// numeric scores. There is no API that accepts free text about the user — no title, no text key,
/// no args — so user prose cannot be serialized even by mistake. SnapshotJson is the single
/// debug surface (a compact index page: counts + ids per decision, never scores or payloads).
/// Thread-safe: the Today screen can log from a background refresh while a settings page reads
/// the snapshot.
/// </summary>
public sealed class DecisionLog
{
    public const int DefaultCapacity = 500;

    private readonly object _gate = new();
    private readonly DecisionEntry?[] _ring;
    private int _next;      // write position ( wraps )
    private int _count;

    public DecisionLog(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
        _ring = new DecisionEntry?[capacity];
    }

    public int Capacity { get; }

    /// <summary>Entries currently held (&lt;= Capacity).</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>
    /// Record one ranking decision. Candidate ids are taken from the frozen Recommendation objects;
    /// only Id + numeric score are kept — the Recommendation instances themselves are not retained.
    /// </summary>
    public DecisionEntry Record(
        string correlationId,
        DateTime nowUtc,
        IReadOnlyList<Recommendation> candidates,
        IReadOnlyList<RecommendationCard> chosen)
    {
        if (correlationId is null) throw new ArgumentNullException(nameof(correlationId));
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (chosen is null) throw new ArgumentNullException(nameof(chosen));

        var chosenIds = chosen.Select(c => c.Recommendation.Id).ToList();
        var chosenSet = new HashSet<string>(chosenIds, StringComparer.Ordinal);
        var droppedIds = candidates.Select(c => c.Id).Where(id => !chosenSet.Contains(id)).ToList();

        var entry = new DecisionEntry(
            correlationId,
            nowUtc,
            candidates.Count,
            chosenIds,
            droppedIds,
            ScoresOf(chosen));

        lock (_gate)
        {
            _ring[_next] = entry;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
        return entry;
    }

    /// <summary>Oldest → newest (post-eviction order).</summary>
    public IReadOnlyList<DecisionEntry> Entries()
    {
        lock (_gate)
        {
            var list = new List<DecisionEntry>(_count);
            int start = _count == Capacity ? _next : 0;   // when full, _next is the oldest slot
            for (int i = 0; i < _count; i++)
                list.Add(_ring[(start + i) % Capacity]!);
            return list;
        }
    }

    /// <summary>
    /// The debug surface: a compact JSON index (counts + ids only — no scores, no keys, no text).
    /// Deterministic for identical entry sequences.
    /// </summary>
    public string SnapshotJson()
    {
        var entries = Entries();
        var sb = new StringBuilder();
        sb.Append("{\"capacity\":").Append(Capacity)
          .Append(",\"count\":").Append(entries.Count)
          .Append(",\"entries\":[");
        for (int i = 0; i < entries.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var e = entries[i];
            sb.Append("{\"cid\":").Append(Quote(e.CorrelationId))
              .Append(",\"utc\":").Append(Quote(e.NowUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture)))
              .Append(",\"seen\":").Append(e.CandidateCount)
              .Append(",\"chosen\":").Append(IdArray(e.ChosenIds))
              .Append(",\"dropped\":").Append(IdArray(e.DroppedIds))
              .Append('}');
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>ids-only score map: {"rec-id":0.42,...} — numbers formatted invariantly.</summary>
    private static string ScoresOf(IReadOnlyList<RecommendationCard> chosen)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        for (int i = 0; i < chosen.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Quote(chosen[i].Recommendation.Id)).Append(':')
              .Append(chosen[i].Score.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.Append('}').ToString();
    }

    private static string IdArray(IReadOnlyList<string> ids)
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < ids.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Quote(ids[i]));
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Minimal JSON string escaper (control-safe; no external serializer needed).</summary>
    internal static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
