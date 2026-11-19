using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Devices.Vendors;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

internal enum RemovalSoftwareRole { Ordinary, GameLibrary, Sensitive, Protected }

internal sealed class RemovalFileTooLargeException : Exception;

internal sealed record RemovalExecutable(string Path, string Signer, string Company, string Product,
    string? Description, string? OriginalName, string Hash);

internal sealed record RemovalUninstaller(string Signer, string Hash);



internal sealed record RemovalLaunchBinding(string Target, string Label, string Identity, bool UserScope,
    bool AppPath = false, Guid? ProductCode = null);

internal sealed class RemovalPathIndex
{
    private readonly HashSet<string> _exact = new(RemovalSoftwareEvidence.Paths);
    private readonly HashSet<string> _ancestors = new(RemovalSoftwareEvidence.Paths);

    public void Add(string path)
    {
        if (!_exact.Add(path)) return;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            _ancestors.Add(parent);
    }

    public bool ContainsExact(string path) => _exact.Contains(path);
    public bool ContainsUnder(string path) => _ancestors.Contains(path) || _exact.Contains(path);

    public bool Overlaps(string path)
    {
        if (ContainsUnder(path)) return true;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (_exact.Contains(parent)) return true;
        return false;
    }
}

internal sealed class RemovalInstallRoots
{
    private readonly Dictionary<string, HashSet<string>> _directories = new(RemovalSoftwareEvidence.Paths);
    private readonly Dictionary<string, HashSet<string>> _ancestors = new(RemovalSoftwareEvidence.Paths);

    public RemovalInstallRoots(UninstallInventory inventory)
    {
        foreach (var entry in inventory.Entries)
        {
            var owner = RemovalPolicy.RegistrationKey(entry);
            Add(RemovalSoftwareEvidence.LocalPath(entry.InstallLocation), owner);
            Add(RemovalSoftwareEvidence.IconDirectory(entry.DisplayIcon), owner);
            if (RemovalSoftwareEvidence.LocalPath(entry.UninstallExecutable) is { } uninstall)
                Add(Path.GetDirectoryName(uninstall), owner);
        }
    }

    private void Add(string? path, string key)
    {
        if (path is null || Path.GetDirectoryName(path) is null || RemovalSoftwareEvidence.IsSystemPath(path)) return;
        AddOwner(_directories, path);
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            AddOwner(_ancestors, parent);

        void AddOwner(Dictionary<string, HashSet<string>> index, string directory)
        {
            if (!index.TryGetValue(directory, out var owners))
                index.Add(directory, owners = new(RemovalSoftwareEvidence.Paths));
            owners.Add(key);
        }
    }

    public bool Shared(string path, UninstallRegistration registration)
    {
        var own = RemovalPolicy.RegistrationKey(registration);
        if (OtherOwner(_ancestors, path)) return true;
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent))
            if (OtherOwner(_directories, parent)) return true;
        return false;

        bool OtherOwner(Dictionary<string, HashSet<string>> index, string directory) =>
            index.TryGetValue(directory, out var owners) && (owners.Count != 1 || !owners.Contains(own));
    }
}

internal sealed class RemovalSoftwareSnapshot
{
    private readonly Dictionary<string, List<RemovalLaunchBinding>> _byTarget = new(RemovalSoftwareEvidence.Paths);
    private readonly Dictionary<string, List<RemovalLaunchBinding>> _byRoot = new(RemovalSoftwareEvidence.Paths);

    public bool LaunchesComplete { get; set; } = true;
    public bool ServicesComplete { get; set; } = true;
    public bool ProcessesComplete { get; set; } = true;
    public bool IntegrationsComplete { get; set; } = true;
    public bool StartupComplete => LaunchesComplete && ServicesComplete && IntegrationsComplete;
    public bool Complete => StartupComplete && ProcessesComplete;
    public RemovalPathIndex Services { get; } = new();
    public RemovalPathIndex DefaultBrowser { get; } = new();
    public HashSet<string> DefaultBrowserFamilies { get; } = new(RemovalSoftwareEvidence.Paths);
    public RemovalPathIndex Consumers { get; } = new();
    public RemovalPathIndex Running { get; } = new();

