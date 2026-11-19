using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Modules.Games;
using Lycoris.Modules.Services;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

/// <summary>Read-only evidence from the desktop user's shell and Windows registrations. No target is launched or resolved.</summary>
internal static class RemovalSoftwareWindows
{
    private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private static readonly RegistryValueOptions Unexpanded = RegistryValueOptions.DoNotExpandEnvironmentNames;
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static RemovalSoftwareSnapshot Capture(CancellationToken ct) => Capture(ct, readProcesses: true);
    public static RemovalSoftwareSnapshot CaptureStartup(CancellationToken ct) => Capture(ct, readProcesses: false);

    private static RemovalSoftwareSnapshot Capture(CancellationToken ct, bool readProcesses)
    {
        var result = new RemovalSoftwareSnapshot();
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeLimit.CancelAfter(TimeSpan.FromSeconds(12));
        var work = timeLimit.Token;
        result.ServicesComplete = ReadPart(() => ReadServices(result.Services, work), ct);
        // HKCU, per-user Start Menu and protocol defaults must all describe the desktop account.
        var desktopAccount = Elevation.InteractiveUserState == true;
        result.IntegrationsComplete = desktopAccount && ReadPart(() => ReadIntegrations(result, work), ct);
        result.LaunchesComplete = desktopAccount && ReadPart(() => ReadLaunches(result, work), ct);
        result.ProcessesComplete = !readProcesses || ReadPart(() => ReadRunning(result.Running, work), ct);
        return result;
    }

