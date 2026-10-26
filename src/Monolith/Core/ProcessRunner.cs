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
    public string? WorkingDirectory { get; init; }
    /// <summary>Write the command line into the app log.</summary>
    public bool LogCommand { get; init; } = true;
    public IDictionary<string, string>? Environment { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, bool Cancelled)
{
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
            return kind switch
            {
                OutputEncoding.Utf8 => new UTF8Encoding(false),
                OutputEncoding.Unicode => System.Text.Encoding.Unicode,
                OutputEncoding.Ansi => System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage),
                _ => System.Text.Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
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

    private static async Task<ProcessResult> RunCoreAsync(ProcessStartInfo psi, string display, ProcessRunOptions options, CancellationToken ct)
    {
        if (options.LogCommand) Log.Command(display);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

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
                return ProcessResult.Failed($"Не удалось запустить {psi.FileName}");
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить {display}", ex);
            return ProcessResult.Failed(ex.Message);
        }

        try { process.StandardInput.Close(); } catch { }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = options.Timeout is { } t ? new CancellationTokenSource(t) : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        bool timedOut = false, cancelled = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            cancelled = ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch { }
            Log.Warn(timedOut ? $"Превышено время ожидания: {display}" : $"Отменено: {display}");
        }

        int exit;
        try { exit = process.HasExited ? process.ExitCode : -1; } catch { exit = -1; }

        string o, e2;
        lock (stdout) o = stdout.ToString();
        lock (stderr) e2 = stderr.ToString();
        return new ProcessResult(exit, o, e2, timedOut, cancelled);
    }

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
