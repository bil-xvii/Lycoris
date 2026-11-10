using System.Globalization;
using System.IO;
using System.ServiceProcess;
using Lycoris.Core;

namespace Lycoris.Modules.Tools;

/// <summary>State of one Windows Update service before the reset (read-only query).</summary>
internal sealed record WuServiceState(string Name, bool Exists, ServiceStartMode? StartMode, ServiceControllerStatus? Status)
{
    public bool IsDisabled => StartMode == ServiceStartMode.Disabled;
}

/// <summary>One folder the reset renames: <see cref="Source"/> → <see cref="Target"/> (same parent, never deleted).</summary>
internal sealed record WuFolder(string Source, string Target)
{
    public string TargetName => Path.GetFileName(Target);
}

/// <summary>
/// The system side of the reset, behind an interface so the sequence is unit-tested with a fake (tests never stop a
/// real service or rename a real folder). Every method returns null on success, else a short error text.
/// </summary>
internal interface IWuResetSystem
{
    WuServiceState Query(string name);
    bool IsRunning(string name);
    Task<string?> StopAsync(string name);
    Task<string?> StartAsync(string name);
    bool DirectoryExists(string path);
    string? Rename(string source, string target);
}

/// <summary>What the reset did, step by step (input of <see cref="WuReset.Classify"/>).</summary>
internal sealed record WuResetReport
{
    public List<string> Stopped { get; } = [];
    /// <summary>The service that refused to stop (then nothing was renamed).</summary>
    public string? StopFailed { get; set; }
    public string? StopError { get; set; }
    public List<WuFolder> Renamed { get; } = [];
    /// <summary>Folders that did not exist (nothing to rename).</summary>
    public List<WuFolder> Missing { get; } = [];
    public List<(WuFolder Folder, string Error)> RenameFailed { get; } = [];
    public List<string> Started { get; } = [];
    public List<(string Service, string Error)> StartFailed { get; } = [];
}

/// <summary>
/// «Сброс Центра обновления Windows»: the documented reset of the Windows Update components (Microsoft Learn,
/// «Troubleshoot Windows Update error 0x80240439», step 1 «Reset the Windows Update agent»;
/// https://learn.microsoft.com/troubleshoot/windows-client/installing-updates-features-roles/troubleshoot-windows-update-error-0x80240439):
/// stop wuauserv, BITS and CryptSvc → rename %windir%\SoftwareDistribution and %windir%\System32\catroot2 → start CryptSvc,
/// BITS and wuauserv. Lycoris only RENAMES (a timestamped *.bak-… name, so a second run never collides) — nothing is deleted.
/// It never changes a start type: a service the user disabled blocks the reset with an explanation instead of being enabled.
/// The steps Microsoft marks as last resort (security descriptor reset with sc.exe, DLL re-registration, Winsock reset)
/// are not part of it.
/// </summary>
internal static class WuReset
{
    public const string UpdateService = "wuauserv";
    public const string Bits = "BITS";
    public const string Crypt = "CryptSvc";
    /// <summary>Windows Modules Installer: running = Windows is installing or finishing updates right now.</summary>
    public const string ServicingService = "TrustedInstaller";

    /// <summary>Microsoft's order: net stop wuauserv · net stop bits · net stop cryptSvc.</summary>
    public static readonly IReadOnlyList<string> StopOrder = [UpdateService, Bits, Crypt];
    /// <summary>Microsoft's order: net start cryptSvc · net start bits · net start wuauserv.</summary>
    public static readonly IReadOnlyList<string> StartOrder = [Crypt, Bits, UpdateService];

    /// <summary>The only services this feature may stop and start (they are <see cref="ServiceControl.Protected"/>: never re-configured).</summary>
    public static bool IsResetService(string name) => StopOrder.Contains(name, StringComparer.OrdinalIgnoreCase);

    public const string CommandText = "net stop wuauserv · bits · cryptsvc → ren SoftwareDistribution, catroot2 → net start";

    /// <summary>The batch title in the undo journal (Russian key, translated where shown).</summary>
    public static string JournalTitle => Loc.Key("Сброс компонентов Центра обновления Windows");

    /// <summary>Stored in the batch instead of a restore point message (Russian key, translated where shown).</summary>
    public static string JournalNote =>
        Loc.Key("Точка восстановления не нужна: ничего не удалено — старые папки Центра обновления сохранены рядом с именем *.bak-дата и время.");

