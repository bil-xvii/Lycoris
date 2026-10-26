using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

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
            Log.Warn($"Резервные копии драйверов: {ex.Message}");
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
        if (!RequireAdmin("резервное копирование драйверов")) return;
        var (free, _) = SystemProfile.SystemDriveSpace();
        var details = new List<string>
        {
            $"Что сохраняется: все сторонние драйверы из хранилища Windows — пакетов: {OemPackageCount}",
            "Встроенные драйверы Windows не копируются: они и так есть в любой установке системы",
            $"Куда: {Path.Combine(BackupsRoot, DateTime.Now.ToString("yyyyMMdd-HHmm"))}",
            "Будет выполнено: pnputil /export-driver * <папка>",
            $"Объём — до нескольких гигабайт (пакет NVIDIA — 1–2 ГБ). Свободно на диске: {(free == 0 ? "—" : SystemProfile.FormatBytes(free))}",
            "Система при этом не изменяется",
        };
        if (free > 0 && free < LowSpaceBytes) details.Add("Внимание: на системном диске меньше 5 ГБ — копия может не поместиться");

        var confirmed = await Ui.ConfirmAsync("СОЗДАТЬ РЕЗЕРВНУЮ КОПИЮ ДРАЙВЕРОВ?",
            "Копия пригодится после переустановки Windows: драйверы из неё возвращаются одной кнопкой. Более новую версию драйвера копия не заменяет.",
            details, confirmText: "СОЗДАТЬ");
        if (!confirmed) return;

        await RunBusyAsync("Резервное копирование драйверов…", async () =>
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
            BackupStage = "ЭКСПОРТ ДРАЙВЕРОВ";
            try
            {
                var (outcome, folder) = await DriverBackupService.ExportAllAsync(
                    line => Ui.Post(() => AppendOutput(line)),
                    n => Ui.Post(() =>
                    {
                        BackupPercent = Math.Min(100, n * 100.0 / total);
                        BackupStage = $"ЭКСПОРТ · {Math.Min(n, total)} ИЗ {total}";
                    }),
                    _backupCts.Token);
                BackupResultOk = outcome.Success;
                BackupResult = outcome.Success ? $"Копия создана: {folder}" : outcome.Message;
                if (outcome.Success)
                {
                    Log.Success($"Резервная копия драйверов создана: {folder}");
                    Ui.Toast("Резервная копия драйверов создана.", ToastKind.Success, "РЕЗЕРВНАЯ КОПИЯ");
                }
                else
                {
                    Log.Warn($"Резервная копия драйверов: {outcome.Message}");
                    Ui.Toast(outcome.Message, outcome.Cancelled ? ToastKind.Warning : ToastKind.Error, "РЕЗЕРВНАЯ КОПИЯ");
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
            BackupStage = "ОТМЕНА…";
            cts.Cancel();
        }
    }

    // ───────────────────────────── Restore ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task RestoreBackupAsync(BackupItem? item)
    {
        if (item is null) return;
        if (!RequireAdmin("восстановление драйверов")) return;

        // Check the backup against its manifest before anything else (no restore point for a refused backup).
        var plan = await Task.Run(() => DriverBackupService.PlanRestore(item.Path));
        if (!plan.Ok)
        {
            Log.Warn($"Восстановление из {item.Name} отклонено: {plan.Error}");
            await Ui.AlertAsync("ВОССТАНОВЛЕНИЕ НЕВОЗМОЖНО", plan.Error!);
            return;
        }

        var confirmed = await Ui.ConfirmAsync(
            "ВОССТАНОВИТЬ ДРАЙВЕРЫ ИЗ КОПИИ?",
            "Windows добавит пакеты из копии в хранилище драйверов и установит их на подходящие устройства — только там, где они лучше текущих. Более новые драйверы старыми не заменяются.",
            [
                $"Копия: {item.Name} от {item.DateText}",
                $"Содержимое: {item.InfCountText}, {item.SizeText} — {item.ProvidersText}",
                "Сначала будет создана точка восстановления системы",
                $"Будет выполнено: pnputil /add-driver \"<пакет>.inf\" /install — по очереди для каждого пакета из описания копии (пакетов: {plan.InfPaths.Count})",
                "Устанавливаются только пакеты, перечисленные в описании копии (monolith-backup.json)",
                "Во время установки экран может мигать, а сеть и звук — пропадать на несколько секунд",
                "Компьютер не будет перезагружен автоматически",
            ],
            confirmText: "ВОССТАНОВИТЬ", danger: true,
            footnote: "Откат — через точку восстановления системы.");
        if (!confirmed) return;

        var attempted = false;
        await RunBusyAsync("Восстановление драйверов…", async () =>
        {
            IsBackupRunning = true;
            CanCancelBackup = false; // never kill pnputil in the middle of an install
            BackupOutput.Clear();
            HasBackupOutput = false;
            BackupResult = null;
            BackupIndeterminate = true;
            BackupStage = "ТОЧКА ВОССТАНОВЛЕНИЯ";
            try
            {
                if (!await EnsureRestorePointAsync("MONOLITH: восстановление драйверов"))
                {
                    BackupResult = "Восстановление отменено.";
                    BackupResultOk = false;
                    return;
                }
                BackupStage = $"УСТАНОВКА ИЗ КОПИИ {item.Name}";
                Log.Info($"Восстановление драйверов из {item.Path}");
                attempted = true;
                var outcome = await DriverBackupService.RestoreAsync(item.Path, line => Ui.Post(() => AppendOutput(line)), CancellationToken.None);
                BackupResultOk = outcome.Success;
                BackupResult = outcome.Message;
                if (outcome.RebootRequired) BackupRebootRequired = true;
                if (outcome.Success) Log.Success($"Восстановление драйверов: {outcome.Message}");
                else Log.Warn($"Восстановление драйверов: {outcome.Message}");
                Ui.Toast(outcome.Message, outcome.Success ? ToastKind.Success : ToastKind.Error, "ВОССТАНОВЛЕНИЕ ДРАЙВЕРОВ");
            }
            finally
            {
                IsBackupRunning = false;
                BackupIndeterminate = false;
            }
        });
        if (attempted) await RunBusyAsync("Обновление сведений о драйверах…", LoadInventoryAsync);
    }

    // ───────────────────────────── Delete / open ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task DeleteBackupAsync(BackupItem? item)
    {
        if (item is null) return;
        if (!RequireAdmin("удаление резервной копии")) return;
        var confirmed = await Ui.ConfirmAsync(
            "УДАЛИТЬ РЕЗЕРВНУЮ КОПИЮ?",
            "Папка копии будет удалена безвозвратно (не в корзину). Установленные драйверы это не затрагивает.",
            [$"Папка: {item.Path}", $"Создана: {item.DateText}", $"Освободится: {item.SizeText}"],
            confirmText: "УДАЛИТЬ", danger: true);
        if (!confirmed) return;

        await RunBusyAsync("Удаление резервной копии…", async () =>
        {
            await DriverBackupService.DeleteAsync(item.Path);
            Ui.Toast($"Копия {item.Name} удалена.", ToastKind.Success);
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
        ShellOpen(BackupsRoot, "папку резервных копий");
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
        ShellOpen(item.Path, "папку копии");
    }
}
