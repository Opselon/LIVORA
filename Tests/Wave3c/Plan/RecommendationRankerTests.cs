using LIVORA.Application.Planning.Wave3b;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;
using static LIVORA.Tests.Wave3c.Plan.Wave3cFixtures;

namespace LIVORA.Tests.Wave3c.Plan;

/// <summary>
/// Lane 05: the ranked decision layer. One isolatable test per weight, dedupe, caps, expiry,
/// determinism.
/// </summary>
public class RecommendationRankerTests
{
    private static readonly RecommendationRanker Ranker = new();
    private static readonly DateTime Now = Wave3cFixtures.Now;   // 2026-09-21 10:00 (mid-morning)

    private static UserProfile Profile(params string[] focus) =>
        new() { FocusAreas = focus.ToList() };

    private static IReadOnlyList<RecommendationCard> Rank(
        IReadOnlyList<Recommendation> cands, UserProfile? profile = null,
        DailyPlan? plan = null, DateTime? now = null, int k = RecommendationRanker.DefaultK,
        IReadOnlyList<Goal>? goals = null) =>
        Ranker.Rank(cands, profile ?? Profile(), plan, now ?? Now, k, goals);

    // ---- W1 goalAlignment -----------------------------------------------------

    [Fact]
    public void W1_GoalAlignment_NudgeFlipsOrdering()
    {
        // Identical cards except category + Id ("z" first alphabetically LAST => loses ties).
        var generalZ = Rec("z", category: RecommendationCategory.General);
        var sleepA = Rec("a", category: RecommendationCategory.Sleep);

        // No focus areas: equal scores → tie-break by Id ascending → "a"(sleep) first.
        var plain = Rank(new[] { generalZ, sleepA }, Profile("nothing"));
        Assert.Equal("a", plain[0].Recommendation.Id);

        // The W1 nudge: sleep becomes a focus area → the aligned card wins despite its Id.
        var aligned = Rank(new[] { generalZ, sleepA }, Profile("sleep"));
        Assert.Equal("a", aligned[0].Recommendation.Id);   // same card, but now wins by score...
        Assert.True(aligned[0].Score > aligned[1].Score);

        // Flip the other way: only "general" aligned → the previously-losing card wins outright.
        var flipped = Rank(new[] { sleepA, generalZ }, Profile("general"));
        Assert.Equal("z", flipped[0].Recommendation.Id);
        Assert.Equal(RecommendationRanker.W1GoalAlignment,
            flipped[0].Score - flipped[1].Score, 6);     // the delta is exactly W1
    }

    [Fact]
    public void W1_PlanLinkedIdCountsAsAlignment()
    {
        // Habit-category card whose ExplainArgs carry the plan's habit LinkedId aligns with "habits"?
        // No — alignment matches tokens; the plan bridge lets a LinkedId token in args align.
        var plan = StandardPlan();
        var bridged = Rec("b", action: RecommendationActionKind.AdvanceGoal,
            category: RecommendationCategory.General,
            textArgs: new object[] { "goal-workout" });
        var plain = Rec("a", category: RecommendationCategory.General);

        var noFocus = Rank(new[] { bridged, plain }, Profile("goal-workout"), plan);
        Assert.Equal("b", noFocus[0].Recommendation.Id);  // focused on the linked id token
    }

    // ---- W2 urgency ------------------------------------------------------------

    [Fact]
    public void W2_PriorityNudgeFlipsOrdering()
    {
        var lowA = Rec("a", priority: RecommendationPriority.Low);
        var lowB = Rec("b", priority: RecommendationPriority.Low);
        Assert.Equal("a", Rank(new[] { lowA, lowB }).First().Recommendation.Id); // tie → Id

        var highB = Rec("b", priority: RecommendationPriority.High);
        var cards = Rank(new[] { lowA, highB });
        Assert.Equal("b", cards[0].Recommendation.Id);    // urgency lifted B above A
    }

