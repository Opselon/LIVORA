using System.Text;

namespace Livora.Server.Tests.Quality;

// ============================================================================
// PURPOSE: the shared source scanner behind every Wave 4 honesty tripwire. A tripwire must read
//          CODE — never comment prose, never string text — and must SEE the code hiding inside an
//          interpolation hole: $"blocking {task.Result}" is a blocking call even though it sits in
//          a string, while the prose "the task.Result field" must not be flagged. The lane brief
//          forbids fail-open heuristics, so every construct used in this repo is handled
//          explicitly: line comments, block comments (multi-line), plain strings with backslash
//          escapes, verbatim strings with ""-escaping (spanning lines), interpolated strings with
//          {{ }} escapes and nested code holes, char literals (a '"' char must not open a string),
//          and raw string literals (content is opaque text, dropped).
// OWNER: Agent 16 (QA/release gate). Behaviour is pinned by SourceTokenizerSelfTests — change the
//          pin first, and only where the pin was wrong.
// PROVIDES: SourceTokenizer.Scan(path, source) -> ScannedFile (per-line code projections, literals).
// INVARIANTS:
//   - Lines.Count == number of '\n'-separated lines of the normalised input (POSITION FIDELITY: a
//     violation reports its real file:line; nothing is dropped, merged, or renumbered)
//   - CodeText holds every character the compiler parses as code, with string CONTENT removed and
//     interpolation-hole expressions PRESENT (they really are code)
//   - every unresolvable construct (unterminated string at EOL) falls back to "back to code":
//     the bias is a visible false positive, never a silent miss
// DOCUMENTED LIMITS (honest, narrow, pinned or reported):
//   - holes inside RAW interpolated strings are not parsed as code (raw content is dropped); the
//     tripwire reports any raw interpolated literal it sees so a reviewer can't miss the addition
//   - a ':' inside an interpolation hole is kept AS code (format specifiers and ternary arms both
//     flow through) — that can only produce false positives (a format token matching a word
//     pattern), never a miss; literals still carry their text for a reviewer
//   - a nested string inside a hole is scanned as a real string (its content leaves the code view)
// ============================================================================

/// <summary>A scanned source file: position-faithful per-line code projections + all literals.</summary>
public sealed class ScannedFile
{
    internal ScannedFile(string repoRelativePath, IReadOnlyList<ScannedLine> lines,
        IReadOnlyList<StringLiteral> literals, string code)
    {
        RepoRelativePath = repoRelativePath;
        Lines = lines;
        Literals = literals;
        Code = code;
    }

    /// <summary>The path as handed to <see cref="SourceTokenizer.Scan"/>; verbatim in violations.</summary>
    public string RepoRelativePath { get; }

    /// <summary>1-based, one entry per source line, position-faithful.</summary>
    public IReadOnlyList<ScannedLine> Lines { get; }

    /// <summary>Every string literal in the file (char literals excluded — they carry no words).</summary>
    public IReadOnlyList<StringLiteral> Literals { get; }

    /// <summary>All CodeText joined with '\n'; line numbers match <see cref="Lines"/>.</summary>
    public string Code { get; }

    /// <summary>1-based source line containing the given <see cref="Code"/> offset.</summary>
    public int LineOf(int codeOffset) =>
        Code.AsSpan(0, Math.Clamp(codeOffset, 0, Code.Length)).Count('\n') + 1;

    public override string ToString() => RepoRelativePath;
}

/// <summary>One line. <param name="Number">1-based line number.</param>
/// <param name="CodeText">the code-only projection (string content removed, hole code kept).</param></summary>
public sealed record ScannedLine(int Number, string CodeText);

