using System.IO;
using System.Management;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

/// <summary>One row of Win32_PnPSignedDriver.</summary>
internal sealed record SignedDriverInfo(
    string DeviceName,
    string DeviceId,
    string DeviceClass,
    string Version,
    DateTime? Date,
    string Provider,
    string Manufacturer,
    string InfName,
    bool IsSigned,
    string Signer,
    string HardwareId)
{
    /// <summary>Third-party package from the DriverStore (published as oemNN.inf).</summary>
    public bool IsOem => IsOemInf(InfName);

    /// <summary>
    /// Ships with Windows or comes from Microsoft: an inbox INF (not oemNN.inf) or a Microsoft provider.
    /// Inbox INFs are deliberately dated 2006/2009 — never call them outdated (research §2).
    /// </summary>
    public bool IsMicrosoft => !IsOem || Provider.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

    public static bool IsOemInf(string? inf) =>
        inf is not null && inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase) &&
        inf.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) && inf.Length > 7 &&
        inf[3..^4].All(char.IsAsciiDigit);
}

/// <summary>A device Win32_PnPEntity reports with ConfigManagerErrorCode &lt;&gt; 0.</summary>
internal sealed record ProblemDeviceInfo(
    string Name,
    string DeviceId,
    string PnpClass,
    IReadOnlyList<string> HardwareIds,
    int Code,
    bool? Present,
    string Manufacturer);

/// <summary>Installed details from the display class key (HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-…}\00NN).</summary>
internal sealed record GpuRegistryInfo(string? InfPath, string? Provider, string? RadeonSoftwareVersion, string? RadeonSoftwareEdition);

/// <summary>
/// An "Apps &amp; features" (Uninstall key) entry. <see cref="Publisher"/> lets the vendor-software match require the
/// real publisher too (VendorSoftware.Matches — one rule for Drivers and «Устройства и сеть»).
/// </summary>
internal sealed record UninstallEntry(string DisplayName, string? Version, string? InstallLocation, string? DisplayIcon, string? Publisher = null);

/// <summary>Read-only Windows Update driver policy state (research §1.1 "Policies to read and display").</summary>
internal sealed record WuPolicyInfo(bool ExcludedByPolicy, bool ExcludedByMdm, int? SearchOrderConfig, bool UsesWsus, string? WsusServer)
{
    public bool DriversBlocked => ExcludedByPolicy || ExcludedByMdm;
}

