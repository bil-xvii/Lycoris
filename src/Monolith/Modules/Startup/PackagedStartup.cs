using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Startup;

/// <summary>
/// Windows.ApplicationModel.StartupTaskState — the DWORD <c>State</c> Windows keeps for every startup task of a
/// packaged (MSIX / Store) app. 1 = turned off by the user (only the user can turn it on again; this is what
/// Task Manager and Settings › Apps › Startup write), 3 and 4 are set by an administrator or group policy.
/// </summary>
public enum PackagedTaskState
{
    Disabled = 0,
    DisabledByUser = 1,
    Enabled = 2,
    DisabledByPolicy = 3,
    EnabledByPolicy = 4,
}

/// <summary>A <c>windows.startupTask</c> extension declared in an AppxManifest.xml.</summary>
public sealed record ManifestStartupTask(string TaskId, string? DisplayName, string? Executable, bool? EnabledByDefault);

/// <summary>What the startup page needs from a package manifest.</summary>
public sealed record PackageManifestInfo(string? Name, string? DisplayName, string? PublisherDisplayName, IReadOnlyList<ManifestStartupTask> Tasks);

/// <summary>Parts of a package full name: <c>Name_Version_Architecture_ResourceId_PublisherId</c>.</summary>
public sealed record PackageFullName(string FullName, string Name, Version? Version, string Architecture, string ResourceId, string PublisherId)
{
    public string FamilyName => $"{Name}_{PublisherId}";

    /// <summary>False for resource packs (<c>split.scale-100</c>…) and bundles (<c>~</c>): they carry no applications.</summary>
    public bool IsMain => !(ResourceId.StartsWith("split.", StringComparison.OrdinalIgnoreCase) || ResourceId == "~");
}

/// <summary>
/// Startup tasks of packaged apps (new Teams, Spotify / WhatsApp from the Store, vendor control panels…), exactly where
/// Task Manager keeps their switch (docs/research/general_tweaks.md §1, "Packaged (MSIX) apps"):
/// <c>HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\&lt;PackageFamilyName&gt;\&lt;TaskId&gt;</c>,
/// DWORD <c>State</c>. Discovery is read-only: a task is listed only when that value already exists and the installed
/// package's manifest declares the task. Lycoris never creates these keys and never touches policy states (3/4).
/// </summary>
public static class PackagedStartup
{
    public const string SystemAppDataKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData";
    public const string RepositoryPackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    public const string StateValue = "State";

