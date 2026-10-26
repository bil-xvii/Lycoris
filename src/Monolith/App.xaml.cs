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
///  1. Without admin rights → ask for them via UAC (declined = read-only mode).
///  2. One instance only.
///  3. Load settings, collect system info in the background, open the main window.
/// Command line:
///  --no-elevate            stay in read-only mode (no UAC prompt)
///  --page &lt;key&gt;           open a specific page
///  --snapshot &lt;dir&gt;       render every page to PNG and exit (QA)
///  --pages a,b,c --wait ms limit / tune --snapshot
///  --selftest &lt;dir&gt;       exercise every page with all system writes locked; report.md + exit code = failed pages
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        if (!Elevation.IsElevated && !noElevate)
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
            _singleInstance = new Mutex(false, @"Local\MONOLITH.SingleInstance");
            bool acquired;
            try
            {
                acquired = _singleInstance.WaitOne(HasArg(args, "--elevated") ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true; // previous instance crashed — we own it now
            }
            if (!acquired)
            {
                _singleInstance.Dispose();
                _singleInstance = null;
                ActivateExistingInstance();
                Shutdown();
                return;
            }
            _ownsMutex = true;
        }

        Log.PruneOldLogs();
        AppSettings.Load();
        _ = SystemProfile.LoadAsync();
        Log.Info($"MONOLITH запущен{(Elevation.IsElevated ? " с правами администратора" : " в режиме просмотра (без прав администратора)")}.");

        var vm = new MainViewModel();
        var window = new MainWindow(vm);
        MainWindow = window;

        var startPage = ArgValue(args, "--page") ?? "dashboard";
        vm.NavigateTo(startPage);

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
                    await window.SnapshotPagesAsync(Path.GetFullPath(snapshotDir!), only, wait);
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
        try { ChangeJournal.Instance.Save(); } catch { }
        if (_ownsMutex)
        {
            try { _singleInstance?.ReleaseMutex(); } catch { }
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the app alive: log, notify, continue.
        if (SelfTestRunner.IsRunning) SelfTestRunner.ReportUnhandled(e.Exception);
        Log.Error("Ошибка интерфейса", e.Exception);
        Ui.Toast(e.Exception.Message, ToastKind.Error, "Непредвиденная ошибка");
        e.Handled = true;
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id || p.MainWindowHandle == IntPtr.Zero) continue;
                ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(p.MainWindowHandle);
                break;
            }
        }
        catch { }
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
