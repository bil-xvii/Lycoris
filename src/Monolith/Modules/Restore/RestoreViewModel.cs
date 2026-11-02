using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Tools;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Restore;

/// <summary>
/// The undo center: every change MONOLITH made (journal), reverting one record / one batch / everything,
/// and Windows System Restore (status, enabling, restore points).
/// </summary>
public sealed partial class RestoreViewModel : PageViewModel
{
    private ChangeJournal? _journal;
    private bool _rebuildQueued;
    private IReadOnlyDictionary<Guid, DriftItem> _drift = new Dictionary<Guid, DriftItem>();
    private bool _driftRunning, _driftPending;
    private bool _confirmingRestorePoints;

    public override string Key => "restore";
    public override string Index => "10";
    public override string Title => "ВОССТАНОВЛЕНИЕ";
    public override string Subtitle => "Журнал изменений MONOLITH, откат и точки восстановления Windows.";
    public override string Glyph => Glyphs.History;

    public ObservableCollection<BatchItem> Batches { get; } = new();
    public ObservableCollection<RestorePointItem> RestorePoints { get; } = new();

    // ── journal ──
    [ObservableProperty] private JournalFilter _filter = JournalFilter.All;
    [ObservableProperty] private int _activeCount;
    /// <summary>Active records «ОТКАТИТЬ ВСЁ» reverts (this account's and machine-wide, see <see cref="ChangeJournal.IsBulkRevertible"/>).</summary>
    [ObservableProperty] private int _revertAllCount;
    /// <summary>Tooltip of «ОТКАТИТЬ ВСЁ»: the active records it leaves and why (null when it reverts every active record).</summary>
    [ObservableProperty] private string? _revertAllHint;
    /// <summary>Why «ОТКАТИТЬ ВСЁ» is grey (shown as text next to it), or null when it is available.</summary>
    [ObservableProperty] private string? _revertAllReason;
    /// <summary>One caption next to «ОТКАТИТЬ ВСЁ»: the reason it is grey, else what it leaves out.</summary>
    public string? RevertAllNote => RevertAllReason ?? RevertAllHint;
    partial void OnRevertAllReasonChanged(string? value) => OnPropertyChanged(nameof(RevertAllNote));
    partial void OnRevertAllHintChanged(string? value) => OnPropertyChanged(nameof(RevertAllNote));
    [ObservableProperty] private int _revertedCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private bool _isFilterEmpty;
    [ObservableProperty] private string _filterEmptyText = "";

    // ── drift: active records changed outside MONOLITH (competitors.md §3 must-have #5) ──
    [ObservableProperty] private int _driftCount;
    [ObservableProperty] private bool _hasDrift;
    [ObservableProperty] private bool _hasReapplicableDrift;
    [ObservableProperty] private string _driftLine = "";

    // ── system restore ──
    [ObservableProperty] private bool _protectionEnabled;
    [ObservableProperty] private bool _protectionBlocked;
    [ObservableProperty] private string _protectionTitle = "—";
    [ObservableProperty] private string _protectionText = "Чтение состояния защиты системы…";
    [ObservableProperty] private bool _canOfferEnable;
    [ObservableProperty] private bool _pointsLoading;
    [ObservableProperty] private string _pointsMessage = "";
    [ObservableProperty] private string _pointsCountText = "";

    public string BackupsPath => AppPaths.BackupsDir;

