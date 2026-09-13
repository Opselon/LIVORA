namespace LIVORA.Presentation.Views;

/// <summary>
/// One-shot entrance fade for card stacks (wave 3c, lane 07 UI surfaces).
///
/// Contract: each card goes opacity 0 -> 1 over ~140 ms, staggered ~20 ms per card, ONCE per
/// page instance (the first time the page appears). No looping animation, no pulsing.
///
/// Timer policy (audited): this class uses NO System.Threading.Timer and NO DispatcherTimer.
/// A single finite <see cref="Task.Delay(int)"/> per staggered card schedules the start of a
/// fade; both the delay and the <c>FadeToAsync</c> complete on their own and are dropped, so nothing
/// periodic survives page dismissal. The awaiting page never needs a Disappearing cleanup for a
/// running loop because there is no loop — the worst case after a pop is one already-scheduled
/// 60 ms first-frame delay firing on a detached element, which is a no-op.
///
/// ReduceMotion: the repo currently has no reduce-motion token anywhere (grep: zero hits in
/// Resources/, Presentation/, docs/), so this lane does NOT invent a shared one. The static flag
/// below is the seam: when lane 05 lands a token, its setter writes here and every page built
/// on this helper honors it immediately.
/// </summary>
public static class Wave3cMotion
{
    /// <summary>Fade duration per card, milliseconds.</summary>
    public const uint FadeMs = 140;

    /// <summary>Extra start delay per card index, milliseconds.</summary>
    public const uint StaggerMs = 20;

    /// <summary>True = skip the fade entirely (instant opacity 1). Wired by whoever owns the
    /// accessibility token; nothing in this wave sets it, and the default keeps the fade on.</summary>
    public static bool ReduceMotion { get; set; }

    /// <summary>
    /// Fade the given cards in, in order, staggered by <see cref="StaggerMs"/>. Await-safe: the
    /// caller in OnAppearing fires it once and never again for the page's lifetime.
    /// </summary>
    public static async Task StaggerInAsync(params VisualElement?[] elements)
    {
        var cards = elements.OfType<VisualElement>().ToArray();
        if (cards.Length == 0) return;

        if (ReduceMotion)
        {
            foreach (var c in cards) c.Opacity = 1;
            return;
        }

        var tasks = new List<Task>(cards.Length);
        for (var i = 0; i < cards.Length; i++)
        {
            cards[i].Opacity = 0;
            tasks.Add(FadeOneAsync(cards[i], (uint)(i * (int)StaggerMs)));
        }
        try { await Task.WhenAll(tasks); }
        catch { /* a fade interrupted by page teardown must never surface as an error */ }
    }

    private static async Task FadeOneAsync(VisualElement el, uint delay)
    {
        if (delay > 0) await Task.Delay((int)delay);
        await el.FadeToAsync(1, FadeMs, Easing.CubicOut);
        el.Opacity = 1; // land exactly on 1 even if the animation was cut short
    }
}
