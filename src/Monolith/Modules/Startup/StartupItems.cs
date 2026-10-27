using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Core;

namespace Monolith.Modules.Startup;

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

    public string ScopeText => IsMachine ? "ДЛЯ ВСЕХ" : "ДЛЯ МЕНЯ";
    public string SourceText => StartupLocations.ShortLabel(Entry.Source);
    public string PublisherText => Entry.Publisher ?? (Entry.TargetExists ? "Издатель не указан" : "Нет данных");
    public bool HasPublisher => Entry.Publisher is not null;
    public string FileText => BootPerformance.FileName(TargetPath) ?? "—";

    public string NameTooltip =>
        (IsPackaged ? $"{Entry.PackageFamilyName} › {Entry.Name}" : $"{Entry.Name}\n{StartupLocations.Describe(Entry.Source)}") +
        (Entry.Description is { } d && d != Entry.DisplayName ? $"\n{d}" : "");

    public string LocationTooltip => IsPackaged
        ? $"Задача автозапуска приложения MSIX\n{StartupLocations.DescribeMark(Entry)} = {Entry.PackageState?.ToString() ?? "?"} ({PackagedStartup.StateText(Entry.PackageState)})"
        : $"{StartupLocations.Describe(Entry.Source)}\nОтметка: {(IsMachine ? "HKLM" : "HKCU")}\\{StartupLocations.ApprovedKey(Entry.Source)}";

    public bool CanDisable => StartupClassifier.CanDisable(Entry.Risk);

    /// <summary>False for packaged tasks under policy, in an unknown state or with names the journal could not undo.</summary>
    public bool CanChange => Entry.CanChange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanToggle), nameof(ToggleTooltip), nameof(ShowBootDelay))]
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

    public string StatusText => IsEnabled ? "ВКЛЮЧЕНО" : "ОТКЛЮЧЕНО";

    public string StatusDetail
    {
        get
        {
            var parts = new List<string>();
            if (IsPackaged) parts.Add(PackagedStartup.StateText(Entry.PackageState));
            else if (!IsEnabled && DisabledAtUtc is { } at) parts.Add("с " + at.ToLocalTime().ToString("dd.MM.yyyy HH:mm"));
            else parts.Add(IsEnabled ? "при входе в Windows" : "не запускается");
            if (IsJournaled) parts.Add("MONOLITH");
            return string.Join(" · ", parts);
        }
    }

    public bool CanToggle => Elevation.IsElevated && CanChange && (!IsEnabled || CanDisable);

    public string ToggleTooltip =>
        !Elevation.IsElevated ? "Нужны права администратора: изменения записываются в журнал отката."
        : IsPackaged && PackagedStartup.IsPolicy(Entry.PackageState)
            ? "Состоянием этой задачи управляет администратор или групповая политика — MONOLITH его не меняет."
        : !CanChange ? "Состояние задачи не распознано или имя пакета нестандартное — MONOLITH её не меняет."
        : IsEnabled && !CanDisable ? "Значок защиты Windows или антивируса — MONOLITH его не отключает."
        : IsPackaged && IsEnabled ? "Отключить, как в Диспетчере задач (State = 1): приложение не будет запускаться при входе. Ничего не удаляется."
        : IsPackaged ? "Включить (State = 2). Windows запускает такое приложение при входе, если его хотя бы раз открывали."
        : IsEnabled ? "Отключить: программа не будет запускаться при входе. Ничего не удаляется."
        : "Включить: программа снова будет запускаться при входе в Windows.";

    /// <summary>Takes the state read back after a change (<see cref="StartupScanner.Refresh"/>).</summary>
    public void Update(StartupEntry fresh)
    {
        Entry = fresh;
        IsEnabled = fresh.Enabled;
        DisabledAtUtc = fresh.DisabledAtUtc;
        OnPropertyChanged(nameof(CanChange));
        OnPropertyChanged(nameof(CanToggle));
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

    public string TriggerText => Info.AtLogon && Info.AtBoot ? "ВХОД + ЗАГРУЗКА" : Info.AtLogon ? "ПРИ ВХОДЕ" : "ПРИ ЗАГРУЗКЕ";

    public string TriggerDetail
    {
        get
        {
            var who = Info.AtLogon
                ? Info.LogonUser is { } u ? $"вход: {ShortUser(u)}" : "вход любого пользователя"
                : "старт Windows";
            return Info.OtherTriggers > 0 ? $"{who} · +{Info.OtherTriggers} триг." : who;
        }
    }

    public string AuthorText => Info.Author ?? Info.Publisher ?? "Автор не указан";
    public string FolderText => LogonTaskScanner.FolderLabel(Info.Folder);

    public string NameTooltip =>
        Info.Path + (Info.Description is { } d ? $"\n{d}" : "") +
        (Info.RunAs is { } r ? $"\nЗапуск от: {r}{(Info.RunsElevated ? " (с наивысшими правами)" : "")}" : "") +
        (Info.LastRun is { } lr ? $"\nПоследний запуск: {lr:dd.MM.yyyy HH:mm}" : "\nЕщё не запускалась");

    public bool CanDisable => StartupClassifier.CanDisable(Info.Risk);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanToggle), nameof(ToggleTooltip), nameof(ShowBootDelay))]
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

    public string StatusText => IsEnabled ? "ВКЛЮЧЕНА" : "ОТКЛЮЧЕНА";

    public string StatusDetail
    {
        get
        {
            var text = Info.LastRun is { } lr ? $"запуск {lr:dd.MM HH:mm}" : "не запускалась";
            return IsJournaled ? text + " · MONOLITH" : text;
        }
    }

    public bool CanToggle => Elevation.IsElevated && (!IsEnabled || CanDisable);

    public string ToggleTooltip =>
        !Elevation.IsElevated ? "Нужны права администратора: изменения записываются в журнал отката."
        : IsEnabled && !CanDisable
            ? Info.Risk == StartupRisk.Updater
                ? "Задача обновляет программу и закрывает уязвимости — MONOLITH её не отключает."
                : "Задача защитного ПО — MONOLITH её не отключает."
        : IsEnabled ? "Отключить задачу в планировщике. Задача не удаляется."
        : "Включить задачу снова.";

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
