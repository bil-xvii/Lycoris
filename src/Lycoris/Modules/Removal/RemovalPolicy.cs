using System.IO;
using System.Text.RegularExpressions;
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

internal static partial class RemovalPolicy
{
    public static IReadOnlyList<RemovalRow> Programs(UninstallInventory inventory, bool current)
    {
        var registrations = inventory.Entries.GroupBy(RegistrationKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Entry: group.First(), Duplicate: group.Skip(1).Any())).ToList();
        var duplicateLabels = registrations.GroupBy(row => DisplayIdentity(row.Entry), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trustedRead = current && inventory.State == UninstallReadState.Complete;
        return registrations.Select(row => Program(row.Entry, trustedRead, row.Duplicate || duplicateLabels.Contains(DisplayIdentity(row.Entry))))
            .OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(row => row.Key, StringComparer.Ordinal).ToList();
    }

    private static string RegistrationKey(UninstallRegistration entry) => $"{entry.Scope}:{entry.KeyName}";
    private static string DisplayIdentity(UninstallRegistration entry) => $"{entry.DisplayName}\u001f{entry.Publisher}\u001f{entry.Version}";

    internal static RemovalRow Program(UninstallRegistration entry, bool current, bool duplicate = false)
    {
        var (decision, reason) = AssessProgram(entry, current, duplicate);
        var source = entry.Scope switch
        {
            UninstallScope.Machine64 => Loc.T("Реестр · все пользователи (64 бита)"),
            UninstallScope.Machine32 => Loc.T("Реестр · все пользователи (32 бита)"),
            _ => Loc.T("Реестр · пользователь рабочего стола"),
        };
        return new RemovalRow(RegistrationKey(entry), entry.DisplayName, entry.Publisher, entry.Version, source, decision, reason);
    }

    private static (RemovalDecision Decision, string Reason) AssessProgram(UninstallRegistration entry, bool current, bool duplicate)
    {
        var name = entry.DisplayName;
        var publisher = entry.Publisher;
        if (entry.SystemComponent == true || entry.ParentOrUpdate)
            return (RemovalDecision.Keep, Loc.T("Установщик зарегистрировал системный или связанный компонент либо обновление."));
        if (entry.NoRemove == true)
            return (RemovalDecision.Keep, Loc.T("В записи установщика отключено удаление через список программ Windows."));
        if (IsSharedRuntime(name, publisher))
            return (RemovalDecision.Keep, Loc.T("Это общая среда выполнения. Она может требоваться другим программам и играм."));
        var publisherFamily = StartupClassifier.ClassifyEntry(null, publisher);
        if (publisherFamily == StartupRisk.Security)
            return (RemovalDecision.Keep, Loc.T("Издатель относится к защитному ПО. Проверьте защиту Windows перед любыми изменениями."));
        if (publisherFamily == StartupRisk.Driver && ContainsDriverName(name))
            return (RemovalDecision.Keep, Loc.T("Имя и издатель указывают на пакет драйвера. Он может быть нужен устройству."));
        if (!current)
            return (RemovalDecision.Review, Loc.T("Для рекомендации нужен полный свежий список программ."));
        if (duplicate)
            return (RemovalDecision.Review, Loc.T("Одинаковые данные в нескольких записях мешают однозначно найти программу в Windows."));
        if (entry.NoRemove is null || entry.SystemComponent is null || string.IsNullOrWhiteSpace(entry.Version)
            || string.IsNullOrWhiteSpace(publisher) || !entry.HasUninstallRegistration)
            return (RemovalDecision.Review, Loc.T("Не хватает проверяемых сведений об издателе, версии или регистрации удаления."));
        if ((Equal(name, "Steam") && Equal(publisher, "Valve Corporation"))
            || (Equal(name, "Epic Games Launcher") && Equal(publisher, "Epic Games, Inc.")))
            return (RemovalDecision.ConsiderRemoval, Loc.T("По записи установщика это игровой лаунчер. Если больше им не пользуетесь, проверьте установленные игры, библиотеки и сохранения перед удалением."));
        if ((Equal(publisher, "VideoLAN") && Equal(name, "VLC media player"))
            || (Equal(publisher, "OBS Project") && Equal(name, "OBS Studio"))
            || (Equal(publisher, "Igor Pavlov") && SevenZipName().IsMatch(name)))
            return (RemovalDecision.ConsiderRemoval, Loc.T("По записи установщика это отдельная программа. Если больше ею не пользуетесь, сохраните данные и проверьте, какие программы зависят от неё."));
        return (RemovalDecision.Review, Loc.T("Запись есть в реестре, но назначение программы и её зависимости не подтверждены."));
    }

