using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Lycoris.Core;

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

    /// <summary>Why the last <see cref="TryRelaunchElevated"/> refused to ask for rights (integrity check), or null.</summary>
    public static string? LastRefusal { get; private set; }

    /// <summary>
    /// Starts a new elevated copy of the app. Returns false if the user declined the UAC prompt.
    /// The copy gets "--elevated --parent &lt;pid&gt;" so it can wait for this process to close before
    /// taking the single-instance mutex.
    /// </summary>
    public static bool TryRelaunchElevated(IEnumerable<string>? extraArgs = null)
    {
        // UAC binds to the exe PATH, not its bytes: before asking, make sure the file is still the one we started from,
        // does not lie in a place anyone controls, and its signature (if any) and unpacked libraries are intact.
        var verdict = Security.SelfIntegrity.CheckBeforeElevation();
        LastRefusal = verdict.Allowed ? null : verdict.Reason;
        if (!verdict.Allowed)
        {
            Log.Error("Запуск с правами администратора отклонён проверкой целостности: {0}", verdict.Reason ?? "");
            return false;
        }

        var args = RelaunchArguments(Environment.GetCommandLineArgs().Skip(1), extraArgs, Environment.ProcessId);

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
    /// Starts a new copy with the SAME rights as this one (an elevated process starts an elevated child without a new UAC
    /// prompt; a read-only one starts a read-only child) — used to apply a setting that is read at startup (the language).
    /// The copy waits for this process to close (same hand-over as <see cref="TryRelaunchElevated"/>). False if it did not start.
    /// </summary>
    public static bool TryRelaunchSameRights(IEnumerable<string>? extraArgs = null)
    {
        // An elevated copy starts its elevated child WITHOUT a UAC prompt: it must be sure that the file is still itself.
        LastRefusal = null;
        if (IsElevated)
        {
            var verdict = Security.SelfIntegrity.CheckBeforeElevation();
            LastRefusal = verdict.Allowed ? null : verdict.Reason;
            if (!verdict.Allowed)
            {
                Log.Error("Перезапуск с правами администратора отклонён проверкой целостности: {0}", verdict.Reason ?? "");
                return false;
            }
        }
        var args = RelaunchArguments(Environment.GetCommandLineArgs().Skip(1), extraArgs, Environment.ProcessId);
        var psi = new ProcessStartInfo
        {
            FileName = AppPaths.ExecutablePath,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось перезапустить Lycoris", ex);
            return false;
        }
    }

    /// <summary>
    /// Arguments of this run that describe only this run and must not be carried over to a relaunched copy: the page
    /// and tab it was opened on and a remembered action («--replay-…», see Shell/AdminIntent). Each takes one value.
    /// The relaunch passes its own fresh values instead.
    /// </summary>
    public static readonly IReadOnlySet<string> PerRunArgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "--page", "--tab", "--focus", "--replay-page", "--replay-command", "--replay-param", "--parent", "--lang",
    };

    /// <summary>
    /// The command line of the elevated copy (pure; unit-tested): the current arguments without the per-run ones and
    /// without «--elevated», then <paramref name="extraArgs"/>, then «--elevated --parent &lt;pid&gt;».
    /// </summary>
    public static IReadOnlyList<string> RelaunchArguments(IEnumerable<string> current, IEnumerable<string>? extraArgs, int parentPid)
    {
        var list = current.ToList();
        var kept = new List<string>();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Equals("--elevated", StringComparison.OrdinalIgnoreCase)) continue;
            if (PerRunArgs.Contains(list[i])) { i++; continue; }
            kept.Add(list[i]);
        }
        kept.AddRange(extraArgs ?? []);
        kept.Add("--elevated");
        kept.Add("--parent");
        kept.Add(parentPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return kept;
    }

    /// <summary>
    /// True when this process runs under a different account than the one signed in to the desktop
    /// (e.g. a standard user approved UAC with another admin's password). HKCU then belongs to that other account.
    /// False also when it cannot be told (see <see cref="InteractiveUserState"/>, which keeps that case apart).
    /// </summary>
    public static bool IsRunningAsDifferentUser() => InteractiveUserState == false;

    // ───────────────────────────── Interactive user ─────────────────────────────

    /// <summary>TESTS ONLY: replaces the detection (null result = «cannot tell»).</summary>
    internal static Func<bool?>? InteractiveUserForTests { get; set; }

    private static bool? _interactiveKnown;

    /// <summary>
    /// Does Lycoris run as the person signed in to this desktop? True only when the shell (explorer.exe) of this session
    /// was found and its owner is this process's account; false when it is another account (over-the-shoulder UAC); null
    /// when it cannot be told (Explorer restarting, a shell replacement, Lycoris started before the shell). Only a known
    /// answer is cached: an unknown one is detected again next time. Blocking (process tokens): call off the UI thread.
    /// </summary>
    public static bool? InteractiveUserState
    {
        get
        {
            if (InteractiveUserForTests is { } test) return test();
            if (_interactiveKnown is { } known) return known;
            var detected = DetectInteractiveUser();
            if (detected is not null) _interactiveKnown = detected;
            return detected;
        }
    }

    /// <summary>
    /// Per-user writes (mouse/keyboard parameters, microphone levels) are allowed only here. Fails closed: an account that
    /// cannot be determined counts as «not the desktop user».
    /// </summary>
    public static bool IsInteractiveUser => InteractiveUserState == true;

    public static string OtherAccountText =>
        Loc.T("Lycoris запущен от имени другой учётной записи администратора — эти настройки применились бы к ней, а не к вам. " +
              "Запустите Lycoris из своей учётной записи.");
    public static string UnknownAccountText => Loc.T("Не удалось определить пользователя рабочего стола — пользовательские настройки пока не меняются.");

    /// <summary>Why per-user settings cannot be written now, or null when they can.</summary>
    public static string? InteractiveUserProblem => InteractiveUserState switch
    {
        true => null,
        false => OtherAccountText,
        null => UnknownAccountText,
    };

    private static bool? DetectInteractiveUser()
    {
        string? own;
        int session;
        try
        {
            using var me = WindowsIdentity.GetCurrent();
            own = me.User?.Value;
            using var current = Process.GetCurrentProcess();
            session = current.SessionId;
        }
        catch
        {
            return null;
        }
        if (own is null) return null;

        string? shell = null;
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    try
                    {
                        if (shell is null && p.SessionId == session) shell = OwnerSid(p.Id);
                    }
                    catch { }
                }
            }
        }
        catch
        {
            return null;
        }
        return shell is null ? null : string.Equals(shell, own, StringComparison.OrdinalIgnoreCase);
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    /// <summary>The account SID of a process (its token's user), or null.</summary>
    private static string? OwnerSid(int pid)
    {
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
            try
            {
                GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
                if (length <= 0) return null;
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, TokenUser, buffer, length, out _)) return null;
                    return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value; // TOKEN_USER.User.Sid
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Throws a friendly exception when an admin-only action is attempted without rights.</summary>
    public static void Demand(string action)
    {
        if (!IsElevated)
            throw new UnauthorizedAccessException(Loc.Tf("Для действия «{0}» нужны права администратора. Перезапустите Lycoris от имени администратора.", Loc.Sink(action)));
    }
}
