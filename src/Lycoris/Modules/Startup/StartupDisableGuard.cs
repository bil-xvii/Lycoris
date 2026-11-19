using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Removal;

namespace Lycoris.Modules.Startup;

/// <summary>Checks a manually confirmed disable against the current registration and executable.</summary>
internal static class StartupDisableGuard
{
    internal static string Changed => Loc.T("Не удалось подтвердить текущую запись. Обновите список автозапуска и проверьте её перед отключением.");
    internal static string Protected => Loc.T("Запись нужна Windows, защите, играм, драйверу или обновлениям. Lycoris её не отключает.");

    internal static bool IsProtected(StartupEntry entry)
    {
        var packaged = StartupLocations.IsPackaged(entry.Source);
        var target = packaged ? System.IO.Path.GetFileName(entry.TargetPath) : entry.TargetPath;
        return !StartupClassifier.CanDisable(entry.Risk) ||
            PackagedRemovalInventory.IsGamingInfrastructureFamily(entry.PackageFamilyName) ||
            RemovalSoftwareEvidence.Role(target, entry.SignedBy, entry.Name, entry.Publisher, entry.Description) == RemovalSoftwareRole.Protected;
    }

    internal static bool IsProtected(LogonTask task) =>
        !StartupClassifier.CanDisable(task.Risk) || StartupClassifier.IsSystemTask(task.Path, task.Author) ||
        RemovalSoftwareEvidence.Role(task.TargetPath, null, task.Name, task.Author, task.Publisher, task.Description) == RemovalSoftwareRole.Protected;

    internal static string? EntryRefusal(StartupEntry expected, StartupEntry? live,
        Func<string, (SignatureState? State, string? SignedBy)> signature)
    {
        if (IsProtected(expected) || live is not null && IsProtected(live)) return Protected;
        if (live is null || !live.CanChange || !live.TargetExists || !live.Enabled || !expected.Enabled
            || !Same(live.Key, expected.Key) || live.PackageState != expected.PackageState
            || live.RegistrationKind != expected.RegistrationKind || live.Command != expected.Command
            || !Same(live.ItemPath, expected.ItemPath) || !Same(live.TargetPath, expected.TargetPath)
            || !Same(live.WorkingDirectory, expected.WorkingDirectory))
            return Changed;
        return TargetRefusal(live.TargetPath, StartupLocations.IsPackaged(live.Source), signature);
    }

    internal static string? TaskRefusal(LogonTask expected, LogonTask? live,
        Func<string, (SignatureState? State, string? SignedBy)> signature)
    {
        if (IsProtected(expected) || live is not null && IsProtected(live)) return Protected;
        if (live is null || !live.TargetExists || !live.Enabled || !expected.Enabled || live.ActionCount != 1
            || live.DefinitionHash is null || live.DefinitionHash != expected.DefinitionHash
            || !Same(live.Path, expected.Path) || !Same(live.TargetPath, expected.TargetPath) || live.Command != expected.Command)
            return Changed;
        return TargetRefusal(live.TargetPath, packaged: false, signature);
    }

    private static string? TargetRefusal(string? target, bool packaged,
        Func<string, (SignatureState? State, string? SignedBy)> signature)
    {
        if (RemovalSoftwareEvidence.LocalPath(target) is not { } path || !StartupSignatures.IsSignable(path))
            return Changed;
        var (state, signer) = signature(path);
        if (state is not (SignatureState.Valid or SignatureState.Unsigned) || state == SignatureState.Valid && string.IsNullOrWhiteSpace(signer))
            return Changed;
        var identityPath = packaged ? System.IO.Path.GetFileName(path) : path;
        return RemovalSoftwareEvidence.Role(identityPath, signer) == RemovalSoftwareRole.Protected ? Protected : null;
    }

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
