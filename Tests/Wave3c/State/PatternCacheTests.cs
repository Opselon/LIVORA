using LIVORA.Application.Abstractions;
using LIVORA.Application.Patterns;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.History;
using static LIVORA.Tests.Wave3c.Lane04Fixtures;

namespace LIVORA.Tests.Wave3c;

/// <summary>Cache identity, zero-read hits, LRU bound + eviction counting — PatternCache (lane 04).</summary>
public class PatternCacheTests
{
    /// <summary>History fake that COUNTS reads — the whole point being provable.</summary>
    private sealed class CountingHistoryRepository : IHistoryRepository
    {
        private readonly List<DailyHistoryRecord> _records;
        public CountingHistoryRepository(IEnumerable<DailyHistoryRecord> records) => _records = records.ToList();
        public int GetAllCalls { get; private set; }

        public Task<IReadOnlyList<DailyHistoryRecord>> GetAllAsync()
        {
            GetAllCalls++;
            return Task.FromResult<IReadOnlyList<DailyHistoryRecord>>(_records.ToList());
        }
        public Task UpsertAsync(DailyHistoryRecord record) => Task.CompletedTask;
        public Task EnsureLoadedAsync(UserProfile profile, DateTime today) => Task.CompletedTask;
        public Task RecordInsightAsync(DateTime date, InsightTopic topic, RecommendationPriority priority) => Task.CompletedTask;
        public Task RecordHabitCompletionAsync(DateTime date, string habitId, bool completed) => Task.CompletedTask;
        public Task RecordGoalProgressAsync(DateTime date, string goalId) => Task.CompletedTask;
    }

    // ---- key identity ---------------------------------------------------------

    [Fact]
    public void KeyFor_StableForSameData_ChangesWithCountAndNewestDate()
    {
        var a = PatternCache.KeyFor(Flat(10));
        Assert.Equal(a, PatternCache.KeyFor(Flat(10)));                 // same identity
        Assert.NotEqual(a, PatternCache.KeyFor(Flat(11)));              // +1 record
        Assert.NotEqual(a, PatternCache.KeyFor(Flat(9)));               // −1 record
        var moved = Flat(10);
        moved[9] = Day(11);                                             // same count, newer day
        Assert.NotEqual(a, PatternCache.KeyFor(moved));
        Assert.NotEqual(a, PatternCache.KeyFor(new List<DailyHistoryRecord>()));  // empty is its own key
    }

    // ---- zero reads on hit ------------------------------------------------------

    [Fact]
    public async Task SecondCall_SameKey_ReadsRepositoryZeroTimes()
    {
        // Key computed from the data WITHOUT touching the counter's repo instance semantics:
        var records = Flat(28);
        var key = PatternCache.KeyFor(records);
        var repo = new CountingHistoryRepository(records);
        var cache = new PatternCache();

        var first = await cache.GetOrComputeAsync(key, repo);
        Assert.Equal(1, repo.GetAllCalls);           // cold: exactly one read
        var second = await cache.GetOrComputeAsync(key, repo);
        Assert.Equal(1, repo.GetAllCalls);           // HIT: zero further reads — the mandate
        var third = await cache.GetOrComputeAsync(key, repo);
        Assert.Equal(1, repo.GetAllCalls);
        Assert.Same(first, second);                  // identical stored result set, verbatim
        Assert.Same(first, third);
    }

    [Fact]
    public async Task DifferentKey_TriggersAFreshRead_AndBothSetsCoexist()
    {
        var cache = new PatternCache();
        var recA = Flat(28);
        var recB = Flat(21);
        var repoA = new CountingHistoryRepository(recA);
        var repoB = new CountingHistoryRepository(recB);
        await cache.GetOrComputeAsync(PatternCache.KeyFor(recA), repoA);
        await cache.GetOrComputeAsync(PatternCache.KeyFor(recB), repoB);
        Assert.Equal(1, repoA.GetAllCalls);          // distinct keys: one read each, no false hit
        Assert.Equal(1, repoB.GetAllCalls);
        Assert.Equal(2, cache.Keys.Count);
    }

    // ---- LRU bound + eviction ------------------------------------------------------

