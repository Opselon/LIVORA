namespace Livora.Server.Tests.Quality;

/// <summary>
/// PURPOSE: pin the tokenizer semantics every honesty tripwire stands on. If any of these fails,
///          the tripwires are scanning blind and the honesty gate is theatre — this is the one
///          file in the lane where "it compiles" is never the claim: each test asserts what the
///          scanner SAW and what it must NOT have seen.
/// OWNER: Agent 16 (QA/release gate).
/// TESTS: comments never read as code; string content never reads as code; interpolation holes DO
///          read as code; verbatim ""-escaping; backslash escapes; char literals; raw strings
///          (inline and multi-line); nested literals inside holes; malformed-line fallback; and a
///          self-scan proving the scanner does not flag this file's own prose about task.Result.
/// </summary>
public sealed class SourceTokenizerSelfTests
{
    private static ScannedFile S(string source) => SourceTokenizer.Scan("self.cs", source);

    [Fact]
    public void Line_comment_is_not_code()
    {
        var f = S("var x = 1; // task.Result here\nvar y = 2;");
        Assert.DoesNotContain("task.Result", f.Lines[0].CodeText);
        Assert.Contains("var x = 1;", f.Lines[0].CodeText);
        Assert.Equal(2, f.Lines.Count);
    }

    [Fact]
    public void Block_comment_spanning_lines_is_not_code_but_positions_survive()
    {
        var f = S("int a = 1;\n/* doc about task.Result and\n   .Wait() calls\n*/ int b = a.Wait();");
        Assert.Equal(4, f.Lines.Count);
        Assert.DoesNotContain("task.Result", f.Code);
        Assert.DoesNotContain(".Wait()", f.Lines[1].CodeText);
        Assert.Contains("a.Wait()", f.Lines[3].CodeText); // the real code after the comment closes
    }

    [Fact]
    public void Plain_string_content_is_not_code()
    {
        var f = S("Log(\"the task.Result value is wrong\");");
        Assert.DoesNotContain("task.Result", f.Code);
        Assert.Contains("Log(", f.Code);
        var lit = Assert.Single(f.Literals);
        Assert.Equal("the task.Result value is wrong", lit.Text);
    }

    [Fact]
    public void Interpolation_hole_code_IS_visible_and_separated_from_text()
    {
        var f = S("Console.WriteLine($\"value={task.Result} done\");");
        Assert.Contains("task.Result", f.Code);   // the blocking call inside a string must surface
        var lit = Assert.Single(f.Literals);
        Assert.True(lit.IsInterpolated);
        Assert.Contains("task.Result", lit.HoleCode);
        Assert.DoesNotContain("task.Result", lit.Text);
        Assert.Contains(SourceTokenizer.HoleMarker, lit.Text);
    }

    [Fact]
    public void Double_brace_escapes_are_text_not_holes()
    {
        var f = S("var t = $\"literal {{brace}} and {x}\";");
        var lit = Assert.Single(f.Literals);
        Assert.Equal("literal {brace} and \u0001", lit.Text);
        Assert.Equal("x", lit.HoleCode);
    }

    [Fact]
    public void Colon_inside_hole_flows_through_as_code()
    {
        // Design decision (documented in SourceTokenizer's header): a ':' inside a hole is kept AS
        // code instead of ending the hole's code region. Format tokens can trip a word-pattern
        // regex — a visible false positive a reviewer fixes — while a ternary arm hidden after a
        // ':' would be a silent miss, and silent misses are what a tripwire must never do.
        var f = S("Log($\"at {DateTime.UtcNow:yyyy-MM-dd} now\");");
        var lit = Assert.Single(f.Literals);
        Assert.Contains("DateTime.UtcNow", lit.HoleCode);
        Assert.Contains("yyyy", lit.HoleCode);
        Assert.DoesNotContain("DateTime", lit.Text);
    }

    [Fact]
    public void Verbatim_string_content_is_not_code_and_doubled_quotes_unescape()
    {
        var f = S("var p = @\"C:\\files\"\"x\"\"\"; var q = obj.Result;");
        var lit = Assert.Single(f.Literals);
        Assert.Equal("C:\\files\"x\"", lit.Text);
        Assert.Contains(".Result", f.Code); // the code AFTER the verbatim close is still code
    }

    [Fact]
    public void Verbatim_string_spanning_lines_keeps_line_numbers()
    {
        var f = S("int keep = 1;\nvar s = @\"line1\nline2 has .Wait() text\";\nint after = s.Result;");
        Assert.Equal(4, f.Lines.Count);
        Assert.DoesNotContain(".Wait()", f.Code);           // verbatim content is not code
        Assert.DoesNotContain(".Wait()", f.Lines[2].CodeText); // line 3 is still inside the string
        Assert.Contains(".Result", f.Lines[3].CodeText);    // line 4: the real blocking call
    }

