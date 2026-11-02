using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Monolith.Core;

/// <summary>How a console tool encodes its output.</summary>
public enum OutputEncoding
{
    /// <summary>OEM code page of the system (CP866 on Russian Windows): powercfg, DISM, pnputil, schtasks, reg, fsutil...</summary>
    Oem,
    /// <summary>UTF-8: winget, PowerShell scripts run through <see cref="ProcessRunner.RunPowerShellAsync"/>.</summary>
    Utf8,
    /// <summary>UTF-16 LE: sfc.exe.</summary>
    Unicode,
    /// <summary>ANSI code page (CP1251 on Russian Windows).</summary>
    Ansi,
}

public sealed class ProcessRunOptions
{
    public OutputEncoding Encoding { get; init; } = OutputEncoding.Oem;
    /// <summary>Receives every output line (stdout and stderr) as it arrives. Called on a background thread.</summary>
    public Action<string>? OnOutput { get; init; }
    public TimeSpan? Timeout { get; init; }
    /// <summary>
    /// When <see cref="Timeout"/> expires: true (default) kills the whole process tree; false only stops waiting and
    /// leaves the process running (installers, DISM servicing — killing them half-way is worse than waiting). The
    /// result then has <see cref="ProcessResult.TimedOut"/> = true and <see cref="ProcessResult.StillRunning"/> = true.
    /// Cancellation through the token always kills.
    /// </summary>
    public bool KillOnTimeout { get; init; } = true;
    public string? WorkingDirectory { get; init; }
    /// <summary>Write the command line into the app log.</summary>
    public bool LogCommand { get; init; } = true;
    public IDictionary<string, string>? Environment { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Cancelled)
{
    /// <summary>Timed out with <see cref="ProcessRunOptions.KillOnTimeout"/> = false: the process was left running.</summary>
    public bool StillRunning { get; init; }

    public bool Success => ExitCode == 0 && !TimedOut && !Cancelled;
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : StdOut + System.Environment.NewLine + StdErr;

    public static ProcessResult Failed(string message) => new(-1, string.Empty, message, false, false);
}

/// <summary>Runs external tools (winget, powercfg, sfc, DISM, pnputil...) without a console window.</summary>
public static class ProcessRunner
{
    static ProcessRunner()
    {
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string System32 => Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>Full path of a tool in System32 (so a planted exe in the working dir can never be picked up).</summary>
    public static string Sys(string exe) => Path.Combine(System32, exe);

    public static Encoding GetEncoding(OutputEncoding kind)
    {
        try
        {
            // The system code pages (not the user culture): correct even with the "UTF-8 for worldwide language support" option.
            return kind switch
            {
                OutputEncoding.Utf8 => new UTF8Encoding(false),
                OutputEncoding.Unicode => System.Text.Encoding.Unicode,
                OutputEncoding.Ansi => System.Text.Encoding.GetEncoding((int)GetACP()),
                _ => System.Text.Encoding.GetEncoding((int)GetOEMCP()),
            };
        }
        catch
        {
            return new UTF8Encoding(false);
        }
    }

    public static Task<ProcessResult> RunAsync(string fileName, string arguments, ProcessRunOptions? options = null, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(fileName, options);
        psi.Arguments = arguments;
        return RunCoreAsync(psi, $"{Path.GetFileName(fileName)} {arguments}", options ?? new(), ct);
    }

    public static Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, ProcessRunOptions? options = null, CancellationToken ct = default)
    {
        var psi = CreateStartInfo(fileName, options);
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return RunCoreAsync(psi, $"{Path.GetFileName(fileName)} {string.Join(' ', psi.ArgumentList)}", options ?? new(), ct);
    }

