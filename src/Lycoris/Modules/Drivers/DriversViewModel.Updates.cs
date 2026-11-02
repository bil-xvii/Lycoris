using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Drivers;

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
    [NotifyPropertyChangedFor(nameof(InstallButtonText), nameof(PrimaryText), nameof(HasPrimary))]
    [NotifyCanExecuteChangedFor(nameof(InstallUpdatesCommand))]
    private int _selectedUpdatesCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchUpdatesCommand), nameof(InstallUpdatesCommand), nameof(CancelInstallCommand), nameof(RefreshCommand))]
    private bool _isInstalling;

    [ObservableProperty] private double _installPercent;
    [ObservableProperty] private bool _installIndeterminate;
    [ObservableProperty] private string _installStage = "";
    [ObservableProperty] private string _installDetail = "";

    // ── Policy (read-only) ──
    [ObservableProperty] private string? _policyNotice;
    [ObservableProperty] private bool _usesWsus;
    [ObservableProperty] private bool _useMicrosoftServers;

    public bool IsSearching => UpdatesState == UpdatesState.Searching;

    public string InstallButtonText => SelectedUpdatesCount > 0 ? Loc.Tf("ЗАГРУЗИТЬ И УСТАНОВИТЬ · {0}", SelectedUpdatesCount) : Loc.T("ЗАГРУЗИТЬ И УСТАНОВИТЬ");

    private int ServerSelection => UsesWsus && UseMicrosoftServers ? WuText.ServerWindowsUpdate : WuText.ServerDefault;

    private void ApplyPolicy(WuPolicyInfo policy)
    {
        UsesWsus = policy.UsesWsus;
        var notes = new List<string>();
        if (policy.DriversBlocked)
            notes.Add(Loc.T("Политика организации запрещает Windows Update присылать драйверы (ExcludeWUDriversInQualityUpdate = 1), поэтому поиск может ничего не найти. Драйверы ставьте с сайтов производителей."));
        if (policy.UsesWsus)
            notes.Add(Loc.Tf("Обновления приходят с сервера организации (WSUS: {0}) — список драйверов определяет администратор.", policy.WsusServer));
        if (policy.SearchOrderConfig == 0)
            notes.Add(Loc.T("В параметрах установки устройств отключена автоматическая загрузка драйверов из Windows Update (SearchOrderConfig = 0) — так делает, например, DDU. Новые устройства не получат драйверы сами."));
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
                ? Loc.T("Windows Update: новых драйверов нет.")
                : Loc.Tf("Windows Update: найдено драйверов — {0}.", result.Updates.Count));
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
        LastSearchText = Loc.Tf("ПРОВЕРЕНО В {0:HH:mm}", result.When);
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
    private Task OpenOptionalUpdatesAsync() =>
        OpenUrlAsync(SystemProfile.Current.IsWindows11 ? DriverSources.OptionalUpdatesUri : DriverSources.WindowsUpdateUri);

    [RelayCommand]
    private void OpenServices()
    {
        // Straight to the Windows Update service in ВСЕ СЛУЖБЫ (the page opens on АВТОЗАПУСК otherwise).
        Ui.RequestFocus("services", "wuauserv");
        Ui.Navigate("services");
    }

    partial void OnUseMicrosoftServersChanged(bool value)
    {
        if (UpdatesState != UpdatesState.Searching && !IsInstalling) _ = SearchUpdatesAsync();
    }

    // ───────────────────────────── Install ─────────────────────────────

    // A replayed intent carries the ids of the checked drivers and waits for the Windows Update search to finish.
    private bool CanInstallUpdates(string? packedIds) =>
        CanModify() && (packedIds is not null || (UpdatesState != UpdatesState.Searching && SelectedUpdatesCount > 0));

    [RelayCommand(CanExecute = nameof(CanInstallUpdates))]
    private async Task InstallUpdatesAsync(string? packedIds)
    {
        if (!RequireAdmin(Loc.T("установка драйверов"), "InstallUpdates", Startup.IntentList.Pack(Updates.Where(u => u.IsSelected && u.CanSelect).Select(u => u.Id)))) return;
        if (Startup.IntentList.Unpack(packedIds) is { Count: > 0 } replayed)
        {
            if (UpdatesState == UpdatesState.NotSearched) _ = SearchUpdatesAsync();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (UpdatesState is UpdatesState.Searching or UpdatesState.NotSearched && sw.Elapsed < TimeSpan.FromSeconds(180) && !_lifetime.IsCancellationRequested)
                await Task.Delay(300);
            var set = replayed.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var u in Updates) u.IsSelected = u.CanSelect && set.Contains(u.Id);
            RecountSelection();
        }
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
            var text = string.IsNullOrWhiteSpace(item.Update.EulaText) ? Loc.T("Текст соглашения недоступен.") : item.Update.EulaText!;
            var paragraphs = text.Split(['\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(60).ToList();
            if (await Ui.ConfirmAsync("ЛИЦЕНЗИОННОЕ СОГЛАШЕНИЕ",
                    Loc.Tf("Драйвер «{0}» требует принять лицензию производителя. Без согласия он не будет установлен.", item.Title),
                    paragraphs, confirmText: Loc.T("ПРИНИМАЮ"), cancelText: Loc.T("НЕ ПРИНИМАЮ")))
                eulaApproved.Add(item.Id);
        }

        var totalSize = selected.Sum(u => u.Update.SizeBytes);
        var details = selected.Select(u =>
                Loc.Tf("{0} — {1}, {2}{3}{4}", u.Title, u.ClassText.ToLowerInvariant(), u.SizeText, u.IsOptional ? Loc.T(", необязательное") : "", u.NeedsEula && !eulaApproved.Contains(u.Id) ? Loc.T(" (лицензия не принята — будет пропущено)") : ""))
            .ToList();
        details.Add(Loc.T("Сначала: точка восстановления системы и копия текущих драйверов этих устройств"));
        details.Add(Loc.T("Затем: загрузка и установка через Windows Update с отображением хода"));
        details.Add(Loc.T("Компьютер не будет перезагружен автоматически"));
        if (RebootPending) details.Add(Loc.T("Windows уже ожидает перезагрузки — лучше перезагрузить компьютер до установки"));

        var confirmed = await Ui.ConfirmAsync(
            Loc.N(selected.Count, "УСТАНОВИТЬ {0} ДРАЙВЕР?", "УСТАНОВИТЬ {0} ДРАЙВЕРА?", "УСТАНОВИТЬ {0} ДРАЙВЕРОВ?"),
            Loc.Tf("Драйверы будут загружены с серверов Windows Update (около {0}) и установлены. ", DriverFormat.Size(totalSize)) +
            Loc.T("Во время установки экран может мигнуть, а устройство — ненадолго отключиться."),
            details, confirmText: Loc.T("УСТАНОВИТЬ"),
            footnote: Loc.T("Откат: точка восстановления или «Откатить драйвер» в диспетчере устройств."));
        if (!confirmed) return;

        var ids = selected.Select(u => u.Id).ToList();
        foreach (var item in selected)
        {
            item.ResultText = null;
            item.ResultMessage = null;
        }

        var attempted = false;
        await RunBusyAsync(OperationClass.Install, Loc.T("Установка драйверов…"), async () =>
        {
            _installCts?.Dispose();
            _installCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var ct = _installCts.Token;
            IsInstalling = true;
            InstallPercent = 0;
            InstallIndeterminate = true;
            InstallSummary = null;
            try
            {
                // 1. Restore point.
                InstallStage = Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ");
                InstallDetail = Loc.T("Создание точки восстановления системы…");
                if (!await EnsureRestorePointAsync(Loc.T("Lycoris: драйверы из Windows Update")))
                {
                    Log.Info("Установка драйверов отменена: нет точки восстановления.");
                    return;
                }

                // 2. Copy of the drivers these updates replace.
                InstallStage = Loc.T("РЕЗЕРВНАЯ КОПИЯ");
                InstallDetail = Loc.T("Сохранение текущих драйверов устройств…");
                await BackupAffectedDriversAsync(selected, ct);

                // 3. Download + install.
                var progress = new Progress<WuProgress>(OnInstallProgress);
                attempted = true;
                // On cancel this await keeps going until Windows Update has really stopped (IsInstalling stays true),
                // then the result shows what was installed before the abort.
                var result = await WindowsUpdateDrivers.InstallAsync(ids, eulaApproved, ServerSelection, progress, ct);
                ApplyInstallResult(selected, result);
            }
            catch (Exception ex) when (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // Cancelled before the install step, or Windows Update did not report per-driver results.
                InstallSummary = attempted
                    ? Loc.T("Установка отменена. Драйверы, которые Windows Update успела поставить, остались на месте — список обновится.")
                    : Loc.T("Установка отменена. Драйверы не устанавливались.");
                Ui.Toast(InstallSummary, ToastKind.Warning);
                Log.Warn(InstallSummary);
            }
            catch (Exception ex)
            {
                var message = WuText.DescribeException(ex);
                InstallSummary = Loc.Tf("Установка не выполнена: {0}", message);
                Log.Error("Установка драйверов из Windows Update", ex);
                Ui.Toast(message, ToastKind.Error, Loc.T("УСТАНОВКА НЕ ВЫПОЛНЕНА"));
            }
            finally
            {
                IsInstalling = false;
                InstallIndeterminate = false;
            }
        });

        // Installed drivers changed — refresh the inventory (problems, installed list, GPU versions).
        if (attempted) await RunBusyAsync(OperationClass.Measure, Loc.T("Обновление сведений о драйверах…"), LoadInventoryAsync);
    }

    [RelayCommand(CanExecute = nameof(IsInstalling))]
    private void CancelInstall()
    {
        if (_installCts is null || _installCts.IsCancellationRequested) return;
        InstallDetail = Loc.T("Отмена… ждём, пока Windows Update завершит текущий драйвер. Это может занять до пары минут.");
        _installCts.Cancel();
    }

    private void OnInstallProgress(WuProgress p)
    {
        if (_installCts is { IsCancellationRequested: true }) return; // keep the "Отмена…" message while WUA winds down
        switch (p.Stage)
        {
            case WuStage.Searching:
                InstallIndeterminate = true;
                InstallStage = Loc.T("ПРОВЕРКА");
                InstallDetail = Loc.T("Windows Update проверяет, что выбранные драйверы всё ещё актуальны…");
                break;
            case WuStage.Downloading:
                InstallIndeterminate = false;
                InstallPercent = p.Percent * 0.5;
                InstallStage = Loc.Tf("ЗАГРУЗКА · {0} ИЗ {1}", p.Current, p.Total);
                InstallDetail = Loc.Tf("Загружено {0}%", p.Percent);
                break;
            case WuStage.Installing:
                InstallIndeterminate = false;
                InstallPercent = 50 + p.Percent * 0.5;
                InstallStage = Loc.Tf("УСТАНОВКА · {0} ИЗ {1}", p.Current, p.Total);
                InstallDetail = Loc.Tf("Установлено {0}%", p.Percent);
                break;
        }
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
            var note = Loc.Tf("Перед установкой: {0}", string.Join("; ", selected.Select(s => s.Title)));
            var (count, folder) = await DriverBackupService.ExportPackagesAsync(infs, note, ct);
            if (folder is not null) Log.Success("Текущие драйверы сохранены ({0}): {1}", count, folder);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Not fatal: the restore point is the main safety net.
            Log.Warn("Не удалось сохранить текущие драйверы: {0}", ex.Message);
        }
        _ = ReloadBackupsAsync();
    }

    private void ApplyInstallResult(IReadOnlyList<WuUpdateItem> selected, WuInstallResult result)
    {
        foreach (var r in result.Items)
        {
            var item = selected.FirstOrDefault(s => string.Equals(s.Id, r.UpdateId, StringComparison.OrdinalIgnoreCase));
            item?.ApplyResult(r);
            if (r.Success) Log.Success("Драйвер установлен: {0}", r.Title);
            else Log.Warn("Драйвер не установлен: {0} — {1}", r.Title, r.Message);
        }
        RecountSelection();
        var ok = result.Items.Count(i => i.Success);
        var failed = result.Items.Count - ok;
        if (result.RebootRequired) RebootRequiredAfterInstall = true;

        InstallSummary = result.Cancelled
            ? Loc.Tf("Установка прервана. Установлено: {0}, не установлено: {1}.", ok, failed)
            : failed == 0
                ? Loc.N(ok, "Готово: установлено {0} драйвер.", "Готово: установлено {0} драйвера.", "Готово: установлено {0} драйверов.")
                : Loc.Tf("Установлено: {0}, с ошибками: {1}. Подробности — у каждого драйвера в списке.", ok, failed);
        Ui.Toast(InstallSummary + (result.RebootRequired ? Loc.T(" Требуется перезагрузка.") : ""),
            failed == 0 && !result.Cancelled ? ToastKind.Success : ToastKind.Warning, Loc.T("ДРАЙВЕРЫ"));
    }
}
