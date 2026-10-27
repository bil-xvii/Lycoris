using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;

namespace Monolith.Tests.Core;

/// <summary>
/// In-memory SystemParametersInfo: a live value per entry; Set also writes the catalog's persisted value through
/// RegistryHelper (redirected in tests), as Windows does with SPIF_UPDATEINIFILE.
/// </summary>
internal sealed class FakeSpiWriter : ISpiWriter
{
    public Dictionary<string, int> Live { get; } = new(StringComparer.Ordinal);
    /// <summary>Set reports success but the live value does not change (read-back mismatch).</summary>
    public bool IgnoreSets { get; set; }
    public int SetCalls { get; private set; }

    public int? Get(SpiEntry e) => Live.TryGetValue(e.Id, out var v) ? v : null;

    public bool Set(SpiEntry e, int value)
    {
        SetCalls++;
        if (e.Persisted is { } p)
        {
            if (p.Kind == RegistryValueKind.DWord) RegistryHelper.Write(RegHive.HKCU, p.Path, p.Name, RegistryValueKind.DWord, value);
            else RegistryHelper.Write(RegHive.HKCU, p.Path, p.Name, RegistryValueKind.String, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!IgnoreSets) Live[e.Id] = value;
        return true;
    }
}

internal sealed class FakeMonitorWriter : IMonitorWriter
{
    public Dictionary<(string, MonitorVcp), VcpReading> Values { get; } = new();
    public bool IgnoreSets { get; set; }
    public int SetCalls { get; private set; }

    public VcpReading? Get(string path, MonitorVcp code) => Values.TryGetValue((path, code), out var v) ? v : null;

    public bool Set(string path, MonitorVcp code, uint value)
    {
        SetCalls++;
        if (!Values.TryGetValue((path, code), out var v)) return false;
        if (!IgnoreSets) Values[(path, code)] = v with { Value = value };
        return true;
    }
}

internal sealed class FakeAudioWriter : IAudioWriter
{
    public Dictionary<string, (float Scalar, bool Mute)> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IgnoreSets { get; set; }
    public int SetCalls { get; private set; }

    public bool IsPresent(string id) => Endpoints.ContainsKey(id);
    public (float Scalar, bool Mute)? Read(string id) => Endpoints.TryGetValue(id, out var v) ? v : null;

    public bool SetScalar(string id, float scalar)
    {
        SetCalls++;
        if (!Endpoints.TryGetValue(id, out var v)) return false;
        if (!IgnoreSets) Endpoints[id] = (scalar, v.Mute);
        return true;
    }

    public bool SetDb(string id, float db) => SetScalar(id, Math.Clamp((db + 20f) / 40f, 0f, 1f));

    public bool SetMute(string id, bool mute)
    {
        SetCalls++;
        if (!Endpoints.TryGetValue(id, out var v)) return false;
        if (!IgnoreSets) Endpoints[id] = (v.Scalar, mute);
        return true;
    }
}

internal sealed class FakeNicWriter : INicWriter
{
    public Guid Adapter { get; set; } = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    public bool AdapterPresent { get; set; } = true;
    public Dictionary<string, NicPropertyInfo> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int SetCalls { get; private set; }

    public string? AdapterName(Guid g) => AdapterPresent && g == Adapter ? "Ethernet" : null;

    public NicPropertyInfo? Read(Guid g, string keyword) =>
        AdapterPresent && g == Adapter && Props.TryGetValue(keyword, out var p) ? p : null;

    public bool Set(Guid g, string keyword, string value)
    {
        SetCalls++;
        if (Read(g, keyword) is not { } p) return false;
        Props[keyword] = p with { Value = value };
        return true;
    }

    /// <summary>A running game (null: none).</summary>
    public string? Game { get; set; }
    public string? RunningGame() => Game;
}

/// <summary>A display: current / saved mode, offered modes, HDR, failure switches per step and a guard with flags.</summary>
internal sealed class FakeDisplayWriter : IDisplayWriter
{
    public sealed class Guard : IDisplayGuard
    {
        public bool Resolved { get; private set; }
        public bool Disposed { get; private set; }
        public void Resolve() => Resolved = true;
        public void Dispose() => Disposed = true;
    }

