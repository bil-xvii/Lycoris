using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

/// <summary>ОБНОВЛЕНИЯ WINDOWS: driver search and install through the Windows Update Agent.</summary>
public sealed partial class DriversViewModel
{
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _installCts;
    private WuSearchResult? _lastSearch;
    private int _lastSearchHResult;

    public ObservableCollection<WuUpdateItem> Updates { get; } = [];
    public bool HasUpdates => Updates.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    [NotifyCanExecuteChangedFor(nameof(SearchUpdatesCommand), nameof(InstallUpdatesCommand), nameof(CancelSearchCommand))]
    private UpdatesState _updatesState = UpdatesState.NotSearched;

    [ObservableProperty] private string _updatesError = "";
    [ObservableProperty] private bool _updatesErrorIsService;
    [ObservableProperty] private string _lastSearchText = "";
    [ObservableProperty] private bool _rebootPending;
    [ObservableProperty] private bool _rebootRequiredAfterInstall;
    [ObservableProperty] private string? _installSummary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdatesCommand))]
    private int _selectedUpdatesCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchUpdatesCommand), nameof(InstallUpdatesCommand), nameof(CancelInstallCommand), nameof(RefreshCommand))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelInstallCommand))]
    private bool _installCancelRequested;

    [ObservableProperty] private double _installPercent;
    [ObservableProperty] private bool _installIndeterminate;
    [ObservableProperty] private string _installStage = "";
    [ObservableProperty] private string _installDetail = "";

    // ── Policy (read-only) ──
    [ObservableProperty] private string? _policyNotice;
    [ObservableProperty] private bool _usesWsus;
    [ObservableProperty] private bool _useMicrosoftServers;

    public bool IsSearching => UpdatesState == UpdatesState.Searching;

    public string InstallButtonText => SelectedUpdatesCount > 0 ? $"ЗАГРУЗИТЬ И УСТАНОВИТЬ · {SelectedUpdatesCount}" : "ЗАГРУЗИТЬ И УСТАНОВИТЬ";

    private int ServerSelection => UsesWsus && UseMicrosoftServers ? WuText.ServerWindowsUpdate : WuText.ServerDefault;

    private void ApplyPolicy(WuPolicyInfo policy)
    {
        UsesWsus = policy.UsesWsus;
        var notes = new List<string>();
        if (policy.DriversBlocked)
            notes.Add("Политика организации запрещает Windows Update присылать драйверы (ExcludeWUDriversInQualityUpdate = 1), поэтому поиск может ничего не найти. Драйверы ставьте с сайтов производителей.");
        if (policy.UsesWsus)
            notes.Add($"Обновления приходят с сервера организации (WSUS: {policy.WsusServer}) — список драйверов определяет администратор.");
        if (policy.SearchOrderConfig == 0)
            notes.Add("В параметрах установки устройств отключена автоматическая загрузка драйверов из Windows Update (SearchOrderConfig = 0) — так делает, например, DDU. Новые устройства не получат драйверы сами.");
        PolicyNotice = notes.Count > 0 ? string.Join("\n", notes) : null;
    }

    // ───────────────────────────── Search ─────────────────────────────

    private bool CanSearch() => UpdatesState != UpdatesState.Searching && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task SearchUpdatesAsync()
    {
        if (UpdatesState == UpdatesState.Searching || IsInstalling) return;
        _searchCts?.Dispose();
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _searchCts.Token;

        UpdatesState = UpdatesState.Searching;
        UpdatesError = "";
        UpdatesErrorIsService = false;
        UpdateStats();
        Log.Info("Поиск драйверов в Windows Update…");
        try
        {
            var result = await WindowsUpdateDrivers.SearchAsync(ServerSelection, ct);
            ApplySearch(result);
            _lastSearchHResult = 0;
            UpdatesState = UpdatesState.Ready;
            Log.Info(result.Updates.Count == 0
                ? "Windows Update: новых драйверов нет."
                : $"Windows Update: найдено драйверов — {result.Updates.Count}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            UpdatesState = _lastSearch is null ? UpdatesState.NotSearched : UpdatesState.Ready;
            Log.Info("Поиск драйверов в Windows Update отменён.");
        }
        catch (Exception ex)
        {
            _lastSearchHResult = WuText.HResultOf(ex);
            UpdatesError = WuText.DescribeException(ex);
            UpdatesErrorIsService = _lastSearchHResult == WuText.ServiceDisabled;
            UpdatesState = UpdatesState.Error;
            Log.Warn("Windows Update: " + UpdatesError);
        }
        finally
        {
            UpdateStats();
        }
    }

    [RelayCommand(CanExecute = nameof(IsSearching))]
    private void CancelSearch() => _searchCts?.Cancel();

    private void ApplySearch(WuSearchResult result)
    {
        _lastSearch = result;
        foreach (var old in Updates) old.PropertyChanged -= OnUpdateItemChanged;
        Updates.Clear();
        var ordered = result.Updates
            .Select(u => new WuUpdateItem(u))
            .OrderByDescending(i => i.FixesProblem)
            .ThenBy(i => i.IsFirmware)
            .ThenBy(i => i.IsOptional)
            .ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in ordered)
        {
            item.PropertyChanged += OnUpdateItemChanged;
            Updates.Add(item);
        }
        RebootPending = result.RebootPending;
        LastSearchText = $"ПРОВЕРЕНО В {result.When:HH:mm}";
        InstallSummary = null;
        RecountSelection();
    }

    private void OnUpdateItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WuUpdateItem.IsSelected)) RecountSelection();
    }

    private void RecountSelection() => SelectedUpdatesCount = Updates.Count(u => u.IsSelected && u.CanSelect);

    [RelayCommand]
    private void SelectAllUpdates()
    {
        foreach (var u in Updates.Where(u => u.CanSelect && u.ResultOk != true)) u.IsSelected = true;
    }

    [RelayCommand]
    private void ClearUpdateSelection()
    {
        foreach (var u in Updates) u.IsSelected = false;
    }

    [RelayCommand]
    private void OpenOptionalUpdates() =>
        OpenUrl(SystemProfile.Current.IsWindows11 ? DriverSources.OptionalUpdatesUri : DriverSources.WindowsUpdateUri);

    [RelayCommand]
    private void OpenServices() => Ui.Navigate("services");

    partial void OnUseMicrosoftServersChanged(bool value)
    {
        if (UpdatesState != UpdatesState.Searching && !IsInstalling) _ = SearchUpdatesAsync();
    }

    // ───────────────────────────── Install ─────────────────────────────

    private bool CanInstallUpdates() => CanModify() && UpdatesState != UpdatesState.Searching && SelectedUpdatesCount > 0;

    [RelayCommand(CanExecute = nameof(CanInstallUpdates))]
    private async Task InstallUpdatesAsync()
    {
        if (!RequireAdmin("установка драйверов")) return;
        var selected = Updates.Where(u => u.IsSelected && u.CanSelect).ToList();
        if (selected.Count == 0)
        {
            Ui.Toast("Отметьте драйверы, которые нужно установить.", ToastKind.Warning);
            return;
        }

        // EULA: never accepted silently (research §1.1).
        var eulaApproved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected.Where(u => u.NeedsEula))
        {
            var text = string.IsNullOrWhiteSpace(item.Update.EulaText) ? "Текст соглашения недоступен." : item.Update.EulaText!;
            var paragraphs = text.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(60).ToList();
            if (await Ui.ConfirmAsync("ЛИЦЕНЗИОННОЕ СОГЛАШЕНИЕ",
                    $"Драйвер «{item.Title}» требует принять лицензию производителя. Без согласия он не будет установлен.",
                    paragraphs, confirmText: "ПРИНИМАЮ", cancelText: "НЕ ПРИНИМАЮ"))
                eulaApproved.Add(item.Id);
        }

        var totalSize = selected.Sum(u => u.Update.SizeBytes);
        var details = selected.Select(u =>
                $"{u.Title} — {u.ClassText.ToLowerInvariant()}, {u.SizeText}{(u.IsOptional ? ", необязательное" : "")}{(u.NeedsEula && !eulaApproved.Contains(u.Id) ? " (лицензия не принята — будет пропущено)" : "")}")
            .ToList();
        details.Add(RestorePointPlanLine());
        details.Add("Текущие драйверы этих устройств сохраняются в «Резервную копию» — на случай, если драйвер пропадёт (более новый драйвер копия не заменяет)");
        details.Add("Затем: загрузка и установка через Windows Update с отображением хода");
        details.Add("Отмена прерывает загрузку; драйвер, который уже устанавливается, Windows Update доустановит");
        details.Add("Компьютер не будет перезагружен автоматически");
        if (RebootPending) details.Add("Windows уже ожидает перезагрузки — лучше перезагрузить компьютер до установки");

        var confirmed = await Ui.ConfirmAsync(
            $"УСТАНОВИТЬ {selected.Count} {DriverFormat.Plural(selected.Count, "ДРАЙВЕР", "ДРАЙВЕРА", "ДРАЙВЕРОВ")}?",
            $"Драйверы будут загружены с серверов Windows Update (около {DriverFormat.Size(totalSize)}) и установлены. " +
            "Во время установки экран может мигнуть, а устройство — ненадолго отключиться.",
            details, confirmText: "УСТАНОВИТЬ",
            footnote: "Откат: точка восстановления или «Откатить драйвер» в диспетчере устройств.");
        if (!confirmed) return;

        var ids = selected.Select(u => u.Id).ToList();
        foreach (var item in selected)
        {
            item.ResultText = null;
            item.ResultMessage = null;
        }

        var attempted = false;
        await RunBusyAsync("Установка драйверов…", async () =>
        {
            _installCts?.Dispose();
            _installCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _installCts.Token;
            InstallCancelRequested = false;
            IsInstalling = true;
            InstallPercent = 0;
            InstallIndeterminate = true;
            InstallSummary = null;
            try
            {
                // 1. Restore point.
                InstallStage = "ТОЧКА ВОССТАНОВЛЕНИЯ";
                InstallDetail = "Создание точки восстановления системы…";
                if (!await EnsureRestorePointAsync("MONOLITH: драйверы из Windows Update"))
                {
                    Log.Info("Установка драйверов отменена: нет точки восстановления.");
                    return;
                }

                // 2. Copy of the drivers these updates replace.
                InstallStage = "РЕЗЕРВНАЯ КОПИЯ";
                InstallDetail = "Сохранение текущих драйверов устройств…";
                await BackupAffectedDriversAsync(selected, ct);

                // 3. Download + install. A cancel does not end this await early: it returns once Windows Update has
                //    really stopped, with the result of every driver (including one that was finished after the cancel).
                var progress = new Progress<WuProgress>(OnInstallProgress);
                attempted = true;
                var result = await WindowsUpdateDrivers.InstallAsync(ids, eulaApproved, ServerSelection, progress, ct);
                ApplyInstallResult(selected, result);
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // Thrown only before the install stage started (check, backup, download).
                InstallSummary = "Установка отменена — драйверы не устанавливались.";
                Ui.Toast(InstallSummary, ToastKind.Warning);
                Log.Warn(InstallSummary);
            }
            catch (Exception ex)
            {
                var message = WuText.DescribeException(ex);
                InstallSummary = "Установка не выполнена: " + message;
                Log.Error("Установка драйверов из Windows Update", ex);
                Ui.Toast(message, ToastKind.Error, "УСТАНОВКА НЕ ВЫПОЛНЕНА");
            }
            finally
            {
                IsInstalling = false;
                InstallCancelRequested = false;
                InstallIndeterminate = false;
            }
        });

        // Installed drivers changed — refresh the inventory (problems, installed list, GPU versions).
        if (attempted) await RunBusyAsync("Обновление сведений о драйверах…", LoadInventoryAsync);
    }

    private bool CanCancelInstall() => IsInstalling && !InstallCancelRequested;

    [RelayCommand(CanExecute = nameof(CanCancelInstall))]
    private void CancelInstall()
    {
        if (_installCts is null || _installCts.IsCancellationRequested) return;
        InstallCancelRequested = true;
        InstallDetail = CancelDetail;
        _installCts.Cancel();
    }

    private const string CancelDetail = "Отмена… драйвер, который уже устанавливается, Windows Update доустановит";

    private void OnInstallProgress(WuProgress p)
    {
        string detail;
        switch (p.Stage)
        {
            case WuStage.Searching:
                InstallIndeterminate = true;
                InstallStage = "ПРОВЕРКА";
                detail = "Windows Update проверяет, что выбранные драйверы всё ещё актуальны…";
                break;
            case WuStage.Downloading:
                InstallIndeterminate = false;
                InstallPercent = p.Percent * 0.5;
                InstallStage = $"ЗАГРУЗКА · {p.Current} ИЗ {p.Total}";
                detail = $"Загружено {p.Percent}%";
                break;
            case WuStage.Installing:
                InstallIndeterminate = false;
                InstallPercent = 50 + p.Percent * 0.5;
                InstallStage = $"УСТАНОВКА · {p.Current} ИЗ {p.Total}";
                detail = $"Установлено {p.Percent}%";
                break;
            default:
                return;
        }
        // After a cancel the stage still moves, but the line keeps telling what is going on.
        InstallDetail = InstallCancelRequested ? CancelDetail : detail;
    }

    /// <summary>Exports the oemNN.inf packages of the devices the selected updates will replace.</summary>
    private async Task BackupAffectedDriversAsync(IReadOnlyList<WuUpdateItem> selected, CancellationToken ct)
    {
        var infs = selected
            .SelectMany(u => DriverMatching.FindAffected(_signedDrivers, u.Update.HardwareId))
            .Where(d => d.IsOem)
            .Select(d => d.InfName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (infs.Count == 0)
        {
            Log.Info("Резервная копия перед обновлением не нужна: заменяемых сторонних драйверов нет.");
            return;
        }
        try
        {
            var note = "Перед установкой: " + string.Join("; ", selected.Select(s => s.Title));
            var (count, folder) = await DriverBackupService.ExportPackagesAsync(infs, note, ct);
            if (folder is not null) Log.Success($"Текущие драйверы сохранены ({count}): {folder}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Not fatal: the restore point is the main safety net.
            Log.Warn($"Не удалось сохранить текущие драйверы: {ex.Message}");
        }
        _ = ReloadBackupsAsync();
    }

    private void ApplyInstallResult(IReadOnlyList<WuUpdateItem> selected, WuInstallResult result)
    {
        foreach (var r in result.Items)
        {
            var item = selected.FirstOrDefault(s => string.Equals(s.Id, r.UpdateId, StringComparison.OrdinalIgnoreCase));
            item?.ApplyResult(r);
            if (r.Success) Log.Success($"Драйвер установлен: {r.Title}");
            else Log.Warn($"Драйвер не установлен: {r.Title} — {r.Message}");
        }
        RecountSelection();
        var ok = result.Items.Count(i => i.Success);
        var failed = result.Items.Count - ok;
        if (result.RebootRequired) RebootRequiredAfterInstall = true;

        InstallSummary = result.StillRunning
            ? "Windows Update не подтвердил остановку и, возможно, ещё доустанавливает драйвер в фоне. Не запускайте установку повторно " +
              "несколько минут, затем нажмите «Обновить» — список драйверов покажет, что установилось."
            : result.Cancelled
                ? $"Установка прервана. Установлено: {ok}, не установлено: {failed}."
                : failed == 0
                    ? $"Готово: установлено {ok} {DriverFormat.Plural(ok, "драйвер", "драйвера", "драйверов")}."
                    : $"Установлено: {ok}, с ошибками: {failed}. Подробности — у каждого драйвера в списке.";
        Ui.Toast(InstallSummary + (result.RebootRequired ? " Требуется перезагрузка." : ""),
            failed == 0 && !result.Cancelled && !result.StillRunning ? ToastKind.Success : ToastKind.Warning, "ДРАЙВЕРЫ");
    }
}
