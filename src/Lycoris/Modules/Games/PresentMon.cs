using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Tools.DpcLatency;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Modules.Games;

/// <summary>Where the PresentMon console is and whether it may be run.</summary>
/// <param name="ExePath">The verified console exe, or null.</param>
/// <param name="Problem">Why it cannot be used (not installed, not signed by Intel, folder writable by users); null when ready.</param>
/// <param name="Installed">The Intel PresentMon package is on the PC (even when its console is missing or refused).</param>
public sealed record PresentMonStatus(string? ExePath, string? Problem, bool Installed)
{
    public bool Ready => ExePath is not null;
}

/// <summary>
/// Intel PresentMon console (MIT, signed by Intel). The winget package <c>Intel.PresentMon</c> (catalog entry on the Apps page, machine
/// scope MSI) installs it next to the overlay app: <c>%ProgramFiles%\Intel\PresentMon\PresentMonConsoleApplication\PresentMon-&lt;ver&gt;-x64.exe</c>
/// (source: GameTechDev/PresentMon IntelPresentMon/PMInstaller/PresentMon.wxs + PMPreProcessor.GetConsoleAppFileName).
/// It records frame events through ETW, which needs administrator rights (or the «Performance Log Users» group) — Lycoris
/// runs it only elevated. Before every run: the folder must be writable only by SYSTEM/Administrators (<see cref="PathTrust"/>)
/// and the file must carry a valid Authenticode signature of Intel (<see cref="Authenticode"/>) — Smart App Control runs only
/// signed code anyway, and an elevated process never starts an unverified exe. Nothing is downloaded here: a missing PresentMon
/// is installed through the Apps page (winget, hash-checked).
/// </summary>
public static partial class PresentMon
{
    public const string WingetId = "Intel.PresentMon";
    public const string ExpectedSigner = "Intel Corporation";
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 258;