    public DisplayModeSpec? Current { get; set; }
    public DisplayModeSpec? Persisted { get; set; }
    public bool? Hdr { get; set; }
    public bool? Acm { get; set; }
    public List<DisplayModeSpec> Offered { get; } = [];
    public string? Block { get; set; }
    public bool FailTest { get; set; }
    public bool FailTemporary { get; set; }
    public bool FailPersist { get; set; }
    /// <summary>The first Persist saves the mode but the monitor ends up in this one (verification fails).</summary>
    public DisplayModeSpec? FirstPersistLandsOn { get; set; }
    /// <summary>Persist calls from this index on (0-based) fail.</summary>
    public int PersistFailsFrom { get; set; } = int.MaxValue;
    private int _persists;
    public bool GuardStartFails { get; set; }
    public bool Present { get; set; } = true;
    public string Gdi { get; set; } = @"\\.\DISPLAY1";
    public List<Guard> Guards { get; } = [];
    public List<string> Calls { get; } = [];

    public DisplayModeSpec? ReadCurrentMode(string p) => Present ? Current : null;
    public DisplayModeSpec? ReadPersistedMode(string p) => Persisted;
    public bool IsModeOffered(string p, DisplayModeSpec m) => Offered.Any(o => o.SameMode(m));
    public bool? ReadHdrEnabled(string p) => Present ? Hdr : null;
    public bool? ReadAcmEnabled(string p) => Acm;
    public string? GdiName(string p) => Gdi;
    public string? BlockReason() => Block;

    public DisplayWriteResult TestMode(string p, DisplayModeSpec m)
    {
        Calls.Add("test");
        return FailTest ? new(DisplayWriteStatus.Failed, "CDS_TEST") : DisplayWriteResult.Success;
    }

    public DisplayWriteResult ApplyTemporary(string p, DisplayModeSpec m)
    {
        Calls.Add("temp");
        if (FailTemporary) return new(DisplayWriteStatus.Failed, "temp");
        Current = m;
        return DisplayWriteResult.Success;
    }

    public DisplayWriteResult Persist(string p, DisplayModeSpec m)
    {
        Calls.Add("persist");
        var index = _persists++;
        if (FailPersist || index >= PersistFailsFrom) return new(DisplayWriteStatus.Failed, "persist");
        Persisted = m;
        Current = index == 0 && FirstPersistLandsOn is { } other ? other : m;
        return DisplayWriteResult.Success;
    }

    public DisplayWriteResult RevertTemporary(string p, DisplayModeSpec original)
    {
        Calls.Add("revert");
        Current = Persisted ?? original; // CDS(NULL): back to the saved mode
        return DisplayWriteResult.Success;
    }

    public DisplayWriteResult SetHdr(string p, bool enable)
    {
        Calls.Add(enable ? "hdr-on" : "hdr-off");
        Hdr = enable;
        return DisplayWriteResult.Success;
    }

    public IDisplayGuard? StartGuard(Guid recordId, DateTime deadlineUtc, DisplayGuardAction action)
    {
        Calls.Add("guard");
        if (GuardStartFails) return null;
        var g = new Guard();
        Guards.Add(g);
        return g;
    }

    public int RunGuard(string[] args) => 0;
}

internal static class DeviceTestData
{
    public const string Sid = "S-1-5-21-1000000001-1000000002-1000000003-1001";
    public const string MonitorPath = @"\\?\DISPLAY#AOC2701#5&2a1b3c4d&0&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    public const string MicId = "{0.0.1.00000000}.{11111111-2222-3333-4444-555555555555}";
    public const string SpeakerId = "{0.0.0.00000000}.{11111111-2222-3333-4444-555555555555}";

    public static DisplayModeSpec Mode(int w, int h, int hz, uint num = 0, uint den = 0) => new(w, h, hz, num, den, 32, 0, 0, 0);
}
