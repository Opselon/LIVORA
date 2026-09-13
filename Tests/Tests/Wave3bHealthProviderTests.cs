using System.Xml.Linq;
using LIVORA.Application.Abstractions;
using LIVORA.Application.HealthData.Wave3bHealth;
using LIVORA.Domain.Enums;
using LIVORA.Domain.Models;
using LIVORA.Domain.Models.Health;
using LIVORA.Infrastructure.HealthProviders;

namespace LIVORA.Tests.Tests;

/// <summary>
/// Wave 3b (lane 02) — the health-integration foundation. The contract under test is mostly
/// negative, and that is the point: an integration that does not exist must be unable to pretend
/// otherwise. Supporting suites add the positive paths through the fake bridge: capability
/// disclosure, the permission budget, and row -> NormalizedDay mapping with provenance.
/// </summary>
public class Wave3bHealthProviderTests
{
    private static readonly DateTime ImportedAt = new(2026, 9, 13, 9, 30, 0);
    private static readonly DateTime Day = new(2026, 9, 11);
    private static readonly UserProfile Profile = new() { Id = "lane02" };

    private static FakeHealthPlatformBridge ReadyBridge() =>
        new(BridgeAvailability.Ready, PermissionState.Granted, PermissionState.Granted);

    // 1. Unsupported-state honesty

    [Fact]
    public void Provider_WithNoBridgeAtAll_IsDisconnected_AndAdvertisesNothing()
    {
        // The composition root's Android-less default: a provider built with no bridge.
        var provider = new HealthConnectProvider();

        Assert.Equal(ConnectionState.Disconnected, provider.State);
        Assert.Equal(DataSourceCapabilities.None, provider.Capabilities);
        Assert.False(provider.Permissions.MayRead);
    }

    [Fact]
    public async Task Provider_OnAnUnsupportedHead_ReturnsNullDay_NeverFabricatedValues()
    {
        var provider = new HealthConnectProvider(
            new UnsupportedHealthPlatformBridge(BridgeErrorCategory.PlatformUnsupported),
            clock: new FakeClock(Day));

        var day = await provider.GetNormalizedDayAsync(Day, Profile);

        // null (not an empty NormalizedDay, not zeros): the pipeline's honest "no data" signal.
        Assert.Null(day);
        Assert.Null(provider.LastMapping);
        Assert.Equal(ConnectionState.Disconnected, provider.State);
        Assert.Equal(DataSourceCapabilities.None, provider.Capabilities);
    }

    [Fact]
    public async Task UnsupportedBridge_CannotBeTrickedIntoReading_AndNeverPrompts()
    {
        var bridge = new UnsupportedHealthPlatformBridge(BridgeErrorCategory.ApiNotBundled, "api-not-bundled");
        var probe = bridge.Probe();

        Assert.Equal(BridgeAvailability.Unavailable, probe.Availability);
        Assert.Equal(BridgeErrorCategory.ApiNotBundled, probe.ErrorCategory);
        Assert.False(probe.PlatformPresent);
        Assert.False(probe.Readable);
        Assert.Equal(PermissionState.NotDetermined, bridge.QueryPermission());

        // Read attempts stay empty even with an absurd window: no code path here returns a number.
        Assert.Empty(await bridge.ReadRowsAsync(Day, Day.AddDays(365)));

        // Refusing to prompt for an unusable integration is the documented trust-debt rule.
        Assert.Equal(PermissionState.NotDetermined, await bridge.RequestPermissionAsync());
    }

    [Fact]
    public async Task Provider_WithRowsButNoGrant_ReadsNothing()
    {
        // Ready platform, scripted rows, but the permission machine has not been granted:
        // the read gate is (Ready AND Granted), so a single missing half is enough to stop data.
        var bridge = new FakeHealthPlatformBridge(BridgeAvailability.Ready, PermissionState.NotDetermined)
            .WithDailyRows(Day, 3);
        var provider = new HealthConnectProvider(bridge, clock: new FakeClock(Day));

        Assert.Null(await provider.GetNormalizedDayAsync(Day, Profile));
        Assert.Equal(0, bridge.ReadAttemptCount);          // gate closed before the platform call
        Assert.Equal(PermissionState.NotDetermined, provider.Permissions.State);
    }

    [Fact]
    public async Task Provider_WithGrantButNoReadyPlatform_StillReadsNothing()
    {
        var bridge = new FakeHealthPlatformBridge(BridgeAvailability.NeedsPermission, PermissionState.Granted)
            .WithDailyRows(Day, 3);
        var provider = new HealthConnectProvider(bridge, clock: new FakeClock(Day));

        Assert.Null(await provider.GetNormalizedDayAsync(Day, Profile));
        Assert.Equal(ConnectionState.Disconnected, provider.State);
    }

