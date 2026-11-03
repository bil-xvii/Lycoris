using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Lycoris.Core.Security;

/// <summary>Outcome of an Authenticode check of a file (embedded signature, no revocation lookup, no network).</summary>
public enum SignatureState
{
    /// <summary>Signed and the chain is trusted on this PC.</summary>
    Valid,
    /// <summary>No signature at all (or a file type that cannot carry one).</summary>
    Unsigned,
    /// <summary>Signed, but the publisher is not trusted on this PC (self-signed, unknown root, expired without timestamp).</summary>
    Untrusted,
    /// <summary>Signed, and the file was changed after signing — the signature does not match the bytes.</summary>
    Tampered,
    /// <summary>Could not be checked (file missing / locked / API error).</summary>
    Error,
}

/// <summary>Authenticode through WinVerifyTrust, called directly (no PowerShell, no module search path to hijack).</summary>
public static class Authenticode
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int TrustEFileError = unchecked((int)0x80092003);

    /// <summary>Maps a WinVerifyTrust result code. Pure — unit-tested.</summary>
    public static SignatureState Classify(int result) => result switch
    {
        0 => SignatureState.Valid,
        TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => SignatureState.Unsigned,
        TrustEBadDigest => SignatureState.Tampered,
        TrustEFileError => SignatureState.Error,
        _ => SignatureState.Untrusted,
    };

    public static SignatureState Check(string path)
    {
        if (!File.Exists(path)) return SignatureState.Error;
        // A PE file without a certificate table cannot carry an embedded signature: WinVerifyTrust would answer
        // TRUST_E_NOSIGNATURE after hashing the whole file (~260 ms for the 88 MB exe, on the path to the UAC prompt).
        if (HasNoCertificateTable(path)) return SignatureState.Unsigned;
        try
        {
            return Classify(Verify(path));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException or ArgumentException)
        {
            return SignatureState.Error;
        }
    }

    /// <summary>
    /// True only when the file is a well-formed PE whose certificate table directory is empty. Anything unusual (not a PE,
    /// unreadable, malformed headers) answers false, so the full WinVerifyTrust check still decides.
    /// </summary>
    internal static bool HasNoCertificateTable(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            using var pe = new System.Reflection.PortableExecutable.PEReader(fs, System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);
            var header = pe.PEHeaders.PEHeader;
            return header is not null && header.CertificateTableDirectory.Size == 0 && header.CertificateTableDirectory.RelativeVirtualAddress == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The organization (O, else CN) of the embedded signer certificate, or null. Not a trust decision.</summary>
    public static string? SignerName(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // no replacement exists for reading the signer out of a signed PE file
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? cert.Subject : name;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or ArgumentException)
        {
            return null;
        }
    }

    private static int Verify(string path)
    {
        var pPath = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WinTrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(), pcwszFilePath = pPath };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,           // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10 | 0x1000, // WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL: never touches the network
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data); // INVALID_HANDLE_VALUE: no UI window
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
            Marshal.FreeHGlobal(pPath);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);
}
