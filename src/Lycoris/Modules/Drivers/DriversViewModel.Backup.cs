using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Drivers;

/// <summary>РЕЗЕРВНАЯ КОПИЯ: pnputil export / restore of third-party drivers.</summary>
public sealed partial class DriversViewModel
{
    private const int MaxOutputLines = 500;
    private const ulong LowSpaceBytes = 5UL * 1024 * 1024 * 1024;
    private CancellationTokenSource? _backupCts;

    public ObservableCollection<BackupItem> Backups { get; } = [];
    public ObservableCollection<string> BackupOutput { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportBackupCommand), nameof(RestoreBackupCommand), nameof(DeleteBackupCommand), nameof(RefreshCommand), nameof(InstallUpdatesCommand))]
    private bool _isBackupRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelBackupCommand))]
    private bool _canCancelBackup;

    [ObservableProperty] private string _backupStage = "";
    [ObservableProperty] private double _backupPercent;
    [ObservableProperty] private bool _backupIndeterminate;
    [ObservableProperty] private string? _backupResult;
    [ObservableProperty] private bool _backupResultOk;
    [ObservableProperty] private bool _backupRebootRequired;
    [ObservableProperty] private int _oemPackageCount;
    [ObservableProperty] private string _backupFreeSpace = "";
    [ObservableProperty] private bool _lowDiskSpace;
    [ObservableProperty] private bool _hasBackups;
    [ObservableProperty] private bool _hasBackupOutput;

    public string BackupsRoot => DriverBackupService.Root;

    private void ApplyBackups(IReadOnlyList<DriverBackupInfo> list, int oemPackages)
    {
        Backups.Clear();
        foreach (var b in list) Backups.Add(new BackupItem(b));
        HasBackups = Backups.Count > 0;
        OemPackageCount = oemPackages;
        var (free, _) = SystemProfile.SystemDriveSpace();
        BackupFreeSpace = free == 0 ? "—" : SystemProfile.FormatBytes(free);
        LowDiskSpace = free > 0 && free < LowSpaceBytes;
    }

    private async Task ReloadBackupsAsync()
    {
        try
        {
            var (list, oem) = await Task.Run(() => (DriverBackupService.List(), DriverInventory.CountOemPackages()));
            ApplyBackups(list, oem);
            UpdateStats();
        }
        catch (Exception ex)
        {
            Log.Warn("Резервные копии драйверов: {0}", ex.Message);
        }
    }

    private void AppendOutput(string line)
    {
        BackupOutput.Add(line);
        while (BackupOutput.Count > MaxOutputLines) BackupOutput.RemoveAt(0);
        HasBackupOutput = true;
    }

    // ───────────────────────────── Export ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task ExportBackupAsync()
    {
        if (!RequireAdmin(Loc.T("резервное копирование драйверов"), "ExportBackup")) return;
        var (free, _) = SystemProfile.SystemDriveSpace();
        var details = new List<string>
        {
            Loc.Tf("Что сохраняется: все сторонние драйверы из хранилища Windows — пакетов: {0}", OemPackageCount),
            Loc.T("Встроенные драйверы Windows не копируются: они и так есть в любой установке системы"),
            Loc.Tf("Куда: {0}", Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmm"))),
            Loc.T("Будет выполнено: pnputil /export-driver * <папка>"),
            Loc.Tf("Объём — до нескольких гигабайт (пакет NVIDIA — 1–2 ГБ). Свободно на диске: {0}", (free == 0 ? "—" : SystemProfile.FormatBytes(free))),
            Loc.T("Система при этом не изменяется"),
        };
        if (free > 0 && free < LowSpaceBytes) details.Add(Loc.T("Внимание: на системном диске меньше 5 ГБ — копия может не поместиться"));

        var confirmed = await Ui.ConfirmAsync("СОЗДАТЬ РЕЗЕРВНУЮ КОПИЮ ДРАЙВЕРОВ?",
            Loc.T("Копия пригодится после переустановки Windows: драйверы из неё возвращаются одной кнопкой. Более новую версию драйвера копия не заменяет."),
            details, confirmText: Loc.T("СОЗДАТЬ"));
        if (!confirmed) return;

        await RunBusyAsync(OperationClass.Install, Loc.T("Резервное копирование драйверов…"), async () =>
        {
            _backupCts?.Dispose();
            _backupCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var total = Math.Max(1, OemPackageCount);
            IsBackupRunning = true;
            CanCancelBackup = true;
            BackupOutput.Clear();
            HasBackupOutput = false;
            BackupResult = null;
            BackupPercent = 0;
            BackupIndeterminate = OemPackageCount == 0;
            BackupStage = Loc.T("ЭКСПОРТ ДРАЙВЕРОВ");
            try
            {
                var (outcome, folder) = await DriverBackupService.ExportAllAsync(
                    line => Ui.Post(() => AppendOutput(line)),
                    n => Ui.Post(() =>
                    {
                        BackupPercent = Math.Min(100, n * 100.0 / total);
                        BackupStage = Loc.Tf("ЭКСПОРТ · {0} ИЗ {1}", Math.Min(n, total), total);
                    }),
                    _backupCts.Token);
                BackupResultOk = outcome.Success;
                BackupResult = outcome.Success ? Loc.Tf("Копия создана: {0}", folder) : outcome.Message;
                if (outcome.Success)
                {
                    Log.Success("Резервная копия драйверов создана: {0}", folder);
                    Ui.Toast("Резервная копия драйверов создана.", ToastKind.Success, Loc.T("РЕЗЕРВНАЯ КОПИЯ"));
                }
                else
                {
                    Log.Warn("Резервная копия драйверов: {0}", outcome.Message);
                    Ui.Toast(outcome.Message, outcome.Cancelled ? ToastKind.Warning : ToastKind.Error, Loc.T("РЕЗЕРВНАЯ КОПИЯ"));
                }
            }
            finally
            {
                IsBackupRunning = false;
                CanCancelBackup = false;
                BackupIndeterminate = false;
            }
            await ReloadBackupsAsync();
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancelBackup))]
    private void CancelBackup()
    {
        if (_backupCts is { IsCancellationRequested: false } cts)
        {
            BackupStage = Loc.T("ОТМЕНА…");
            cts.Cancel();
        }
    }

    // ───────────────────────────── Restore ─────────────────────────────

    /// <summary>The backup a command parameter names: the row itself (a click) or its folder name as text (the admin-intent replay).</summary>
    private BackupItem? BackupFor(object? target) => target switch
    {
        BackupItem item => item,
        string name when name.Length > 0 => Backups.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    private bool CanModifyWith(object? target) => CanModify();

    [RelayCommand(CanExecute = nameof(CanModifyWith))]
    private async Task RestoreBackupAsync(object? target)
    {
        if (BackupFor(target) is not { } item) return;
        if (!RequireAdmin(Loc.T("восстановление драйверов"), "RestoreBackup", item.Name)) return;

        // Check the backup against its manifest before anything else (no restore point for a refused backup).
        var plan = await Task.Run(() => DriverBackupService.PlanRestore(item.Path));
        if (!plan.Ok)
        {
            Log.Warn("Восстановление из {0} отклонено: {1}", item.Name, plan.Error);
            await Ui.AlertAsync("ВОССТАНОВЛЕНИЕ НЕВОЗМОЖНО", plan.Error!);
            return;
        }

        var confirmed = await Ui.ConfirmAsync(
            "ВОССТАНОВИТЬ ДРАЙВЕРЫ ИЗ КОПИИ?",
            Loc.T("Windows добавит пакеты из копии в хранилище драйверов и установит их на подходящие устройства — только там, где они лучше текущих. Более новые драйверы старыми не заменяются."),
            [
                Loc.Tf("Копия: {0} от {1}", item.Name, item.DateText),
                Loc.Tf("Содержимое: {0}, {1} — {2}", item.InfCountText, item.SizeText, item.ProvidersText),
                Loc.T("Сначала будет создана точка восстановления системы"),
                Loc.Tf("Будет выполнено: pnputil /add-driver \"<пакет>.inf\" /install — по очереди для каждого пакета из описания копии (пакетов: {0})", plan.InfPaths.Count),
                Loc.T("Устанавливаются только пакеты, перечисленные в описании копии (lycoris-backup.json)"),
                Loc.T("Во время установки экран может мигать, а сеть и звук — пропадать на несколько секунд"),
                Loc.T("Компьютер не будет перезагружен автоматически"),
            ],
            confirmText: Loc.T("ВОССТАНОВИТЬ"), danger: true,
            footnote: Loc.T("Откат — через точку восстановления системы."));
        if (!confirmed) return;

        var attempted = false;
        await RunBusyAsync(OperationClass.Install, Loc.T("Восстановление драйверов…"), async () =>
        {
            IsBackupRunning = true;
            CanCancelBackup = false; // never kill pnputil in the middle of an install
            BackupOutput.Clear();
            HasBackupOutput = false;
            BackupResult = null;
            BackupIndeterminate = true;
            BackupStage = Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ");
            try
            {
                if (!await EnsureRestorePointAsync(Loc.T("Lycoris: восстановление драйверов")))
                {
                    BackupResult = Loc.T("Восстановление отменено.");
                    BackupResultOk = false;
                    return;
                }
                BackupStage = Loc.Tf("УСТАНОВКА ИЗ КОПИИ {0}", item.Name);
                Log.Info("Восстановление драйверов из {0}", item.Path);
                attempted = true;
                var outcome = await DriverBackupService.RestoreAsync(item.Path, line => Ui.Post(() => AppendOutput(line)), CancellationToken.None);
                BackupResultOk = outcome.Success;
                BackupResult = outcome.Message;
                if (outcome.RebootRequired) BackupRebootRequired = true;
                if (outcome.Success) Log.Success("Восстановление драйверов: {0}", outcome.Message);
                else Log.Warn("Восстановление драйверов: {0}", outcome.Message);
                Ui.Toast(outcome.Message, outcome.Success ? ToastKind.Success : ToastKind.Error, Loc.T("ВОССТАНОВЛЕНИЕ ДРАЙВЕРОВ"));
            }
            finally
            {
                IsBackupRunning = false;
                BackupIndeterminate = false;
            }
        });
        if (attempted) await RunBusyAsync(OperationClass.Measure, Loc.T("Обновление сведений о драйверах…"), LoadInventoryAsync);
    }

    // ───────────────────────────── Delete / open ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanModifyWith))]
    private async Task DeleteBackupAsync(object? target)
    {
        if (BackupFor(target) is not { } item) return;
        if (!RequireAdmin(Loc.T("удаление резервной копии"), "DeleteBackup", item.Name)) return;
        var confirmed = await Ui.ConfirmAsync(
            "УДАЛИТЬ РЕЗЕРВНУЮ КОПИЮ?",
            Loc.T("Папка копии будет удалена безвозвратно (не в корзину). Установленные драйверы это не затрагивает."),
            [Loc.Tf("Папка: {0}", item.Path), Loc.Tf("Создана: {0}", item.DateText), Loc.Tf("Освободится: {0}", item.SizeText)],
            confirmText: Loc.T("УДАЛИТЬ"), danger: true);
        if (!confirmed) return;

        await RunBusyAsync(OperationClass.Cleanup, Loc.T("Удаление резервной копии…"), async () =>
        {
            await DriverBackupService.DeleteAsync(item.Path);
            Ui.Toast(Loc.Tf("Копия {0} удалена.", item.Name), ToastKind.Success);
            await ReloadBackupsAsync();
        });
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        if (!Directory.Exists(BackupsRoot) && !AppPaths.TryEnsureDir(BackupsRoot))
        {
            Ui.Toast("Папка резервных копий ещё не создана — она появится вместе с первой копией.", ToastKind.Info);
            return;
        }
        ShellOpen(BackupsRoot, Loc.T("папку резервных копий"));
    }

    [RelayCommand]
    private void OpenBackup(BackupItem? item)
    {
        if (item is null) return;
        if (!BackupFolders.IsDirectChild(BackupsRoot, item.Path) || !Directory.Exists(item.Path))
        {
            Ui.Toast("Папка копии не найдена.", ToastKind.Warning);
            return;
        }
        ShellOpen(item.Path, Loc.T("папку копии"));
    }
}
