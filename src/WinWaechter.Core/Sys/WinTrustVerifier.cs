using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using WinWaechter.Core.Logging;
using static WinWaechter.Core.Sys.Native.WinTrustNative;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Authenticode check via WinVerifyTrust: embedded signature first, then the system catalogs
/// (many Windows files are only catalog-signed). No network, no UI. Anything that cannot be
/// determined is Unknown, never Unsigned.
/// </summary>
public sealed class WinTrustVerifier : ISignatureVerifier
{
    static readonly SignatureInfo UnknownInfo = new(SignatureState.Unknown, null, false);
    static readonly string[] CatalogHashAlgorithms = ["SHA256", "SHA1"]; // BCRYPT_SHA256_ALGORITHM, BCRYPT_SHA1_ALGORITHM

    readonly ConcurrentDictionary<(string Path, DateTime LastWriteUtc), SignatureInfo> _cache = new();

    public SignatureInfo Verify(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return UnknownInfo;
            var full = Path.GetFullPath(path);
            var fi = new FileInfo(full);
            if (!fi.Exists) return UnknownInfo;
            var key = (full.ToUpperInvariant(), fi.LastWriteTimeUtc);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            var info = VerifyUncached(full);
            // Unknown may be transient (file locked etc.), so it is not cached.
            return info.State == SignatureState.Unknown ? info : _cache.GetOrAdd(key, info);
        }
        catch (Exception ex)
        {
            FileLog.Warn($"Signaturprüfung fehlgeschlagen: {path}", ex);
            return UnknownInfo;
        }
    }

    static bool IsNoSignature(int code) =>
        code is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN;

    static SignatureInfo VerifyUncached(string path)
    {
        var (code, publisher) = VerifyEmbedded(path);
        if (IsNoSignature(code))
        {
            var cat = VerifyCatalog(path);
            if (cat is null) return new(SignatureState.Unsigned, null, false);
            (code, publisher) = cat.Value;
        }
        return ToInfo(code, publisher);
    }

    static SignatureInfo ToInfo(int code, string? publisher)
    {
        if (code == 0)
            return new(SignatureState.Valid, publisher,
                publisher?.StartsWith("Microsoft", StringComparison.Ordinal) == true);
        if (IsNoSignature(code)) return new(SignatureState.Unsigned, null, false);
        if (BadSignatureCodes.Contains(code)) return new(SignatureState.Invalid, publisher, false);
        return new(SignatureState.Unknown, publisher, false);
    }

    static (int Code, string? Publisher) VerifyEmbedded(string path)
    {
        var pPath = Marshal.StringToHGlobalUni(path);
        var pInfo = IntPtr.Zero;
        try
        {
            var info = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = pPath };
            pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(info, pInfo, false);
            return RunWinVerifyTrust(WTD_CHOICE_FILE, pInfo);
        }
        finally
        {
            if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo);
            Marshal.FreeHGlobal(pPath);
        }
    }

    // null = the file is in no catalog. Throws when the catalog API itself is unusable.
    static (int Code, string? Publisher)? VerifyCatalog(string path)
    {
        var anyContext = false;
        (int Code, string? Publisher)? lastFailure = null;
        foreach (var alg in CatalogHashAlgorithms)
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, alg, IntPtr.Zero, 0)) continue;
            anyContext = true;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                uint len = 64;
                var hash = new byte[len];
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, fs.SafeFileHandle, ref len, hash, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Hash für Katalogsuche ({alg}) fehlgeschlagen.");
                Array.Resize(ref hash, (int)len);

                var r = VerifyInCatalogs(admin, hash, path, fs.SafeFileHandle);
                if (r is { Code: 0 }) return r;
                lastFailure ??= r;
            }
            finally
            {
                CryptCATAdminReleaseContext(admin, 0);
            }
        }
        if (!anyContext) throw new Win32Exception(Marshal.GetLastWin32Error(), "Kein Katalog-Kontext verfügbar.");
        return lastFailure;
    }

    // Tries every catalog that lists the hash; first success wins, otherwise the last failure.
    static (int Code, string? Publisher)? VerifyInCatalogs(IntPtr admin, byte[] hash, string path, SafeFileHandle file)
    {
        var tag = Convert.ToHexString(hash);
        (int Code, string? Publisher)? last = null;
        var prev = IntPtr.Zero;
        while (true)
        {
            // Passing the previous handle lets the API free it and return the next one.
            var cat = CryptCATAdminEnumCatalogFromHash(admin, hash, (uint)hash.Length, 0, ref prev);
            if (cat == IntPtr.Zero) return last;
            prev = cat;

            var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
            if (!CryptCATCatalogInfoFromContext(cat, ref ci, 0)) continue;

            var r = VerifyCatalogMember(admin, ci.wszCatalogFile, tag, path, file, hash);
            if (r.Code == 0)
            {
                CryptCATAdminReleaseCatalogContext(admin, cat, 0);
                return r;
            }
            last = r;
        }
    }

    static (int Code, string? Publisher) VerifyCatalogMember(IntPtr admin, string catalogFile, string tag,
        string path, SafeFileHandle file, byte[] hash)
    {
        var pCat = Marshal.StringToHGlobalUni(catalogFile);
        var pTag = Marshal.StringToHGlobalUni(tag);
        var pPath = Marshal.StringToHGlobalUni(path);
        var pHash = Marshal.AllocHGlobal(hash.Length);
        var pInfo = IntPtr.Zero;
        var addRef = false;
        try
        {
            Marshal.Copy(hash, 0, pHash, hash.Length);
            file.DangerousAddRef(ref addRef);
            var info = new WINTRUST_CATALOG_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                pcwszCatalogFilePath = pCat,
                pcwszMemberTag = pTag,
                pcwszMemberFilePath = pPath,
                hMemberFile = file.DangerousGetHandle(),
                pbCalculatedFileHash = pHash,
                cbCalculatedFileHash = (uint)hash.Length,
                hCatAdmin = admin,
            };
            pInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
            Marshal.StructureToPtr(info, pInfo, false);
            return RunWinVerifyTrust(WTD_CHOICE_CATALOG, pInfo);
        }
        finally
        {
            if (addRef) file.DangerousRelease();
            if (pInfo != IntPtr.Zero) Marshal.FreeHGlobal(pInfo);
            Marshal.FreeHGlobal(pHash);
            Marshal.FreeHGlobal(pPath);
            Marshal.FreeHGlobal(pTag);
            Marshal.FreeHGlobal(pCat);
        }
    }

    // Verify, read the signer while the state is open, then always close the state.
    static (int Code, string? Publisher) RunWinVerifyTrust(uint unionChoice, IntPtr pInfo)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pInfo = pInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
        };
        var action = GenericVerifyV2;
        var noUi = new IntPtr(-1); // INVALID_HANDLE_VALUE: no interactive user
        try
        {
            var code = WinVerifyTrust(noUi, ref action, ref data);
            return (code, ReadSigner(data.hWVTStateData));
        }
        finally
        {
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(noUi, ref action, ref data);
        }
    }

    // Simple name of the leaf signer certificate, or null.
    static string? ReadSigner(IntPtr state)
    {
        if (state == IntPtr.Zero) return null;
        try
        {
            var prov = WTHelperProvDataFromStateData(state);
            if (prov == IntPtr.Zero) return null;
            var signer = WTHelperGetProvSignerFromChain(prov, 0, false, 0);
            if (signer == IntPtr.Zero) return null;
            var provCert = WTHelperGetProvCertFromChain(signer, 0);
            if (provCert == IntPtr.Zero) return null;
            var head = Marshal.PtrToStructure<CRYPT_PROVIDER_CERT_HEAD>(provCert);
            if (head.pCert == IntPtr.Zero) return null;
            using var cert = new X509Certificate2(head.pCert);
            var name = cert.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }
}
