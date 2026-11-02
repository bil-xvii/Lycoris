using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Devices.Input;

internal enum RawCaptureMode
{
    /// <summary>Report arrival times while the user moves the mouse in circles (stops 5 s after the first motion, 10 s without).</summary>
    Polling,
    /// <summary>Counts summed while the DPI pad is held (60 s at most).</summary>
    Drag,
}

/// <summary>How far a capture is: <see cref="Fraction"/> 0..1, <see cref="Moving"/> once motion arrived.</summary>
internal readonly record struct RawProgress(double Fraction, bool Moving);

/// <summary>Summed counts of one device during one hold of the DPI pad.</summary>
internal readonly record struct DragCounts(long X, long Y, int Reports);

/// <summary>
/// The DPI pad as the measurement sees it: <see cref="Press"/> / <see cref="Release"/> come from the UI (the pad captured
/// the mouse); counts are summed only while it is held, so presses anywhere else are ignored.
/// </summary>
internal sealed class RawDragGate
{
    private volatile bool _held;
    private int _generation;

    public bool Held => _held;
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Raised (on the caller's thread) when a hold ends.</summary>
    public event Action? Released;

    public void Press()
    {
        Interlocked.Increment(ref _generation);
        _held = true;
    }

    public void Release()
    {
        if (!_held) return;
        _held = false;
        Released?.Invoke();
    }
}

/// <summary>What a capture collected. Never throws: problems are in <see cref="Error"/> / the flags.</summary>
internal sealed record RawCapture
{
    /// <summary>Polling: kept reports per raw device handle (relative, non-zero motion, Lycoris in the foreground).</summary>
    public IReadOnlyDictionary<IntPtr, IReadOnlyList<MotionSample>> Devices { get; init; } = new Dictionary<IntPtr, IReadOnlyList<MotionSample>>();
    public long Frequency { get; init; } = Stopwatch.Frequency;
    public int Kept { get; init; }
    /// <summary>Reports that arrived while Lycoris was not the foreground app (RIM_INPUTSINK, possibly throttled): never used.</summary>
    public int DroppedBackground { get; init; }
    public bool TimedOut { get; init; }
    /// <summary>Lycoris lost the foreground (or is closing) during the capture.</summary>
    public bool Interrupted { get; init; }
    public string? Error { get; init; }

    public bool MostlyBackground => DroppedBackground > 0 && DroppedBackground * 5 > Kept + DroppedBackground; // > 20 %
}

/// <summary>
/// A short, user-started raw-input capture of the mouse (research devices_mouse-keyboard.md §5): a dedicated STA thread
/// with its own dispatcher and a message-only window (HWND_MESSAGE) registered for the mouse class with RIDEV_INPUTSINK.
/// WM_INPUT is timestamped first and parsed into a preallocated buffer; DefWindowProc always runs. The registration is
/// removed in <c>finally</c> on the same thread (RIDEV_REMOVE, hwndTarget NULL) on every path — stop, timeout, page leave,
/// app deactivation or exit — because a background raw-input listener makes games with 1000+ Hz mice stutter.
/// Changes nothing in the system; works without admin rights.
/// </summary>
internal static class RawInputSession
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEMOUSE = 0;
    private const int RIM_INPUTSINK = 1;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x0001;
    private const ushort UsagePageGeneric = 0x01;
    private const ushort UsageMouse = 0x02;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    public static readonly TimeSpan PollingWindow = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PollingIdleLimit = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DragLimit = TimeSpan.FromSeconds(60);

    /// <summary>8 kHz × 5 s for a couple of mice, with room to spare; reports beyond it are not kept.</summary>
    private const int Capacity = 1 << 17;
    private const int BufferSize = 256;

    private static int _active;