    [Fact]
    public void Gate_TransportFailureIsError_NotSilentDisconnected()
    {
        // A broken integration must not read like an absent-but-fine one: Error is a distinct state.
        var probe = new BridgeProbeResult(
            BridgeAvailability.Unavailable, BridgeErrorCategory.TransportFailure, "no-android-context");
        Assert.Equal(ConnectionState.Error, HealthConnectionGate.ConnectionState(probe, PermissionState.NotDetermined));
        Assert.Equal(DataSourceCapabilities.None, HealthConnectionGate.Capabilities(probe));
        Assert.False(HealthConnectionGate.AllowsRead(probe, PermissionState.Granted));
    }

    [Fact]
    public void Gate_ReadyButUnpermitted_IsDisconnected_AndNeverReadable()
    {
        var probe = new BridgeProbeResult(BridgeAvailability.Ready);
        Assert.Equal(ConnectionState.Disconnected, HealthConnectionGate.ConnectionState(probe, PermissionState.Denied));
        Assert.False(HealthConnectionGate.AllowsRead(probe, PermissionState.Denied));
        Assert.False(HealthConnectionGate.AllowsRead(probe, PermissionState.NotDetermined));
        Assert.True(HealthConnectionGate.AllowsRead(probe, PermissionState.Granted));
        Assert.Equal(ConnectionState.Connected, HealthConnectionGate.ConnectionState(probe, PermissionState.Granted));
    }

    [Fact]
    public async Task AndroidShell_IsDeclaredPending_AndReportsApiNotBundled()
    {
        // The lane's honest headline in testable form: the record client is NOT compiled in this
        // wave (no new NuGet), so the Android bridge may report the platform but may not claim a
        // readable feed. If someone bundles the client and forgets to flip this, the test fires.
        Assert.True(Wave3bProbeContract.ReadsPending);
        Assert.Equal(BridgeErrorCategory.ApiNotBundled, Wave3bProbeContract.PendingCategory);
        Assert.False(HealthConnectAndroidBridge.ApiBundled);
        Assert.NotEmpty(HealthConnectAndroidBridge.RecordsDiagnosticsTag);

        // Reads must be empty on the shell, so the mapper can only ever produce "no day".
        var bridge = new HealthConnectAndroidBridge();
        Assert.Empty(await bridge.ReadRowsAsync(Day, Day.AddDays(7)));

        // On the Windows head the shell's probe reports platform-unsupported (the #if ANDROID
        // branch is a compile-time choice; this copy is built for net10.0).
        var probe = bridge.Probe();
        Assert.Equal(BridgeAvailability.Unavailable, probe.Availability);
        Assert.True(probe.ErrorCategory is BridgeErrorCategory.PlatformUnsupported or BridgeErrorCategory.ApiNotBundled);
        Assert.Equal(DataOrigin.HealthConnect, bridge.RowOrigin);   // real records would be Health Connect
    }

    [Fact]
    public void Provider_KeepsTheManualPipelineIdentitySeparate()
    {
        // Lane 02 must not disturb the Wave 3 winner: HealthConnectProvider is a Real source that
        // can be Disconnected, while the pipeline's IDataProvider (ManualOverlayProvider) reports
        // Mock. Asserting the split here is what makes an accidental DI swap visible.
        var provider = new HealthConnectProvider();
        Assert.Equal(SourceType.Real, provider.SourceType);
        Assert.NotEqual(ConnectionState.Mock, provider.State);
        Assert.StartsWith("livora.healthconnect", provider.Id, StringComparison.Ordinal);
        Assert.Equal("Health.Provider.HealthConnect", provider.DisplayNameKey);
    }

    // 2. Capability discovery — keys + args, never prose

    [Fact]
    public void CapabilityMap_EveryConceptHasACapabilityBitAMetricKeyAndAnExistingLabelKey()
    {
        var pair = Wave3Harness.ReadBoth();
        foreach (var concept in HealthCapabilityMap.SupportedConcepts)
        {
            Assert.NotEqual(DataSourceCapabilities.None, HealthCapabilityMap.CapabilityFor(concept));
            Assert.DoesNotContain(' ', HealthCapabilityMap.ConceptTag(concept));
            Assert.Contains('.', HealthCapabilityMap.MetricKey(concept));

            // The label reuses an existing Health.* key — a new key per concept would drift FA.
            var label = HealthCapabilityMap.LabelKey(concept);
            Assert.DoesNotContain(' ', label);
            if (pair is not null)
            {
                Assert.True(pair.En.ContainsKey(label), $"{label} (label for {concept}) missing in EN");
                Assert.True(pair.Fa.ContainsKey(label), $"{label} (label for {concept}) missing in FA");
            }
        }
    }

