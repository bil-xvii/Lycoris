using System.IO;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

internal abstract record ProgramRemovalProof;

internal sealed record ProgramRemovalAssessment(UninstallRegistration Registration, RemovalRow Row, ProgramRemovalProof? Proof);

internal interface IProgramRemovalInspector
{
    Task<IReadOnlyList<ProgramRemovalAssessment>> InspectAsync(UninstallInventory inventory, CancellationToken ct);
    bool Revalidate(UninstallRegistration registration, ProgramRemovalProof proof, CancellationToken ct);
}

internal sealed class ProgramRemovalInspector : IProgramRemovalInspector
{
    private readonly Func<CancellationToken, RemovalSoftwareSnapshot> _capture;
    private readonly Func<string, CancellationToken, RemovalExecutable?> _readExecutable;
    private readonly Func<UninstallInventory> _readInventory;
    private readonly Func<string, CancellationToken, RemovalUninstaller?> _readUninstaller;

    public ProgramRemovalInspector()
        : this(RemovalSoftwareWindows.Capture, RemovalSoftwareEvidence.ReadExecutable, UninstallEntries.ReadInventory)
    {
    }

    internal ProgramRemovalInspector(Func<CancellationToken, RemovalSoftwareSnapshot> capture,
        Func<string, CancellationToken, RemovalExecutable?> readExecutable, Func<UninstallInventory> readInventory,
        Func<string, CancellationToken, RemovalUninstaller?>? readUninstaller = null)
    {
        _capture = capture;
        _readExecutable = readExecutable;
        _readInventory = readInventory;

        _readUninstaller = readUninstaller ?? RemovalSoftwareEvidence.ReadUninstaller;
    }

    private sealed record Proof(ProgramRemovalInspector Owner, UninstallRegistration Registration,
        string Root, RemovalLaunchBinding Binding, RemovalExecutable File, RemovalUninstaller? Uninstaller) : ProgramRemovalProof;

    public Task<IReadOnlyList<ProgramRemovalAssessment>> InspectAsync(UninstallInventory inventory, CancellationToken ct) =>
        StaWorker.Run<IReadOnlyList<ProgramRemovalAssessment>>(() => Inspect(inventory, ct), "Lycoris program evidence");

    private IReadOnlyList<ProgramRemovalAssessment> Inspect(UninstallInventory inventory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var groups = RemovalPolicy.ProgramRows(inventory, current: true);
        if (!groups.Any(group => group.CanInspect)) return groups.Select(WithoutProof).ToArray();

        var facts = _capture(ct);
        var roots = new RemovalInstallRoots(inventory);
        var files = new Dictionary<string, RemovalExecutable?>(RemovalSoftwareEvidence.Paths);
        RemovalExecutable? ReadOnce(string path, CancellationToken token)
        {
            if (!files.TryGetValue(path, out var file))
            {
                file = _readExecutable(path, token);
                files.Add(path, file);
            }
            return file;
        }

        return groups.Select(group => Assess(group, facts, roots, ReadOnce, ct)).ToArray();
    }

    public bool Revalidate(UninstallRegistration registration, ProgramRemovalProof proof, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (proof is not Proof expected || !ReferenceEquals(expected.Owner, this) || expected.Registration != registration)
            return false;

        // Other registrations may now share or consume this root. Refresh the bounded read-only inventory too.
        return StaWorker.Run(() =>
        {
            var current = _readInventory();
            var target = RemovalPolicy.ProgramRows(current, current: true).SingleOrDefault(group =>
                RemovalSoftwareEvidence.Paths.Equals(group.Row.Key, RemovalPolicy.RegistrationKey(registration)));
            return target is { CanInspect: true } && target.Registration == registration
                && Assess(target, _capture(ct), new RemovalInstallRoots(current), _readExecutable, ct).Proof == expected;
        }, "Lycoris program evidence recheck").GetAwaiter().GetResult();
    }

    private ProgramRemovalAssessment Assess(RemovalPolicy.ProgramRow seed, RemovalSoftwareSnapshot facts, RemovalInstallRoots roots,
        Func<string, CancellationToken, RemovalExecutable?> readExecutable, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!seed.CanInspect) return WithoutProof(seed);

        ProgramRemovalAssessment Review(string reason) => new(seed.Registration, seed.Row with { Reason = reason }, null);
        ProgramRemovalAssessment Keep(string reason) => new(seed.Registration,
            seed.Row with { Decision = RemovalDecision.Keep, Reason = reason }, null);

        try { return WithEvidence(seed.Registration); }
        catch (RemovalFileTooLargeException)
        {
            return Review(Loc.T("Файл запуска или удаления превышает предел проверки в 2 ГБ."));
        }

