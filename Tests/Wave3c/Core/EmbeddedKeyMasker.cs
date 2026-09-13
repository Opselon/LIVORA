using System.Security.Cryptography;
using System.Text;

namespace LIVORA.Tests.Wave3c.Core;

/// <summary>
/// The build-time masker for <c>GatewayKeyStore</c>'s embedded blob — the counterparty of the
/// runtime keystream, kept in the TEST project (see the class comment for why it may not live in the
/// app, and why it may not be skipped either).
///
/// WHAT IT IS FOR. <c>GatewayKeyStore</c> stores the built-in gateway credential as base64 of
/// (bytes XOR SHA-256(salt || 1-based block index)). That is obfuscation, not encryption: it keeps
/// the key out of greps, <c>strings</c> dumps and accidental source commits, and the shipped binary
/// still hands it back to anyone who tries. Its ONE guarantee — "the blob in the source file really
/// is this credential, and decoding never silently returns garbage" — is only real if something
/// computes the blob independently of the runtime code. If the runtime produced both the blob and
/// its expected answer, a broken keystream would be self-confirming: decode would return the same
/// wrong bytes and every test would stay green while the gateway 401s.
///
/// So this masker re-derives the blob from the credential and asserts equality with the blob
/// compiled into <c>GatewayKeyStore</c> (<see cref="GatewayKeyBlobTests"/>), and the runtime decoder
/// is round-tripped through it. It is the only place in the repo that ever holds the plaintext, and
/// it holds it as the SAME obfuscated blob (never as a literal) plus the environment variable the
/// developer already exported to use the gateway. That keeps the plaintext-key tripwire meaningful:
/// no test source, fixture or resource contains the credential.
///
/// NEVER SHIP THIS CLASS: it lives under <c>Tests/</c>, which the app project excludes from
/// <c>Compile</c>, so it cannot reach a device build.
/// </summary>
internal static class EmbeddedKeyMasker
{
    /// <summary>Must equal <c>GatewayKeyStore.MaskSalt</c> — pinned by test, because a silent drift
    /// between the two keystreams is the bug this whole file exists to catch.</summary>
    internal const string MaskSalt = "LIVORA::gateway::wave3c::v1";

    /// <summary>The environment variable holding the built-in gateway credential on a dev machine
    /// (the same one the Hermes config uses for this endpoint). Never a literal fallback.</summary>
    internal const string KeyEnvVariable = "HERMES_CUSTOM_SUB_LEGOTEN_COM_4455_API_KEY";

    /// <summary>SHA-256(salt || block) blocks concatenated (1-based index), truncated to length.</summary>
    internal static byte[] Keystream(int length)
    {
        var salt = Encoding.ASCII.GetBytes(MaskSalt);
        var output = new byte[length];
        int pos = 0;
        for (int block = 1; pos < length; block++)
        {
            var seed = new byte[salt.Length + 1];
            Buffer.BlockCopy(salt, 0, seed, 0, salt.Length);
            seed[^1] = (byte)block;
            var hash = SHA256.HashData(seed);
            var take = Math.Min(hash.Length, length - pos);
            new ReadOnlySpan<byte>(hash, 0, take).CopyTo(new Span<byte>(output, pos, take));
            pos += take;
        }
        return output;
    }

    /// <summary>base64(XOR(UTF8(credential), keystream)) — the exact blob form GatewayKeyStore embeds.</summary>
    internal static string MaskToBlob(string credential)
    {
        var bytes = Encoding.UTF8.GetBytes(credential);
        var mask = Keystream(bytes.Length);
        for (int i = 0; i < bytes.Length; i++) bytes[i] ^= mask[i];
        var blob = Convert.ToBase64String(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return blob;
    }

    /// <summary>The credential from the developer environment, or null when it is not exported
    /// (CI without the secret → the equality test skips honestly instead of passing vacuously).</summary>
    internal static string? CredentialFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(KeyEnvVariable);
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }
}
