using System.Text;
using LIVORA.Application.Abstractions;
using LIVORA.Domain.Enums;
using LIVORA.Infrastructure.Persistence;
using LIVORA.Infrastructure.Security;
using LIVORA.Infrastructure.Security.Gateway;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// Shared scaffolding for the wave 3c lane 01 suites: a throw-away data directory per test (so the
/// JSON stores — which are the real classes under test — never collide), plus the fakes the
/// security stack needs:
/// <list type="bullet">
///   <item><see cref="FakeSecureBox"/> — a byte-inverting box whose ciphertext is guaranteed to
///     differ from its input, which is what lets the "no plaintext on disk" scan be a REAL test in
///     the plain-net10 head instead of a claim about DPAPI the head cannot exercise. It also counts
///     calls so "nothing is logged / nothing is read" is measurable.</item>
///   <item><see cref="RecordingSecureStorage"/> — an in-memory <see cref="ISecureStorageService"/>
///     used by the gateway/passcode tests that do not care about bytes.</item>
///   <item><see cref="FakeGatewayConfig"/>-style helpers for building configs without a network:
///     lane 01 never touches the gateway endpoint (the brief reserves the network for the probe lane).</item>
/// </list>
/// Everything here is test-only: the app project excludes <c>Tests\**</c> from compilation.
/// </summary>
internal static class Lane01Harness
{
    /// <summary>A fresh empty directory the caller must delete (tests use <c>using</c> on it).</summary>
    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir(string tag)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "livora_w3c_lane01",
                tag + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Root => System.IO.Path.Combine(Path, "LIVORA");
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    internal static LocalJsonStore RootStore(TempDir dir) => new(dir.Root);
    internal static LocalJsonStore SubStore(TempDir dir, string sub) =>
        new(System.IO.Path.Combine(dir.Root, sub));

    /// <summary>The credential, decoded the only supported way. Null when the blob is corrupt.</summary>
    internal static string? EmbeddedKey() => GatewayKeyStore.GetEmbeddedKey();

    /// <summary>True when <paramref name="haystack"/> contains <paramref name="needle"/> as UTF-8,
    /// UTF-16LE (a UTF-16 file would otherwise hide it from a byte scan) or a UTF-8 BOM'd prefix.</summary>
    internal static bool ContainsPlaintext(byte[] haystack, string needle)
    {
        if (needle.Length == 0) return false;
        return IndexOfBytes(haystack, Encoding.UTF8.GetBytes(needle)) >= 0
            || IndexOfBytes(haystack, Encoding.Unicode.GetBytes(needle)) >= 0
            || IndexOfBytes(haystack, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(needle)).ToArray()) >= 0;
    }

    internal static int IndexOfBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;
        // Naive scan is fine: these files are KBs, and correctness beats cleverness in a tripwire.
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }
}

/// <summary>
/// A byte-inverting at-rest box. NOT encryption and not claimed as such: it exists so the test head
/// can prove the two things that actually matter about <see cref="SecureStorageService"/> — the
/// persisted bytes differ from the value (no plaintext on disk) and the round-trip is lossless.
/// </summary>
internal sealed class FakeSecureBox : IPlatformSecureBox
{
    public const string FakeLabel = "fake-inverting-box";

    public string Label => FakeLabel;
    public bool IsHardwareOrOsBacked => false;

    /// <summary>Call counters — so a test can prove e.g. "nothing was read while building status".</summary>
    public int ProtectCalls;
    public int UnprotectCalls;

    public byte[] Protect(byte[] plaintext)
    {
        ProtectCalls++;
        var output = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++) output[i] = (byte)(~plaintext[i]);
        return output;
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        UnprotectCalls++;
        var output = new byte[ciphertext.Length];
        for (int i = 0; i < ciphertext.Length; i++) output[i] = (byte)(~ciphertext[i]);
        return output;
    }
}

/// <summary>An OS-backed box (used to prove the capability flag is a read-through, not a guess).</summary>
internal sealed class FakeOsSecureBox : IPlatformSecureBox
{
    public string Label => "fake-dpapi";
    public bool IsHardwareOrOsBacked => true;
    public byte[] Protect(byte[] plaintext)
    {
        var o = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++) o[i] = (byte)(plaintext[i] ^ 0xA5);
        return o;
    }
    public byte[] Unprotect(byte[] ciphertext) => Protect(ciphertext);
}

/// <summary>In-memory secret store with a plain dictionary — for tests about OTHER classes.</summary>
internal sealed class RecordingSecureStorage : ISecureStorageService
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Raw => _map;
    public List<string> Writes { get; } = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_map.TryGetValue(key, out var v) ? v : null);

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Writes.Add(key);
        _map[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _map.Remove(key);
        return Task.CompletedTask;
    }
}

/// <summary>A clock the lockout/probe tests can wind by hand.</summary>
internal sealed class FakeClock
{
    public DateTime Now { get; set; } = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
    public DateTime UtcNow() => Now;
    public void Advance(TimeSpan by) => Now = Now.Add(by);
}
