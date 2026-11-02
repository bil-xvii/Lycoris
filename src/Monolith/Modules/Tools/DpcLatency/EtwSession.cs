using System.Diagnostics;
using System.Runtime.InteropServices;
using Monolith.Core;

namespace Monolith.Modules.Tools.DpcLatency;

/// <summary>
/// A private, read-only ETW system-logger session that receives the kernel's DPC and ISR events for a fixed time.
/// Rules (docs/research/tech.md §10):
/// <list type="bullet">
/// <item>Private system logger (EVENT_TRACE_SYSTEM_LOGGER_MODE, Windows 8+) under a unique name — the global
/// «NT Kernel Logger» is never started, stopped or queried, so LatencyMon, xperf, WPR or Process Lasso running elsewhere are not touched.</item>
/// <item>On start, only stale sessions whose name begins with <see cref="LoggerPrefix"/> (a crashed MONOLITH) are stopped.</item>
/// <item>Admin only (StartTrace refuses otherwise); the session is always stopped in <c>finally</c> — on timeout, cancellation and exceptions.</item>
/// <item>Nothing is written: no log file (real-time mode), no system change.</item>
/// </list>
/// Event layout verified against Microsoft Learn (PerfInfo / DPC / ISR MOF classes) and PerfView's KernelTraceEventParser:
/// provider GUID {ce1dbfb4-137e-4da6-87b0-3f59aa102cbc}, opcodes 66 ThreadedDPC, 67 ISR, 68 DPC, 69 TimerDPC; payload
/// <c>InitialTime</c> (int64, QPC ticks) at offset 0, <c>Routine</c> (pointer) at 8; duration = event timestamp − InitialTime,
/// both in QPC ticks when the session uses ClientContext 1 and the consumer asks for raw timestamps.
/// </summary>
public static class EtwSession
{
    public const string LoggerPrefix = "MONOLITH-DpcLatency-";

    /// <summary>The only sessions MONOLITH may stop without having started them in this process.</summary>
    public static bool IsOwnLoggerName(string? name) =>
        name is not null && name.StartsWith(LoggerPrefix, StringComparison.Ordinal) && name.Length > LoggerPrefix.Length;

    public static string NewLoggerName() => LoggerPrefix + Guid.NewGuid().ToString("N");

    /// <summary>Private system loggers exist since Windows 8 (6.2) — always true on Windows 10/11.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public static readonly Guid PerfInfoGuid = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

    // ─── evntrace.h constants ───
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint EVENT_TRACE_SYSTEM_LOGGER_MODE = 0x02000000;
    private const uint EVENT_TRACE_FLAG_DPC = 0x00000020;
    private const uint EVENT_TRACE_FLAG_INTERRUPT = 0x00000040;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x00001000;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ushort EVENT_HEADER_FLAG_32_BIT_HEADER = 0x0020;
    private const uint ClientContextQpc = 1;
    private const int MaxSessions = 64;
    private const int NameChars = 1024;
    private const int NameBytes = NameChars * 2;

    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_BAD_LENGTH = 24;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_ALREADY_EXISTS = 183;
    private const int ERROR_MORE_DATA = 234;
    private const int ERROR_NO_SYSTEM_RESOURCES = 1450;
    private const int ERROR_WMI_INSTANCE_NOT_FOUND = 4201;

    internal static readonly int PropertiesSize = Marshal.SizeOf<EVENT_TRACE_PROPERTIES>();
    internal static readonly int LogFileSize = Marshal.SizeOf<EVENT_TRACE_LOGFILEW>();
    private static readonly int PropertiesBufferSize = PropertiesSize + 2 * NameBytes;

    /// <param name="StoppedForGame">A game session began during the measurement: the trace was stopped early and the partial data
    /// must not be judged.</param>
    public sealed record Result(bool Started, string? Error, long EventsLost, long BuffersLost, TimeSpan Elapsed, bool Cancelled,
        bool StoppedForGame = false);

    /// <summary>Plain-language text for a failed StartTrace.</summary>
    internal static string DescribeStartError(int code) => code switch
    {
        ERROR_ACCESS_DENIED => "нет доступа к трассировке ядра — нужны права администратора (код 5).",
        ERROR_ALREADY_EXISTS => "сеанс трассировки с таким именем уже существует — повторите попытку (код 183).",
        ERROR_NO_SYSTEM_RESOURCES => "достигнут предел системных сеансов трассировки Windows (не более 8) — закройте LatencyMon, xperf/WPR или другие профилировщики и повторите (код 1450).",
        ERROR_BAD_LENGTH or ERROR_INVALID_PARAMETER => $"Windows отклонила параметры сеанса трассировки (код {code}).",
        _ => $"не удалось запустить сеанс трассировки (код {code}).",
    };