    [Fact]
    public void CapabilityMap_UnavailableProbe_DisablesEveryConcept()
    {
        var rows = new Dictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>>
        {
            [HealthMetricConcept.Steps] = new[] { new HealthRawRow(Day, 9000, "rec-1", HealthMetricConcept.Steps) },
        };
        var disclosures = HealthCapabilityMap.Discover(
            BridgeProbeResult.Unavailable(BridgeErrorCategory.ApiNotBundled),
            HealthCapabilityMap.HealthConnectAdvertised, PermissionState.Granted, rows);

        Assert.Equal(3, disclosures.Count);
        Assert.All(disclosures, d => Assert.Equal(CapabilityStatus.Unavailable, d.Status));
    }

    [Fact]
    public void CapabilityMap_PermissionGap_AndRecordGap_AreDistinguished()
    {
        var granted = new Dictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>>
        {
            [HealthMetricConcept.SleepMinutes] = new[] { new HealthRawRow(Day, 450, "rec-s", HealthMetricConcept.SleepMinutes) },
        };
        var probe = BridgeProbeResult.Ready();

        var withPermission = HealthCapabilityMap.Discover(
            probe, HealthCapabilityMap.HealthConnectAdvertised, PermissionState.Granted, granted);
        Assert.Equal(CapabilityStatus.Available,
            Assert.Single(withPermission, d => d.Concept == HealthMetricConcept.SleepMinutes).Status);
        Assert.Equal(CapabilityStatus.NoData,
            Assert.Single(withPermission, d => d.Concept == HealthMetricConcept.Steps).Status);

        var withoutPermission = HealthCapabilityMap.Discover(
            probe, HealthCapabilityMap.HealthConnectAdvertised, PermissionState.NotDetermined, granted);
        Assert.All(withoutPermission, d => Assert.Equal(CapabilityStatus.NeedsPermission, d.Status));
    }

    [Fact]
    public void CapabilityMap_UnadvertisedConcept_IsReportedUnsupportedNotBroken()
    {
        // The provider must not claim a concept the integration cannot serve — Recovery/HRV are
        // contract slots, and a slot must read "unsupported", never "your data is missing".
        var rows = new Dictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>>
        {
            [HealthMetricConcept.Steps] = new[] { new HealthRawRow(Day, 4321, "rec-steps", HealthMetricConcept.Steps) },
        };
        var disclosures = HealthCapabilityMap.Discover(
            BridgeProbeResult.Ready(), DataSourceCapabilities.Steps, PermissionState.Granted, rows);
        Assert.Equal(CapabilityStatus.Available,
            Assert.Single(disclosures, d => d.Concept == HealthMetricConcept.Steps).Status);
        Assert.Equal(CapabilityStatus.Unsupported,
            Assert.Single(disclosures, d => d.Concept == HealthMetricConcept.SleepMinutes).Status);
        Assert.Equal(CapabilityStatus.Unsupported,
            Assert.Single(disclosures, d => d.Concept == HealthMetricConcept.ActiveMinutes).Status);
    }

    [Fact]
    public void CapabilityMap_AvailableVerdictAlwaysCarriesItsRecordIds()
    {
        var rows = new Dictionary<HealthMetricConcept, IReadOnlyList<HealthRawRow>>
        {
            [HealthMetricConcept.ActiveMinutes] = new[]
            {
                new HealthRawRow(Day, 20, "rec-a1", HealthMetricConcept.ActiveMinutes),
                new HealthRawRow(Day, 15, "rec-a2", HealthMetricConcept.ActiveMinutes),
            },
        };
        var d = Assert.Single(HealthCapabilityMap.Discover(
            BridgeProbeResult.Ready(), HealthCapabilityMap.HealthConnectAdvertised,
            PermissionState.Granted, rows), x => x.Concept == HealthMetricConcept.ActiveMinutes);

        Assert.Equal(CapabilityStatus.Available, d.Status);
        Assert.Equal(new[] { "rec-a1", "rec-a2" }, d.SourceRecordIds.OrderBy(s => s, StringComparer.Ordinal));
        Assert.Empty(HealthCapabilityMap.Diagnostics(
            HealthCapabilityMap.Discover(BridgeProbeResult.Ready(),
                HealthCapabilityMap.HealthConnectAdvertised, PermissionState.Granted, rows)));
    }

    [Fact]
    public void CapabilityMap_StatusKeysAreDottedAndAgreeWithTheirArgCounts()
    {
        // A {0} in a template that gets no arg (or vice versa) is the bug
        // Wave3ResxIntegrityTests hunts across the two resx files; this pins the same rule on the
        // emitting side, per status.
        foreach (CapabilityStatus status in Enum.GetValues<CapabilityStatus>())
        {
            var key = HealthCapabilityMap.StatusKeyFor(status);
            Assert.DoesNotContain(' ', key);
            Assert.Contains('.', key);
            var args = HealthCapabilityMap.StatusArgsFor(status, HealthMetricConcept.Steps);
            Assert.Equal(HealthCapabilityMap.StatusArgCount(status), args.Count);
            if (args.Count == 1)
            {
                // The single arg is a resolvable LABEL KEY, not a raw machine tag or prose.
                var arg = Assert.IsType<string>(args[0]);
                Assert.DoesNotContain(' ', arg);
                Assert.Contains('.', arg);
            }
        }
    }

