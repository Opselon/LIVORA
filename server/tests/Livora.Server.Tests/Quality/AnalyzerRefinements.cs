using System.Text.RegularExpressions;

namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: the two analyzers that need more than a regex over the code projection — (a) which
///          `.Result` accesses actually BLOCK on a Task, and (b) whether a `Problems.Of` code
///          argument is a local that can only ever hold a frozen ProblemCodes constant.
/// OWNER: Agent 16 (P1-F/R3 quality lane). Split out of SourceHonestyTests so the gate file stays
///          readable; SourceHonestyTests pins both with proof-of-fire tests.
/// PROVIDES: <see cref="BlockingCallScanner.Violations"/>,
///           <see cref="ProblemCodeArgumentRules.IsConstantOnlyLocalCodeArgument"/>.
///
/// WHY THESE EXIST (the honesty part, stated plainly — R3 refinement, 14 Sep):
///   The first version of tripwire 1 was a pure text rule: any `.Result` in server/src code. That
///   is wrong on a tree with record DTOs: `record RuleOutcome(string RuleKey, string Result, …)`
///   and `outcomes.Select(o => o.Result)` are a PROPERTY READ on a verification result, not a
///   blocking Task wait. A gate that cries wolf on every DTO is a gate that gets its allowlist
///   widened until it gates nothing, so the rule was narrowed to its actual contract-law target:
///   SYNCHRONOUS-BLOCKING access to an ASYNC result.
///   What was WIDENED: `.Result` is ignored unless the receiver reads as a Task — see the
///   receiver evidence below. Nothing else was loosened: `.Wait()` and `.GetAwaiter().GetResult()`
///   are still flagged unconditionally.
///   WHAT THE RULE DEMANDS INSTEAD (proof, not vibes): the receiver expression must positively
///   look like a Task — (1) `Task`/`ValueTask`/`Task<T>` declared name in this file (field,
///   property, parameter or local), (2) a local `var x = …SomeAsync(...)` minted from an async
///   call without `await`, (3) a call whose invoked name ends in `Async` (`GetThingAsync().Result`),
///   or (4) a static on the task family (`Task.Run(…).Result`, `Task.Delay(…).Wait()`).
///   RESIDUAL FALSE NEGATIVE, disclosed: a Task-typed value that reaches `.Result` through a shape
///   none of the four evidence kinds covers — a property or method whose DECLARED type is Task but
///   named without the Async suffix, across a file boundary (e.g. `svc.Pending.Result` where
///   `Pending` is a `Task` property in another project). The scanner cannot see types without a
///   compiler. Compensation: (i) `.Wait()`/`GetAwaiter().GetResult()` need no receiver evidence, so
///   the other two blocking idioms are fully covered; (ii) the file-level convention guard stays:
///   server/src is an async-only codebase (every handler is `async`/`await`), so an unawaited Task
///   is overwhelmingly minted by a call named `…Async(...)` or a declared Task local — both covered;
///   (iii) any new false negative the lead wants covered is an ADD to the evidence list, never a
///   removal, and must come with a synthetic offender in SourceHonestyTests.
/// INVARIANTS:
///   - position-faithful: every violation carries the real 1-based line of the code projection
///   - fail-closed on the shapes that need no type knowledge (Wait / GetAwaiter)
///   - a `.Result` on a name that is NOT Task-evidenced is reported by
///     <see cref="TaskShapedMisses"/> as a COUNT only, so the reader can see how much the rule
///     declined to judge instead of pretending it saw nothing
/// </summary>
public static class BlockingCallScanner
{
    /// <summary>Blocking idioms that need no receiver evidence at all.</summary>
    private static readonly Regex AwaiterGetResult =
        new(@"\.GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(", RegexOptions.Compiled);