    private static bool ReadPart(Func<bool> read, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return read();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex)) { return false; }
    }

    private static bool ReadServices(RemovalPathIndex paths, CancellationToken ct)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = machine.OpenSubKey(ServicesKey);
        if (services is null) return false;

        // This includes the SCM's registered drivers and stopped/disabled services, which a process scan misses.
        var names = services.GetSubKeyNames();
        if (names.Length > 4096) return false;
        var complete = true;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            complete &= ReadPart(() =>
            {
                using var service = services.OpenSubKey(name);
                if (service is null) return false;
                var image = ReadRegistryPath(service, "ImagePath");
                var serviceType = service.GetValue("Type") as int?;
                if (serviceType is null)
                    return image is null; // Configuration-only keys in the services tree have no service type or image.
                if (image is null && (serviceType.Value & 0x0B) != 0)
                    image = Path.Join(Windows, "System32", "drivers", name + ".sys");
                var imageOk = AddServicePath(paths, image);
                using var parameters = service.OpenSubKey("Parameters");
                var dllOk = AddServicePath(paths, parameters is null ? null : ReadRegistryPath(parameters, "ServiceDll"));
                var installation = ReadRegistryPath(service, "InstallationPath");
                var root = RemovalSoftwareEvidence.LocalPath(installation?.Trim('"'));
                if (root is not null) paths.Add(root);
                return image is not null && imageOk && dllOk && (installation is null || root is not null);
            }, ct);
        }
        return complete;
    }

    private static bool AddServicePath(RemovalPathIndex paths, string? raw)
    {
        if (raw is null) return true;
        var image = ServicePathParser.ExtractExecutable(raw, Windows);
        if (Path.GetFileName(image) == image && image.Length > 0)
            image = Path.Join(Windows, "System32", image);
        if (RemovalSoftwareEvidence.LocalPath(image) is not { } path) return false;
        paths.Add(path);
        return true;
    }

    private static bool ReadRunning(RemovalPathIndex paths, CancellationToken ct)
    {
        var processes = Process.GetProcesses();
        try
        {
            if (processes.Length > 2048) return false;
            var complete = true;
            foreach (var process in processes)
            {
                ct.ThrowIfCancellationRequested();
                if (process.Id is 0 or 4) continue; // Idle and System have no user-mode image.
                if (GamesNative.GetImagePath(process.Id) is { } image)
                {
                    if (RemovalSoftwareEvidence.LocalPath(image) is { } path) paths.Add(path);
                    else complete = false;
                    continue;
                }

                try
                {
                    if (IsKernelPseudoProcess(process.SessionId, process.ProcessName) || process.HasExited) continue;
                }
                catch (InvalidOperationException) { }
                complete = false;
            }
            return complete;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    // Applies only when native image lookup found no path. Vmmem is the OS-synthesized VM accounting process.
    // Microsoft: devblogs.microsoft.com/oldnewthing/20180717-00/?p=99265 and learn.microsoft.com/windows/wsl/troubleshooting-guide.
    internal static bool IsKernelPseudoProcess(int sessionId, string processName) =>
        sessionId == 0 && NonExecutableSystemProcesses.Contains(processName);

    private static readonly HashSet<string> NonExecutableSystemProcesses = new(RemovalSoftwareEvidence.Paths)
    {
        "Registry", "Memory Compression", "Secure System", "vmmem", "vmmemWSL",
    };

    private static bool ReadIntegrations(RemovalSoftwareSnapshot result, CancellationToken ct)
    {
        const uint protocolNoFixups = 0x00001000 | 0x00000100; // ASSOCF_IS_PROTOCOL | ASSOCF_NOFIXUPS
        foreach (var protocol in new[] { "http", "https" })
        {
            ct.ThrowIfCancellationRequested();
            var file = new StringBuilder(1024);
            var length = (uint)file.Capacity;
            var family = false;
            if (AssocQueryStringW(protocolNoFixups, 21, protocol, "open", file, ref length) == 0)
            {
                var id = file.ToString();
                var separator = id.IndexOf('!');
                family = separator > 0;
                if (family) result.DefaultBrowserFamilies.Add(id[..separator]);
            }
            file.Clear();
            length = (uint)file.Capacity;
            if (AssocQueryStringW(protocolNoFixups, 2, protocol, "open", file, ref length) == 0
                && RemovalSoftwareEvidence.LocalPath(file.ToString()) is { } executable)
                result.DefaultBrowser.Add(executable);
            else if (!family) return false;
        }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var machineEnvironment = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
        using var userEnvironment = user.OpenSubKey("Environment");
        if (machineEnvironment is null) return false;
        foreach (var key in new[] { machineEnvironment, userEnvironment })
        {
            ct.ThrowIfCancellationRequested();
            if (key is null) continue;
            var value = ReadRegistryPath(key, "Path");
            if (value is null) continue;
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (RemovalSoftwareEvidence.LocalPath(part.Trim('"')) is { } path && Path.GetDirectoryName(path) is not null)
                    result.Consumers.Add(path);
        }

        const string winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        foreach (var scope in new[] { machine, user })
        {
            ct.ThrowIfCancellationRequested();
            using var key = scope.OpenSubKey(winlogon);
            if (key is null) continue;
            foreach (var name in new[] { "Shell", "Userinit" })
            {
                if (ReadRegistryPath(key, name) is not { } value) continue;
                foreach (var command in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (!AddServicePath(result.Consumers, command)) return false;
            }
        }
        return true;
    }

    private static bool ReadLaunches(RemovalSoftwareSnapshot result, CancellationToken ct)
    {
        var complete = ReadMenu(result, Environment.SpecialFolder.Programs, userScope: true, ct);
        complete &= ReadMenu(result, Environment.SpecialFolder.CommonPrograms, userScope: false, ct);

        var aliases = new List<(string Key, RemovalLaunchBinding Binding)>();
        complete &= ReadAppPaths(RegistryHive.CurrentUser, RegistryView.Registry64, aliases, ct);
        complete &= ReadAppPaths(RegistryHive.LocalMachine, RegistryView.Registry64, aliases, ct);
        if (Environment.Is64BitOperatingSystem)
            complete &= ReadAppPaths(RegistryHive.LocalMachine, RegistryView.Registry32, aliases, ct);
        foreach (var aliasesByName in aliases.GroupBy(entry => entry.Key, RemovalSoftwareEvidence.Paths))
        {
            if (aliasesByName.Select(entry => entry.Binding.Target).Distinct(RemovalSoftwareEvidence.Paths).Skip(1).Any())
                continue; // The name resolves to different files across registry views/users.
            foreach (var alias in aliasesByName) result.AddLaunch(alias.Binding);
        }
        return complete;
    }

    private static bool ReadMenu(RemovalSoftwareSnapshot result, Environment.SpecialFolder folder, bool userScope,
        CancellationToken ct) => ReadPart(() =>
    {
        var menu = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        if (!RemovalSoftwareEvidence.RegularDirectory(menu)) return false;
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((menu, 0));
        var visited = 0;
        var complete = true;
        while (stack.TryPop(out var directory))
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(directory.Directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 4096) return false;
                if (RemovalSoftwareEvidence.LocalPath(item) is null) continue;
                var attributes = File.GetAttributes(item);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Startup-folder shortcuts cannot establish that the app can be started manually.
                    if (RemovalSoftwareEvidence.Paths.Equals(item, Environment.GetFolderPath(userScope
                            ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup))) continue;
                    if (directory.Depth >= 6) { complete = false; continue; }
                    stack.Push((item, directory.Depth + 1));
                }
                else if (Path.GetExtension(item).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    complete &= ReadPart(() => ReadShortcut(result, item, userScope, ct), ct);
            }
        }
        return complete;
    }, ct);

    private static bool ReadShortcut(RemovalSoftwareSnapshot result, string path, bool userScope, CancellationToken ct)
    {
        using var file = RemovalSoftwareEvidence.OpenRegularFile(path, 1024 * 1024);
        if (file is null) return true;
        var product = new StringBuilder(39);
        var component = new StringBuilder(39);
        var advertised = MsiGetShortcutTargetW(path, product, null, component) == 0;
        var link = ShellLink.Read(path);
        if (link is null) return false;
        if (link.Arguments.Length != 0) return true; // No uninstall switches, templates, wrappers or other arguments.
        Guid? productCode = null;
        var target = link.TargetPath;
        if (advertised)
        {
            if (!Guid.TryParse(product.ToString(), out var code)) return false;
            productCode = code;
            target = ReadMsiComponent(product.ToString(), component.ToString(), userScope);
        }
        if (RemovalSoftwareEvidence.LocalPath(target) is not { } executable
            || !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return true;
        result.AddLaunch(new(executable, Path.GetFileNameWithoutExtension(path),
            $"{path}|{RemovalSoftwareEvidence.Hash(file, ct)}", userScope, ProductCode: productCode));
        return true;
    }

    private static string? ReadMsiComponent(string product, string component, bool userScope)
    {
        var output = new StringBuilder(1024);
        if (!userScope) return Read(4); // MSIINSTALLCONTEXT_MACHINE
        var managed = Read(1);
        var unmanaged = Read(2);
        return managed is null ? unmanaged : unmanaged is null ? managed : null; // Conflicting user contexts.

        string? Read(uint context)
        {
            var size = (uint)output.Capacity;
            return MsiGetComponentPathExW(product, component, null, context, output, ref size) == 3 // INSTALLSTATE_LOCAL
                ? output.ToString() : null;
        }
    }

    private static bool ReadAppPaths(RegistryHive hive, RegistryView view,
        List<(string Key, RemovalLaunchBinding Binding)> aliases, CancellationToken ct) => ReadPart(() =>
    {
        using var scope = RegistryKey.OpenBaseKey(hive, view);
        using var appPaths = scope.OpenSubKey(AppPaths);
        if (appPaths is null) return true;
        var names = appPaths.GetSubKeyNames();
        if (names.Length > 2048) return false;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            using var entry = appPaths.OpenSubKey(name);
            if (entry is null) return false;
            if (entry.GetValue("", null, Unexpanded) is not string raw || raw.Length > 1024) continue;
            var kind = entry.GetValueKind("");
            if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)) continue;
            var path = kind == RegistryValueKind.ExpandString ? Environment.ExpandEnvironmentVariables(raw) : raw;
            if (RemovalSoftwareEvidence.LocalPath(path.Trim().Trim('"')) is not { } target
                || !Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !RemovalSoftwareEvidence.Paths.Equals(name, Path.GetFileName(target))) continue;
            var identity = $"{hive}|{view}|{name}|{kind}|{raw}|{ReadRegistryPath(entry, "Path")}";
            aliases.Add((name, new(target, name, identity, hive == RegistryHive.CurrentUser, AppPath: true)));
        }
        return true;
    }, ct);

    private static string? ReadRegistryPath(RegistryKey key, string name)
    {
        var raw = key.GetValue(name, null, Unexpanded);
        if (raw is null) return null;
        if (raw is not string path || path.Length > 32_767 || path.Any(char.IsControl))
            throw new InvalidDataException();
        return key.GetValueKind(name) switch
        {
            RegistryValueKind.String => path,
            RegistryValueKind.ExpandString => Environment.ExpandEnvironmentVariables(path),
            _ => throw new InvalidDataException(),
        };
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint kind, string association, string verb,
        StringBuilder value, ref uint length);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetShortcutTargetW(string path, StringBuilder product, StringBuilder? feature, StringBuilder component);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MsiGetComponentPathExW(string product, string component, string? sid, uint context,
        StringBuilder path, ref uint length);
}