    [Fact]
    public async Task Lru_CapsAtFiveResultSets_AndEvictsTheLeastRecentlyUsed()
    {
        var cache = new PatternCache();
        Assert.Equal(5, PatternCache.MaxResultSets);

        var repos = new List<CountingHistoryRepository>();
        for (int i = 0; i < 5; i++)
        {
            var repo = new CountingHistoryRepository(Flat(28 + i));
            repos.Add(repo);
            await cache.GetOrComputeAsync($"k{i}", repo);
        }
        Assert.Equal(5, cache.Keys.Count);
        Assert.Equal(0, cache.EvictionCount);
        Assert.All(repos, r => Assert.Equal(1, r.GetAllCalls));

        // Sixth distinct key ⇒ exactly one eviction (k0, the oldest).
        var repo5 = new CountingHistoryRepository(Flat(33));
        await cache.GetOrComputeAsync("k5", repo5);
        Assert.Equal(5, cache.Keys.Count);
        Assert.Equal(1, cache.EvictionCount);
        Assert.DoesNotContain("k0", cache.Keys);
        Assert.Equal("k5", cache.Keys[0]);   // newest first

        // k0 is gone: a call on it reads the repo AGAIN (proves the slot really vacated).
        var repo0again = new CountingHistoryRepository(Flat(28));
        await cache.GetOrComputeAsync("k0", repo0again);
        Assert.Equal(1, repo0again.GetAllCalls);
        Assert.Equal(2, cache.EvictionCount);   // k1 fell out to make room
        Assert.DoesNotContain("k1", cache.Keys);
    }

    [Fact]
    public async Task Lru_Hit_TouchesEntryToMostRecent()
    {
        var cache = new PatternCache();
        for (int i = 0; i < 5; i++)
            await cache.GetOrComputeAsync($"k{i}", new CountingHistoryRepository(Flat(28 + i)));

        // Touch k0: it becomes most-recent, so the next insert must evict k1 instead.
        await cache.GetOrComputeAsync("k0", new CountingHistoryRepository(Flat(28)));
        Assert.Equal("k0", cache.Keys[0]);
        await cache.GetOrComputeAsync("k5", new CountingHistoryRepository(Flat(33)));
        Assert.DoesNotContain("k1", cache.Keys);
        Assert.Contains("k0", cache.Keys);
        Assert.Equal(1, cache.EvictionCount);
    }

    [Fact]
    public async Task Invalidate_EmptyiesTheCache_ForceingFreshReads()
    {
        var cache = new PatternCache();
        var repo = new CountingHistoryRepository(Flat(28));
        await cache.GetOrComputeAsync("k", repo);
        await cache.GetOrComputeAsync("k", repo);
        Assert.Equal(1, repo.GetAllCalls);

        cache.Invalidate();
        Assert.Empty(cache.Keys);
        await cache.GetOrComputeAsync("k", repo);
        Assert.Equal(2, repo.GetAllCalls);   // cold again — honest, not a stale hit
    }

    // ---- thread-safety ---------------------------------------------------------------

    [Fact]
    public async Task ConcurrentCalls_NeverCorrupt_TheIndexOrLru()
    {
        var cache = new PatternCache();
        var key = "shared";
        var repo = new CountingHistoryRepository(Flat(28));
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await cache.GetOrComputeAsync(key, repo)))
            .ToList();
        var results = await Task.WhenAll(tasks);

        // Every caller sees the same value-equal result; the index holds exactly one entry;
        // repository reads stay small (a few racing misses are allowed, 32 reads are not).
        Assert.All(results, r => Assert.Equal(results[0].Findings.Count, r.Findings.Count));
        Assert.Single(cache.Keys);
        Assert.True(repo.GetAllCalls <= 8, $"thundering herd: {repo.GetAllCalls} reads for one key");
    }

    [Fact]
    public async Task CachedResult_CarriesRealFindings_ForALateSleepFixture()
    {
        var hist = Enumerable.Range(0, 28).Select(i => Day(i, bedtime: i >= 24 ? 1500 : 1380)).ToList();
        var repo = new CountingHistoryRepository(hist);
        var cache = new PatternCache();
        var result = await cache.GetOrComputeAsync(PatternCache.KeyFor(hist), repo);
        Assert.Contains(result.Findings, f => f.Kind == PatternKind.LateSleepRecurring);
    }
}