    private static readonly Regex WaitCall =
        new(@"\.Wait\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex ResultAccess =
        new(@"\.Result(?![A-Za-z0-9_])", RegexOptions.Compiled);

    /// <summary>Names the file itself declares as Task/ValueTask (fields, properties, parameters,
    /// locals, and `foreach`/`using` clauses). Generic arguments are skipped so `Task<int> x`
    /// yields `x`, not `int`.</summary>
    private static readonly Regex TaskDeclaredName = new(
        @"(?<![\w.])(?:Value)?Task(?:<[^<>]*(?:<[^<>]*>[^<>]*)*>)?\s+" +
        @"(?:\(\s*[A-Za-z_][\w]*\s*\)|[A-Za-z_][\w]*)",
        RegexOptions.Compiled);

    /// <summary>`var x = <something>Async(...)` without an `await` between `=` and the call: the
    /// local holds a Task, so `x` becomes Task-evidenced (the chain case, `x.Result`). A name
    /// assigned from an AWAITED async call is deliberately NOT collected — that value is the
    /// result, not a Task, so collecting it would manufacture false positives.</summary>
    private static readonly Regex VarFromUnawaitedAsyncCall = new(
        @"\bvar\s+(?<name>[A-Za-z_]\w*)\s*=\s*(?:(?!await\b)[^;=])*?(?<callee>[A-Za-z_]\w*Async)\s*\(",
        RegexOptions.Compiled);

    /// <summary>A member name that is Task-shaped by convention (`task`, `pendingTask`, `_jobTask`).</summary>
    private static readonly Regex TaskShapedName =
        new(@"^(?:_?task\w*|\w*Task)$", RegexOptions.Compiled);

    /// <summary>Returns "file:line text" style violations for one scanned file.</summary>
    public static List<string> Violations(ScannedFile scanned, string displayPath)
    {
        var hits = new List<string>();
        var taskNames = TaskNamedSymbols(scanned.Code);

        foreach (var line in scanned.Lines)
        {
            var text = line.CodeText;
            foreach (Match m in AwaiterGetResult.Matches(text))
                hits.Add(Report(displayPath, line.Number, text, m, "GetAwaiter().GetResult() blocks on a Task"));
            foreach (Match m in WaitCall.Matches(text))
                hits.Add(Report(displayPath, line.Number, text, m, ".Wait() blocks (no receiver evidence needed)"));
            foreach (Match m in ResultAccess.Matches(text))
            {
                if (IsTaskReceiver(text, m.Index, taskNames))
                    hits.Add(Report(displayPath, line.Number, text, m, ".Result on a Task-shaped receiver"));
            }
        }
        return hits;
    }

    /// <summary>How many `.Result` accesses the rule DECLINED to judge in this file (DTO property
    /// reads). Not a verdict — a disclosure, so the widening is visible in the test output.</summary>
    public static int TaskShapedMisses(ScannedFile scanned)
    {
        var taskNames = TaskNamedSymbols(scanned.Code);
        return scanned.Lines.Sum(l => ResultAccess.Matches(l.CodeText)
            .Count(m => !IsTaskReceiver(l.CodeText, m.Index, taskNames)));
    }

    private static string Report(string file, int line, string text, Match m, string why) =>
        $"{file}:{line} col {m.Index + 1}: {why} — {text.Trim()}";

    /// <summary>True when the expression immediately left of `.Result` reads as a Task.
    /// <paramref name="resultIndex"/> is the offset of the '.' that opens the access.</summary>
    internal static bool IsTaskReceiver(string codeText, int resultIndex, HashSet<string> taskNames)
    {
        int i = resultIndex - 1;                       // the receiver's last char (skip blanks)
        while (i >= 0 && char.IsWhiteSpace(codeText[i])) i--;
        if (i < 0) return false;

        // (a) call receiver: `…Async(…).Result`, `Task.Run(…).Result`, `t.ContinueWith(…).Result`
        if (codeText[i] == ')')
        {
            int open = MatchParen(codeText, i);
            if (open < 0) return false;
            int j = open - 1;
            while (j >= 0 && (char.IsLetterOrDigit(codeText[j]) || codeText[j] is '_' or '.')) j--;
            var chain = codeText[(j + 1)..open].Trim('.');
            if (chain.Length == 0) return false;       // e.g. "(x).Result" — no name to judge
            return ChainReadsAsTask(chain, taskNames);
        }

        // (b) identifier / member-access receiver: `t.Result`, `t2.Result`, `o.Result`, `a.b.Result`
        if (char.IsLetter(codeText[i]) || char.IsDigit(codeText[i]) || codeText[i] == '_')
        {
            int j = i;
            while (j >= 0 && (char.IsLetterOrDigit(codeText[j]) || codeText[j] == '_')) j--;
            // walk a dotted prefix too: `dto.Outcome.Result` → chain "dto.Outcome"
            while (j >= 0 && codeText[j] == '.' && j - 1 >= 0
                   && (char.IsLetter(codeText[j - 1]) || codeText[j - 1] == '_'))
            {
                int k = j - 1;
                while (k >= 0 && (char.IsLetterOrDigit(codeText[k]) || codeText[k] == '_')) k--;
                j = k;
            }
            return ChainReadsAsTask(codeText[(j + 1)..(i + 1)], taskNames);
        }

        // (c) any other receiver (`a[0].Result`, literals): not judgeable as a Task — decline.
        return false;
    }

    /// <summary>A dotted receiver chain reads as a Task when it ends in the `…Async` convention,
    /// contains the `Task`/`ValueTask` family segment (`Task.Run`), or its first/last segment is a
    /// Task-declared or Task-named symbol (`t.ContinueWith(…)`, `pendingTask.Result`).</summary>
    private static bool ChainReadsAsTask(string chain, HashSet<string> taskNames)
    {
        if (chain.EndsWith("Async", StringComparison.Ordinal)) return true;
        var segments = chain.Split('.');
        if (segments.Any(s => s is "Task" or "ValueTask")) return true;
        var last = segments[^1];
        if (last.Length == 0) return false;
        if (taskNames.Contains(last) || TaskShapedName.IsMatch(last)) return true;
        return segments.Length > 1 && taskNames.Contains(segments[0]);
    }

    /// <summary>All Task/ValueTask-typed symbols declared anywhere in the file, plus the `Task`
    /// family name itself (`Task.Run(…).Result` is caught by the factory rule).</summary>
    internal static HashSet<string> TaskNamedSymbols(string code)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in TaskDeclaredName.Matches(code))
        {
            var tail = m.Value;
            int space = tail.IndexOf(' ');
            var name = (space < 0 ? tail : tail[space..]).Trim();
            if (name.Length > 0 && name is not ("Task" or "ValueTask"))
                names.Add(name);
        }
        foreach (Match m in VarFromUnawaitedAsyncCall.Matches(code))
            names.Add(m.Groups["name"].Value);
        return names;
    }

    private static int MatchParen(string s, int closeIdx)
    {
        int depth = 0;
        for (int i = closeIdx; i >= 0; i--)
        {
            if (s[i] == ')') depth++;
            else if (s[i] == '(' && --depth == 0) return i;
        }
        return -1;
    }
}