    public void AddLaunch(RemovalLaunchBinding binding)
    {
        Add(_byTarget, binding.Target);
        for (var parent = Path.GetDirectoryName(binding.Target); parent is not null; parent = Path.GetDirectoryName(parent))
            Add(_byRoot, parent);

        void Add(Dictionary<string, List<RemovalLaunchBinding>> index, string key)
        {
            if (!index.TryGetValue(key, out var values)) index.Add(key, values = []);
            values.Add(binding);
        }
    }

    public IReadOnlyList<RemovalLaunchBinding> LaunchesFor(string path) => _byTarget.TryGetValue(path, out var entries) ? entries : [];
    public IReadOnlyList<RemovalLaunchBinding> LaunchesUnder(string root) => _byRoot.TryGetValue(root, out var entries) ? entries : [];
}

internal static partial class RemovalSoftwareEvidence
{
    internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private const long MaximumExecutableSize = 2L * 1024 * 1024 * 1024;
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string CommonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static readonly string ProgramFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    private static readonly string[] ProtectedVendors =
    [
        "ASUSTeK", "Acer", "Dell", "Hewlett Packard", "HP Inc", "Lenovo", "Micro-Star", "Gigabyte",
        "Logitech", "Corsair", "Razer", "Brother", "Epson", "Canon", "SteelSeries", "McAfee", "Norton",
        "Broadcom", "Fortinet", "Palo Alto Networks", "CrowdStrike", "SentinelOne", "Symantec", "WatchGuard",
        "Acronis", "Veeam", "Citrix", "TeamViewer", "AnyDesk", "VeraCrypt", "TrueCrypt", "IDRIX",
    ];

    private static readonly HashSet<string> CommandHosts = new(Paths)
    {
        "cmd", "powershell", "pwsh", "rundll32", "regsvr32", "wscript", "cscript", "msiexec", "mshta",
        "conhost", "explorer", "control", "mmc", "wsl", "bash", "sh", "node", "python", "pythonw", "java", "javaw",
        "dotnet", "electron", "wine", "ssh", "taskeng", "schtasks", "installutil", "msbuild",
    };

    public static RemovalSoftwareRole Role(string? path, string? signer, params string?[] labels)
    {
        var fromSigner = StartupClassifier.ClassifyEntry(path, signer);
        if (fromSigner != StartupRisk.Normal || StartupClassifier.MatchesVendor(signer, ProtectedVendors)
            || IsSystemPath(path) || IsCommandHost(path) || IsSignedDeviceUtility(path, signer))
            return RemovalSoftwareRole.Protected;

        var all = labels.Append(Path.GetFileNameWithoutExtension(path)).Where(label => !string.IsNullOrWhiteSpace(label));
        var result = RemovalSoftwareRole.Ordinary;
        foreach (var label in all)
        {
            var words = CamelBoundary().Replace(label!, " ").Replace('-', ' ').Replace('_', ' ');
            if (StartupClassifier.ClassifyEntry(path, label) != StartupRisk.Normal
                || StartupClassifier.MatchesVendor(label, ProtectedVendors)
                || ProtectedRole().IsMatch(words))
                return RemovalSoftwareRole.Protected;
            if (SensitiveRole().IsMatch(words))
                result = RemovalSoftwareRole.Sensitive;
            else if (result == RemovalSoftwareRole.Ordinary && GameLibraryRole().IsMatch(label!))
                result = RemovalSoftwareRole.GameLibrary;
        }
        return result;
    }

    private static bool IsSignedDeviceUtility(string? path, string? signer) =>
        !string.IsNullOrWhiteSpace(signer) && Path.GetFileName(path) is { Length: > 0 } executable
        && VendorApps.All.Any(app => app.IsLaunchable && Paths.Equals(executable, Path.GetFileName(app.ExeUnderProgramFiles))
            && app.Signers.Any(expected => VendorDownloads.SignerMatches(signer, expected)));

    public static bool IsCommandHost(string? path) => Path.GetFileNameWithoutExtension(path) is { } file && CommandHosts.Contains(file);

