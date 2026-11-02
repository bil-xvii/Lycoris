using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Monolith.Core;

namespace Monolith.Modules.Apps;

public enum UnelevatedOutcome
{
    /// <summary>winget ran and its exit code is known.</summary>
    Completed,
    /// <summary>winget was started but its result could not be followed (e.g. another account's TEMP); re-detect later.</summary>
    Untracked,
    /// <summary>The desktop shell could not start it at all.</summary>
    LaunchFailed,
    Cancelled,
}

public sealed record UnelevatedResult(UnelevatedOutcome Outcome, int ExitCode, string Output)
{
    /// <summary>Untracked because winget exceeded the wait time; it was left running, not killed.</summary>
    public bool StillRunning { get; init; }
    public WingetResult ToWinget() => new(ExitCode, Output, Outcome == UnelevatedOutcome.Cancelled) { StillRunning = StillRunning };
}

/// <summary>
/// Runs one winget command with the desktop user's normal token while MONOLITH is elevated.
/// Explorer starts "cmd /c winget … &gt; %TEMP%\MONOLITH-winget-&lt;token&gt;.log" hidden; the token makes the process
/// findable (WMI command line) for cancelling, the .log file is tailed for live output and a second file
/// receives winget's exit code. Holds the winget gate, so it never overlaps other winget calls.
/// </summary>
public static partial class UnelevatedWinget
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private const string FilePrefix = "MONOLITH-winget-";

    public static Task<UnelevatedResult> RunAsync(IReadOnlyList<string> wingetArgs, Action<string>? output, CancellationToken ct) =>
        WingetClient.RunExclusiveAsync(token => RunCoreAsync(wingetArgs, output, token), ct);

    /// <summary>Deletes log/exit files left by runs that were still going when MONOLITH stopped waiting (older than a day).</summary>
    private static void SweepStale(string temp)
    {
        try
        {
            foreach (var f in new DirectoryInfo(temp).EnumerateFiles(FilePrefix + "*"))
                if (f.Extension is ".log" or ".exit" && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    TryDelete(f.FullName);
        }
        catch { /* best effort */ }
    }

    private static async Task<UnelevatedResult> RunCoreAsync(IReadOnlyList<string> wingetArgs, Action<string>? output, CancellationToken ct)
    {
        if (SafetyLock.IsLocked && WingetClient.IsModifying(wingetArgs))
            return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, Loc.T("Установка программ заблокирована: режим самопроверки."));
        var token = Guid.NewGuid().ToString("N");
        var logName = $"{FilePrefix}{token}.log";
        var exitName = $"{FilePrefix}{token}.exit";
        var temp = Environment.GetEnvironmentVariable("TEMP") is { Length: > 0 } t ? t : Path.GetTempPath();
        var logPath = Path.Combine(temp, logName);
        var exitPath = Path.Combine(temp, exitName);
        SweepStale(temp);
        var stillRunning = false;

        string cmdLine;
        try
        {
            cmdLine = BuildCommandLine(WingetExeForShell(), wingetArgs, logName, exitName);
        }
        catch (ArgumentException ex)
        {
            return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, ex.Message);
        }

        Log.Command("winget (без прав администратора) " + string.Join(' ', wingetArgs));
        var started = DateTime.UtcNow;
        if (!await UnelevatedShell.ShellExecuteAsync(ProcessRunner.Sys("cmd.exe"), cmdLine, ProcessRunner.System32, UnelevatedShell.SwHide).ConfigureAwait(false))
            return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, "");

        var all = new StringBuilder();
        var tail = new LogTail(logPath, line =>
        {
            all.AppendLine(line);
            output?.Invoke(line);
        });

        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    await KillAsync(token).ConfigureAwait(false);
                    tail.Pump(final: true);
                    return new UnelevatedResult(UnelevatedOutcome.Cancelled, -1, all.ToString());
                }

                tail.Pump(final: false);

                if (TryReadExitCode(exitPath, out var code))
                {
                    await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                    tail.Pump(final: true);
                    if (code == 0 || WingetClient.IsBenign(code)) Log.Success("winget {0} (без прав администратора): {1}", wingetArgs.FirstOrDefault(), WingetClient.Describe(code));
                    else Log.Warn("winget {0} (без прав администратора): {1}", wingetArgs.FirstOrDefault(), WingetClient.Describe(code));
                    return new UnelevatedResult(UnelevatedOutcome.Completed, code, all.ToString());
                }

                var elapsed = DateTime.UtcNow - started;
                if (!tail.Exists && elapsed > StartTimeout)
                {
                    // The shell did not start it, or it runs under another account's TEMP folder.
                    Log.Warn("winget без прав администратора: не удалось отследить выполнение — результат будет проверен повторно.");
                    return new UnelevatedResult(UnelevatedOutcome.Untracked, -1, all.ToString());
                }
                if (elapsed > RunTimeout)
                {
                    // Never kill on timeout: that would also kill the installer and can leave a half-installed program.
                    tail.Pump(final: true);
                    Log.Warn("winget без прав администратора: идёт дольше 30 минут — MONOLITH перестал ждать, процесс не прерывался.");
                    stillRunning = true;
                    return new UnelevatedResult(UnelevatedOutcome.Untracked, -1, all.ToString()) { StillRunning = true };
                }

                try { await Task.Delay(400, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* handled at the top of the loop */ }
            }
        }
        finally
        {
            // Still running: cmd keeps the log open and writes the exit file later; stale files are swept next time.
            if (!stillRunning)
            {
                TryDelete(logPath);
                TryDelete(exitPath);
            }
        }
    }

    /// <summary>
    /// cmd.exe arguments: /d (no AutoRun), /v:on (delayed !ERRORLEVEL!), /s /c "…". %TEMP% is expanded by the
    /// de-elevated cmd itself. Every winget argument must be a plain token (validated) — nothing reaches cmd unquoted
    /// that could act as a shell metacharacter. The one exception is the value after --location: it must be a
    /// validated location template (<see cref="WingetClient.IsValidLocationTemplate"/>: one known %ROOT% variable plus
    /// plain folder names), is quoted, and the signed-in user's cmd expands the variable to that user's folders.
    /// </summary>
    public static string BuildCommandLine(string wingetExe, IReadOnlyList<string> wingetArgs, string logFileName, string exitFileName)
    {
        if (string.IsNullOrWhiteSpace(wingetExe) || wingetExe.IndexOfAny(['"', '%', '!', '^', '\r', '\n']) >= 0)
            throw new ArgumentException(Loc.T("Недопустимый путь к winget."));
        for (var i = 0; i < wingetArgs.Count; i++)
        {
            var a = wingetArgs[i];
            var ok = IsLocationValue(wingetArgs, i) ? WingetClient.IsValidLocationTemplate(a) : IsSafeToken(a);
            if (!ok) throw new ArgumentException(Loc.Tf("Недопустимый аргумент winget: «{0}»", a));
        }
        foreach (var f in new[] { logFileName, exitFileName })
            if (!IsSafeToken(f)) throw new ArgumentException(Loc.Tf("Недопустимое имя файла: «{0}»", f));

        var sb = new StringBuilder("/d /v:on /s /c \"");
        sb.Append('"').Append(wingetExe).Append('"');
        for (var i = 0; i < wingetArgs.Count; i++)
        {
            sb.Append(' ');
            if (IsLocationValue(wingetArgs, i)) sb.Append('"').Append(wingetArgs[i]).Append('"');
            else sb.Append(wingetArgs[i]);
        }
        sb.Append(" >\"%TEMP%\\").Append(logFileName).Append("\" 2>&1");
        sb.Append(" & (echo !ERRORLEVEL!)>\"%TEMP%\\").Append(exitFileName).Append('"');
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsLocationValue(IReadOnlyList<string> args, int index) =>
        index > 0 && string.Equals(args[index - 1], "--location", StringComparison.Ordinal);

    /// <summary>Letters, digits and . + _ - (winget switches, package ids, sources, file names).</summary>
    public static bool IsSafeToken(string s) =>
        s.Length is > 0 and <= 160 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-');

    /// <summary>Parses the exit-code file written by "echo !ERRORLEVEL!" (a signed 32-bit number).</summary>
    public static bool TryParseExitCode(string text, out int code) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out code);

    private static string WingetExeForShell()
    {
        var exe = WingetClient.ExePath;
        // The App Execution Alias of this account; if its path cannot be passed safely, let the shell resolve it.
        return exe is not null && exe.IndexOfAny(['"', '%', '!', '^']) < 0 ? exe : "winget.exe";
    }

    private static bool TryReadExitCode(string path, out int code)
    {
        code = 0;
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            var text = reader.ReadToEnd();
            return text.Trim().Length > 0 && TryParseExitCode(text, out code);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task KillAsync(string token)
    {
        await Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT ProcessId FROM Win32_Process WHERE Name = 'cmd.exe' AND CommandLine LIKE '%{FilePrefix}{token}%'");
                foreach (var mo in searcher.Get())
                {
                    using (mo)
                    {
                        var pid = Convert.ToInt32(mo["ProcessId"]);
                        try
                        {
                            using var p = Process.GetProcessById(pid);
                            p.Kill(entireProcessTree: true);
                            Log.Warn("winget без прав администратора: прервано пользователем.");
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                            // already finished
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось прервать winget: {0}", ex.Message);
            }
        }).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Follows a growing UTF-8 log file and emits complete, de-noised lines.</summary>
    private sealed class LogTail(string path, Action<string> emit)
    {
        private readonly Decoder _decoder = new UTF8Encoding(false).GetDecoder();
        private readonly StringBuilder _pending = new();
        private long _offset;

        public bool Exists { get; private set; }

        public void Pump(bool final)
        {
            try
            {
                if (!File.Exists(path)) return;
                Exists = true;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > _offset)
                {
                    fs.Seek(_offset, SeekOrigin.Begin);
                    var buffer = new byte[fs.Length - _offset];
                    var read = fs.Read(buffer, 0, buffer.Length);
                    _offset += read;
                    var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
                    _decoder.GetChars(buffer, 0, read, chars, 0);
                    _pending.Append(chars);
                }
            }
            catch (IOException)
            {
                return;
            }

            var text = _pending.ToString();
            var lastBreak = text.LastIndexOf('\n');
            var complete = final ? text : lastBreak >= 0 ? text[..(lastBreak + 1)] : "";
            _pending.Clear();
            if (!final && lastBreak >= 0) _pending.Append(text[(lastBreak + 1)..]);
            else if (!final) _pending.Append(text);

            foreach (var raw in complete.Split('\n'))
            {
                var line = CleanLine(raw);
                if (!WingetClient.IsNoise(line)) emit(line);
            }
        }
    }

    /// <summary>
    /// Same clean-up as ProcessRunner: drop NULs, backspaces and terminal escape sequences, keep the text after
    /// the last carriage return (progress bars redraw the same line).
    /// </summary>
    public static string CleanLine(string line)
    {
        line = line.TrimEnd('\r');
        if (line.IndexOf('\0') >= 0) line = line.Replace("\0", string.Empty);
        var cr = line.LastIndexOf('\r');
        if (cr >= 0) line = line[(cr + 1)..];
        if (line.IndexOf('\b') >= 0) line = line.Replace("\b", string.Empty);
        if (line.IndexOf('\u001b') >= 0) line = EscapeRegex().Replace(line, string.Empty);
        return line.TrimEnd();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\u001b\[[0-9;?]*[A-Za-z]")]
    private static partial System.Text.RegularExpressions.Regex EscapeRegex();
}