/// <summary>
/// PURPOSE: the narrow second lens for tripwire 4. The rule's PURPOSE is "a client-visible problem
///          code must come from the frozen ProblemCodes set" — not "it must be spelled inline".
///          P1-B's sync handler mints `var replayCode = result.ProblemCode == ProblemCodes.VersionConflict
///          ? ProblemCodes.VersionConflict : ProblemCodes.Conflict;` and passes `replayCode`. Every
///          VALUE that variable can hold is a ProblemCodes constant, so the law is satisfied; the
///          bare regex would have flagged a compliant line.
/// OWNER: Agent 16 (R3). Widening documented in TRIPWIRES.md; the demanded evidence is exact:
///          every assignment to the name in the SAME file must have a right-hand side built only
///          from `ProblemCodes.X`, `nameof(...)`, literals of those, and ternaries/parens between
///          them. Anything else — a request header, a DB column, a method call — still fires.
/// INVARIANTS:
///   - a name with NO assignment found in the file is NOT excused (unproven = violation, fail-closed)
///   - a name with at least one non-constant assignment is a violation
///   - only bare identifiers are considered; quoted literals and dotted expressions are judged by
///     the existing rules in SourceHonestyTests
/// </summary>
public static class ProblemCodeArgumentRules
{
    private static readonly Regex ConstantAtom =
        new(@"^(?:ProblemCodes\.\w+|nameof\s*\(\s*[A-Za-z0-9_.]+\s*\))$", RegexOptions.Compiled);

    private static readonly Regex AssignmentOf =
        new(@"(?<![\w.])(?:var\s+|(?:string|ProblemCode)\s+)?(?<name>\w+)\s*=\s*(?<rhs>[^;]*);", RegexOptions.Compiled);

    /// <summary>True when `name` is a local in this file whose every assignment yields only frozen
    /// problem-code constants.</summary>
    public static bool IsConstantOnlyLocalCodeArgument(ScannedFile scanned, string name)
    {
        if (name.Length == 0 || !char.IsLetter(name[0]) && name[0] != '_') return false;
        bool sawAssignment = false;
        foreach (Match m in AssignmentOf.Matches(scanned.Code))
        {
            if (m.Groups["name"].Value != name) continue;
            sawAssignment = true;
            if (!ValuesAreConstantOnly(m.Groups["rhs"].Value)) return false;
        }
        return sawAssignment;
    }

    /// <summary>Value positions only: a ternary's two branches (recursively) and any bare atom.
    /// The CONDITION may read state — it cannot introduce a new code value.</summary>
    internal static bool ValuesAreConstantOnly(string rhs)
    {
        rhs = rhs.Trim();
        while (rhs.StartsWith('(') && rhs.EndsWith(')') && BalancedOuter(rhs))
            rhs = rhs[1..^1].Trim();

        int q = TopLevel(rhs, '?');
        if (q > 0)
        {
            int colon = TopLevel(rhs[(q + 1)..], ':');
            if (colon < 0) return false;                       // malformed ternary → not excused
            var whenTrue = rhs[(q + 1)..(q + 1 + colon)];
            var whenFalse = rhs[(q + 2 + colon)..];
            return ValuesAreConstantOnly(whenTrue) && ValuesAreConstantOnly(whenFalse);
        }
        return ConstantAtom.IsMatch(rhs);
    }

    private static bool BalancedOuter(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i == s.Length - 1;
        }
        return false;
    }

    private static int TopLevel(string s, char c)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '(' or '[' or '{') depth++;
            else if (ch is ')' or ']' or '}') depth--;
            else if (ch == c && depth == 0) return i;
        }
        return -1;
    }
}
