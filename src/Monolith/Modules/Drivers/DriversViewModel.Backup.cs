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

    /// <summary>What a backup is good for — and what it cannot do (pnputil never replaces a newer driver with an older one).</summary>
    public const string BackupPurposeText =
        "Копия пригодится после переустановки Windows или если драйвер пропал: из неё драйверы ставятся одной кнопкой. " +
        "Более новый драйвер копия не заменяет — чтобы откатить неудачное обновление, используйте «Откатить драйвер» " +
        "в диспетчере устройств или точку восстановления.";

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
            "Затем MONOLITH запишет опись копии — SHA-256 каждого файла: восстановление ставит драйверы, только если копия не изменилась",
            $"Объём — до нескольких гигабайт (пакет NVIDIA — 1–2 ГБ). Свободно на диске: {(free == 0 ? "—" : SystemProfile.FormatBytes(free))}",
            "Система при этом не изменяется",
        };
        if (free > 0 && free < LowSpaceBytes) details.Add("Внимание: на системном диске меньше 5 ГБ — копия может не поместиться");

        var confirmed = await Ui.ConfirmAsync("СОЗДАТЬ РЕЗЕРВНУЮ КОПИЮ ДРАЙВЕРОВ?",
            BackupPurposeText, details, confirmText: "СОЗДАТЬ");
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

    private bool CanRestoreBackup(BackupItem? item) => item is { CanRestore: true } && CanModify();

    /// <summary>
    /// Installs drivers from a backup. Only folders MONOLITH created qualify: right before pnputil, every file is
    /// compared with the backup's manifest (SHA-256, nothing extra, no links); the confirmation lists every package.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreBackup))]
    private async Task RestoreBackupAsync(BackupItem? item)
    {
        if (item is null) return;
        if (!RequireAdmin("восстановление драйверов")) return;
        if (!item.CanRestore)
        {
            await Ui.AlertAsync("ВОССТАНОВЛЕНИЕ НЕДОСТУПНО", item.RestoreBlockedReason!);
            return;
        }

        var details = new List<string>
        {
            $"Копия: {item.Name} от {item.DateText} — {item.SizeText}",
            "Сначала каждый файл копии сверяется с её описью (SHA-256): если что-то добавлено, изменено или пропало, ничего не устанавливается",
            RestorePointPlanLine(),
            $"Будет выполнено: pnputil /add-driver \"{Path.Combine(item.Path, "*.inf")}\" /subdirs /install",
            "Во время установки экран может мигать, а сеть и звук — пропадать на несколько секунд",
            "Компьютер не будет перезагружен автоматически",
            $"Пакеты драйверов в копии ({item.Packages.Count}) — Windows поставит только те, что лучше установленных:",
        };
        details.AddRange(item.Info.Packages.Select(BackupItem.DescribePackage));

        var confirmed = await Ui.ConfirmAsync(
            "ВОССТАНОВИТЬ ДРАЙВЕРЫ ИЗ КОПИИ?",
            "Windows добавит пакеты из копии в хранилище драйверов и установит их на подходящие устройства — только там, где они лучше текущих. " +
            "Более новые драйверы старыми не заменяются: для отката обновления используйте «Откатить драйвер» в диспетчере устройств.",
            details, confirmText: "ВОССТАНОВИТЬ", danger: true,
            footnote: "Откат — через точку восстановления системы или «Откатить драйвер» в диспетчере устройств.");
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
            BackupStage = "ПРОВЕРКА ЦЕЛОСТНОСТИ КОПИИ";
            try
            {
                Log.Info($"Восстановление драйверов из {item.Path}");
                var (outcome, verification) = await DriverBackupService.RestoreAsync(item.Path,
                    line => Ui.Post(() => AppendOutput(line)),
                    async _ =>
                    {
                        // Runs on a worker thread (between the check and pnputil): UI updates go through the dispatcher.
                        Ui.Post(() => BackupStage = "ТОЧКА ВОССТАНОВЛЕНИЯ");
                        if (!await EnsureRestorePointAsync("MONOLITH: восстановление драйверов")) return false;
                        Ui.Post(() => BackupStage = $"УСТАНОВКА ИЗ КОПИИ {item.Name}");
                        attempted = true;
                        return true;
                    },
                    CancellationToken.None);

                if (!verification.Ok)
                {
                    BackupResultOk = false;
                    BackupResult = $"Копия не прошла проверку: {verification.Problem}. Ничего не установлено.";
                    Ui.Toast(BackupResult, ToastKind.Error, "ВОССТАНОВЛЕНИЕ ДРАЙВЕРОВ");
                    return;
                }
                if (outcome is null)
                {
                    BackupResultOk = false;
                    BackupResult = "Восстановление отменено.";
                    return;
                }
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
    private async Task OpenBackupsFolderAsync()
    {
        if (!Directory.Exists(BackupsRoot) && !AppPaths.TryEnsureDir(BackupsRoot))
        {
            Ui.Toast("Папка резервных копий ещё не создана — она появится вместе с первой копией.", ToastKind.Info);
            return;
        }
        await ShellOpenAsync(BackupsRoot, "папку резервных копий");
    }

    [RelayCommand]
    private async Task OpenBackupAsync(BackupItem? item)
    {
        if (item is null) return;
        if (!BackupFolders.IsDirectChild(BackupsRoot, item.Path) || !Directory.Exists(item.Path))
        {
            Ui.Toast("Папка копии не найдена.", ToastKind.Warning);
            return;
        }
        await ShellOpenAsync(item.Path, "папку копии");
    }
}