    /// <summary>
    /// Stops sessions left behind by a crashed MONOLITH: only names that start with <see cref="LoggerPrefix"/>; every other
    /// session on the PC (NT Kernel Logger, LatencyMon, WPR …) is only listed, never touched. Returns how many were stopped.
    /// </summary>
    public static int StopStaleSessions()
    {
        var buffers = new IntPtr[MaxSessions];
        try
        {
            for (var i = 0; i < buffers.Length; i++)
            {
                buffers[i] = Marshal.AllocHGlobal(PropertiesBufferSize);
                InitProperties(buffers[i], null);
            }
            var rc = QueryAllTracesW(buffers, (uint)buffers.Length, out var count);
            if (rc != 0 && rc != ERROR_MORE_DATA)
            {
                Log.Warn($"DPC/ISR: QueryAllTraces вернула код {rc} — проверка старых сеансов пропущена.");
                return 0;
            }
            var stopped = 0;
            for (var i = 0; i < Math.Min((int)count, buffers.Length); i++)
            {
                var name = ReadLoggerName(buffers[i]);
                if (!IsOwnLoggerName(name)) continue;
                var stopRc = ControlTraceW(0, name, buffers[i], EVENT_TRACE_CONTROL_STOP);
                if (stopRc == 0 || stopRc == ERROR_WMI_INSTANCE_NOT_FOUND)
                {
                    stopped++;
                    Log.Info($"DPC/ISR: остановлен оставшийся сеанс {name}.");
                }
                else Log.Warn($"DPC/ISR: сеанс {name} не остановлен (код {stopRc}).");
            }
            return stopped;
        }
        finally
        {
            foreach (var b in buffers) if (b != IntPtr.Zero) Marshal.FreeHGlobal(b);
        }
    }

