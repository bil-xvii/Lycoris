namespace Monolith.Core.Devices;

// The native side of device changes. Core owns the journal protocol; the feature packages own the native code and plug
// it in through the partial hooks of DeviceWriters (DeviceWriters.Input.cs, .Displays.cs, .Audio.cs, .Network.cs).
// Every method is blocking: callers are never on the UI thread. A missing writer means «this build cannot do that»:
// the change is refused and an undo of such a record fails loudly (the record stays active) — never a silent no-op.

/// <summary>SystemParametersInfo for <see cref="SpiCatalog"/> entries.</summary>
public interface ISpiWriter
{
    /// <summary>The live value; null when it cannot be read.</summary>
    int? Get(SpiEntry e);

    /// <summary>Sets the value with SPIF_UPDATEINIFILE | SPIF_SENDCHANGE (live session + the account's HKCU).</summary>
    bool Set(SpiEntry e, int value);
}

public enum DisplayWriteStatus { Ok, Failed, NotFound, NeedsRestart }

public sealed record DisplayWriteResult(DisplayWriteStatus Status, string? Message = null)
{
    public static readonly DisplayWriteResult Success = new(DisplayWriteStatus.Ok);
    public bool Ok => Status == DisplayWriteStatus.Ok;
}

public enum DisplayGuardKind { Mode, Hdr }

/// <summary>
/// What the out-of-process guard does when MONOLITH dies or the deadline passes. Mode: return to the persisted mode
/// (CDS(NULL) then SDC_USE_DATABASE_CURRENT). Hdr: set <see cref="OriginalHdr"/> on <see cref="MonitorPath"/>.
/// </summary>
public sealed record DisplayGuardAction(DisplayGuardKind Kind, string MonitorPath, bool OriginalHdr);

/// <summary>A running guard process. <see cref="Resolve"/> tells it the outcome is settled (it exits without touching anything).</summary>
public interface IDisplayGuard : IDisposable
{
    void Resolve();
}

/// <summary>CCD / GDI display modes and HDR (package «displays»).</summary>
public interface IDisplayWriter
{
    /// <summary>The live mode of the monitor; null when the monitor is not active / not found.</summary>
    DisplayModeSpec? ReadCurrentMode(string monitorPath);

    /// <summary>The mode saved for the monitor (ENUM_REGISTRY_SETTINGS / CCD database); null when unknown.</summary>
    DisplayModeSpec? ReadPersistedMode(string monitorPath);

    /// <summary>The driver lists this mode for the monitor (progressive, same orientation).</summary>
    bool IsModeOffered(string monitorPath, DisplayModeSpec mode);

    bool? ReadHdrEnabled(string monitorPath);

    /// <summary>Automatic colour management (24H2+); null when unknown or unsupported.</summary>
    bool? ReadAcmEnabled(string monitorPath);

    /// <summary>\\.\DISPLAYn of the monitor (where the confirmation must NOT appear); null when unknown.</summary>
    string? GdiName(string monitorPath);

    /// <summary>Why display changes are not allowed right now (remote session, full-screen app…), or null.</summary>
    string? BlockReason();

    /// <summary>CDS_TEST of the mode: the driver accepts it.</summary>
    DisplayWriteResult TestMode(string monitorPath, DisplayModeSpec mode);

    /// <summary>Applies the mode for this session only (not saved: a reboot or CDS(NULL) returns to the saved mode).</summary>
    DisplayWriteResult ApplyTemporary(string monitorPath, DisplayModeSpec mode);

    /// <summary>Applies and saves the mode (CDS_UPDATEREGISTRY).</summary>
    DisplayWriteResult Persist(string monitorPath, DisplayModeSpec mode);

    /// <summary>Back to <paramref name="original"/> after a temporary change (CDS(NULL) → verify → database → snapshot).</summary>
    DisplayWriteResult RevertTemporary(string monitorPath, DisplayModeSpec original);

    DisplayWriteResult SetHdr(string monitorPath, bool enable);

    /// <summary>
    /// Starts «MONOLITH.exe --display-guard …» and waits until it reports armed. Null when it could not be started:
    /// the change must then not be made.
    /// </summary>
    IDisplayGuard? StartGuard(Guid recordId, DateTime deadlineUtc, DisplayGuardAction action);