    public static string BackupSuffix(DateTime now) => ".bak-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    /// <summary>The two folders and their new names; a taken name gets «-2», «-3»… (never overwrites an older backup).</summary>
    public static IReadOnlyList<WuFolder> PlanFolders(string windowsDir, DateTime now, Func<string, bool> exists)
    {
        var suffix = BackupSuffix(now);
        string[] sources =
        [
            Path.Combine(windowsDir, "SoftwareDistribution"),
            Path.Combine(windowsDir, "System32", "catroot2"),
        ];
        var result = new List<WuFolder>();
        foreach (var source in sources)
        {
            var target = source + suffix;
            for (var i = 2; exists(target) && i < 100; i++) target = source + suffix + "-" + i.ToString(CultureInfo.InvariantCulture);
            result.Add(new WuFolder(source, target));
        }
        return result;
    }

    /// <summary>Display name of a service for the texts (the service name stays in brackets).</summary>
    public static string DisplayName(string service) => service.ToLowerInvariant() switch
    {
        "wuauserv" => Loc.T("Центр обновления Windows (wuauserv)"),
        "bits" => Loc.T("Фоновая интеллектуальная служба передачи (BITS)"),
        "cryptsvc" => Loc.T("Службы криптографии (CryptSvc)"),
        _ => service,
    };

    /// <summary>
    /// Why the reset must not start, or null. A disabled service is the user's (or an admin's) decision: Lycoris never
    /// enables it silently — the text tells what is disabled and where to change it.
    /// </summary>
    public static string? Blocker(IReadOnlyList<WuServiceState> services, bool servicingRunning)
    {
        var missing = services.Where(s => !s.Exists).Select(s => s.Name).ToList();
        if (missing.Count > 0)
            return Loc.Tf("Служба {0} не найдена в системе — сброс не выполняется.", string.Join(", ", missing));
        var disabled = services.Where(s => s.IsDisabled).Select(s => DisplayName(s.Name)).ToList();
        if (disabled.Count > 0)
            return Loc.Tf("Отключены службы: {0}. Lycoris не включает их сам — возможно, их отключили вы, администратор или другая программа. Если обновления нужны, задайте тип запуска «Вручную» в оснастке «Службы» (services.msc) и повторите сброс.",
                string.Join(", ", disabled));
        if (servicingRunning)
            return Loc.T("Windows сейчас устанавливает обновления (работает служба «Установщик модулей Windows»). Дождитесь окончания или перезагрузите компьютер, затем повторите сброс.");
        return null;
    }

    /// <summary>
    /// Runs the documented sequence. A service that does not stop aborts the renames (folders stay untouched); the
    /// services are started again in every case. <paramref name="emit"/> gets one console line per step.
    /// </summary>
    public static async Task<WuResetReport> RunAsync(IWuResetSystem system, IReadOnlyList<WuFolder> folders, Action<string> emit)
    {
        var report = new WuResetReport();
        foreach (var name in StopOrder)
        {
            emit(Loc.Tf("Остановка службы: {0}…", DisplayName(name)));
            var error = await system.StopAsync(name).ConfigureAwait(false);
            if (error is not null)
            {
                report.StopFailed = name;
                report.StopError = error;
                emit(Loc.Tf("Служба не остановилась: {0}. Папки не трогаем.", error));
                break;
            }
            report.Stopped.Add(name);
        }

        if (report.StopFailed is null)
        {
            foreach (var folder in folders)
            {
                if (!system.DirectoryExists(folder.Source))
                {
                    report.Missing.Add(folder);
                    emit(Loc.Tf("Папки нет, пропущено: {0}", folder.Source));
                    continue;
                }
                emit(Loc.Tf("Переименование: {0} → {1}", folder.Source, folder.TargetName));
                var error = system.Rename(folder.Source, folder.Target);
                if (error is null) report.Renamed.Add(folder);
                else
                {
                    report.RenameFailed.Add((folder, error));
                    emit(Loc.Tf("Не переименовано: {0}", error));
                }
            }
        }

        foreach (var name in StartOrder)
        {
            emit(Loc.Tf("Запуск службы: {0}…", DisplayName(name)));
            var error = await system.StartAsync(name).ConfigureAwait(false);
            if (error is null) report.Started.Add(name);
            else
            {
                report.StartFailed.Add((name, error));
                emit(Loc.Tf("Служба не запустилась: {0}", error));
            }
        }
        return report;
    }

