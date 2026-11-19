using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

/// <summary>Reads the identity and approval state of an exact Run value or Startup-folder file.</summary>
internal sealed class StartupDisableObservation(
    Func<string, (SignatureState? State, string? SignedBy)> readSignature,
    Func<StartupSource, string> folderPath)
{
    internal const string ExplorerPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    internal const string UserRunPolicy = "DisableCurrentUserRun";
    internal const string MachineRunPolicy = "DisableLocalMachineRun";

    internal sealed record Binding(RegistryValueKind? Kind, string Command, string? ItemPath, string? LinkHash, string? WorkingDirectory);
    internal sealed record Entry(StartupEntry Startup, Binding Source, string TargetHash, Approval Mark, uint? UserPolicy, uint? MachinePolicy);

    internal enum ApprovalState { Missing, Enabled, Disabled, Unknown }

    internal readonly record struct Approval(ApprovalState State, string? Data = null)
    {
        public bool IsEnabled => State is ApprovalState.Missing or ApprovalState.Enabled;
        public bool Matches(RegistryValueSnapshot snapshot) => State == ApprovalState.Missing
            ? !snapshot.ValueExisted
            : snapshot.ValueExisted && snapshot.Kind == RegistryValueKind.Binary && snapshot.Data == Data;
    }

    private static bool CanObserve(StartupEntry entry) =>
        (entry.Source is StartupSource.RunUser or StartupSource.RunMachine or StartupSource.Run32Machine
            or StartupSource.FolderUser or StartupSource.FolderCommon)
        && !string.IsNullOrWhiteSpace(entry.Name) && !entry.Name.Any(char.IsControl) && !entry.Name.Contains('\\')
        && !string.IsNullOrWhiteSpace(entry.TargetPath)
        && StartupClassifier.IsOptionalAtLogon(entry.TargetPath, entry.Command, StartupRisk.Normal);

    public Entry? Read(StartupEntry seed)
    {
        try
        {
            if (!CanObserve(seed)) return null;
            var binding = ReadBinding(seed);
            if (binding is null || !string.Equals(binding.Command.Trim(), seed.Command.Trim(), StringComparison.Ordinal))
                return null;

            var target = Target(binding);
            if (target is null || !string.Equals(target, Path.GetFullPath(seed.TargetPath!), StringComparison.OrdinalIgnoreCase))
                return null;

            using var file = OpenRegularFile(target);
            if (file is null) return null;
            var (signature, signer) = readSignature(target);
            var (publisher, _) = StartupScanner.ReadVersion(target);
            var mark = ReadApproval(seed);
            var entry = seed with
            {
                Command = binding.Command, TargetPath = target, TargetExists = true, Publisher = publisher,
                Signature = signature, SignedBy = signer, ItemPath = binding.ItemPath, Enabled = mark.IsEnabled,
                DisabledAtUtc = null, Risk = StartupClassifier.ClassifyEntry(target, publisher),
            };
            if (RemovalPolicy.Startup(entry, unique: true).Decision != RemovalDecision.DisableStartup)
                return null;

            var hash = Convert.ToHexString(SHA256.HashData(file));
            // Registration and policy can change while WinVerifyTrust and hashing are running.
            if (ReadBinding(seed) != binding || ReadApproval(seed) != mark) return null;

            // A disabled or unreadable legacy Run policy makes the live startup state ambiguous.
            var userPolicy = RegistryHelper.ReadDwordOrMissing(RegHive.HKCU, ExplorerPolicy, UserRunPolicy);
            var machinePolicy = RegistryHelper.ReadDwordOrMissing(RegHive.HKLM, ExplorerPolicy, MachineRunPolicy);
            if (userPolicy is > 0 || machinePolicy is > 0) return null;
            return new Entry(entry, binding, hash, mark, userPolicy, machinePolicy);
        }
        catch (Exception ex) when (ReadFailed(ex))
        {
            Log.Warn("Автозапуск: не удалось проверить {0}: {1}", seed.Key, ex.Message);
            return null;
        }
    }

    public static Approval ReadApproval(StartupEntry entry)
    {
        try
        {
            var value = ReadRegistry(StartupLocations.ApprovedHive(entry.Source), StartupLocations.ApprovedKey(entry.Source),
                entry.Name, StartupApproved.Length);
            if (value is null) return new Approval(ApprovalState.Missing);
            if (value.Value is not (RegistryValueKind.Binary, { Length: StartupApproved.Length } data)
                || data[1] != 0 || data[2] != 0 || data[3] != 0)
                return new Approval(ApprovalState.Unknown);

            var state = data[0] switch
            {
                StartupApproved.EnabledFlag when data.AsSpan(4).IndexOfAnyExcept((byte)0) == -1 => ApprovalState.Enabled,
                StartupApproved.DisabledFlag when StartupApproved.DisabledAtUtc(data) is not null => ApprovalState.Disabled,
                _ => ApprovalState.Unknown,
            };
            return new Approval(state, Convert.ToBase64String(data));
        }
        catch (Exception ex) when (ReadFailed(ex))
        {
            Log.Warn("Автозапуск: не удалось прочитать отметку {0}: {1}", entry.Key, ex.Message);
            return new Approval(ApprovalState.Unknown);
        }
    }

    private Binding? ReadBinding(StartupEntry entry) =>
        StartupLocations.IsFolder(entry.Source) ? ReadFolder(entry) : ReadRun(entry);

    private static Binding? ReadRun(StartupEntry entry)
    {
        var (hive, path) = entry.Source switch
        {
            StartupSource.RunUser => (RegHive.HKCU, StartupLocations.RunKey),
            StartupSource.RunMachine => (RegHive.HKLM, StartupLocations.RunKeyMachine),
            StartupSource.Run32Machine => (RegHive.HKLM, StartupLocations.Run32Key),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
        // Bound the native read. Overlong or malformed commands are not candidates.
        var value = ReadRegistry(hive, path, entry.Name, 32 * 1024);
        if (value is not ({ } kind, { Length: >= 4 } bytes)
            || kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || bytes.Length % 2 != 0)
            return null;
        var text = Encoding.Unicode.GetString(bytes);
        if (text[^1] != '\0') return null;
        var command = text[..^1];
        return command.Any(char.IsControl) ? null : new Binding(kind, command, null, null, null);
    }

    private Binding? ReadFolder(StartupEntry entry)
    {
        var directory = folderPath(entry.Source);
        if (!LocalDrivePath(directory) || entry.ItemPath is null) return null;
        var matches = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false,
        }).Where(p => string.Equals(Path.GetFileName(p), entry.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var item = Path.GetFullPath(matches[0]);
        if (!string.Equals(item, Path.GetFullPath(entry.ItemPath), StringComparison.OrdinalIgnoreCase)) return null;

        using var file = OpenRegularFile(item);
        if (file is null) return null;
        if (!Path.GetExtension(item).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return new Binding(null, item, item, null, null);

        var link = ShellLink.Read(item);
        if (link?.TargetPath is not { } target || target.Any(char.IsControl) || link.Arguments.Any(char.IsControl)) return null;
        return new Binding(null, CommandLineParser.Join(target, link.Arguments), item,
            Convert.ToHexString(SHA256.HashData(file)), link.WorkingDirectory);
    }

    private static string? Target(Binding binding)
    {
        if (binding is { ItemPath: { } item, LinkHash: null }) return item;
        var command = binding.Kind == RegistryValueKind.ExpandString
            ? Environment.ExpandEnvironmentVariables(binding.Command) : binding.Command;
        if (command.Contains('%')) return null;
        var parsed = CommandLineParser.Parse(command, fileExists: static path => LocalDrivePath(path) && FileProbe.FileExists(path),
            expand: static s => s, searchDirs: []);
        return parsed is { ViaHost: false } && LocalDrivePath(parsed.Target) ? Path.GetFullPath(parsed.Target) : null;
    }

    private static bool LocalDrivePath(string path) =>
        Path.IsPathFullyQualified(path) && Path.GetPathRoot(path)?.Length == 3 && !path.AsSpan(2).Contains(':');

    private static FileStream? OpenRegularFile(string path)
    {
        if (!LocalDrivePath(path) || Path.GetExtension(path).Length == 0)
            return null;
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
            return null;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return null;
        }
        // Prevent changing or replacing the file while its signature and contents are read.
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static bool ReadFailed(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or Win32Exception or ArgumentException or NotSupportedException;

    private static (RegistryValueKind Kind, byte[] Data)? ReadRegistry(RegHive hive, string path, string name, int maxSize)
    {
        if (RegistryHelper.Redirect is { } redirect) (hive, path) = redirect(hive, path);
        var registryHive = hive == RegHive.HKCU ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
        var buffer = new byte[maxSize];
        uint size = (uint)buffer.Length;
        // Read the type and data together. Only ERROR_FILE_NOT_FOUND denotes an absent value or key.
        const uint flags = 0x0000FFFF | 0x00010000 | 0x10000000; // RRF_RT_ANY | RRF_SUBKEY_WOW6464KEY | RRF_NOEXPAND
        var error = RegGetValueW((nint)(int)registryHive, path, name, flags, out var kind, buffer, ref size);
        if (error == 2) return null;
        if (error != 0) throw new Win32Exception(error);
        return ((RegistryValueKind)kind, buffer.AsSpan(0, (int)size).ToArray());
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(nint key, string subKey, string name, uint flags, out uint type,
        [Out] byte[] data, ref uint size);
}
