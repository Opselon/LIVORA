using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Livora.Server.Tests.Quality;

// NOTE (lane-collision, for the lead): this file and PlantedViolationTests.cs are Agent 16's
// file-based tripwire fixtures demanded by the P1-F brief ("fail on a planted violation you commit
// as a fixture in your own folder"). They COMPLEMENT SourceHonestyTests (inline-sample analyzers
// for blocking calls, secret literals, Ok-probe, ProblemCodes, log privacy, OpenAPI versioning)
// rather than replacing them: this scanner covers the word-level laws (claim words, mock labels,
// bilingual manifests, raw token/health values in logs) over BOTH trees (client + server) with
// on-disk fixtures an auditor can read and diff. Both lenses must agree before the gate says green.

/// <summary>
/// PURPOSE: §7 word-level honesty tripwires as source analysis with proof-of-fire:
///   T1 a DependencyState.Ok Report() with no probe/config/await evidence in its class;
///   T2 a positive "connected"/"verified"/"paid" literal in a file with no state machine behind it;
///   T3 a test-double-shaped type in production directories that never labels itself a mock;
///   T4 an EN key manifest without a complete FA twin (missing key, empty value, placeholder
///      drift, FA value with no Persian script and no passthrough hole);
///   T5 a secret or raw-health VALUE reaching a log call — interpolated hole or structured argument.
/// OWNER: Agent 16 (P1-F QA lane).
/// CONSUMES: RepoPaths (root discovery + tokenizer cache) and SourceTokenizer (the proven lexer:
///           comments and string CONTENT are stripped from the code projection; interpolation-hole
///           code is kept; literal text comes from the same parse, so both views agree).
/// PROVIDES: ScanSources / ScanKeyManifestDirectory returning (Rule, Path, Line, Text) hits — used
///           by PlantedViolationTests against on-disk fixtures (must fire) and the whole tree (must
///           be clean, or the entry is a documented finding).
/// INVARIANTS:
///   - fail-safe: too few scanned files THROWS; a scan that saw nothing certifies nothing
///   - a rule's hit text is the LITERAL or the CODE LINE that tripped, so the report is readable
///     without opening the file (file:line is always present too — Rule A)
///   - this file and its test are excluded from their own scan (they quote the patterns)
///   - fixtures live OUTSIDE ProductionRoots (server/tests/…): they cannot pollute the gate
/// EXTEND: new rule => bad + good fixture pair + a case per direction in PlantedViolationTests.
/// </summary>
public static class TripwireScanner
{
    public sealed record Hit(string Rule, string Path, int Line, string Text);

    public static readonly string[] ProductionRoots =
    [
        "Application", "Domain", "Infrastructure", "Presentation", "server/src",
    ];

    public static readonly string[] SelfExcludedFileNames =
    [
        "TripwireScanner.cs", "PlantedViolationTests.cs",
    ];

    public static IReadOnlyList<(string Path, ScannedFile File, string Raw)> CollectProductionSources()
    {
        var units = new List<(string, ScannedFile, string)>();
        foreach (var root in ProductionRoots)
        {
            if (!Directory.Exists(RepoPaths.Combine(root)))
                throw new InvalidOperationException($"expected production root missing: {root}");
            foreach (var rel in RepoPaths.Enumerate(root))
            {
                if (SelfExcludedFileNames.Contains(Path.GetFileName(rel))) continue;
                var abs = RepoPaths.Combine(rel);
                units.Add((rel, RepoPaths.ScanFile(abs), File.ReadAllText(abs)));
            }
        }
        if (units.Count < 50)
            throw new InvalidOperationException(
                $"tripwire scanned only {units.Count} files — refusing to certify a tree it cannot see");
        return units;
    }

    // ---------------------------------------------------------------- T1: Ok without a probe

    private static readonly Regex ReportDecl =
        new(@"\bModuleHealth\s+Report\s*\(\s*\)\s*(?:=>|\{)", RegexOptions.Compiled);

    private static readonly Regex ProbeEvidence =
        new(@"Probe|CanConnect|Configuration\[|GetConnectionString|Http|\bawait\b|VerifyAsync|PingAsync",
            RegexOptions.Compiled);

    private static readonly Regex ClassDecl =
        new(@"\b(?:(?:public|private|internal|protected|sealed|static|abstract|partial|file)\s+)*class\s+(\w+)",
            RegexOptions.Compiled);

