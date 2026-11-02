using System.IO;
using System.Runtime.InteropServices;
using Lycoris.Core;
using Lycoris.Core.Devices;
using Xunit.Abstractions;

namespace Lycoris.Tests.Hardware;

/// <summary>
/// OPT-IN hardware checks (docs/hardware-verification-2026-11.md): they drive the REAL device writers on the machine the tests
/// run on. Skipped unless the environment variable Lycoris_HW_TESTS=1. Never part of the default run or of --selftest.
/// Every check reads the original, changes it through ChangeSession with an isolated journal (a temp file, never
/// %ProgramData%\Lycoris), undoes it from that journal, and restores + verifies in a finally block.
/// </summary>
public sealed class HwFactAttribute : FactAttribute
{
    public const string Variable = "Lycoris_HW_TESTS";

    public HwFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
            Skip = $"hardware test: set {Variable}=1 to run it on this PC";
    }
}

[CollectionDefinition("Hardware", DisableParallelization = true)]
public sealed class HardwareCollection;

[Collection("Hardware")]
public abstract class HwBase : IDisposable
{
    protected readonly ITestOutputHelper Out;
    protected readonly string JournalPath = Path.Combine(Path.GetTempPath(), $"lycoris-hw-{Guid.NewGuid():N}.json");
    protected readonly ChangeJournal Journal;

    protected HwBase(ITestOutputHelper output)
    {
        Out = output;
        Assert.NotEqual(Path.GetFullPath(JournalPath), Path.GetFullPath(AppPaths.JournalFile));
        ChangeSession.BypassElevationForTests = true;
        // An identity redirect: registry access goes to the REAL key (so SPI persisted values are snapshot and restored for
        // real), but a non-null Redirect also turns off the .reg exports into %ProgramData%\Lycoris\Backups.
        RegistryHelper.Redirect = (h, p) => (h, p);
        Journal = ChangeJournal.ForTests(JournalPath);
        DeviceConfirm.Ask = (_, _, _) => Task.FromResult(false);
    }

    public virtual void Dispose()
    {
        DeviceConfirm.Countdown = null;
        DeviceConfirm.Ask = null;
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        foreach (var f in new[] { JournalPath, JournalPath + ".bak", JournalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    protected Task<ChangeSession> Begin(string title) => ChangeSession.BeginAsync(title, createRestorePoint: false, journal: Journal);

    /// <summary>Writes one line to the test output and, when Lycoris_HW_REPORT names a file, appends it there.</summary>
    protected void Say(string line)
    {
        Out.WriteLine(line);
        if (Environment.GetEnvironmentVariable("Lycoris_HW_REPORT") is { Length: > 0 } f)
            try { File.AppendAllText(f, line + Environment.NewLine); } catch { }
    }

    protected static void Hold(TimeSpan t) => Thread.Sleep(t);

    // ───────────── independent reads (never through the code under test) ─────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? name, int mode, ref Dm dm);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Dm
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PosX, PosY, Orientation, FixedOutput;
        public short Color, Duplex, YRes, TT, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int Bpp, Width, Height, Flags, Freq, IcmMethod, IcmIntent, MediaType, Dither, Reserved1, Reserved2, PanW, PanH;
    }

    /// <summary>EnumDisplaySettings of a GDI device: -1 = live, -2 = saved in the registry.</summary>
    protected static string RawMode(string? gdi, int which)
    {
        var dm = new Dm { Size = (short)Marshal.SizeOf<Dm>() };
        return EnumDisplaySettingsW(gdi, which, ref dm) ? $"{dm.Width}x{dm.Height}@{dm.Freq}" : "?";
    }

    protected static string? RegString(string path, string name) =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path)?.GetValue(name) is { } v ? $"{v.GetType().Name}:{v}" : null;
}
