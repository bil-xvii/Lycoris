using System.IO;
using System.ServiceProcess;

namespace Monolith.Modules.Services;

/// <summary>A start type the user can pick in the ВСЕ СЛУЖБЫ tab.</summary>
public sealed record StartOption(string Label, ServiceStartMode Mode, bool Delayed)
{
    public static IReadOnlyList<StartOption> All { get; } =
    [
        new("Автоматически", ServiceStartMode.Automatic, false),
        new("Автоматически (отложенно)", ServiceStartMode.Automatic, true),
        new("Вручную", ServiceStartMode.Manual, false),
        new("Отключена", ServiceStartMode.Disabled, false),
    ];

    /// <summary>The option matching a start configuration; null for Boot/System (drivers).</summary>
    public static StartOption? For(ServiceStartMode mode, bool delayed) =>
        All.FirstOrDefault(o => o.Mode == mode && o.Delayed == (mode == ServiceStartMode.Automatic && delayed));

    public override string ToString() => Label;
}

/// <summary>How far the ВСЕ СЛУЖБЫ tab may change a service's start type.</summary>
public enum StartChangeScope
{
    /// <summary>Locked service: read-only.</summary>
    None,
    /// <summary>A service that must stay disabled (Remote Registry): only «Отключена».</summary>
    DisabledOnly,
    /// <summary>A Windows service: «Автоматически» or «Вручную», never «Отключена».</summary>
    NoDisable,
    /// <summary>A third-party service, or a curated entry whose researched target is «Отключена».</summary>
    Full,
}

/// <summary>
/// Start types the ВСЕ СЛУЖБЫ tab offers (docs/research/services.md §0.4, §1, §2B): MONOLITH disables only what its
/// research allows — curated entries with a «Отключена» target and third-party services (with a warning). Any other
/// Windows service can only be switched between «Автоматически» and «Вручную»: a disabled Windows service is not
/// started even on demand, and the feature that needs it breaks without saying why.
/// </summary>
public static class ServiceStartPolicy
{
    public static StartChangeScope ScopeFor(string name, bool isThirdParty, bool isLocked)
    {
        if (isLocked) return StartChangeScope.None;
        if (ServiceGuard.MustStayDisabled(name)) return StartChangeScope.DisabledOnly;
        if (ServiceCatalog.Find(name) is { } curated)
            return curated.Target == ServiceStartMode.Disabled ? StartChangeScope.Full : StartChangeScope.NoDisable;
        return isThirdParty ? StartChangeScope.Full : StartChangeScope.NoDisable;
    }

    /// <summary>True when MONOLITH may set this start type.</summary>
    public static bool Allows(StartChangeScope scope, StartOption? option) => option is not null && scope switch
    {
        StartChangeScope.Full => true,
        StartChangeScope.NoDisable => option.Mode != ServiceStartMode.Disabled,
        StartChangeScope.DisabledOnly => option.Mode == ServiceStartMode.Disabled,
        _ => false,
    };

    /// <summary>The combo box items: every allowed start type plus the current one (so the box can show it).</summary>
    public static IReadOnlyList<StartOption> OptionsFor(StartChangeScope scope, ServiceStartMode? current, bool delayed)
    {
        var now = current is { } mode ? StartOption.For(mode, delayed) : null;
        return StartOption.All.Where(o => Allows(scope, o) || o == now).ToList();
    }

    /// <summary>Why some start types are missing from the list (null = nothing to explain).</summary>
    public static string? Note(StartChangeScope scope, bool isCurated) => scope switch
    {
        StartChangeScope.DisabledOnly => "Эту службу можно только оставить отключённой — MONOLITH её не включает.",
        StartChangeScope.NoDisable when isCurated =>
            "«Отключена» для этой службы недоступно: MONOLITH ставит ей «Вручную», чтобы Windows могла запустить её сама, когда она понадобится.",
        StartChangeScope.NoDisable =>
            "Служба Windows вне списка рекомендаций: доступны только «Автоматически» и «Вручную». Отключённую службу Windows не запустит " +
            "даже по требованию, а сломанная функция не подскажет причину.",
        _ => null,
    };
}

