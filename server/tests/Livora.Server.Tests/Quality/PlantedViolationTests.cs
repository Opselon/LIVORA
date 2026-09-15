using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Livora.Server.Tests.Quality;

// NOTE (lane-collision note for the lead, 14 Sep): the P1-F worktree had a concurrent writer early
// in this session; files in Quality/ from before commit 032dda0 were that writer's in-flight copies
// of the same brief (kept, renamed _quarantine_foreign/ by the collision watcher, then committed).
// This file + TripwireScanner.cs + TripwireFixtures/ are the P1-F lane's OWN word-level tripwires
// with ON-DISK planted fixtures, exactly as the brief demands ("fail on a planted violation you
// commit as a fixture in your own folder"). They complement SourceHonestyTests (inline samples,
// server/src only) — both lenses must agree green before the release gate says green.

/// <summary>
/// PURPOSE: proof-of-fire for the §7 word-level laws. A tripwire nobody has seen FIRE is a hope,
///          not a gate. Violations are committed as fixture FILES under Quality/TripwireFixtures/
///          (extension .cs.fix — the test project does not compile them; the scanner reads them as
///          text, line-for-line, the way it reads lane code) and the analyzer must catch every
///          planted one and NOTHING else. Both directions: "matches nothing" and "matches
///          everything" are the two ways a green tripwire lies.
/// OWNER: Agent 16 (P1-F QA lane).
/// TESTS: T1 Ok-without-probe; T2 connected/verified/paid without a state machine; T3 unlabelled
///        mock; T4 English-only key manifests (missing FA twin / placeholder drift / no Persian
///        script) + the shipped wave3b/wave3c manifests certified; T5 raw token/health value in a
///        log call (interpolated hole + structured argument) with presence/hash/length twins green;
///        and the whole-tree scan of all four code rules.
/// INVARIANTS:
///   - fixtures are inert text outside ProductionRoots — they cannot pollute the gate they calibrate
///   - a missing fixture fails loudly (absent violation = silently disarmed rule)
///   - the whole-tree scan reports EVERY hit, not the first: file:line + the tripped text (Rule A)
///   - allowlist entries are asserted live against the current tree: an entry that hides nothing
///     must be deleted, or the allowlist rots into a hide-list
/// </summary>
public sealed class PlantedViolationTests(ITestOutputHelper output)
{
    private const string FixtureRelDir = "server/tests/Livora.Server.Tests/Quality/TripwireFixtures";
    private static readonly string FixtureDir = RepoPaths.Combine(FixtureRelDir);

    private static (string Path, ScannedFile File, string Raw) Load(string name)
    {
        var path = Path.Combine(FixtureDir, name);
        Assert.True(File.Exists(path),
            $"planted fixture missing: {name} — an absent violation silently disarms its rule");
        var raw = File.ReadAllText(path);
        return ($"{FixtureRelDir}/{name}", SourceTokenizer.Scan(name, raw), raw);
    }

    private static IReadOnlyList<TripwireScanner.Hit> OfRule(
        string rulePrefix, IReadOnlyList<TripwireScanner.Hit> hits) =>
        hits.Where(h => h.Rule.StartsWith(rulePrefix, StringComparison.Ordinal)).ToList();

    // ---------------------------------------------------------------- whole-tree gate

    /// <summary>Documented exceptions (path suffix, rule, reason). Currently EMPTY by construction:
    /// a lane that lands a violation adds a RED here, never a silent pass. If a future entry is
    /// added it MUST carry a reason and it MUST be live (asserted below).</summary>
    public static readonly (string PathSuffix, string Rule, string Reason)[] AllowList = [];