    public static bool PublishersMatch(string? first, string? second)
    {
        var left = PublisherKey(first);
        return left.Length >= 2 && Paths.Equals(left, PublisherKey(second));

        static string PublisherKey(string? name) => string.Concat(Words().Matches(name ?? "").Select(match => match.Value)
            .Where(word => !CompanySuffix().IsMatch(word)));
    }

    public static bool ProductMatches(string? displayName, params string?[] names)
    {
        var left = ProductKey(displayName);
        return left.Length >= 2 && names.Any(name => Paths.Equals(left, ProductKey(name)));
    }

    private static string ProductKey(string? value) =>
        string.Concat(VersionOrArchitecture().Replace(value ?? "", "").Where(char.IsLetterOrDigit));

    public static string? DedicatedRoot(UninstallRegistration entry)
    {
        var path = LocalPath(entry.InstallLocation);
        if (path is null || Path.GetDirectoryName(path) is null || IsSystemPath(path)) return null;
        var userRoot = IsUnder(path, LocalAppData) && !IsUnder(path, Path.Join(LocalAppData, "Packages"))
            && !IsUnder(path, Path.Join(LocalAppData, "Temp")) && !IsUnder(path, Path.Join(LocalAppData, "Microsoft"));
        var machineRoot = IsUnder(path, ProgramFiles) || IsUnder(path, ProgramFilesX86);
        if (!(entry.Scope == UninstallScope.DesktopUser ? userRoot : machineRoot)) return null;
        return Paths.Equals(path, Path.Join(LocalAppData, "Programs")) ? null : path;
    }