/// <summary>Russian texts for start types and states.</summary>
public static class ServiceText
{
    /// <summary>
    /// The honest restore-point sentence for a confirmation. The Services page asks separately before it goes on
    /// without a restore point, so «protection off» says so instead of promising one.
    /// </summary>
    public static string RestorePointLine(bool settingOn, bool protectionOn, string journaled) =>
        !settingOn ? $"Точка восстановления не создаётся: она отключена в настройках MONOLITH. {journaled}"
        : !protectionOn ? $"Защита системы Windows выключена — точку восстановления создать не получится, MONOLITH спросит, продолжать ли без неё. {journaled}"
        : $"Перед изменением создаётся точка восстановления. {journaled}";

    public static string Start(ServiceStartMode? mode, bool delayed, bool triggers = false) => mode switch
    {
        null => "Нет данных",
        ServiceStartMode.Automatic when delayed => triggers ? "Автоматически (отложенно, триггер)" : "Автоматически (отложенно)",
        ServiceStartMode.Automatic => triggers ? "Автоматически (триггер)" : "Автоматически",
        ServiceStartMode.Manual => triggers ? "Вручную (триггер)" : "Вручную",
        ServiceStartMode.Disabled => "Отключена",
        ServiceStartMode.Boot => "Загрузка",
        ServiceStartMode.System => "Система",
        _ => mode.ToString()!,
    };

    public static string Status(ServiceControllerStatus? status) => status switch
    {
        ServiceControllerStatus.Running => "Работает",
        ServiceControllerStatus.Stopped => "Остановлена",
        ServiceControllerStatus.StartPending => "Запускается",
        ServiceControllerStatus.StopPending => "Останавливается",
        ServiceControllerStatus.Paused => "Приостановлена",
        ServiceControllerStatus.PausePending => "Приостанавливается",
        ServiceControllerStatus.ContinuePending => "Возобновляется",
        _ => "Нет данных",
    };

    public static bool IsRunning(ServiceControllerStatus? status) =>
        status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending;

    /// <summary>Win32_Service.State ("Running", "Stopped", …) → status.</summary>
    public static ServiceControllerStatus? ParseWmiState(string? state) => state switch
    {
        "Running" => ServiceControllerStatus.Running,
        "Stopped" => ServiceControllerStatus.Stopped,
        "Start Pending" => ServiceControllerStatus.StartPending,
        "Stop Pending" => ServiceControllerStatus.StopPending,
        "Paused" => ServiceControllerStatus.Paused,
        "Pause Pending" => ServiceControllerStatus.PausePending,
        "Continue Pending" => ServiceControllerStatus.ContinuePending,
        _ => null,
    };

    /// <summary>Win32_Service.StartMode ("Auto", "Manual", …) → start mode (registry is preferred; this is the fallback).</summary>
    public static ServiceStartMode? ParseWmiStartMode(string? startMode) => startMode switch
    {
        "Auto" => ServiceStartMode.Automatic,
        "Manual" => ServiceStartMode.Manual,
        "Disabled" => ServiceStartMode.Disabled,
        "Boot" => ServiceStartMode.Boot,
        "System" => ServiceStartMode.System,
        _ => null,
    };
}