    /// <summary>T1: Report() claiming Ok must sit in a class that actually probes/reads config/awaits
    /// something. Independent second lens over SourceHonestyTests.Tripwire3 (same idea, stricter:
    /// no type-name allowlist here, file-class-scope evidence only).</summary>
    public static IReadOnlyList<Hit> OkWithoutProbe(IEnumerable<(string Path, ScannedFile File, string Raw)> units)
    {
        var hits = new List<Hit>();
        foreach (var (path, f, _) in units)
        {
            if (!f.Code.Contains("IFlivoraModule", StringComparison.Ordinal)) continue;
            foreach (Match decl in ReportDecl.Matches(f.Code))
            {
                var body = ReportBody(f.Code, decl);
                if (body is null || !body.Contains("DependencyState.Ok", StringComparison.Ordinal)) continue;
                var enclosing = EnclosingClassBody(f.Code, decl.Index);
                if (enclosing is not null && ProbeEvidence.IsMatch(enclosing)) continue;
                int line = f.LineOf(decl.Index);
                hits.Add(new Hit("T1/ok-without-probe", path, line, CodeText(f, line)));
            }
        }
        return hits;
    }

    private static string? ReportBody(string code, Match decl)
    {
        int last = decl.Index + decl.Length - 1; // last char of the match: '>' of '=>' or the '{'
        if (last >= code.Length) return null;
        if (code[last] == '{')
        {
            int depth = 0;
            for (int j = last; j < code.Length; j++)
            {
                if (code[j] == '{') depth++;
                else if (code[j] == '}' && --depth == 0) return code[last..(j + 1)];
            }
            return code[last..];
        }
        // expression body: the match ended with '=>', the body starts just past it
        int body = decl.Index + decl.Length;
        int semi = code.IndexOf(';', body);
        return semi < 0 ? code[body..] : code[body..semi];
    }

    private static string? EnclosingClassBody(string code, int index)
    {
        string? best = null;
        foreach (Match m in ClassDecl.Matches(code))
        {
            int brace = code.IndexOf('{', m.Index);
            if (brace < 0 || brace > index) continue;
            int depth = 0;
            int end = -1;
            for (int j = brace; j < code.Length; j++)
            {
                if (code[j] == '{') depth++;
                else if (code[j] == '}' && --depth == 0) { end = j + 1; break; }
            }
            if (end > index) best = code[brace..end]; // last wins = innermost class
        }
        return best;
    }

    // ---------------------------------------------------------------- T2: claim words