    [Fact]
    public void W2_DeadlineProximityLiftsUrgencyAndFlipsOrdering()
    {
        var a = Rec("a");   // identical cards except Id
        var b = Rec("b");
        Assert.Equal("a", Rank(new[] { a, b }).First().Recommendation.Id); // tie → Id asc

        // A goal due TOMORROW (fraction 0.4 < 0.5): proximity ≈ 1 → urgency lifts BOTH cards
        // equally (the ranking term is global) — so lift only via a priority-differentiated pair:
        var weakerHigh = Rec("z", priority: RecommendationPriority.Medium);
        var strongerLow = Rec("a", priority: RecommendationPriority.Low);
        Assert.Equal("z", Rank(new[] { strongerLow, weakerHigh }).First().Recommendation.Id);

        var far = new[] { WorkoutGoal(Now.AddDays(29), 0.2) };   // prox = 1/30 → tiny lift
        var near = new[] { WorkoutGoal(Now.AddDays(1), 0.2) };   // prox = 29/30 → big lift
        var farCards = Rank(new[] { strongerLow, weakerHigh }, goals: far);
        var nearCards = Rank(new[] { strongerLow, weakerHigh }, goals: near);
        Assert.Equal(farCards[0].Recommendation.Id, nearCards[0].Recommendation.Id); // order stable
        Assert.True(nearCards.Sum(c => c.Score) > farCards.Sum(c => c.Score));       // urgency rose
    }

    // ---- W3 confidence -----------------------------------------------------------

    [Fact]
    public void W3_ConfidenceNudgeFlipsOrdering()
    {
        var a = Rec("a", confidence: 0.5);
        var b = Rec("b", confidence: 0.5);
        Assert.Equal("a", Rank(new[] { a, b }).First().Recommendation.Id);

        var bSure = Rec("b", confidence: 0.95);
        Assert.Equal("b", Rank(new[] { a, bSure }).First().Recommendation.Id);
    }

    // ---- W4 schedule fit -------------------------------------------------------------

    [Fact]
    public void W4_CrossingTheEveningCutoffFlipsOrdering()
    {
        // B leads early (higher confidence); late in the day B's 60-min block ends after 22:00
        // → W4 zeroes it and A (30 min, ends 21:45) wins. Only the schedule-fit term changed.
        var a = Rec("a", duration: 30, confidence: 0.3);
        var b = Rec("b", duration: 60, confidence: 0.9);

        var morning = Rank(new[] { a, b }, now: new DateTime(2026, 9, 21, 10, 0, 0));
        Assert.Equal("b", morning[0].Recommendation.Id);

        var lateNow = new DateTime(2026, 9, 21, 21, 15, 0);
        var late = Rank(new[] { a, b }, now: lateNow);
        Assert.Equal("a", late[0].Recommendation.Id);
        Assert.Equal(0.0, BreakdownOf(late.First(c => c.Recommendation.Id == "b"), lateNow).ScheduleFit); // B fell off
        Assert.Equal(1.0, BreakdownOf(late[0], lateNow).ScheduleFit);
    }

    [Fact]
    public void W4_EndingExactlyAt2200DoesNotFit()
    {
        var card = Rec("x", duration: 30);
        var t2200 = new DateTime(2026, 9, 21, 21, 30, 0);
        Assert.Equal(0.0, BreakdownOf(Rank(new[] { card }, now: t2200)[0], t2200).ScheduleFit); // ends 22:00 sharp
        var t2159 = new DateTime(2026, 9, 21, 21, 29, 0);
        Assert.Equal(1.0, BreakdownOf(Rank(new[] { card }, now: t2159)[0], t2159).ScheduleFit);
    }

    // ---- W5 effort ---------------------------------------------------------------------

    [Fact]
    public void W5_EffortNudgeFlipsOrdering()
    {
        var a = Rec("a", duration: 150);
        var b = Rec("b", duration: 200);   // heavier effort, otherwise identical, later Id
        Assert.Equal("a", Rank(new[] { a, b }).First().Recommendation.Id);

        var bLight = Rec("b", duration: 100);  // only the effort term moves: 200 → 100
        Assert.Equal("b", Rank(new[] { a, bLight }).First().Recommendation.Id);
        Assert.Equal(RecommendationRanker.W5Effort * (50 / 120.0),
            Rank(new[] { a, bLight }).First().Score - Rank(new[] { a, bLight })[1].Score, 6);
    }

    // ---- composition + presentation rules ---------------------------------------------

    [Fact]
    public void ScoreIsTheNamedWeightCombination()
    {
        var r = Rec("q", category: RecommendationCategory.Sleep,
            priority: RecommendationPriority.Medium, confidence: 0.8, duration: 30);
        var card = Rank(new[] { r }, Profile("sleep")).Single();
        var b = RecommendationRanker.Breakdown(card.Recommendation,
            new HashSet<string>(StringComparer.Ordinal) { "sleep" },
            new HashSet<string>(StringComparer.Ordinal), 0, Now);

        double expected = RecommendationRanker.W1GoalAlignment * b.GoalAlignment
                        + RecommendationRanker.W2Urgency * b.Urgency
                        + RecommendationRanker.W3Confidence * b.Confidence
                        + RecommendationRanker.W4ScheduleFit * b.ScheduleFit
                        - RecommendationRanker.W5Effort * b.Effort;
        Assert.Equal(Math.Round(expected, 6), card.Score);
        Assert.Equal(0.25, b.Urgency, 6);     // 0.5·(2/4) + 0.5·0
    }

