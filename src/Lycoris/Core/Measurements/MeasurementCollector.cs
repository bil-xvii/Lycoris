using System.Reflection;
using System.Runtime.InteropServices;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Startup;

namespace Lycoris.Core.Measurements;

public interface IMeasurementBootSource
{
    (double? Seconds, string Source) Read();
}

public interface IMeasurementMemorySource
{
    long? ReadFreeBytes();
}

public interface IMeasurementStartupSource
{
    int ReadEnabledCount();
}

public interface IMeasurementEnvironmentSource
{
    (string Version, string Build, string LycorisVersion) Read();
}

/// <summary>Collects local read-only conditions on explicit request; does not save or run in the background.</summary>
public sealed class MeasurementCollector(
    IMeasurementBootSource boot,
    IMeasurementMemorySource memory,
    IMeasurementStartupSource startup,
    IMeasurementEnvironmentSource environment)
{
    public Measurement Collect()
    {
        var os = environment.Read();
        var bootResult = boot.Read();
        return new Measurement
        {
            Conditions = new MeasurementConditions
            {
                WindowsVersion = os.Version,
                WindowsBuild = os.Build,
                LycorisVersion = os.LycorisVersion,
                EnabledStartupItems = Math.Max(0, startup.ReadEnabledCount()),
                FreeMemoryBytes = memory.ReadFreeBytes(),
                BootTimeSeconds = bootResult.Seconds,
                BootTimeSource = bootResult.Source
            }
        };
    }

    public static MeasurementCollector CreateDefault() => new(
        new WindowsBootSource(), new WindowsMemorySource(), new ProjectStartupSource(), new WindowsEnvironmentSource());
}

public sealed class WindowsBootSource : IMeasurementBootSource
{
    public (double? Seconds, string Source) Read()
    {
        var report = BootPerformance.Read();
        var last = report?.Last;
        return last is null ? (null, "недоступно") : (last.BootMs / 1000d, BootPerformance.LogName + " / ID 100 BootDuration"); // loc:ignore: stable on-disk source marker
    }
}

public sealed class WindowsMemorySource : IMeasurementMemorySource
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    public long? ReadFreeBytes()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.AvailPhys <= long.MaxValue ? (long)status.AvailPhys : null;
    }
}

public sealed class ProjectStartupSource : IMeasurementStartupSource
{
    public int ReadEnabledCount() => SystemInspector.CountStartupEntries();
}

public sealed class WindowsEnvironmentSource : IMeasurementEnvironmentSource
{
    public (string Version, string Build, string LycorisVersion) Read()
    {
        var version = Environment.OSVersion.Version;
        var product = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", null) as string;
        return (product ?? "Windows", version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown");
    }
}