    public static string AlreadyRunning => Loc.T("Измерение уже запущено — дождитесь его окончания.");

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetRegisteredRawInputDevices([Out] RAWINPUTDEVICE[]? pRawInputDevices, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    private static readonly uint DeviceSize = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
    /// <summary>RAWINPUTHEADER: dwType, dwSize, hDevice, wParam.</summary>
    private static readonly int HeaderSize = 8 + 2 * IntPtr.Size;

    /// <summary>
    /// Captures mouse motion until the mode's own end, <paramref name="ct"/>, a timeout, Lycoris losing the foreground or
    /// the app closing. <paramref name="progress"/> and <paramref name="onRun"/> are called on the capture thread.
    /// </summary>
    public static Task<RawCapture> CaptureAsync(RawCaptureMode mode, Action<RawProgress>? progress, CancellationToken ct,
        RawDragGate? gate = null, Action<IReadOnlyDictionary<IntPtr, DragCounts>>? onRun = null)
    {
        if (Interlocked.Exchange(ref _active, 1) == 1) return Task.FromResult(new RawCapture { Error = AlreadyRunning });

        var tcs = new TaskCompletionSource<RawCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var interrupted = 0;
        EventHandler onDeactivated = (_, _) =>
        {
            Interlocked.Exchange(ref interrupted, 1);
            try { stop.Cancel(); } catch (ObjectDisposedException) { }
        };
        ExitEventHandler onExit = (_, _) => onDeactivated(null, EventArgs.Empty);
        Ui.Post(() =>
        {
            if (Application.Current is not { } app) return;
            app.Deactivated += onDeactivated;
            app.Exit += onExit;
        });

        var worker = new Worker(mode, progress, stop.Token, gate, onRun);
        var thread = new Thread(() =>
        {
            RawCapture result;
            try { result = worker.Run(); }
            catch (Exception ex) { result = new RawCapture { Error = ex.Message }; }
            finally
            {
                Interlocked.Exchange(ref _active, 0);
                Ui.Post(() =>
                {
                    if (Application.Current is not { } app) return;
                    app.Deactivated -= onDeactivated;
                    app.Exit -= onExit;
                });
            }
            if (Volatile.Read(ref interrupted) == 1 && !ct.IsCancellationRequested) result = result with { Interrupted = true };
            stop.Dispose();
            tcs.TrySetResult(result);
        })
        {
            IsBackground = true,
            Name = "Lycoris raw input",
        };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _active, 0);
            stop.Dispose();
            return Task.FromResult(new RawCapture { Error = ex.Message });
        }
        return tcs.Task;
    }

    /// <summary>The process already listens to the mouse class (only one window per class and process: never override it).</summary>
    private static bool MouseClassRegistered()
    {
        uint count = 0;
        GetRegisteredRawInputDevices(null, ref count, DeviceSize);
        if (count == 0) return false;
        var list = new RAWINPUTDEVICE[count];
        var got = GetRegisteredRawInputDevices(list, ref count, DeviceSize);
        if (got == uint.MaxValue) return true; // cannot tell: refuse rather than override someone's registration
        for (var i = 0; i < got && i < list.Length; i++)
            if (list[i].usUsagePage == UsagePageGeneric && list[i].usUsage == UsageMouse) return true;
        return false;
    }

    /// <summary>Everything that lives on the capture thread.</summary>
    private sealed class Worker(RawCaptureMode mode, Action<RawProgress>? progress, CancellationToken ct, RawDragGate? gate,
        Action<IReadOnlyDictionary<IntPtr, DragCounts>>? onRun)
    {
        private readonly long[] _ticks = mode == RawCaptureMode.Polling ? new long[Capacity] : [];
        private readonly int[] _dx = mode == RawCaptureMode.Polling ? new int[Capacity] : [];
        private readonly int[] _dy = mode == RawCaptureMode.Polling ? new int[Capacity] : [];
        private readonly IntPtr[] _device = mode == RawCaptureMode.Polling ? new IntPtr[Capacity] : [];
        private readonly Dictionary<IntPtr, DragCounts> _drag = [];
        private int _count;
        private int _dropped;
        private int _dragGeneration;
        private long _start;
        private long _firstMotion;
        private bool _timedOut;
        private string? _error;

        private Dispatcher? _dispatcher;
        private HwndSource? _source;
        private DispatcherTimer? _timer;
        private IntPtr _buffer;
        private bool _registered;
        private bool _stopping;

        public RawCapture Run()
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            try
            {
                if (MouseClassRegistered()) return Result(error: AlreadyRunning);
                _buffer = Marshal.AllocHGlobal(BufferSize);
                _source = new HwndSource(new HwndSourceParameters("Lycoris raw input")
                {
                    ParentWindow = HWND_MESSAGE, Width = 0, Height = 0, WindowStyle = 0,
                });
                _source.AddHook(WndProc);
                var device = new[]
                {
                    new RAWINPUTDEVICE { usUsagePage = UsagePageGeneric, usUsage = UsageMouse, dwFlags = RIDEV_INPUTSINK, hwndTarget = _source.Handle },
                };
                if (!RegisterRawInputDevices(device, 1, DeviceSize))
                    return Result(error: Loc.Tf("Windows не дала подписаться на движения мыши (код {0}).", Marshal.GetLastWin32Error()));
                _registered = true;
                _start = Stopwatch.GetTimestamp();

                if (gate is not null) gate.Released += OnGateReleased;
                using var registration = ct.Register(() => dispatcher.BeginInvoke(new Action(Stop)));
                _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, OnTick, dispatcher);
                _timer.Start();
                Dispatcher.Run(); // until Stop shuts this dispatcher down
                return Result();
            }
            catch (Exception ex)
            {
                Log.Warn("Измерение мыши: {0}", ex.Message);
                return Result(error: ex.Message);
            }
            finally
            {
                Teardown();
                if (_dispatcher is { HasShutdownStarted: false } d) d.InvokeShutdown();
            }
        }

        /// <summary>Unregisters (same thread), removes the window, frees the buffer. Idempotent.</summary>
        private void Teardown()
        {
            if (gate is not null) gate.Released -= OnGateReleased;
            _timer?.Stop();
            _timer = null;
            if (_registered)
            {
                var remove = new[] { new RAWINPUTDEVICE { usUsagePage = UsagePageGeneric, usUsage = UsageMouse, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
                if (RegisterRawInputDevices(remove, 1, DeviceSize)) _registered = false;
                else Log.Warn("Измерение мыши: подписку на ввод снять не удалось (код {0}).", Marshal.GetLastWin32Error());
            }
            if (_source is { } source)
            {
                _source = null;
                try
                {
                    source.RemoveHook(WndProc);
                    source.Dispose();
                }
                catch (Exception ex) { Log.Warn("Измерение мыши: {0}", ex.Message); }
            }
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
        }

        private void Stop()
        {
            if (_stopping) return;
            _stopping = true;
            Teardown();
            _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                var now = Stopwatch.GetTimestamp();
                if (mode == RawCaptureMode.Polling)
                {
                    if (_firstMotion != 0)
                    {
                        var moving = Stopwatch.GetElapsedTime(_firstMotion, now);
                        progress?.Invoke(new RawProgress(Math.Clamp(moving / PollingWindow, 0, 1), true));
                        if (moving >= PollingWindow || _count >= Capacity) Stop();
                    }
                    else
                    {
                        progress?.Invoke(new RawProgress(0, false));
                        if (Stopwatch.GetElapsedTime(_start, now) >= PollingIdleLimit)
                        {
                            _timedOut = true;
                            Stop();
                        }
                    }
                }
                else if (Stopwatch.GetElapsedTime(_start, now) >= DragLimit)
                {
                    _timedOut = true;
                    Stop();
                }
            }
            catch (Exception ex)
            {
                _error ??= ex.Message;
                Stop();
            }
        }

        // The hold ended: the counts of that hold become one run (on this thread, after the reports queued before it).
        private void OnGateReleased() => _dispatcher?.BeginInvoke(new Action(FinishRun));

        private void FinishRun()
        {
            if (_stopping) return;
            // A hold without any motion reports an empty run (the tool says «too short»).
            var run = new Dictionary<IntPtr, DragCounts>(_drag);
            _drag.Clear();
            try { onRun?.Invoke(run); }
            catch (Exception ex) { Log.Warn("Проверка DPI: {0}", ex.Message); }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_INPUT) return IntPtr.Zero;
            var t = Stopwatch.GetTimestamp(); // first: the arrival time is the measurement
            try { OnInput(t, wParam, lParam); }
            catch (Exception ex) { _error ??= ex.Message; }
            return IntPtr.Zero; // not handled: DefWindowProc runs and lets the system clean up
        }

        private void OnInput(long t, IntPtr wParam, IntPtr lParam)
        {
            if (_buffer == IntPtr.Zero) return;
            var size = (uint)BufferSize;
            var read = GetRawInputData(lParam, RID_INPUT, _buffer, ref size, (uint)HeaderSize);
            if (read == uint.MaxValue || read < HeaderSize + 20) return;
            if ((uint)Marshal.ReadInt32(_buffer, 0) != RIM_TYPEMOUSE) return;
            var device = Marshal.ReadIntPtr(_buffer, 8);
            var flags = (ushort)Marshal.ReadInt16(_buffer, HeaderSize);
            var dx = Marshal.ReadInt32(_buffer, HeaderSize + 12);
            var dy = Marshal.ReadInt32(_buffer, HeaderSize + 16);
            if ((flags & MOUSE_MOVE_ABSOLUTE) != 0 || (dx == 0 && dy == 0)) return;
            if (device == IntPtr.Zero) return; // precision touchpad / injected input
            if (((long)wParam & 0xFF) == RIM_INPUTSINK)
            {
                _dropped++; // Lycoris was not in the foreground: possibly throttled, never used
                return;
            }

            if (mode == RawCaptureMode.Polling)
            {
                if (_count >= Capacity) return;
                if (_firstMotion == 0) _firstMotion = t;
                _ticks[_count] = t;
                _dx[_count] = dx;
                _dy[_count] = dy;
                _device[_count] = device;
                _count++;
                return;
            }

            if (gate is not { Held: true }) return; // only while the pad is held
            var generation = gate.Generation;
            if (generation != _dragGeneration)
            {
                _drag.Clear(); // a new hold started
                _dragGeneration = generation;
            }
            _drag[device] = _drag.TryGetValue(device, out var c) ? new DragCounts(c.X + dx, c.Y + dy, c.Reports + 1) : new DragCounts(dx, dy, 1);
            _count++;
        }

        public RawCapture Result(string? error = null, bool interrupted = false)
        {
            var devices = new Dictionary<IntPtr, IReadOnlyList<MotionSample>>();
            if (mode == RawCaptureMode.Polling)
            {
                var lists = new Dictionary<IntPtr, List<MotionSample>>();
                for (var i = 0; i < _count && i < _ticks.Length; i++)
                {
                    if (!lists.TryGetValue(_device[i], out var list)) lists[_device[i]] = list = [];
                    list.Add(new MotionSample(_ticks[i], _dx[i], _dy[i]));
                }
                foreach (var (handle, list) in lists) devices[handle] = list;
            }
            return new RawCapture
            {
                Devices = devices, Kept = _count, DroppedBackground = _dropped, TimedOut = _timedOut, Interrupted = interrupted,
                Error = error ?? _error,
            };
        }
    }
}