    public static ToolOutcome Classify(WuResetReport r)
    {
        var startNote = r.StartFailed.Count == 0 ? ""
            : " " + Loc.Tf("Не запустились: {0} — перезагрузите компьютер, Windows запустит их сама.", string.Join(", ", r.StartFailed.Select(f => DisplayName(f.Service))));
        if (r.StopFailed is { } svc)
            return ToolOutcome.Fail(Loc.Tf("Сброс не выполнен: служба {0} не остановилась ({1}). Папки не тронуты.", DisplayName(svc), r.StopError) + startNote);
        if (r.Renamed.Count == 0 && r.RenameFailed.Count == 0)
            return ToolOutcome.Warn(Loc.T("Переименовывать нечего: папок SoftwareDistribution и catroot2 нет. Службы перезапущены.") + startNote);
        var kept = r.Renamed.Count == 0 ? ""
            : " " + Loc.Tf("Старые папки сохранены: {0}.", string.Join(", ", r.Renamed.Select(f => f.TargetName)));
        if (r.RenameFailed.Count > 0)
            return ToolOutcome.Warn(Loc.Tf("Сброс выполнен частично: не переименовано — {0}.", string.Join("; ", r.RenameFailed.Select(f => $"{Path.GetFileName(f.Folder.Source)} ({f.Error})"))) +
                                    kept + " " + Loc.T("Перезагрузите компьютер и повторите сброс.") + startNote);
        var text = Loc.T("Компоненты Центра обновления сброшены.") + kept + " " +
                   Loc.T("Следующая проверка обновлений займёт больше времени: Windows заново соберёт сведения об обновлениях.");
        return r.StartFailed.Count == 0 ? ToolOutcome.Ok(text) : ToolOutcome.Warn(text + startNote);
    }
}

/// <summary>
/// The real system: stop/start of exactly the three reset services (never their start type, never other services, never a
/// service's dependents) and a same-folder rename. Refused in self-test mode (<see cref="SafetyLock"/>).
/// </summary>
internal sealed class WuResetSystem : IWuResetSystem
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static WuResetSystem Instance { get; } = new();

    public WuServiceState Query(string name)
    {
        var start = ServiceControl.Query(name);
        return new WuServiceState(name, start is not null, start?.StartMode, ServiceControl.GetStatus(name));
    }

    public bool IsRunning(string name) => ServiceControl.GetStatus(name) is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

    public Task<string?> StopAsync(string name) => Task.Run(() => Control(name, stop: true));

    public Task<string?> StartAsync(string name) => Task.Run(() => Control(name, stop: false));

    private static string? Control(string name, bool stop)
    {
        if (!WuReset.IsResetService(name)) return Loc.Tf("служба {0} не входит в сброс", name);
        if (SafetyLock.IsLocked) return Loc.T("изменения системы заблокированы (режим самопроверки)");
        try
        {
            using var sc = new ServiceController(name);
            if (stop)
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    // Dependents are never stopped behind the user's back: a running dependent makes the stop fail.
                    if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop(stopDependentServices: false);
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
                }
                Log.Info("Служба остановлена для сброса Центра обновления: {0}", name);
            }
            else
            {
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    if (sc.Status != ServiceControllerStatus.StartPending) sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
                }
                Log.Info("Служба запущена после сброса Центра обновления: {0}", name);
            }
            return null;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return Loc.T("не ответила за 60 секунд");
        }
        catch (Exception ex)
        {
            Log.Warn("Служба {0}: {1}", name, ex.InnerException?.Message ?? ex.Message);
            return ex.InnerException?.Message ?? ex.Message;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string? Rename(string source, string target)
    {
        if (SafetyLock.IsLocked) return Loc.T("изменения системы заблокированы (режим самопроверки)");
        if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase))
            return Loc.T("новое имя должно быть в той же папке");
        try
        {
            if (Directory.Exists(target) || File.Exists(target)) return Loc.Tf("имя {0} уже занято", Path.GetFileName(target));
            Directory.Move(source, target);
            Log.Info("Папка переименована: {0} → {1}", source, target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Папка не переименована ({0}): {1}", source, ex.Message);
            return ex.Message;
        }
    }
}