    public RestoreViewModel()
    {
        PropertyChanged += OnSelfPropertyChanged;
        AppSettings.Current.PropertyChanged += OnSettingsChanged;
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key && IsLoaded) ApplyFocus(Ui.TakeFocusRequest(Key));
        };
    }

    /// <summary>Batches the user opened («ПОКАЗАТЬ ЗАПИСИ»): kept across rebuilds of the list.</summary>
    private readonly HashSet<Guid> _expanded = [];

    protected override async Task LoadAsync()
    {
        _journal = await Task.Run(() => ChangeJournal.Instance);
        _journal.Changed += OnJournalChanged;
        Rebuild();
        _ = RefreshDriftAsync();
        await RefreshProtectionAsync();
        MarkChecked();
        ApplyFocus(Ui.TakeFocusRequest(Key));
    }

    protected override async Task OnReturnAsync()
    {
        Rebuild();
        _ = RefreshDriftAsync();
        await RefreshProtectionAsync();
        MarkChecked();
        ApplyFocus(Ui.TakeFocusRequest(Key));
    }

    /// <summary>«ОБНОВИТЬ» in the header: re-reads the journal, the drift check and the System Restore state.</summary>
    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync(OperationClass.Measure, "Обновление…", async () =>
    {
        Rebuild();
        _ = RefreshDriftAsync();
        await RefreshProtectionAsync();
        MarkChecked();
    });

    // ═════════════════════════════ Focus («section:…» from the palette / other pages) ═════════════════════════════

    /// <summary>Section the view scrolls to: last-batch, journal, restore-points, drift (one-shot, see RestoreView).</summary>
    [ObservableProperty] private string? _focusedSection;

    /// <summary>«section:&lt;name&gt;»: opens the part of the page (last batch expanded, drift filter) and asks the view to scroll to it.</summary>
    private void ApplyFocus(string? focusId)
    {
        const string prefix = CommandPaletteIndex.RestoreSectionPrefix;
        if (focusId is null || !focusId.StartsWith(prefix, StringComparison.Ordinal)) return;
        var section = focusId[prefix.Length..];
        if (section == "drift" && HasDrift) Filter = JournalFilter.Drifted;
        else if (section == "last-batch")
        {
            if (Filter == JournalFilter.Drifted || Filter == JournalFilter.Reverted) Filter = JournalFilter.Active;
            if (Batches.FirstOrDefault(b => b.CanRevert) is { } b) b.IsExpanded = true;
        }
        FocusedSection = null; // re-raise even for the same section
        FocusedSection = section;
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy)) NotifyCommands();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.CreateRestorePoints) && !_confirmingRestorePoints)
            Ui.Post(() => OnPropertyChanged(nameof(CreateRestorePoints)));
    }

    private const int BatchPage = 30;
    private int _shownBatches = BatchPage;
    [ObservableProperty] private bool _hasMoreBatches;
    [ObservableProperty] private string _showMoreText = "";

    [RelayCommand]
    private void ShowMoreBatches()
    {
        _shownBatches += BatchPage;
        Rebuild();
    }

    partial void OnFilterChanged(JournalFilter value)
    {
        _shownBatches = BatchPage;
        Rebuild();
    }

    /// <summary>The journal raises Changed from background threads; coalesce bursts into one rebuild on the UI thread.</summary>
    private void OnJournalChanged()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        d.BeginInvoke(() =>
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            d.BeginInvoke(() =>
            {
                _rebuildQueued = false;
                Rebuild();
                _ = RefreshDriftAsync();
            }, DispatcherPriority.Background);
        });
    }

    private void Rebuild()
    {
        if (_journal is null) return;
        var batches = _journal.Batches;
        var records = _journal.Records;
        var sid = UserScope.CurrentSid;

        ActiveCount = records.Count(r => !r.Reverted);
        RevertAllCount = records.Count(ChangeJournal.IsBulkRevertible);
        RevertedCount = records.Count(r => r.Reverted);
        TotalCount = records.Count;
        // Account names come from the cache (filled off the UI thread in RefreshDriftAsync): a SID lookup can block for seconds.
        var left = RestoreFormat.LeftText(records.Where(ChangeJournal.IsLeftByBulk), sid, UserScope.CachedAccountName);
        RevertAllHint = left.Length == 0 ? null : "Не входят в общий откат. " + left;

        BadgeText = ActiveCount > 0 ? ActiveCount.ToString() : null; // sidebar badge: the active changes the journal already holds
        RevertAllReason = RevertAllCount > 0 ? null
            : ActiveCount > 0 ? "Нечего откатывать общим откатом: оставшиеся записи откатываются по одной в списке."
            : "Нечего откатывать: активных изменений нет.";

        var covered = RestoreFormat.CoveredBy(records, sid);
        var titles = new Dictionary<Guid, string>();
        foreach (var r in records) titles.TryAdd(r.Id, r.Title);
        RecordItem Item(ChangeRecord r) => new(r, _drift.GetValueOrDefault(r.Id),
            RestoreFormat.RecordNote(r, sid, covered.GetValueOrDefault(r.Id), r.HandedTo is { } h ? titles.GetValueOrDefault(h) : null,
                UserScope.CachedAccountName),
            ChangeJournal.IsOtherUser(r, sid));

        var groups = RestoreFormat.Group(batches, records, Filter, _drift.Keys.ToHashSet(), ChangeJournal.IsBulkRevertible);
        // A long history (hundreds of batches) is drawn page by page: rendering it all at once froze the window for a second.
        Batches.Clear();
        foreach (var g in groups.Take(_shownBatches))
        {
            // Collapsed by default: the batch header carries the revert button; the records open with «ПОКАЗАТЬ».
            var item = new BatchItem(g, Item) { IsExpanded = _expanded.Contains(g.Batch.Id) || Filter == JournalFilter.Drifted };
            var id = g.Batch.Id;
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(BatchItem.IsExpanded)) return;
                if (item.IsExpanded) _expanded.Add(id); else _expanded.Remove(id);
            };
            Batches.Add(item);
        }
        var hidden = Math.Max(0, groups.Count - _shownBatches);
        HasMoreBatches = hidden > 0;
        ShowMoreText = $"ПОКАЗАТЬ ЕЩЁ ({Math.Min(hidden, BatchPage)} ИЗ {hidden})";

        var journalEmpty = batches.Count == 0 && records.Count == 0;
        IsFilterEmpty = Batches.Count == 0;
        FilterEmptyText = journalEmpty ? "Журнал пуст — MONOLITH ещё ничего не менял в системе." : Filter switch
        {
            JournalFilter.Active => "Активных изменений нет — система в исходном состоянии относительно MONOLITH.",
            JournalFilter.Drifted => "Все настройки MONOLITH на месте — вне MONOLITH ничего не изменено.",
            _ => "Откаченных изменений пока нет.",
        };
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RevertRecordCommand.NotifyCanExecuteChanged();
        RevertBatchCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
        CreateRestorePointCommand.NotifyCanExecuteChanged();
        ReapplyDriftCommand.NotifyCanExecuteChanged();
        ReapplyAllDriftCommand.NotifyCanExecuteChanged();
        EnableProtectionCommand.NotifyCanExecuteChanged();
    }

    // ═════════════════════════════ Drift ═════════════════════════════

    /// <summary>Live read of every active record off the UI thread; bursts of journal changes coalesce into one more run.</summary>
    private async Task RefreshDriftAsync()
    {
        if (_journal is null) return;
        if (_driftRunning)
        {
            _driftPending = true;
            return;
        }
        _driftRunning = true;
        try
        {
            do
            {
                _driftPending = false;
                var accepted = AppSettings.Current.DriftAccepted.ToList();
                var journal = _journal!;
                var list = await Task.Run(() =>
                {
                    ResolveAccountNames(journal.ActiveRecords);
                    return DriftDetector.DetectLive(accepted);
                });
                _drift = list.ToDictionary(d => d.Record.Id);
                DriftCount = list.Count;
                HasDrift = list.Count > 0;
                HasReapplicableDrift = list.Any(DriftReapply.CanReapply);
                DriftLine = list.Count == 0 ? "" :
                    "Windows или другая программа изменила эти настройки после MONOLITH.";
                if (!HasDrift && Filter == JournalFilter.Drifted) Filter = JournalFilter.All; // rebuilds
                else Rebuild();
            } while (_driftPending);
        }
        catch (Exception ex)
        {
            Log.Warn($"Проверка изменений вне MONOLITH не выполнена: {ex.Message}");
        }
        finally
        {
            _driftRunning = false;
        }
    }

    /// <summary>Fills the account-name cache for the SIDs the rows and dialogs show. Blocking (LSA lookups): off the UI thread.</summary>
    private static void ResolveAccountNames(IEnumerable<ChangeRecord> records)
    {
        var sid = UserScope.CurrentSid;
        if (sid is not null) UserScope.AccountName(sid);
        foreach (var other in records.Where(r => !r.Reverted && ChangeJournal.IsOtherUser(r, sid)).Select(r => r.UserSid!)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            UserScope.AccountName(other);
    }

    [RelayCommand]
    private void ShowDrifted() => Filter = JournalFilter.Drifted;

    [RelayCommand]
    private void ShowDriftOwner(RecordItem? item)
    {
        if (item?.Drift is not { OwnerPage: { } page } drift) return;
        // Open the owning page right at the item: the tweak row, the service, or the ЗАДАЧИ tab.
        var source = DriftDetector.LatestSource(drift.Record);
        if (drift.TweakId is { } tweakId) Ui.RequestFocus("optimize", tweakId);
        else if (source is DriftDetector.ServicesTaskSource or DriftDetector.StartupTaskSource)
        {
            Ui.RequestFocus("services", "tab:tasks");
            page = "services"; // not the "startup" alias: it would replace this request with "tab:startup"
        }
        else if (source == DriftDetector.ServicesSource && drift.Record.Service?.Name is { } service) Ui.RequestFocus("services", service);
        Ui.Navigate(page);
    }

    /// <summary>
    /// A row command's parameter is the row itself (a click) or its id as text (the replay after a relaunch with admin
    /// rights, see <see cref="PageViewModel.RequireAdmin(string, string, string?)"/>): both resolve to the same record.
    /// </summary>
    private RecordItem? ResolveRecord(object? parameter)
    {
        if (parameter is RecordItem item) return item;
        if (parameter is not string id || !Guid.TryParse(id, out var guid)) return null;
        RecordItem? Find() => Batches.SelectMany(b => b.Records).FirstOrDefault(r => r.Record.Id == guid);
        if (Find() is { } found) return found;
        if (_shownBatches < int.MaxValue) { _shownBatches = int.MaxValue; Rebuild(); } // the record is in a batch not drawn yet
        return Find();
    }

    private BatchItem? ResolveBatch(object? parameter)
    {
        if (parameter is BatchItem batch) return batch;
        if (parameter is not string id || !Guid.TryParse(id, out var guid)) return null;
        BatchItem? Find() => Batches.FirstOrDefault(b => b.Id == guid);
        if (Find() is { } found) return found;
        if (_shownBatches < int.MaxValue) { _shownBatches = int.MaxValue; Rebuild(); }
        return Find();
    }

    // Read-only mode keeps every button clickable: the click asks for admin rights and remembers the action (RequireAdmin).
    private bool CanReapplyDrift(object? parameter) => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanReapplyDrift))]
    private async Task ReapplyDriftAsync(object? parameter)
    {
        if (!RequireAdmin("Повторное применение настроек", "ReapplyDrift", (parameter as RecordItem)?.Record.Id.ToString() ?? parameter as string)) return;
        if (parameter is string) await EnsureDriftAsync(); // a replay right after the start: the live read may not be finished yet
        if (ResolveRecord(parameter)?.Drift is { } d) await ReapplyAsync([d]);
    }

    private bool CanReapplyAllDrift() => !IsBusy; // the button is shown only while there is something to re-apply

    [RelayCommand(CanExecute = nameof(CanReapplyAllDrift))]
    private async Task ReapplyAllDriftAsync()
    {
        if (!RequireAdmin("Повторное применение настроек", "ReapplyAllDrift")) return;
        await EnsureDriftAsync();
        await ReapplyAsync(_drift.Values.Where(DriftReapply.CanReapply).ToList());
    }

    /// <summary>Waits for the live «изменено вне MONOLITH» read (started at load) so a replayed command sees its result.</summary>
    private async Task EnsureDriftAsync()
    {
        await RefreshDriftAsync();
        for (var i = 0; i < 100 && _driftRunning; i++) await Task.Delay(100);
    }

    private Task ReapplyAsync(IReadOnlyList<DriftItem> items) => RunBusyAsync(OperationClass.JournalChange, "Повторное применение…", async () =>
    {
        if (items.Count == 0) return;
        var lines = DriftReapply.DescribeForConfirm(items, id => Monolith.Tweaks.TweakCatalog.Find(id)?.Title);
        if (!await Ui.ConfirmAsync("ПРИМЕНИТЬ СНОВА?",
                "MONOLITH снова запишет значения, которые он ставил раньше, отдельным пакетом. Откат этого пакета вернёт нынешние значения, а «ОТКАТИТЬ ВСЁ» — исходные (до MONOLITH).",
                lines, confirmText: "ПРИМЕНИТЬ СНОВА",
                footnote: "Если Windows снова сбросит настройку после обновления, MONOLITH покажет это при следующем запуске."))
            return;

        var result = await Task.Run(() => DriftReapply.ApplyAsync(items));
        if (result.Errors.Count == 0)
            Ui.Toast($"Применено снова: {result.Applied}.", ToastKind.Success);
        else
            await Ui.AlertAsync("Часть настроек не применена", "Остальные записаны; ошибки — ниже и в консоли.", result.Errors);
        await RefreshDriftAsync();
    });

    /// <summary>«ЗАБЫТЬ»: accept the new value. Only a per-user note; the journal record (and its revert) stays as is.</summary>
    [RelayCommand]
    private async Task ForgetDriftAsync(RecordItem? item)
    {
        if (item?.Drift is not { } d || _journal is null) return;
        var journal = _journal;
        var kept = await Task.Run(() => DriftDetector.PruneIgnored(AppSettings.Current.DriftAccepted, journal.ActiveRecords));
        kept.Add(d.IgnoreKey);
        AppSettings.Current.DriftAccepted = kept; // new list → PropertyChanged → saved
        Ui.Toast($"«{item.Title}»: новое значение принято. Откат по-прежнему вернёт исходное значение (до MONOLITH).", ToastKind.Info);
        await RefreshDriftAsync();
    }

    // ═════════════════════════════ System Restore ═════════════════════════════

    private async Task RefreshProtectionAsync()
    {
        var (enabled, blocked) = await Task.Run(() => (RestorePointService.IsEnabled(), RestorePointService.IsBlockedByPolicy));
        ProtectionEnabled = enabled;
        ProtectionBlocked = blocked;
        CanOfferEnable = !enabled && !blocked;
        var drive = SystemProfile.SystemDrive.TrimEnd('\\');
        (ProtectionTitle, ProtectionText) = (enabled, blocked) switch
        {
            (_, true) => ("ЗАПРЕЩЕНА", "Защита системы отключена групповой политикой. Включить её может только администратор организации; до этого откат возможен только через журнал MONOLITH."),
            (true, _) => ("ВКЛЮЧЕНА", $"Windows хранит точки восстановления для диска {drive} — через них можно вернуть системные файлы, реестр и драйверы к прежнему состоянию."),
            _ => ("ВЫКЛЮЧЕНА", $"Точки восстановления для диска {drive} не создаются. Изменения MONOLITH всё равно откатываются через журнал, но страховки на уровне Windows нет."),
        };
        NotifyCommands();
        await LoadPointsAsync();
    }

    private async Task LoadPointsAsync()
    {
        if (!Elevation.IsElevated)
        {
            RestorePoints.Clear();
            PointsCountText = "";
            PointsMessage = "Windows показывает список точек только администратору. Кнопка ниже перезапустит MONOLITH с правами и откроет список здесь; можно и открыть «Восстановление системы».";
            return;
        }

        PointsLoading = true;
        try
        {
            var list = await RestorePointService.ListAsync();
            RestorePoints.Clear();
            foreach (var p in list) RestorePoints.Add(new RestorePointItem(p));
            PointsCountText = list.Count.ToString();
            PointsMessage = list.Count > 0 ? "" : ProtectionEnabled
                ? "Точек восстановления пока нет. Создайте первую кнопкой «СОЗДАТЬ ТОЧКУ» вверху страницы."
                : "Точек восстановления нет — защита системы выключена.";
        }
        finally
        {
            PointsLoading = false;
        }
    }

    /// <summary>Buttons stay clickable in read-only mode (the click asks for rights, see RequireAdmin); only a running job blocks them.</summary>
    private bool CanAdmin() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAdmin))]
    private Task CreateRestorePointAsync() => RunBusyAsync(OperationClass.Disruptive, "Создание точки восстановления…", async () =>
    {
        if (!RequireAdmin("Создание точки восстановления", "CreateRestorePoint")) return;
        if (ProtectionBlocked)
        {
            await Ui.AlertAsync("Защита системы запрещена", "Групповая политика запрещает точки восстановления на этом компьютере.");
            return;
        }
        if (!ProtectionEnabled)
        {
            if (!await Ui.ConfirmAsync("Защита системы выключена", "Чтобы создать точку восстановления, сначала нужно включить защиту системы для системного диска.",
                    ["Windows выделит под точки восстановления часть места на диске; размер можно изменить в «Защите системы»"],
                    confirmText: "ВКЛЮЧИТЬ И СОЗДАТЬ"))
                return;
            if (!await RestorePointService.EnableAsync())
            {
                Ui.Toast("Не удалось включить защиту системы — подробности в консоли.", ToastKind.Error);
                return;
            }
            await RefreshProtectionAsync();
        }

        var result = await RestorePointService.CreateAsync("MONOLITH: точка, созданная вручную");
        Ui.Toast(result.Message, result.Outcome switch
        {
            RestorePointOutcome.Created => ToastKind.Success,
            RestorePointOutcome.Skipped => ToastKind.Info,
            _ => ToastKind.Error,
        }, "Точка восстановления");
        await LoadPointsAsync();
    });

    private bool CanEnable() => !IsBusy && CanOfferEnable;

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableProtectionAsync() => RunBusyAsync(OperationClass.Disruptive, "Включение защиты системы…", async () =>
    {
        if (!RequireAdmin("Включение защиты системы", "EnableProtection")) return;
        var drive = SystemProfile.SystemDrive.TrimEnd('\\');
        if (!await Ui.ConfirmAsync("Включить защиту системы?",
                $"Windows начнёт создавать точки восстановления для диска {drive} — перед обновлениями, установкой драйверов и изменениями MONOLITH.",
                [
                    $"Защита включается для диска {drive} (то же, что «Защита системы» → «Настроить» → «Включить защиту»)",
                    "Под точки выделяется часть места на диске; старые точки Windows удаляет сама, когда место заканчивается",
                    "Отключить защиту можно в любой момент кнопкой «Настроить»",
                ],
                confirmText: "ВКЛЮЧИТЬ"))
            return;

        if (await RestorePointService.EnableAsync())
            Ui.Toast($"Защита системы включена для диска {drive}.", ToastKind.Success);
        else
            Ui.Toast("Не удалось включить защиту системы — подробности в консоли.", ToastKind.Error);
        await RefreshProtectionAsync();
    });

    [RelayCommand]
    private void ConfigureProtection() => RestorePointService.OpenSystemProtection();

    [RelayCommand]
    private void OpenSystemRestore() => RestorePointService.OpenSystemRestoreWizard();

    [RelayCommand]
    private void OpenBackups() => ShellLauncher.OpenFolder(AppPaths.BackupsDir, "Резервные копии");

    [RelayCommand]
    private Task RefreshPointsAsync() => RefreshProtectionAsync();

    /// <summary>Windows lists restore points only to an administrator: the button next to that message relaunches with rights and reads the list.</summary>
    [RelayCommand]
    private async Task ShowPointsAsync()
    {
        if (!RequireAdmin("Список точек восстановления", "ShowPoints")) return;
        await RefreshProtectionAsync();
    }

    /// <summary>
    /// «Точка перед изменениями» (AppSettings.CreateRestorePoints, per user): turning it off asks first; turning it on is immediate.
    /// </summary>
    public bool CreateRestorePoints
    {
        get => AppSettings.Current.CreateRestorePoints;
        set
        {
            if (value == AppSettings.Current.CreateRestorePoints) return;
            if (value)
            {
                AppSettings.Current.CreateRestorePoints = true;
                return;
            }
            // Keep the toggle on until the user confirms (re-raised after the binding finishes its update).
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                OnPropertyChanged(nameof(CreateRestorePoints));
                await ConfirmDisableRestorePointsAsync();
            });
        }
    }

    private async Task ConfirmDisableRestorePointsAsync()
    {
        if (_confirmingRestorePoints) return;
        _confirmingRestorePoints = true;
        try
        {
            var ok = await Ui.ConfirmAsync(
                "Не создавать точки восстановления?",
                "MONOLITH перестанет создавать точку восстановления Windows перед пакетами изменений.",
                [
                    "Журнал отката продолжит работать: каждое изменение по-прежнему можно откатить на этой странице",
                    "Пропадёт вторая страховка: если система не загрузится, вернуться будет не к чему",
                    "Пакеты изменений будут применяться быстрее — без паузы на создание точки",
                    "Включить обратно можно здесь же в любой момент",
                ],
                confirmText: "ВЫКЛЮЧИТЬ", danger: true);
            if (ok)
            {
                AppSettings.Current.CreateRestorePoints = false;
                Log.Warn("Создание точек восстановления перед изменениями выключено.");
            }
        }
        finally
        {
            _confirmingRestorePoints = false;
            OnPropertyChanged(nameof(CreateRestorePoints));
        }
    }

    // ═════════════════════════════ Revert ═════════════════════════════

    private bool CanRevertRecord(object? parameter) => CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertRecord))]
    private Task RevertRecordAsync(object? parameter) => RunBusyAsync(OperationClass.JournalChange, "Откат изменения…", async () =>
    {
        if (_journal is null || !RequireAdmin("Откат изменения", "RevertRecord", (parameter as RecordItem)?.Record.Id.ToString() ?? parameter as string)) return;
        if (ResolveRecord(parameter) is not { CanRevert: true } item) return;
        var journal = _journal;
        var record = item.Record;
        // Research services.md §3.5: show the real current value and warn when it changed after MONOLITH.
        var (live, newer, shadow, bottom, account) = await Task.Run(() =>
        {
            var n = journal.NewerLayer(record);
            var s = n is null ? journal.ShadowedBy(record) : null;
            // The oldest active change of the target: its original is the value from before MONOLITH.
            var first = ReferenceEquals(journal.ActiveLayers(record.Target).FirstOrDefault(), record);
            var me = record.IsLegacyPerUser && UserScope.CurrentSid is { } sid ? UserScope.AccountName(sid) : null;
            return (LiveState.Read(record, isTop: n is null && s is null), n, s, first, me);
        });
        if (shadow is not null)
        {
            await Ui.AlertAsync("Сначала откатите более позднее изменение",
                $"«{item.Title}» записано старой версией MONOLITH без учётной записи. Сейчас это значение в профиле текущей учётной записи " +
                $"задаёт «{shadow.Title}», и откат старой записи заменил бы его. Сначала откатите «{shadow.Title}».");
            return;
        }
        var lines = new List<string>
        {
            $"{item.KindLabel}: {item.Target}",
            live.Drifted is null && newer is null && item.NewValue is { Length: > 0 } applied ? $"Установлено MONOLITH: {applied}" : $"Сейчас: {live.Text}",
            newer is null ? $"Станет: {item.OldValue}" : $"Останется как сейчас; исходное «{item.OldValue}» перейдёт к «{newer.Title}»",
        };
        if (live.IsDrifted)
            lines.Add($"После MONOLITH значение изменили (Windows, политика или вы): MONOLITH ставил «{item.NewValue}». Откат заменит текущее значение исходным.");
        if (account is not null)
            lines.Add($"Запись старой версии без учётной записи: исходное значение запишется в профиль {account}. Если изменение делали под другой учётной записью, у неё оно останется.");
        if (record.IsLegacyMerged)
            lines.Add($"Также откатит: {string.Join(", ", record.AlsoSources.Distinct().Select(RestoreFormat.SourceTitle))} — они меняли тот же параметр.");
        var message = newer is not null
            ? $"Сейчас значение задаёт более позднее изменение «{newer.Title}». Откат «{item.Title}» систему не изменит: исходное значение «{item.OldValue}» перейдёт к нему, и его откат вернёт " +
              (bottom ? "то, что было до MONOLITH." : $"значение, которое было до «{item.Title}».")
            : live.IsDrifted
                ? $"«{item.Title}» изменено вне MONOLITH. Если откатить, вернётся " +
                  (bottom ? "значение, которое было до MONOLITH" : $"значение, которое было до этого изменения («{item.OldValue}»)") +
                  ", а более позднее изменение пропадёт."
                : $"«{item.Title}» вернётся к значению, которое было до этого изменения.";
        if (!await Ui.ConfirmAsync("Откатить изменение?", message, lines, confirmText: "ОТКАТИТЬ", danger: live.IsDrifted))
            return;

        var result = await Task.Run(() => journal.RevertAsync([record], LiveState.VerifyRevert));
        await ReportAsync(result, newer is null ? "Изменение откачено." : $"Изменение откачено; значение остаётся за «{newer.Title}».");
    });

    private bool CanRevertBatch(object? parameter) => CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertBatch))]
    private Task RevertBatchAsync(object? parameter) => RunBusyAsync(OperationClass.JournalChange, "Откат пакета…", async () =>
    {
        if (_journal is null || !RequireAdmin("Откат пакета", "RevertBatch", (parameter as BatchItem)?.Id.ToString() ?? parameter as string)) return;
        if (ResolveBatch(parameter) is not { CanRevert: true } batch) return;
        var journal = _journal;
        var inBatch = journal.Records.Where(r => !r.Reverted && (batch.IsSynthetic
            ? journal.Batches.All(b => b.Id != r.BatchId)
            : r.BatchId == batch.Id)).ToList();
        var active = inBatch.Where(ChangeJournal.IsBulkRevertible).ToList();
        if (active.Count == 0) return;
        var left = await LeftTextAsync(inBatch.Where(ChangeJournal.IsLeftByBulk).ToList());

        var (lines, drifted, kept) = await DescribeLiveAsync(journal, active);
        if (!await Ui.ConfirmAsync("Откатить пакет?",
                $"«{batch.Title}» — {active.Count} {RestoreFormat.Plural(active.Count, "изменение откатится", "изменения откатятся", "изменений откатятся")}, от последнего к первому." +
                KeptSentence(kept) + DriftSentence(drifted) + (left.Length > 0 ? " Не войдут в откат: " + left : ""),
                lines,
                confirmText: "ОТКАТИТЬ ПАКЕТ", danger: drifted > 0))
            return;

        var result = await Task.Run(() => journal.RevertAsync(active, LiveState.VerifyRevert));
        await ReportAsync(result, $"Пакет «{batch.Title}» откачен.", left);
    });

    /// <summary>
    /// «ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ» (the title-bar shortcut and the page's pinned button): the newest batch that still has
    /// changes to revert, through the ordinary batch revert with its confirmation. Nothing to revert = a toast.
    /// </summary>
    [RelayCommand]
    private async Task RevertLastBatchAsync()
    {
        // Read-only mode: relaunch with rights and come back to exactly this action.
        if (!RequireAdmin("Откат последнего пакета", "RevertLastBatch")) return;
        if (_journal is null) return;
        if (Filter != JournalFilter.All && Filter != JournalFilter.Active) Filter = JournalFilter.Active; // rebuilds
        var last = Batches.FirstOrDefault(b => b.CanRevert && !b.IsSynthetic) ?? Batches.FirstOrDefault(b => b.CanRevert);
        if (last is null)
        {
            Ui.Toast("Откатывать нечего: активных пакетов изменений нет.", ToastKind.Info);
            return;
        }
        if (RevertBatchCommand.CanExecute(last)) await RevertBatchCommand.ExecuteAsync(last);
        MarkChecked();
    }

    // Grey only when there is nothing to revert — the reason is the text next to the button (RevertAllNote).
    private bool CanRevertAll() => RevertAllCount > 0 && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private Task RevertAllAsync() => RunBusyAsync(OperationClass.JournalChange, "Откат всех изменений…", async () =>
    {
        if (_journal is null || !RequireAdmin("Откат всех изменений", "RevertAll")) return;
        var journal = _journal;
        var active = journal.RevertAllCandidates().Reverse().ToList();
        if (active.Count == 0) return;
        var left = await LeftTextAsync(journal.ActiveRecords.Where(ChangeJournal.IsLeftByBulk).ToList());

        var (lines, drifted, _) = await DescribeLiveAsync(journal, active);
        if (!await Ui.ConfirmAsync("Откатить все изменения?",
                $"{active.Count} {RestoreFormat.Plural(active.Count, "активное изменение MONOLITH вернётся", "активных изменения MONOLITH вернутся", "активных изменений MONOLITH вернутся")} к исходным значениям — от последнего к первому." +
                DriftSentence(drifted) + (left.Length > 0 ? " Не войдут в откат: " + left : "") +
                " Удалённые файлы, установленные драйверы и программы, сброс сети этим не отменяются.",
                lines,
                confirmText: "ОТКАТИТЬ ВСЁ", danger: true,
                footnote: "Для некоторых изменений нужна перезагрузка."))
            return;

        var result = await Task.Run(() => journal.RevertAllAsync(LiveState.VerifyRevert));
        // What was actually left after the revert (the journal picks the records again under its gate).
        await ReportAsync(result, "Все изменения откачены.", await LeftTextAsync(result.Left));
    });

    /// <summary>Why records stay active (<see cref="RestoreFormat.LeftText"/>), with account names resolved off the UI thread.</summary>
    private static Task<string> LeftTextAsync(IReadOnlyList<ChangeRecord> left) => left.Count == 0
        ? Task.FromResult("")
        : Task.Run(() => RestoreFormat.LeftText(left, UserScope.CurrentSid, UserScope.AccountName));

    private static string Describe(ChangeRecord r) =>
        $"{r.Title} · {RestoreFormat.DescribeTarget(r.Target)}: «{r.NewValue}» → «{r.OldValue}»";

    /// <summary>
    /// Dialog lines with a live read per record, newest first; drifted ones are flagged (research services.md §3.5).
    /// A record whose target a newer change outside the selection now sets is listed as kept: its revert hands the
    /// original over to that change and leaves the system as it is.
    /// </summary>
    private static async Task<(List<string> Lines, int Drifted, int Kept)> DescribeLiveAsync(ChangeJournal journal, IReadOnlyList<ChangeRecord> records)
    {
        var selected = new HashSet<ChangeRecord>(records, ChangeJournal.ByReference);
        var rows = await Task.Run(() => records.Select(r =>
        {
            // The first newer layer that is not reverted together with this record keeps the value.
            var keeper = journal.ActiveLayers(r.Target).SkipWhile(x => !ReferenceEquals(x, r)).Skip(1)
                .FirstOrDefault(x => !selected.Contains(x));
            var isTop = journal.NewerLayer(r) is null;
            return (Live: LiveState.Read(r, isTop), Keeper: keeper);
        }).ToList());
        var lines = new List<string>(records.Count);
        int drifted = 0, kept = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            var (live, keeper) = rows[i];
            if (keeper is not null)
            {
                kept++;
                lines.Add($"{r.Title} · {RestoreFormat.DescribeTarget(r.Target)}: остаётся «{live.Text}» — его задаёт более позднее «{keeper.Title}», исходное значение перейдёт к нему");
            }
            else if (live.IsDrifted)
            {
                drifted++;
                lines.Add($"ИЗМЕНЕНО ВНЕ MONOLITH · {r.Title} · {RestoreFormat.DescribeTarget(r.Target)}: сейчас «{live.Text}» (MONOLITH ставил «{r.NewValue}») → «{r.OldValue}»");
            }
            else
            {
                lines.Add(Describe(r));
            }
        }
        return (lines, drifted, kept);
    }

    private static string DriftSentence(int drifted) => drifted == 0
        ? ""
        : $" {drifted} {RestoreFormat.Plural(drifted, "значение изменено", "значения изменены", "значений изменено")} после MONOLITH (Windows, политикой или вами) — откат заменит и {(drifted == 1 ? "его" : "их")} исходным значением; такие пункты отмечены «ИЗМЕНЕНО ВНЕ MONOLITH».";

    private static string KeptSentence(int kept) => kept == 0
        ? ""
        : $" {kept} {RestoreFormat.Plural(kept, "параметр", "параметра", "параметров")} позже изменил другой пакет — {(kept == 1 ? "он останется" : "они останутся")} как есть, а исходное значение перейдёт к тому изменению.";

    /// <summary>
    /// The outcome of a revert. <paramref name="success"/> is shown only when everything in scope was reverted;
    /// <paramref name="left"/> (<see cref="RestoreFormat.LeftText"/>) names what stayed active on purpose.
    /// </summary>
    private static async Task ReportAsync(RevertResult result, string success, string left = "")
    {
        var check = result.VerificationText is { } v ? " " + v : "";
        var counts = RestoreFormat.RevertCounts(result);
        if (!result.JournalSaved)
            Ui.Toast("Откат выполнен, но журнал не удалось сохранить на диск — после перезапуска эти записи могут снова показаться активными.",
                ToastKind.Warning, "ЖУРНАЛ НЕ СОХРАНЁН");
        if (result.Success && result.Mismatches.Count == 0)
        {
            if (left.Length == 0)
                Ui.Toast((result.Reverted > 1 ? $"{success} {counts}" : success) + check, ToastKind.Success);
            else
                Ui.Toast($"{counts}{check} {left}", ToastKind.Warning, "ОТКАЧЕНО НЕ ВСЁ");
            return;
        }
        Ui.Toast(result.Success ? $"{counts}{check}" : $"{counts} Ошибок: {result.Failed}.{check}", ToastKind.Warning);
        var tail = left.Length > 0 ? " " + left : "";
        if (!result.Success)
            await Ui.AlertAsync("Часть изменений не откачена",
                "Эти записи остались активными — их можно откатить повторно после перезагрузки или вернуть точкой восстановления Windows." + tail,
                result.Errors.Concat(result.Mismatches.Select(m => "НЕ СОВПАДАЕТ · " + m)));
        else
            await Ui.AlertAsync("Откат не совпал с исходным",
                "После отката MONOLITH прочитал значения заново, и эти отличаются от записанных до изменения: их могла сразу поменять Windows, политика или другая программа. Строки отмечены «НЕ СОВПАДАЕТ»." + tail,
                result.Mismatches);
    }
}
