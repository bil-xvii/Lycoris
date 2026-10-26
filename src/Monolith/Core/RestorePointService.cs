using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace Monolith.Core;

public enum RestorePointOutcome { Created, Disabled, Failed, Skipped }

public sealed record RestorePointResult(RestorePointOutcome Outcome, string Message)
{
    public bool Created => Outcome == RestorePointOutcome.Created;
}

public sealed record RestorePointInfo(uint Sequence, string Description, DateTime Created, uint Type);

/// <summary>Windows System Restore: status, enabling, creating and listing restore points (WMI root\default).</summary>
public static class RestorePointService
{
    private const string SrKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string SrPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>True when System Restore is turned on for at least one drive and not blocked by policy.</summary>
    public static bool IsEnabled()
    {
        if (RegistryHelper.ReadInt(RegHive.HKLM, SrPolicyKey, "DisableSR") == 1) return false;
        return RegistryHelper.ReadInt(RegHive.HKLM, SrKey, "RPSessionInterval") is 1;
    }

    public static bool IsBlockedByPolicy =>
        RegistryHelper.ReadInt(RegHive.HKLM, SrPolicyKey, "DisableSR") == 1 ||
        RegistryHelper.ReadInt(RegHive.HKLM, SrPolicyKey, "DisableConfig") == 1;

    /// <summary>Turns System Restore on for the system drive (same as "Configure → Turn on system protection").</summary>
    public static Task<bool> EnableAsync() => Task.Run(() =>
    {
        try
        {
            Elevation.Demand("включение защиты системы");
            var drive = Environment.GetEnvironmentVariable("SystemDrive") + "\\";
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("Enable");
            inParams["Drive"] = drive;
            using var outParams = cls.InvokeMethod("Enable", inParams, null);
            var rc = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
            if (rc == 0)
            {
                Log.Success($"Защита системы включена для {drive}");
                return true;
            }
            Log.Warn($"Не удалось включить защиту системы (код {rc}).");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось включить защиту системы", ex);
            return false;
        }
    });

    public static Task<IReadOnlyList<RestorePointInfo>> ListAsync() => Task.Run<IReadOnlyList<RestorePointInfo>>(() =>
    {
        var list = new List<RestorePointInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"),
                new ObjectQuery("SELECT Description, SequenceNumber, CreationTime, RestorePointType FROM SystemRestore"));
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var created = DateTime.MinValue;
                    try { created = ManagementDateTimeConverter.ToDateTime(mo["CreationTime"]?.ToString() ?? ""); } catch { }
                    list.Add(new RestorePointInfo(
                        Convert.ToUInt32(mo["SequenceNumber"] ?? 0u),
                        mo["Description"]?.ToString() ?? "",
                        created,
                        Convert.ToUInt32(mo["RestorePointType"] ?? 0u)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось получить список точек восстановления: {ex.Message}");
        }
        return list.OrderByDescending(p => p.Created).ToList();
    });

    /// <summary>
    /// Creates a restore point. Windows normally allows only one per 24 hours; the limit is lifted
    /// for this one call (SystemRestorePointCreationFrequency = 0) and put back right after.
    /// </summary>
    public static async Task<RestorePointResult> CreateAsync(string description)
    {
        if (!Elevation.IsElevated)
            return new(RestorePointOutcome.Skipped, "Нет прав администратора — точка восстановления не создана.");
        if (!IsEnabled())
            return new(RestorePointOutcome.Disabled,
                "Защита системы отключена — точка восстановления не создана. Включите её в «Восстановлении».");

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(() => CreateCore(description)).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string PendingFrequencyFile => System.IO.Path.Combine(AppPaths.MachineDir, "pending-restorepoint-frequency.json");

    /// <summary>
    /// Elevated startup: if MONOLITH was closed while creating a restore point, the 24-hour limit we lifted
    /// for that one call is still lifted — put the original value back.
    /// </summary>
    public static void RecoverPendingFrequency()
    {
        if (!Elevation.IsElevated) return;
        try
        {
            if (!System.IO.File.Exists(PendingFrequencyFile)) return;
            var snap = System.Text.Json.JsonSerializer.Deserialize<RegistryValueSnapshot>(System.IO.File.ReadAllText(PendingFrequencyFile));
            if (snap is { Hive: RegHive.HKLM, Name: "SystemRestorePointCreationFrequency" } &&
                string.Equals(snap.Path, SrKey, StringComparison.OrdinalIgnoreCase))
            {
                RegistryHelper.Restore(snap);
                Log.Info("Восстановлено исходное ограничение частоты точек восстановления.");
            }
            System.IO.File.Delete(PendingFrequencyFile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось восстановить ограничение частоты точек восстановления: {ex.Message}");
        }
    }

    private static RestorePointResult CreateCore(string description)
    {
        var before = MaxSequence();
        RegistryValueSnapshot? freq = null;
        try
        {
            freq = RegistryHelper.Snapshot(RegHive.HKLM, SrKey, "SystemRestorePointCreationFrequency");
            // Write the original down first, so a crash between here and "finally" can be repaired at next start.
            if (AppPaths.TryEnsureDir(AppPaths.MachineDir))
                System.IO.File.WriteAllText(PendingFrequencyFile, System.Text.Json.JsonSerializer.Serialize(freq));
            RegistryHelper.Write(RegHive.HKLM, SrKey, "SystemRestorePointCreationFrequency", RegistryValueKind.DWord, 0);
        }
        catch { freq = null; }

        try
        {
            if (description.Length > 60) description = description[..60];
            Log.Info("Создание точки восстановления…");
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12u;  // MODIFY_SETTINGS
            inParams["EventType"] = 100u;        // BEGIN_SYSTEM_CHANGE
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, null);
            var rc = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);

            var after = MaxSequence();
            if (rc == 0 && after > before)
            {
                Log.Success($"Точка восстановления создана: {description}");
                return new(RestorePointOutcome.Created, $"Точка восстановления создана ({DateTime.Now:dd.MM HH:mm}).");
            }
            if (rc == 0)
                return new(RestorePointOutcome.Skipped, "Windows не создала новую точку (недавно уже была создана).");
            Log.Warn($"Точка восстановления не создана, код {rc}");
            return new(RestorePointOutcome.Failed, $"Точка восстановления не создана (код {rc}).");
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка создания точки восстановления", ex);
            return new(RestorePointOutcome.Failed, "Ошибка создания точки восстановления: " + ex.Message);
        }
        finally
        {
            if (freq is not null)
            {
                try
                {
                    RegistryHelper.Restore(freq);
                    System.IO.File.Delete(PendingFrequencyFile);
                }
                catch { }
            }
        }
    }

    private static uint MaxSequence()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"),
                new ObjectQuery("SELECT SequenceNumber FROM SystemRestore"));
            uint max = 0;
            foreach (ManagementObject mo in searcher.Get())
                using (mo) max = Math.Max(max, Convert.ToUInt32(mo["SequenceNumber"] ?? 0u));
            return max;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Opens the standard Windows "System Restore" wizard.</summary>
    public static void OpenSystemRestoreWizard()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProcessRunner.Sys("rstrui.exe")) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть мастер восстановления системы", ex);
        }
    }

    /// <summary>Opens "System Properties → System Protection".</summary>
    public static void OpenSystemProtection()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProcessRunner.Sys("SystemPropertiesProtection.exe")) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть «Защита системы»", ex);
        }
    }
}
