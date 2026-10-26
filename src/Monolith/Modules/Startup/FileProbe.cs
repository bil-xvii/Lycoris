using System.IO;
using System.Runtime.InteropServices;

namespace Monolith.Modules.Startup;

/// <summary>
/// File existence check that does not lie about protected files.
/// <see cref="File.Exists"/> returns false for files the current user may not read (for example inside
/// <c>C:\Program Files\WindowsApps</c>); here only "not found"-type errors count as missing.
/// </summary>
internal static class FileProbe
{
    private const uint InvalidAttributes = 0xFFFFFFFF;
    private const uint DirectoryAttribute = 0x10;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    /// <summary>True when <paramref name="path"/> is an existing file, or exists but cannot be inspected.</summary>
    public static bool FileExists(string? path) => Probe(path) is { } attr && (attr & DirectoryAttribute) == 0;

    public static bool DirectoryExists(string? path) => Probe(path) is { } attr && (attr & DirectoryAttribute) != 0;

    private static uint? Probe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        if (path.AsSpan().IndexOfAny(['"', '<', '>', '|', '*', '?']) >= 0) return null;

        var attr = GetFileAttributesW(path);
        if (attr != InvalidAttributes) return attr;

        // The object is there but we may not look at it: report it as an existing file rather than "missing".
        var error = Marshal.GetLastWin32Error();
        return error is ErrorAccessDenied or ErrorSharingViolation ? 0u : null;
    }
}
