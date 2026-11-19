using System.IO;
using Lycoris.Core.Security;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Startup;

namespace Lycoris.Modules.Removal;

public enum RemovalDecision { ConsiderRemoval, DisableStartup, Keep, Review, AlreadyDisabled }

public sealed record RemovalRow(string Key, string Name, string? Publisher, string? Version, string Source,
    RemovalDecision Decision, string Reason)
{
    public string DecisionText => RemovalPolicy.DecisionText(Decision);
    public string Metadata => string.Join(" · ", new[] { Publisher, Version, Source }.Where(v => !string.IsNullOrWhiteSpace(v)));
}

internal static class RemovalPolicy
{
    internal sealed record ProgramRow(UninstallRegistration Registration, RemovalRow Row, bool CanInspect);

    internal static string ProtectedSoftware =>
        Loc.T("Признаки системного компонента, среды выполнения, драйвера, защиты или средства обслуживания.");
    internal static string SensitiveSoftware =>
        Loc.T("Программа может обеспечивать резервное копирование, синхронизацию, доступ, управление или другие важные функции.");

    internal static string GameLibrarySoftware =>
        Loc.T("Удаление игрового лаунчера может затронуть библиотеки игр и сохранения.");

    public static IReadOnlyList<RemovalRow> Programs(UninstallInventory inventory, bool current) =>
        ProgramRows(inventory, current).Select(entry => entry.Row).ToArray();

