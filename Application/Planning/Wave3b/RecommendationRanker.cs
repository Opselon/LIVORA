using System.Globalization;
using System.Text;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Planning;

namespace LIVORA.Application.Planning.Wave3b;

/// <summary>
/// A scored recommendation: the frozen <see cref="Recommendation"/> plus the ranker's verdict.
/// Lane-05 DTO (the contracts are frozen; nothing here leaks back into Domain).
/// ExpiresAt is set only for stale candidates (produced > 24h before <c>now</c>); null = fresh.
/// </summary>
public sealed record RecommendationCard(Recommendation Recommendation, double Score, DateTime? ExpiresAt);

/// <summary>Per-component score breakdown — exposed so each weight has an isolatable test.</summary>
public sealed record ScoreBreakdown(
    double GoalAlignment, double Urgency, double DeadlineProximity, double PriorityNorm,
    double Confidence, double ScheduleFit, double Effort, double Total);

/// <summary>
/// Wave 3c (lane 05): the ranked decision layer under the Today cards. Deterministic: same
/// candidates + profile + plan + now => same card order (stable tie-break by Id, ordinal).
///
/// score = W1·goalAlignment + W2·urgency + W3·confidence + W4·scheduleFit − W5·effort, where
///   goalAlignment  : candidate speaks one of the profile's FocusAreas (category token, plan
///                    LinkedId bridge, or a focus token inside its TextArgs/ExplainArgs) → 1 else 0
///   urgency        : 0.5·(ScoredPriority/4) + 0.5·deadlineProximity (nearest active goal deadline
///                    over a horizon of DeadlineHorizonDays; goals optional — absent → priority only)
///   confidence     : Recommendation.Confidence clamped to 0..1
///   scheduleFit    : 1 when now + DurationMinutes still ends before the EveningCutoff (22:00)
///   effort         : DurationMinutes / 120 (two hours of commitment is the full penalty)
///
/// Presentation rules: dedupe by ActionKind+Category+TextKey+args keeping the best score; the
/// visible set is at most 1 High/Critical + up to 3 others, capped by k (default 4); candidates
/// carries ExpiresAt = CreatedAt + StaleCandidateHours and drops out at/after it; empty in ⇒ empty out.
/// </summary>
public sealed class RecommendationRanker
{
    // ---- named weights (one isolatable test per weight) ----
    public const double W1GoalAlignment = 0.35;
    public const double W2Urgency = 0.30;
    public const double W3Confidence = 0.25;
    public const double W4ScheduleFit = 0.20;
    public const double W5Effort = 0.25;

    public const int DefaultK = 4;
    public const int MaxHighPriorityCards = 1;
    public const int MaxOtherCards = 3;
    public const int StaleCandidateHours = 24;
    public const int DeadlineHorizonDays = 30;

    /// <summary>Cards must still FINISH before this local time to count as schedulable.</summary>
    public static readonly TimeSpan EveningCutoff = new(22, 0, 0);

    public IReadOnlyList<RecommendationCard> Rank(
        IReadOnlyList<Recommendation> candidates,
        UserProfile profile,
        DailyPlan? plan,
        DateTime now,
        int k = DefaultK,
        IReadOnlyList<Goal>? goals = null)
    {
        if (candidates is null || candidates.Count == 0 || k <= 0) return Array.Empty<RecommendationCard>();

        var focus = FocusTokens(profile);
        var linked = PlanLinkedTokens(plan);
        double deadlineProximity = DeadlineProximity(goals, now);

        // 1) score + expiry filter ------------------------------------------------------
        var scored = new List<RecommendationCard>();
        foreach (var c in candidates)
        {
            // Freshness window: a candidate carries the moment it stops being offerable
            // (CreatedAt + 24h). Anything at/past that moment is excluded. CreatedAt unset
            // (default) is treated as timeless — no expiry, never excluded.
            DateTime? expiresAt = c.CreatedAt == default ? null : c.CreatedAt.AddHours(StaleCandidateHours);
            if (expiresAt is { } exp && now >= exp) continue;
            var b = Breakdown(c, focus, linked, deadlineProximity, now);
            scored.Add(new RecommendationCard(c, Math.Round(b.Total, 6), expiresAt));
        }

        // 2) dedupe by ActionKind+Category+TextKey+args, keeping the best ----------------
        var deduped = scored
            .GroupBy(c => DedupeKey(c.Recommendation), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(c => c.Score)
                          .ThenBy(c => c.Recommendation.Id, StringComparer.Ordinal)
                          .First())
            .ToList();

        // 3) rank (score desc, stable Id tie-break) then cap 1 High + up to 3 others -----
        var ordered = deduped
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Recommendation.Id, StringComparer.Ordinal)
            .ToList();

        var high = ordered
            .Where(c => (int)c.Recommendation.ScoredPriority >= (int)RecommendationPriority.High)
            .Take(MaxHighPriorityCards)
            .ToList();
        var others = ordered
            .Where(c => (int)c.Recommendation.ScoredPriority < (int)RecommendationPriority.High)
            .Take(MaxOtherCards)
            .ToList();

