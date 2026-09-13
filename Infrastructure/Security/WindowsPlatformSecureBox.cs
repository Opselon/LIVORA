using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace LIVORA.Infrastructure.Security;

/// <summary>
/// The Windows at-rest box: DPAPI (<c>crypt32!CryptProtectData</c>) scoped to the CURRENT USER —
/// the stronger path the gateway ADR points at for USER-entered secrets. Whole file is compiled
/// only under <c>#if WINDOWS</c>, so the plain-net10 test head never sees it (and the test head can
/// never accidentally claim DPAPI it does not have).
///
/// Implementation notes (why P/Invoke instead of <c>System.Security.Cryptography.ProtectedData</c>):
/// the app forbids new NuGet packages, and that wrapper ships as a separate package outside the
/// shared framework — so the two crypt32 entry points are declared directly here. Nothing else
/// changes: same API, same scope, same memory ownership rules (the returned buffer is freed with
/// <see cref="LocalFree"/>, which is what crypt32 documents).
///
/// Guarantees and NON-guarantees, stated honestly:
/// <list type="bullet">
///   <item>the blob is key-derived from the logged-on user's credentials and is readable only by
///     that same user (and, by design, LOCAL SYSTEM). Another Windows account on the same machine
///     cannot decrypt it.</item>
///   <item>it is NOT a hardware-backed guarantee on every device (no TPM binding is requested) and
///     it is NOT post-quantum / not anti-malware. It is an OS user-scoped boundary: strictly
///     stronger than a plain JSON value, weaker than a secure element.</item>
///   <item>process scope, not domain scope: a roaming profile will not carry a readable blob.</item>
/// </list>
/// Plaintext given to <c>Protect</c> is never logged and never written as-is.
/// </summary>
#if WINDOWS
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformSecureBox : IPlatformSecureBox
{
    /// <summary>Machine tag surfaced by <see cref="SecureStorageService.PlatformLabel"/>.</summary>
    public const string WindowsLabel = "dpapi-current-user";

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public string Label => WindowsLabel;
    public bool IsHardwareOrOsBacked => true;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, out string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        IntPtr inPtr = Marshal.AllocHGlobal(Math.Max(plaintext.Length, 1));
        try
        {
            Marshal.Copy(plaintext, 0, inPtr, plaintext.Length);
            var input = new DATA_BLOB { cbData = plaintext.Length, pbData = inPtr };
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output) || output.pbData == IntPtr.Zero)
                throw new CryptographicException(
                    "DPAPI CryptProtectData failed (0x" + Marshal.GetLastWin32Error().ToString("X8") + ").");
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }

    public byte[] Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        IntPtr inPtr = Marshal.AllocHGlobal(Math.Max(ciphertext.Length, 1));
        try
        {
            Marshal.Copy(ciphertext, 0, inPtr, ciphertext.Length);
            var input = new DATA_BLOB { cbData = ciphertext.Length, pbData = inPtr };
            if (!CryptUnprotectData(ref input, out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CRYPTPROTECT_UI_FORBIDDEN, out var output) || output.pbData == IntPtr.Zero)
                throw new CryptographicException(
                    "DPAPI CryptUnprotectData failed (0x" + Marshal.GetLastWin32Error().ToString("X8") + ").");
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }
}
#endif