    private static bool IsSharedRuntime(string name, string? publisher) =>
        (Equal(publisher, "Microsoft Corporation") || Equal(publisher, "Microsoft"))
        && (name.StartsWith("Microsoft Visual C++ ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft .NET ", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft Windows Desktop Runtime", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Microsoft ASP.NET Core ", StringComparison.OrdinalIgnoreCase)
            || Equal(name, "Microsoft Edge WebView2 Runtime"));

    private static bool ContainsDriverName(string name) =>
        name.Contains(" Driver", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Chipset", StringComparison.OrdinalIgnoreCase);

    public static RemovalRow Startup(StartupEntry entry, bool unique)
    {
        var (decision, reason) = AssessStartup(entry, unique);
        return new RemovalRow(entry.Key, UninstallEntries.InventoryText(entry.DisplayName) ?? "—",
            UninstallEntries.InventoryText(entry.SignedBy ?? entry.Publisher), null, StartupLocations.ShortLabel(entry.Source), decision, reason);
    }

    private static (RemovalDecision Decision, string Reason) AssessStartup(StartupEntry entry, bool unique)
    {
        if (!StartupClassifier.CanDisable(entry.Risk) || entry.Risk is StartupRisk.Microsoft or StartupRisk.Driver)
            return (RemovalDecision.Keep, Loc.T("Запись связана с Windows, драйвером, защитой или обновлениями. Оставьте её включённой, если она включена."));
        if (StartupLocations.IsPackaged(entry.Source) && PackagedStartup.IsPolicy(entry.PackageState))
            return (RemovalDecision.Keep, Loc.T("Этой записью автозапуска управляет политика Windows."));
        if (!unique || !entry.CanChange)
            return (RemovalDecision.Review, Loc.T("Не подтверждены однозначная запись и возможность её изменить."));
        if (!entry.Enabled)
            return (RemovalDecision.AlreadyDisabled, Loc.T("Эта запись автозапуска уже выключена."));
        if (!entry.TargetExists || !HasKnownStartupIdentity(entry)
            || !StartupClassifier.IsOptionalAtLogon(entry.TargetPath, entry.Command, entry.Risk))
            return (RemovalDecision.Review, Loc.T("Не подтверждено, что эту запись можно отключить без потери нужной фоновой функции."));
        return (RemovalDecision.DisableStartup, Loc.T("Это подтверждённая запись запуска при входе. Отключите её, если автоматический запуск не нужен. Программу можно будет открыть вручную."));
    }

    private static bool HasKnownStartupIdentity(StartupEntry entry) =>
        entry.Signature == SignatureState.Valid && (Path.GetFileName(entry.TargetPath)?.ToUpperInvariant() switch
        {
            "STEAM.EXE" => Equal(entry.SignedBy, "Valve Corp.") || Equal(entry.SignedBy, "Valve Corporation"),
            "EPICGAMESLAUNCHER.EXE" => Equal(entry.SignedBy, "Epic Games, Inc."),
            "DISCORD.EXE" or "UPDATE.EXE" => Equal(entry.SignedBy, "Discord Inc.") || Equal(entry.SignedBy, "Discord Inc"),
            "SPOTIFY.EXE" => Equal(entry.SignedBy, "Spotify AB"),
            _ => false,
        });

    private static bool Equal(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A7-Zip(?: \d+(?:\.\d+){1,3})?(?: \((?:x64|x86|arm64)\))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SevenZipName();

    public static string DecisionText(RemovalDecision decision) => decision switch
    {
        RemovalDecision.ConsiderRemoval => Loc.T("МОЖНО РАССМОТРЕТЬ УДАЛЕНИЕ"),
        RemovalDecision.DisableStartup => Loc.T("МОЖНО ОТКЛЮЧИТЬ АВТОЗАПУСК"),
        RemovalDecision.Keep => Loc.T("ОСТАВИТЬ"),
        RemovalDecision.AlreadyDisabled => Loc.T("УЖЕ ОТКЛЮЧЕНО"),
        _ => Loc.T("НУЖНО ПРОВЕРИТЬ"),
    };
}