    public static bool IsSystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return IsSameOrUnder(path, Windows) || IsSameOrUnder(path, CommonAppData)
            || IsSharedProgramFolder(path, ProgramFiles) || IsSharedProgramFolder(path, ProgramFilesX86);
    }

    private static bool IsSharedProgramFolder(string path, string root) => root.Length > 0 &&
        (Paths.Equals(path, root) || IsSameOrUnder(path, Path.Join(root, "Common Files"))
         || IsSameOrUnder(path, Path.Join(root, "WindowsApps")) || IsSameOrUnder(path, Path.Join(root, "ModifiableWindowsApps")));

    internal static bool IsUnder(string path, string parent) => parent.Length != 0
        && path.StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSameOrUnder(string path, string parent) => Paths.Equals(path, parent) || IsUnder(path, parent);

    public static string? LocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.Any(char.IsControl)) return null;
        var p = path.Trim();
        // Reject NT/UNC/device paths, alternate streams, unresolved variables and ambiguous short names.
        if (p.Length < 3 || !char.IsAsciiLetter(p[0]) || p[1] != ':' || p[2] != '\\'
            || p.AsSpan(2).IndexOfAny(":%*?\"<>|/~") >= 0)
            return null;
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            return Paths.Equals(full, Path.TrimEndingDirectorySeparator(p)) ? full : null;
        }
        catch (Exception ex) when (ReadFailed(ex)) { return null; }
    }

    public static string? IconDirectory(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var value = displayIcon.Trim();
        var comma = value.LastIndexOf(',');
        if (comma >= 0 && int.TryParse(value.AsSpan(comma + 1), out _)) value = value[..comma].TrimEnd();
        return LocalPath(value.Trim('"')) is { } path ? Path.GetDirectoryName(path) : null;
    }

    public static FileStream? OpenRegularFile(string path, long maximumSize)
    {
        if (LocalPath(path) is null || !RegularDirectory(Path.GetDirectoryName(path))) return null;
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) return null;

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is > 0 && file.Length <= maximumSize) return file;
        file.Dispose();
        return null;
    }

    public static bool RegularDirectory(string? path)
    {
        if (path is null || LocalPath(path) is null || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed) return false;
        var directories = new Stack<string>();
        for (string? parent = path; parent is not null; parent = Path.GetDirectoryName(parent)) directories.Push(parent);
        foreach (var directory in directories)
        {
            var attributes = File.GetAttributes(directory);
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                return false;
        }
        return true;
    }

    public static RemovalExecutable? ReadExecutable(string path, CancellationToken ct)
    {
        try
        {
            using var file = OpenExecutable(path);
            return file is null ? null : ReadExecutable(path, file, StartupSignatures.ReadUncached, ct);
        }
        catch (Exception ex) when (ReadFailed(ex)) { return null; }
    }

    public static FileStream? OpenExecutable(string path)
    {
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || IsCommandHost(path)) return null;
        var file = OpenRegularFile(path, long.MaxValue);
        if (file is null || file.Length <= MaximumExecutableSize) return file;
        file.Dispose();
        throw new RemovalFileTooLargeException();
    }

    public static RemovalUninstaller? ReadUninstaller(string path, CancellationToken ct)
    {
        try
        {
            using var file = OpenExecutable(path);
            if (file is null) return null;
            ct.ThrowIfCancellationRequested();
            var (state, signer) = StartupSignatures.ReadUncached(path);
            return state == SignatureState.Valid && !string.IsNullOrWhiteSpace(signer)
                ? new RemovalUninstaller(signer, Hash(file, ct)) : null;
        }
        catch (Exception ex) when (ReadFailed(ex)) { return null; }
    }

    public static RemovalExecutable? ReadExecutable(string path, FileStream file,
        Func<string, (SignatureState? State, string? SignedBy)> readSignature, CancellationToken ct)
    {
        if (!IsGuiExecutable(file)) return null;
        ct.ThrowIfCancellationRequested();
        var (signature, signer) = readSignature(path);
        if (signature != SignatureState.Valid || string.IsNullOrWhiteSpace(signer)) return null;
        var version = FileVersionInfo.GetVersionInfo(path);
        if (string.IsNullOrWhiteSpace(version.CompanyName) || string.IsNullOrWhiteSpace(version.ProductName)) return null;
        return new RemovalExecutable(path, signer, version.CompanyName, version.ProductName, version.FileDescription,
            version.OriginalFilename, Hash(file, ct));
    }

    public static bool IsGuiExecutable(FileStream file)
    {
        using var pe = new PEReader(file, PEStreamOptions.LeaveOpen);
        return pe.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsGui
            && (pe.PEHeaders.CoffHeader.Characteristics & (Characteristics.Dll | Characteristics.System)) == 0;
    }

    public static string Hash(FileStream file, CancellationToken ct)
    {
        file.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var count = file.Read(buffer);
            if (count == 0) return Convert.ToHexString(hash.GetHashAndReset());
            hash.AppendData(buffer, 0, count);
        }
    }

    public static bool ReadFailed(Exception ex) => ex is IOException or UnauthorizedAccessException or SecurityException
        or Win32Exception or ArgumentException or NotSupportedException or BadImageFormatException or InvalidOperationException or InvalidDataException;

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelBoundary();

    [GeneratedRegex(@"\b(?:runtime|redist(?:ributable)?|framework|sdk|wdk|toolchain|build tools|developer tools|installer|setup|uninstall(?:er)?|updat(?:e|er)|driver|chipset|firmware|bios|uefi|codec|webview2|visual c\+\+|visual studio|windows subsystem|java(?: se)?|python|node\.?js|powershell|dotnet|antivirus|anti[- ]?(?:cheat|malware)|endpoint (?:security|protection)|defender|firewall|screen reader|accessibility|credential provider|device manager|control panel)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtectedRole();

    [GeneratedRegex(@"\b(?:backup|sync|synchroni[sz](?:ation|er)|cloud drive|file history|remote (?:desktop|access|support|administration)|vpn|password manager|administrat(?:ion|ive) tools?|server|agent|screen magnifier|on-screen keyboard|touchpad|hotkeys|storage manager|(?:disk|drive|volume) encryption|storage encryption)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveRole();

    [GeneratedRegex(@"\b(?:game (?:launcher|store|manager|library)|gaming services|steam|epic games launcher|battle\.net|ubisoft connect|uplay|riot client|ea app|gog galaxy|rockstar games launcher|itch\.io)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameLibraryRole();

    [GeneratedRegex(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();

    [GeneratedRegex(@"\A(?:inc|corp|corporation|co|company|ltd|limited|llc|llp|gmbh|plc|sarl|sas|ag|ab|oy|bv)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])(?:v?\d+(?:\.\d+)+|x64|x86|arm64|win64|win32)(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionOrArchitecture();
}
