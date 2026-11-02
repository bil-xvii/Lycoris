using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>
/// Shared building blocks for the catalog partial files: registry shorthands, hardware rules
/// (Ryzen X3D, GeForce RTX 40/50, laptops, editions), read-only system probes and the command builders
/// used by tweaks that change things through <see cref="ChangeSession.RunCommandAsync"/>.
/// Everything here only READS the system; all writes go through <see cref="TweakContext"/>.
/// </summary>
public static partial class TweakCatalog
{
    // ───────────────────────────── Registry paths ─────────────────────────────

    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string SessionPower = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power";
    private const string DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string DataCollectionSettings = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";
    private const string WindowsUpdatePolicy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string TcpipInterfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string Tcpip6Interfaces = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces";

    // ───────────────────────────── Registry shorthands ─────────────────────────────

    private static RegSet Cu(string path, string name, int value) => RegSet.Dword(RegHive.HKCU, path, name, value);
    private static RegSet Lm(string path, string name, int value) => RegSet.Dword(RegHive.HKLM, path, name, value);
    private static int? CuInt(string path, string name) => RegistryHelper.ReadInt(RegHive.HKCU, path, name);
    private static int? LmInt(string path, string name) => RegistryHelper.ReadInt(RegHive.HKLM, path, name);

    private static bool Matches(RegSet v) => RegistryHelper.ValueEquals(v.Hive, v.Path, v.Name, v.Kind, v.Value);

    private static void SetDword(TweakContext ctx, RegHive hive, string path, string name, int value) =>
        ctx.SetRegistry(hive, path, name, RegistryValueKind.DWord, value);

    /// <summary>Applied / Partial / NotApplied from a count of checks that already match.</summary>
    internal static TweakState StateOf(int matches, int total) =>
        total <= 0 ? TweakState.NotApplicable
        : matches >= total ? TweakState.Applied
        : matches == 0 ? TweakState.NotApplied
        : TweakState.Partial;

    // ───────────────────────────── Hardware and edition rules ─────────────────────────────

    /// <summary>
    /// Ryzen X3D with two CCDs (7900X3D, 7950X3D, 9900X3D, 9950X3D(2), mobile 7945HX3D / 9955HX3D):
    /// the AMD 3D V-Cache driver steers games to the cache CCD only on the Balanced plan with Game Mode on.
    /// Single-CCD parts (5600/5700/5800X3D, 7600/7800X3D, 9800X3D) have nothing to steer.
    /// </summary>
    internal static bool IsDualCcdX3D(string cpuName, int cores)
    {
        if (string.IsNullOrWhiteSpace(cpuName) || !cpuName.Contains("X3D", StringComparison.OrdinalIgnoreCase)) return false;
        if (Regex.IsMatch(cpuName, @"[79]9[05]0X3D", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(cpuName, @"\d[5-8]\d0X3D", RegexOptions.IgnoreCase)) return false;
        return cores >= 12;
    }

    /// <summary>
    /// True on dual-CCD Ryzen X3D. Anything that switches power plans (profiles, game boost) must keep these systems on
    /// Balanced with Game Mode on, and must not touch core parking / minimum processor state.
    /// </summary>
    public static bool IsDualCcdX3D(SystemProfile sp) => IsDualCcdX3D(sp.CpuName, sp.CpuCores);

    /// <summary>GeForce RTX 4050–4090 / 5050–5090 (desktop and laptop).</summary>
    internal static bool IsRtx40Or50(string gpuName) =>
        !string.IsNullOrEmpty(gpuName) && Regex.IsMatch(gpuName, @"RTX\s*[45]0[5-9]0", RegexOptions.IgnoreCase);

    private static bool HasRtx40Or50(SystemProfile sp) =>
        sp.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia && IsRtx40Or50(g.Name));

    /// <summary>Home editions (EditionID Core, CoreN, CoreSingleLanguage, CoreCountrySpecific) ignore update policies.</summary>
    internal static bool IsHomeEdition(string editionId) => editionId.StartsWith("Core", StringComparison.OrdinalIgnoreCase);

    private static bool IsInsider() =>
        !string.IsNullOrWhiteSpace(RegistryHelper.ReadString(RegHive.HKLM, @"SOFTWARE\Microsoft\WindowsSelfHost\Applicability", "BranchName"));

    private static bool HasWifiAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────────── GPU scheduling capabilities (D3DKMT, read-only) ─────────────────────────────

    internal readonly record struct GpuSchedulingCaps(bool Supported, bool Enabled);

    private static readonly Lazy<GpuSchedulingCaps?> GpuSchedulingLazy = new(QueryGpuScheduling);

    /// <summary>What the WDDM drivers report about hardware-accelerated GPU scheduling (null = unknown).</summary>
    internal static GpuSchedulingCaps? GpuScheduling => GpuSchedulingLazy.Value;

