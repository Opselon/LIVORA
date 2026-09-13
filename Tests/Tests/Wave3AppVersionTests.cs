using LIVORA.Application.Abstractions;

namespace LIVORA.Tests.Tests;

/// <summary>
/// AppVersion is the arithmetic the update banner is built on, so it owns the honesty surface:
/// a wrong comparison either nags the user for no reason or hides a real update. Every branch in
/// the contract comment is covered here, including the "no comparison possible" null that callers
/// must never launder into "up to date".
/// </summary>
public class AppVersionTests
{
    // ---- TryParse ----------------------------------------------------------

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.0")]
    [InlineData("1.10")]
    [InlineData("v1.2")]
    [InlineData("V1.2")]
    [InlineData("1.2.3-rc.4")]
    [InlineData("  1.2  ")]
    [InlineData("2")]
    [InlineData("1.2.3.4")]
    public void TryParse_AcceptsDocumentedShapes(string text)
        => Assert.True(AppVersion.TryParse(text, out _), $"should parse: {text}");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4.5")]        // more than four parts is refused, not truncated
    [InlineData("1.x")]              // a part with no leading digits is refused
    [InlineData(".")]
    [InlineData("x1.2")]             // trim only strips a leading v/V
    public void TryParse_RejectsUnparseableShapes(string? text)
        => Assert.False(AppVersion.TryParse(text, out _), $"should not parse: {text}");

    [Fact]
    public void TryParse_MissingParts_DefaultToZero()
    {
        Assert.True(AppVersion.TryParse("1.2", out var v));
        Assert.Equal(new Version(1, 2, 0, 0), v);
    }

    [Fact]
    public void TryParse_PrereleaseSuffix_IsIgnoredForTheNumber()
    {
        Assert.True(AppVersion.TryParse("1.2.3-rc.4", out var v));
        Assert.Equal(new Version(1, 2, 3, 0), v);
    }

    [Fact]
    public void TryParse_TrailingNonNumericMarker_IsTolerated()
    {
        // Documented tolerance: "3beta" reads as 3.
        Assert.True(AppVersion.TryParse("1.2.3beta", out var v));
        Assert.Equal(3, v.Build);
    }

    [Fact]
    public void TryParse_OnFailure_OutValueIsSafeZeroVersion()
    {
        Assert.False(AppVersion.TryParse("nope", out var v));
        Assert.NotNull(v);
        Assert.Equal(new Version(0, 0), v);
    }

    [Fact]
    public void TryParse_LargeComponents_DoNotOverflow()
    {
        Assert.True(AppVersion.TryParse("65535.65535.65535.65535", out var v));
        Assert.Equal(65535, v.Major);
    }

    // ---- Compare ----------------------------------------------------------

    [Fact]
    public void Compare_OlderInstalled_ReturnsMinusOne()
        => Assert.Equal(-1, AppVersion.Compare("1.1", "1.2"));

    [Fact]
    public void Compare_EqualAcrossMissingParts()
        => Assert.Equal(0, AppVersion.Compare("1.2", "1.2.0"));

    [Fact]
    public void Compare_NewerInstalled_ReturnsOne()
        => Assert.Equal(1, AppVersion.Compare("1.3", "1.2"));

    [Fact]
    public void Compare_MajorBeatsMinor()
        => Assert.Equal(-1, AppVersion.Compare("1.99", "2.0"));

    [Fact]
    public void Compare_TwoDigitMinorIsNotLexical()
    {
        // "1.10" must beat "1.9" — the string compare trap the release feed actually walks into.
        Assert.Equal(1, AppVersion.Compare("1.10", "1.9"));
    }

    [Fact]
    public void Compare_BuildAndRevisionParticipate()
    {
        Assert.Equal(-1, AppVersion.Compare("1.2.3", "1.2.4"));
        Assert.Equal(-1, AppVersion.Compare("1.2.3.1", "1.2.3.2"));
    }

    [Theory]
    [InlineData(null, "1.2")]
    [InlineData("1.2", null)]
    [InlineData("", "1.2")]
    [InlineData("1.2", "")]
    [InlineData("garbage", "1.2")]
    [InlineData("1.2", "garbage")]
    [InlineData(null, null)]
    public void Compare_UnparseableEitherSide_IsNull_NotZero(string? installed, string? feed)
    {
        // Null means "no comparison possible". A zero here would render as "You're up to date"
        // on top of a feed we could not read — the exact over-claim the product law forbids.
        Assert.Null(AppVersion.Compare(installed, feed));
    }

    [Fact]
    public void Compare_PrereleaseTagDoesNotLowerTheNumber()
    {
        // Deliberate (and documented in the contract): "1.2.0-rc.1" == "1.2.0".
        // Callers must surface IsPrerelease separately rather than pretend to sort tags.
        Assert.Equal(0, AppVersion.Compare("1.2.0-rc.1", "1.2.0"));
    }

    [Fact]
    public void Compare_IsAntisymmetric()
    {
        foreach (var (a, b) in new[] { ("1.0", "1.1"), ("1.0", "1.0"), ("2.0.1", "2.0") })
        {
            var ab = AppVersion.Compare(a, b);
            var ba = AppVersion.Compare(b, a);
            Assert.Equal(ab, ba is null ? null : -ba);
        }
    }
}
