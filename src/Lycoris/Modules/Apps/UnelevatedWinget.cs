using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Lycoris.Core;
using Lycoris.Core.Security;
using Microsoft.Win32.SafeHandles;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace Lycoris.Modules.Apps;

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
/// Runs one winget command with the desktop user's normal token while Lycoris is elevated.
/// Explorer starts "cmd /c winget … &gt; %TEMP%\Lycoris-winget-&lt;token&gt;.log" hidden; the token makes the process
/// findable (WMI command line) for cancelling, the .log file is tailed for live output and a second file
/// receives winget's exit code. Holds the winget gate, so it never overlaps other winget calls.
/// </summary>
public static partial class UnelevatedWinget
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(30);
    private const string FilePrefix = "Lycoris-winget-";

    public static Task<UnelevatedResult> RunAsync(IReadOnlyList<string> wingetArgs, Action<string>? output, CancellationToken ct) =>
        WingetClient.RunExclusiveAsync(token => RunCoreAsync(wingetArgs, output, token), ct);

    internal static async Task<UnelevatedResult> RunForRemovalAsync(string sid, IReadOnlyList<string> args,
        Func<bool>? validateBeforeLaunch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sid)) return new(UnelevatedOutcome.LaunchFailed, -1, "");
        var command = args.ToArray();
        try
        {
            return await WingetClient.RunExclusiveAsync(token => RunCoreAsync(command, null, token, sid, validateBeforeLaunch), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The wait for the shared gate was cancelled before the shell was asked to start anything.
            return new(UnelevatedOutcome.LaunchFailed, -1, "");
        }
    }

    /// <summary>Deletes log/exit files left by runs that were still going when Lycoris stopped waiting (older than a day).</summary>
    private static void SweepStale(string temp)
    {
        try
        {
            foreach (var f in new DirectoryInfo(temp).EnumerateFiles(FilePrefix + "*"))
                if (f.Extension is ".log" or ".exit" or ".json" && !f.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    && DateTime.UtcNow - f.LastWriteTimeUtc > TimeSpan.FromDays(1))
                    TryDelete(f.FullName);
        }
        catch { /* best effort */ }
    }

    private static async Task<UnelevatedResult> RunCoreAsync(IReadOnlyList<string> wingetArgs, Action<string>? output,
        CancellationToken ct, string? verifiedSid = null, Func<bool>? validateBeforeLaunch = null)
    {
        var strict = verifiedSid is not null;
        var command = wingetArgs.FirstOrDefault();
        var sourceProbe = strict && IsRemovalSourceProbe(wingetArgs);
        var versionProbe = strict && IsRemovalVersionProbe(wingetArgs);
        if (strict && !IsStrictRemovalCommand(wingetArgs))
            return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, "");
        if (SafetyLock.IsLocked && !sourceProbe && WingetClient.IsModifying(wingetArgs))
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
            var exe = strict ? RegisteredWingetExe(verifiedSid!) : WingetExeForShell();
            if (exe is null) return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, "");
            cmdLine = BuildCommandLine(exe, wingetArgs, logName, exitName, strict);
        }
        catch (ArgumentException ex)
        {
            return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, ex.Message);
        }

        if (!strict)
        {
            var argsText = string.Join(' ', wingetArgs);
            Log.Write(LogLevel.Command, "winget (без прав администратора) " + argsText, null, Loc.Tf("winget (без прав администратора) {0}", argsText)); // loc:ignore Russian text for the log file, translated for the console
        }
        var started = DateTime.UtcNow;
        var launchTimer = Stopwatch.StartNew();
        const int pending = 0, dispatching = 1, abandoned = 2;
        var launchState = pending;
        bool BeforeShellDispatch()
        {
            if (ct.IsCancellationRequested || launchTimer.Elapsed >= StartTimeout || !IsUnelevatedDesktopUser(verifiedSid!) ||
                command == "uninstall" && (SafetyLock.IsLocked || !Elevation.IsElevated || validateBeforeLaunch?.Invoke() != true))
                return false;
            return !ct.IsCancellationRequested && launchTimer.Elapsed < StartTimeout &&
                (command != "uninstall" || !SafetyLock.IsLocked && Elevation.IsElevated) &&
                Interlocked.CompareExchange(ref launchState, dispatching, pending) == pending;
        }
        var launch = UnelevatedShell.ShellExecuteAsync(ProcessRunner.Sys("cmd.exe"), cmdLine, ProcessRunner.System32,
            UnelevatedShell.SwHide, strict ? BeforeShellDispatch : null);
        if (strict)
        {
            try
            {
                if (!await launch.WaitAsync(StartTimeout, ct).ConfigureAwait(false))
                    return new UnelevatedResult(Volatile.Read(ref launchState) == dispatching
                        ? UnelevatedOutcome.Untracked : UnelevatedOutcome.LaunchFailed, -1, "");
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                if (Interlocked.CompareExchange(ref launchState, abandoned, pending) == pending)
                    return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, "");
                return new UnelevatedResult(UnelevatedOutcome.Untracked, -1, "") { StillRunning = true };
            }
        }
        else if (!await launch.ConfigureAwait(false)) return new UnelevatedResult(UnelevatedOutcome.LaunchFailed, -1, "");

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
                    if (strict)
                    {
                        stillRunning = true;
                        return new UnelevatedResult(UnelevatedOutcome.Untracked, -1, "") { StillRunning = true };
                    }
                    await KillAsync(token).ConfigureAwait(false);
                    tail.Pump(final: true);
                    return new UnelevatedResult(UnelevatedOutcome.Cancelled, -1, all.ToString());
                }

                if (!strict) tail.Pump(final: false);

                if (TryReadExitCode(exitPath, out var code, strict))
                {
                    if (strict)
                    {
                        var evidence = sourceProbe ? ReadEvidence(logPath, 64 * 1024)
                            : versionProbe ? ReadEvidence(logPath, 256) : "";
                        return new UnelevatedResult(UnelevatedOutcome.Completed, code, evidence);
                    }
                    await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                    tail.Pump(final: true);
                    if (code == 0 || WingetClient.IsBenign(code)) Log.Success("winget {0} (без прав администратора): {1}", wingetArgs.FirstOrDefault(), WingetClient.Describe(code));
                    else Log.Warn("winget {0} (без прав администратора): {1}", wingetArgs.FirstOrDefault(), WingetClient.Describe(code));
                    return new UnelevatedResult(UnelevatedOutcome.Completed, code, all.ToString());
                }

                var elapsed = DateTime.UtcNow - started;
                if (!(strict ? File.Exists(logPath) : tail.Exists) && elapsed > StartTimeout)
                {
                    // The shell did not start it, or it runs under another account's TEMP folder.
                    Log.Warn("winget без прав администратора: не удалось отследить выполнение — результат будет проверен повторно.");
                    stillRunning = strict;
                    return new UnelevatedResult(UnelevatedOutcome.Untracked, -1, all.ToString()) { StillRunning = stillRunning };
                }
                if (elapsed > RunTimeout)
                {
                    // Never kill on timeout: that would also kill the installer and can leave a half-installed program.
                    if (!strict) tail.Pump(final: true);
                    Log.Warn("winget без прав администратора: идёт дольше 30 минут — Lycoris перестал ждать, процесс не прерывался.");
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
    public static string BuildCommandLine(string wingetExe, IReadOnlyList<string> wingetArgs, string logFileName, string exitFileName) =>
        BuildCommandLine(wingetExe, wingetArgs, logFileName, exitFileName, strictRemoval: false);

    private static string BuildCommandLine(string wingetExe, IReadOnlyList<string> wingetArgs, string logFileName,
        string exitFileName, bool strictRemoval)
    {
        if (string.IsNullOrWhiteSpace(wingetExe) || wingetExe.IndexOfAny(['"', '%', '!', '^', '\r', '\n']) >= 0)
            throw new ArgumentException(Loc.T("Недопустимый путь к winget."));
        if (!strictRemoval)
        {
            for (var i = 0; i < wingetArgs.Count; i++)
            {
                var a = wingetArgs[i];
                var ok = IsLocationValue(wingetArgs, i) ? WingetClient.IsValidLocationTemplate(a) : IsSafeToken(a);
                if (!ok) throw new ArgumentException(Loc.Tf("Недопустимый аргумент winget: «{0}»", a));
            }
        }
        foreach (var f in new[] { logFileName, exitFileName })
            if (!IsSafeToken(f)) throw new ArgumentException(Loc.Tf("Недопустимое имя файла: «{0}»", f));

        var sb = new StringBuilder("/d /v:on /s /c \"");
        sb.Append('"').Append(wingetExe).Append('"');
        for (var i = 0; i < wingetArgs.Count; i++)
        {
            sb.Append(' ');
            if (strictRemoval ? IsRemovalValue(wingetArgs, i) : IsLocationValue(wingetArgs, i))
                sb.Append('"').Append(wingetArgs[i]).Append('"');
            else sb.Append(wingetArgs[i]);
        }
        sb.Append(" >\"%TEMP%\\").Append(logFileName).Append("\" 2>&1");
        sb.Append(" & (echo !ERRORLEVEL!)>\"%TEMP%\\").Append(exitFileName).Append('"');
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsLocationValue(IReadOnlyList<string> args, int index) =>
        index > 0 && string.Equals(args[index - 1], "--location", StringComparison.Ordinal);

    private static bool IsRemovalSourceProbe(IReadOnlyList<string> args) =>
        args is ["source", "export", "--name", "winget", "--disable-interactivity"];

    private static bool IsRemovalVersionProbe(IReadOnlyList<string> args) => args is ["--version"];

    private static bool IsRemovalValue(IReadOnlyList<string> args, int index) =>
        (args[0] is "list" or "uninstall" && index == 2) || args[0] == "uninstall" && index is 10 or 12;

    /// <summary>Only the exact read probes and interactive, fully pinned native ARP uninstall are allowed here.</summary>
    internal static bool IsStrictRemovalCommand(IReadOnlyList<string> args)
    {
        if (IsRemovalSourceProbe(args) || IsRemovalVersionProbe(args)) return true;
        if (args is not [("list" or "uninstall"), "--id", var id, "--exact", "--source", "winget", "--scope",
            var scope, "--disable-interactivity", ..]) return false;
        var parts = id.Split('\\');
        if (parts.Length != 4 || parts[0] != "ARP" || !IsSafeProductCode(parts[3])) return false;
        var scopeMatches = parts[1] switch
        {
            "Machine" => scope == "machine" && parts[2] is "X64" or "X86",
            "User" => scope == "user" && parts[2] == "X64",
            _ => false,
        };
        if (!scopeMatches) return false;
        return args[0] == "list" ? args.Count == 9 :
            args.Count == 14 && args[9] == "--version" && IsSafeRemovalVersion(args[10]) &&
            args[11] == "--product-code" && args[12] == parts[3] && args[13] == "--interactive";
    }

    internal static bool IsSafeProductCode(string code)
    {
        if (IsSafeRemovalVersion(code)) return true;
        if (code.Length is < 38 or > 128 || !Guid.TryParseExact(code.AsSpan(0, 38), "B", out _)) return false;
        if (code.Length == 38) return true;
        // Inno and some other installers append a subkey suffix such as _is1 to the braced GUID.
        return code[38] == '_' && code.Length > 39 && char.IsAsciiLetterOrDigit(code[^1]) &&
            code[39..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    internal static bool IsSafeRemovalVersion(string value) =>
        value.Length is > 0 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) && char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '_' or '-' or ' ');

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

    private static bool TryReadExitCode(string path, out int code, bool strict)
    {
        code = 0;
        if (strict) return TryParseExitCode(ReadEvidence(path, 32), out code);
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

    private static string ReadEvidence(string path, int maxBytes)
    {
        try
        {
            var bytes = SafeIo.ReadTailNoFollow(path, maxBytes + 1);
            return bytes.Length > maxBytes ? "" : Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return ""; }
    }

    internal const string AppInstallerFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    private const uint AppExecutionLink = 0x8000001B;

    private static string? RegisteredWingetExe(string sid)
    {
        try
        {
            var alias = WingetClient.ExePath;
            if (alias is null || !Path.IsPathFullyQualified(alias)) return null;
            using (var link = CreateFileW(alias, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero))
            {
                // Read the tag of the canonical alias itself rather than resolving a user-writable reparse point.
                if (link.IsInvalid || !GetFileInformationByHandleEx(link, 9, out var info, (uint)Marshal.SizeOf<AttributeTagInfo>())
                    || info.ReparseTag != AppExecutionLink) return null;
            }

            var package = new PackageManager().FindPackagesForUser(sid, AppInstallerFamily)
                .SingleOrDefault(p => !p.IsFramework && !p.IsResourcePackage && !p.IsBundle);
            if (package is null || package.Id.FamilyName != AppInstallerFamily || package.IsDevelopmentMode ||
                package.SignatureKind is not (PackageSignatureKind.Store or PackageSignatureKind.System) ||
                !package.Status.VerifyIsOK()) return null;

            var protectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var packageDir = Path.Combine(protectedRoot, package.Id.FullName);
            if (!Path.IsPathFullyQualified(packageDir) || !string.Equals(package.InstalledLocation.Path, packageDir, StringComparison.OrdinalIgnoreCase))
                return null;
            var exe = Path.Combine(packageDir, "winget.exe");
            if (new[] { protectedRoot, packageDir, exe }.Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
                return null;

            // Use the checked file in the protected installation; the alias could be replaced before Explorer starts cmd.
            return Authenticode.Check(exe) == SignatureState.Valid &&
                string.Equals(Authenticode.SignerName(exe), "Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ? exe : null;
        }
        catch (Exception ex) when (ex is SystemException) { return null; }
    }

    internal static bool IsUnelevatedDesktopUser(string sid)
    {
        try
        {
            var window = GetShellWindow();
            if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out var pid) == 0) return false;
            using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (process.IsInvalid || !OpenProcessToken(process, 0x0008, out var token)) return false; // TOKEN_QUERY
            using (token)
            using (var owner = new WindowsIdentity(token.DangerousGetHandle()))
                return string.Equals(owner.User?.Value, sid, StringComparison.OrdinalIgnoreCase)
                    && GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) && elevated == 0; // TokenElevation
        }
        catch (Exception ex) when (ex is SystemException) { return false; }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token,
        int infoClass, out int info, int length, out int returned);
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTagInfo { public uint Attributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(
        string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,
        int infoClass, out AttributeTagInfo info, uint size);

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