    [Fact]
    public void CapabilityMap_ProbeStatusLines_NeverCarryOrphanArgs()
    {
        foreach (BridgeAvailability availability in Enum.GetValues<BridgeAvailability>())
            foreach (BridgeErrorCategory category in Enum.GetValues<BridgeErrorCategory>())
            {
                var probe = new BridgeProbeResult(availability, category, "diagnostic-tag");
                var key = probe.StatusKey;
                Assert.DoesNotContain(' ', key);
                Assert.Empty(probe.StatusArgs);           // provider-level lines are 0-arg
                Assert.DoesNotContain(' ', HealthCapabilityMap.ProbeDiagnosticTag(probe));

                // The reused Wave 2 line must be a key that actually exists in both languages.
                if (key == "Profile.Status.NotConnected")
                {
                    var pair = Wave3Harness.ReadBoth();
                    if (pair is not null)
                    {
                        Assert.True(pair.En.ContainsKey(key));
                        Assert.True(pair.Fa.ContainsKey(key));
                    }
                }
            }
    }

    // 3. Permission flow — state machine + max-asks-per-session

    [Fact]
    public async Task PermissionFlow_UnavailablePlatform_IsUnavailableNotDenied_AndNeverPrompts()
    {
        var bridge = new FakeHealthPlatformBridge(BridgeAvailability.Unavailable)
        {
            RequestOutcome = PermissionState.Granted,     // would grant if asked — the point is it must not ask
        };
        var flow = new PermissionFlow(bridge);

        var attempt = await flow.EnsureAsync();

        Assert.Equal(PermissionPhase.Unavailable, attempt.Phase);
        Assert.Equal(PermissionState.UnavailableInPhase, flow.State);   // maps to the existing enum
        Assert.Equal(0, bridge.PermissionRequestCount);
        Assert.False(attempt.CanRead);
        Assert.Equal(PermissionFlow.ReasonPlatformUnavailable, attempt.BlockedReason);
        Assert.DoesNotContain(' ', flow.StatusKey);
    }

    [Fact]
    public async Task PermissionFlow_ExistingGrant_IsFoundByQuery_WithoutCostingADialog()
    {
        var bridge = new FakeHealthPlatformBridge(BridgeAvailability.Ready, PermissionState.Granted);
        var flow = new PermissionFlow(bridge);

        var attempt = await flow.EnsureAsync();

        Assert.Equal(PermissionPhase.Granted, attempt.Phase);
        Assert.False(attempt.PromptedPlatform);
        Assert.Equal(0, bridge.PermissionRequestCount);
        Assert.True(flow.MayRead);
        Assert.True(attempt.CanRead);
    }

    [Fact]
    public async Task PermissionFlow_DenialIsTerminalForTheSession_AndKeepsThePlatformUntouched()
    {
        var bridge = new FakeHealthPlatformBridge(
            BridgeAvailability.Ready, PermissionState.NotDetermined, PermissionState.Denied);
        var flow = new PermissionFlow(bridge);

        var first = await flow.EnsureAsync();
        Assert.Equal(PermissionPhase.Denied, first.Phase);
        Assert.True(first.PromptedPlatform);
        Assert.Equal(1, bridge.PermissionRequestCount);

        // Re-render storm: the page asks again ten times.
        for (var i = 0; i < 10; i++)
        {
            var again = await flow.EnsureAsync();
            Assert.Equal(PermissionPhase.Denied, again.Phase);
            Assert.False(again.PromptedPlatform);
            Assert.False(again.CanRead);
        }
        Assert.Equal(1, bridge.PermissionRequestCount);
        Assert.Equal(PermissionState.Denied, flow.State);
    }

    [Fact]
    public async Task PermissionFlow_DefaultBudgetIsTwoAsks_ThenStopsPromptingForever()
    {
        // The prompt-loop guard, pinned: max 2 platform prompts per session, default.
        var bridge = new FakeHealthPlatformBridge(
            BridgeAvailability.Ready, PermissionState.NotDetermined, PermissionState.NotDetermined);
        var flow = new PermissionFlow(bridge);
        Assert.Equal(2, PermissionFlow.DefaultMaxAsksPerSession);
        Assert.Equal(2, flow.MaxAsksPerSession);

        for (var i = 0; i < 12; i++) await flow.EnsureAsync();

        Assert.Equal(2, bridge.PermissionRequestCount);
        Assert.Equal(2, flow.AskCount);
        Assert.False(flow.CanAsk);
        Assert.False(flow.MayRead);
        Assert.Equal(PermissionFlow.ReasonBudgetExhausted, flow.LastBlockedReason);
        // Budget exhaustion is NOT a denial the user never made: the phase stays Requested.
        Assert.Equal(PermissionPhase.Requested, flow.Phase);
        Assert.Equal(PermissionState.NotDetermined, flow.State);
    }

