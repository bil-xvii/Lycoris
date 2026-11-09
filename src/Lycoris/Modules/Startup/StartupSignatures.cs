using System.Collections.Concurrent;
using System.IO;
using Lycoris.Core.Security;

namespace Lycoris.Modules.Startup;

/// <summary>
/// Digital signature of autostart programs (trust info like Sysinternals Autoruns): the CompanyName in a file's version info
/// is free text that malware fills in too, the Authenticode signature is not. Read-only: WinVerifyTrust on the local file,
/// no network, no revocation lookup. Results are cached by path + size + write time, so a rescan does not hash files again.
/// </summary>
public static class StartupSignatures
{
    private static readonly ConcurrentDictionary<(string Path, long Size, DateTime Write), (SignatureState?, string?)> Cache = new();

    /// <summary>Only file types that carry an embedded Authenticode signature; scripts and others stay «not checked».</summary>
    public static bool IsSignable(string? path) =>
        Path.GetExtension(path ?? "").ToLowerInvariant() is ".exe" or ".dll" or ".com" or ".scr" or ".cpl" or ".sys" or ".ocx";

    public static (SignatureState? State, string? SignedBy) Read(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !IsSignable(path)) return (null, null);
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return (null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return (null, null); }
        return Cache.GetOrAdd((fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc), _ =>
        {
            // embedded signature, else the Windows catalogs (Windows' own programs are catalog-signed — not «unsigned»)
            var (state, signer) = Authenticode.CheckWithCatalog(path);
            if (state == SignatureState.Error) return (null, null);   // locked / unreadable: say nothing rather than guess
            return (state, state == SignatureState.Valid ? signer : null);
        });
    }
}