    /// <summary>The body of the guard process (App.OnStartup «--display-guard»). Returns the exit code.</summary>
    int RunGuard(string[] args);
}

/// <summary>One VCP reading: the monitor's minimum, current value and maximum.</summary>
public readonly record struct VcpReading(uint Min, uint Value, uint Max);

/// <summary>DDC/CI brightness and contrast through the high-level monitor configuration API (package «displays»).</summary>
public interface IMonitorWriter
{
    VcpReading? Get(string monitorPath, MonitorVcp code);
    bool Set(string monitorPath, MonitorVcp code, uint value);
}

/// <summary>Endpoint volume of capture devices (package «audio»).</summary>
public interface IAudioWriter
{
    /// <summary>
    /// Windows knows an endpoint with this id (an endpoint id changes when the driver is reinstalled). False means «gone for
    /// good» — undo closes its record on it — so a failed query throws instead of answering false.
    /// </summary>
    bool IsPresent(string endpointId);

    (float Scalar, bool Mute)? Read(string endpointId);
    bool SetScalar(string endpointId, float scalar);
    bool SetDb(string endpointId, float db);
    bool SetMute(string endpointId, bool mute);
}

/// <summary>An advanced property as the adapter reports it (MSFT_NetAdapterAdvancedPropertySettingData).</summary>
public sealed record NicPropertyInfo(string Keyword, string? Value, IReadOnlyList<string> ValidValues, string? DefaultValue, string? DisplayName = null);

/// <summary>NDIS advanced properties of a network adapter, found by InterfaceGuid (package «network»).</summary>
public interface INicWriter
{
    /// <summary>The adapter's name, or null when no adapter has this InterfaceGuid. A failed query throws (null closes an undo record).</summary>
    string? AdapterName(Guid interfaceGuid);

    /// <summary>The property; null when the adapter or the keyword does not exist. A failed query throws (null closes an undo record).</summary>
    NicPropertyInfo? Read(Guid interfaceGuid, string keyword);

    /// <summary>Sets the RegistryValue (the driver restarts the adapter; blocking for the few seconds that takes).</summary>
    bool Set(Guid interfaceGuid, string keyword, string value);

    /// <summary>
    /// A running game the adapter restart would disconnect (Game Boost session or a library game with a window), or null.
    /// Changes and undo are refused while one runs.
    /// </summary>
    string? RunningGame() => null;
}

/// <summary>The writers the running build has. Feature packages set them in their Init* hooks.</summary>
public static partial class DeviceWriters
{
    private static ISpiWriter? _spi;
    private static IDisplayWriter? _display;
    private static IMonitorWriter? _monitor;
    private static IAudioWriter? _audio;
    private static INicWriter? _nic;

    // Every writer is wrapped so that a mutating call throws in self-test mode (SafetyLock), whoever implemented it.
    public static ISpiWriter? Spi { get => _spi; private set => _spi = value is null or GuardedSpi ? value : new GuardedSpi(value); }
    public static IDisplayWriter? Display { get => _display; private set => _display = value is null or GuardedDisplay ? value : new GuardedDisplay(value); }
    public static IMonitorWriter? Monitor { get => _monitor; private set => _monitor = value is null or GuardedMonitor ? value : new GuardedMonitor(value); }
    public static IAudioWriter? Audio { get => _audio; private set => _audio = value is null or GuardedAudio ? value : new GuardedAudio(value); }
    public static INicWriter? Nic { get => _nic; private set => _nic = value is null or GuardedNic ? value : new GuardedNic(value); }

    static DeviceWriters()
    {
        InitInput();
        InitDisplays();
        InitAudio();
        InitNetwork();
    }

    // Implemented by the feature packages in their own files (same namespace, same partial class).
    static partial void InitInput();
    static partial void InitDisplays();
    static partial void InitAudio();
    static partial void InitNetwork();

    /// <summary>The message of a refused change or undo when this build has no writer for it.</summary>
    public static string NoWriterApply => Loc.T("эта версия MONOLITH не умеет менять этот параметр");
    public static string NoWriterRevert => Loc.T("эта версия MONOLITH не умеет откатывать такое изменение");

