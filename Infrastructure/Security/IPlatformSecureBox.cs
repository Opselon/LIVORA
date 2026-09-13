namespace LIVORA.Infrastructure.Security;

/// <summary>
/// The one platform seam <see cref="SecureStorageService"/> needs: turn a byte array into bytes
/// that are useless to anybody else, and back again. Everything above this seam (framing,
/// fingerprinting, file IO, the honest capability flag) is platform-free and unit-tested against a
/// fake box, so the test build never has to trust a platform.
///
/// Two implementations exist and they differ in exactly one honest way:
/// <list type="bullet">
///   <item><b>Windows</b> (<c>WindowsPlatformSecureBox</c>, compiled under <c>#if WINDOWS</c>):
///     DPAPI <c>ProtectedData.Protect(CurrentUser)</c> — a real at-rest boundary per user account,
///     so <see cref="SecureStorageService.IsPlatformHardwareBacked"/> is true there.</item>
///   <item><b>everywhere else</b> (<c>PrivateFileSecureBox</c>): NO cipher at all. Bytes are handed
///     straight back and persisted inside the app's private data directory, which is the actual
///     (and only) protection being claimed. <see cref="SecureStorageService.IsPlatformHardwareBacked"/>
///     stays false so the UI can never say "encrypted" about this path. Android keystore / iOS
///     keychain is the stronger path for those heads and is a future-wave swap behind this seam.</item>
/// </list>
/// Implementations must never log the plaintext they are given.
/// </summary>
public interface IPlatformSecureBox
{
    /// <summary>Machine tag reported to the UI/telemetry-free status surfaces.</summary>
    string Label { get; }

    /// <summary>True only when <see cref="Protect"/> establishes a real at-rest cryptographic boundary.</summary>
    bool IsHardwareOrOsBacked { get; }

    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>
/// The non-Windows / test-build box: identity transform plus a "this is not encryption" label.
/// It is <i>deliberately</i> not a cipher — a home-rolled XOR "encryption" on this path would let
/// the app claim a protection it cannot deliver, which the honesty law forbids. The only thing
/// standing between the bytes and another app is the private app-data directory they live in.
/// </summary>
public sealed class PrivateFileSecureBox : IPlatformSecureBox
{
    public const string MachineLabel = "private-app-dir-no-cipher";

    public string Label => MachineLabel;
    public bool IsHardwareOrOsBacked => false;

    public byte[] Protect(byte[] plaintext) => (byte[])plaintext.Clone();
    public byte[] Unprotect(byte[] ciphertext) => (byte[])ciphertext.Clone();
}
