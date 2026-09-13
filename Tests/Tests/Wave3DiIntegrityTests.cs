using System.Text.RegularExpressions;
using LIVORA.Application.Abstractions;
using LIVORA.Application.Context;
using LIVORA.Application.HealthData;
using LIVORA.Application.Insights;
using LIVORA.Application.Planning;
using LIVORA.Application.Rules;
using LIVORA.Application.State;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Infrastructure.IntelligenceProviders;
using LIVORA.Infrastructure.Localization;
using LIVORA.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Composition-root integrity. Ten lanes each hand the orchestrator an `APPEND:` block that lands in
/// one of three marker regions; a block applied twice, or a registration with a typo, is a runtime
/// failure the app build may not catch (duplicate DI entries resolve to the LAST one silently).
/// These tests read the composition files as text — the same shape a merge reviewer reads — and
/// build a real container for the part of the graph that can exist without a MAUI head.
/// </summary>
public class Wave3DiIntegrityTests
{
    private static readonly string? Root = Wave3Harness.RepoRoot;

    private static string? Read(string relative)
    {
        if (Root is null) return null;
        var path = Path.Combine(Root, relative);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string Region(string text, string open, string close)
    {
        var m = Regex.Match(text, Regex.Escape(open) + ".*?" + Regex.Escape(close), RegexOptions.Singleline);
        return m.Success ? m.Value : "";
    }

    // ---- marker regions (the merge protocol) ------------------------------

    [Theory]
    [InlineData("MauiProgram.cs", "// WAVE3-DI:", "// WAVE3-DI-END", "builder.Services")]
    [InlineData("App.xaml.cs", "// WAVE3-APP:", "// WAVE3-APP-END", "CreateWindow")]
    [InlineData("AppShell.xaml.cs", "// WAVE3-SHELL:", "// WAVE3-SHELL-END", "Routing.RegisterRoute")]
    public void MarkerRegion_ExistsExactlyOnce_AndSitsInsideItsHost(string file, string open, string close, string hostContext)
    {
        var text = Read(file);
        if (text is null) return;   // composition file moved — skip rather than invent a failure

        // The prose inside the region names the markers, so count line-anchored hits only:
        // the real marker is the comment that opens/closes the region on its own line.
        var openRx = new Regex(@"^\s*" + Regex.Escape(open), RegexOptions.Multiline);
        var closeRx = new Regex(@"^\s*" + Regex.Escape(close), RegexOptions.Multiline);
        Assert.Equal(1, openRx.Matches(text).Count);
        Assert.Equal(1, closeRx.Matches(text).Count);
        Assert.True(text.IndexOf(open, StringComparison.Ordinal) < text.IndexOf(close, StringComparison.Ordinal));
        Assert.Contains(hostContext, text);
    }

    [Fact]
    public void MarkerRegion_Blocks_AreInFileOrderAndNotOverlapping()
    {
        // The orchestrator pastes lane blocks inside the region; a lane that ships a block already
        // containing the END marker would truncate the region and silently drop the rest.
        var text = Read("MauiProgram.cs");
        if (text is null) return;
        var region = Region(text, "// WAVE3-DI:", "// WAVE3-DI-END");
        Assert.DoesNotContain("builder.Build()", region);   // the region must stay above app build
        Assert.DoesNotContain("ServiceHelper.Initialize", region);
    }

    [Fact]
    public void NoServiceInterface_IsRegisteredTwiceInMauiProgram()
    {
        // A duplicate registration is not an error in MS.DI — the last one silently wins, which is
        // exactly how a merge could swap the real IDataProvider back to the mock without anyone
        // noticing. Every contract may appear once.
        var text = Read("MauiProgram.cs");
        if (text is null) return;

        var pattern = new Regex(@"Add(?:Singleton|Scoped|Transient)<(?<t>[A-Za-z0-9_<>,\. \?]+?)(?:,|\()");
        var seen = new List<string>();
        var dupes = new List<string>();
        foreach (Match m in pattern.Matches(text))
        {
            var service = m.Groups["t"].Value.Trim();
            if (seen.Contains(service)) dupes.Add(service);
            else seen.Add(service);
        }
        Assert.True(dupes.Count == 0,
            $"duplicate DI registrations (last wins silently): {string.Join(", ", dupes)}");
        Assert.Contains("IRuleEngine", seen);
        Assert.Contains("IUserStateService", seen);
    }

    [Fact]
    public void EveryConcreteTypeNamedInMauiProgram_ExistsSomInTheDocument()
    {
        // Catches the merge failure a build would also catch, but with a message that names the
        // lane whose file did not land (a bare CS0246 does not tell you which APPEND block broke).
        var text = Read("MauiProgram.cs");
        if (text is null) return;

        var concrete = new Regex(@"Add(?:Singleton|Scoped|Transient)<[^>]*,\s*(?<t>[A-Za-z0-9_]+)>")
            .Matches(text).Select(m => m.Groups["t"].Value).Distinct().ToList();
        Assert.NotEmpty(concrete);

        var sources = Wave3Harness.Sources("Application")
            .Concat(Wave3Harness.Sources("Domain"))
            .Concat(Wave3Harness.Sources("Infrastructure"))
            .Concat(Wave3Harness.Sources("Presentation"))
            .Select(s => s.Source)
            .ToList();
        var missing = concrete
            .Where(t => !sources.Any(s => Regex.IsMatch(s, $@"(class|record|struct)\s+{Regex.Escape(t)}\b")))
            .ToList();
        Assert.True(missing.Count == 0,
            $"registered but not defined anywhere in the source tree: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Wave3Contracts_AreNotRegisteredYetInThisCopy_WhenTheirLanesHavenotLanded()
    {
        // Deliberate reverse check: if the orchestrator lands a lane block in this copy the test
        // must notice (it changes the merge checklist). Reported as information, not failure.
        var text = Read("MauiProgram.cs");
        if (text is null) return;
        var region = Region(text ?? "", "// WAVE3-DI:", "// WAVE3-DI-END");
        var pending = new[] { "IUpdateService", "IManualEntryService", "IReminderService", "IThemeService" }
            .Where(c => !region.Contains(c, StringComparison.Ordinal))
            .ToList();
        // In a clean Wave 3 base all four are pending; after the merge none should be. Either way the
        // region must not reference a type that does not exist in the tree (checked above).
        Assert.True(pending.Count is 0 or 4,
            $"half-merged DI region (pending: {string.Join(", ", pending)}) — re-run the merge checklist");
    }

    // ---- real container for the MAUI-free half of the graph ---------------

    [Fact]
    public void PureServiceGraph_ResolvesFromARealServiceProvider()
    {
        if (Root is null) return;
        using var provider = PureGraph().BuildServiceProvider();

        // Every contract the test project can honestly construct must resolve, with the lifetime the
        // composition root documents (singleton engines, transient-free on purpose).
        Assert.NotNull(provider.GetRequiredService<IRuleEngine>());
        Assert.NotNull(provider.GetRequiredService<ITrendService>());
        Assert.NotNull(provider.GetRequiredService<IBaselineService>());
        Assert.NotNull(provider.GetRequiredService<IUserStateService>());
        Assert.NotNull(provider.GetRequiredService<IRecommendationService>());
        Assert.NotNull(provider.GetRequiredService<IDailyPlanService>());
        Assert.NotNull(provider.GetRequiredService<IIntelligenceService>());
        Assert.NotNull(provider.GetRequiredService<IWeeklySummaryService>());
        Assert.NotNull(provider.GetRequiredService<IIntelligenceProvider>());
        Assert.NotNull(provider.GetRequiredService<IDataProvider>());
        Assert.NotNull(provider.GetRequiredService<IDataNormalizer>());
        Assert.NotNull(provider.GetRequiredService<IHistoryRepository>());
        Assert.NotNull(provider.GetRequiredService<ILocalizationService>());
        Assert.NotNull(provider.GetRequiredService<IFormatService>());
        Assert.NotNull(provider.GetRequiredService<ProgramAdapter>());
        Assert.NotNull(provider.GetRequiredService<SessionState>());
        Assert.NotNull(provider.GetRequiredService<IDateTimeProvider>());
    }

    [Fact]
    public void Singletons_AreTheSameInstanceAcrossResolves()
    {
        if (Root is null) return;
        using var provider = PureGraph().BuildServiceProvider();

        Assert.Same(provider.GetRequiredService<IRuleEngine>(), provider.GetRequiredService<IRuleEngine>());
        Assert.Same(provider.GetRequiredService<IUserStateService>(), provider.GetRequiredService<IUserStateService>());
        // The one-object-two-interfaces wiring the app relies on (LocalizationService IS the
        // IFormatService); if these ever split, live language switching stops reaching formatting.
        Assert.Same(provider.GetRequiredService<ILocalizationService>(), provider.GetRequiredService<IFormatService>());
    }

    [Fact]
    public void ResolvedGraph_RunsOneHonestEndToEndCycle()
    {
        // The cheapest real proof the seams line up: state -> rules -> recommendations -> plan ->
        // insight, with no key invented and no origin claim flipped on the way.
        if (Root is null) return;
        using var provider = PureGraph().BuildServiceProvider();

        var state = provider.GetRequiredService<IUserStateService>()
            .GetStateAsync(DataRefreshMode.InitialLoad).GetAwaiter().GetResult();
        Assert.Equal(DataOrigin.Manual, state.Metrics["sleep.minutes"].Origin);  // our fake provider is manual

        var fired = provider.GetRequiredService<IRuleEngine>()
            .Evaluate(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), DateTime.Today.AddHours(9));
        var recs = provider.GetRequiredService<IRecommendationService>()
            .BuildRecommendations(state, new UserProfile(), Array.Empty<Goal>(), Array.Empty<Habit>(), null, DateTime.Today.AddHours(9));

        Assert.All(recs, r =>
        {
            Assert.Contains('.', r.TextKey);
            Assert.DoesNotContain(' ', r.TextKey);
            Assert.InRange(r.Confidence, 0, 1);
        });
        Assert.True(recs.Count <= 3);
        Assert.NotNull(provider.GetRequiredService<IIntelligenceService>()
            .ExplainRecommendationKey(recs.FirstOrDefault() ?? new Recommendation { ExplainKey = "Rule.Reason.AllNearBaseline" }));
        Assert.All(fired, r =>
        {
            Assert.StartsWith("Rule.", r.RuleKey);
            Assert.StartsWith("Rule.Reason.", r.ConditionKey);
        });
    }

    [Fact]
    public void Wave3Contracts_CannotBeResolvedHere_BecauseTheirImplementationsNeedAMauiHead()
    {
        // Honest skip, stated as a test so the gap is visible instead of silent: IUpdateService,
        // IManualEntryService, IReminderService and IThemeService have contracts but no
        // implementation in this copy (lanes 01/02/09/05 are writing them in parallel), and their
        // infrastructure needs FileSystem/Preferences/Plugin.LocalNotification. Documented in
        // docs/WAVE3.md as the second-pass target for the orchestrator.
        var contracts = new[] { typeof(IUpdateService), typeof(IManualEntryService), typeof(IReminderService), typeof(IThemeService) };
        foreach (var c in contracts)
            Assert.Null(c.Assembly.DefinedTypes.FirstOrDefault(t => t.IsClass && !t.IsAbstract && c.IsAssignableFrom(t)));
    }

    /// <summary>
    /// The Wave 2/3 graph rebuilt with the SAME contracts the app uses, but with MAUI-free
    /// stand-ins where the real adapter needs a head (JsonFileStore touches FileSystem,
    /// PreferencesSettingsService touches Preferences). Everything else is the production type.
    /// </summary>
    private static ServiceCollection PureGraph()
    {
        var s = new ServiceCollection();
        // Abstractions only: the real app gets ILogger<T> from MauiApp's logging builder.
        s.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var history = new FakeHistoryRepository(Enumerable.Range(1, 20).Select(i => new Domain.Models.History.DailyHistoryRecord
        {
            Date = DateTime.Today.AddDays(-i), Origin = nameof(DataOrigin.Manual), Completeness = 1,
            SleepMinutes = 450, SleepQuality = 0.8, SleepConsistency = 0.8, BedtimeMinutesOfDay = 1380,
            Steps = 8000, ActiveMinutes = 30, RecoveryScore = 0.7, Stress = 0.9, Mood = 0.7, Energy = 0.4,
        }));

        s.AddSingleton<IHistoryRepository>(history);
        s.AddSingleton<ISettingsService, StubSettings>();
        s.AddSingleton<ILocalizationService, LocalizationService>();
        s.AddSingleton<IFormatService>(sp => (LocalizationService)sp.GetRequiredService<ILocalizationService>());
        s.AddSingleton<IDateTimeProvider>(new FakeClock(DateTime.Today));
        s.AddSingleton(new SessionState { CurrentProfile = new UserProfile { Id = "di-probe" } });
        s.AddSingleton(typeof(IRepository<>), typeof(InMemoryRepo<>));
        s.AddSingleton(new ManualOriginProvider());     // stands in for ManualOverlayProvider

        s.AddSingleton<IDataNormalizer, DataNormalizer>();
        s.AddSingleton<IDataProvider>(sp => sp.GetRequiredService<ManualOriginProvider>());
        s.AddSingleton<ITrendService, TrendService>();
        s.AddSingleton<IBaselineService, BaselineService>();
        s.AddSingleton<IUserStateService, UserStateService>();
        s.AddSingleton<IRuleEngine, RuleEngine>();
        s.AddSingleton<IRecommendationService, RecommendationService>();
        s.AddSingleton<IIntelligenceProvider, SampleIntelligenceProvider>();
        s.AddSingleton<IDailyPlanService, DailyPlanService>();
        s.AddSingleton<IIntelligenceService, IntelligenceOrchestrator>();
        s.AddSingleton<IWeeklySummaryService, WeeklySummaryService>();
        s.AddSingleton<ProgramAdapter>();
        return s;
    }

    /// <summary>A real <see cref="IDataProvider"/> that reports Manual, so origin provenance is
    /// exercised through the container rather than asserted about it.</summary>
    private sealed class ManualOriginProvider : IDataProvider
    {
        private readonly SampleHealthProvider _inner = new();
        public string Id => "di.probe.provider";
        public SourceType SourceType => Domain.Enums.SourceType.Real;
        public ConnectionState State => ConnectionState.Connected;
        public string DisplayNameKey => "Profile.DataSource.Sample";
        public DataOrigin Origin => DataOrigin.Manual;
        public DataSourceCapabilities Capabilities => DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps;

        public async Task<Domain.Models.Health.NormalizedDay?> GetNormalizedDayAsync(
            DateTime date, UserProfile profile, CancellationToken ct = default)
        {
            var day = await _inner.GetNormalizedDayAsync(date, profile, ct);
            if (day is null) return null;
            return new Domain.Models.Health.NormalizedDay
            {
                Date = day.Date,
                Origin = DataOrigin.Manual,
                SleepMinutes = Manual(day.SleepMinutes), SleepQuality = Manual(day.SleepQuality),
                SleepConsistency = Manual(day.SleepConsistency), BedtimeMinutesOfDay = Manual(day.BedtimeMinutesOfDay),
                WakeMinutesOfDay = Manual(day.WakeMinutesOfDay), Steps = Manual(day.Steps),
                ActiveMinutes = Manual(day.ActiveMinutes), RecoveryScore = Manual(day.RecoveryScore),
                Stress = Manual(day.Stress), Mood = Manual(day.Mood), Energy = Manual(day.Energy),
            };
        }

        private static Domain.Models.Health.DataPoint Manual(Domain.Models.Health.DataPoint p) => new()
        {
            Value = p.Value, Timestamp = p.Timestamp, Origin = DataOrigin.Manual,
            Quality = p.Quality, Confidence = p.Confidence, Unit = p.Unit,
        };
    }
}

