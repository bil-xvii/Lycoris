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
    /// <summary>Upper bound for one WMI CreateRestorePoint call (a stuck VSS must not hold <see cref="Gate"/> forever).</summary>
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromMinutes(5);
    private static readonly object PendingLock = new();
    /// <summary>Original SystemRestorePointCreationFrequency while the 24-hour limit is lifted; null otherwise.</summary>
    private static RegistryValueSnapshot? _pendingFreq;
    /// <summary>The last CreateCore worker; may still run after a timeout.</summary>
    private static Task<RestorePointResult>? _inFlight;

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
            Elevation.Demand(Loc.T("включение защиты системы"));
            var drive = Environment.GetEnvironmentVariable("SystemDrive") + "\\";
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("Enable");
            inParams["Drive"] = drive;
            using var outParams = cls.InvokeMethod("Enable", inParams, null);
            var rc = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);
            if (rc == 0)
            {
                Log.Success("Защита системы включена для {0}", drive);
                return true;
            }
            Log.Warn("Не удалось включить защиту системы (код {0}).", rc);
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
            Log.Warn("Не удалось получить список точек восстановления: {0}", ex.Message);
        }
        return list.OrderByDescending(p => p.Created).ToList();
    });

    /// <summary>
    /// Creates a restore point. Windows normally allows only one per 24 hours; the limit is lifted
    /// for this one call (SystemRestorePointCreationFrequency = 0) and put back right after.
    /// </summary>
    public static async Task<RestorePointResult> CreateAsync(string description)
    {
        // Creating a restore point is disruptive (VSS freezes volumes, Windows allows one per 24 h): never two at once, never
        // next to an installer or another disruptive change. Inside an operation of the same flow this costs nothing (nested).
        try
        {
            return await OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.Disruptive, Loc.T("Создание точки восстановления")) { QueueIfBusy = true },
                _ => CreateGatedAsync(description)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationTimeoutException or OperationRefusedException or OperationCanceledException)
        {
            Log.Warn(Loc.T("Точка восстановления не создана: ") + ex.Message);
            return new(RestorePointOutcome.Failed, Loc.T("Точка восстановления не создана: ") + ex.Message);
        }
    }

    private static async Task<RestorePointResult> CreateGatedAsync(string description)
    {
        if (!Elevation.IsElevated)
            return new(RestorePointOutcome.Skipped, Loc.T("Нет прав администратора — точка восстановления не создана."));
        if (!IsEnabled())
            return new(RestorePointOutcome.Disabled,
                Loc.T("Защита системы отключена — точка восстановления не создана. Включите её в «Восстановлении»."));

        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A previous call timed out but its WMI call is still running (and still owns the lifted limit):
            // do not start a second one, it would snapshot the lifted value as the "original".
            if (_inFlight is { IsCompleted: false })
                return new(RestorePointOutcome.Failed,
                    Loc.T("Предыдущее создание точки восстановления ещё не завершилось — новая точка не создана."));
            var work = Task.Run(() => CreateCore(description));
            _inFlight = work;
            try
            {
                return await work.WaitAsync(CreateTimeout + TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The worker keeps running on its own and puts the limit back in its finally when WMI returns.
                Log.Warn("Создание точки восстановления не завершилось вовремя — продолжаем без неё.");
                return new(RestorePointOutcome.Failed, Loc.T("Windows слишком долго создаёт точку восстановления — операция прервана по времени."));
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Puts the original 24-hour restore point limit back if it is still lifted (synchronous; safe to call
    /// more than once). Called by the worker when it finishes and by App.OnExit when the window is closed mid-call.
    /// </summary>
    public static void RestoreThrottleIfPending()
    {
        lock (PendingLock)
        {
            if (_pendingFreq is not { } snap) return;
            try
            {
                RegistryHelper.Restore(snap);
                _pendingFreq = null;
                try { System.IO.File.Delete(PendingFrequencyFile); } catch { }
            }
            catch (Exception ex)
            {
                // Keep the pending file: the next elevated start retries (RecoverPendingFrequency).
                Log.Warn("Не удалось вернуть ограничение частоты точек восстановления: {0}", ex.Message);
            }
        }
    }

    private static string PendingFrequencyFile => System.IO.Path.Combine(AppPaths.MachineDir, "pending-restorepoint-frequency.json");

    /// <summary>
    /// Elevated startup: if Lycoris was closed while creating a restore point, the 24-hour limit we lifted
    /// for that one call is still lifted — put the original value back.
    /// </summary>
    public static void RecoverPendingFrequency()
    {
        if (!Elevation.IsElevated) return;
        try
        {
            if (!System.IO.File.Exists(PendingFrequencyFile)) return;
            var snap = System.Text.Json.JsonSerializer.Deserialize<RegistryValueSnapshot>(Monolith.Core.Security.SafeIo.ReadText(PendingFrequencyFile));
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
            Log.Warn("Не удалось восстановить ограничение частоты точек восстановления: {0}", ex.Message);
        }
    }

    private static RestorePointResult CreateCore(string description)
    {
        var before = MaxSequence();
        try
        {
            lock (PendingLock)
            {
                var freq = RegistryHelper.Snapshot(RegHive.HKLM, SrKey, "SystemRestorePointCreationFrequency");
                // Write the original down first, so a crash between here and "finally" can be repaired at next start.
                if (AppPaths.TryEnsureDir(AppPaths.MachineDir))
                    System.IO.File.WriteAllText(PendingFrequencyFile, System.Text.Json.JsonSerializer.Serialize(freq));
                _pendingFreq = freq;
                RegistryHelper.Write(RegHive.HKLM, SrKey, "SystemRestorePointCreationFrequency", RegistryValueKind.DWord, 0);
            }
        }
        catch
        {
            // Could not lift the limit (or record the original): put back whatever was recorded and go on with the limit.
            RestoreThrottleIfPending();
        }

        try
        {
            if (description.Length > 60) description = description[..60];
            Log.Info("Создание точки восстановления…");
            using var cls = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var inParams = cls.GetMethodParameters("CreateRestorePoint");
            inParams["Description"] = description;
            inParams["RestorePointType"] = 12u;  // MODIFY_SETTINGS
            inParams["EventType"] = 100u;        // BEGIN_SYSTEM_CHANGE
            using var outParams = cls.InvokeMethod("CreateRestorePoint", inParams, new InvokeMethodOptions(null, CreateTimeout));
            var rc = Convert.ToUInt32(outParams?["ReturnValue"] ?? 1u);

            var after = MaxSequence();
            if (rc == 0 && after > before)
            {
                Log.Success("Точка восстановления создана: {0}", description);
                return new(RestorePointOutcome.Created, Loc.Tf("Точка восстановления создана ({0:dd.MM HH:mm}).", DateTime.Now));
            }
            if (rc == 0)
                return new(RestorePointOutcome.Skipped, Loc.T("Windows не создала новую точку (недавно уже была создана)."));
            Log.Warn("Точка восстановления не создана, код {0}", rc);
            return new(RestorePointOutcome.Failed, Loc.Tf("Точка восстановления не создана (код {0}).", rc));
        }
        catch (Exception ex)
        {
            Log.Error("Ошибка создания точки восстановления", ex);
            return new(RestorePointOutcome.Failed, Loc.T("Ошибка создания точки восстановления: ") + ex.Message);
        }
        finally
        {
            RestoreThrottleIfPending();
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