/// <summary>Read-only inventory of drivers and devices (WMI + registry). Call from a background thread.</summary>
internal static class DriverInventory
{
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string WuPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string WuMdmKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\Update";
    private const string DriverSearchingKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching";
    private const string NvidiaDisplayDriverUninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B2FE1952-0186-46C3-BAEC-A80AA35AC5B8}_Display.Driver";

    public static List<SignedDriverInfo> LoadSignedDrivers()
    {
        var list = new List<SignedDriverInfo>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(
            "SELECT DeviceName, DeviceID, DeviceClass, DriverVersion, DriverDate, DriverProviderName, Manufacturer, " +
            "InfName, IsSigned, Signer, HardWareID FROM Win32_PnPSignedDriver"));
        foreach (var mo in searcher.Get())
        {
            using (mo)
            {
                var deviceId = Str(mo["DeviceID"]);
                var name = Str(mo["DeviceName"]);
                if (name.Length == 0 && deviceId.Length == 0) continue;
                list.Add(new SignedDriverInfo(
                    name.Length > 0 ? name : deviceId,
                    deviceId,
                    Str(mo["DeviceClass"]),
                    Str(mo["DriverVersion"]),
                    CimDate(mo["DriverDate"]),
                    Str(mo["DriverProviderName"]),
                    Str(mo["Manufacturer"]),
                    Str(mo["InfName"]),
                    mo["IsSigned"] is true,
                    Str(mo["Signer"]),
                    Str(mo["HardWareID"])));
            }
        }
        return list;
    }

    public static List<ProblemDeviceInfo> LoadProblemDevices()
    {
        try
        {
            return QueryProblems("SELECT Name, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode, Present, Manufacturer " +
                                 "FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", withPresent: true);
        }
        catch (ManagementException)
        {
            // Older builds without the Present property.
            return QueryProblems("SELECT Name, DeviceID, PNPClass, HardwareID, ConfigManagerErrorCode, Manufacturer " +
                                 "FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0", withPresent: false);
        }
    }

    private static List<ProblemDeviceInfo> QueryProblems(string wql, bool withPresent)
    {
        var list = new List<ProblemDeviceInfo>();
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\cimv2"), new ObjectQuery(wql));
        foreach (var mo in searcher.Get())
        {
            using (mo)
            {
                var ids = mo["HardwareID"] as string[] ?? [];
                bool? present = withPresent && mo["Present"] is bool p ? p : null;
                var id = Str(mo["DeviceID"]);
                var name = Str(mo["Name"]);
                list.Add(new ProblemDeviceInfo(
                    name.Length > 0 ? name : "Неизвестное устройство",
                    id,
                    Str(mo["PNPClass"]),
                    ids,
                    Convert.ToInt32(mo["ConfigManagerErrorCode"] ?? 0),
                    present,
                    Str(mo["Manufacturer"])));
            }
        }
        return list;
    }

    /// <summary>Finds the display class subkey of a GPU (by MatchingDeviceId prefix or DriverDesc).</summary>
    public static GpuRegistryInfo? ReadGpuRegistry(GpuInfo gpu)
    {
        try
        {
            using var hklm = RegistryHelper.OpenBase(RegHive.HKLM);
            using var cls = hklm.OpenSubKey(DisplayClassKey);
            if (cls is null) return null;
            RegistryKey? byDesc = null;
            try
            {
                foreach (var sub in cls.GetSubKeyNames().Where(s => s.Length == 4 && s.All(char.IsAsciiDigit)))
                {
                    var k = cls.OpenSubKey(sub);
                    if (k is null) continue;
                    var matching = k.GetValue("MatchingDeviceId")?.ToString();
                    if (!string.IsNullOrEmpty(matching) && gpu.PnpDeviceId.StartsWith(matching, StringComparison.OrdinalIgnoreCase))
                    {
                        using (k) return FromKey(k);
                    }
                    if (byDesc is null && string.Equals(k.GetValue("DriverDesc")?.ToString(), gpu.Name, StringComparison.OrdinalIgnoreCase))
                        byDesc = k;
                    else
                        k.Dispose();
                }
                return byDesc is null ? null : FromKey(byDesc);
            }
            finally
            {
                byDesc?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Драйверы: не удалось прочитать сведения видеоадаптера: {ex.Message}");
            return null;
        }

        static GpuRegistryInfo FromKey(RegistryKey k) => new(
            k.GetValue("InfPath")?.ToString(),
            k.GetValue("ProviderName")?.ToString(),
            k.GetValue("RadeonSoftwareVersion")?.ToString(),
            k.GetValue("RadeonSoftwareEdition")?.ToString());
    }

    /// <summary>NVIDIA package version from the installer's Uninstall entry ("610.88"), cross-check for the Windows version.</summary>
    public static string? ReadNvidiaPackageVersion() =>
        RegistryHelper.ReadString(RegHive.HKLM, NvidiaDisplayDriverUninstall, "DisplayVersion");

    /// <summary>All Uninstall entries (64- and 32-bit views, machine and user).</summary>
    public static List<UninstallEntry> ReadUninstallEntries()
    {
        var result = new List<UninstallEntry>();
        void Scan(RegistryKey root, string path)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key is null) return;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(sub);
                        var name = k?.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        result.Add(new UninstallEntry(name.Trim(), k!.GetValue("DisplayVersion")?.ToString(),
                            k.GetValue("InstallLocation")?.ToString(), k.GetValue("DisplayIcon")?.ToString(),
                            k.GetValue("Publisher")?.ToString()?.Trim()));
                    }
                    catch { }
                }
            }
            catch { }
        }

        const string u64 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        const string u32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
        using (var hklm = RegistryHelper.OpenBase(RegHive.HKLM))
        {
            Scan(hklm, u64);
            Scan(hklm, u32);
        }
        using (var hkcu = RegistryHelper.OpenBase(RegHive.HKCU))
            Scan(hkcu, u64);
        return result;
    }

    public static WuPolicyInfo ReadWuPolicy()
    {
        var policy = RegistryHelper.ReadInt(RegHive.HKLM, WuPolicyKey, "ExcludeWUDriversInQualityUpdate") == 1;
        var mdm = RegistryHelper.ReadInt(RegHive.HKLM, WuMdmKey, "ExcludeWUDriversInQualityUpdate") == 1;
        var order = RegistryHelper.ReadInt(RegHive.HKLM, DriverSearchingKey, "SearchOrderConfig");
        var server = RegistryHelper.ReadString(RegHive.HKLM, WuPolicyKey, "WUServer");
        var useServer = RegistryHelper.ReadInt(RegHive.HKLM, WuPolicyKey + @"\AU", "UseWUServer") == 1;
        return new WuPolicyInfo(policy, mdm, order, useServer && !string.IsNullOrWhiteSpace(server), useServer ? server : null);
    }

    /// <summary>Number of third-party driver packages in the DriverStore (= files %windir%\INF\oem*.inf).</summary>
    public static int CountOemPackages()
    {
        try
        {
            var inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
            return Directory.EnumerateFiles(inf, "oem*.inf").Count(f => SignedDriverInfo.IsOemInf(Path.GetFileName(f)));
        }
        catch
        {
            return 0;
        }
    }

    private static string Str(object? o) => o?.ToString()?.Trim() ?? "";

    private static DateTime? CimDate(object? o)
    {
        var s = o?.ToString();
        if (string.IsNullOrWhiteSpace(s)) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s).Date; } catch { return null; }
    }
}