    [Fact]
    public async Task PermissionFlow_BudgetIsConfigurable_AndZeroMeansNeverPrompt()
    {
        var bridge = new FakeHealthPlatformBridge(
            BridgeAvailability.Ready, PermissionState.NotDetermined, PermissionState.Granted);
        var noAsks = new PermissionFlow(bridge, maxAsksPerSession: 0);
        var attempt = await noAsks.EnsureAsync();

        Assert.Equal(0, bridge.PermissionRequestCount);
        Assert.Equal(PermissionFlow.ReasonBudgetExhausted, attempt.BlockedReason);
        Assert.False(attempt.CanRead);

        var oneAsk = new PermissionFlow(
            new FakeHealthPlatformBridge(BridgeAvailability.Ready, PermissionState.NotDetermined, PermissionState.Granted),
            maxAsksPerSession: 1);
        var granted = await oneAsk.EnsureAsync();
        Assert.True(granted.PromptedPlatform);
        Assert.Equal(PermissionPhase.Granted, granted.Phase);
        Assert.True(oneAsk.MayRead);
    }

    [Theory]
    [InlineData(PermissionPhase.NotAsked, PermissionState.NotDetermined)]
    [InlineData(PermissionPhase.Requested, PermissionState.NotDetermined)]
    [InlineData(PermissionPhase.Granted, PermissionState.Granted)]
    [InlineData(PermissionPhase.Denied, PermissionState.Denied)]
    [InlineData(PermissionPhase.Unavailable, PermissionState.UnavailableInPhase)]
    public void PermissionFlow_MapsOntoTheExistingPermissionStateEnum(PermissionPhase phase, PermissionState expected) =>
        Assert.Equal(expected, PermissionFlow.ToState(phase));

