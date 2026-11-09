using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Lycoris.Core;

namespace Lycoris.Modules.Startup;

/// <summary>What the row actions (open location, copy, search) need from a row.</summary>
public interface IStartupRow
{
    string DisplayName { get; }
    string Command { get; }
    string? TargetPath { get; }
    bool TargetExists { get; }
    /// <summary>File that represents the entry itself when the target is unknown (the .lnk in the Startup folder).</summary>
    string? ItemPath { get; }
}

/// <summary>Row of the autostart table.</summary>
public sealed partial class StartupItem : ObservableObject, IStartupRow
{
    public StartupItem(StartupEntry entry)
    {
        Entry = entry;
        _isEnabled = entry.Enabled;
        _disabledAtUtc = entry.DisabledAtUtc;
    }

    public StartupEntry Entry { get; private set; }

    public string Key => Entry.Key;
    public string DisplayName => Entry.DisplayName;
    public string Command => Entry.Command;
    public string? TargetPath => Entry.TargetPath;
    public bool TargetExists => Entry.TargetExists;
    public string? ItemPath => Entry.ItemPath;
    public StartupRisk Risk => Entry.Risk;
    public bool IsMachine => StartupLocations.IsMachine(Entry.Source);
    public bool IsPackaged => StartupLocations.IsPackaged(Entry.Source);

    public string ScopeText => IsMachine ? Loc.T("ДЛЯ ВСЕХ") : Loc.T("ДЛЯ МЕНЯ");
    public string SourceText => StartupLocations.ShortLabel(Entry.Source);
    public string PublisherText => Entry.Publisher ?? (Entry.TargetExists ? Loc.T("Издатель не указан") : Loc.T("Нет данных"));
    public bool HasPublisher => Entry.Publisher is not null;

    // ── Цифровая подпись (как в Sysinternals Autoruns): издатель из свойств файла пишет кто угодно, подпись — проверяема ──
    public bool SignatureOk => Entry.Signature == Lycoris.Core.Security.SignatureState.Valid;
    /// <summary>No signature, or a signature this PC does not trust: worth a look, not proof of anything bad.</summary>
    public bool SignatureWarn => Entry.Signature is Lycoris.Core.Security.SignatureState.Unsigned or Lycoris.Core.Security.SignatureState.Untrusted;
    /// <summary>The file was changed after it was signed — the strongest warning sign the table can show.</summary>
    public bool SignatureBad => Entry.Signature == Lycoris.Core.Security.SignatureState.Tampered;
    public string SignatureText => Entry.Signature switch
    {
        Lycoris.Core.Security.SignatureState.Valid => Loc.T("ПОДПИСАНО"),
        Lycoris.Core.Security.SignatureState.Unsigned => Loc.T("БЕЗ ПОДПИСИ"),
        Lycoris.Core.Security.SignatureState.Untrusted => Loc.T("ПОДПИСЬ НЕ ДОВЕРЕНА"),
        Lycoris.Core.Security.SignatureState.Tampered => Loc.T("ФАЙЛ ИЗМЕНЁН ПОСЛЕ ПОДПИСИ"),
        _ => "",
    };
    public string SignatureTooltip => Entry.Signature switch
    {
        Lycoris.Core.Security.SignatureState.Valid => Loc.Tf("Цифровая подпись проверена: {0}", Entry.SignedBy ?? Loc.T("издатель не прочитан")),
        Lycoris.Core.Security.SignatureState.Unsigned => Loc.T("У файла нет цифровой подписи. Так бывает у небольших и самодельных программ, но вредоносные тоже обычно без подписи: если программа вам незнакома, проверьте её."),
        Lycoris.Core.Security.SignatureState.Untrusted => Loc.T("Файл подписан, но этот ПК не доверяет подписавшему (сертификат самодельный, неизвестный или просрочен)."),
        Lycoris.Core.Security.SignatureState.Tampered => Loc.T("Файл изменили после того, как его подписали: подпись не совпадает с содержимым. Если вы не знаете, почему так, — отключите запись и проверьте ПК антивирусом."),
        _ => "",
    };
    public string FileText => BootPerformance.FileName(TargetPath) ?? "—";

    public string NameTooltip =>
        (IsPackaged ? $"{Entry.PackageFamilyName} › {Entry.Name}" : $"{Entry.Name}\n{StartupLocations.Describe(Entry.Source)}") +
        (Entry.Description is { } d && d != Entry.DisplayName ? $"\n{d}" : "");

    public string LocationTooltip => IsPackaged
        ? Loc.Tf("Задача автозапуска приложения MSIX\n{0} = {1} ({2})", StartupLocations.DescribeMark(Entry), Entry.PackageState?.ToString() ?? "?", PackagedStartup.StateText(Entry.PackageState))
        : Loc.Tf("{0}\nОтметка: {1}\\{2}", StartupLocations.Describe(Entry.Source), IsMachine ? "HKLM" : "HKCU", StartupLocations.ApprovedKey(Entry.Source));

