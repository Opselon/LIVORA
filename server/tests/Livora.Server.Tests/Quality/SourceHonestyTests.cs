using System.Text.RegularExpressions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: the Wave 4 honesty tripwires (CONTRACT-P1 §7) as executable gates over REAL repo
///          source — not vibes, not samples. Every scan reads the whole in-scope tree; a scan that
///          silently saw zero files fails or throws (an empty scan is the classic way a gate stops
///          gating). Each tripwire ships with a proof-of-fire self-test on a synthetic offender, so
///          a broken scanner can never masquerade as a green gate.
/// OWNER: Agent 16 (QA/release gate). Lane code that trips one of these is a gate-RED event: fix
///          the code, or file a request line with a reason — the allowlists here are the only
///          sanctioned exceptions and every entry carries a documented why.
/// TESTS: (1) no blocking calls (.Result / .Wait() / GetAwaiter().GetResult()) in server/src code
///            text (client tests are out of scope by design: that file set is FROZEN for this lane
///            and legitimately blocks on tasks — see FLAKE-STORAGE-BUDGET.md);
///        (2) no secret-looking string literals in server/src (complements the CI diff scanner in
///            scripts/integration/livora_gates.py, which only sees ADDED lines of one PR diff);
///        (3) a module Report() that claims DependencyState.Ok must show probe evidence in its
///            class — with ONE explicit, documented allowlist exception (PlatformModule);
///        (4) ProblemCodes: Modules/** may not hold a string literal equal to a code value and may
///            not call Problems.Of with a non-constant code argument — ambiguous shapes are
///            ENUMERATED as violations, never skipped (fail-open is not acceptable per the brief);
///        (5) privacy: no log-call site may carry a password/token/secret/refresh VALUE (template
///            hole or structured argument) — naming a config key or measuring a length is allowed
///            via the documented narrow allowlist below;
///        (6) OpenAPI versioning gate: every served path starts with /api/v1 or is a platform
///            utility (/healthz, /openapi…) — asserted against the LIVE document via the fixture.
/// INVARIANTS:
///   - scans use <see cref="RepoPaths.ScanFile"/>: comments and string CONTENT are removed while
///     interpolation-hole code is kept, so prose can never trip these gates and code hiding inside
///     a template can never escape them (semantics pinned by SourceTokenizerSelfTests)
///   - violations report repo-relative file:line
///   - the only allowlists are the ones in this file, each with an inline reason
/// </summary>
public sealed class SourceHonestyTests
{
    private static readonly string[] ServerSrcFiles =
        [.. RepoPaths.Enumerate("server/src").Select(RepoPaths.Relative)];

    [Fact]
    public void Scope_sanity_the_tree_we_scan_is_the_tree_we_claim()
    {
        Assert.True(ServerSrcFiles.Length >= 15,
            $"expected the server/src scan scope to hold the whole backend tree, found {ServerSrcFiles.Length} files");
        var lines = ServerSrcFiles.Sum(f => RepoPaths.ScanFile(RepoPaths.Combine(f)).Lines.Count);
        Assert.True(lines > 1000,
            $"server/src scan saw only {lines} lines — scope is wrong, every gate below would pass vacuously");
    }

    // ---- (1) blocking calls ------------------------------------------------------------------------

    private static readonly Regex BlockingCall =
        new(@"\.Result\b|\.Wait\(\)|GetAwaiter\(\)\s*\.\s*GetResult\(\)", RegexOptions.Compiled);

    [Fact]
    public void Tripwire1_no_blocking_task_calls_in_server_src()
    {
        Assert.Empty(FilesMatching(ServerSrcFiles, BlockingCall));
    }

    [Fact]
    public void Tripwire1_self_test_the_analyzer_fires_on_a_synthetic_offender()
    {
        var sample = SourceTokenizer.Scan("synthetic.cs", """
            class C {
                void M(Task t, Task t2) {
                    var v = t.Result;                   // offender 1
                    t.Wait();                           // offender 2
                    var w = t.GetAwaiter().GetResult(); // offender 3
                    // t.Result in a comment is NOT code
                    Log($"inside a string {t2.Result} too"); // offender 4 hidden in an interpolation hole
                    Log("prose about .Wait() is NOT code");
                }
            }
            """);
        Assert.Equal(new[] { 3, 4, 5, 7 }, SampleMatches(sample, BlockingCall));
    }