    [Fact]
    public void PermissionFlow_RejectsANegativeBudget_BecauseThatWouldNotMeanNoAsks()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PermissionFlow(new UnsupportedHealthPlatformBridge(), maxAsksPerSession: -1));
        Assert.Throws<ArgumentNullException>(() => new PermissionFlow(null!));
    }

    // 4. Bridge rows -> NormalizedDay

    [Fact]
    public async Task FakeBridge_LabelsItsOwnRows_AsMockWithFakeRecordIds()
    {
        var bridge = ReadyBridge().WithDailyRows(Day, 1);
        var rows = await bridge.ReadRowsAsync(Day, Day.AddDays(1));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(DataOrigin.Mock, r.Origin));
        Assert.All(rows, r => Assert.StartsWith("fake-", r.SourceRecordId, StringComparison.Ordinal));
        Assert.Equal(DataOrigin.Mock, bridge.RowOrigin);
        Assert.Equal("healthconnect", bridge.ProviderId);
    }

    [Fact]
    public async Task FakeBridge_DeliversExactlyTheScriptedWindow_NoInventedDays()
    {
        var bridge = ReadyBridge().WithDailyRows(Day, 5);
        var rows = await bridge.ReadRowsAsync(Day, Day.AddDays(3));

        Assert.Equal(3 * 3, rows.Count);                       // 3 days x 3 concepts
        Assert.All(rows, r => Assert.InRange(r.Day, Day, Day.AddDays(2)));
        // Deterministic: the same window read twice is the same sequence (no RNG, no clock).
        var again = await bridge.ReadRowsAsync(Day, Day.AddDays(3));
        Assert.Equal(rows.Select(r => (r.Day, r.Concept, r.Value, r.SourceRecordId)).ToList(),
                     again.Select(r => (r.Day, r.Concept, r.Value, r.SourceRecordId)).ToList());
    }

    [Fact]
    public async Task Provider_MapsBridgeRowsOntoANormalizedDay_WithHealthConnectProvenance()
    {
        var bridge = new FakeHealthPlatformBridge(
                BridgeAvailability.Ready, PermissionState.Granted, rowOrigin: DataOrigin.HealthConnect)
            .AddRow(Day, HealthMetricConcept.SleepMinutes, 410)
            .AddRow(Day, HealthMetricConcept.SleepMinutes, 45)     // two records sum
            .AddRow(Day, HealthMetricConcept.Steps, 9200)
            .AddRow(Day, HealthMetricConcept.ActiveMinutes, 33);
        var provider = new HealthConnectProvider(bridge, clock: new FakeClock(Day));
        await provider.EnsurePermissionAsync();

        var day = await provider.GetNormalizedDayAsync(Day, Profile);

        Assert.NotNull(day);
        Assert.Equal(Day, day!.Date);
        Assert.Equal(DataOrigin.HealthConnect, day.Origin);
        Assert.Equal(DataOrigin.HealthConnect, provider.Origin);
        Assert.Equal(455, day.SleepMinutes.Value, 6);
        Assert.Equal("minutes", day.SleepMinutes.Unit);
        Assert.Equal(9200, day.Steps.Value, 6);
        Assert.Equal("steps", day.Steps.Unit);
        Assert.Equal(33, day.ActiveMinutes.Value, 6);
        Assert.Equal(ConnectionState.Connected, provider.State);

        // Measured data, not estimated: Complete + full confidence.
        Assert.Equal(DataQuality.Complete, day.Steps.Quality);
        Assert.Equal(1.0, day.Steps.Confidence);
        Assert.False(day.Steps.IsEstimated);

        // Completeness reflects 3 of the 8 counted fields — the honest 3/8, not a rounded 1.0.
        Assert.Equal(3.0 / 8.0, day.Completeness(), 6);
    }

    [Fact]
    public async Task MappedDay_UnsuppliedFieldsAreMissing_WithTheDaysOrigin_NotMockNotZero()
    {
        var bridge = new FakeHealthPlatformBridge(
                BridgeAvailability.Ready, PermissionState.Granted, rowOrigin: DataOrigin.HealthConnect)
            .AddRow(Day, HealthMetricConcept.Steps, 5000);
        var mapping = await HealthDayMapper.MapDayAsync(bridge, Day, ImportedAt);

        Assert.NotNull(mapping);
        var day = mapping!.Day;
        var missing = new[]
        {
            day.SleepMinutes, day.SleepQuality, day.SleepConsistency, day.BedtimeMinutesOfDay,
            day.WakeMinutesOfDay, day.ActiveMinutes, day.RecoveryScore, day.Stress, day.Mood, day.Energy,
        };
        Assert.All(missing, p =>
        {
            Assert.Equal(DataQuality.Missing, p.Quality);
            Assert.Equal(DataOrigin.HealthConnect, p.Origin);   // the day's origin — never Mock fallback
            Assert.True(double.IsNaN(p.Value));                 // not 0: zero is a claim about the user
            Assert.Equal(0, p.Confidence);
        });
        // Device-grade signals are absent, not present-but-empty.
        Assert.Null(day.RestingHeartRate);
        Assert.Null(day.HrvMs);
        Assert.Equal(1.0 / 8.0, day.Completeness(), 6);
    }

    [Fact]
    public async Task MappedDay_EstimatedRecord_DropsToEstimatedQualityAndConfidence()
    {
        var bridge = new FakeHealthPlatformBridge(
                BridgeAvailability.Ready, PermissionState.Granted, rowOrigin: DataOrigin.HealthConnect)
            .AddRow(new HealthRawRow(Day, 300, "rec-est", HealthMetricConcept.SleepMinutes,
                DataOrigin.HealthConnect, Estimated: true));
        var mapping = await HealthDayMapper.MapDayAsync(bridge, Day, ImportedAt);

        var point = mapping!.Day.SleepMinutes;
        Assert.Equal(DataQuality.Estimated, point.Quality);
        Assert.Equal(HealthDayMapper.EstimatedConfidence, point.Confidence);
        Assert.True(point.IsEstimated);
    }

    [Fact]
    public async Task MappedDay_CarriesPerFieldProvenanceWithTheSourceRecordId()
    {
        var bridge = new FakeHealthPlatformBridge(
                BridgeAvailability.Ready, PermissionState.Granted, rowOrigin: DataOrigin.HealthConnect)
            .AddRow(Day, HealthMetricConcept.Steps, 7777, "hc-record-42")
            .AddRow(Day, HealthMetricConcept.ActiveMinutes, 12, "hc-record-77");
        var mapping = await HealthDayMapper.MapDayAsync(bridge, Day, ImportedAt);

        Assert.NotNull(mapping);
        var rows = mapping!.RowProvenance["activity.steps"];
        var p = Assert.Single(rows);
        Assert.Equal("healthconnect", p.Source);
        Assert.Equal("hc-record-42", p.SourceRecordId);
        Assert.Equal(DataOrigin.HealthConnect, p.Origin);
        Assert.Equal(ImportedAt, p.ImportedAtUtc);

        Assert.Equal("healthconnect", mapping.DayProvenance.Source);
        Assert.Contains("hc-record-42", mapping.DayProvenance.SourceRecordId, StringComparison.Ordinal);
        Assert.Contains("hc-record-77", mapping.DayProvenance.SourceRecordId, StringComparison.Ordinal);
        Assert.Empty(mapping.Diagnostics);
    }

    [Fact]
    public void MappedDay_UntraceableRow_IsDroppedAndReported_NotSilentlyAZero()
    {
        // A row with no record id cannot be cited, so it cannot be believed: it is dropped and the
        // day degrades to "no data" instead of a number nobody can trace.
        var rows = new[]
        {
            new HealthRawRow(Day, 5000, "", HealthMetricConcept.Steps),
            new HealthRawRow(Day, double.NaN, "rec-x", HealthMetricConcept.Steps),
        };
        Assert.False(rows[0].IsTraceable);
        Assert.False(rows[1].IsTraceable);

        var mapping = HealthDayMapper.MapDay(Day, rows, "healthconnect", DataOrigin.HealthConnect, ImportedAt);
        Assert.Null(mapping);
        Assert.Equal(5000, rows[0].Value);   // the raw value survives; only the mapping refuses it
    }

    [Fact]
    public async Task FakeBridge_RowsOnlyOnSomeDays_LeaveTheOtherDaysAbsent()
    {
        var bridge = ReadyBridge().WithDailyRows(Day, 4, omitDays: new[] { Day.AddDays(2) });
        var provider = new HealthConnectProvider(
            bridge, new PermissionFlow(bridge), new FakeClock(Day));
        await provider.EnsurePermissionAsync();

        for (var i = 0; i < 4; i++)
        {
            var day = await provider.GetNormalizedDayAsync(Day.AddDays(i), Profile);
            if (i == 2)
            {
                Assert.Null(day);                 // the omitted day stays omitted
                Assert.Null(provider.LastMapping);
            }
            else
            {
                Assert.NotNull(day);
                Assert.Equal(DataOrigin.Mock, day!.Origin);      // fake bridge -> Mock, always
                Assert.All(new[] { day.SleepMinutes, day.Steps, day.ActiveMinutes },
                    p => Assert.Equal(DataOrigin.Mock, p.Origin));
            }
        }
    }

    [Fact]
    public void MapRange_ProducesOneMappingPerDay_InDateOrder()
    {
        var bridge = ReadyBridge().WithDailyRows(Day, 3);
        var mappings = HealthDayMapper.MapRange(bridge.Rows, "healthconnect", ImportedAt, DataOrigin.Mock);

        Assert.Equal(3, mappings.Count);
        Assert.Equal(new[] { Day, Day.AddDays(1), Day.AddDays(2) }, mappings.Select(m => m.Day.Date));
        Assert.All(mappings, m => Assert.Equal(m.Day.Date.AddHours(12), m.Day.SleepMinutes.Timestamp));
    }

    // Registry seam + this lane's key files

    [Fact]
    public void Registry_DescribesTheUnsupportedConnection_AsAPlaceholderRow()
    {
        var provider = new HealthConnectProvider();
        var registry = new HealthDataProviderRegistry(new[]
        {
            HealthDataProviderRegistry.For(provider, new UnsupportedHealthPlatformBridge(), provider.Permissions.State),
        });

        var row = Assert.Single(registry.Describe());
        Assert.Equal("Health.Provider.HealthConnect", row.DisplayNameKey);
        Assert.Equal(ConnectionState.Disconnected, row.State);
        Assert.Equal(DataSourceCapabilities.None, provider.Capabilities);
        Assert.True(row.IsPlaceholder);
        Assert.Null(registry.PickReadable());
        Assert.DoesNotContain(' ', row.StatusKey);
        Assert.Empty(row.StatusArgs);
    }

    [Fact]
    public void Registry_PicksAReadableConnection_WhenTheGateOpens()
    {
        var bridge = new FakeHealthPlatformBridge(
            BridgeAvailability.Ready, PermissionState.Granted, rowOrigin: DataOrigin.HealthConnect);
        var provider = new HealthConnectProvider(bridge, clock: new FakeClock(Day));
        var registry = new HealthDataProviderRegistry(new[]
        {
            HealthDataProviderRegistry.For(provider, bridge, PermissionState.Granted),
        });

        var picked = registry.PickReadable();
        Assert.NotNull(picked);
        Assert.Same(provider, picked!.Provider);
        Assert.False(Assert.Single(registry.Describe()).IsPlaceholder);
    }

    [Fact]
    public void Registry_LiveFactory_ReProbes_InsteadOfFreezingTheBootSnapshot()
    {
        // The composition root uses the Live form so a Settings row cannot go stale: the same
        // registry must report Not-installed before the bridge is installed and Ready after.
        var bridge = new FakeHealthPlatformBridge(BridgeAvailability.NotInstalled);
        var provider = new HealthConnectProvider(bridge, clock: new FakeClock(Day));
        var registry = HealthDataProviderRegistry.Live(() => new[]
        {
            HealthDataProviderRegistry.For(provider, bridge, provider.Permissions.State),
        });

        Assert.True(Assert.Single(registry.Describe()).IsPlaceholder);
        Assert.Null(registry.PickReadable());

        bridge.Availability = BridgeAvailability.Ready;
        var live = Assert.Single(registry.Describe());
        Assert.Equal(DataSourceCapabilities.Sleep | DataSourceCapabilities.Steps
                     | DataSourceCapabilities.ActiveMinutes, live.Capabilities);
        Assert.Equal("Health.Cap.Provider.Connected", live.StatusKey);
    }

    [Fact]
    public void LaneKeyFiles_HaveIdenticalKeyOrderMatchingPlaceholdersAndRealPersian()
    {
        // This lane's own version of Wave3ResxIntegrityTests, run against the two keys files before
        // the orchestrator ever appends them: a key in EN without an FA twin is an English leak in
        // an RTL UI, and a placeholder mismatch is a literal "{0}" in front of a real user.
        var root = Wave3Harness.RepoRoot;
        if (root is null) return;
        var en = ReadKeys(Path.Combine(root, "wave3b-keys", "lane02.en.keys.xml"));
        var fa = ReadKeys(Path.Combine(root, "wave3b-keys", "lane02.fa.keys.xml"));

        Assert.NotEmpty(en);
        Assert.Equal(en.Keys.ToList(), fa.Keys.ToList());
        foreach (var key in en.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(en[key]), $"{key} has no English text");
            Assert.False(string.IsNullOrWhiteSpace(fa[key]), $"{key} has no Persian text");
            Assert.True(Wave3Harness.HasPersianCodepoint(fa[key]), $"{key} is not translated into Persian");
            Assert.Equal(Wave3Harness.Placeholders(en[key]), Wave3Harness.Placeholders(fa[key]));
        }

        // Every key the capability map can emit must exist in one of the two places: this lane's
        // files (new keys) or the shipped resx (reused keys). Nothing may dangle.
        var pair = Wave3Harness.ReadBoth();
        if (pair is not null)
        {
            var emitted = HealthCapabilityMap.SupportedConcepts
                .SelectMany(_ => ((CapabilityStatus[])Enum.GetValues<CapabilityStatus>()).Select(s => HealthCapabilityMap.StatusKeyFor(s)))
                .Concat(((BridgeAvailability[])Enum.GetValues<BridgeAvailability>()).Select(a =>
                    HealthCapabilityMap.ProbeStatusKey(new BridgeProbeResult(a))))
                .Concat(((PermissionState[])Enum.GetValues<PermissionState>()).Select(HealthCapabilityMap.PermissionStatusKey))
                .Concat(HealthCapabilityMap.SupportedConcepts.Select(HealthCapabilityMap.LabelKey))
                .Append("Health.Provider.HealthConnect")
                .Append(PermissionFlow.AskLimitStatusKey)
                .Distinct(StringComparer.Ordinal);

            // The ask-limit line is 1-arg ({0} = asks used) wherever it is defined.
            foreach (var (label, dict) in new[] { ("EN", en), ("FA", fa) })
                if (dict.TryGetValue(PermissionFlow.AskLimitStatusKey, out var text))
                    Assert.Equal(new[] { 0 }, Wave3Harness.Placeholders(text));
            if (pair is not null)
                foreach (var (label, dict) in new[] { ("EN", pair.En), ("FA", pair.Fa) })
                    if (dict.TryGetValue(PermissionFlow.AskLimitStatusKey, out var text))
                        Assert.Equal(new[] { 0 }, Wave3Harness.Placeholders(text));

            foreach (var key in emitted)
                Assert.True(en.ContainsKey(key) || (pair?.En.ContainsKey(key) ?? false),
                    $"{key} is emitted by the lane but defined in neither lane02.en.keys.xml nor AppResources.resx");
        }

        // The provider's own display key must ship too. Pre-merge the guard was "NOT in resx yet"
        // (double-definition would mean another lane reused it with a different meaning). The
        // wave3b merge landed the fragment into AppResources, so post-merge the honest assertion is:
        // the key exists in resx AND carries the SAME text as the lane fragment — one key, one meaning.
        Assert.True(en.ContainsKey("Health.Provider.HealthConnect"));
        if (pair is not null && pair.En.TryGetValue("Health.Provider.HealthConnect", out var shipped))
            Assert.Equal(en["Health.Provider.HealthConnect"].Trim(), shipped.Trim());
        if (pair is not null && pair.Fa.TryGetValue("Health.Provider.HealthConnect", out var shippedFa))
            Assert.Equal(fa["Health.Provider.HealthConnect"].Trim(), shippedFa.Trim());
    }

    private static Dictionary<string, string> ReadKeys(string path)
    {
        if (!File.Exists(path)) return new Dictionary<string, string>();
        var doc = XDocument.Load(path);
        var values = new Dictionary<string, string>();
        foreach (var data in doc.Root?.Elements("data") ?? Enumerable.Empty<XElement>())
        {
            var name = data.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            values[name] = data.Element("value")?.Value ?? string.Empty;
        }
        return values;
    }
}