    /// <summary>
    /// Runs a PowerShell script (Windows PowerShell 5.1, no profile) and decodes its output as UTF-8.
    /// The script is passed with -EncodedCommand, so no quoting problems.
    /// </summary>
    public static Task<ProcessResult> RunPowerShellAsync(string script, ProcessRunOptions? options = null, CancellationToken ct = default)
    {
        var full = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Continue';" +
                   "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" + script;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(full));
        var ps = Path.Combine(System32, "WindowsPowerShell", "v1.0", "powershell.exe");
        var opts = new ProcessRunOptions
        {
            Encoding = OutputEncoding.Utf8,
            OnOutput = options?.OnOutput,
            Timeout = options?.Timeout,
            KillOnTimeout = options?.KillOnTimeout ?? true,
            WorkingDirectory = options?.WorkingDirectory,
            LogCommand = false,
            Environment = options?.Environment,
        };
        if (options?.LogCommand ?? true)
            Log.Command("powershell: " + (script.Length > 160 ? script[..160] + "…" : script));
        return RunAsync(ps, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", opts, ct);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, ProcessRunOptions? options)
    {
        var enc = GetEncoding(options?.Encoding ?? OutputEncoding.Oem);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = enc,
            StandardErrorEncoding = enc,
            WorkingDirectory = options?.WorkingDirectory ?? System32,
        };
        if (options?.Environment is { } env)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>
    /// At most this many SHORT child processes (probes: powercfg, reg, schtasks, PowerShell one-liners — a timeout of 90 s or less)
    /// run at once. Dozens of parallel PowerShell / WMI helpers (Dashboard, Drivers, Devices, Cleaner warm-ups together) exhaust
    /// handles and memory and make every one of them slower. Long tools (winget, DISM, SFC, installers: no or a long timeout) are
    /// ordered by the OperationCoordinator instead and never take a slot, so they cannot starve the probes.
    /// </summary>
    public const int MaxParallelProbes = 4;
    private static readonly SemaphoreSlim ProbeSlots = new(MaxParallelProbes, MaxParallelProbes);
    private static readonly TimeSpan ShortRun = TimeSpan.FromSeconds(90);

    /// <summary>Short processes running right now (self-test and tests read it).</summary>
    public static int ProbesRunning => MaxParallelProbes - ProbeSlots.CurrentCount;

    /// <summary>Highest number of short processes seen at once (self-test: must stay ≤ <see cref="MaxParallelProbes"/>).</summary>
    public static int PeakProbes => Volatile.Read(ref _peakProbes);
    private static int _peakProbes;

    private static async Task<ProcessResult> RunCoreAsync(ProcessStartInfo psi, string display, ProcessRunOptions options, CancellationToken ct)
    {
        var shortRun = options.Timeout is { } limit && limit <= ShortRun;
        if (!shortRun) return await RunUnlimitedAsync(psi, display, options, ct).ConfigureAwait(false);
        try
        {
            await ProbeSlots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ProcessResult(-1, string.Empty, string.Empty, false, true);
        }
        try
        {
            var now = ProbesRunning;
            int seen;
            while (now > (seen = Volatile.Read(ref _peakProbes)) && Interlocked.CompareExchange(ref _peakProbes, now, seen) != seen) { }
            return await RunUnlimitedAsync(psi, display, options, ct).ConfigureAwait(false);
        }
        finally
        {
            ProbeSlots.Release();
        }
    }

    private static async Task<ProcessResult> RunUnlimitedAsync(ProcessStartInfo psi, string display, ProcessRunOptions options, CancellationToken ct)
    {
        if (options.LogCommand) Log.Command(display);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var keepAlive = false;
        try
        {

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = Clean(e.Data);
            lock (stdout) stdout.AppendLine(line);
            if (line.Length > 0) options.OnOutput?.Invoke(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var line = Clean(e.Data);
            lock (stderr) stderr.AppendLine(line);
            if (line.Length > 0) options.OnOutput?.Invoke(line);
        };

        try
        {
            if (!process.Start())
                return ProcessResult.Failed(Loc.Tf("Не удалось запустить {0}", psi.FileName));
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить {0}", ex, display);
            return ProcessResult.Failed(ex.Message);
        }

        try { process.StandardInput.Close(); } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = options.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        bool timedOut = false, cancelled = false, stillRunning = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !options.KillOnTimeout)
        {
            // Stop waiting, but never kill: the process keeps running on its own.
            stillRunning = true;
            try { stillRunning = !process.HasExited; } catch { }
            if (stillRunning)
            {
                timedOut = true;
                Log.Warn("Превышено время ожидания, процесс оставлен работать: {0}", display);
            }
            else
            {
                // It finished right at the deadline: collect the real exit code and output.
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            if (timedOut) Log.Warn("Превышено время ожидания: {0}", display);
            else Log.Warn("Отменено: {0}", display);
        }

        int exit;
        try { exit = process.HasExited ? process.ExitCode : -1; } catch { exit = -1; }

        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        keepAlive = stillRunning;
        return new ProcessResult(exit, o, e2, timedOut, cancelled) { StillRunning = stillRunning };
        }
        finally
        {
            // A process left running keeps its output pipes open until it exits: closing them early could make
            // DISM or an installer fail on its next write.
            if (keepAlive)
                _ = process.WaitForExitAsync(CancellationToken.None).ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
            else
                process.Dispose();
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetOEMCP();
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetACP();

    /// <summary>Removes NULs (sfc), backspaces and carriage-return progress spinners (winget).</summary>
    private static string Clean(string line)
    {
        if (line.IndexOf('\0') >= 0) line = line.Replace("\0", string.Empty);
        var cr = line.LastIndexOf('\r');
        if (cr >= 0) line = line[(cr + 1)..];
        if (line.IndexOf('\b') >= 0) line = line.Replace("\b", string.Empty);
        return line.TrimEnd();
    }
}