/// <summary>
/// One string literal occurrence.
/// <param name="Line">1-based line where the literal opens.</param>
/// <param name="Text">literal text; each interpolation hole appears as <see cref="SourceTokenizer.HoleMarker"/>
/// so literal text can never accidentally read as code</param>
/// <param name="IsInterpolated">true when at least one hole was present</param>
/// <param name="HoleCode">the holes' expressions concatenated — this IS code, evaluated per value</param>
/// <param name="IsRaw">true for raw string literals (content kept verbatim, holes unparsed)</param>
/// </summary>
public sealed record StringLiteral(int Line, string Text, bool IsInterpolated, string HoleCode, bool IsRaw);

public static class SourceTokenizer
{
    /// <summary>Sentinel inside <see cref="StringLiteral.Text"/> marking one interpolation hole.</summary>
    public const char HoleMarker = '\u0001';

    public static ScannedFile Scan(string repoRelativePath, string source)
        => new Scanner(repoRelativePath, source).Run();

    private sealed class Scanner
    {
        private enum Kind { Code, Hole, Str, Raw }

        private sealed class Frame
        {
            public Kind Kind;
            public bool Verbatim;
            public bool Interpolated;
            public int RawLen;
            public int Braces;            // nested '{' depth while inside a Hole frame
            public int StartLine;         // 1-based line the literal opened on
            public readonly StringBuilder Text = new();
            public readonly StringBuilder HoleCode = new();
        }

        private readonly string _path;
        private readonly string _s;
        private readonly Stack<Frame> _stack = new();
        private readonly StringBuilder _cur = new();       // current source line's code text
        private readonly List<string> _lineCodes = [];
        private readonly List<StringLiteral> _literals = [];
        private int _i;
        private int _line;                                 // 0-based current source line

        internal Scanner(string path, string source)
        {
            _path = path;
            _s = source.Replace("\r\n", "\n").Replace('\r', '\n');
            _stack.Push(new Frame { Kind = Kind.Code });
        }

        private Frame Top => _stack.Peek();
        private char At(int idx) => idx < _s.Length ? _s[idx] : '\0';

        public ScannedFile Run()
        {
            while (_i < _s.Length)
            {
                if (At(_i) == '\n')
                {
                    if (Top.Kind == Kind.Raw && RawClosesOnNextLine(out int closeEnd))
                    {
                        // The closing delimiter is the first non-whitespace quote-run of the NEXT
                        // line: end this content line (position fidelity), then resume in code
                        // right after the quotes — `""");` leaves `);` as code on that same line.
                        var closer = Top;
                        EmitLiteral(closer);
                        _stack.Pop();
                        FlushLine();
                        _i = closeEnd;
                        continue;
                    }
                    if (Top.Kind == Kind.Str && !Top.Verbatim)
                    {
                        // unterminated plain string at EOL: close at the edge. The file is malformed
                        // for the compiler too; a tripwire must never lose the rest of the file.
                        EmitLiteral(Top);
                        _stack.Pop();
                    }
                    FlushLine();
                    continue;
                }
                switch (Top.Kind)
                {
                    case Kind.Code:
                    case Kind.Hole:
                        ScanCode();
                        break;
                    case Kind.Str:
                        ScanString();
                        break;
                    default:
                        ScanRaw();
                        break;
                }
            }
            if (_cur.Length > 0) FlushLine();

            var lines = new List<ScannedLine>(_lineCodes.Count);
            var code = new StringBuilder();
            for (int n = 0; n < _lineCodes.Count; n++)
            {
                lines.Add(new ScannedLine(n + 1, _lineCodes[n]));
                if (n > 0) code.Append('\n');
                code.Append(_lineCodes[n]);
            }
            return new ScannedFile(_path, lines, _literals, code.ToString());
        }

        private void FlushLine()
        {
            _lineCodes.Add(_cur.ToString());
            _cur.Clear();
            _line++;
            _i++; // consume the newline
        }