        return high.Concat(others)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Recommendation.Id, StringComparer.Ordinal)
            .Take(k)
            .ToList();
    }

    /// <summary>Public for weight-isolation tests: the per-component arithmetic of one card.</summary>
    public static ScoreBreakdown Breakdown(
        Recommendation c, IReadOnlySet<string> focusTokens, IReadOnlySet<string> planLinkedTokens,
        double deadlineProximity, DateTime now)
    {
        double goalAlignment = IsGoalAligned(c, focusTokens, planLinkedTokens) ? 1.0 : 0.0;

        double priorityNorm = Math.Clamp((int)c.ScoredPriority, 0, 4) / 4.0;
        double urgency = 0.5 * priorityNorm + 0.5 * deadlineProximity;

        double confidence = Math.Clamp(c.Confidence, 0, 1);

        var end = now.AddMinutes(Math.Max(0, c.DurationMinutes));
        double scheduleFit = end.TimeOfDay < EveningCutoff && end.Date == now.Date ? 1.0 : 0.0;
        // A block ending exactly at 22:00 (or rolling past midnight) is not "before 22:00".

        double effort = Math.Max(0, c.DurationMinutes) / 120.0;

        double total = W1GoalAlignment * goalAlignment
                     + W2Urgency * urgency
                     + W3Confidence * confidence
                     + W4ScheduleFit * scheduleFit
                     - W5Effort * effort;

        return new ScoreBreakdown(goalAlignment, urgency, deadlineProximity, priorityNorm,
            confidence, scheduleFit, effort, total);
    }

    // ---- alignment helpers -------------------------------------------------------------

    /// <summary>profile.FocusAreas → lowercase ordinal token set ("sleep", "activity", …).</summary>
    internal static IReadOnlySet<string> FocusTokens(UserProfile profile) =>
        (IReadOnlySet<string>)profile.FocusAreas
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A Recommendation has no LinkedId field (frozen Domain), so the plan is the bridge:
    /// a candidate aligned with a plan category whose item carries a LinkedId can match that
    /// linkage too (goal/habit ids surface in FocusAreas-free alignment checks).
    /// </summary>
    internal static IReadOnlySet<string> PlanLinkedTokens(DailyPlan? plan)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (plan is null) return set;
        foreach (var i in plan.Items)
        {
            if (i.LinkedId is { Length: > 0 } id) set.Add(id);
        }
        return set;
    }

    private static bool IsGoalAligned(Recommendation c, IReadOnlySet<string> focus, IReadOnlySet<string> planLinked)
    {
        if (focus.Count == 0) return false;
        if (focus.Contains(CategoryToken(c.Category))) return true;

        // TextArgs/ExplainArgs may carry a focus token or a goal/habit id (the engine passes ids
        // through as machine tokens — never prose — so matching them is safe here).
        foreach (var args in new[] { c.TextArgs, c.ExplainArgs })
            foreach (var a in args)
            {
                if (a is null) continue;
                var s = Convert.ToString(a, CultureInfo.InvariantCulture)?.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(s)) continue;
                if (focus.Contains(s)) return true;
                if (planLinked.Count > 0 && planLinked.Contains(s)) return true;
            }
        return false;
    }

    /// <summary>Category enum → the lowercase focus-area token vocabulary used by UserProfile.</summary>
    internal static string CategoryToken(RecommendationCategory category) => category switch
    {
        RecommendationCategory.Sleep => "sleep",
        RecommendationCategory.Activity => "activity",
        RecommendationCategory.Recovery => "recovery",
        RecommendationCategory.Focus => "focus",
        RecommendationCategory.Stress => "stress",
        RecommendationCategory.Habit => "habits",
        RecommendationCategory.Goal => "goals",
        RecommendationCategory.Program => "program",
        _ => "general",
    };

    private static double DeadlineProximity(IReadOnlyList<Goal>? goals, DateTime now)
    {
        if (goals is null || goals.Count == 0) return 0;
        double best = 0;
        foreach (var g in goals)
        {
            if (g.IsArchived || g.Deadline is not { } d) continue;
            double days = (d.Date - now.Date).TotalDays;
            if (days < 0 || days > DeadlineHorizonDays) continue;
            double prox = 1 - days / DeadlineHorizonDays;           // due today → 1, in 30d → 0
            if (prox > best) best = prox;
        }
        return best;
    }

    /// <summary>
    /// Dedupe fingerprint: ActionKind + Category + TextKey + invariant-culture args.
    /// Machine-shaped on purpose (no user prose reaches it — TextKey is a localization key and
    /// args are ids/numbers at this layer).
    /// </summary>
    internal static string DedupeKey(Recommendation c)
    {
        var sb = new StringBuilder();
        sb.Append(c.ActionKind).Append('|').Append(c.Category).Append('|').Append(c.TextKey);
        foreach (var a in c.TextArgs ?? Array.Empty<object>())
            sb.Append('|').Append(Convert.ToString(a, CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
