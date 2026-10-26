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
    /// The copy gets "--elevated --parent &lt;pid&gt;" so it can wait for this process to close before
    /// taking the single-instance mutex.
    /// </summary>
    public static bool TryRelaunchElevated(IEnumerable<string>? extraArgs = null)
    {
        var current = Environment.GetCommandLineArgs().Skip(1).ToList();
        var kept = new List<string>();
        for (var i = 0; i < current.Count; i++)
        {
            if (current[i].Equals("--elevated", StringComparison.OrdinalIgnoreCase)) continue;
            if (current[i].Equals("--parent", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            kept.Add(current[i]);
        }
        var args = kept
            .Concat(extraArgs ?? [])
            .Append("--elevated")
            .Append("--parent")
            .Append(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

    /// <summary>
    /// True when this process runs under a different account than the one signed in to the desktop
    /// (e.g. a standard user approved UAC with another admin's password). HKCU then belongs to that other account.
    /// </summary>
    public static bool IsRunningAsDifferentUser()
    {
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            var mySid = me.User?.Value;
            var session = Process.GetCurrentProcess().SessionId;
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Handle, SessionId FROM Win32_Process WHERE Name = 'explorer.exe' AND SessionId = {session}");
            foreach (System.Management.ManagementObject p in searcher.Get())
            {
                using (p)
                {
                    using var outParams = p.InvokeMethod("GetOwnerSid", null, null);
                    var sid = outParams?["Sid"]?.ToString();
                    if (!string.IsNullOrEmpty(sid) && mySid is not null)
                        return !string.Equals(sid, mySid, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Throws a friendly exception when an admin-only action is attempted without rights.</summary>
    public static void Demand(string action)
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException($"Для действия «{action}» нужны права администратора. Перезапустите MONOLITH от имени администратора.");
    }
}
