using System.Text.Json;
using LIVORA.Application.Planning.Wave3b;
using LIVORA.Domain.Models;
using static LIVORA.Tests.Wave3c.Plan.Wave3cFixtures;

namespace LIVORA.Tests.Wave3c.Plan;

/// <summary>
/// Lane 05: the bounded decision ring. Ring behavior, id-only payloads, and the hard rule that
/// user prose never reaches a serialized byte.
/// </summary>
public class DecisionLogTests
{
    private static readonly DateTime NowUtc = new(2026, 9, 21, 6, 30, 0, DateTimeKind.Utc);

    private static (Recommendation Rec, RecommendationCard Card) Card(string id, double score)
    {
        var r = Rec(id);
        return (r, new RecommendationCard(r, score, null));
    }

    [Fact]
    public void Ring_IsBoundedToCapacity_EvictingOldest()
    {
        var log = new DecisionLog(capacity: 500);
        for (int i = 0; i < 537; i++)
        {
            var (rec, card) = Card($"r{i}", 0.5);
            log.Record($"c{i}", NowUtc.AddSeconds(i), new[] { rec }, new[] { card });
        }

        Assert.Equal(500, log.Count);
        var entries = log.Entries();
        Assert.Equal(500, entries.Count);
        Assert.Equal("c37", entries[0].CorrelationId);       // 0..36 evicted
        Assert.Equal("c536", entries[^1].CorrelationId);     // newest last
    }

    [Fact]
    public void Entry_CarriesCorrelationCountChosenDroppedAndIdsOnlyScores()
    {
        var log = new DecisionLog(capacity: 500);
        var seen = new[] { Card("a", 0.7).Rec, Card("b", 0.6).Rec, Card("c", 0.1).Rec };
        var chosen = new[] { new RecommendationCard(seen[0], 0.7123, null),
                             new RecommendationCard(seen[1], 0.42, null) };

        var e = log.Record("corr-1", NowUtc, seen, chosen);

        Assert.Equal("corr-1", e.CorrelationId);
        Assert.Equal(NowUtc, e.NowUtc);
        Assert.Equal(3, e.CandidateCount);
        Assert.Equal(new[] { "a", "b" }, e.ChosenIds);
        Assert.Equal(new[] { "c" }, e.DroppedIds);

        // ScoresJson: object of id -> number, ids only, invariant formatting.
        using var doc = JsonDocument.Parse(e.ScoresJson);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(0.7123, root.GetProperty("a").GetDouble(), 6);
        Assert.Equal(0.42, root.GetProperty("b").GetDouble(), 6);
        Assert.Equal(2, root.EnumerateObject().Count());
    }

    [Fact]
    public void NoUserProseIsEverSerialized_TextArgsAreInvisibleToTheLog()
    {
        // A recommendation whose TextArgs carry user prose (a free-text habit name typed by the
        // user — exactly the data the product law keeps out of logs/debug surfaces).
        var prose = "Marché du dimanche avec Maman ❤";
        var r = Rec("id-1", textKey: "Plan.Item.Habit", textArgs: new object[] { prose });
        var card = new RecommendationCard(r, 0.5, null);

        var log = new DecisionLog();
        var e = log.Record("corr-prose", NowUtc, new[] { r }, new[] { card });
        var snapshot = log.SnapshotJson();

        Assert.DoesNotContain(prose, e.ScoresJson, StringComparison.Ordinal);
        Assert.DoesNotContain(prose, snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Plan.Item.Habit", snapshot, StringComparison.Ordinal);
        Assert.DoesNotContain(prose, JsonSerializer.Serialize(e), StringComparison.Ordinal);
        // The only strings in the entry are the correlation id and the recommendation ids.
        Assert.Equal(new[] { "id-1" }, e.ChosenIds);
    }

    [Fact]
    public void SnapshotJson_IsCompactIndex_ValidJson_DeterministicOrder()
    {
        var log = new DecisionLog(capacity: 4);
        for (int i = 0; i < 3; i++)
        {
            var (rec, card) = Card($"r{i}", 0.5);
            log.Record($"c{i}", NowUtc.AddMinutes(i), new[] { rec }, new[] { card });
        }

        var a = log.SnapshotJson();
        var b = log.SnapshotJson();
        Assert.Equal(a, b);                                       // deterministic for same state
        using var doc = JsonDocument.Parse(a);                    // valid JSON, not prose
        Assert.Equal(4, doc.RootElement.GetProperty("capacity").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        var first = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("c0", first.GetProperty("cid").GetString());
        Assert.Equal(1, first.GetProperty("seen").GetInt32());
        Assert.Equal("r0", first.GetProperty("chosen")[0].GetString());
        // Index surface only: scores live in the entries, not the snapshot.
        Assert.DoesNotContain("0.5", a, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultCapacity_Is500_AndInvalidCapacityThrows()
    {
        Assert.Equal(500, new DecisionLog().Capacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionLog(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecisionLog(-5));
    }

    [Fact]
    public void Record_RejectsNulls_AndQuotesSpecialCharacters()
    {
        var log = new DecisionLog();
        var (rec, card) = Card("x", 0.5);
        Assert.Throws<ArgumentNullException>(() => log.Record(null!, NowUtc, new[] { rec }, new[] { card }));
        Assert.Throws<ArgumentNullException>(() => log.Record("c", NowUtc, null!, new[] { card }));
        Assert.Throws<ArgumentNullException>(() => log.Record("c", NowUtc, new[] { rec }, null!));

        // An id with quotes/backslashes cannot break the JSON (escaper is exercised end-to-end).
        var weird = Rec("a\"b\\c");
        log.Record("c\"or", NowUtc, new[] { weird }, new[] { new RecommendationCard(weird, 0.25, null) });
        using var doc = JsonDocument.Parse(log.SnapshotJson());
        Assert.Equal("c\"or", doc.RootElement.GetProperty("entries")[0].GetProperty("cid").GetString());
    }

    [Fact]
    public void EmptyCandidates_LogsASightedButChosenNothingDecision()
    {
        var log = new DecisionLog();
        var e = log.Record("c-empty", NowUtc, Array.Empty<Recommendation>(), Array.Empty<RecommendationCard>());

        Assert.Equal(0, e.CandidateCount);
        Assert.Empty(e.ChosenIds);
        Assert.Empty(e.DroppedIds);
        Assert.Equal("{}", e.ScoresJson);
    }
}
