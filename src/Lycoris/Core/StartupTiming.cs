using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Lycoris.Core;

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

    /// <summary>QA leak check (--cycle-pages N): visits every page N times, then reports the managed heap after a full GC and live view counts.</summary>
    public static async Task CyclePagesAsync(Shell.MainViewModel vm, int rounds)
    {
        await Task.Delay(6000);
        var last = Stopwatch.GetTimestamp();
        var stall = 0.0;
        var probe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        probe.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            stall = Math.Max(stall, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds - 20);
            last = now;
        };
        probe.Start();
        Mark("cycle start heap " + (GC.GetTotalMemory(true) / 1048576) + " MB");
        for (var r = 1; r <= rounds; r++)
        {
            foreach (var page in vm.Pages.ToList())
            {
                var sw = Stopwatch.StartNew();
                stall = 0;
                vm.NavigateTo(page.Key);
                if (r == 1)
                {
                    var load = page.EnsureLoadedAsync();
                    await Task.WhenAny(load, Task.Delay(30000));
                    Mark("page " + page.Key + " first load " + sw.ElapsedMilliseconds + " ms" + ", worst UI stall " + (int)stall + " ms" + (load.IsCompleted ? "" : " (not finished in 30 s)"));
                }
                await Task.Delay(700);
            }
            vm.NavigateTo("dashboard");
            await Task.Delay(700);
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); await Task.Delay(100); }
            Mark("round " + r + " heap after GC " + (GC.GetTotalMemory(true) / 1048576) + " MB, WS " + (Process.GetCurrentProcess().WorkingSet64 / 1048576) + " MB");
        }
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
                sb.Append("managed heap ").Append(GC.GetTotalMemory(false) / 1048576).Append(" MB, committed ").Append(GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576).AppendLine(" MB");
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