    // ---- (2) secret-looking literals -----------------------------------------------------------------

    // The CI diff scanner's shapes, plus the value-shaped ones that survive renaming: a bare
    // `var k = "sk-…"` assignment is invisible to an `api[_-]?key[:=]` regex, so the VALUE itself
    // must match here regardless of the identifier it lands in.
    private static readonly Regex[] SecretLiteralPatterns =
    [
        new(@"^sk-[A-Za-z0-9][A-Za-z0-9._-]{16,}$", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9._~+/-]{32,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled),
        new(@"^(?:password|passwd|secret|token|apikey|api_key)\s*[:=]\s*\S{8,}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^[A-Za-z0-9+/]{40,}={0,2}$", RegexOptions.Compiled), // long base64/hex-shaped value
    ];

    [Fact]
    public void Tripwire2_no_secret_looking_string_literals_in_server_src()
    {
        var violations = new List<string>();
        foreach (var f in ServerSrcFiles)
        {
            foreach (var lit in RepoPaths.ScanFile(RepoPaths.Combine(f)).Literals)
            {
                // The honest absence shape in this repo is "" for unconfigured keys — a zero-length
                // literal is not a secret. Below 16 chars nothing here can hold a real provider key.
                if (lit.Text.Length < 16) continue;
                foreach (var pat in SecretLiteralPatterns)
                {
                    if (pat.IsMatch(lit.Text))
                    {
                        violations.Add($"{f}:{lit.Line} literal of length {lit.Text.Length} matches {pat}");
                        break;
                    }
                }
            }
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void Tripwire2_self_test_the_analyzer_fires_on_a_planted_key()
    {
        // The planted key is CONSTRUCTED at runtime, never written as a literal: a fake secret in
        // source is exactly the leak-shape the client suite's GatewayKeyTripwireTests scans the
        // WHOLE repo (including server/**) for — a QA lane that plants a literal key to test the
        // key scanner would be the repo's first false-positive offender.
        const string KeyShapedBody = "WXyZ0123456789abcdefghijkl"; // never appears quoted after "sk-"
        var planted = "sk-" + KeyShapedBody;
        var sample = SourceTokenizer.Scan("synthetic.cs",
            $"var k = \"{planted}\"; var cfg = \"\"; var name = \"platform\";");
        var hits = SecretLiteralPatterns
            .SelectMany(p => sample.Literals.Where(l => l.Text.Length >= 16 && p.IsMatch(l.Text)))
            .ToList();
        Assert.Single(hits); // exactly the planted key; the empty config value and short name pass
        Assert.Equal(planted, Assert.Single(hits).Text);
    }

    // ---- (3) honest Ok reports ------------------------------------------------------------------------

    /// <summary>Explicit allowlist — (type, reason). Design rule: a module may report
    /// <see cref="Livora.Server.Modules.DependencyState.Ok"/> for its OWN always-up in-process
    /// surface without probing something external. This is the only such module at Phase 1, and it
    /// earns the exception honestly: PlatformModule's capabilities handler runs
    /// <c>DatabaseProbe.ProbeAsync</c> per request (server/src/Livora.Server/Modules/Platform/PlatformModule.cs:33),
    /// so its Ok describes the host surface that IS up, while every external dependency it exposes
    /// is probed, never assumed. A NEW entry needs a documented always-up surface + a request line
    /// the lead can see; silent additions to this list are exactly what this tripwire exists to stop.</summary>
    private static readonly (string Type, string Reason)[] OkReportAllowlist =
    [
        ("PlatformModule", "platform surface is up iff the host is; its database state is probed live per request"),
    ];

    private static readonly Regex ClassDecl =
        new(@"\b(?:(?:public|private|internal|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)",
            RegexOptions.Compiled);
    private static readonly Regex ReportMethod =
        new(@"\bModuleHealth\s+Report\s*\(\s*\)\s*(?:=>|\{)", RegexOptions.Compiled);
    private static readonly Regex ProbeEvidence =
        new(@"Probe|CanConnect|\.Configuration|Configuration\[|GetConnectionString|Http|GetConfig",
            RegexOptions.Compiled);

    [Fact]
    public void Tripwire3_every_ok_module_report_shows_probe_evidence_or_is_allowlisted()
    {
        var moduleFiles = ServerSrcFiles
            .Where(p => p.Contains("/Modules/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(moduleFiles.Count >= 3,
            $"expected the Modules tree to be scannable, saw {moduleFiles.Count} files");

        var violations = new List<string>();
        int okSites = 0;

        foreach (var f in moduleFiles)
        {
            var scanned = RepoPaths.ScanFile(RepoPaths.Combine(f));
            foreach (var (cls, classStart, classEnd) in ClassRanges(scanned))
            {
                foreach (var (bodyStart, bodyEnd) in ReportBodies(scanned, classStart, classEnd))
                {
                    var body = scanned.Code[bodyStart..bodyEnd];
                    if (!body.Contains("DependencyState.Ok")) continue;
                    okSites++;
                    var classBody = scanned.Code[classStart..classEnd];
                    if (ProbeEvidence.IsMatch(classBody)) continue;
                    if (OkReportAllowlist.Any(a => a.Type == cls)) continue;
                    violations.Add($"{f}:{scanned.LineOf(bodyStart)} class {cls} reports Ok with no " +
                                   "Probe/CanConnect/GetConfig/Http evidence in its body and no allowlist entry");
                }
            }
        }

        Assert.True(okSites >= 1,
            "no Ok-reporting module found anywhere in Modules/** — this gate silently found nothing");
        Assert.Empty(violations);
    }

    [Fact]
    public void Tripwire3_self_test_a_fabricated_ok_without_a_probe_fires()
    {
        var sample = SourceTokenizer.Scan("synthetic.cs", """
            namespace N {
                public sealed class FakeModule : IFlivoraModule {
                    public string Key => "fake";
                    public ModuleHealth Report() => new(Key, DependencyState.Ok, "trust me");
                }
            }
            """);
        var (cls, start, end) = Assert.Single(ClassRanges(sample));
        Assert.Equal("FakeModule", cls);
        Assert.Single(ReportBodies(sample, start, end));
        var classHasProbe = ProbeEvidence.IsMatch(sample.Code[start..end]);
        Assert.False(classHasProbe, "a class with no probe evidence must be the violation shape this gate catches");
    }

    // ---- (4) ProblemCodes literals ---------------------------------------------------------------------

    // The frozen contract's code VALUES, read from the contract file's own literals (the tokenizer
    // strips string content from Code, so the literal list is the canonical source).
    private static readonly Lazy<HashSet<string>> KnownCodes = new(() =>
    {
        var scanned = RepoPaths.ScanFile(RepoPaths.Combine(
            "server/src/Livora.Server.Application/ApiProblem.cs"));
        var set = new HashSet<string>(
            scanned.Literals.Select(l => l.Text).Where(t => Regex.IsMatch(t, @"^[a-z][a-z_]{3,}$")),
            StringComparer.Ordinal);
        if (set.Count < 20)
            throw new InvalidOperationException(
                $"ProblemCodes extraction found only {set.Count} code values — the contract file moved; " +
                "this tripwire must not silently degrade into a no-op.");
        return set;
    });

    // Code argument that is neither a ProblemCodes./nameof() constant NOR a quoted literal: quoted
    // literals are caught by the KnownCodes check above (which can judge their VALUE); this one
    // catches the computed-expression shape (e.g. `Problems.Of(ctx, someVar, …)`).
    private const string NonConstantProblemsOfPattern =
        @"Problems\.Of\s*\([^,]+,\s*(?!ProblemCodes\.|nameof\()([^,)\r\n""'\s][^,)\r\n""']*)";

    [Fact]
    public void Tripwire4_problem_code_literals_exist_only_where_the_contract_says()
    {
        // Rules: handlers under Modules/** answer errors via Problems.Of(ctx, ProblemCodes.X, …).
        // A string literal equal to a code value, or a Problems.Of whose code argument is not a
        // ProblemCodes./nameof() constant, is client-localisation drift waiting to happen — the
        // brief forbids fail-open on ambiguity, so both shapes are REPORTED, never skipped.
        var moduleFiles = ServerSrcFiles
            .Where(p => p.Contains("/Modules/", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var violations = new List<string>();
        foreach (var f in moduleFiles)
        {
            var scanned = RepoPaths.ScanFile(RepoPaths.Combine(f));
            foreach (var lit in scanned.Literals)
            {
                if (KnownCodes.Value.Contains(lit.Text))
                    violations.Add($"{f}:{lit.Line} literal \"{lit.Text}\" equals a ProblemCodes value " +
                                   "outside a ProblemCodes constant");
            }
            foreach (Match m in Regex.Matches(scanned.Code, NonConstantProblemsOfPattern))
            {
                var arg = m.Groups[1].Value.Trim();
                if (arg.Length > 0)
                    violations.Add($"{f}:{scanned.LineOf(m.Index)} Problems.Of carries a non-constant code " +
                                   $"argument '{arg}' — use a ProblemCodes constant or file a request");
            }
        }
        Assert.Empty(violations);
    }

    [Fact]
    public void Tripwire4_self_test_a_bare_code_literal_and_a_computed_argument_fire()
    {
        // bare literal: caught by the VALUE check (the code projection drops string content, so the
        // argument-shape rule deliberately does NOT fire twice on the same line)
        var bare = SourceTokenizer.Scan("synthetic.cs",
            "return Problems.Of(ctx, \"not_found\", \"gone\");");
        Assert.Contains(bare.Literals, l => KnownCodes.Value.Contains(l.Text));
        Assert.Empty(Regex.Matches(bare.Code, NonConstantProblemsOfPattern));

        // computed argument: caught by the shape rule
        var computed = SourceTokenizer.Scan("synthetic.cs",
            "return Problems.Of(ctx, code, \"gone\");");
        var m = Assert.Single(Regex.Matches(computed.Code, NonConstantProblemsOfPattern));
        Assert.Equal("code", m.Groups[1].Value.Trim());

        // the sanctioned form fires neither
        var ok = SourceTokenizer.Scan("synthetic.cs",
            "return Problems.Of(ctx, ProblemCodes.NotFound, \"gone\");");
        Assert.DoesNotContain(ok.Literals, l => KnownCodes.Value.Contains(l.Text));
        Assert.Empty(Regex.Matches(ok.Code, NonConstantProblemsOfPattern));
    }

    // ---- (5) privacy: log sites never carry secret VALUES -----------------------------------------------

    private static readonly Regex LogCall =
        new(@"\bLog(?:Information|Warning|Error|Debug|Trace|Critical)\s*\(", RegexOptions.Compiled);

    /// <summary>Identifier shapes that denote secret MATERIAL. A hole that IS one of these (e.g.
    /// <c>{refreshToken}</c>, <c>{configured}</c>) or a log argument that is exactly one of these
    /// (<c>LogWarning("… reused", refreshToken)</c>) reads a secret value into the log stream.</summary>
    private static readonly Regex SecretValueIdentifier =
        new(@"^(?:refreshToken|accessToken|idToken|token|password|passwd|plainPassword|secret|clientSecret|" +
            @"apiKey|apikey|signature|configured|keyBytes|credential\w*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Documented narrow allowlist for tripwire 5 (per the brief: "explicit documented
    /// allowlist for the constant NAMES in the auth code that talk about them without values"):
    ///   - expressions that name a config or claim KEY, never a value
    ///     (ConfigKeySection, UidClaim, SessionClaim, DefaultIssuer, DefaultAudience)
    ///   - expressions that measure the LENGTH of secret material — <c>{material.Length}</c> at
    ///     server/src/Livora.Server/Platform/LivoraAuth.cs:105 is a byte count, a diagnostic; the
    ///     key itself never appears. Matched against the WHOLE hole/argument expression.</summary>
    private static readonly Regex AllowedSecretWordExpression =
        new(@"^(?:\w*\.)?(?:ConfigKeySection|UidClaim|SessionClaim|DefaultIssuer|DefaultAudience)$|^\w+\.Length$",
            RegexOptions.Compiled);

    [Fact]
    public void Tripwire5_log_call_sites_never_interpolate_secret_values()
    {
        var violations = new List<string>();
        foreach (var f in ServerSrcFiles)
            CheckLogCallsIn(violations, f, RepoPaths.ScanFile(RepoPaths.Combine(f)));
        Assert.Empty(violations);
    }

    /// <summary>The shared analyzer for the gate and its proof-of-fire self-tests: for every log
    /// call, both the interpolated TEMPLATE holes and the structured ARGUMENTS must read as names
    /// or lengths, never as secret material.</summary>
    private static void CheckLogCallsIn(List<string> violations, string file, ScannedFile scanned)
    {
        foreach (var (startLine, endLine, argCodeStart, argCodeEnd) in LogCallRanges(scanned))
        {
            // (a) interpolated templates inside the call: every HOLE must name, not carry
            foreach (var lit in scanned.Literals.Where(l => l.IsInterpolated
                         && l.Line >= startLine && l.Line <= endLine))
            {
                foreach (var hole in lit.HoleCode.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    CheckSecretExpression(violations, file, lit.Line, hole.Trim());
            }
            // (b) structured-logging ARGUMENTS (a plain format string carries values by argument);
            //     the template itself is skipped — (a) already read its holes
            foreach (var arg in SplitTopLevelArgs(scanned.Code[argCodeStart..argCodeEnd]))
            {
                var a = arg.Trim();
                if (a.Length == 0 || a[0] == '"' || a[0] == '$') continue;
                CheckSecretExpression(violations, file, startLine, a);
            }
        }
    }

    private static void CheckSecretExpression(List<string> violations, string file, int line, string expr)
    {
        if (expr.Length == 0) return;
        if (AllowedSecretWordExpression.IsMatch(expr)) return;
        var isSecretMaterial = SecretValueIdentifier.IsMatch(expr)
            || Regex.IsMatch(expr,
                @"^(?:this\.)?(?:refreshToken|accessToken|token|password|secret|clientSecret|apiKey)\.(?:Value|Text|ToString\(\))$");
        if (isSecretMaterial)
            violations.Add($"{file}:{line} log call carries secret material '{expr}' — " +
                           "log the NAME or a LENGTH, never the value (product law 5)");
    }

    private static IEnumerable<string> SplitTopLevelArgs(string callText)
    {
        int depth = 0;
        int start = 0;
        for (int i = 0; i < callText.Length; i++)
        {
            char c = callText[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return callText[start..i];
                start = i + 1;
            }
        }
        yield return callText[start..];
    }

    [Fact]
    public void Tripwire5_self_test_a_leaked_value_fires_and_key_names_pass()
    {
        var leak = new List<string>();
        CheckLogCallsIn(leak, "synthetic.cs", SourceTokenizer.Scan("synthetic.cs",
            "logger.LogInformation($\"user token was {refreshToken} ok\");"));
        Assert.NotEmpty(leak); // an interpolated secret value inside a log message must be caught

        var leakArg = new List<string>();
        CheckLogCallsIn(leakArg, "synthetic.cs", SourceTokenizer.Scan("synthetic.cs",
            "logger.LogWarning(\"refresh token reused\", refreshToken);"));
        Assert.NotEmpty(leakArg); // a structured-logging ARGUMENT carrying the value must be caught too

        var safe = new List<string>();
        CheckLogCallsIn(safe, "synthetic.cs", SourceTokenizer.Scan("synthetic.cs",
            "logger.LogInformation($\"section {ConfigKeySection} key length {material.Length}\");"));
        Assert.Empty(safe); // a config key NAME and a LENGTH are diagnostics, not leaks
    }

    // ---- (6) OpenAPI versioning gate (live host) ---------------------------------------------------------

    [Fact]
    public async Task Tripwire6_openapi_document_exposes_only_versioned_api_and_platform_utility_paths()
    {
        var fixture = new LivoraWebFixture();
        await fixture.InitializeAsync();
        try
        {
            var res = await fixture.Http.GetAsync("/openapi/v1.json");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
            Assert.True(paths.Count >= 3,
                $"the OpenAPI document exposed only {paths.Count} paths — the gate would pass vacuously");

            var offenders = paths
                .Where(p => !p.StartsWith("/api/v1", StringComparison.Ordinal)
                            && !Regex.IsMatch(p, @"^/(healthz|openapi(/|$))", RegexOptions.IgnoreCase))
                .ToList();
            Assert.True(offenders.Count == 0,
                "unversioned public surface found (the API is /api/v1-only; utilities are /healthz and /openapi): "
                + string.Join(", ", offenders));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    /// <summary>(startLine, endLine, argCodeStart, argCodeEnd) for every log-call expression, via
    /// parenthesis matching on the code projection — multi-line calls included, because a gate that
    /// only saw a call's first line would be decorative. The args range excludes the outer parens.</summary>
    private static List<(int startLine, int endLine, int argCodeStart, int argCodeEnd)> LogCallRanges(ScannedFile f)
    {
        var ranges = new List<(int, int, int, int)>();
        foreach (Match m in LogCall.Matches(f.Code))
        {
            int open = m.Index + m.Length - 1; // at '('
            int depth = 0, i = open;
            for (; i < f.Code.Length; i++)
            {
                if (f.Code[i] == '(') depth++;
                else if (f.Code[i] == ')')
                {
                    depth--;
                    if (depth == 0) break;
                }
            }
            if (i >= f.Code.Length) continue; // unbalanced: skip the RANGE; the file-level scans still ran
            ranges.Add((f.LineOf(open), f.LineOf(Math.Min(i, f.Code.Length - 1)), open + 1, i));
        }
        return ranges;
    }

    private static List<string> FilesMatching(string[] files, Regex regex)
    {
        var hits = new List<string>();
        foreach (var f in files)
        {
            var scanned = RepoPaths.ScanFile(RepoPaths.Combine(f));
            foreach (var line in scanned.Lines)
                if (regex.IsMatch(line.CodeText))
                    hits.Add($"{f}:{line.Number} {line.CodeText.Trim()}");
        }
        return hits;
    }

    private static List<int> SampleMatches(ScannedFile sample, Regex regex) =>
        sample.Lines.Where(l => regex.IsMatch(l.CodeText)).Select(l => l.Number).ToList();

    /// <summary>(class name, code start, code end) for every class declaration in the file.</summary>
    private static List<(string Name, int Start, int End)> ClassRanges(ScannedFile f)
    {
        var list = new List<(string, int, int)>();
        foreach (Match m in ClassDecl.Matches(f.Code))
        {
            int body = f.Code.IndexOf('{', m.Index);
            if (body < 0) continue;
            int end = MatchBrace(f.Code, body);
            if (end > 0) list.Add((m.Groups[1].Value, body, end));
        }
        return list;
    }

    /// <summary>(start, end) ranges of every ModuleHealth Report() member inside [classStart, classEnd).</summary>
    private static List<(int Start, int End)> ReportBodies(ScannedFile f, int classStart, int classEnd)
    {
        var list = new List<(int, int)>();
        foreach (Match m in ReportMethod.Matches(f.Code))
        {
            if (m.Index < classStart || m.Index >= classEnd) continue;
            int body = m.Index + m.Length - 1; // at '=>' or '{'
            if (f.Code[body] == '{')
            {
                int end = MatchBrace(f.Code, body);
                if (end > 0) list.Add((body, end));
            }
            else
            {
                int semi = f.Code.IndexOf(';', body);
                if (semi > 0 && semi < classEnd) list.Add((body, semi));
            }
        }
        return list;
    }

    private static int MatchBrace(string s, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i + 1;
        }
        return -1;
    }
}
