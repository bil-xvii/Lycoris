using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith;

/// <summary>
/// Startup:
///  1. One instance only.
///  2. Load settings. Only with «Запрашивать права при запуске» on: without admin rights → ask via UAC
///     (declined = read-only mode). By default MONOLITH opens instantly read-only and asks for rights
///     the first time a change needs them (PageViewModel.RequireAdmin), reopening on the same page.
///  3. Collect system info in the background, open the main window.
/// Command line:
///  --no-elevate            stay in read-only mode (no UAC prompt, even with the setting on)
///  --page &lt;key&gt;           open a specific page
///  --tab &lt;key&gt;            with --page: open that page's tab ("--page devices --tab manual", "--page services --tab tasks")
///  --replay-page &lt;key&gt; --replay-command &lt;Name&gt; [--replay-param &lt;text&gt;]
///                          INTERNAL (Shell/AdminIntent): set by MONOLITH when it relaunches itself with admin rights
///                          after the user clicked an action; the elevated copy opens the page and runs that page's
///                          «{Name}Command» once, with its usual confirmation. Only honoured together with --elevated.
///  --snapshot &lt;dir&gt;       render every page to PNG and exit (QA); tabbed pages once per tab ("devices-auto", "devices-manual")
///  --pages a,b,c --wait ms limit / tune --snapshot (a page key shoots all its tabs, "devices-manual" only that tab)
///  --full                  with --snapshot: also "&lt;page&gt;-full.png" — the page's whole scrolled list, not only the window
///  --selftest &lt;dir&gt;       exercise every page with all system writes locked; report.md + exit code = failed pages
///  --display-guard …       INTERNAL: the guard of a display change (Core/Devices/DisplaySafeApply). Started by MONOLITH
///                          itself, no window; it reverts the display mode/HDR if MONOLITH dies or the user never
///                          confirms. Runs before every other startup step and marks itself (DisplayGuardProcess) so a
///                          new MONOLITH never mistakes it for a hung copy.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private const string SingleInstanceName = @"Local\MONOLITH.SingleInstance";
    /// <summary>The display guard's marker, held for the life of the guard process.</summary>
    private static IDisposable? _guardMarker;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The display guard: before the instance lock, settings, UAC and logging setup. It only waits and, if needed,
        // puts the previous display mode back.
        if (e.Args.Length > 0 && e.Args[0] == Monolith.Core.Devices.DisplayGuardProcess.Switch)
        {
            _guardMarker = Monolith.Core.Devices.DisplayGuardProcess.MarkCurrentProcess();
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = Monolith.Core.Devices.DeviceWriters.Display?.RunGuard(e.Args) ?? 2; }
            catch { code = 3; }
            Shutdown(code);
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Необработанная ошибка: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Ошибка фоновой задачи", ex.Exception);
            ex.SetObserved();
        };

        base.OnStartup(e);

        var args = e.Args;
        var snapshotDir = ArgValue(args, "--snapshot");
        var selfTestDir = ArgValue(args, "--selftest");
        var e2eDir = ArgValue(args, "--e2e");
        if (e2eDir is not null)
        {
            RunEndToEnd(args, e2eDir);
            return;
        }
        var snapshot = snapshotDir is not null || selfTestDir is not null;
        var noElevate = snapshot || HasArg(args, "--no-elevate") || HasArg(args, "--elevated");
        if (selfTestDir is not null)
        {
            // Lock every system-changing API before anything else runs.
            ChangeSession.Locked = true;
            Ui.AutoAnswer = false;
        }

        // Already running? Bring that window up instead of asking for admin rights again. A copy that is still
        // closing (or hangs without a window) is waited for / offered to be ended — never a silent exit.
        if (!snapshot && !HasArg(args, "--elevated") && SingleInstanceMutexExists() && !HandOverToExistingInstance())
        {
            Shutdown();
            return;
        }

        // Preferences decide whether to ask for admin rights right away (off by default: open instantly, ask later).
        AppSettings.Load();
        if (!Elevation.IsElevated && !noElevate && AppSettings.Current.ElevateAtStartup)
        {
            if (Elevation.TryRelaunchElevated())
            {
                Shutdown();
                return;
            }
        }

        if (!snapshot)
        {
            // When relaunched with admin rights the previous (non-elevated) copy is still closing: wait for it.
            if (HasArg(args, "--elevated")) WaitForParentExit(ArgValue(args, "--parent"));
            bool acquired;
            try
            {
                _singleInstance = new Mutex(false, SingleInstanceName);
                acquired = _singleInstance.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // previous instance crashed — we own it now
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось проверить, запущен ли MONOLITH", ex);
                _singleInstance?.Dispose();
                _singleInstance = null;
                ActivateExistingInstance();
                Shutdown();
                return;
            }
            if (!acquired && HandOverToExistingInstance())
            {
                // The other copy has gone (it was closing, or the user ended a hung one): take over now.
                try { acquired = _singleInstance!.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { acquired = true; }
            }
            if (!acquired)
            {
                _singleInstance?.Dispose();
                _singleInstance = null;
                Shutdown();
                return;
            }
            _ownsMutex = true;
        }

        Log.PruneOldLogs();
        if (Elevation.IsElevated && !snapshot)
        {
            // Lock down %ProgramData%\MONOLITH before the journal is read, then repair an interrupted restore point.
            AppPaths.SecureMachineDir();
            Monolith.Modules.Drivers.DriverBackupService.QuarantineIfInsecure();
            RestorePointService.RecoverPendingFrequency();
        }
        _ = SystemProfile.LoadAsync();
        Log.Info($"MONOLITH запущен{(Elevation.IsElevated ? " с правами администратора" : " в режиме просмотра (без прав администратора)")}.");

        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;
        if (!snapshot) window.Closed += (_, _) => StartExitWatchdog();

        var startPage = ArgValue(args, "--page") ?? "dashboard";
        // A tab of the start page ("--tab manual"): a focus request the page takes on its first load.
        if (ArgValue(args, "--tab") is { Length: > 0 } tab)
            Ui.RequestFocus(MainViewModel.ResolvePageKey(startPage), "tab:" + tab);
        vm.NavigateTo(startPage);
        // «--replay-page … --replay-command …»: the action the user clicked before the relaunch with admin rights.
        // Only an elevated copy started by MONOLITH itself replays it, once, after the startup checks.
        if (!snapshot && HasArg(args, "--elevated") && Elevation.IsElevated && AdminIntent.Parse(args) is { } intent)
            vm.PendingIntent = intent;

        if (snapshot)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -20000;
            window.Top = 0;
            window.Width = 1440;
            window.Height = 900;
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.Show();

            if (selfTestDir is not null)
            {
                window.Dispatcher.InvokeAsync(async () =>
                {
                    var dir = Path.GetFullPath(selfTestDir);
                    var failures = 99;
                    try
                    {
                        await Task.Delay(1000);
                        failures = await new SelfTestRunner(window, vm, dir).RunAsync();
                    }
                    catch (Exception ex)
                    {
                        Directory.CreateDirectory(dir);
                        File.WriteAllText(Path.Combine(dir, "selftest-crash.txt"), ex.ToString());
                    }
                    finally
                    {
                        Shutdown(failures);
                    }
                }, DispatcherPriority.ApplicationIdle);
                return;
            }

            var only = ArgValue(args, "--pages")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            var wait = int.TryParse(ArgValue(args, "--wait"), out var w) ? w : 2500;
            window.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Task.Delay(800);
                    await window.SnapshotPagesAsync(Path.GetFullPath(snapshotDir!), only, wait, HasArg(args, "--full"));
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(Path.GetFullPath(snapshotDir!), "snapshot-error.txt"), ex.ToString());
                }
                finally
                {
                    Shutdown();
                }
            }, DispatcherPriority.ApplicationIdle);
            return;
        }

        window.Show();
        _ = vm.RunStartupChecksAsync();
    }

    /// <summary>"--e2e &lt;dir&gt;": headless real-system apply/revert test (asks for admin rights once).</summary>
    private void RunEndToEnd(string[] args, string dir)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (!Elevation.IsElevated)
        {
            if (!HasArg(args, "--elevated") && Elevation.TryRelaunchElevated())
            {
                Shutdown(0);
                return;
            }
        }
        AppSettings.Load();
        if (Elevation.IsElevated)
        {
            AppPaths.SecureMachineDir();
            RestorePointService.RecoverPendingFrequency();
        }
        var full = Path.GetFullPath(dir);
        Dispatcher.InvokeAsync(async () =>
        {
            var failures = 99;
            try
            {
                failures = await EndToEndTest.RunAsync(full);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(full);
                File.WriteAllText(Path.Combine(full, "e2e-crash.txt"), ex.ToString());
            }
            finally
            {
                Shutdown(failures);
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Only an elevated MONOLITH writes the machine journal (Save is a no-op otherwise).
        if (Elevation.IsElevated)
        {
            // Closed while a restore point was being created: put the lifted 24-hour limit back now.
            try { RestorePointService.RestoreThrottleIfPending(); } catch { }
            try { ChangeJournal.Instance.Save(); } catch { }
        }
        if (_ownsMutex)
        {
            try { _singleInstance?.ReleaseMutex(); } catch { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive: log, notify, continue. While closing there is nothing to keep alive: the exit watchdog
        // ends the process, so a half-finished shutdown can never leave a windowless MONOLITH holding the instance lock.
        if (SelfTestRunner.IsRunning) SelfTestRunner.ReportUnhandled(e.Exception);
        if (_closing)
        {
            Log.Error("Ошибка при закрытии MONOLITH", e.Exception);
            e.Handled = true;
            return;
        }
        Log.Error("Ошибка интерфейса", e.Exception);
        Ui.Toast(e.Exception.Message, ToastKind.Error, "Непредвиденная ошибка");
        e.Handled = true;
    }

    private static volatile bool _closing;

    /// <summary>
    /// The main window is closed: normal shutdown (game-session undo ≤ ~10 s, journal save) gets 20 s, then the
    /// process ends no matter what. Before, an exception inside WPF's own shutdown left the process alive without a
    /// window; it kept the single-instance lock, and every new start silently gave up ("приложение не открывается").
    /// </summary>
    private static void StartExitWatchdog()
    {
        _closing = true;
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromSeconds(20));
            Log.Warn("MONOLITH не завершился за 20 с после закрытия окна — завершение принудительно.");
            Environment.Exit(0);
        })
        { IsBackground = true, Name = "MONOLITH exit watchdog" }.Start();
    }

    private static bool SingleInstanceMutexExists()
    {
        try
        {
            var exists = Mutex.TryOpenExisting(SingleInstanceName, out var existing);
            existing?.Dispose();
            return exists;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // exists, but belongs to an elevated instance we may not open
        }
    }

    /// <summary>
    /// Another copy holds the instance lock. Returns false when its window was brought up (this copy should exit) or
    /// the user chose to leave it; true when it is gone and this copy may start: it finished closing within 10 s, or
    /// it hung without a window and the user agreed to end it.
    /// </summary>
    private static bool HandOverToExistingInstance()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            if (ActivateExistingInstance()) return false;
            if (!SingleInstanceMutexExists()) return true;
            if (DateTime.UtcNow >= deadline) break;
            Thread.Sleep(500);
        }

        var hung = OtherInstances().Where(p => SafeHasNoWindow(p)).ToList();
        if (hung.Count == 0) return false;
        var answer = MessageBox.Show(
            "Предыдущая копия MONOLITH не отвечает: процесс работает, но окна у него нет — скорее всего, он завис при закрытии.\n\n" +
            "Завершить её и открыть MONOLITH? Если та копия была запущена с правами администратора, Windows попросит подтверждение.",
            "MONOLITH", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;

        foreach (var p in hung) EndProcess(p);
        for (var i = 0; i < 20 && SingleInstanceMutexExists(); i++) Thread.Sleep(500);
        if (!SingleInstanceMutexExists()) return true;
        MessageBox.Show("Не удалось завершить зависшую копию MONOLITH. Завершите процесс MONOLITH.exe в диспетчере задач " +
                        "(вкладка «Подробности») и запустите программу снова.", "MONOLITH", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>
    /// Other MONOLITH copies — never a display guard: it has no window by design, must finish its job (it may be about to
    /// put the old display mode back) and ends by itself at its deadline. It is neither activated nor offered to be ended.
    /// </summary>
    private static IEnumerable<Process> OtherInstances()
    {
        var current = Process.GetCurrentProcess();
        return Process.GetProcessesByName(current.ProcessName)
            .Where(p => p.Id != current.Id && !Monolith.Core.Devices.DisplayGuardProcess.IsGuard(p.Id));
    }

    private static bool SafeHasNoWindow(Process p)
    {
        try { p.Refresh(); return p.MainWindowHandle == IntPtr.Zero && !p.HasExited; }
        catch { return true; }
    }

    /// <summary>Ends a hung copy: directly when allowed, else (elevated copy) taskkill with a UAC prompt.</summary>
    private static void EndProcess(Process p)
    {
        try
        {
            p.Kill();
            p.WaitForExit(5000);
            return;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException)
        {
            if (ex is InvalidOperationException) return; // already gone
        }
        try
        {
            using var kill = Process.Start(new ProcessStartInfo(ProcessRunner.Sys("taskkill.exe"), $"/PID {p.Id} /F")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            kill?.WaitForExit(15000);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user said no in the UAC window: the caller reports that the copy is still there.
        }
        catch { }
    }

    /// <summary>"--parent &lt;pid&gt;": wait (up to 20 s) for the copy that relaunched us to close.</summary>
    private static void WaitForParentExit(string? pidArg)
    {
        if (!int.TryParse(pidArg, out var pid) || pid <= 0) return;
        try
        {
            using var parent = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            // The pid may already have been reused by an unrelated process.
            if (!string.Equals(parent.ProcessName, self.ProcessName, StringComparison.OrdinalIgnoreCase)) return;
            parent.WaitForExit(20000);
        }
        catch (ArgumentException) { }          // already gone
        catch (InvalidOperationException) { }  // exited meanwhile
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Brings up the window of another running copy; false when no copy has a window.</summary>
    private static bool ActivateExistingInstance()
    {
        try
        {
            foreach (var p in OtherInstances())
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static bool HasArg(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? ArgValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