    [GeneratedRegex(@"^PresentMon-(\d+(?:\.\d+){1,3})-x64\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleName();

    public static string InstallRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Intel", "PresentMon");

    public static string ConsoleDir => Path.Combine(InstallRoot, "PresentMonConsoleApplication");

    /// <summary>The newest «PresentMon-x.y.z-x64.exe» among file names (other names are ignored). Pure.</summary>
    public static string? PickConsole(IEnumerable<string> fileNames)
    {
        string? best = null;
        Version? bestVer = null;
        foreach (var f in fileNames)
        {
            var name = Path.GetFileName(f);
            var m = ConsoleName().Match(name);
            if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) continue;
            if (bestVer is null || v > bestVer) (best, bestVer) = (f, v);
        }
        return best;
    }

    /// <summary>Decision on a found console exe, from facts already read. Pure — unit-tested.</summary>
    public static string? Refusal(string? whyUserWritable, SignatureState signature, string? signer)
    {
        if (whyUserWritable is not null)
            return Loc.Tf("Папка PresentMon доступна для записи обычным программам ({0}) — Lycoris не запустит оттуда файл с правами администратора.", whyUserWritable);
        if (signature != SignatureState.Valid)
            return signature switch
            {
                SignatureState.Unsigned => Loc.T("Файл PresentMon не подписан — запуск отклонён."),
                SignatureState.Tampered => Loc.T("Файл PresentMon изменён после подписи — запуск отклонён."),
                _ => Loc.T("Подпись файла PresentMon не проверена — запуск отклонён."),
            };
        if (signer is null || !string.Equals(signer.Trim(), ExpectedSigner, StringComparison.OrdinalIgnoreCase))
            return Loc.Tf("PresentMon подписан не Intel ({0}) — запуск отклонён.", signer ?? "?");
        return null;
    }

    /// <summary>Finds and verifies the console. Blocking (file system, WinVerifyTrust) — call from a background thread.</summary>
    public static PresentMonStatus Locate()
    {
        var installed = Directory.Exists(InstallRoot);
        string? exe = null;
        try
        {
            if (Directory.Exists(ConsoleDir)) exe = PickConsole(Directory.EnumerateFiles(ConsoleDir, "PresentMon-*-x64.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("PresentMon: папка не читается — {0}", ex.Message);
        }
        if (exe is null)
            return new(null, installed
                ? Loc.T("Intel PresentMon установлен без консольной программы замера — переустановите его на странице «Приложения».")
                : Loc.T("Для замера FPS нужен Intel PresentMon — бесплатная программа Intel с подписью. Установка — на странице «Приложения»."), installed);

        var refusal = Refusal(PathTrust.WhyUserWritable(exe), Authenticode.Check(exe), Authenticode.SignerName(exe));
        return refusal is null ? new(exe, null, true) : new(null, refusal, true);
    }

    /// <summary>
    /// Captures <paramref name="seconds"/> seconds of frames of <paramref name="processExe"/>. Re-verifies the exe right before
    /// starting it. The CSV goes to the private temp folder (admin-only when elevated) and is deleted afterwards.
    /// Returns the stats, or an error text for the user.
    /// </summary>
    public static async Task<(FpsStats? Stats, string? Error)> CaptureAsync(string processExe, int seconds, CancellationToken ct)
    {
        if (!FpsMeasure.DurationChoices.Contains(seconds))
            return (null, Loc.T("Длительность замера должна составлять 15, 30 или 60 секунд."));
        if (!Elevation.IsElevated) return (null, Loc.T("Замер FPS нужен с правами администратора (PresentMon читает события Windows)."));
        if (FpsMeasure.NormalizeProcess(processExe) is not { } exeName) return (null, Loc.T("Недопустимое имя процесса."));
        using var target = await Task.Run(() => CaptureTarget.Find(exeName), ct).ConfigureAwait(false);
        if (target is null)
            return (null, Loc.Tf("Для замера запустите один экземпляр {0} с открытым окном в этом сеансе Windows.", exeName));
        var status = await Task.Run(Locate, ct).ConfigureAwait(false);
        if (status.ExePath is not { } exe) return (null, status.Problem);
        if (SafeIo.NewPrivateTempFile(".csv") is not { } csv) return (null, Loc.T("Нет личной временной папки Lycoris для файла замера."));
        try
        {
            var trace = new CaptureTrace(TraceControl);
            var options = new ProcessRunOptions
            {
                Encoding = OutputEncoding.Utf8,
                Timeout = TimeSpan.FromSeconds(seconds + 30),
                WorkingDirectory = Path.GetDirectoryName(exe),
                LogCommand = false,
            };
            var run = await trace.RunAsync(token => ProcessRunner.RunAsync(exe,
                FpsMeasure.BuildArguments(target.Pid, seconds, csv, trace.Name), options, token), target.IsCurrent, ct)
                .ConfigureAwait(false);
            if (!run.TraceClean)
                return (null, Loc.T("Не удалось подтвердить состояние сеанса замера PresentMon."));
            if (run.TargetChanged)
                return (null, Loc.Tf("Процесс {0} изменился во время замера. Запустите замер снова.", exeName));
            if (run.Result is not { } result || result.Cancelled || ct.IsCancellationRequested)
                return (null, Loc.T("Замер отменён."));
            if (!result.Success || !File.Exists(csv))
            {
                var tail = result.Combined.Trim();
                if (tail.Length > 300) tail = tail[^300..];
                Log.Warn("PresentMon: замер не завершён (код {0}) {1}", result.ExitCode, tail);
                return (null, Loc.Tf("PresentMon не записал кадры {0}: игра не запущена или не рисует кадры (свёрнута, на паузе).", exeName));
            }
            FpsStats? stats;
            await using (var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
                stats = FpsMeasure.ParseCsv(reader, target.Pid);
            if (ct.IsCancellationRequested) return (null, Loc.T("Замер отменён."));
            if (!target.IsCurrent())
                return (null, Loc.Tf("Процесс {0} изменился во время замера. Запустите замер снова.", exeName));
            if (stats is null)
                return (null, Loc.Tf("Слишком мало кадров от {0} — игра должна быть на экране и не на паузе весь замер.", exeName));
            Log.Info("PresentMon: PID {0}, сеанс {1}.", target.Pid, trace.Name);
            return (stats, null);
        }
        finally
        {
            try { File.Delete(csv); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record ProcessIdentity(int Pid, int Session, DateTime StartedUtc, string Image, string OwnerSid, nint Window);

    internal static bool CaptureWindowMatches(nint? expected, nint current, int processId, int windowProcessId,
        bool visible, bool minimized) => current != 0 && (expected is null || current == expected) &&
        processId == windowProcessId && visible && !minimized;

    private sealed class CaptureTarget(SafeProcessHandle handle, ProcessIdentity identity) : IDisposable
    {
        public int Pid => identity.Pid;

        public static CaptureTarget? Find(string exeName)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                if (self.SessionId == 0) return null;
                var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName));
                try
                {
                    if (candidates.Length != 1) return null;
                    var process = candidates[0];
                    if (process.Id == self.Id || process.Id <= 4 || process.SessionId != self.SessionId) return null;
                    var handle = GamesNative.Open(process.Id, GamesNative.PROCESS_QUERY_LIMITED_INFORMATION | Synchronize);
                    try
                    {
                        if (handle.IsInvalid || WaitForSingleObject(handle, 0) != WaitTimeout ||
                            ReadIdentity(process) is not { } found ||
                            !string.Equals(Path.GetFileName(found.Image), exeName, StringComparison.OrdinalIgnoreCase) ||
                            !InteractiveUser.AllSids.Contains(found.OwnerSid) ||
                            GamePaths.IsUnder(found.Image, GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows))) ||
                            GamesNative.IsCritical(process.Id) || WaitForSingleObject(handle, 0) != WaitTimeout) return null;
                        var target = new CaptureTarget(handle, found);
                        handle = null!;
                        return target;
                    }
                    finally { handle?.Dispose(); }
                }
                finally
                {
                    foreach (var candidate in candidates) candidate.Dispose();
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        public bool IsCurrent()
        {
            if (WaitForSingleObject(handle, 0) != WaitTimeout) return false;
            try
            {
                using var current = Process.GetProcessById(Pid);
                return ReadIdentity(current, identity.Window) == identity && WaitForSingleObject(handle, 0) == WaitTimeout;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
            {
                return false;
            }
        }

        private static ProcessIdentity? ReadIdentity(Process process, nint? expectedWindow = null)
        {
            var window = process.MainWindowHandle;
            if (GetWindowThreadProcessId(window, out var owner) == 0 ||
                !CaptureWindowMatches(expectedWindow, window, process.Id, owner, IsWindowVisible(window), IsIconic(window)))
                return null;
            return GamesNative.GetImagePath(process.Id) is { } image && GamesNative.GetOwnerSid(process.Id) is { } sid
                ? new(process.Id, process.SessionId, process.StartTime.ToUniversalTime(), image, sid, window) : null;
        }

        public void Dispose() => handle.Dispose();
    }

    internal const uint TraceQuery = 0;
    internal const uint TraceStop = 1;
    internal const uint TraceNotFound = 4201;
    internal readonly record struct TraceState(uint Code, Guid Id = default, ulong Handle = 0);
    internal readonly record struct CaptureRun(ProcessResult? Result, bool TargetChanged, bool TraceClean);

    internal sealed class CaptureTrace(Func<string, uint, TraceState> control)
    {
        private TraceState? _observed;
        private bool _traceChanged;
        public string Name { get; } = $"LycorisFps-{Guid.NewGuid():N}";

        public async Task<CaptureRun> RunAsync(Func<CancellationToken, Task<ProcessResult>> run,
            Func<bool> isCurrent, CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return new(null, false, true);
            if (!isCurrent()) return new(null, true, true);
            // The name is fresh, is passed only to the verified child, and is never reused or supplied by callers.
            if (Read().Code != TraceNotFound) return new(null, false, false);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task<ProcessResult>? running = null;
            ProcessResult? result = null;
            var targetChanged = false;
            var clean = false;
            try
            {
                running = run(cancel.Token);
                while (await Task.WhenAny(running, Task.Delay(250, cancel.Token)).ConfigureAwait(false) != running)
                {
                    if (cancel.IsCancellationRequested) break;
                    if (!Observe())
                    {
                        _traceChanged = true;
                        cancel.Cancel();
                        break;
                    }
                    if (isCurrent()) continue;
                    targetChanged = true;
                    cancel.Cancel();
                    break;
                }
                result = await running.ConfigureAwait(false);
                targetChanged |= !ct.IsCancellationRequested && !isCurrent();
            }
            finally
            {
                try
                {
                    if (running is { IsCompleted: false })
                    {
                        cancel.Cancel();
                        result = await running.ConfigureAwait(false);
                    }
                }
                finally { clean = Finish(result); }
            }
            return new(result, targetChanged, clean && !_traceChanged);
        }

        private TraceState Read() => control(Name, TraceQuery);

        private bool Observe()
        {
            var live = Read();
            if (live.Code == TraceNotFound) return true;
            if (!HasIdentity(live) || (_observed is { } previous && live != previous)) return false;
            _observed = live;
            return true;
        }

        private bool Finish(ProcessResult? result)
        {
            var live = Read();
            if (live.Code == TraceNotFound) return true;
            // In PresentMon 2.6, startup failure (including an existing session) exits with code 6.
            // A cancellation can kill our child before the first observation. Only its new, prechecked name is eligible then.
            if (!HasIdentity(live) || result?.ExitCode == 6 ||
                (_observed is { } expected ? live != expected : _traceChanged ||
                    result is not { Cancelled: true } and not { TimedOut: true }))
                return false;
            // Use the private name even after verification: Windows can recycle a numeric trace handle.
            var stopped = control(Name, TraceStop).Code;
            return (stopped == 0 || stopped == TraceNotFound) && Read().Code == TraceNotFound;
        }

        private static bool HasIdentity(TraceState trace) => trace.Code == 0 && trace.Id != Guid.Empty && trace.Handle != 0;
    }

    private static TraceState TraceControl(string name, uint operation)
    {
        const int NameBytes = 1024 * sizeof(char);
        var size = EtwSession.PropertiesSize + 2 * NameBytes;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            var properties = new EtwSession.EVENT_TRACE_PROPERTIES
            {
                Wnode = new EtwSession.WNODE_HEADER { BufferSize = (uint)size },
                LoggerNameOffset = (uint)EtwSession.PropertiesSize,
                LogFileNameOffset = (uint)(EtwSession.PropertiesSize + NameBytes),
            };
            Marshal.StructureToPtr(properties, buffer, false);
            var code = ControlTraceW(0, name, buffer, operation);
            var read = Marshal.PtrToStructure<EtwSession.EVENT_TRACE_PROPERTIES>(buffer);
            return new(code, read.Wnode.Guid, read.Wnode.HistoricalContext);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string name, nint properties, uint operation);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