    internal static IReadOnlyList<ProgramRow> ProgramRows(UninstallInventory inventory, bool current)
    {
        var registrations = inventory.Entries.Where(entry => entry.HasDisplayIdentity)
            .GroupBy(RegistrationKey, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicateLabels = registrations.SelectMany(group => group).GroupBy(DisplayIdentity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var duplicateKeys = inventory.Entries.GroupBy(entry => entry.KeyName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trustedRead = current && inventory.State == UninstallReadState.Complete;
        return registrations.Select(group =>
            {
                var assessed = group.Select(entry => PreliminaryProgram(entry, trustedRead,
                    group.Skip(1).Any() || duplicateLabels.Contains(DisplayIdentity(entry)) || duplicateKeys.Contains(entry.KeyName)));
                return assessed.FirstOrDefault(entry => entry.Row.Decision == RemovalDecision.Keep) ?? assessed.First();
            })
            .OrderBy(entry => entry.Row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Row.Key, StringComparer.Ordinal).ToArray();
    }

    internal static string RegistrationKey(UninstallRegistration entry) => $"{entry.Scope}:{entry.KeyName}";
    private static string DisplayIdentity(UninstallRegistration entry) => $"{entry.DisplayName}\u001f{entry.Publisher}\u001f{entry.Version}";

    internal static RemovalRow Program(UninstallRegistration entry, bool current, bool duplicate = false) =>
        PreliminaryProgram(entry, current, duplicate).Row;

    private static ProgramRow PreliminaryProgram(UninstallRegistration entry, bool current, bool duplicate)
    {
        var (decision, reason, inspect) = AssessProgram(entry, current, duplicate);
        var source = entry.Scope switch
        {
            UninstallScope.Machine64 => Loc.T("Реестр · все пользователи (64 бита)"),
            UninstallScope.Machine32 => Loc.T("Реестр · все пользователи (32 бита)"),
            _ => Loc.T("Реестр · пользователь рабочего стола"),
        };
        return new ProgramRow(entry, new RemovalRow(RegistrationKey(entry), entry.DisplayName, entry.Publisher, entry.Version,
            source, decision, reason), inspect);
    }

    private static (RemovalDecision Decision, string Reason, bool Inspect) AssessProgram(
        UninstallRegistration entry, bool current, bool duplicate)
    {
        if (entry.SystemComponent == true || entry.ParentOrUpdate)
            return (RemovalDecision.Keep, Loc.T("Установщик зарегистрировал системный или связанный компонент либо обновление."), false);
        if (entry.NoRemove == true)
            return (RemovalDecision.Keep, Loc.T("В записи установщика отключено удаление через список программ Windows."), false);
        var role = RemovalSoftwareEvidence.Role(entry.InstallLocation, null, entry.DisplayName, entry.Publisher);
        if (role == RemovalSoftwareRole.Protected)
            return (RemovalDecision.Keep, ProtectedSoftware, false);
        if (role == RemovalSoftwareRole.GameLibrary)
            return (RemovalDecision.Review, GameLibrarySoftware, false);
        if (role == RemovalSoftwareRole.Sensitive)
            return (RemovalDecision.Review, SensitiveSoftware, false);
        if (!current)
            return (RemovalDecision.Review, Loc.T("Для рекомендации нужен полный свежий список программ."), false);
        if (duplicate)
            return (RemovalDecision.Review, Loc.T("Одинаковые данные в нескольких записях мешают однозначно найти программу в Windows."), false);
        if (entry.NoRemove is null || entry.SystemComponent is null || string.IsNullOrWhiteSpace(entry.Version)
            || string.IsNullOrWhiteSpace(entry.Publisher) || !entry.HasUninstallRegistration || string.IsNullOrEmpty(entry.UninstallFingerprint))
            return (RemovalDecision.Review, Loc.T("Не хватает проверяемых сведений об издателе, версии или регистрации удаления."), false);
        return (RemovalDecision.Review, Loc.T("Запись есть в реестре, но назначение программы и её зависимости не подтверждены."), true);
    }

    public static RemovalRow Startup(StartupEntry entry, bool unique, bool confirmed = false)
    {
        var (decision, reason) = AssessStartup(entry, unique, confirmed);
        return new RemovalRow(entry.Key, UninstallEntries.InventoryText(entry.DisplayName) ?? "—",
            UninstallEntries.InventoryText(entry.SignedBy ?? entry.Publisher), null, StartupLocations.ShortLabel(entry.Source), decision, reason);
    }

    private static (RemovalDecision Decision, string Reason) AssessStartup(StartupEntry entry, bool unique, bool confirmed)
    {
        if (!StartupClassifier.CanDisable(entry.Risk) || entry.Risk is StartupRisk.Microsoft or StartupRisk.Driver)
            return (RemovalDecision.Keep, StartupProtected);
        if (StartupLocations.IsPackaged(entry.Source) && PackagedStartup.IsPolicy(entry.PackageState))
            return (RemovalDecision.Keep, Loc.T("Этой записью автозапуска управляет политика Windows."));
        var target = StartupLocations.IsPackaged(entry.Source) ? Path.GetFileName(entry.TargetPath) : entry.TargetPath;
        var role = RemovalSoftwareEvidence.Role(target,
            entry.Signature == SignatureState.Valid ? entry.SignedBy : null, entry.Name, entry.Publisher, entry.Description);
        if (role == RemovalSoftwareRole.Protected)
            return (RemovalDecision.Keep, StartupProtected);
        if (!unique || !entry.CanChange)
            return (RemovalDecision.Review, Loc.T("Не подтверждены однозначная запись и возможность её изменить."));
        if (!entry.Enabled)
            return (RemovalDecision.AlreadyDisabled, Loc.T("Эта запись автозапуска уже выключена."));
        if (role == RemovalSoftwareRole.Sensitive)
            return (RemovalDecision.Review, SensitiveSoftware);
        if (!confirmed || !entry.TargetExists)
            return (RemovalDecision.Review, Loc.T("Не подтверждено, что эту запись можно отключить без потери нужной фоновой функции."));
        return (RemovalDecision.DisableStartup, Loc.T("Программу можно открыть вручную. После отключения уведомления и фоновая работа могут начаться только после открытия."));
    }

    internal static string StartupProtected => Loc.T("Запись связана с Windows, драйвером, защитой или обновлениями. Оставьте её включённой, если она включена.");

    public static string DecisionText(RemovalDecision decision) => decision switch
    {
        RemovalDecision.ConsiderRemoval => Loc.T("МОЖНО РАССМОТРЕТЬ УДАЛЕНИЕ"),
        RemovalDecision.DisableStartup => Loc.T("МОЖНО ОТКЛЮЧИТЬ АВТОЗАПУСК"),
        RemovalDecision.Keep => Loc.T("ОСТАВИТЬ"),
        RemovalDecision.AlreadyDisabled => Loc.T("УЖЕ ОТКЛЮЧЕНО"),
        _ => Loc.T("НУЖНО ПРОВЕРИТЬ"),
    };
}
