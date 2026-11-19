using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

/// <summary>Reads the identity and approval state of an exact Run value or Startup-folder file.</summary>
internal sealed class StartupDisableObservation(
    Func<string, (SignatureState? State, string? SignedBy)> readSignature,
    Func<StartupSource, string> folderPath,
    Func<CancellationToken, RemovalSoftwareSnapshot>? capture = null,
    Func<string, FileStream, CancellationToken, RemovalExecutable?>? executableReader = null,
    Func<StartupEntry, CancellationToken, RemovalPackagedStartup.Assessment>? packageReader = null)
{
    internal const string ExplorerPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    internal const string UserRunPolicy = "DisableCurrentUserRun";
    internal const string MachineRunPolicy = "DisableLocalMachineRun";

    internal sealed record Binding(RegistryValueKind? Kind, string Command, string? ItemPath, string? LinkHash, string? WorkingDirectory);
    internal abstract record Entry(StartupEntry Startup);
    internal sealed record ClassicEntry(StartupEntry Startup, Binding Source, RemovalExecutable File, RemovalLaunchBinding ManualLaunch,
        Approval Mark, uint? UserPolicy, uint? MachinePolicy) : Entry(Startup);
    internal sealed record PackagedEntry(StartupEntry Startup, RemovalPackagedStartup.Proof Package, int State) : Entry(Startup);
    internal sealed record Assessment(RemovalRow Row, Entry? Proof = null);

    public RemovalSoftwareSnapshot Capture(CancellationToken ct) => (capture ?? RemovalSoftwareWindows.CaptureStartup)(ct);

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
        && !string.IsNullOrWhiteSpace(entry.TargetPath);

    private static readonly HashSet<string> SimpleLogonArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "-silent", "--silent", "-startup", "--startup", "--autostart",
        "-minimized", "--minimized", "--start-minimized",
    };

    public Assessment Read(StartupEntry seed, RemovalSoftwareSnapshot facts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var basic = RemovalPolicy.Startup(seed, unique: true);
        if (basic.Decision is RemovalDecision.Keep or RemovalDecision.AlreadyDisabled) return new(basic);
        if (StartupLocations.IsPackaged(seed.Source)) return ReadPackaged(seed, facts, basic, ct);
        try
        {
            if (!CanObserve(seed)) return new(basic);
            var binding = ReadBinding(seed, ct);
            var target = binding is null ? null : Target(binding);
            if (binding is null || !string.Equals(binding.Command.Trim(), seed.Command.Trim(), StringComparison.Ordinal)
                || target is null || !RemovalSoftwareEvidence.Paths.Equals(target, RemovalSoftwareEvidence.LocalPath(seed.TargetPath)))
                return new(basic);
            if (ProtectedBinding(facts, target)) return Keep(basic);
            if (!facts.StartupComplete) return new(basic with
            {
                Reason = Loc.T("Не удалось полностью проверить точки запуска, службы, процессы или системные привязки."),
            });

            var launches = facts.LaunchesFor(target);
            if (launches.Count == 0) return new(basic with
            {
                Reason = Loc.T("Не подтверждена однозначная точка ручного запуска программы."),
            });
            using var file = RemovalSoftwareEvidence.OpenExecutable(target);
            if (file is null) return new(basic);
            var software = executableReader is not null ? executableReader(target, file, ct)
                : RemovalSoftwareEvidence.ReadExecutable(target, file, readSignature, ct);
            if (software is null || !RemovalSoftwareEvidence.Paths.Equals(software.Path, target))
                return new(basic);

            var role = RemovalSoftwareEvidence.Role(target, software.Signer, software.Company, software.Product,
                software.Description, software.OriginalName, seed.Name);
            if (role == RemovalSoftwareRole.Protected) return Keep(basic);
            if (role == RemovalSoftwareRole.Sensitive) return new(basic with { Reason = RemovalPolicy.SensitiveSoftware });
            if (!RemovalSoftwareEvidence.PublishersMatch(software.Company, software.Signer))
                return new(basic);
            var manual = launches.Where(launch => IsManualLaunch(launch, software, seed))
                .OrderBy(launch => launch.Identity, RemovalSoftwareEvidence.Paths).FirstOrDefault();
            if (manual is null) return new(basic);

            var mark = ReadApproval(seed);
            if (mark.State == ApprovalState.Unknown) return new(basic);
            var entry = seed with
            {
                Command = binding.Command, TargetPath = target, TargetExists = true, Publisher = software.Company,
                Signature = SignatureState.Valid, SignedBy = software.Signer, Description = software.Description,
                ItemPath = binding.ItemPath, Enabled = mark.IsEnabled, DisabledAtUtc = null, Risk = StartupRisk.Normal,
            };
            if (!mark.IsEnabled) return new(RemovalPolicy.Startup(entry, unique: true));

            // Registration and policy can change while WinVerifyTrust and hashing are running.
            if (ReadBinding(seed, ct) != binding || ReadApproval(seed) != mark) return new(basic);
            var userPolicy = RegistryHelper.ReadDwordOrMissing(RegHive.HKCU, ExplorerPolicy, UserRunPolicy);
            var machinePolicy = RegistryHelper.ReadDwordOrMissing(RegHive.HKLM, ExplorerPolicy, MachineRunPolicy);
            if (userPolicy is > 0 || machinePolicy is > 0) return new(basic);
            var row = RemovalPolicy.Startup(entry, unique: true, confirmed: true);
            return row.Decision == RemovalDecision.DisableStartup
                ? new(row, new ClassicEntry(entry, binding, software, manual, mark, userPolicy, machinePolicy)) : new(row);
        }
        catch (RemovalFileTooLargeException)
        {
            return new(basic with { Reason = Loc.T("Файл запуска или удаления превышает предел проверки в 2 ГБ.") });
        }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex))
        {
            Log.Warn("Автозапуск: не удалось проверить запись ({0}).", ex.GetType().Name);
            return new(basic);
        }
    }

    private Assessment ReadPackaged(StartupEntry seed, RemovalSoftwareSnapshot facts, RemovalRow basic, CancellationToken ct)
    {
        if (seed.PackageFamilyName is not { } family || !PackagedStartup.IsJournalSafe(family, seed.Name)) return new(basic);
        try
        {
            var state = ReadPackageState(seed);
            var entry = seed with { PackageState = state, Enabled = PackagedStartup.IsEnabled(state) };
            if (state != (int)PackagedTaskState.Enabled) return new(RemovalPolicy.Startup(entry, unique: true));
            var result = (packageReader ?? RemovalPackagedStartup.Read)(entry, ct);
            if (result.Protected) return Keep(basic);
            if (result.Package is not { } proof || ReadPackageState(entry) != state)
                return new(basic with { Reason = RemovalPackagedStartup.Unverified });
            if (ProtectedBinding(facts, proof.Target) || facts.DefaultBrowserFamilies.Contains(family)) return Keep(basic);
            if (!facts.IntegrationsComplete || !facts.ServicesComplete)
                return new(basic with { Reason = RemovalPackagedStartup.Unverified });
            var row = RemovalPolicy.Startup(entry, unique: true, confirmed: true);
            return row.Decision == RemovalDecision.DisableStartup
                ? new(row, new PackagedEntry(entry, proof, state.GetValueOrDefault())) : new(row);
        }
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex))
        {
            return new(basic with { Reason = RemovalPackagedStartup.Unverified });
        }
    }

    public static int? ReadPackageState(StartupEntry entry)
    {
        if (!PackagedStartup.IsJournalSafe(entry.PackageFamilyName, entry.Name)) return null;
        var value = ReadRegistry(RegHive.HKCU, PackagedStartup.KeyPath(entry.PackageFamilyName!, entry.Name),
            PackagedStartup.StateValue, sizeof(uint));
        return value is (RegistryValueKind.DWord, { Length: sizeof(uint) } data) ? BitConverter.ToInt32(data) : null;
    }

    private static bool ProtectedBinding(RemovalSoftwareSnapshot facts, string path) =>
        facts.Services.ContainsExact(path) || facts.DefaultBrowser.ContainsExact(path) || facts.Consumers.ContainsExact(path);

    private static Assessment Keep(RemovalRow row) => new(row with
    {
        Decision = RemovalDecision.Keep, Reason = RemovalPolicy.StartupProtected,
    });

    private static bool IsManualLaunch(RemovalLaunchBinding launch, RemovalExecutable software, StartupEntry seed)
    {
        if (StartupLocations.IsMachine(seed.Source) && launch.UserScope) return false;
        var purpose = StartupLocations.IsFolder(seed.Source) ? Path.GetFileNameWithoutExtension(seed.Name) : seed.Name;
        var executable = Path.GetFileNameWithoutExtension(software.Path);
        if (!RemovalSoftwareEvidence.ProductMatches(purpose, software.Product, software.Description, executable)) return false;
        return launch.AppPath || RemovalSoftwareEvidence.ProductMatches(launch.Label, software.Product, software.Description, executable);
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
        catch (Exception ex) when (RemovalSoftwareEvidence.ReadFailed(ex))
        {
            Log.Warn("Автозапуск: не удалось прочитать отметку {0}: {1}", entry.Key, ex.Message);
            return new Approval(ApprovalState.Unknown);
        }
    }

    private Binding? ReadBinding(StartupEntry entry, CancellationToken ct) =>
        StartupLocations.IsFolder(entry.Source) ? ReadFolder(entry, ct) : ReadRun(entry);

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

    private Binding? ReadFolder(StartupEntry entry, CancellationToken ct)
    {
        var directory = folderPath(entry.Source);
        if (!RemovalSoftwareEvidence.RegularDirectory(directory) || entry.ItemPath is null) return null;
        var matches = Directory.EnumerateFiles(directory, "*", new EnumerationOptions
        {
            AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false,
        }).Where(p => string.Equals(Path.GetFileName(p), entry.Name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var item = Path.GetFullPath(matches[0]);
        if (!string.Equals(item, Path.GetFullPath(entry.ItemPath), StringComparison.OrdinalIgnoreCase)) return null;

        using var file = RemovalSoftwareEvidence.OpenRegularFile(item, 256L * 1024 * 1024);
        if (file is null) return null;
        if (!Path.GetExtension(item).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return new Binding(null, item, item, null, null);

        var link = ShellLink.Read(item);
        if (link?.TargetPath is not { } target || target.Any(char.IsControl) || link.Arguments.Any(char.IsControl)
            || file.Length > 1024 * 1024) return null;
        return new Binding(null, CommandLineParser.Join(target, link.Arguments), item,
            RemovalSoftwareEvidence.Hash(file, ct), link.WorkingDirectory);
    }

    private static string? Target(Binding binding)
    {
        if (binding is { ItemPath: { } item, LinkHash: null }) return RemovalSoftwareEvidence.LocalPath(item);
        var command = (binding.Kind == RegistryValueKind.ExpandString
            ? Environment.ExpandEnvironmentVariables(binding.Command) : binding.Command).Trim();
        if (command.Contains('%') || command.StartsWith('"') && command.IndexOf('"', 1) < 0) return null;
        var (declared, _) = CommandLineParser.SplitProgram(command, static _ => false, []);
        if (!Path.GetExtension(declared).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var parsed = CommandLineParser.Parse(command, fileExists: static _ => false,
            expand: static value => value, searchDirs: []);
        if (parsed is not { ViaHost: false } || RemovalSoftwareEvidence.LocalPath(parsed.Target) is not { } target
            || RemovalSoftwareEvidence.IsCommandHost(target) || !SimpleLogonArguments.Contains(parsed.Arguments)
            || !command.StartsWith('"') && target.Any(char.IsWhiteSpace)) return null;
        if (!string.IsNullOrWhiteSpace(binding.WorkingDirectory)
            && !RemovalSoftwareEvidence.Paths.Equals(RemovalSoftwareEvidence.LocalPath(binding.WorkingDirectory), Path.GetDirectoryName(target)))
            return null;
        return target;
    }
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
