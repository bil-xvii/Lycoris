using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Monolith.Core.Security;

/// <summary>
/// Process-wide hardening that runs before any other MONOLITH code (module initializer): where Windows looks for DLLs,
/// and the image-load mitigations. Nothing here uses «dynamic code prohibited» or «Microsoft-signed only» — WPF, the .NET
/// JIT and GPU drivers need both.
/// </summary>
public static class Hardening
{
    private const uint LoadLibrarySearchUserDirs = 0x00000400;
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    /// <summary>ProcessImageLoadPolicy for SetProcessMitigationPolicy.</summary>
    private const int ProcessImageLoadPolicy = 10;
    private const uint NoRemoteImages = 0x1, NoLowLabelImages = 0x2, PreferSystem32Images = 0x4;

    /// <summary>What <see cref="Apply"/> achieved (shown in the self-test and the log).</summary>
    public sealed record Report(bool DllDirectoriesRestricted, int TrustedNativeDirectories, bool ImageLoadPolicy, string Details);

    private static Report? _report;
    private static readonly object Gate = new();

    /// <summary>The result of the first <see cref="Apply"/> (applies it if that has not happened yet).</summary>
    public static Report Current => Apply();

    [ModuleInitializer]
    internal static void Initialize()
    {
        try { Apply(); }
        catch { /* never prevent the app from starting because a mitigation is unavailable */ }
    }

    public static Report Apply()
    {
        lock (Gate)
        {
            if (_report is not null) return _report;

            // 1. DLL search order: System32 + explicitly added folders only. The application folder and the current directory
            //    (Desktop\…, Downloads) are no longer searched for plain-name LoadLibrary calls, so a planted «version.dll»
            //    next to MONOLITH.exe is never loaded. The folders the .NET host itself resolved for native libraries
            //    (framework folder, or the single-file extraction folder) are added back — they are loaded by full path.
            var restricted = false;
            var trusted = 0;
            var details = new List<string>();
            try
            {
                restricted = SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs);
                if (restricted)
                {
                    var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
                    foreach (var dir in NativeSearchDirectories())
                    {
                        // The exe folder is exactly what must NOT be searched (single-file: nothing is loaded from it).
                        if (string.Equals(Path.TrimEndingDirectorySeparator(dir), exeDir, StringComparison.OrdinalIgnoreCase)) continue;
                        if (AddDllDirectory(dir) != IntPtr.Zero) trusted++;
                        else details.Add("AddDllDirectory failed: " + dir);
                    }
                }
                else details.Add("SetDefaultDllDirectories failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                details.Add("DLL directory API unavailable");
            }

            // 2. Image-load mitigation: no DLLs from network shares or low-integrity folders; System32 preferred.
            var imageLoad = false;
            try
            {
                var policy = NoRemoteImages | NoLowLabelImages | PreferSystem32Images;
                imageLoad = SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref policy, (UIntPtr)sizeof(uint));
                if (!imageLoad) details.Add("SetProcessMitigationPolicy(ImageLoad) failed: " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                details.Add("mitigation API unavailable");
            }

            return _report = new Report(restricted, trusted, imageLoad, string.Join("; ", details));
        }
    }

    /// <summary>The native-library folders the runtime host computed for this app (full paths).</summary>
    internal static IEnumerable<string> NativeSearchDirectories()
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string list) yield break;
        foreach (var raw in list.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;
            try { full = Path.GetFullPath(raw); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (Path.IsPathFullyQualified(full) && !full.StartsWith(@"\\", StringComparison.Ordinal) && Directory.Exists(full))
                yield return full;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, UIntPtr length);
}
