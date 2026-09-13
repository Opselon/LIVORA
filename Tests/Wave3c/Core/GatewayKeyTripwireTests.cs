using System.Reflection;
using LIVORA.Infrastructure.Security.Gateway;
using LIVORA.Tests.Tests;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// THE KEY TRIPWIRE. Wave 3c's one non-negotiable rule about the built-in gateway credential: it
/// may exist in the source tree in exactly one representation — the obfuscated blob inside
/// <see cref="GatewayKeyStore"/> — and never as plaintext. A plaintext key in the tree is not a
/// style problem: this repo is merged by ten lanes at once, leaves as a patch file, and its history
/// outlives the endpoint. So these tests scan.
///
/// What is checked:
/// <list type="bullet">
///   <item>every file under the repo root (bin/obj/.vs/TestResults excluded, exactly like the patch
///     protocol) is read as BYTES and searched for the decoded credential in UTF-8 and UTF-16LE — a
///     source file, resx, keys manifest, README or fixture that carries it fails here, whatever
///     extension it hides behind;</item>
///   <item>no file may contain BOTH ends of the credential (the cheap split-and-concatenate smuggle);</item>
///   <item>the blob literal appears in AT MOST the one file that owns it, so a copy-paste of the
///     blob into a second "helper" cannot split the decode path across the tree;</item>
///   <item>the runtime keystream is pinned byte-for-byte against the independent build-time masker
///     (<see cref="EmbeddedKeyMasker"/>), so the blob cannot silently drift into something that
///     decodes to garbage.</item>
/// </list>
/// HONEST LIMIT: no test in this file can prove the credential is currently ACCEPTED by the gateway
/// — that is the probe lane's live check. Lane 01 pins the decode CONTRACT (shape, single source,
/// no plaintext anywhere), which is the part a merge can break silently.
///
/// Where the plaintext comes from for the scan: <see cref="GatewayKeyStore"/> decodes it at runtime.
/// Nothing in this file contains it — the value is a local variable only, so the scanner's own
/// source cannot be the leak it hunts.
/// </summary>
public class GatewayKeyTripwireTests
{
    private static readonly string? Root = Lane01Repo.Root;

    /// <summary>Folders whose contents are build output, not source.</summary>
    private static readonly string[] ExcludedDirs = { "bin", "obj", ".vs", ".git", "TestResults" };

    private static string? DecodedCredential() => GatewayKeyStore.GetEmbeddedKey();

    [Fact]
    public void EmbeddedBlob_DecodesToTheShapeAGatewayKeyMustHave()
    {
        var key = DecodedCredential();
        Assert.NotNull(key);
        Assert.True(key!.Length >= 8, "decoder accepted a too-short blob");
        Assert.StartsWith("sk-", key, StringComparison.Ordinal);
        Assert.DoesNotContain(' ', key);
        Assert.All(key, c => Assert.True(c >= 0x21 && c <= 0x7E, "credential must be printable ASCII"));
    }