        ProgramRemovalAssessment WithEvidence(UninstallRegistration entry)
        {
            if (RemovalSoftwareEvidence.DedicatedRoot(entry) is not { } root)
                return Review(Loc.T("Не подтверждена отдельная локальная папка установки программы."));
            if (facts.Services.ContainsUnder(root))
                return Keep(Loc.T("С папкой программы связана зарегистрированная служба или драйвер Windows."));
            if (facts.DefaultBrowser.ContainsUnder(root))
                return Keep(Loc.T("Программа связана с обработчиком веб-ссылок Windows по умолчанию."));
            if (facts.Consumers.Overlaps(root) || roots.Shared(root, entry))
                return Review(Loc.T("Папка установки пересекается с другой программой или системной привязкой."));
            if (facts.Running.ContainsUnder(root))
                return Review(Loc.T("Из папки установки сейчас работает процесс. Закройте программу и повторите проверку."));
            if (!facts.Complete)
                return Review(Loc.T("Не удалось полностью проверить точки запуска, службы, процессы или системные привязки."));

            var entries = facts.LaunchesUnder(root).Where(binding => BindingMatches(binding, entry))
                .GroupBy(binding => binding.Target, RemovalSoftwareEvidence.Paths).Take(9).ToArray();
            if (entries.Length is 0 or > 8)
                return Review(Loc.T("Не подтверждена однозначная точка ручного запуска программы."));
            RemovalLaunchBinding? accepted = null;
            RemovalExecutable? acceptedFile = null;
            var unreadable = false;
            foreach (var group in entries)
            {
                ct.ThrowIfCancellationRequested();
                var file = readExecutable(group.Key, ct);
                if (file is null || !RemovalSoftwareEvidence.Paths.Equals(file.Path, group.Key))
                {
                    unreadable = true;
                    continue;
                }
                var role = RemovalSoftwareEvidence.Role(file.Path, file.Signer, file.Company, file.Product, file.Description, file.OriginalName);
                if (role == RemovalSoftwareRole.Protected) return Keep(RemovalPolicy.ProtectedSoftware);
                if (role == RemovalSoftwareRole.Sensitive) return Review(RemovalPolicy.SensitiveSoftware);
                if (role == RemovalSoftwareRole.GameLibrary) return Review(RemovalPolicy.GameLibrarySoftware);
                if (!RemovalSoftwareEvidence.PublishersMatch(entry.Publisher, file.Signer)
                    || !RemovalSoftwareEvidence.PublishersMatch(file.Company, file.Signer)
                    || !RemovalSoftwareEvidence.ProductMatches(entry.DisplayName, file.Product, file.Description))
                    continue;

                if (accepted is not null)
                    return Review(Loc.T("Не подтверждена однозначная точка ручного запуска программы."));
                accepted = group.OrderBy(binding => binding.Identity, RemovalSoftwareEvidence.Paths).First();
                acceptedFile = file;
            }

            if (accepted is null || acceptedFile is null || unreadable)
                return Review(Loc.T("Не подтверждены графический файл запуска, его подпись или связь с издателем записи."));
            var rootLabel = Path.GetFileName(root);
            if (!RemovalSoftwareEvidence.ProductMatches(rootLabel, entry.DisplayName, acceptedFile.Product,
                    Path.GetFileNameWithoutExtension(acceptedFile.Path)))
                return Review(Loc.T("Не подтверждена отдельная локальная папка установки программы."));
            var uninstaller = ReadUninstaller(entry, root, acceptedFile, ct);
            if (!uninstaller.Verified)
                return Review(Loc.T("Не подтверждён штатный деинсталлятор программы и его издатель."));
            return new ProgramRemovalAssessment(entry, seed.Row with
            {
                Decision = RemovalDecision.ConsiderRemoval,
                Reason = Loc.T("Подтверждены отдельная папка установки и подписанный файл ручного запуска. Проверьте, нужна ли вам программа и сохранены ли её данные."),
            }, new Proof(this, entry, root, accepted, acceptedFile, uninstaller.File));
        }
    }

    private (bool Verified, RemovalUninstaller? File) ReadUninstaller(UninstallRegistration entry, string root,
        RemovalExecutable executable, CancellationToken ct)
    {
        if (entry.WindowsInstaller == true)
            return (entry.IsStandardMsiHook && Guid.TryParseExact(entry.KeyName, "B", out _), null);
        if (entry.WindowsInstaller != false || entry.IsStandardMsiHook
            || RemovalSoftwareEvidence.LocalPath(entry.UninstallExecutable) is not { } path
            || !RemovalSoftwareEvidence.IsUnder(path, root))
            return (false, null);

        var file = RemovalSoftwareEvidence.Paths.Equals(path, executable.Path)
            ? new RemovalUninstaller(executable.Signer, executable.Hash)
            : _readUninstaller(path, ct);
        return (file is not null && RemovalSoftwareEvidence.PublishersMatch(file.Signer, executable.Signer), file);
    }

    private static bool BindingMatches(RemovalLaunchBinding binding, UninstallRegistration registration)
    {
        if (binding.ProductCode is { } product)
            return registration.WindowsInstaller == true && Guid.TryParse(registration.KeyName, out var code) && code == product
                && binding.UserScope == (registration.Scope == UninstallScope.DesktopUser);

        // A machine-wide App Paths entry must not be used to substantiate a per-user registration, or the reverse.
        if (binding.AppPath && binding.UserScope != (registration.Scope == UninstallScope.DesktopUser)) return false;
        return binding.AppPath || RemovalSoftwareEvidence.ProductMatches(registration.DisplayName, binding.Label);
    }

    private static ProgramRemovalAssessment WithoutProof(RemovalPolicy.ProgramRow seed) => new(seed.Registration, seed.Row, null);
}