    [Fact]
    public void Char_literal_quote_does_not_open_a_string()
    {
        var f = S("char q = '\"'; var secret = task.Result;");
        Assert.DoesNotContain("\"'", f.Code); // the char collapsed to a placeholder
        Assert.Contains("task.Result", f.Code);
    }

    [Fact]
    public void Escape_sequence_backslash_quote_closes_at_the_right_place()
    {
        var f = S("var s = \"a\\\"b.Result\"; var t = other.Result;");
        Assert.DoesNotContain("b", f.Code);       // first literal's content dropped
        Assert.Contains("other.Result", f.Code);  // second blocking call survives
        var lit = Assert.Single(f.Literals);
        Assert.Equal("a\\\"b.Result", lit.Text);
    }

    [Fact]
    public void Multi_line_raw_string_content_is_dropped_and_code_resumes_after_it()
    {
        // built with concatenation so this test file itself does not need a raw literal holding one
        var sample = string.Join("\n",
            "var json = \"\"\"",                                   // var json = """
            "{ \"deadlock\": \"task.Result and .Wait()\" }",       // content line
            "\"\"\";",                                             // """;
            "var live = svc.Result;");
        var f = S(sample);
        Assert.DoesNotContain("deadlock", f.Code);
        Assert.DoesNotContain("task.Result", f.Code); // raw content is opaque text
        Assert.Contains("svc.Result", f.Code);        // code after the raw close is scanned again
    }

    [Fact]
    public void Inline_raw_string_closes_on_its_line()
    {
        var f = S("var one = \"\"\"deadlock .Wait() here\"\"\";\nvar two = svc.Result;");
        Assert.DoesNotContain("deadlock", f.Code);
        Assert.Contains("svc.Result", f.Code);
    }

    [Fact]
    public void Nested_literal_inside_hole_does_not_confuse_the_hole_end()
    {
        var f = S("Log($\"{cond ? \"task.Result\" : other.Result}\");");
        Assert.Contains("other.Result", f.Code);   // false arm is code
        Assert.DoesNotContain("cond ? \"task", f.Code);
    }

    [Fact]
    public void Unterminated_string_falls_back_to_code_not_silence()
    {
        var f = S("var broken = \"oops\nvar later = task.Result;");
        Assert.Contains("task.Result", f.Code); // the rest of the file is still scanned as code
    }

    [Fact]
    public void Verbatim_interpolated_holes_are_code_across_lines()
    {
        var f = S("Log($@\"path {cfg.Result} more\nstill {svc.Wait()} done\");");
        Assert.Contains("cfg.Result", f.Code);
        Assert.Contains("svc.Wait()", f.Code);
    }

    [Fact]
    public void Empty_file_and_trailing_newline_count_lines_exactly()
    {
        Assert.Empty(S("").Lines);
        Assert.Single(S("a").Lines);
        Assert.Equal(2, S("a\nb").Lines.Count);
        Assert.Equal(2, S("a\nb\n").Lines.Count); // no phantom trailing empty line
    }

    [Fact]
    public void LineOf_maps_code_offsets_back_to_real_line_numbers()
    {
        var f = S("a\nb\nc.Result");
        Assert.Equal(3, f.LineOf(f.Code.LastIndexOf("c.Result", StringComparison.Ordinal)));
    }

    [Fact]
    public void Self_scan_finds_no_false_positives_in_this_very_file()
    {
        // Meta-check: a scanner that flags its own prose is not a scanner. This file talks about
        // task.Result / .Wait() constantly — none of it may project into code text.
        var src = File.ReadAllText(RepoPaths.Combine(
            "server/tests/Livora.Server.Tests/Quality/SourceTokenizerSelfTests.cs"));
        var f = SourceTokenizer.Scan("server/tests/Livora.Server.Tests/Quality/SourceTokenizerSelfTests.cs", src);

        foreach (var l in f.Lines)
        {
            Assert.False(l.CodeText.Contains("task.Result", StringComparison.Ordinal)
                         || l.CodeText.Contains(".Wait()", StringComparison.Ordinal),
                $"tokenizer leaked prose into code at line {l.Number}: {l.CodeText}");
        }

        var normalized = src.Replace("\r\n", "\n");
        var expected = normalized.Split('\n').Length - (normalized.EndsWith('\n') ? 1 : 0);
        Assert.Equal(expected, f.Lines.Count);
    }
}