    [Fact]
    public void NoFileInTheDocument_ContainsThePlaintextCredential()
    {
        if (Root is null) return;   // source tree not next to the binaries — cannot claim, skip
        var key = DecodedCredential();
        Assert.NotNull(key);

        var offenders = new List<string>();
        foreach (var file in SourceFiles())
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (Lane01Harness.ContainsPlaintext(bytes, key!))
                offenders.Add(Path.GetRelativePath(Root!, file));
        }
        Assert.True(offenders.Count == 0,
            "plaintext gateway credential found in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NeitherResourceFile_NorAnySplitOfTheCredential_AppearsInTheDocument()
    {
        // Two cheap smuggles the whole-file scan would still catch only by accident: a key pasted
        // into a translatable resource, and a key split in two halves that are joined at runtime.
        if (Root is null) return;
        var key = DecodedCredential();
        Assert.NotNull(key);

        foreach (var resx in new[]
                 {
                     Path.Combine(Root, "Resources", "Localization", "AppResources.resx"),
                     Path.Combine(Root, "Resources", "Localization", "AppResources.fa.resx"),
                 })
            if (File.Exists(resx))
                Assert.False(Lane01Harness.ContainsPlaintext(File.ReadAllBytes(resx), key!),
                    Path.GetFileName(resx) + " carries the credential");

        var head = key!.Substring(0, 8);
        var tail = key!.Substring(key!.Length - 8);
        foreach (var file in SourceFiles())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception) { continue; }
            Assert.False(text.Contains(head, StringComparison.Ordinal) && text.Contains(tail, StringComparison.Ordinal),
                $"{Path.GetRelativePath(Root!, file)} contains both ends of the credential");
        }
    }

    [Fact]
    public void TheObfuscatedBlob_LivesInExactlyTheFileThatOwnsIt()
    {
        if (Root is null) return;
        var blob = BlobOf(typeof(GatewayKeyStore));
        Assert.False(string.IsNullOrEmpty(blob));

        var holders = new List<string>();
        foreach (var file in SourceFiles())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception) { continue; }
            if (text.Contains(blob!, StringComparison.Ordinal))
                holders.Add(Path.GetRelativePath(Root!, file));
        }
        Assert.Equal(new[] { Path.Combine("Infrastructure", "Security", "Gateway", "GatewayKeyStore.cs") }, holders);
    }

    [Fact]
    public void OnlyTheKeyStore_KnowsTheMaskSalt()
    {
        // The decode seam must stay single: a second file re-implementing the keystream is a second
        // place the key can leak from and a second thing to rotate.
        var offenders = Wave3Harness.Sources("Infrastructure")
            .Concat(Wave3Harness.Sources("Application"))
            .Concat(Wave3Harness.Sources("Presentation"))
            .Where(s => Path.GetFileName(s.Path) != "GatewayKeyStore.cs"
                        && s.Source.Contains("MaskSalt", StringComparison.Ordinal))
            .Select(s => Path.GetFileName(s.Path))
            .ToList();
        Assert.True(offenders.Count == 0, "duplicate keystream/secret path in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void RuntimeKeystream_MatchesTheIndependentMaskerByteForByte()
    {
        // The anti-drift pin: the app's private keystream and the build-time masker's must be the
        // same function, or the shipped blob decodes to noise and every caller sees "not configured".
        var runtime = (Func<int, byte[]>)GatewayRuntimeKeystream();
        foreach (var len in new[] { 1, 16, 31, 32, 33, 64, 97 })
            Assert.Equal(EmbeddedKeyMasker.Keystream(len), runtime(len));
    }

    [Fact]
    public void Blob_RoundTripsThroughTheIndependentMasker()
    {
        var key = DecodedCredential();
        Assert.NotNull(key);
        Assert.Equal(BlobOf(typeof(GatewayKeyStore)), EmbeddedKeyMasker.MaskToBlob(key!));
    }

    [Fact]
    public void TryDecode_FillsAZeroAbleBuffer_AndClearingItReallyClearsIt()
    {
        // The decode path's memory contract: the caller gets an array it CAN zero (a string cannot
        // be zeroed), and zeroing works — this is what "decoded only at use time" means in practice.
        Assert.True(GatewayKeyStore.TryDecodeToBuffer(out var buf));
        Assert.True(buf.Length >= 8);
        Assert.NotEqual('\0', buf[0]);
        Array.Clear(buf, 0, buf.Length);
        Assert.All(buf, b => Assert.Equal('\0', b));
    }

    [Fact]
    public void TheBriefKeyNeverAppearsAsAStringLiteralInAnySourceFile()
    {
        if (Root is null) return;
        // Literals are the leak vector a reviewer sees too late: a quoted key inside a comment is
        // still a quoted key in git. Comment-stripped scan + raw scan of every *.cs, both directions.
        foreach (var (path, source) in Wave3Harness.Sources("."))
        {
            foreach (var literal in Wave3Harness.Literals(source))
                Assert.False(literal.StartsWith("sk-", StringComparison.Ordinal) && literal.Length > 20,
                    $"{Path.GetRelativePath(Root, path)} holds a key-shaped literal");
            // A quoted "sk-" prefix is only a leak when the rest is key-shaped (long, no spaces);
            // the tripwire's own Assert.StartsWith("sk-") must not trip it.
            Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex("\"sk-[A-Za-z0-9._\\-]{16,}"), source);
        }
    }

    // ---- helpers ---------------------------------------------------------------

    private static IEnumerable<string> SourceFiles()
    {
        if (Root is null) return Array.Empty<string>();
        return Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(f => !ExcludedDirs.Any(d => f.Contains(Path.DirectorySeparatorChar + d + Path.DirectorySeparatorChar)))
            .Where(f => new FileInfo(f).Length < 20L * 1024 * 1024);
    }

    /// <summary>Invoke the keystore's private keystream function (test-only access to the exact
    /// shipped implementation — no copy of the algorithm in this file).</summary>
    private static object GatewayRuntimeKeystream()
    {
        var method = typeof(GatewayKeyStore).GetMethod(
            "Keystream", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (int length) => (byte[])method!.Invoke(null, new object[] { length })!;
    }

    /// <summary>The private <c>Blob</c> constant, read by reflection so this test file never has to
    /// carry the obfuscated value in its own source either.</summary>
    private static string? BlobOf(Type type) =>
        type.GetField("Blob", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
}

/// <summary>Locates the repo root the same way the wave 3 harness does, as its own copy so the
/// tripwire does not depend on a shared helper being moved.</summary>
internal static class Lane01Repo
{
    public static readonly string? Root = Find();

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LIVORA.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
