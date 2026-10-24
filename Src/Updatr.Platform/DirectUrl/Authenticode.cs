using System.Runtime.InteropServices;

namespace Updatr.Platform.DirectUrl;

/// <summary>The result of checking a file's Authenticode signature.</summary>
/// <param name="Trusted">Windows accepts the signature and its chain.</param>
/// <param name="Signer">The signing certificate's simple name (CN), when there is a signature.</param>
/// <param name="Problem">Why it is not trusted, in words.</param>
public sealed record SignatureCheck(bool Trusted, string? Signer, string? Problem);

/// <summary>
/// Verifies an embedded Authenticode signature with Windows' own trust provider and reads the
/// signer's name. Direct-URL installers are run only when both pass: trusted, and signed by the
/// publisher the catalog names.
/// </summary>
/// <remarks>
/// <c>X509Certificate.CreateFromSignedFile</c> is obsolete in .NET 10 (SYSLIB0057) and has no
/// replacement, so the signer comes from the trust provider's own state
/// (<c>WTHelperProvDataFromStateData</c> -> signer -> leaf certificate -> <c>CertGetNameString</c>).
/// Revocation is not checked online: an offline machine must still be able to install, and a
/// revoked vendor certificate is rare next to a captive portal.
/// </remarks>
public static partial class Authenticode
{
    public static SignatureCheck Check(string path)
    {
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
        IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
        IntPtr fileInfo = IntPtr.Zero;
        IntPtr trustData = IntPtr.Zero;

        try
        {
            WintrustFileInfo file = new() { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pathPtr };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(file, fileInfo, false);

            WintrustData data = new()
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,             // WTD_UI_NONE
                fdwRevocationChecks = 0,    // WTD_REVOKE_NONE
                dwUnionChoice = 1,          // WTD_CHOICE_FILE
                pFile = fileInfo,
                dwStateAction = 1,          // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x1000,       // WTD_CACHE_ONLY_URL_RETRIEVAL: never block on the network
            };
            trustData = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
            Marshal.StructureToPtr(data, trustData, false);

            int result = WinVerifyTrust(IntPtr.Zero, ref action, trustData);
            data = Marshal.PtrToStructure<WintrustData>(trustData);
            string? signer = data.hWVTStateData != IntPtr.Zero ? SignerName(data.hWVTStateData) : null;

            // Always release the trust provider's state.
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            Marshal.StructureToPtr(data, trustData, true);
            WinVerifyTrust(IntPtr.Zero, ref action, trustData);

            return result == 0
                ? new SignatureCheck(true, signer, null)
                : new SignatureCheck(false, signer, Describe(result));
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);

            if (fileInfo != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfo);
            if (trustData != IntPtr.Zero)
                Marshal.FreeHGlobal(trustData);
        }
    }

    /// <summary>
    /// Trusted and signed by <paramref name="expectedSigner"/> (case-insensitive, whole name).
    /// Returns null when acceptable, otherwise why not.
    /// </summary>
    public static string? Require(string path, string expectedSigner)
    {
        SignatureCheck check = Check(path);

        if (!check.Trusted)
            return $"the download's signature is not trusted ({check.Problem})";

        return string.Equals(check.Signer, expectedSigner, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"the download is signed by '{check.Signer}', not '{expectedSigner}' as the catalog expects";
    }

    private static string? SignerName(IntPtr stateData)
    {
        IntPtr provider = WTHelperProvDataFromStateData(stateData);
        IntPtr signer = provider == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        IntPtr providerCert = signer == IntPtr.Zero ? IntPtr.Zero : WTHelperGetProvCertFromChain(signer, 0);

        if (providerCert == IntPtr.Zero)
            return null;

        // CRYPT_PROVIDER_CERT: DWORD cbStruct, then PCCERT_CONTEXT pCert (pointer-aligned).
        IntPtr cert = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        char[] buffer = new char[512];
        int length;

        unsafe
        {
            fixed (char* name = buffer)
                length = CertGetNameStringW(cert, 4 /* CERT_NAME_SIMPLE_DISPLAY_TYPE */, 0, IntPtr.Zero, name, buffer.Length);
        }

        return length > 1 ? new string(buffer, 0, length - 1) : null;
    }

    private static string Describe(int hresult) => unchecked((uint)hresult) switch
    {
        0x800B0100 => "the file is not signed",
        0x80096010 => "the signature is invalid - the file was changed after signing",
        0x800B0109 => "the certificate chain does not end in a trusted root",
        0x800B0101 => "the certificate has expired",
        0x800B010C => "the certificate was revoked",
        0x80092003 => "the file could not be read",
        _ => $"0x{hresult:X8}",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr stateData);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certIndex);

    [LibraryImport("crypt32.dll")]
    private static unsafe partial int CertGetNameStringW(IntPtr cert, uint type, uint flags, IntPtr typePara, char* name, int nameLength);
}