    public bool CanDisable => StartupClassifier.CanDisable(Entry.Risk);

    /// <summary>False for packaged tasks under policy, in an unknown state or with names the journal could not undo.</summary>
    public bool CanChange => Entry.CanChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanToggle), nameof(ToggleTooltip), nameof(ShowBootDelay), nameof(LockNote))]
    private bool _isEnabled;

    /// <summary>Newest boot slowdown Windows recorded for this program (Diagnostics-Performance event 101), if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBootDelay), nameof(BootDelayTooltip))]
    private BootDelayMatch? _bootDelay;

    /// <summary>The «ЗАМЕДЛЯЕТ ЗАГРУЗКУ» tag: only while the entry still starts with Windows.</summary>
    public bool ShowBootDelay => BootDelay is not null && IsEnabled;

    public string? BootDelayTooltip => BootDelay is { } m ? BootPerformance.DelayTooltip(m) : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    private DateTime? _disabledAtUtc;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    private bool _isJournaled;

    /// <summary>Packaged tasks: the State from before Lycoris changed it (this account's journal), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleTooltip))]
    private int? _packageStateBefore;

    public string StatusText => IsEnabled ? Loc.T("ВКЛЮЧЕНО") : Loc.T("ОТКЛЮЧЕНО");

    public string StatusDetail
    {
        get
        {
            var parts = new List<string>();
            if (IsPackaged) parts.Add(PackagedStartup.StateText(Entry.PackageState));
            else if (!IsEnabled && DisabledAtUtc is { } at) parts.Add(Loc.Tf("с {0:dd.MM.yyyy HH:mm}", at.ToLocalTime()));
            else parts.Add(IsEnabled ? Loc.T("при входе в Windows") : Loc.T("не запускается"));
            if (IsJournaled) parts.Add("Lycoris");
            return string.Join(" · ", parts);
        }
    }

    public bool CanToggle => CanChange && (!IsEnabled || CanDisable);

    /// <summary>
    /// Why the switch is locked, as text next to it (never only a tooltip): protection icons, policy-managed or
    /// unrecognised packaged tasks. Null when the switch works.
    /// </summary>
    public string? LockNote =>
        IsPackaged && PackagedStartup.IsPolicy(Entry.PackageState) ? Loc.T("управляет политика — не меняется")
        : !CanChange ? Loc.T("состояние не распознано — не меняется")
        : IsEnabled && !CanDisable ? Loc.T("защита Windows — не отключается")
        : null;

    public string ToggleTooltip =>
        IsPackaged && PackagedStartup.IsPolicy(Entry.PackageState)
            ? Loc.T("Состоянием этой задачи управляет администратор или групповая политика — Lycoris его не меняет.")
        : !CanChange ? Loc.T("Состояние задачи не распознано или имя пакета нестандартное — Lycoris её не меняет.")
        : IsEnabled && !CanDisable ? Loc.T("Значок защиты Windows или антивируса — Lycoris его не отключает.")
        : !Elevation.IsElevated ? Loc.T("Без прав администратора: нажмите — Lycoris запросит права, перезапустится и продолжит это действие.")
        : IsPackaged ? PackagedToggleTooltip(!IsEnabled, PackagedStartup.StateAfterSwitch(!IsEnabled, PackageStateBefore))
        : IsEnabled ? Loc.T("Отключить: программа не будет запускаться при входе. Ничего не удаляется.")
        : Loc.T("Включить: программа снова будет запускаться при входе в Windows.");

    /// <summary>The packaged toggle's tooltip names the State the switch really leaves (an undo restores the original).</summary>
    internal static string PackagedToggleTooltip(bool enable, int state) =>
        state != PackagedStartup.TargetState(enable)
            ? (enable
                ? Loc.Tf("Включить: Lycoris вернёт State = {0} ({1}), как было до него.", state, PackagedStartup.StateText(state))
                : Loc.Tf("Отключить: Lycoris вернёт State = {0} ({1}), как было до него.", state, PackagedStartup.StateText(state)))
              + (PackagedStartup.StateNote(state) is { } note ? " " + note : "")
        : enable ? Loc.T("Включить (State = 2). Windows запускает такое приложение при входе, если его хотя бы раз открывали.")
        : Loc.T("Отключить, как в Диспетчере задач (State = 1): приложение не будет запускаться при входе. Ничего не удаляется.");

    /// <summary>Takes the state read back after a change (<see cref="StartupScanner.Refresh"/>).</summary>
    public void Update(StartupEntry fresh)
    {
        Entry = fresh;
        IsEnabled = fresh.Enabled;
        DisabledAtUtc = fresh.DisabledAtUtc;
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(LockNote));
        OnPropertyChanged(nameof(ToggleTooltip));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(LocationTooltip));
        SyncToggle();
    }

    /// <summary>The toggle flips itself on click; this pushes the real state back into it (after cancel / failure).</summary>
    public void SyncToggle() => OnPropertyChanged(nameof(IsEnabled));
}