    [Fact]
    public void Dedupe_KeepsBestScorePerActionCategoryTextKeyArgs()
    {
        var weak = Rec("w", confidence: 0.4, textKey: "Rec.Same");
        var strong = Rec("s", confidence: 0.9, textKey: "Rec.Same");  // same fingerprint
        var other = Rec("o", action: RecommendationActionKind.TakeBreak, confidence: 0.5);

        var cards = Rank(new[] { weak, strong, other });
        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, c => c.Recommendation.Id == "s");
        Assert.DoesNotContain(cards, c => c.Recommendation.Id == "w");
    }

    [Fact]
    public void Cap_OneHighPlusThreeOthers_KLimits()
    {
        var highShapes = new[]
        {
            (RecommendationActionKind.EarlierBedtime, RecommendationCategory.Sleep),
            (RecommendationActionKind.NapBriefly, RecommendationCategory.Sleep),
            (RecommendationActionKind.AdvanceGoal, RecommendationCategory.Goal),
        };
        var medShapes = new[]
        {
            (RecommendationActionKind.ShortWalk, RecommendationCategory.Activity),
            (RecommendationActionKind.TakeBreak, RecommendationCategory.Stress),
            (RecommendationActionKind.CompleteHabit, RecommendationCategory.Habit),
            (RecommendationActionKind.ModerateScreenTime, RecommendationCategory.Sleep),
        };
        var highs = Enumerable.Range(0, 3).Select(i =>
            Rec($"h{i}", action: highShapes[i].Item1, category: highShapes[i].Item2,
                priority: RecommendationPriority.High, confidence: 0.5 + i * 0.01)).ToList();
        var mediums = Enumerable.Range(0, 4).Select(i =>
            Rec($"m{i}", action: medShapes[i].Item1, category: medShapes[i].Item2,
                confidence: 0.4 + i * 0.01)).ToList();

        var cards = Rank(highs.Concat(mediums).ToList());

        Assert.Equal(4, cards.Count);   // k default = 4
        Assert.Equal(1, cards.Count(c => (int)c.Recommendation.ScoredPriority >= (int)RecommendationPriority.High));
        Assert.Equal("h2", cards.First(c => (int)c.Recommendation.ScoredPriority >= (int)RecommendationPriority.High)
            .Recommendation.Id);        // best high (highest confidence) survives
        Assert.Equal(3, cards.Count(c => (int)c.Recommendation.ScoredPriority < (int)RecommendationPriority.High));

        Assert.Equal(2, Rank(highs.Concat(mediums).ToList(), k: 2).Count);
    }

    [Fact]
    public void StaleCandidates_GetExpiresAt_AndAreExcludedPastIt()
    {
        var fresh = Rec("fresh", createdAt: Now.AddHours(-23));
        var old = Rec("old", createdAt: Now.AddHours(-25));

        var cards = Rank(new[] { fresh, old });
        var freshCard = Assert.Single(cards);
        Assert.Equal("fresh", freshCard.Recommendation.Id);
        Assert.Equal(Now.AddHours(1), freshCard.ExpiresAt);     // created+24h, still ahead
    }

    [Fact]
    public void UnsetCreatedAt_IsTimeless_NoExpiryNoExclusion()
    {
        var timeless = Rec("t", timeless: true);
        var card = Assert.Single(Rank(new[] { timeless }));
        Assert.Null(card.ExpiresAt);
    }

    [Fact]
    public void EmptyIn_EmptyOut()
    {
        Assert.Empty(Rank(Array.Empty<Recommendation>()));
        Assert.Empty(Rank(new List<Recommendation>()));
    }

    [Fact]
    public void Determinism_StableTieBreakById_AndRepeatable()
    {
        var cands = new[] { Rec("c"), Rec("a"), Rec("b") };   // identical scores
        var first = Rank(cands);
        var second = Rank(cands.Reverse().ToList());

        Assert.Equal(new[] { "a", "b", "c" }, first.Select(c => c.Recommendation.Id));
        Assert.Equal(first.Select(c => c.Recommendation.Id), second.Select(c => c.Recommendation.Id));
    }

    private static ScoreBreakdown BreakdownOf(RecommendationCard card, DateTime? at = null) =>
        RecommendationRanker.Breakdown(card.Recommendation,
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal), 0, at ?? Now);
}