        // ================= code + interpolation holes =================
        private void ScanCode()
        {
            char c = At(_i);
            char n = At(_i + 1);

            if (c == '/' && n == '/')
            {
                while (_i < _s.Length && _s[_i] != '\n') _i++;
                return;
            }
            if (c == '/' && n == '*')
            {
                _i += 2;
                while (_i < _s.Length)
                {
                    if (At(_i) == '\n') { FlushLine(); continue; }
                    if (At(_i) == '*' && At(_i + 1) == '/') { _i += 2; return; }
                    _i++;
                }
                _i = _s.Length;
                return;
            }
            if (c == '\'')
            {
                int j = _i + 1;
                while (j < _s.Length && _s[j] != '\'' && _s[j] != '\n')
                    j += _s[j] == '\\' ? 2 : 1;
                if (j < _s.Length && _s[j] == '\'')
                {
                    Emit("'c'");           // placeholder: keeps token edges honest, adds no words
                    _i = j + 1;
                    return;
                }
                Emit("'");
                _i++;
                return;
            }

            if (Top.Kind == Kind.Hole)
            {
                if (c == '{') { Top.Braces++; Emit("{"); _i++; return; }
                if (c == '}')
                {
                    if (Top.Braces == 0) { _stack.Pop(); _i++; return; } // back into the string frame
                    Top.Braces--;
                    Emit("}");
                    _i++;
                    return;
                }
                // ':' flows through as code on purpose: format specifiers are a false-positive
                // risk (acceptable), ternary arms are a false-NEGATIVE risk (not acceptable).
            }

            if ((c == '@' || c == '$') && (n == '"' || n == '$' || n == '@'))
            {
                if (TryOpenStringLiteral()) return;
                Emit(c.ToString());
                _i++;
                return;
            }
            if (c == '"')
            {
                if (TryOpenStringLiteral()) return;
                Emit("\"");
                _i++;
                return;
            }

            Emit(c.ToString());
            _i++;
        }

        // ================= literal openers =================
        private bool TryOpenStringLiteral()
        {
            bool verbatim = false, interpolated = false;
            int q = _i;
            // consume any mix of @ and $ prefixes (@" $" @"$ "$@ are all real shapes in C#)
            while (true)
            {
                if (At(q) == '@' && !verbatim) { verbatim = true; q++; continue; }
                if (At(q) == '$' && !interpolated) { interpolated = true; q++; continue; }
                break;
            }
            if (At(q) != '"') return false;

            int run = 0;
            while (At(q + run) == '"') run++;
            if (run >= 3)
            {
                var f = new Frame
                {
                    Kind = Kind.Raw,
                    Verbatim = verbatim,
                    Interpolated = interpolated,
                    RawLen = run,
                    StartLine = _line + 1,
                };
                _stack.Push(f);
                _i = q + run;
                TryCloseInlineRaw(f);
                return true;
            }

            _stack.Push(new Frame
            {
                Kind = Kind.Str,
                Verbatim = verbatim,
                Interpolated = interpolated,
                StartLine = _line + 1,
            });
            _i = q + 1;
            return true;
        }

        private void TryCloseInlineRaw(Frame f)
        {
            int i = _i;
            while (i < _s.Length && _s[i] != '\n')
            {
                if (_s[i] == '"')
                {
                    int run = 0, j = i;
                    while (At(j) == '"') { run++; j++; }
                    if (run == f.RawLen)
                    {
                        f.Text.Append(_s[_i..i]);
                        EmitLiteral(f);
                        _stack.Pop();
                        _i = j;
                        return;
                    }
                    i = j;
                    continue;
                }
                i++;
            }
        }

        /// <summary>True when the NEXT line's first non-whitespace token is a quote run of at least
        /// RawLen — C#'s rule that the closing delimiter is preceded on its line only by
        /// whitespace; what follows it (`;`, `)`, …) is ordinary code. <paramref name="end"/>
        /// receives the index just past the closing quotes.</summary>
        private bool RawClosesOnNextLine(out int end)
        {
            end = _i;
            var f = Top;
            int k = _i + 1;
            while (At(k) == ' ' || At(k) == '\t') k++;
            if (At(k) != '"') return false;
            int run = 0;
            for (int j = k; At(j) == '"'; j++) run++;
            if (run < f.RawLen) return false; // a shorter run is content
            end = k + f.RawLen;               // exactly RawLen quotes close the literal
            return true;
        }