    private static readonly Regex ClaimWord =
        new(@"\b(connected|verified|paid)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NegativePrefix =
        new(@"(?:not|never|un|non)[_\- ]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Dot-separated resource key: rendered text is the resx suite's job, not this rule's.</summary>
    private static readonly Regex ResourceKeyShape =
        new(@"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_-]+)+$", RegexOptions.Compiled);

    private static readonly Regex StateMachineSymbol =
        new(@"\b(DependencyState|ConnectionState|ConnectorState|AccountStatus|AccountTier|BridgeAvailability|PermissionState|PaymentState|PaymentStatus|PurchaseState|VerificationState)\b|" +
            // R3 WIDENING (documented in TRIPWIRES.md, 14 Sep): the rule asks "is there a state
            // machine behind the claim word?" and used to answer that only from an enumerated list
            // of TYPE NAMES — so a file that DECLARES its own status enum failed the eye test.
            // The verification engine (Engines/Pipeline/Verification.cs) is the living proof: it
            // defines `public enum VerificationStatus { Unattested, Corroborated, Contradicted,
            // … }` + EvidenceGrade and literally exists to refuse a collapsed "verified" — the
            // opposite of the lie the rule hunts. What is accepted now instead: a local enum
            // DECLARATION whose name ends in State/Status/Grade/Tier/Phase (a declaration, not a
            // mention — a string or comment cannot satisfy `enum\s+\w*Status`), or a switch
            // expression over such a type. What is STILL rejected: any claim word in a file with no
            // state machine at all (the planted T2_ClaimWords_Bad.cs.fix carries zero enums and
            // must keep firing — pinned by T2_fires_on_every_planted_connected_verified_paid_claim).
            @"\benum\s+\w*(?:State|Status|Grade|Tier|Phase)\b|" +
            @"\b\w*(?:State|Status|Grade|Tier|Phase)\s+\w+\s+switch\b",
            RegexOptions.Compiled);

    /// <summary>
    /// T2: a POSITIVE human-readable connected/verified/paid string is only honest where a state
    /// machine in the same file decides when it may be shown (product law §0.1/§0.2). Negated
    /// ("Not connected") and key-shaped ("Profile.Status.NotConnected") strings are exempt: they
    /// claim the opposite or nothing at all.
    /// </summary>
    public static IReadOnlyList<Hit> ClaimWordWithoutStateMachine(IEnumerable<(string Path, ScannedFile File, string Raw)> units)
    {
        var hits = new List<Hit>();
        foreach (var (path, f, _) in units)
        {
            if (StateMachineSymbol.IsMatch(f.Code)) continue;
            foreach (var lit in f.Literals)
            {
                if (lit.Text.Length == 0 || lit.IsRaw) continue;
                if (ResourceKeyShape.IsMatch(lit.Text)) continue;
                foreach (Match w in ClaimWord.Matches(lit.Text))
                {
                    var before = lit.Text[Math.Max(0, w.Index - 6)..w.Index];
                    if (NegativePrefix.IsMatch(before)) continue;
                    hits.Add(new Hit("T2/claim-word-no-state-machine", path, lit.Line,
                        $"\"{lit.Text}\" (word: {w.Value})"));
                    break; // one hit per literal is enough; the line is the unit of blame
                }
            }
        }
        return hits;
    }

    // ---------------------------------------------------------------- T3: unlabelled mock

    private static readonly Regex MockTypeName =
        new(@"\b(?:class|record|struct)\s+(\w*(?:Mock|Fake|Stub|Dummy|Double)\w*)\b", RegexOptions.Compiled);

    private static readonly Regex MockLabelEvidence =
        new(@"DataOrigin\.Mock|SourceType\.Mock|AiProviderKind\.Mock", RegexOptions.Compiled);

    private static readonly Regex MockDocWord = new(@"^\s*(///|//).*\bmock\b", RegexOptions.Compiled);

    /// <summary>T3: a test-double-shaped TYPE in production directories must say so — a mock-origin
    /// enum in its code, or a comment line naming itself a mock (the label exists to be read).</summary>
    public static IReadOnlyList<Hit> UnlabelledMock(IEnumerable<(string Path, ScannedFile File, string Raw)> units)
    {
        var hits = new List<Hit>();
        foreach (var (path, f, raw) in units)
        {
            bool labelled = MockLabelEvidence.IsMatch(f.Code)
                            || raw.Split('\n').Any(l => MockDocWord.IsMatch(l));
            if (labelled) continue;
            foreach (Match m in MockTypeName.Matches(f.Code))
            {
                int line = f.LineOf(m.Index);
                hits.Add(new Hit("T3/unlabelled-mock", path, line, CodeText(f, line)));
            }
        }
        return hits;
    }

    // ---------------------------------------------------------------- T5: raw value into a log call

    private static readonly Regex LogCall =
        new(@"\bLog(?:Information|Warning|Error|Debug|Trace|Critical)\s*\(", RegexOptions.Compiled);

    /// <summary>Bare identifiers that ARE secret or raw-health material (law §0.5: content never rides logs).</summary>
    private static readonly Regex SecretValueName =
        new(@"^(?:refreshToken|accessToken|idToken|token|password|passwd|plainPassword|secret|clientSecret|apiKey|apikey|webhookSigningSecret|heartRate|restingHeartRate|glucose|sleepMinutesRaw|rawSleep|stepsRaw)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Member access ending ON secret/health material: account.Password, row.HeartRate, …</summary>
    private static readonly Regex SecretMemberAccess =
        new(@"^(?:[A-Za-z_][\w]*\.)*(?:Password|Secret|ApiKey|AccessToken|RefreshToken|IdToken|WebhookSigningSecret|HeartRate|Glucose)(?:\.(?:Value|ToString\(\)|Trim\(\)))?$",
            RegexOptions.Compiled);

    /// <summary>Names/lengths/hashes/presence flags: diagnostics, not values. Deliberately narrow.</summary>
    private static readonly Regex SafeExpression =
        new(@"(?i)(hash|present|presence|configured|length|count|fingerprint|redacted|correlation|lifetime|section|claim|issuer|audience)$",
            RegexOptions.Compiled);

    /// <summary>
    /// T5: both leak shapes — interpolated holes ({refreshToken}) and structured arguments
    /// (Log("…", refreshToken)) — checked against every LogXxx call in the code projection, where
    /// hole code survives by lexer design. Second, broader lens over SourceHonestyTests.Tripwire5
    /// (this one also names raw HEALTH fields, and covers the client tree, not only server/src).
    /// </summary>
    public static IReadOnlyList<Hit> SecretOrHealthInLogCall(IEnumerable<(string Path, ScannedFile File, string Raw)> units)
    {
        var hits = new List<Hit>();
        foreach (var (path, f, _) in units)
        {
            foreach (Match m in LogCall.Matches(f.Code))
            {
                int open = m.Index + m.Length - 1; // at '('
                int depth = 0;
                int close = -1;
                for (int j = open; j < f.Code.Length; j++)
                {
                    if (f.Code[j] == '(') depth++;
                    else if (f.Code[j] == ')' && --depth == 0) { close = j; break; }
                }
                if (close < 0) continue;
                int startLine = f.LineOf(open);
                int endLine = f.LineOf(close);

                foreach (var lit in f.Literals.Where(l => l.IsInterpolated
                             && l.Line >= startLine && l.Line <= endLine))
                {
                    foreach (var hole in lit.HoleCode.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (IsSecretExpression(hole))
                            hits.Add(new Hit("T5/secret-or-health-in-log", path, lit.Line,
                                CodeText(f, lit.Line) + $"  <hole {hole}>"));
                    }
                }

                foreach (var arg in SplitTopLevelArgs(f.Code[(open + 1)..close]))
                {
                    var a = arg.Trim();
                    if (a.Length == 0) continue;
                    if (a[0] is '"' or '$' or '@') continue;    // template strings: holes checked above
                    if (IsSecretExpression(a))
                        hits.Add(new Hit("T5/secret-or-health-in-log", path, startLine,
                            CodeText(f, startLine) + $"  <arg {a}>"));
                }
            }
        }
        return hits.DistinctBy(h => (h.Path, h.Line, h.Text)).ToList();
    }

    private static bool IsSecretExpression(string expr)
    {
        expr = expr.Trim();
        if (expr.Length == 0) return false;
        var last = expr.Split('.')[^1];
        last = Regex.Replace(last, @"\(+\)*$", ""); // ToString() -> ToString
        if (last.Length == 0) return false;
        if (SafeExpression.IsMatch(last)) return false;
        return SecretValueName.IsMatch(last) || SecretMemberAccess.IsMatch(expr);
    }

    private static IEnumerable<string> SplitTopLevelArgs(string callText)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < callText.Length; i++)
        {
            char c = callText[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { yield return callText[start..i]; start = i + 1; }
        }
        yield return callText[start..];
    }

    // ---------------------------------------------------------------- aggregate + T4

    public static IReadOnlyList<Hit> ScanSources(IEnumerable<(string Path, ScannedFile File, string Raw)> units)
    {
        var shared = units.ToList();
        var all = new List<Hit>();
        all.AddRange(OkWithoutProbe(shared));
        all.AddRange(ClaimWordWithoutStateMachine(shared));
        all.AddRange(UnlabelledMock(shared));
        all.AddRange(SecretOrHealthInLogCall(shared));
        return all;
    }

    private static string CodeText(ScannedFile f, int line) =>
        f.Lines.FirstOrDefault(l => l.Number == line)?.CodeText.Trim() ?? "";

    /// <summary>
    /// T4: the bilingual law (§0.4) over the wave key-manifest convention
    /// (lane-XX.en.keys.xml + lane-XX.fa.keys.xml). Every EN key needs an FA twin with identical
    /// key sets, non-empty values, matching {0}/{1} placeholder sets, and — unless allowlisted as
    /// Latin-by-definition (brand names) — actual Persian script in the FA value.
    /// </summary>
    public static IReadOnlyList<Hit> ScanKeyManifestDirectory(string directory,
        IReadOnlySet<string>? latinOnlyAllowed = null)
    {
        var hits = new List<Hit>();
        if (!Directory.Exists(directory)) return hits; // caller asserts existence; vacuity is pinned there
        latinOnlyAllowed ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var en in Directory.EnumerateFiles(directory, "*.en.keys.xml", SearchOption.AllDirectories))
        {
            var fa = en[..^".en.keys.xml".Length] + ".fa.keys.xml";
            var rel = en.Replace('\\', '/');
            var enKeys = ReadKeys(en);
            if (!File.Exists(fa))
            {
                hits.Add(new Hit("T4/english-only-keys", rel, 1, "no .fa twin manifest exists"));
                continue;
            }
            var faKeys = ReadKeys(fa);
            foreach (var k in enKeys.Keys.Where(k => !faKeys.ContainsKey(k)))
                hits.Add(new Hit("T4/english-only-keys", rel, 1, $"key '{k}' has no FA counterpart"));
            foreach (var k in faKeys.Keys.Where(k => !enKeys.ContainsKey(k)))
                hits.Add(new Hit("T4/english-only-keys", rel, 1, $"FA key '{k}' has no EN counterpart"));
            foreach (var k in enKeys.Keys.Where(k => faKeys.ContainsKey(k)))
            {
                if (string.IsNullOrWhiteSpace(enKeys[k]))
                    hits.Add(new Hit("T4/english-only-keys", rel, 1, $"key '{k}' has an empty EN value"));
                if (string.IsNullOrWhiteSpace(faKeys[k]))
                    hits.Add(new Hit("T4/english-only-keys", rel, 1, $"key '{k}' has an empty FA value"));
                var phEn = Placeholders(enKeys[k]);
                var phFa = Placeholders(faKeys[k]);
                if (!phEn.SequenceEqual(phFa))
                    hits.Add(new Hit("T4/english-only-keys", rel, 1,
                        $"key '{k}' placeholder drift EN[{string.Join(",", phEn)}] FA[{string.Join(",", phFa)}]"));
                if (!faKeys[k].Any(c => c is >= '\u0600' and <= '\u06FF') && phFa.Length == 0
                    && !latinOnlyAllowed.Contains(k))
                    hits.Add(new Hit("T4/english-only-keys", rel, 1,
                        $"key '{k}' FA value has no Persian script and no placeholder: '{faKeys[k]}'"));
            }
        }
        return hits;
    }

    private static Dictionary<string, string> ReadKeys(string path)
    {
        // Strict parse FIRST. Only if it fails do we retry through the documented comment
        // normalisation (see NormalizeManifestForReading) — so a file broken in any way beyond its
        // comment dashes still throws, and the bilingual law never reads mutated data bytes.
        XDocument doc;
        var raw = File.ReadAllText(path);
        try
        {
            doc = XDocument.Parse(raw);
        }
        catch (System.Xml.XmlException) when (NeedsCommentRepair(raw))
        {
            doc = XDocument.Parse(NormalizeManifestForReading(raw));
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in doc.Root?.Elements("data") ?? [])
        {
            var name = data.Attribute("name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            map[name] = data.Element("value")?.Value ?? "";
        }
        return map;
    }

    /// <summary>True only when the sole well-formedness defect is `--` inside comments — the gate
    /// refuses to work around any other XML error.</summary>
    private static bool NeedsCommentRepair(string raw)
    {
        try { XDocument.Parse(NormalizeManifestForReading(raw)); return true; }
        catch (System.Xml.XmlException) { return false; }
    }

    /// <summary>
    /// R3 (14 Sep): the shipped P1-D manifests (`wave4-keys/lane-p1d.{en,fa}.keys.xml`) use
    /// `<!-- ---- heading ---- -->` banner comments, and `--` is illegal INSIDE an XML comment
    /// (W3C XML 1.0 §2.5) — so `XDocument.Load` throws before the bilingual law can be read at all.
    /// This lane may not edit another lane's files, so the scanner reads the real files through a
    /// documented, minimal normalisation: `-` is stripped from comment INTERIORS only, so every
    /// `<data>`/`<value>` byte the law judges
    /// is the shipped byte, and the strict parse still has to succeed afterwards (a file that is
    /// broken beyond its comments throws). The defect itself is NOT hidden: it is counted and
    /// printed by <see cref="ManifestCommentDefects"/> and filed as a request line
    /// (docs/quality/wave4/requests/r3.md) for P1-D to fix, so the certification stays honest in
    /// both directions — the bilingual law is enforced in full, and the invalid-XML fact is on record.
    /// </summary>
    internal static string NormalizeManifestForReading(string xml) =>
        Regex.Replace(xml, @"<!--(.*?)-->", m => "<!--" + m.Groups[1].Value.Replace("-", "") + "-->",
            RegexOptions.Singleline);

    /// <summary>Files under `directory` whose XML comments are not well-formed (the P1-D defect
    /// shape). Used to report the finding, never to excuse it.</summary>
    public static IReadOnlyList<string> ManifestCommentDefects(string directory)
    {
        var bad = new List<string>();
        if (!Directory.Exists(directory)) return bad;
        foreach (var f in Directory.EnumerateFiles(directory, "*.keys.xml", SearchOption.AllDirectories))
        {
            try { XDocument.Load(f); }
            catch (System.Xml.XmlException) { bad.Add(f.Replace('\\', '/')); }
        }
        return bad;
    }

    private static int[] Placeholders(string format) =>
        Regex.Matches(format ?? "", @"\{(\d+)").Select(m => int.Parse(m.Groups[1].Value))
              .Distinct().OrderBy(x => x).ToArray();
}