    [Fact]
    public void The_real_tree_is_clean_under_every_word_level_rule()
    {
        var units = TripwireScanner.CollectProductionSources();
        var hits = TripwireScanner.ScanSources(units)
            .Where(h => !h.Path.Contains(FixtureRelDir, StringComparison.Ordinal)
                        && !h.Path.Contains("/Quality/", StringComparison.Ordinal))
            .ToList();
        var unexpected = hits.Where(h => !AllowList.Any(a =>
            a.Rule == h.Rule && h.Path.Replace('\\', '/').EndsWith(a.PathSuffix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(unexpected.Count == 0,
            "CONTRACT-P1 §7 HONESTY TRIPWIRES FIRED — gate RED:\n  " +
            string.Join("\n  ", unexpected.Select(h => $"{h.Rule}  {h.Path}:{h.Line}  {h.Text}")));
    }

    [Fact]
    public void Allow_list_entries_are_live_or_rotten()
    {
        // Every entry must still correspond to a RAW hit in the tree; an entry that no longer hides
        // anything is deleted (else allowlists silently accrete into a hide-list nobody re-reads).
        if (AllowList.Length == 0) return; // vacuous TODAY — and that is the honest state
        var hits = TripwireScanner.ScanSources(TripwireScanner.CollectProductionSources());
        foreach (var (suffix, rule, _) in AllowList)
            Assert.Contains(hits, h => h.Rule == rule
                && h.Path.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- T1: Ok without a probe

    [Fact]
    public void T1_fires_on_the_planted_Ok_report_that_probes_nothing()
    {
        var hits = OfRule("T1/ok-without-probe",
            TripwireScanner.OkWithoutProbe([Load("T1_OkWithoutProbe_Bad.cs.fix")]));
        var hit = Assert.Single(hits);
        Assert.Contains("Report", hit.Text, StringComparison.Ordinal);
        Assert.True(hit.Line > 1);
    }

    [Fact]
    public void T1_passes_the_probe_backed_twin() =>
        Assert.Empty(OfRule("T1/ok-without-probe",
            TripwireScanner.OkWithoutProbe([Load("T1_OkWithProbe_Good.cs.fix")])));

    // ---------------------------------------------------------------- T2: claim words

    [Fact]
    public void T2_fires_on_every_planted_connected_verified_paid_claim()
    {
        var hits = OfRule("T2/claim-word-no-state-machine",
            TripwireScanner.ClaimWordWithoutStateMachine([Load("T2_ClaimWords_Bad.cs.fix")]));
        Assert.True(hits.Count >= 3,
            $"expected connected+verified+paid to each fire, got {hits.Count}: " +
            string.Join(" | ", hits.Select(h => h.Line)));
        Assert.All(["connected", "verified", "paid"], word =>
            Assert.Contains(hits, h => Regex.IsMatch(h.Text, word, RegexOptions.IgnoreCase)));
    }

    [Fact]
    public void T2_accepts_negations_key_shapes_and_state_backed_claims()
    {
        Assert.Empty(OfRule("T2/claim-word-no-state-machine",
            TripwireScanner.ClaimWordWithoutStateMachine([Load("T2_ClaimWords_Honest.cs.fix")])));
        Assert.Empty(OfRule("T2/claim-word-no-state-machine",
            TripwireScanner.ClaimWordWithoutStateMachine([Load("T2_ClaimWordsWithState_Good.cs.fix")])));
    }

    // ---------------------------------------------------------------- T3: unlabelled mock

    [Fact]
    public void T3_fires_on_a_double_that_hides_its_nature()
    {
        var hits = OfRule("T3/unlabelled-mock",
            TripwireScanner.UnlabelledMock([Load("T3_UnlabelledMock_Bad.cs.fix")]));
        var hit = Assert.Single(hits);
        Assert.Contains("QuietTestStub", hit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void T3_passes_a_double_that_says_it_is_a_mock() =>
        Assert.Empty(OfRule("T3/unlabelled-mock",
            TripwireScanner.UnlabelledMock([Load("T3_LabelledMock_Good.cs.fix")])));

    // ---------------------------------------------------------------- T4: English-only UI keys

    [Fact]
    public void T4_fires_on_each_planted_bilingual_defect()
    {
        var hits = TripwireScanner.ScanKeyManifestDirectory(Path.Combine(FixtureDir, "keys-bad"));
        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("T4/english-only-keys", h.Rule));
        Assert.Contains(hits, h => h.Text.Contains("no FA counterpart", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Text.Contains("placeholder drift", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Text.Contains("no Persian script", StringComparison.Ordinal));
        Assert.Contains(hits, h => h.Text.Contains("no .fa twin manifest", StringComparison.Ordinal));
        // the honest twin pair in the sibling folder must add nothing (checked by the next test)
        Assert.Empty(TripwireScanner.ScanKeyManifestDirectory(Path.Combine(FixtureDir, "keys-good")));
    }

    [Fact]
    public void T4_certifies_the_shipped_wave_key_manifests()
    {
        // The law applies to what already shipped: every lane manifest must be EN+FA complete.
        // Asserted non-vacuous: the scan must SEE manifests or the test fails.
        var dirs = new[] { "wave3b-keys", "wave3c-keys", "wave4-keys" }
            .Select(d => RepoPaths.Combine(d)).Where(Directory.Exists).ToList();
        Assert.True(dirs.Count >= 2, "expected the shipped key-manifest directories to exist");
        int manifests = 0;
        var malformed = new List<string>();
        foreach (var d in dirs)
        {
            var files = Directory.GetFiles(d, "*.en.keys.xml");
            manifests += files.Length;
            var hits = TripwireScanner.ScanKeyManifestDirectory(d, LatinOnlyAllowList);
            malformed.AddRange(TripwireScanner.ManifestCommentDefects(d));
            Assert.True(hits.Count == 0,
                "shipped key manifests trip the bilingual rule:\n  " +
                string.Join("\n  ", hits.Select(h => $"{h.Path}: {h.Text}")));
        }
        Assert.True(manifests >= 6, $"expected the shipped waves' manifests, saw {manifests}");
        // DISCLOSED, not hidden: the P1-D manifests carry `--` inside XML comments (invalid XML per
        // W3C §2.5; the strict parser throws on them). The bilingual law above reads their real
        // <data>/<value> bytes through the scanner's documented comment-only normalisation, so the
        // certification stands — but the malformation is a FACT reported here and filed as a
        // request line (docs/quality/wave4/requests/r3.md) for P1-D to repair in its own files.
        output.WriteLine("T4 MANIFEST HEALTH: " + (malformed.Count == 0
            ? "all shipped manifests parse strictly"
            : "MALFORMED (invalid XML comments, certified only via documented normalisation — " +
              "request filed): " + string.Join(", ", malformed)));
    }


    /// <summary>FA values legitimately in Latin script (product names — the same shapes the client
    /// resx suite allowlists in Wave3ResxIntegrityTests.LatinOnlyAllowList, kept in sync).</summary>
    private static readonly IReadOnlySet<string> LatinOnlyAllowList = new HashSet<string>(StringComparer.Ordinal)
    {
        "Update.Platform.MacOS",  // value is "macOS": Latin by definition
        "Update.Platform.iOS",    // value is "iOS"
        "Ai.Body.Provider",       // pure "{0}" passthrough (placeholder present, listed for safety)
        "DataStudio.Path.Placeholder", // a Windows path is Latin by definition
    };

    // ---------------------------------------------------------------- T5: secret/health in log calls

    [Fact]
    public void T5_fires_on_planted_raw_token_and_health_values_in_log_calls()
    {
        var hits = OfRule("T5/secret-or-health-in-log",
            TripwireScanner.SecretOrHealthInLogCall([Load("T5_SecretsInLog_Bad.cs.fix")]));
        // the fixture plants four shapes: structured arg (refreshToken), interpolated hole
        // (accessToken), raw-health member (row.SleepMinutesRaw), password member access.
        Assert.True(hits.Count >= 4,
            $"expected >=4 planted leaks to fire, got {hits.Count}:\n  " +
            string.Join("\n  ", hits.Select(h => $"{h.Line} {h.Text}")));
    }

    [Fact]
    public void T5_accepts_presence_flags_lengths_and_hashes() =>
        Assert.Empty(OfRule("T5/secret-or-health-in-log",
            TripwireScanner.SecretOrHealthInLogCall([Load("T5_PresenceLengthHash_Good.cs.fix")])));
}
