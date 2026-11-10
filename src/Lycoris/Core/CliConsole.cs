using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Lycoris.Core;

/// <summary>
/// Text output of a command-line run of Lycoris.exe (a WinExe has no console of its own). Redirected output (a pipe or a file)
/// gets UTF-8; otherwise the process attaches to its parent's console (<c>AttachConsole(ATTACH_PARENT_PROCESS)</c>) and writes
/// UTF-16 through <c>WriteConsoleW</c>, so Cyrillic shows correctly whatever the console code page, and the code page of the
/// user's console is never changed. Null = no console at all (started from Explorer): the caller shows the text another way.
/// </summary>
internal static class CliConsole
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;
    private const uint FileTypeDisk = 1, FileTypeChar = 2, FileTypePipe = 3;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WriteConsoleW(IntPtr console, string buffer, int charsToWrite, out int written, IntPtr reserved);

    public static TextWriter? Open()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            var type = handle == IntPtr.Zero || handle == new IntPtr(-1) ? 0 : GetFileType(handle);
            if (type is FileTypeDisk or FileTypePipe)
                return new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            if (type != FileTypeChar && !AttachConsole(AttachParentProcess)) return null;
            handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
            var writer = new ConsoleWriter(handle);
            writer.WriteLine(); // the shell may have printed its prompt already (cmd does not wait for a window program)
            return writer;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ConsoleWriter(IntPtr handle) : TextWriter
    {
        public override Encoding Encoding => Encoding.Unicode;

        public override void Write(char value) => Write(value.ToString());

        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            WriteConsoleW(handle, value, value.Length, out _, IntPtr.Zero);
        }

        public override void WriteLine(string? value) => Write((value ?? "") + NewLine);
    }
}
