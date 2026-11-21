using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinNetworkManager.Platform.Windows.Interop;

#pragma warning disable IDE1006 // Native names are kept verbatim.

/// <summary>Authenticode verification (wintrust.dll, crypt32.dll).</summary>
internal static unsafe partial class WinTrustNative
{
    public static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public const uint WTD_UI_NONE = 2;
    public const uint WTD_REVOKE_NONE = 0;
    public const uint WTD_CHOICE_FILE = 1;
    public const uint WTD_CHOICE_CATALOG = 2;
    public const uint WTD_STATEACTION_VERIFY = 1;
    public const uint WTD_STATEACTION_CLOSE = 2;
    public const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    public const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    public const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    public const uint CERT_NAME_SIMPLE_DISPLAY_TYPE = 4;
    public const uint CERT_NAME_ISSUER_FLAG = 0x1;

    // HRESULTs
    public const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    public const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    public const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    public const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    public const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    public const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    public const int CERT_E_CHAINING = unchecked((int)0x800B010A);
    public const int CERT_E_EXPIRED = unchecked((int)0x800B0101);
    public const int CERT_E_REVOKED = unchecked((int)0x800B010C);
    public const int CRYPT_E_SECURITY_SETTINGS = unchecked((int)0x80092026);
    public const int CRYPT_E_FILE_ERROR = unchecked((int)0x80092003);

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public char* pcwszCatalogFilePath;
        public char* pcwszMemberTag;
        public char* pcwszMemberFilePath;
        public nint hMemberFile;
        public byte* pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public nint pcCatalogContext;
        public nint hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public void* pInfo;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CATALOG_INFO
    {
        public uint cbStruct;
        public fixed char wszCatalogFile[260];
    }

    [LibraryImport("wintrust.dll")]
    public static partial int WinVerifyTrust(nint hwnd, Guid* actionId, WINTRUST_DATA* data);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll")]
    public static partial nint WTHelperGetProvSignerFromChain(nint provData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminAcquireContext2(out nint catAdmin, Guid* subsystem, string? hashAlgorithm, nint strongHashPolicy, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseContext(nint catAdmin, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminCalcHashFromFileHandle2(nint catAdmin, SafeFileHandle file, uint* hashSize, byte* hash, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    public static partial nint CryptCATAdminEnumCatalogFromHash(nint catAdmin, byte* hash, uint hashSize, uint flags, nint* previousCatInfo);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATCatalogInfoFromContext(nint catInfo, CATALOG_INFO* info, uint flags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CryptCATAdminReleaseCatalogContext(nint catAdmin, nint catInfo, uint flags);

    [LibraryImport("crypt32.dll", EntryPoint = "CertGetNameStringW")]
    public static partial uint CertGetNameString(nint certContext, uint type, uint flags, void* typePara, char* name, uint nameLength);
}
