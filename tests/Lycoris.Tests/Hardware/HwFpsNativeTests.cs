using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Lycoris.Core;
using Lycoris.Modules.Games;
using Lycoris.Modules.Tools.DpcLatency;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;

namespace Lycoris.Tests.Hardware;

[Collection("Hardware")]
public sealed class HwFpsNativeTests(ITestOutputHelper output)
{
    private const int Duration = 15;

    [FpsNativeFact]
    public async Task Two_live_captures_of_the_same_visible_workload_round_trip_through_the_store()
    {
        Assert.Null(FpsNativeFactAttribute.PrerequisiteFailure());
        var testRoot = ReadAbsolutePath(FpsNativeFactAttribute.TestRootVariable);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        Assert.Equal(temp, Path.GetDirectoryName(testRoot), ignoreCase: true);
        Assert.False(Directory.Exists(testRoot), "Set LYCORIS_TEST_ROOT to a fresh temporary directory before starting the test host.");
        Assert.Equal(testRoot, AppPaths.TestRoot);
        Assert.Equal(Path.Combine(testRoot, "machine"), AppPaths.MachineDir);
        Assert.Equal(Path.Combine(testRoot, "user"), AppPaths.UserDir);

        var processName = Environment.GetEnvironmentVariable(FpsNativeFactAttribute.ProcessVariable)!;
        Assert.Equal(processName, FpsMeasure.NormalizeProcess(processName));
        Assert.False(processName.Equals("Lycoris.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(processName.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase));
        var executable = Path.Combine(ReadAbsolutePath(FpsNativeFactAttribute.WorkloadRootVariable), processName);
        Assert.True(File.Exists(executable), "The selected rendering workload must have the exact executable in the specified directory.");
        AssertOrdinaryExecutable(executable);

        using var current = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotEqual(0, current.SessionId);
        Assert.False(identity.IsSystem);
        var owner = Assert.IsType<SecurityIdentifier>(identity.User);
        var target = ReadTarget(executable, current.SessionId, owner);
        var path = Path.Combine(testRoot, "fps-measurements.json");
        var store = new FpsStore(path);
        Assert.Equal(new FpsPair(null, null), store.Read());
        try
        {
            var (before, beforeTrace) = await CaptureAsync(FpsRecord.BeforeKind, executable, current.SessionId, owner, target);
            store.Save(before);
            Assert.Equal(new FpsPair(before, null), new FpsStore(path).Read());

            var (after, afterTrace) = await CaptureAsync(FpsRecord.AfterKind, executable, current.SessionId, owner, target);
            store.Save(after);
            Assert.Equal(new FpsPair(before, after), new FpsStore(path).Read());
            Assert.True(before.RecordedAt < after.RecordedAt);
            Assert.NotEqual(beforeTrace, afterTrace);
            Assert.Null(FpsMeasure.Compare(before, after).Warning);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private async Task<(FpsRecord Record, string Trace)> CaptureAsync(string kind, string executable, int session, SecurityIdentifier owner, Target target)
    {
        Assert.Equal(target, ReadTarget(executable, session, owner));
        var processName = Path.GetFileName(executable);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var log = new ConcurrentQueue<string>();
        void OnLog(LogEntry entry) => log.Enqueue(entry.Message);
        Log.Written += OnLog;
        var elapsed = Stopwatch.StartNew();
        FpsStats? stats;
        string? error;
        try
        {
            // CaptureAsync reads the real ETW CSV through FpsMeasure.ParseCsv before removing it.
            (stats, error) = await PresentMon.CaptureAsync(processName, Duration, limit.Token);
        }
        finally
        {
            elapsed.Stop();
            Log.Written -= OnLog;
        }
        Assert.Equal(target, ReadTarget(executable, session, owner));
        Assert.True(stats is not null && error is null, $"{kind} capture failed: {error}");
        var evidence = Assert.Single(log.Select(line => Regex.Match(line, @"^PresentMon: PID ([0-9]+), сеанс (LycorisFps-[0-9a-f]{32})\.$")),
            match => match.Success);
        Assert.Equal(target.Pid.ToString(CultureInfo.InvariantCulture), evidence.Groups[1].Value);
        var traceName = evidence.Groups[2].Value;
        AssertTraceNotRunning(traceName, kind);
        Assert.InRange(elapsed.Elapsed.TotalSeconds, 14, 50);
        Assert.InRange(stats.Frames, FpsMeasure.MinFrames, FpsMeasure.MaxRows);
        Assert.InRange(stats.Seconds, 10, 20);
        Assert.All(new[] { stats.AvgFps, stats.Low1Fps, stats.AvgFrameMs, stats.P99FrameMs },
            value => Assert.True(double.IsFinite(value) && value > 0, $"Expected a finite positive FPS or frame-time value, got {value}."));
        output.WriteLine(FormattableString.Invariant(
            $"{kind} {processName} PID {target.Pid}, started {target.StartUtc:O}, requested {Duration} s, ran {elapsed.Elapsed.TotalSeconds:F2} s, {stats.Frames} frames in {stats.Seconds:F2} s, avg {stats.AvgFps:F2} FPS, 1% low {stats.Low1Fps:F2} FPS, avg {stats.AvgFrameMs:F2} ms, p99 {stats.P99FrameMs:F2} ms"));
        return (new FpsRecord { Kind = kind, Process = processName, RequestedSeconds = Duration, Stats = stats }, traceName);
    }

    private static void AssertTraceNotRunning(string name, string kind)
    {
        const uint EventTraceControlQuery = 0;
        const uint ErrorWmiInstanceNotFound = 4201;
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
            var status = ControlTraceW(0, name, buffer, EventTraceControlQuery);
            Assert.True(status == ErrorWmiInstanceNotFound, status == 0
                ? $"ETW session {name} is running after the {kind} capture."
                : $"Cannot establish the state of ETW session {name} after the {kind} capture (Win32 {status}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Target ReadTarget(string executable, int session, SecurityIdentifier user)
    {
        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try
        {
            Assert.True(candidates.Length == 1, $"Expected one {Path.GetFileName(executable)} process, found {candidates.Length}.");
            var process = candidates[0];
            Assert.NotEqual(Environment.ProcessId, process.Id);
            Assert.Equal(session, process.SessionId);
            Assert.False(process.HasExited);
            var image = process.MainModule?.FileName;
            Assert.Equal(executable, image, ignoreCase: true);
            Assert.Equal(user, ReadProcessUser(process));

            var window = process.MainWindowHandle;
            Assert.True(window != 0 && GetWindowThreadProcessId(window, out var pid) != 0 && pid == process.Id &&
                IsWindowVisible(window) && !IsIconic(window), "The selected workload must have a visible, non-minimized main window.");
            return new(process.Id, process.StartTime.ToUniversalTime(), window);
        }
        finally
        {
            foreach (var candidate in candidates) candidate.Dispose();
        }
    }

    private static SecurityIdentifier ReadProcessUser(Process process)
    {
        if (!OpenProcessToken(process.SafeHandle, TokenAccessLevels.Query, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return Assert.IsType<SecurityIdentifier>(identity.User);
    }

    private static string ReadAbsolutePath(string variable)
    {
        var path = Environment.GetEnvironmentVariable(variable)!;
        Assert.True(Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal),
            $"{variable} must be an absolute local path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void AssertOrdinaryExecutable(string executable)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
        {
            var directory = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(directory))
                Assert.False(executable.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase), "A Windows system executable cannot be the test workload.");
        }

        Assert.False(File.GetAttributes(executable).HasFlag(FileAttributes.ReparsePoint));
        for (var directory = new FileInfo(executable).Directory; directory is not null; directory = directory.Parent)
            Assert.False(directory.Attributes.HasFlag(FileAttributes.ReparsePoint), "The workload path must not traverse a reparse point.");
    }

    private sealed record Target(int Pid, DateTime StartUtc, nint Window);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, TokenAccessLevels access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ControlTraceW(ulong sessionHandle, string name, nint properties, uint code);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}

public sealed class FpsNativeFactAttribute : FactAttribute
{
    internal const string RunVariable = "LYCORIS_FPS_INTEGRATION";
    internal const string ProcessVariable = "LYCORIS_FPS_PROCESS";
    internal const string WorkloadRootVariable = "LYCORIS_FPS_WORKLOAD_ROOT";
    internal const string TestRootVariable = "LYCORIS_TEST_ROOT";

    public FpsNativeFactAttribute() => Skip = PrerequisiteFailure();

    internal static string? PrerequisiteFailure()
    {
#if DEBUG
        if (Environment.GetEnvironmentVariable(HwFactAttribute.Variable) != "1" || Environment.GetEnvironmentVariable(RunVariable) != "1")
            return $"Opt in with {HwFactAttribute.Variable}=1 and {RunVariable}=1.";
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProcessVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WorkloadRootVariable)) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestRootVariable)))
            return $"Set {ProcessVariable}, {WorkloadRootVariable}, and {TestRootVariable} before starting the test host.";
        return OperatingSystem.IsWindows() && Environment.UserInteractive && Elevation.IsElevated
            ? null : "Requires an elevated Windows test host in the workload's interactive desktop session.";
#else
        return "Requires the Debug build with an isolated application data directory.";
#endif
    }
}