    /// <summary>TESTS ONLY: replaces the writers until disposed (null leaves a writer absent).</summary>
    internal static IDisposable OverrideForTests(ISpiWriter? spi = null, IDisplayWriter? display = null, IMonitorWriter? monitor = null,
        IAudioWriter? audio = null, INicWriter? nic = null)
    {
        var previous = (_spi, _display, _monitor, _audio, _nic);
        Spi = spi;
        Display = display;
        Monitor = monitor;
        Audio = audio;
        Nic = nic;
        return new Restorer(() => (_spi, _display, _monitor, _audio, _nic) = previous);
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) restore();
        }
    }

    // ───────────────────────────── SafetyLock decorators ─────────────────────────────

    private sealed class GuardedSpi(ISpiWriter inner) : ISpiWriter
    {
        public int? Get(SpiEntry e) => inner.Get(e);
        public bool Set(SpiEntry e, int value)
        {
            SafetyLock.ThrowIfLocked(Loc.T("изменение параметра Windows"));
            return inner.Set(e, value);
        }
    }

    private sealed class GuardedDisplay(IDisplayWriter inner) : IDisplayWriter
    {
        private static string What => Loc.T("изменение параметров экрана");
        public DisplayModeSpec? ReadCurrentMode(string p) => inner.ReadCurrentMode(p);
        public DisplayModeSpec? ReadPersistedMode(string p) => inner.ReadPersistedMode(p);
        public bool IsModeOffered(string p, DisplayModeSpec m) => inner.IsModeOffered(p, m);
        public bool? ReadHdrEnabled(string p) => inner.ReadHdrEnabled(p);
        public bool? ReadAcmEnabled(string p) => inner.ReadAcmEnabled(p);
        public string? GdiName(string p) => inner.GdiName(p);
        public string? BlockReason() => inner.BlockReason();
        public DisplayWriteResult TestMode(string p, DisplayModeSpec m) => inner.TestMode(p, m);

        public DisplayWriteResult ApplyTemporary(string p, DisplayModeSpec m)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.ApplyTemporary(p, m);
        }

        public DisplayWriteResult Persist(string p, DisplayModeSpec m)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.Persist(p, m);
        }

        public DisplayWriteResult RevertTemporary(string p, DisplayModeSpec original)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.RevertTemporary(p, original);
        }

        public DisplayWriteResult SetHdr(string p, bool enable)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.SetHdr(p, enable);
        }

        public IDisplayGuard? StartGuard(Guid recordId, DateTime deadlineUtc, DisplayGuardAction action)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.StartGuard(recordId, deadlineUtc, action);
        }

        // The guard process never sets the lock: it must always be able to put the old mode back.
        public int RunGuard(string[] args) => inner.RunGuard(args);
    }

    private sealed class GuardedMonitor(IMonitorWriter inner) : IMonitorWriter
    {
        public VcpReading? Get(string p, MonitorVcp code) => inner.Get(p, code);
        public bool Set(string p, MonitorVcp code, uint value)
        {
            SafetyLock.ThrowIfLocked(Loc.T("изменение яркости или контраста монитора"));
            return inner.Set(p, code, value);
        }
    }

    private sealed class GuardedAudio(IAudioWriter inner) : IAudioWriter
    {
        private static string What => Loc.T("изменение уровня звука");
        public bool IsPresent(string id) => inner.IsPresent(id);
        public (float Scalar, bool Mute)? Read(string id) => inner.Read(id);

        public bool SetScalar(string id, float scalar)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.SetScalar(id, scalar);
        }

        public bool SetDb(string id, float db)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.SetDb(id, db);
        }

        public bool SetMute(string id, bool mute)
        {
            SafetyLock.ThrowIfLocked(What);
            return inner.SetMute(id, mute);
        }
    }

    private sealed class GuardedNic(INicWriter inner) : INicWriter
    {
        public string? AdapterName(Guid g) => inner.AdapterName(g);
        public NicPropertyInfo? Read(Guid g, string keyword) => inner.Read(g, keyword);
        public bool Set(Guid g, string keyword, string value)
        {
            SafetyLock.ThrowIfLocked(Loc.T("изменение свойства сетевого адаптера"));
            return inner.Set(g, keyword, value);
        }
        public string? RunningGame() => inner.RunningGame();
    }
}
