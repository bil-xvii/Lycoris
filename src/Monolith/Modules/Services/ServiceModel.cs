using System.IO;
using System.ServiceProcess;

namespace Monolith.Modules.Services;

/// <summary>A start type the user can pick in the ВСЕ СЛУЖБЫ tab.</summary>
public sealed record StartOption(string Label, ServiceStartMode Mode, bool Delayed)
{
    public static IReadOnlyList<StartOption> All { get; } =
    [
        new(Loc.T("Автоматически"), ServiceStartMode.Automatic, false),
        new(Loc.T("Автоматически (отложенно)"), ServiceStartMode.Automatic, true),
        new(Loc.T("Вручную"), ServiceStartMode.Manual, false),
        new(Loc.T("Отключена"), ServiceStartMode.Disabled, false),
    ];

    /// <summary>The option matching a start configuration; null for Boot/System (drivers).</summary>
    public static StartOption? For(ServiceStartMode mode, bool delayed) =>
        All.FirstOrDefault(o => o.Mode == mode && o.Delayed == (mode == ServiceStartMode.Automatic && delayed));

    public override string ToString() => Label;
}

/// <summary>Russian texts for start types and states.</summary>
public static class ServiceText
{
    public static string Start(ServiceStartMode? mode, bool delayed, bool triggers = false) => mode switch
    {
        null => Loc.T("Нет данных"),
        ServiceStartMode.Automatic when delayed => triggers ? Loc.T("Автоматически (отложенно, триггер)") : Loc.T("Автоматически (отложенно)"),
        ServiceStartMode.Automatic => triggers ? Loc.T("Автоматически (триггер)") : Loc.T("Автоматически"),
        ServiceStartMode.Manual => triggers ? Loc.T("Вручную (триггер)") : Loc.T("Вручную"),
        ServiceStartMode.Disabled => Loc.T("Отключена"),
        ServiceStartMode.Boot => Loc.T("Загрузка"),
        ServiceStartMode.System => Loc.T("Система"),
        _ => mode.ToString()!,
    };

    public static string Status(ServiceControllerStatus? status) => status switch
    {
        ServiceControllerStatus.Running => Loc.T("Работает"),
        ServiceControllerStatus.Stopped => Loc.T("Остановлена"),
        ServiceControllerStatus.StartPending => Loc.T("Запускается"),
        ServiceControllerStatus.StopPending => Loc.T("Останавливается"),
        ServiceControllerStatus.Paused => Loc.T("Приостановлена"),
        ServiceControllerStatus.PausePending => Loc.T("Приостанавливается"),
        ServiceControllerStatus.ContinuePending => Loc.T("Возобновляется"),
        _ => Loc.T("Нет данных"),
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
        (company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || company.Contains(Loc.Keep("Майкрософт"), StringComparison.OrdinalIgnoreCase));

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
