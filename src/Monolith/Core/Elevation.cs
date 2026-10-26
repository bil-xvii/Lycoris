using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace Monolith.Core;

/// <summary>Administrator rights: detection and relaunch through UAC.</summary>
public static class Elevation
{
    private static readonly Lazy<bool> Elevated = new(() =>
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    });

    /// <summary>True when the process runs with a full administrator token.</summary>
    public static bool IsElevated => Elevated.Value;

    /// <summary>
    /// Starts a new elevated copy of the app. Returns false if the user declined the UAC prompt.
    /// </summary>
    public static bool TryRelaunchElevated(IEnumerable<string>? extraArgs = null)
    {
        var args = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.Equals("--elevated", StringComparison.OrdinalIgnoreCase))
            .Concat(extraArgs ?? [])
            .Append("--elevated");

        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ExecutablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: user said "No"
        {
            Log.Warn("Запуск с правами администратора отменён пользователем — режим просмотра.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перезапуститься с правами администратора", ex);
            return false;
        }
    }

    /// <summary>Throws a friendly exception when an admin-only action is attempted without rights.</summary>
    public static void Demand(string action)
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException($"Для действия «{action}» нужны права администратора. Перезапустите MONOLITH от имени администратора.");
    }
}