/// <summary>Turns a service ImagePath / Win32_Service.PathName into the executable on disk.</summary>
public static class ServicePathParser
{
    public static string ExtractExecutable(string? pathName, string? windowsDir = null)
    {
        if (string.IsNullOrWhiteSpace(pathName)) return "";
        var p = pathName.Trim();
        windowsDir ??= Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (p.StartsWith('"'))
        {
            var end = p.IndexOf('"', 1);
            p = end > 1 ? p[1..end] : p.Trim('"');
        }
        else
        {
            // Unquoted: the executable ends at ".exe"/".sys"/".dll" followed by a space or the end (paths may contain spaces).
            var cut = -1;
            foreach (var ext in new[] { ".exe", ".sys", ".dll" })
            {
                var i = p.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                while (i >= 0)
                {
                    var after = i + ext.Length;
                    if (after == p.Length || p[after] == ' ')
                    {
                        if (cut < 0 || after < cut) cut = after;
                        break;
                    }
                    i = p.IndexOf(ext, after, StringComparison.OrdinalIgnoreCase);
                }
            }
            if (cut > 0) p = p[..cut];
            else if (p.IndexOf(' ') is var sp and > 0) p = p[..sp];
        }

        p = Environment.ExpandEnvironmentVariables(p);
        if (p.StartsWith(@"\??\", StringComparison.Ordinal)) p = p[4..];
        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) p = Path.Combine(windowsDir, p[12..]);
        else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            p = Path.Combine(windowsDir, p);
        return p;
    }

    public static bool IsSvchost(string executable) =>
        Path.GetFileName(executable).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string folder) =>
        !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(folder) &&
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Microsoft vs. third-party classification by the binary's publisher.</summary>
public static class ServiceClassifier
{
    public static bool IsMicrosoftCompany(string? company) =>
        !string.IsNullOrWhiteSpace(company) &&
        (company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || company.Contains("Майкрософт", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Third-party = the binary's publisher is not Microsoft. With no publisher info, files inside the Windows folder
    /// (outside the DriverStore) count as Windows components.
    /// </summary>
    public static bool IsThirdParty(string? company, string binaryPath, string windowsDir)
    {
        if (IsMicrosoftCompany(company)) return false;
        if (!string.IsNullOrWhiteSpace(company)) return true;
        if (ServiceGuard.IsDriverStorePath(binaryPath)) return true;
        return !ServicePathParser.IsUnder(binaryPath, windowsDir);
    }
}

public enum ServiceFilterKind { All, Running, Automatic, Disabled, ThirdParty }

/// <summary>The fields the ВСЕ СЛУЖБЫ search and filters look at.</summary>
public sealed record ServiceFacts(
    string Name, string DisplayName, string Description, string Path,
    bool IsRunning, ServiceStartMode? StartMode, bool IsThirdParty);

public static class ServiceFilter
{
    public static bool Matches(ServiceFacts s, ServiceFilterKind kind, string? search)
    {
        var passKind = kind switch
        {
            ServiceFilterKind.Running => s.IsRunning,
            ServiceFilterKind.Automatic => s.StartMode == ServiceStartMode.Automatic,
            ServiceFilterKind.Disabled => s.StartMode == ServiceStartMode.Disabled,
            ServiceFilterKind.ThirdParty => s.IsThirdParty,
            _ => true,
        };
        if (!passKind) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;

        foreach (var term in search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!(Contains(s.Name, term) || Contains(s.DisplayName, term) || Contains(s.Description, term) || Contains(s.Path, term)))
                return false;
        }
        return true;

        static bool Contains(string? hay, string needle) =>
            !string.IsNullOrEmpty(hay) && hay.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }
}

/// <summary>Detects PCs managed by an organization (domain or MDM): there group policies would revert changes.</summary>
public static class ManagedDetection
{
    /// <summary>
    /// A real MDM enrollment (Intune and other MDM servers): EnrollmentState = 1 and a full-MDM enrollment type
    /// (6 = MDM, 13 = Azure AD join + MDM) or the Intune provider. Built-in local/cloud "authority" entries don't count.
    /// </summary>
    public static bool IsMdmEnrollment(int? enrollmentState, int? enrollmentType, string? providerId) =>
        enrollmentState == 1 &&
        (enrollmentType is 6 or 13 || string.Equals(providerId, "MS DM Server", StringComparison.OrdinalIgnoreCase));
}