/// <summary>Row of the logon / boot task table.</summary>
public sealed partial class LogonTaskItem : ObservableObject, IStartupRow
{
    public LogonTaskItem(LogonTask task)
    {
        Info = task;
        _isEnabled = task.Enabled;
    }

    public LogonTask Info { get; private set; }

    public string DisplayName => Info.Name;
    public string Command => Info.Command;
    public string? TargetPath => Info.TargetPath;
    public bool TargetExists => Info.TargetExists;
    public string? ItemPath => null;
    public StartupRisk Risk => Info.Risk;
    public string Path => Info.Path;

    public string TriggerText => Info.AtLogon && Info.AtBoot ? Loc.T("ВХОД + ЗАГРУЗКА") : Info.AtLogon ? Loc.T("ПРИ ВХОДЕ") : Loc.T("ПРИ ЗАГРУЗКЕ");

    public string TriggerDetail
    {
        get
        {
            var who = Info.AtLogon
                ? Info.LogonUser is { } u ? Loc.Tf("вход: {0}", ShortUser(u)) : Loc.T("вход любого пользователя")
                : Loc.T("старт Windows");
            return Info.OtherTriggers > 0 ? Loc.Tf("{0} · +{1} триг.", who, Info.OtherTriggers) : who;
        }
    }

    public string AuthorText => Info.Author ?? Info.Publisher ?? Loc.T("Автор не указан");
    public string FolderText => LogonTaskScanner.FolderLabel(Info.Folder);

    public string NameTooltip =>
        Info.Path + (Info.Description is { } d ? $"\n{d}" : "") +
        (Info.RunAs is { } r ? Loc.Tf("\nЗапуск от: {0}", r) + (Info.RunsElevated ? Loc.T(" (с наивысшими правами)") : "") : "") +
        (Info.LastRun is { } lr ? Loc.Tf("\nПоследний запуск: {0:dd.MM.yyyy HH:mm}", lr) : Loc.T("\nЕщё не запускалась"));

    public bool CanDisable => StartupClassifier.CanDisable(Info.Risk);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanToggle), nameof(ToggleTooltip), nameof(ShowBootDelay), nameof(LockNote))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusDetail))]
    private bool _isJournaled;

    /// <summary>Newest boot slowdown Windows recorded for the task's program (Diagnostics-Performance event 101), if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBootDelay), nameof(BootDelayTooltip))]
    private BootDelayMatch? _bootDelay;

    /// <summary>The «ЗАМЕДЛЯЕТ ЗАГРУЗКУ» tag: only while the task is enabled.</summary>
    public bool ShowBootDelay => BootDelay is not null && IsEnabled;

    public string? BootDelayTooltip => BootDelay is { } m ? BootPerformance.DelayTooltip(m) : null;

    public string StatusText => IsEnabled ? Loc.T("ВКЛЮЧЕНА") : Loc.T("ОТКЛЮЧЕНА");

    public string StatusDetail
    {
        get
        {
            var text = Info.LastRun is { } lr ? Loc.Tf("запуск {0:dd.MM HH:mm}", lr) : Loc.T("не запускалась");
            return IsJournaled ? text + " · Lycoris" : text;
        }
    }

    public bool CanToggle => !IsEnabled || CanDisable;

    /// <summary>Why the switch is locked, as text next to it; null when it works.</summary>
    public string? LockNote =>
        IsEnabled && !CanDisable ? (Info.Risk == StartupRisk.Updater ? Loc.T("обновление программы — не отключается") : Loc.T("защитное ПО — не отключается")) : null;

    public string ToggleTooltip =>
        IsEnabled && !CanDisable
            ? Info.Risk == StartupRisk.Updater
                ? Loc.T("Задача обновляет программу и закрывает уязвимости — Lycoris её не отключает.")
                : Loc.T("Задача защитного ПО — Lycoris её не отключает.")
        : !Elevation.IsElevated ? Loc.T("Без прав администратора: нажмите — Lycoris запросит права, перезапустится и продолжит это действие.")
        : IsEnabled ? Loc.T("Отключить задачу в планировщике. Задача не удаляется.")
        : Loc.T("Включить задачу снова.");

    public void Update(bool enabled)
    {
        IsEnabled = enabled;
        Info = Info with { Enabled = enabled };
        SyncToggle();
    }

    public void SyncToggle() => OnPropertyChanged(nameof(IsEnabled));

    private static string ShortUser(string user)
    {
        var i = user.LastIndexOf('\\');
        return i >= 0 ? user[(i + 1)..] : user;
    }
}