    // Same shape the journal accepts for a packaged startup mark (JournalTrust): anything else could not be undone, so
    // Lycoris does not change it either.
    private static readonly Regex FamilyNamePattern = new(@"^[A-Za-z0-9.\-]{3,50}_[a-z0-9]{13}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex TaskIdPattern = new(@"^[A-Za-z0-9._\-]{1,128}$", RegexOptions.CultureInvariant);

    public static string KeyPath(string familyName, string taskId) => $@"{SystemAppDataKey}\{familyName}\{taskId}";

    /// <summary>The journal key of the State value (same format as <see cref="ChangeSession.SetRegistry"/>).</summary>
    public static string JournalTarget(string familyName, string taskId) => $@"reg:HKCU\{KeyPath(familyName, taskId)}\{StateValue}";

    /// <summary>Names Lycoris may write (and the journal will later accept for undo).</summary>
    public static bool IsJournalSafe(string? familyName, string? taskId) =>
        familyName is not null && taskId is not null && FamilyNamePattern.IsMatch(familyName) && TaskIdPattern.IsMatch(taskId);

    public static bool IsEnabled(int? state) => state is (int)PackagedTaskState.Enabled or (int)PackagedTaskState.EnabledByPolicy;

    public static bool IsPolicy(int? state) => state is (int)PackagedTaskState.DisabledByPolicy or (int)PackagedTaskState.EnabledByPolicy;

    /// <summary>States the user (and so Lycoris, like Task Manager) may switch: 0, 1 and 2.</summary>
    public static bool IsUserChangeable(int? state) => state is >= 0 and <= 2;

    /// <summary>What Task Manager writes: 2 to turn on, 1 (disabled by user — an app cannot re-enable itself from it) to turn off.</summary>
    public static int TargetState(bool enable) => enable ? (int)PackagedTaskState.Enabled : (int)PackagedTaskState.DisabledByUser;

    /// <summary>Why the live State must not be written, or null when switching is allowed.</summary>
    public static string? WriteRefusal(int? liveState) => liveState switch
    {
        null => Loc.T("задача автозапуска не найдена — приложение удалено или обновлено. Обновите список"),
        3 or 4 => Loc.T("состоянием управляет администратор или групповая политика — Lycoris его не меняет"),
        < 0 or > 4 => Loc.Tf("неизвестное состояние задачи ({0}) — Lycoris его не меняет", liveState),
        _ => null,
    };

    /// <summary>The State recorded before Lycoris changed it. Null when the journal snapshot is not a DWORD State.</summary>
    public static int? OriginalState(RegistryValueSnapshot snapshot) =>
        snapshot is { ValueExisted: true, Kind: RegistryValueKind.DWord }
        && uint.TryParse(snapshot.Data, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= int.MaxValue
            ? (int)v
            : null;

    /// <summary>Was the task on before Lycoris changed it? Null when the journal snapshot is not a DWORD State.</summary>
    public static bool? OriginalEnabled(RegistryValueSnapshot snapshot) =>
        OriginalState(snapshot) is { } v ? IsEnabled(v) : null;

    /// <summary>
    /// The State a switch leaves (same rule as <see cref="StartupChanges.SetEntriesAsync"/>): back to the state the task
    /// had before Lycoris (<paramref name="before"/>) the journal restores that exact value — 0 stays 0 —, otherwise
    /// the Task Manager value (<see cref="TargetState"/>) is written.
    /// </summary>
    public static int StateAfterSwitch(bool enable, int? before) =>
        before is { } b && IsEnabled(b) == enable ? b : TargetState(enable);

    /// <summary>"= …" text of the State a switch leaves, for the confirmation dialog.</summary>
    public static string SwitchText(bool enable, int state) =>
        state != TargetState(enable) ? Loc.Tf("{0} ({1}) — как было до Lycoris", state, StateText(state))
        : enable ? Loc.T("2 (включено)")
        : Loc.T("1 (отключено пользователем — как в Диспетчере задач)");

    /// <summary>Added when a switch leaves State 0: unlike 1, it lets the app turn its autostart on again by itself.</summary>
    public static string? StateNote(int state) => state == (int)PackagedTaskState.Disabled
        ? Loc.T("При State = 0 приложение может само снова включить свой автозапуск (при 1 — только пользователь).")
        : null;

    public static string StateText(int? state) => state switch
    {
        0 => Loc.T("отключено приложением"),
        1 => Loc.T("отключено пользователем"),
        2 => Loc.T("при входе в Windows"),
        3 => Loc.T("отключено политикой"),
        4 => Loc.T("включено политикой"),
        _ => Loc.T("состояние не распознано"),
    };

    // ───────────────────────────── Package names and manifests (pure) ─────────────────────────────

    /// <summary>Splits <c>Name_Version_Arch_ResourceId_PublisherId</c>; null when it is not a package full name.</summary>
    public static PackageFullName? ParseFullName(string? fullName)
    {
        if (string.IsNullOrWhiteSpace(fullName)) return null;
        var parts = fullName.Split('_');
        if (parts.Length < 5 || parts[0].Length == 0 || parts[^1].Length == 0) return null;
        Version.TryParse(parts[1], out var version);
        var resource = string.Join('_', parts[3..^1]);
        return new PackageFullName(fullName, parts[0], version, parts[2], resource, parts[^1]);
    }

    /// <summary>The package that holds the applications: a main package (no resource pack / bundle), newest version first.</summary>
    public static PackageFullName? ChooseMainPackage(IEnumerable<string> fullNames) =>
        fullNames.Select(ParseFullName).OfType<PackageFullName>()
            .OrderByDescending(p => p.IsMain)
            .ThenByDescending(p => p.Version ?? new Version(0, 0))
            .ThenBy(p => p.FullName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// Reads the startup tasks of an AppxManifest.xml (uap5:/desktop: <c>Extension Category="windows.startupTask"</c>).
    /// The executable comes from the extension or, when absent, from its Application. DTDs are refused.
    /// </summary>
    public static PackageManifestInfo? ParseManifest(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        XDocument doc;
        try
        {
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, SafeXml());
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        if (doc.Root is not { } root) return null;

        var identity = Child(root, "Identity");
        var properties = Child(root, "Properties");
        var tasks = new List<ManifestStartupTask>();
        foreach (var ext in root.Descendants().Where(e => e.Name.LocalName == "Extension"
                     && string.Equals((string?)e.Attribute("Category"), "windows.startupTask", StringComparison.OrdinalIgnoreCase)))
        {
            var task = Child(ext, "StartupTask");
            var id = ((string?)task?.Attribute("TaskId"))?.Trim();
            if (string.IsNullOrEmpty(id) || tasks.Any(t => string.Equals(t.TaskId, id, StringComparison.OrdinalIgnoreCase))) continue;

            var app = ext.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Application");
            var exe = NonEmpty((string?)ext.Attribute("Executable")) ?? NonEmpty((string?)app?.Attribute("Executable"));
            var enabledText = ((string?)task!.Attribute("Enabled"))?.Trim();
            bool? enabled = enabledText is null ? null : enabledText is "true" or "1";
            tasks.Add(new ManifestStartupTask(id, NonEmpty((string?)task.Attribute("DisplayName")), exe, enabled));
        }

        return new PackageManifestInfo(NonEmpty((string?)identity?.Attribute("Name")), NonEmpty(Child(properties, "DisplayName")?.Value),
            NonEmpty(Child(properties, "PublisherDisplayName")?.Value), tasks);

        static XElement? Child(XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>
    /// Sources for SHLoadIndirectString: an <c>@{…}</c> string as is, an <c>ms-resource:</c> reference expanded to
    /// <c>@{PackageFullName?ms-resource://Name/…}</c> (relative keys live under <c>/resources/</c>). Null for a literal text;
    /// empty when it is a resource reference that cannot be resolved without the package.
    /// </summary>
    public static IReadOnlyList<string>? IndirectSources(string raw, string? packageFullName, string? packageName)
    {
        raw = raw.Trim();
        if (raw.StartsWith("@{", StringComparison.Ordinal)) return [raw];
        const string scheme = "ms-resource:";
        if (!raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        if (string.IsNullOrEmpty(packageFullName) || string.IsNullOrEmpty(packageName)) return [];

        var rest = raw[scheme.Length..];
        var uris = new List<string>();
        if (rest.StartsWith("//", StringComparison.Ordinal)) uris.Add(scheme + rest);
        else if (rest.StartsWith('/')) uris.Add($"{scheme}//{packageName}{rest}");
        else if (rest.Contains('/'))
        {
            uris.Add($"{scheme}//{packageName}/{rest}");
            uris.Add($"{scheme}//{packageName}/resources/{rest}");
        }
        else
        {
            uris.Add($"{scheme}//{packageName}/resources/{rest}");
            uris.Add($"{scheme}//{packageName}/{rest}");
        }
        return uris.Select(u => $"@{{{packageFullName}?{u}}}").ToList();
    }

    /// <summary>Package root + manifest-relative executable ("VFS/…" or "app\x.exe") with Windows separators.</summary>
    public static string? JoinPackagePath(string? root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relative)) return null;
        var rel = relative.Trim().Replace('/', '\\').TrimStart('\\');
        if (rel.Length == 0 || rel.Split('\\').Any(s => s == "..")) return null;
        return root.Trim().TrimEnd('\\') + "\\" + rel;
    }

    /// <summary>"MSTeams" from "MSTeams_8wekyb3d8bbwe" — the last-resort display name.</summary>
    public static string NamePart(string familyName)
    {
        var i = familyName.LastIndexOf('_');
        return i > 0 ? familyName[..i] : familyName;
    }

    private static XmlReaderSettings SafeXml() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 8_000_000,
        IgnoreComments = true,
    };

    // ───────────────────────────── Live system (read-only) ─────────────────────────────

    /// <summary>The live DWORD State of a task; null when the key or value is missing or is not a DWORD.</summary>
    public static int? ReadState(string familyName, string taskId) =>
        RegistryHelper.Read(RegHive.HKCU, KeyPath(familyName, taskId), StateValue) is int v ? v : null;

    private sealed record InstalledPackage(PackageFullName Id, string Root, string? DisplayName);

    /// <summary>The packaged startup tasks of the current user (read-only; safe on any thread, no COM).</summary>
    public static List<StartupEntry> Scan()
    {
        var list = new List<StartupEntry>();
        try
        {
            using var hkcu = RegistryHelper.OpenBase(RegHive.HKCU);
            using var appData = hkcu.OpenSubKey(SystemAppDataKey, false);
            if (appData is null) return list;
            var repository = ReadRepository(hkcu);

            foreach (var family in appData.GetSubKeyNames())
            {
                try
                {
                    ScanFamily(list, hkcu, appData, family, repository);
                }
                catch (Exception ex)
                {
                    // Read-only scan: one odd package must not hide the rest of the autostart list.
                    Log.Warn("Автозапуск: пропущен пакет {0}: {1}", family, ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Автозапуск: нет доступа к задачам приложений MSIX: {0}", ex.Message);
        }
        return list;
    }

    private static void ScanFamily(List<StartupEntry> list, RegistryKey hkcu, RegistryKey appData, string family,
        Dictionary<string, List<string>>? repository)
    {
        var found = ReadFamilyTasks(appData, family);
        if (found.Count == 0) return;

        InstalledPackage? package = null;
        if (repository is not null)
        {
            // The repository lists every package registered for this user: no entry = leftover of an uninstalled app.
            if (!repository.TryGetValue(family, out var fullNames)) return;
            package = OpenPackage(hkcu, fullNames);
        }
        var manifest = package is null ? null : ReadManifest(package.Root);

        foreach (var (taskId, state) in found)
        {
            var declared = manifest?.Tasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.OrdinalIgnoreCase));
            if (manifest is not null && declared is null) continue; // another SystemAppData sub key, or a task the app dropped
            if (state is null && declared is null) continue;
            list.Add(Build(family, taskId, state, package, manifest, declared));
        }
    }

    private static List<(string TaskId, int? State)> ReadFamilyTasks(RegistryKey appData, string family)
    {
        var found = new List<(string, int?)>();
        using var familyKey = appData.OpenSubKey(family, false);
        if (familyKey is null) return found;
        foreach (var taskId in familyKey.GetSubKeyNames())
        {
            using var taskKey = familyKey.OpenSubKey(taskId, false);
            if (taskKey?.GetValue(StateValue) is not { } raw) continue;
            found.Add((taskId, taskKey.GetValueKind(StateValue) == RegistryValueKind.DWord && raw is int s ? s : null));
        }
        return found;
    }

    /// <summary>Family name → installed package full names; null when the repository cannot be read (then nothing is filtered).</summary>
    private static Dictionary<string, List<string>>? ReadRepository(RegistryKey hkcu)
    {
        try
        {
            using var key = hkcu.OpenSubKey(RepositoryPackagesKey, false);
            if (key is null) return null;
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var fullName in key.GetSubKeyNames())
            {
                if (ParseFullName(fullName) is not { } id) continue;
                if (!map.TryGetValue(id.FamilyName, out var names)) map[id.FamilyName] = names = [];
                names.Add(fullName);
            }
            return map.Count > 0 ? map : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static InstalledPackage? OpenPackage(RegistryKey hkcu, IEnumerable<string> fullNames)
    {
        if (ChooseMainPackage(fullNames) is not { } id) return null;
        string? root = null, displayName = null;
        try
        {
            using var key = hkcu.OpenSubKey($@"{RepositoryPackagesKey}\{id.FullName}", false);
            root = key?.GetValue("PackageRootFolder") as string;
            displayName = key?.GetValue("DisplayName") as string;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // fall back to the default install folder
        }
        if (string.IsNullOrWhiteSpace(root))
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", id.FullName);
        return new InstalledPackage(id, root.Trim(), displayName);
    }

    private static PackageManifestInfo? ReadManifest(string root)
    {
        var path = JoinPackagePath(root, "AppxManifest.xml");
        if (path is null) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8_000_000) return null;
            return ParseManifest(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or ArgumentException or NotSupportedException)
        {
            return null; // includes a malformed PackageRootFolder value
        }
    }

    private static StartupEntry Build(string family, string taskId, int? state, InstalledPackage? package,
        PackageManifestInfo? manifest, ManifestStartupTask? declared)
    {
        var target = JoinPackagePath(package?.Root, declared?.Executable);
        var exists = target is null || FileProbe.FileExists(target);
        var (company, description) = exists ? StartupScanner.ReadVersion(target) : (null, null);
        var publisher = Resolve(manifest?.PublisherDisplayName, package) ?? company;
        var displayName = Resolve(declared?.DisplayName, package) ?? Resolve(manifest?.DisplayName, package)
                          ?? Resolve(package?.DisplayName, package) ?? description ?? NamePart(family);

        return new StartupEntry
        {
            Source = StartupSource.PackagedTask,
            Name = taskId,
            PackageFamilyName = family,
            PackageState = state,
            DisplayName = displayName.Length <= 80 ? displayName : NamePart(family),
            Command = target is null ? $"{family} › {taskId}" : CommandLineParser.Join(target, null),
            TargetPath = target,
            TargetExists = exists,
            Publisher = publisher,
            Description = description,
            Enabled = IsEnabled(state),
            Risk = StartupClassifier.ClassifyPackagedTask(family, taskId, target, publisher),
        };
    }

    private static string? Resolve(string? raw, InstalledPackage? package)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sources = IndirectSources(raw, package?.Id.FullName, package?.Id.Name);
        if (sources is null) return raw.Trim();
        foreach (var source in sources)
        {
            if (LoadIndirect(source) is { Length: > 0 } text
                && !text.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("@{", StringComparison.Ordinal))
                return text.Trim();
        }
        return null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(string pszSource, [Out] char[] pszOutBuf, uint cchOutBuf, IntPtr ppvReserved);

    private static string? LoadIndirect(string source)
    {
        try
        {
            var buffer = new char[1024];
            if (SHLoadIndirectString(source, buffer, (uint)buffer.Length, IntPtr.Zero) != 0) return null;
            var end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end < 0 ? buffer.Length : end);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return null;
        }
    }
}
