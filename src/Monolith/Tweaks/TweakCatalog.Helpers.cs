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

    internal readonly record struct PowerCaps(bool HiberFilePresent, bool ModernStandby, byte HiberFileType);

    /// <summary>SYSTEM_POWER_CAPABILITIES: HiberFilePresent (offset 8), AoAc = Modern Standby (20), HiberFileType (22).</summary>
    internal static PowerCaps? ReadPowerCaps()
    {
        var buffer = Marshal.AllocHGlobal(128);
        try
        {
            for (var i = 0; i < 128; i++) Marshal.WriteByte(buffer, i, 0);
            if (!GetPwrCapabilities(buffer)) return null;
            return new PowerCaps(Marshal.ReadByte(buffer, 8) != 0, Marshal.ReadByte(buffer, 20) != 0, Marshal.ReadByte(buffer, 22));
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

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

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(IntPtr capabilities);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    // ───────────────────────────── Visual effects (SystemParametersInfo, read-only here) ─────────────────────────────

    /// <summary>UI effects switched off by the "performance" preset: (SPI_GET*, SPI_SET*, Russian name).</summary>
    internal static readonly (uint Get, uint Set, string Name)[] VisualEffectFlags =
    [
        (0x1002, 0x1003, "анимация меню"),
        (0x1004, 0x1005, "анимация списков"),
        (0x1006, 0x1007, "плавная прокрутка списков"),
        (0x1016, 0x1017, "анимация подсказок"),
        (0x1014, 0x1015, "затухание выделения"),
        (0x101A, 0x101B, "тень курсора"),
        (0x1024, 0x1025, "тени окон"),
        (0x1042, 0x1043, "анимация элементов окон"),
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

    private const string DxPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
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
    internal static string DxTokenScript(string key, string? value, string registryPath = @"HKCU:\" + DxPreferencesPath,
        string valueName = DxGlobalSettings)
    {
        var v = value is null ? "$null" : Ps(value);
        return
            $"$p={Ps(registryPath)};$n={Ps(valueName)};$k={Ps(key)};$v={v};" +
            "$cur=$null;if(Test-Path -LiteralPath $p){$cur=(Get-ItemProperty -LiteralPath $p).$n};" +
            "$t=New-Object 'System.Collections.Generic.List[string]';$found=$false;" +
            "if($cur){foreach($x in ([string]$cur).Split(';')){$x=$x.Trim();if(-not $x){continue};" +
            "if($x.StartsWith($k+'=',[StringComparison]::OrdinalIgnoreCase)){if(-not $found -and $null -ne $v){$t.Add($k+'='+$v)};$found=$true}" +
            "else{$t.Add($x)}}};" +
            "if(-not $found -and $null -ne $v){$t.Add($k+'='+$v)};" +
            "if($t.Count -eq 0){if($null -ne $cur){Remove-ItemProperty -LiteralPath $p -Name $n}}" +
            "else{if(-not(Test-Path -LiteralPath $p)){New-Item -Path $p -Force|Out-Null};" +
            "Set-ItemProperty -LiteralPath $p -Name $n -Value (($t -join ';')+';') -Type String}";
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

    /// <summary>Runs a journaled command (with its undo command). Tweaks run on a worker thread, so blocking is fine.</summary>
    private static bool RunJournaled(TweakContext ctx, string target, string file, string args, string revertFile, string revertArgs,
        string oldValue, string newValue, OutputEncoding encoding = OutputEncoding.Oem) =>
        ctx.Session.RunCommandAsync(target, file, args, revertFile, revertArgs, oldValue, newValue, ctx.Source, ctx.Tweak.Title, encoding)
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
    internal static string SpiScript(IReadOnlyList<(uint SetAction, bool On)> flags, int minAnimate)
    {
        var sb = new StringBuilder(SpiTypeDefinition);
        foreach (var (action, on) in flags)
            sb.Append($"if(-not [Monolith.Spi]::Set(0x{action:X},0,[IntPtr]{(on ? 1 : 0)},3)){{throw 'Windows отклонила изменение эффекта 0x{action:X}'}};");
        sb.Append($"if(-not [Monolith.Spi]::SetAnimation(0x49,8,[int[]]@(8,{minAnimate}),3)){{throw 'Windows отклонила изменение анимации окон'}};");
        return sb.ToString();
    }

    // ───────────────────────────── Power plan helpers ─────────────────────────────

    /// <summary>Activates a built-in power plan through the journal (no-op when it is already active).</summary>
    private static void ActivateScheme(TweakContext ctx, Guid scheme, string missingMessage)
    {
        if (PowerSchemes.GetActive() == scheme) return;
        if (!PowerSchemes.Exists(scheme)) throw new InvalidOperationException(missingMessage);
        ctx.SetPowerScheme(scheme);
    }

    private static TweakState SchemeState(Guid scheme)
    {
        var active = PowerSchemes.GetActive();
        if (active == Guid.Empty) return TweakState.Unknown;
        if (active == scheme) return TweakState.Applied;
        return PowerSchemes.Exists(scheme) ? TweakState.NotApplied : TweakState.NotApplicable;
    }

    /// <summary>
    /// A tweak that changes the "plugged in" (AC) value of one power setting in the ACTIVE plan via powercfg.
    /// The battery (DC) value is never touched. Undo writes the original AC value back.
    /// </summary>
    private static CustomTweak PowerSettingTweak(string id, string title, string description, string details,
        TweakCategory category, TweakRisk risk, ProfileSet defaults, Guid subgroup, Guid setting, uint target,
        Func<uint, string> describe, Func<SystemProfile, bool>? appliesTo = null, string? notApplicableReason = null,
        string? warning = null) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Details = details,
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
            var value = ReadAcIndex(scheme, subgroup, setting);
            if (value is null) return TweakState.NotApplicable;
            return value == target ? TweakState.Applied : TweakState.NotApplied;
        },
        ApplyAction = ctx =>
        {
            var scheme = PowerSchemes.GetActive();
            if (scheme == Guid.Empty) throw new InvalidOperationException("не удалось определить активную схему питания");
            var current = ReadAcIndex(scheme, subgroup, setting)
                          ?? throw new InvalidOperationException("параметр отсутствует в текущей схеме питания");
            if (current == target) return;

            var powercfg = SysTool("powercfg.exe");
            string Set(uint v) => $"{powercfg} /setacvalueindex {scheme:D} {subgroup:D} {setting:D} {v} && {powercfg} /setactive SCHEME_CURRENT";
            var plan = PowerSchemes.GetName(scheme);
            RunJournaled(ctx, $"powercfg:{scheme:D}:{setting:D}:ac", CmdExe, CmdArgs(Set(target)), CmdExe, CmdArgs(Set(current)),
                $"{plan}, от сети: {describe(current)}", $"{plan}, от сети: {describe(target)}");
        },
    };
}
