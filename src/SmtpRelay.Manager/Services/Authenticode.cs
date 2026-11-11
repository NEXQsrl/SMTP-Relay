using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SmtpRelay.Manager.Services;

/// <summary>Result of the Authenticode check of a file.</summary>
/// <param name="IsIntact">The file carries a signature and its content matches it.</param>
/// <param name="IsChainTrusted">The signer chains to a root this computer trusts.</param>
/// <param name="SignerThumbprint">SHA-1 thumbprint of the signing certificate, upper case, or null when unsigned.</param>
/// <param name="SignerSubject">Subject of the signing certificate.</param>
public sealed record SignatureInfo(bool IsIntact, bool IsChainTrusted, string? SignerThumbprint, string? SignerSubject);

/// <summary>Verifies Authenticode signatures with WinVerifyTrust.</summary>
public static class Authenticode
{
    private const int Ok = 0;
    private const int CertUntrustedRoot = unchecked((int)0x800B0109);

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static SignatureInfo Verify(string path)
    {
        int result = WinVerifyTrust(path);

        // A self-signed publisher certificate that was not imported on this computer gives "untrusted root":
        // the signature itself was still verified against the file, so the caller can pin the thumbprint.
        // Any other code (no signature, digest mismatch, expired, revoked...) means the file is not acceptable.
        bool intact = result is Ok or CertUntrustedRoot;
        if (!intact)
            return new SignatureInfo(false, false, null, null);

        try
        {
#pragma warning disable SYSLIB0057 // the only managed API that reads the signer of a signed file
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return new SignatureInfo(true, result == Ok, certificate.GetCertHashString(), certificate.Subject);
        }
        catch (CryptographicException)
        {
            return new SignatureInfo(false, false, null, null);
        }
    }

    private static int WinVerifyTrust(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path
        };

        IntPtr fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x1000 | 0x10 // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            Marshal.StructureToPtr(data, dataPtr, false);
            return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(dataPtr);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
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
}
