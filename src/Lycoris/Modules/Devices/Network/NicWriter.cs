using System.Management;
using Lycoris.Core;
using Lycoris.Core.Devices;

namespace Lycoris.Modules.Devices.Network;

/// <summary>
/// NDIS advanced properties through the documented path (research network_adapter.md §2): read MSFT_NetAdapterAdvancedPropertySettingData
/// (no admin, locale-independent RegistryKeyword), write with Set-NetAdapterAdvancedProperty — the driver restarts the
/// adapter, exactly as the Device Manager «Дополнительно» tab does. The adapter is found by InterfaceGuid at run time
/// (names and indexes change). Only <see cref="DeviceTargets.NicKeywords"/> with a numeric value ever reach PowerShell.
/// Called only by ChangeSession (journal first) and DeviceRestore, off the UI thread; DeviceWriters wraps it in SafetyLock.
/// </summary>
internal sealed class NicWriter : INicWriter
{
    /// <summary>Null only when WMI answered and knows no such adapter; a failed query throws (undo then fails and keeps its record).</summary>
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
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Warn("Сеть: адаптер не прочитан — {0}", ex.Message);
            throw new InvalidOperationException(Loc.Tf("сетевой адаптер не прочитан (WMI): {0} — повторите позже", ex.Message), ex);
        }
        return null;
    }

    /// <summary>Null only when the driver has no such property; a failed query throws (see <see cref="AdapterName"/>).</summary>
    public NicPropertyInfo? Read(Guid interfaceGuid, string keyword)
    {
        if (!DeviceTargets.IsNicKeyword(keyword) && keyword != "*ReceiveBuffers") return null;
        return NetSystem.ReadProperties(interfaceGuid, keyword, throwOnError: true)
            .FirstOrDefault(p => string.Equals(p.Keyword, keyword, StringComparison.OrdinalIgnoreCase));
    }

    public bool Set(Guid interfaceGuid, string keyword, string value)
    {
        var script = Script(interfaceGuid, keyword, value);
        var r = ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { Encoding = OutputEncoding.Utf8, Timeout = TimeSpan.FromSeconds(90) })
            .GetAwaiter().GetResult();
        if (r.ExitCode == 0) return true;
        Log.Warn("Сеть: {0} = {1} — {2}", keyword, value, (r.StdErr + r.StdOut).Trim());
        // Some drivers (Realtek) restart the adapter in the middle of the cmdlet and it fails with «Общий сбой» although
        // the value was written; the driver's value is the truth, so wait for the adapter and read it back before reporting failure.
        var applied = WaitUntilApplied(() => TryRead(interfaceGuid, keyword), value, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(1), Thread.Sleep);
        if (applied) Log.Info("Сеть: {0} = {1} применено, хотя команда вернула ошибку (драйвер перезапустил адаптер)", keyword, value);
        return applied;
    }

    string? TryRead(Guid interfaceGuid, string keyword)
    {
        try { return Read(interfaceGuid, keyword)?.Value; }
        catch (InvalidOperationException) { return null; } // adapter still restarting
    }

    /// <summary>Polls until the property reads back as <paramref name="expected"/> or the time is up (pure; unit-tested).</summary>
    internal static bool WaitUntilApplied(Func<string?> read, string expected, TimeSpan timeout, TimeSpan step, Action<int> sleepMs)
    {
        for (var waited = TimeSpan.Zero; ; waited += step)
        {
            if (string.Equals(read()?.Trim(), expected.Trim(), StringComparison.Ordinal)) return true;
            if (waited >= timeout) return false;
            sleepMs((int)step.TotalMilliseconds);
        }
    }

    /// <summary>Game Boost session or a library game with a window: no adapter restart then (changes and per-row undo).</summary>
    public string? RunningGame() => NetGames.RunningGame();

    /// <summary>
    /// The PowerShell that sets one property of the adapter with this InterfaceGuid. Throws for anything outside the
    /// whitelist, so a forged keyword or value can never become a command (pure; unit-tested).
    /// </summary>
    internal static string Script(Guid interfaceGuid, string keyword, string value)
    {
        if (interfaceGuid == Guid.Empty) throw new ArgumentException(Loc.T("не указан сетевой адаптер"),nameof(interfaceGuid));
        var kw = DeviceTargets.NicKeywords.FirstOrDefault(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase))
                 ?? throw new ArgumentException(Loc.Tf("свойство «{0}» Lycoris не меняет", keyword),nameof(keyword));
        if (!DeviceTargets.IsNicValue(value)) throw new ArgumentException(Loc.T("значение свойства должно быть числом"),nameof(value));
        var guid = interfaceGuid.ToString("B").ToUpperInvariant();
        return $"$a=@(Get-NetAdapter -IncludeHidden | Where-Object {{ $_.InterfaceGuid -eq '{guid}' }});" +
               "if($a.Count -eq 0){throw 'сетевой адаптер не найден'};" + // loc:ignore PowerShell error text, stays in the Russian log
               $"Set-NetAdapterAdvancedProperty -Name $a[0].Name -RegistryKeyword '{kw}' -RegistryValue '{value}' -ErrorAction Stop;exit 0";
    }
}