    /// <summary>
    /// Runs the measurement synchronously (call from Task.Run): starts the private session, consumes events on a dedicated
    /// thread for <paramref name="duration"/> (or until <paramref name="ct"/> is cancelled), then stops and closes everything.
    /// <paramref name="onEvent"/> receives (kind, routine address, duration µs) on the consumer thread only.
    /// A game session that begins meanwhile (<see cref="GameBoostState"/>) stops the trace at once (<see cref="Result.StoppedForGame"/>).
    /// </summary>
    public static Result Run(TimeSpan duration, Action<RoutineKind, ulong, double> onEvent, Action<int>? onPercent, CancellationToken ct)
    {
        var loggerName = NewLoggerName();
        var props = IntPtr.Zero;
        ulong session = 0;
        var trace = ulong.MaxValue;
        var sw = Stopwatch.StartNew();
        long eventsLost = 0, buffersLost = 0;
        Thread? consumer = null;
        var cancelled = false;
        var stoppedForGame = false;
        Consumer? state = null;
        Result result;

        try
        {
            props = Marshal.AllocHGlobal(PropertiesBufferSize);
            InitProperties(props, loggerName);
            var p = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(props);
            p.Wnode.Guid = Guid.NewGuid(); // our own session GUID — never SystemTraceControlGuid, that is the NT Kernel Logger
            p.Wnode.ClientContext = ClientContextQpc;
            p.BufferSize = 64;
            p.MinimumBuffers = 64;
            p.MaximumBuffers = 256;
            p.FlushTimer = 1;
            p.LogFileMode = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_SYSTEM_LOGGER_MODE;
            p.EnableFlags = EVENT_TRACE_FLAG_DPC | EVENT_TRACE_FLAG_INTERRUPT;
            Marshal.StructureToPtr(p, props, false);

            var rc = StartTraceW(out session, loggerName, props);
            if (rc != 0)
            {
                session = 0;
                return new Result(false, DescribeStartError((int)rc), 0, 0, sw.Elapsed, false);
            }
            // From here on the session exists: every exit goes through finally, which stops it.
            Log.Info($"DPC/ISR: сеанс {loggerName} запущен (DPC + INTERRUPT, {duration.TotalSeconds:0} с).");

            state = new Consumer(onEvent);
            var logfile = new EVENT_TRACE_LOGFILEW
            {
                LoggerName = loggerName,
                ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP,
                EventRecordCallback = state.CallbackPointer,
            };
            trace = OpenTraceW(ref logfile);
            if (trace == ulong.MaxValue)
            {
                var err = Marshal.GetLastWin32Error();
                result = new Result(false, $"не удалось подключиться к сеансу трассировки (код {err}).", 0, 0, sw.Elapsed, false);
                return result;
            }

            var handles = new[] { trace };
            uint processRc = 0;
            consumer = new Thread(() => processRc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero))
            {
                IsBackground = true, Name = "MONOLITH DPC/ISR trace", Priority = ThreadPriority.AboveNormal,
            };
            consumer.Start();

            var lastPercent = -1;
            while (sw.Elapsed < duration)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                if (GameBoostState.IsActive) { stoppedForGame = true; break; } // never trace during a game
                if (!consumer.IsAlive) break; // ProcessTrace ended early (session stopped by someone else)
                var percent = (int)Math.Min(99, sw.Elapsed.TotalSeconds * 100 / duration.TotalSeconds);
                if (percent != lastPercent) { lastPercent = percent; onPercent?.Invoke(percent); }
                Thread.Sleep(200);
            }
            result = stoppedForGame
                ? new Result(true, null, 0, 0, sw.Elapsed, false, StoppedForGame: true)
                : !consumer.IsAlive && !cancelled && sw.Elapsed < duration
                ? new Result(true, $"сеанс трассировки завершился раньше срока (код {processRc}) — возможно, его остановила другая программа.", 0, 0, sw.Elapsed, false)
                : new Result(true, null, 0, 0, sw.Elapsed, cancelled);
        }
        finally
        {
            // Stop first: for a real-time consumer this ends ProcessTrace after the remaining buffers are delivered.
            if (session != 0)
            {
                var stopRc = ControlTraceW(session, null, props, EVENT_TRACE_CONTROL_STOP);
                if (stopRc == 0)
                {
                    var stats = Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(props);
                    eventsLost = stats.EventsLost;
                    buffersLost = stats.RealTimeBuffersLost + stats.LogBuffersLost;
                    if (eventsLost > 0 || buffersLost > 0)
                        Log.Warn($"DPC/ISR: потеряно событий {eventsLost}, буферов {buffersLost}.");
                }
                else if (stopRc != ERROR_WMI_INSTANCE_NOT_FOUND)
                    Log.Warn($"DPC/ISR: остановка сеанса вернула код {stopRc}; повторная остановка по имени.");
                if (stopRc != 0 && stopRc != ERROR_WMI_INSTANCE_NOT_FOUND)
                    ControlTraceW(0, loggerName, props, EVENT_TRACE_CONTROL_STOP);
                Log.Info($"DPC/ISR: сеанс {loggerName} остановлен.");
            }
            if (consumer is not null && !consumer.Join(TimeSpan.FromSeconds(10)))
                Log.Warn("DPC/ISR: поток обработки событий не завершился за 10 с — трассировка закрывается принудительно.");
            if (trace != ulong.MaxValue) CloseTrace(trace);
            consumer?.Join(TimeSpan.FromSeconds(5));
            state?.Dispose();
            if (props != IntPtr.Zero) Marshal.FreeHGlobal(props);
        }
        return result with { EventsLost = eventsLost, BuffersLost = buffersLost, Elapsed = sw.Elapsed };
    }

    // ───────────────────────────── event consumer ─────────────────────────────

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void EventRecordCallback(IntPtr record);

    /// <summary>Holds the callback delegate alive for the lifetime of ProcessTrace and parses PerfInfo DPC/ISR records.</summary>
    private sealed class Consumer : IDisposable
    {
        private readonly Action<RoutineKind, ulong, double> _onEvent;
        private readonly EventRecordCallback _callback;
        private readonly double _microsPerTick = 1_000_000d / Stopwatch.Frequency;
        private readonly long _maxTicks = Stopwatch.Frequency * 10; // anything above 10 s is a clock artefact, not a DPC

        public Consumer(Action<RoutineKind, ulong, double> onEvent)
        {
            _onEvent = onEvent;
            _callback = OnRecord;
            CallbackPointer = Marshal.GetFunctionPointerForDelegate(_callback);
        }

        public IntPtr CallbackPointer { get; }

        private unsafe void OnRecord(IntPtr record)
        {
            try
            {
                if (!TryParseRecord((byte*)record, out var kind, out var routine, out var ticks)) return;
                if (ticks < 0 || ticks > _maxTicks) return;
                _onEvent(kind, routine, ticks * _microsPerTick);
            }
            catch
            {
                // never let an exception cross the native callback boundary
            }
        }

        public void Dispose() => GC.KeepAlive(_callback);
    }

    // ───────────────────────────── EVENT_RECORD parsing ─────────────────────────────

    /// <summary>
    /// Reads a PerfInfo DPC/ISR event from an EVENT_RECORD (x64). EVENT_HEADER: Flags @4, TimeStamp @16, ProviderId @24,
    /// EVENT_DESCRIPTOR @40 = { Id @40, Version @42, Channel @43, Level @44, Opcode @45, Task @46, Keyword @48 };
    /// EVENT_RECORD: UserDataLength @86, UserData @96 (sizeof = 112). Payload: InitialTime (int64) @0, Routine (pointer) @8.
    /// <paramref name="ticks"/> = TimeStamp − InitialTime (QPC ticks, may be negative for a clock artefact — the caller filters).
    /// </summary>
    internal static unsafe bool TryParseRecord(byte* p, out RoutineKind kind, out ulong routine, out long ticks)
    {
        kind = default;
        routine = 0;
        ticks = 0;
        if (p == null || *(Guid*)(p + 24) != PerfInfoGuid) return false;
        switch (p[45])
        {
            case 67: kind = RoutineKind.Isr; break;
            case 68: kind = RoutineKind.Dpc; break;
            case 66: kind = RoutineKind.ThreadedDpc; break;
            case 69: kind = RoutineKind.TimerDpc; break;
            default: return false;
        }
        var pointerSize = (*(ushort*)(p + 4) & EVENT_HEADER_FLAG_32_BIT_HEADER) != 0 ? 4 : 8;
        var length = *(ushort*)(p + 86);
        if (length < 8 + pointerSize) return false;
        var data = *(byte**)(p + 96);
        if (data == null) return false;
        var initial = *(long*)data;
        routine = pointerSize == 8 ? *(ulong*)(data + 8) : *(uint*)(data + 8);
        ticks = *(long*)(p + 16) - initial;
        return true;
    }

    /// <summary><see cref="TryParseRecord(byte*, out RoutineKind, out ulong, out long)"/> for a native buffer (unit tests).</summary>
    internal static unsafe bool TryParseRecord(IntPtr record, out RoutineKind kind, out ulong routine, out long ticks) =>
        TryParseRecord((byte*)record, out kind, out routine, out ticks);

    // ───────────────────────────── EVENT_TRACE_PROPERTIES helpers ─────────────────────────────

    private static void InitProperties(IntPtr buffer, string? loggerName)
    {
        unsafe
        {
            new Span<byte>((void*)buffer, PropertiesBufferSize).Clear();
        }
        var p = new EVENT_TRACE_PROPERTIES
        {
            Wnode = new WNODE_HEADER { BufferSize = (uint)PropertiesBufferSize, Flags = WNODE_FLAG_TRACED_GUID },
            LoggerNameOffset = (uint)PropertiesSize,
            LogFileNameOffset = (uint)(PropertiesSize + NameBytes),
        };
        Marshal.StructureToPtr(p, buffer, false);
        if (loggerName is not null)
        {
            var chars = loggerName.Length >= NameChars ? loggerName[..(NameChars - 1)] : loggerName;
            unsafe
            {
                var dst = new Span<char>((void*)(buffer + PropertiesSize), NameChars);
                chars.AsSpan().CopyTo(dst);
                dst[chars.Length] = '\0';
            }
        }
    }

    private static string? ReadLoggerName(IntPtr buffer)
    {
        var offset = (int)Marshal.PtrToStructure<EVENT_TRACE_PROPERTIES>(buffer).LoggerNameOffset;
        if (offset <= 0 || offset >= PropertiesBufferSize) return null;
        return Marshal.PtrToStringUni(buffer + offset);
    }

    // ───────────────────────────── structures (x64 layout per evntrace.h) ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNODE_HEADER
    {
        public uint BufferSize;
        public uint ProviderId;
        public ulong HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public uint ClientContext;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize;
        public uint MinimumBuffers;
        public uint MaximumBuffers;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint FlushTimer;
        public uint EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers;
        public uint FreeBuffers;
        public uint EventsLost;
        public uint BuffersWritten;
        public uint LogBuffersLost;
        public uint RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset;
        public uint LoggerNameOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE_HEADER
    {
        public ushort Size;
        public ushort FieldTypeFlags;
        public uint Version;
        public uint ThreadId;
        public uint ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong ProcessorTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct EVENT_TRACE
    {
        public EVENT_TRACE_HEADER Header;
        public uint InstanceId;
        public uint ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength;
        public uint ClientContext;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEMTIME
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct TIME_ZONE_INFORMATION
    {
        public int Bias;
        public fixed char StandardName[32];
        public SYSTEMTIME StandardDate;
        public int StandardBias;
        public fixed char DaylightName[32];
        public SYSTEMTIME DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize;
        public uint Version;
        public uint ProviderVersion;
        public uint NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution;
        public uint MaximumFileSize;
        public uint LogFileMode;
        public uint BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName;
        public IntPtr LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime;
        public long PerfFreq;
        public long StartTime;
        public uint ReservedFlags;
        public uint BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct EVENT_TRACE_LOGFILEW
    {
        public string? LogFileName;
        public string? LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize;
        public uint Filled;
        public uint EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    // ───────────────────────────── P/Invoke (advapi32 → sechost) ─────────────────────────────

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string instanceName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? instanceName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint QueryAllTracesW([In, Out] IntPtr[] propertyArray, uint propertyArrayCount, out uint loggerCount);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EVENT_TRACE_LOGFILEW logfile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
