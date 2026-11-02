using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Lycoris.Modules.Startup;

/// <summary>Reads .lnk shortcuts through the shell's IShellLink (read-only: no Resolve, no UI, nothing written).</summary>
internal static class ShellLink
{
    public sealed record LinkInfo(string? TargetPath, string Arguments, string? WorkingDirectory);

    private const int MaxPath = 32768;
    private const int StgmRead = 0;

    /// <summary>Target + arguments of a shortcut, or null when it cannot be read. Target is null for shell items without a file path.</summary>
    public static LinkInfo? Read(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new CShellLink();
            ((IPersistFile)obj).Load(lnkPath, StgmRead);
            var link = (IShellLinkW)obj;

            var path = new StringBuilder(MaxPath);
            link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var args = new StringBuilder(MaxPath);
            link.GetArguments(args, args.Capacity);
            var dir = new StringBuilder(MaxPath);
            link.GetWorkingDirectory(dir, dir.Capacity);

            var target = path.ToString().Trim();
            return new LinkInfo(target.Length == 0 ? null : target, args.ToString().Trim(), dir.Length == 0 ? null : dir.ToString());
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj)) Marshal.FinalReleaseComObject(obj);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink;

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}

/// <summary>Runs shell / COM work on a dedicated background STA thread so the UI thread never blocks.</summary>
internal static class StaWorker
{
    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
