using System.Management;
using Monolith.Core;
using Monolith.Core.Devices;

namespace Monolith.Modules.Devices.Network;

/// <summary>
/// NDIS advanced properties through the documented path (research network_adapter.md §2): read MSFT_NetAdapterAdvancedPropertySettingData
/// (no admin, locale-independent RegistryKeyword), write with Set-NetAdapterAdvancedProperty — the driver restarts the
/// adapter, exactly as the Device Manager «Дополнительно» tab does. The adapter is found by InterfaceGuid at run time
/// (names and indexes change). Only <see cref="DeviceTargets.NicKeywords"/> with a numeric value ever reach PowerShell.
/// Called only by ChangeSession (journal first) and DeviceRestore, off the UI thread; DeviceWriters wraps it in SafetyLock.
/// </summary>
internal sealed class NicWriter : INicWriter
{
    public string? AdapterName(Guid interfaceGuid)
    {
        try
        {
            using var searcher = SystemProfile.Searcher(@"\\.\root\StandardCimv2",
                $"SELECT Name FROM MSFT_NetAdapter WHERE InterfaceGuid = '{interfaceGuid.ToString("B").ToUpperInvariant()}'");
            foreach (ManagementObject mo in searcher.Get())
                using (mo)
                    return mo["Name"]?.ToString();
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn($"Сеть: адаптер не прочитан — {ex.Message}");
        }
        return null;
    }

    public NicPropertyInfo? Read(Guid interfaceGuid, string keyword)
    {
        if (!DeviceTargets.IsNicKeyword(keyword) && keyword != "*ReceiveBuffers") return null;
        return NetSystem.ReadProperties(interfaceGuid, keyword).FirstOrDefault(p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
    }

    public bool Set(Guid interfaceGuid, string keyword, string value)
    {
        var script = Script(interfaceGuid, keyword, value);
        var r = ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { Encoding = OutputEncoding.Utf8, Timeout = TimeSpan.FromSeconds(90) })
            .GetAwaiter().GetResult();
        if (r.ExitCode != 0) Log.Warn($"Сеть: {keyword} = {value} — {(r.StdErr + r.StdOut).Trim()}");
        return r.ExitCode == 0;
    }

    /// <summary>
    /// The PowerShell that sets one property of the adapter with this InterfaceGuid. Throws for anything outside the
    /// whitelist, so a forged keyword or value can never become a command (pure; unit-tested).
    /// </summary>
    internal static string Script(Guid interfaceGuid, string keyword, string value)
    {
        if (interfaceGuid == Guid.Empty) throw new ArgumentException("не указан сетевой адаптер", nameof(interfaceGuid));
        var kw = DeviceTargets.NicKeywords.FirstOrDefault(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ArgumentException($"свойство «{keyword}» MONOLITH не меняет", nameof(keyword));
        if (!DeviceTargets.IsNicValue(value)) throw new ArgumentException("значение свойства должно быть числом", nameof(value));
        var guid = interfaceGuid.ToString("B").ToUpperInvariant();
        return $"$a=@(Get-NetAdapter -IncludeHidden | Where-Object {{ $_.InterfaceGuid -eq '{guid}' }});" +
               "if($a.Count -eq 0){throw 'сетевой адаптер не найден'};" +
               $"Set-NetAdapterAdvancedProperty -Name $a[0].Name -RegistryKeyword '{kw}' -RegistryValue '{value}' -ErrorAction Stop;exit 0";
    }
}
