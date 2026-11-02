using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Monolith.Core;

/// <summary>
/// QA only: <c>Lycoris.exe --startup-timing &lt;file&gt;</c> records milliseconds since process start for the startup milestones
/// (OnStartup, window shown, first frame rendered, Dashboard first monitor sample, Dashboard checks done) and writes them,
/// with the working set, to the file. Without the flag every call is a single null check. Reads and writes nothing else.
/// </summary>
internal static class StartupTiming
{
    private static readonly object Gate = new();
    private static readonly StringBuilder Lines = new();
    private static string? _file;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public static bool Enabled => _file is not null;

    private static System.Threading.Timer? _flusher;

    public static void Enable(string file)
    {
        _file = file;
        _flusher = new System.Threading.Timer(_ => Flush(), null, 2000, 2000);
    }

    public static void Mark(string name)
    {
        if (_file is null) return;
        double ms;
        try
        {
            using var p = Process.GetCurrentProcess();
            ms = (DateTime.Now - p.StartTime).TotalMilliseconds;
        }
        catch { ms = -1; }
        lock (Gate)
            Lines.Append(ms.ToString("0", CultureInfo.InvariantCulture)).Append(" ms  ").Append(name).Append(" [thread ").Append(GetCurrentThreadId()).AppendLine("]");
        Flush();
    }

    private static double _sampleMs; private static int _samples;
    /// <summary>Dashboard monitor: duration of one Sample() call (QA average in the report).</summary>
    public static void Sampled(double ms)
    {
        if (_file is null) return;
        lock (Gate) { _sampleMs += ms; _samples++; }
        if (_samples % 5 == 0) Flush();
    }

    public static void Flush()
    {
        if (_file is null) return;
        try
        {
            using var p = Process.GetCurrentProcess();
            string text;
            lock (Gate)
            {
                var sb = new StringBuilder(Lines.ToString());
                if (_samples > 0) sb.Append("monitor Sample() avg ").Append((_sampleMs / _samples).ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms over ").Append(_samples).AppendLine(" samples");
                sb.Append("working set ").Append(p.WorkingSet64 / 1048576).AppendLine(" MB");
                sb.Append("private ").Append(p.PrivateMemorySize64 / 1048576).AppendLine(" MB");
                sb.Append("cpu total ").Append(p.TotalProcessorTime.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)).AppendLine(" ms");
                text = sb.ToString();
            }
            File.WriteAllText(_file, text);
        }
        catch { }
    }
}