    /// <summary>
    /// Asks every display adapter for KMTQAITYPE_WDDM_2_7_CAPS (HwSchSupported / HwSchEnabled bits).
    /// Returns null when the query is not available (Windows 10 before 2004, broken drivers).
    /// </summary>
    internal static GpuSchedulingCaps? QueryGpuScheduling()
    {
        try
        {
            var e = new Kmt.EnumAdapters2();
            if (Kmt.D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return null;
            var count = (int)Math.Min(e.NumAdapters, 64u);
            var size = Marshal.SizeOf<Kmt.AdapterInfo>();
            e.NumAdapters = (uint)count;
            e.Adapters = Marshal.AllocHGlobal(size * count);
            var caps = Marshal.AllocHGlobal(4);
            try
            {
                if (Kmt.D3DKMTEnumAdapters2(ref e) != 0) return null;
                bool any = false, supported = false, enabled = false;
                for (var i = 0; i < Math.Min((int)e.NumAdapters, count); i++)
                {
                    var info = Marshal.PtrToStructure<Kmt.AdapterInfo>(e.Adapters + i * size);
                    Marshal.WriteInt32(caps, 0);
                    var q = new Kmt.QueryAdapterInfo { Adapter = info.Handle, Type = Kmt.Wddm27Caps, PrivateData = caps, PrivateDataSize = 4 };
                    if (Kmt.D3DKMTQueryAdapterInfo(ref q) == 0)
                    {
                        any = true;
                        var bits = Marshal.ReadInt32(caps);
                        supported |= (bits & 1) != 0;
                        enabled |= (bits & 2) != 0;
                    }
                    var close = new Kmt.CloseAdapter { Adapter = info.Handle };
                    Kmt.D3DKMTCloseAdapter(ref close);
                }
                return any ? new GpuSchedulingCaps(supported, enabled) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(e.Adapters);
                Marshal.FreeHGlobal(caps);
            }
        }
        catch
        {
            return null;
        }
    }

    private static class Kmt
    {
        public const int Wddm27Caps = 70; // KMTQAITYPE_WDDM_2_7_CAPS

        [StructLayout(LayoutKind.Sequential)]
        public struct EnumAdapters2
        {
            public uint NumAdapters;
            public IntPtr Adapters;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct AdapterInfo
        {
            public uint Handle;
            public uint LuidLow;
            public int LuidHigh;
            public uint NumOfSources;
            public int PrecisePresentRegionsPreferred;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct QueryAdapterInfo
        {
            public uint Adapter;
            public int Type;
            public IntPtr PrivateData;
            public uint PrivateDataSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CloseAdapter
        {
            public uint Adapter;
        }

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 args);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo args);

        [DllImport("gdi32.dll")]
        public static extern int D3DKMTCloseAdapter(ref CloseAdapter args);
    }

    // ───────────────────────────── Power capabilities and power settings (read-only) ─────────────────────────────

    /// <summary>SYSTEM_POWER_CAPABILITIES (hiberfil, Modern Standby, hybrid sleep, batteries); see <see cref="PowerSchemes.ReadCapabilities"/>.</summary>
    internal static PowerCaps? ReadPowerCaps() => PowerSchemes.ReadCapabilities();

    private static bool IsModernStandby() => ReadPowerCaps()?.ModernStandby == true;

    /// <summary>The "plugged in" value of a power setting in a scheme (null when the setting does not exist).</summary>
    internal static uint? ReadAcIndex(Guid scheme, Guid subgroup, Guid setting)
    {
        try
        {
            return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The "on battery" (DC) value of a power setting in a scheme (null when the setting does not exist). Read-only.</summary>
    internal static uint? ReadDcIndex(Guid scheme, Guid subgroup, Guid setting)
    {
        try
        {
            return PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref setting, out var value) == 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One side of a power setting: AC (plugged in) or DC (on battery).</summary>
    internal static uint? ReadIndex(bool dc, Guid scheme, Guid subgroup, Guid setting) =>
        dc ? ReadDcIndex(scheme, subgroup, setting) : ReadAcIndex(scheme, subgroup, setting);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid, out uint dcValueIndex);

    // ───────────────────────────── Visual effects (SystemParametersInfo, read-only here) ─────────────────────────────

    /// <summary>UI effects switched off by the "performance" preset: (SPI_GET*, SPI_SET*, Russian name).</summary>
    internal static readonly (uint Get, uint Set, string Name)[] VisualEffectFlags =
    [
        (0x1002, 0x1003, Loc.Key("анимация меню")),
        (0x1004, 0x1005, Loc.Key("анимация списков")),
        (0x1006, 0x1007, Loc.Key("плавная прокрутка списков")),
        (0x1016, 0x1017, Loc.Key("анимация подсказок")),
        (0x1014, 0x1015, Loc.Key("затухание выделения")),
        (0x101A, 0x101B, Loc.Key("тень курсора")),
        (0x1024, 0x1025, Loc.Key("тени окон")),
        (0x1042, 0x1043, Loc.Key("анимация элементов окон")),
    ];

    internal sealed record VisualEffectsState(bool[] Flags, int MinAnimate)
    {
        public bool AllOff => Flags.All(f => !f) && MinAnimate == 0;
    }

    internal static VisualEffectsState? ReadVisualEffects()
    {
        try
        {
            var flags = new bool[VisualEffectFlags.Length];
            for (var i = 0; i < flags.Length; i++)
            {
                if (!SpiGetBool(VisualEffectFlags[i].Get, 0, out var value, 0)) return null;
                flags[i] = value != 0;
            }
            var animation = new AnimationInfo { Size = 8 };
            if (!SpiGetAnimation(0x0048, 8, ref animation, 0)) return null; // SPI_GETANIMATION
            return new VisualEffectsState(flags, animation.MinAnimate);
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo
    {
        public uint Size;
        public int MinAnimate;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiGetBool(uint action, uint uiParam, out int value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SpiGetAnimation(uint action, uint uiParam, ref AnimationInfo info, uint winIni);

    // ───────────────────────────── Network adapters (read-only) ─────────────────────────────

    internal sealed record NetAdapter(string Guid, string Name, int Index, bool Ipv6);

    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vpn", "tap-", "tap ", "wireguard", "wintun", "tunnel",
        "loopback", "bluetooth", "npcap", "zerotier", "tailscale", "openvpn", "anyconnect", "fortinet", "radmin", "hamachi",
        "nordlynx", "wsl", "docker", "miniport", "teredo", "isatap", "kernel debug",
    ];

    /// <summary>
    /// Physical Ethernet / Wi-Fi adapters that are up and have a gateway (the ones that carry internet traffic).
    /// Virtual, VPN, Hyper-V/WSL and tunnel adapters are skipped.
    /// </summary>
    internal static IReadOnlyList<NetAdapter> PhysicalAdapters()
    {
        var result = new List<NetAdapter>();
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit)) continue;
                var text = (n.Name + " " + n.Description).ToLowerInvariant();
                if (VirtualAdapterMarkers.Any(text.Contains)) continue;
                if (!n.Supports(NetworkInterfaceComponent.IPv4)) continue;

                var props = n.GetIPProperties();
                if (props.GatewayAddresses.Count == 0) continue;
                int index;
                try { index = props.GetIPv4Properties()?.Index ?? -1; }
                catch { continue; }
                if (index <= 0) continue;
                if (!RegistryHelper.KeyExists(RegHive.HKLM, $@"{TcpipInterfaces}\{n.Id}")) continue;

                result.Add(new NetAdapter(n.Id, n.Name, index, n.Supports(NetworkInterfaceComponent.IPv6)));
            }
        }
        catch
        {
            // No network stack information — treated as "no adapters".
        }
        return result;
    }

    /// <summary>Statically configured DNS servers of an adapter (empty = assigned by DHCP / router advertisements).</summary>
    internal static string[] StaticDns(string adapterGuid, bool ipv6) =>
        ParseNameServers(RegistryHelper.ReadString(RegHive.HKLM, $@"{(ipv6 ? Tcpip6Interfaces : TcpipInterfaces)}\{adapterGuid}", "NameServer"));

    /// <summary>NameServer is "a,b" (or space separated on old builds); empty means DHCP.</summary>
    internal static string[] ParseNameServers(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Same servers in the same order (IPv6 spelling normalized).</summary>
    internal static bool SameServers(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == b.Count && a.Select(NormalizeIp).SequenceEqual(b.Select(NormalizeIp), StringComparer.OrdinalIgnoreCase);

    private static string NormalizeIp(string s) => IPAddress.TryParse(s, out var ip) ? ip.ToString() : s.Trim();

    // ───────────────────────────── DirectX user settings string ─────────────────────────────

    private const string DxParentPath = @"Software\Microsoft\DirectX";
    private const string DxPreferencesPath = DxParentPath + @"\UserGpuPreferences";
    private const string DxGlobalSettings = "DirectXUserGlobalSettings";

    /// <summary>Value of one "Key=Value;" token in DirectXUserGlobalSettings (null when the token is absent).</summary>
    internal static string? GetDxToken(string? settings, string key)
    {
        if (string.IsNullOrEmpty(settings)) return null;
        foreach (var raw in settings.Split(';'))
        {
            var token = raw.Trim();
            if (token.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return token[(key.Length + 1)..];
        }
        return null;
    }

    /// <summary>
    /// Sets (or with <paramref name="value"/> = null removes) one token, keeping every other token and their order.
    /// Returns null when no token is left. Mirrors <see cref="DxTokenScript"/> exactly.
    /// </summary>
    internal static string? SetDxToken(string? settings, string key, string? value)
    {
        var tokens = new List<string>();
        var found = false;
        if (!string.IsNullOrEmpty(settings))
        {
            foreach (var raw in settings.Split(';'))
            {
                var token = raw.Trim();
                if (token.Length == 0) continue;
                if (token.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!found && value is not null) tokens.Add(key + "=" + value);
                    found = true;
                }
                else
                {
                    tokens.Add(token);
                }
            }
        }
        if (!found && value is not null) tokens.Add(key + "=" + value);
        return tokens.Count == 0 ? null : string.Join(";", tokens) + ";";
    }

    /// <summary>
    /// PowerShell that edits a single token of DirectXUserGlobalSettings in place (value null = remove the token).
    /// Each DirectX tweak owns exactly one token, so undoing one never disturbs another.
    /// </summary>
    /// <remarks>
    /// Undo scripts get <paramref name="createdKey"/> when the key did not exist before Lycoris's first DirectX token
    /// (and <paramref name="createdParent"/> when Software\Microsoft\DirectX did not exist either): once the last token is
    /// gone, the script removes the key (and that parent) again, but only while they hold no values and no subkeys — per-app
    /// GPU preferences live in the same key and are never touched. Without either flag the text is exactly what older
    /// builds wrote, so their journal records still rebuild character for character.
    /// </remarks>
    internal static string DxTokenScript(string key, string? value, string registryPath = @"HKCU:\" + DxPreferencesPath,
        string valueName = DxGlobalSettings, bool createdKey = false, bool createdParent = false)
    {
        var v = value is null ? "$null" : Ps(value);
        var prefix = createdKey ? $"$mk=$true;$mp=${(createdParent ? "true" : "false")};" : "";
        var cleanup = createdKey
            ? ";if($mk){$ki=Get-Item -LiteralPath $p -ErrorAction SilentlyContinue;" +
              "if($ki -and $ki.ValueCount -eq 0 -and $ki.SubKeyCount -eq 0){Remove-Item -LiteralPath $p;" +
              "if($mp){$q=Split-Path -Parent $p;$kq=Get-Item -LiteralPath $q -ErrorAction SilentlyContinue;" +
              "if($kq -and $kq.ValueCount -eq 0 -and $kq.SubKeyCount -eq 0){Remove-Item -LiteralPath $q}}}}"
            : "";
        return
            prefix +
            $"$p={Ps(registryPath)};$n={Ps(valueName)};$k={Ps(key)};$v={v};" +
            "$cur=$null;if(Test-Path -LiteralPath $p){$cur=(Get-ItemProperty -LiteralPath $p).$n};" +
            "$t=New-Object 'System.Collections.Generic.List[string]';$found=$false;" +
            "if($cur){foreach($x in ([string]$cur).Split(';')){$x=$x.Trim();if(-not $x){continue};" +
            "if($x.StartsWith($k+'=',[StringComparison]::OrdinalIgnoreCase)){if(-not $found -and $null -ne $v){$t.Add($k+'='+$v)};$found=$true}" +
            "else{$t.Add($x)}}};" +
            "if(-not $found -and $null -ne $v){$t.Add($k+'='+$v)};" +
            "if($t.Count -eq 0){if($null -ne $cur){Remove-ItemProperty -LiteralPath $p -Name $n}" + cleanup + "}" +
            "else{if(-not(Test-Path -LiteralPath $p)){New-Item -Path $p -Force|Out-Null};" +
            "Set-ItemProperty -LiteralPath $p -Name $n -Value (($t -join ';')+';') -Type String}";
    }

    /// <summary>
    /// Whether the undo of a new DirectX token must also remove the key / its DirectX parent: true when the key (parent) is
    /// absent now, or when an active change of this account already claims to have created it — a DirectX token whose undo
    /// removes it, or a registry change (the Games page's per-game GPU choice) whose created key covers it. Then the key
    /// only exists because Lycoris created it, and whichever change is reverted last must clean it up, in any revert
    /// order. Other accounts' records never count: their claims are about another profile's HKCU.
    /// </summary>
    internal static (bool CreatedKey, bool CreatedParent) DxUndoOwnership(bool keyExists, bool parentExists,
        IEnumerable<ChangeRecord> activeRecords, string keyPath = DxPreferencesPath)
    {
        bool key = !keyExists, parent = !parentExists;
        var parentPath = ParentKeyPath(keyPath);
        foreach (var r in activeRecords)
        {
            if (r.Reverted || !ChangeJournal.IsMine(r)) continue;
            if (r.Kind == ChangeKind.Command)
            {
                var (k, p) = DxClaim(r);
                key |= k;
                parent |= p;
            }
            else if (r.Kind == ChangeKind.Registry && r.Registry is { Hive: RegHive.HKCU } s
                     && (s.CreatedKeyRoot ?? (!s.KeyExisted ? s.Path : null)) is { } root
                     && RegistryHelper.IsSameOrAncestor(root, keyPath))
            {
                key = true;
                parent |= RegistryHelper.IsSameOrAncestor(root, parentPath);
            }
        }
        return (key, key && parent);
    }

    /// <summary>
    /// The key a DirectX-token record created and its undo removes once it is empty: the DirectX parent ($mp) or
    /// UserGpuPreferences itself ($mk); null for any other record. A per-game GPU choice written into that key later
    /// carries it as its created root (<see cref="ChangeJournal.InheritedKeyRoot"/>), so whichever of the two changes is
    /// reverted last removes the key, in any order.
    /// </summary>
    internal static string? DxCreatedKeyRoot(ChangeRecord r, string keyPath = DxPreferencesPath) => DxClaim(r) switch
    {
        (true, true) => ParentKeyPath(keyPath),
        (true, false) => keyPath,
        _ => null,
    };

    /// <summary>"Software\Microsoft\DirectX\UserGpuPreferences" → "Software\Microsoft\DirectX" (registry paths, any OS).</summary>
    private static string ParentKeyPath(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var i = trimmed.LastIndexOf('\\');
        return i > 0 ? trimmed[..i] : trimmed;
    }

    /// <summary>What a DirectX-token record's undo promises to remove: the key it created ($mk) and its parent ($mp).</summary>
    private static (bool Key, bool Parent) DxClaim(ChangeRecord r)
    {
        if (r.Kind != ChangeKind.Command || !r.Target.StartsWith("cmd:dx:", StringComparison.Ordinal)) return (false, false);
        if (DecodePowerShellArgs(r.RevertArgs) is not { } script) return (false, false);
        return (script.Contains("$mk=$true;", StringComparison.Ordinal), script.Contains("$mk=$true;$mp=$true;", StringComparison.Ordinal));
    }

    /// <summary>The script inside <see cref="PowerShellArgs"/> output (null when the arguments are not such a line).</summary>
    internal static string? DecodePowerShellArgs(string? args)
    {
        const string prefix = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";
        if (args is null || !args.StartsWith(prefix, StringComparison.Ordinal)) return null;
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(args[prefix.Length..])); }
        catch (FormatException) { return null; }
    }

    /// <summary>Single-quoted PowerShell literal.</summary>
    private static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    // ───────────────────────────── Command builders (executed only through ChangeSession) ─────────────────────────────

    private static string CmdExe => ProcessRunner.Sys("cmd.exe");

    internal static string PowerShellExe => Path.Combine(ProcessRunner.System32, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Quoted full path of a System32 tool (never resolved through the working directory).</summary>
    private static string SysTool(string exe) => "\"" + ProcessRunner.Sys(exe) + "\"";

    /// <summary>cmd.exe arguments for a command line; /s keeps inner quotes intact.</summary>
    internal static string CmdArgs(string commandLine) => "/d /s /c \"" + commandLine + "\"";

    /// <summary>powershell.exe arguments: the script runs with stop-on-error and exits with 1 on any failure.</summary>
    internal static string PowerShellArgs(string script)
    {
        var full = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
                   "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;" +
                   "try{" + script + "}catch{[Console]::Error.WriteLine($_.Exception.Message);exit 1}";
        return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
               Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
    }

    /// <summary>Runs <paramref name="f"/> with Russian texts: the journal (data on disk) stores Russian whatever the UI language is.</summary>
    internal static T Ru<T>(Func<T> f)
    {
        using var _ = Loc.UseLanguage(Loc.Ru);
        return f();
    }

    /// <summary>Runs a journaled command (with its undo command). Tweaks run on a worker thread, so blocking is fine.</summary>
    private static bool RunJournaled(TweakContext ctx, string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, OutputEncoding encoding = OutputEncoding.Oem) =>
        ctx.Session.RunCommandAsync(target, file, args, revertFile, revertArgs, oldValue, newValue, ctx.Source, ctx.Tweak.TitleRu, encoding)
            .GetAwaiter().GetResult();

    /// <summary>The Add-Type block that exposes SystemParametersInfoW to the visual-effects scripts.</summary>
    internal const string SpiTypeDefinition =
        "if(-not ('Monolith.Spi' -as [type])){Add-Type -Namespace Monolith -Name Spi -MemberDefinition " +
        "'[DllImport(\"user32.dll\", EntryPoint=\"SystemParametersInfoW\", SetLastError=true)] public static extern bool Set(uint a, uint u, IntPtr p, uint f); " +
        "[DllImport(\"user32.dll\", EntryPoint=\"SystemParametersInfoW\", SetLastError=true)] public static extern bool SetAnimation(uint a, uint u, int[] p, uint f);'};";

    /// <summary>
    /// PowerShell that sets the UI-effect flags and the window animation (SPI_SETANIMATION) with
    /// SPIF_UPDATEINIFILE | SPIF_SENDCHANGE, exactly as the Performance Options dialog does.
    /// </summary>
    /// <remarks>
    /// With <paramref name="rollback"/>, a failure part-way puts every flag back to the rollback state (best effort,
    /// each flag on its own) and then rethrows, so a failed apply leaves nothing changed without a journal record.
    /// </remarks>
    internal static string SpiScript(IReadOnlyList<(uint SetAction, bool On)> flags, int minAnimate,
        IReadOnlyList<(uint SetAction, bool On)>? rollback = null, int rollbackMinAnimate = 0)
    {
        var sb = new StringBuilder(SpiTypeDefinition);
        if (rollback is null)
        {
            AppendSpi(sb, flags, minAnimate, strict: true);
            return sb.ToString();
        }
        sb.Append("try{");
        AppendSpi(sb, flags, minAnimate, strict: true);
        sb.Append("}catch{$e=$_;");
        AppendSpi(sb, rollback, rollbackMinAnimate, strict: false);
        sb.Append("throw $e};");
        return sb.ToString();
    }

    private static void AppendSpi(StringBuilder sb, IReadOnlyList<(uint SetAction, bool On)> flags, int minAnimate, bool strict)
    {
        foreach (var (action, on) in flags)
        {
            var call = $"[Monolith.Spi]::Set(0x{action:X},0,[IntPtr]{(on ? 1 : 0)},3)";
            sb.Append(strict
                ? $"if(-not {call}){{throw 'Windows отклонила изменение эффекта 0x{action:X}'}};" // loc:ignore: PowerShell error text, stored in the journal script
                : $"try{{[void]{call}}}catch{{}};");
        }
        var anim = $"[Monolith.Spi]::SetAnimation(0x49,8,[int[]]@(8,{minAnimate}),3)";
        sb.Append(strict
            ? $"if(-not {anim}){{throw 'Windows отклонила изменение анимации окон'}};" // loc:ignore: PowerShell error text, stored in the journal script
            : $"try{{[void]{anim}}}catch{{}};");
    }

    // ───────────────────────────── Power plan helpers ─────────────────────────────

    /// <summary>Journal source of a game session (GameBoostService.Source).</summary>
    private const string GameSessionSource = "gameboost";

    /// <summary>
    /// True while the live plan is a game session's temporary one: the session's record is the newest layer of the active
    /// plan (also after a crash, until the session is undone). That plan goes back when the session ends.
    /// </summary>
    private static bool SessionHoldsPlan() => ChangeJournal.Instance.FindActive("power:active")?.Source == GameSessionSource;

    /// <summary>The game session switched the AC power mode for its duration (journal target cmd:powermode:ac).</summary>
    private static bool SessionHoldsPowerMode() => ChangeJournal.Instance.FindActive("cmd:powermode:ac")?.Source == GameSessionSource;

    /// <summary>
    /// Whether choosing <paramref name="scheme"/> has to record a change: it is not the live plan, or it is live only for a
    /// game session. Then the choice becomes a newer layer that takes over the session's original, so it stays after the
    /// session instead of silently ending with it.
    /// </summary>
    internal static bool SchemeChangeNeeded(Guid active, Guid scheme, bool sessionPlan) => active != scheme || sessionPlan;

    /// <summary>Activates a built-in power plan through the journal (no-op when it is already the user's live plan).</summary>
    private static void ActivateScheme(TweakContext ctx, Guid scheme, string missingMessage)
    {
        if (!SchemeChangeNeeded(PowerSchemes.GetActive(), scheme, SessionHoldsPlan())) return;
        if (!PowerSchemes.Exists(scheme)) throw new InvalidOperationException(missingMessage);
        ctx.SetPowerScheme(scheme);
    }

    /// <summary>
    /// A plan item's state. The session's temporary plan never counts as applied: it ends with the session, so the item
    /// stays selectable and applying it records the user's own choice (<see cref="SchemeChangeNeeded"/>).
    /// </summary>
    internal static TweakState SchemeState(Guid active, Guid scheme, bool sessionPlan, Func<Guid, bool> exists)
    {
        if (active == Guid.Empty) return TweakState.Unknown;
        if (active == scheme) return sessionPlan ? TweakState.NotApplied : TweakState.Applied;
        return exists(scheme) ? TweakState.NotApplied : TweakState.NotApplicable;
    }

    private static TweakState SchemeState(Guid scheme) =>
        SchemeState(PowerSchemes.GetActive(), scheme, SessionHoldsPlan(), PowerSchemes.Exists);

    /// <summary>
    /// powercfg command line that writes the AC value and re-applies the active scheme. With <paramref name="rollbackTo"/>,
    /// any failure (e.g. /setactive refused after the value was written) writes the original value back and exits 1,
    /// so a failed apply never leaves the plan changed without a journal record.
    /// </summary>
    internal static string PowerSettingCommand(Guid scheme, Guid subgroup, Guid setting, uint value, uint? rollbackTo = null) =>
        PowerSettingCommand(dc: false, scheme, subgroup, setting, value, rollbackTo);

    /// <summary>The DC (on battery) twin of <see cref="PowerSettingCommand(Guid, Guid, Guid, uint, uint?)"/>: powercfg /setdcvalueindex, same self-rollback.</summary>
    internal static string PowerSettingCommandDc(Guid scheme, Guid subgroup, Guid setting, uint value, uint? rollbackTo = null) =>
        PowerSettingCommand(dc: true, scheme, subgroup, setting, value, rollbackTo);

    private static string PowerSettingCommand(bool dc, Guid scheme, Guid subgroup, Guid setting, uint value, uint? rollbackTo)
    {
        var powercfg = SysTool("powercfg.exe");
        var verb = dc ? "/setdcvalueindex" : "/setacvalueindex";
        string Set(uint v) => $"{powercfg} {verb} {scheme:D} {subgroup:D} {setting:D} {v} && {powercfg} /setactive SCHEME_CURRENT";
        if (rollbackTo is not { } original) return Set(value);
        return $"({Set(value)}) || ({powercfg} {verb} {scheme:D} {subgroup:D} {setting:D} {original} & " +
               $"{powercfg} /setactive SCHEME_CURRENT & exit /b 1)";
    }

    /// <summary>
    /// Shared by every <see cref="PowerSettingTweak"/>: powercfg has no per-setting "back to the plan default", and the
    /// override keys belong to the Power service, so the undo writes the original number explicitly.
    /// </summary>
    internal static readonly string PowerSettingUndoNote =
        Loc.T("Откат возвращает прежнее значение, но записывает его в схему явно. Если раньше параметр брался из значений схемы " +
        "по умолчанию, вернуть и это можно кнопкой «Восстановить для схемы параметры по умолчанию» в Панели управления → " +
        "Электропитание (она сбрасывает все параметры этой схемы).");

    /// <summary>
    /// True when the plan has its own AC value for the setting; false when the value comes from the plan's defaults
    /// (Windows stores only modified values under User\PowerSchemes). Read-only.
    /// </summary>
    internal static bool HasAcOverride(Guid scheme, Guid subgroup, Guid setting) => HasOverride(dc: false, scheme, subgroup, setting);

    /// <summary>As <see cref="HasAcOverride"/> for the plan's own DC (on battery) value (DCSettingIndex). Read-only.</summary>
    internal static bool HasDcOverride(Guid scheme, Guid subgroup, Guid setting) => HasOverride(dc: true, scheme, subgroup, setting);

    private static bool HasOverride(bool dc, Guid scheme, Guid subgroup, Guid setting) =>
        RegistryHelper.Read(RegHive.HKLM,
            $@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\{scheme:D}\{subgroup:D}\{setting:D}",
            dc ? "DCSettingIndex" : "ACSettingIndex") is not null;

    /// <summary>
    /// «Только сокращает»: an idle timeout (seconds, 0 = never) is written only when it is off (0) or longer than the target;
    /// a shorter value already meets the goal and is never lengthened.
    /// </summary>
    internal static bool TimeoutNeedsShortening(uint current, uint target) => current == 0 || current > target;

    /// <summary>Idle timeout in seconds as the records and previews say it: «никогда» (0) or «через N мин».</summary>
    internal static string DescribeTimeout(uint seconds)
    {
        if (seconds == 0) return Loc.T("никогда");
        if (seconds < 60) return Loc.Tf("через {0} с", seconds);
        return seconds % 60 == 0 ? Loc.Tf("через {0} мин", seconds / 60) : Loc.Tf("через {0} мин {1} с", seconds / 60, seconds % 60);
    }

    /// <summary>
    /// The state of a power setting's AC value: Applied when it equals <paramref name="target"/> or when
    /// <paramref name="alsoSatisfied"/> says the current value already meets the goal (e.g. wake timers «отключены» is
    /// stricter than «только важные» — the item never loosens it).
    /// </summary>
    internal static TweakState PowerSettingState(uint value, uint target, Func<uint, bool>? alsoSatisfied = null) =>
        value == target || alsoSatisfied?.Invoke(value) == true ? TweakState.Applied : TweakState.NotApplied;

    /// <summary>
    /// Mirror of BoostPolicy.DecidePowerPlan (Modules/Games): the plan a game session switches a desktop to — always High
    /// performance (GameBoostService.StartAsync activates PowerSchemes.HighPerformance; an Ultimate copy only matters when it is
    /// already the active plan, which the normal path writes). A power setting that must hold during the session is written into
    /// this plan too (one journal record per plan). Empty when the session never switches plans on this PC: option off, laptop,
    /// dual-CCD X3D, Modern Standby, no High performance.
    /// </summary>
    internal static IReadOnlyList<Guid> ExtraSchemesForBoost(bool boostOn, bool laptop, bool dualCcd, bool modernStandby, bool highExists) =>
        !boostOn || laptop || dualCcd || modernStandby || !highExists ? [] : [PowerSchemes.HighPerformance];

    /// <summary>The live inputs of <see cref="ExtraSchemesForBoost(bool, bool, bool, bool, bool)"/> (GameBoostService.DecidePower uses the same facts).</summary>
    private static IReadOnlyList<Guid> ExtraSchemesForBoost(SystemProfile sp)
    {
        if (!sp.HardwareFactsKnown) return []; // the session itself never switches blindly (SkipUnknownHardware)
        var boostOn = AppSettings.Current.GameBoostPowerPlan;
        return ExtraSchemesForBoost(boostOn, sp.IsLaptop, IsDualCcdX3D(sp), IsModernStandby(),
            boostOn && PowerSchemes.Exists(PowerSchemes.HighPerformance));
    }

    /// <summary>
    /// A tweak that changes the "plugged in" (AC) value of one power setting in the ACTIVE plan via powercfg.
    /// The battery (DC) value is never touched. Undo writes the original AC value back.
    /// </summary>
    /// <param name="alsoSatisfied">Current values that already meet the goal: detected as applied, never changed.</param>
    /// <param name="alsoSchemes">
    /// Other plans that get the same AC value (each its own journal record), e.g. the plans a game session switches to.
    /// Detection stays on the active plan.
    /// </param>
    /// <param name="onlyShorten">
    /// Idle timeouts (seconds, 0 = never): write only when the current value is 0 or longer than <paramref name="target"/>;
    /// a shorter value counts as applied (<see cref="TimeoutNeedsShortening"/>).
    /// </param>
    private static CustomTweak PowerSettingTweak(string id, string title, string description, string details,
        TweakCategory category, TweakRisk risk, ProfileSet defaults, Guid subgroup, Guid setting, uint target,
        Func<uint, string> describe, Func<SystemProfile, bool>? appliesTo = null, string? notApplicableReason = null,
        string? warning = null, Func<uint, bool>? alsoSatisfied = null, Func<SystemProfile, IReadOnlyList<Guid>>? alsoSchemes = null,
        bool onlyShorten = false) =>
        PowerSettingTweakCore(dc: false, id, title, description, details, category, risk, defaults, subgroup, setting, target, describe,
            appliesTo, notApplicableReason, warning, alsoSatisfied, alsoSchemes, onlyShorten);

    /// <summary>
    /// The DC twin of <see cref="PowerSettingTweak"/>: changes the "on battery" value of one power setting in the ACTIVE plan via
    /// powercfg /setdcvalueindex; the AC value is never touched. Target <c>powercfg:{scheme}:{setting}:dc</c>, record text
    /// «{plan}, от батареи: …», same game-session guard and <see cref="PowerSettingUndoNote"/>. Laptops only by nature
    /// (<paramref name="appliesTo"/> must name the desktop counterpart in <paramref name="notApplicableReason"/>).
    /// </summary>
    private static CustomTweak PowerSettingTweakDc(string id, string title, string description, string details,
        TweakCategory category, TweakRisk risk, ProfileSet defaults, Guid subgroup, Guid setting, uint target,
        Func<uint, string> describe, Func<SystemProfile, bool> appliesTo, string notApplicableReason,
        string? warning = null, Func<uint, bool>? alsoSatisfied = null, bool onlyShorten = false) =>
        PowerSettingTweakCore(dc: true, id, title, description, details, category, risk, defaults, subgroup, setting, target, describe,
            appliesTo, notApplicableReason, warning, alsoSatisfied, alsoSchemes: null, onlyShorten);

    private static CustomTweak PowerSettingTweakCore(bool dc, string id, string title, string description, string details,
        TweakCategory category, TweakRisk risk, ProfileSet defaults, Guid subgroup, Guid setting, uint target,
        Func<uint, string> describe, Func<SystemProfile, bool>? appliesTo, string? notApplicableReason,
        string? warning, Func<uint, bool>? alsoSatisfied, Func<SystemProfile, IReadOnlyList<Guid>>? alsoSchemes, bool onlyShorten) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Details = details + " " + PowerSettingUndoNote,
        Category = category,
        Risk = risk,
        DefaultIn = defaults,
        AppliesTo = appliesTo,
        NotApplicableReason = notApplicableReason,
        Warning = warning,
        DetectFunc = () =>
        {
            var scheme = PowerSchemes.GetActive();
            if (scheme == Guid.Empty) return TweakState.Unknown;
            var value = ReadIndex(dc, scheme, subgroup, setting);
            if (value is null) return TweakState.NotApplicable;
            return PowerSettingState(value.Value, target, Satisfied(alsoSatisfied, onlyShorten, target));
        },
        ApplyAction = ctx =>
        {
            // During a game session the active plan is the session's temporary one: a setting changed now would be
            // recorded against that plan and silently stop applying when the session ends.
            if (SessionHoldsPlan())
                throw new InvalidOperationException(Loc.T("сначала завершите игровой сеанс — сейчас действует его временная схема питания"));
            var scheme = PowerSchemes.GetActive();
            if (scheme == Guid.Empty) throw new InvalidOperationException(Loc.T("не удалось определить активную схему питания"));
            var satisfied = Satisfied(alsoSatisfied, onlyShorten, target);
            var current = ReadIndex(dc, scheme, subgroup, setting)
                          ?? throw new InvalidOperationException(Loc.T("параметр отсутствует в текущей схеме питания"));
            if (PowerSettingState(current, target, satisfied) != TweakState.Applied)
                WriteValue(ctx, dc, scheme, subgroup, setting, current, target, describe);

            // The plans a game session switches to: the same value, one record per plan, so the fix holds during the session.
            if (alsoSchemes is null) return;
            foreach (var extra in alsoSchemes(ctx.System).Distinct())
            {
                if (extra == scheme || ReadIndex(dc, extra, subgroup, setting) is not { } extraCurrent) continue;
                if (PowerSettingState(extraCurrent, target, satisfied) == TweakState.Applied) continue;
                WriteValue(ctx, dc, extra, subgroup, setting, extraCurrent, target, describe);
            }
        },
    };

    /// <summary>The «already meets the goal» rule of a power setting item: the caller's own rule and/or «only shorten».</summary>
    internal static Func<uint, bool>? Satisfied(Func<uint, bool>? alsoSatisfied, bool onlyShorten, uint target)
    {
        if (!onlyShorten) return alsoSatisfied;
        return v => !TimeoutNeedsShortening(v, target) || alsoSatisfied?.Invoke(v) == true;
    }

    /// <summary>
    /// One journaled powercfg record for the AC or the DC side of a plan's setting: value → <paramref name="target"/>; the undo
    /// writes <paramref name="current"/> back (target <c>powercfg:{scheme}:{setting}:ac|dc</c>, text «{plan}, от сети|от батареи: …»).
    /// </summary>
    private static void WriteValue(TweakContext ctx, bool dc, Guid scheme, Guid subgroup, Guid setting, uint current, uint target, Func<uint, string> describe)
    {
        var plan = PowerSchemes.GetName(scheme);
        // The effective value is restored either way; say honestly when it came from the plan's defaults.
        // The journal texts stay Russian (Ru): the Restore page translates them when it shows them.
        var (oldText, newText) = Ru(() =>
        {
            var side = dc ? Loc.T("от батареи") : Loc.T("от сети");
            var origin = HasOverride(dc, scheme, subgroup, setting) ? "" : Loc.T(" (по умолчанию схемы; при откате будет записано явно)");
            return (Loc.Tf("{0}, {1}: {2}{3}", plan, side, describe(current), origin), Loc.Tf("{0}, {1}: {2}", plan, side, describe(target)));
        });
        RunJournaled(ctx, PowercfgTarget(dc, scheme, setting),
            CmdExe, CmdArgs(PowerSettingCommand(dc, scheme, subgroup, setting, target, rollbackTo: current)),
            CmdExe, CmdArgs(PowerSettingCommand(dc, scheme, subgroup, setting, current, rollbackTo: null)),
            oldText, newText);
    }

    /// <summary>The journal target of a plan setting's AC or DC record (without the "cmd:" prefix ChangeSession adds).</summary>
    internal static string PowercfgTarget(bool dc, Guid scheme, Guid setting) => $"powercfg:{scheme:D}:{setting:D}:{(dc ? "dc" : "ac")}";
}