        // ================= string body =================
        private void ScanString()
        {
            var f = Top;
            char c = At(_i);
            char n = At(_i + 1);

            if (f.Verbatim)
            {
                if (c == '"')
                {
                    if (n == '"') { f.Text.Append('"'); _i += 2; return; }
                    EmitLiteral(f);
                    _stack.Pop();
                    _i++;
                    return;
                }
                if (f.Interpolated)
                {
                    if (c == '{')
                    {
                        if (n == '{') { f.Text.Append('{'); _i += 2; return; }
                        f.Text.Append(HoleMarker);
                        OpenHole();
                        return;
                    }
                    if (c == '}' && n == '}') { f.Text.Append('}'); _i += 2; return; }
                }
                if (c == '\n')
                {
                    f.Text.Append('\n');   // verbatim strings span lines; keep line fidelity
                    FlushLine();
                    return;
                }
                f.Text.Append(c);
                _i++;
                return;
            }

            if (c == '\\')
            {
                f.Text.Append(c);
                if (_i + 1 < _s.Length && _s[_i + 1] != '\n') f.Text.Append(_s[_i + 1]);
                _i += 2;
                return;
            }
            if (c == '"')
            {
                EmitLiteral(f);
                _stack.Pop();
                _i++;
                return;
            }
            if (f.Interpolated)
            {
                if (c == '{')
                {
                    if (n == '{') { f.Text.Append('{'); _i += 2; return; }
                    f.Text.Append(HoleMarker);
                    OpenHole();
                    return;
                }
                if (c == '}' && n == '}') { f.Text.Append('}'); _i += 2; return; }
                if (c == '}') { _i++; return; } // stray close brace: ignore the character
            }
            f.Text.Append(c);
            _i++;
        }

        private void OpenHole()
        {
            _i++; // past '{'
            // separate adjacent holes so their expressions never glue into one identifier
            foreach (var fr in _stack)
                if (fr.Kind is Kind.Str or Kind.Raw)
                {
                    if (fr.HoleCode.Length > 0) fr.HoleCode.Append(' ');
                    break;
                }
            _stack.Push(new Frame { Kind = Kind.Hole, StartLine = _line + 1 });
        }

        // ================= raw body (content dropped) =================
        private void ScanRaw()
        {
            var f = Top;
            if (At(_i) == '"')
            {
                int run = 0, j = _i;
                while (At(j) == '"') { run++; j++; }
                if (run == f.RawLen && j < _s.Length && _s[j] == '\n')
                {
                    f.Text.Append(_s[_i..j]);
                    EmitLiteral(f);
                    _stack.Pop();
                    _i = j;
                    return;
                }
                f.Text.Append(_s[_i..j]);   // quote run inside content: keep it as text
                _i = j;
                return;
            }
            int start = _i;
            while (_i < _s.Length && At(_i) != '\n' && At(_i) != '"') _i++;
            f.Text.Append(_s[start.._i]);
        }

        private void Emit(string text)
        {
            _cur.Append(text);
            if (Top.Kind == Kind.Hole)
            {
                // mirror hole code into the enclosing literal's frame so StringLiteral.HoleCode
                // carries the expressions the scanner treated as code
                foreach (var fr in _stack)
                {
                    if (fr.Kind is Kind.Str or Kind.Raw)
                    {
                        fr.HoleCode.Append(text);
                        break;
                    }
                }
            }
        }

        private void EmitLiteral(Frame f) =>
            _literals.Add(new StringLiteral(f.StartLine, f.Text.ToString(), f.Interpolated,
                f.HoleCode.ToString(), f.Kind == Kind.Raw));
    }
}
