using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Tools;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Restore;

/// <summary>
/// The undo center: every change Lycoris made (journal), reverting one record / one batch / everything,
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
    public override string Title => Loc.T("ВОССТАНОВЛЕНИЕ");
    public override string Subtitle => Loc.T("Журнал изменений Lycoris, откат и точки восстановления Windows.");
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

    // ── drift: active records changed outside Lycoris (competitors.md §3 must-have #5) ──
    [ObservableProperty] private int _driftCount;
    [ObservableProperty] private bool _hasDrift;
    [ObservableProperty] private bool _hasReapplicableDrift;
    [ObservableProperty] private string _driftLine = "";

    // ── system restore ──
    [ObservableProperty] private bool _protectionEnabled;
    [ObservableProperty] private bool _protectionBlocked;
    [ObservableProperty] private string _protectionTitle = "—";
    [ObservableProperty] private string _protectionText = Loc.T("Чтение состояния защиты системы…");
    [ObservableProperty] private bool _canOfferEnable;
    [ObservableProperty] private bool _pointsLoading;
    [ObservableProperty] private string _pointsMessage = "";
    [ObservableProperty] private string _pointsCountText = "";

    public string BackupsPath => AppPaths.BackupsReadDir;

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
    private Task RefreshAsync() => RunBusyAsync(OperationClass.Measure, Loc.T("Обновление…"), async () =>
    {
        Rebuild();
        _ = RefreshDriftAsync();
        await RefreshProtectionAsync();
        MarkChecked();
    });

    // ═════════════════════════════ Focus («section:…» from the palette / other pages) ═════════════════════════════

    /// <summary>Section the view scrolls to: last-batch, journal, restore-points, drift (one-shot, see RestoreView).</summary>
    [ObservableProperty] private string? _focusedSection;
    [ObservableProperty] private string? _focusedRecordId;

    /// <summary>«section:&lt;name&gt;»: opens the part of the page (last batch expanded, drift filter) and asks the view to scroll to it.</summary>
    private void ApplyFocus(string? focusId)
    {
        const string prefix = CommandPaletteIndex.RestoreSectionPrefix;
        if (focusId is null || !focusId.StartsWith(prefix, StringComparison.Ordinal)) return;
        var section = focusId[prefix.Length..];
        if (section.StartsWith("record:", StringComparison.Ordinal) && Guid.TryParse(section[7..], out var recordId))
        {
            if (Filter != JournalFilter.All) Filter = JournalFilter.All;
            if (_shownBatches < int.MaxValue) { _shownBatches = int.MaxValue; Rebuild(); }
            var batch = Batches.FirstOrDefault(b => b.Records.Any(r => r.Record.Id == recordId));
            if (batch is not null) batch.IsExpanded = true;
            FocusedRecordId = recordId.ToString();
            FocusedSection = null;
            FocusedSection = "journal";
            return;
        }
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
        RevertAllHint = left.Length == 0 ? null : Loc.Tf("Не входят в общий откат. {0}", left);

        BadgeText = ActiveCount > 0 ? ActiveCount.ToString() : null; // sidebar badge: the active changes the journal already holds
        RevertAllReason = RevertAllCount > 0 ? null
            : ActiveCount > 0 ? Loc.T("Нечего откатывать общим откатом: оставшиеся записи откатываются по одной в списке.")
            : Loc.T("Нечего откатывать: активных изменений нет.");

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
        ShowMoreText = Loc.Tf("ПОКАЗАТЬ ЕЩЁ ({0} ИЗ {1})", Math.Min(hidden, BatchPage), hidden);

        var journalEmpty = batches.Count == 0 && records.Count == 0;
        IsFilterEmpty = Batches.Count == 0;
        FilterEmptyText = journalEmpty ? Loc.T("Журнал пуст — Lycoris ещё ничего не менял в системе.") : Filter switch
        {
            JournalFilter.Active => Loc.T("Активных изменений нет — система в исходном состоянии относительно Lycoris."),
            JournalFilter.Drifted => Loc.T("Все настройки Lycoris на месте — вне Lycoris ничего не изменено."),
            _ => Loc.T("Откаченных изменений пока нет."),
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
                    Loc.T("Windows или другая программа изменила эти настройки после Lycoris.");
                if (!HasDrift && Filter == JournalFilter.Drifted) Filter = JournalFilter.All; // rebuilds
                else Rebuild();
            } while (_driftPending);
        }
        catch (Exception ex)
        {
            Log.Warn("Проверка изменений вне Lycoris не выполнена: {0}", ex.Message);
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
        if (!RequireAdmin(Loc.T("Повторное применение настроек"), "ReapplyDrift", (parameter as RecordItem)?.Record.Id.ToString() ?? parameter as string)) return;
        if (parameter is string) await EnsureDriftAsync(); // a replay right after the start: the live read may not be finished yet
        if (ResolveRecord(parameter)?.Drift is { } d) await ReapplyAsync([d]);
    }

    private bool CanReapplyAllDrift() => !IsBusy; // the button is shown only while there is something to re-apply

    [RelayCommand(CanExecute = nameof(CanReapplyAllDrift))]
    private async Task ReapplyAllDriftAsync()
    {
        if (!RequireAdmin(Loc.T("Повторное применение настроек"), "ReapplyAllDrift")) return;
        await EnsureDriftAsync();
        await ReapplyAsync(_drift.Values.Where(DriftReapply.CanReapply).ToList());
    }

    /// <summary>Waits for the live «изменено вне Lycoris» read (started at load) so a replayed command sees its result.</summary>
    private async Task EnsureDriftAsync()
    {
        await RefreshDriftAsync();
        for (var i = 0; i < 100 && _driftRunning; i++) await Task.Delay(100);
    }

    private Task ReapplyAsync(IReadOnlyList<DriftItem> items) => RunBusyAsync(OperationClass.JournalChange, Loc.T("Повторное применение…"), async () =>
    {
        if (items.Count == 0) return;
        var lines = DriftReapply.DescribeForConfirm(items, id => Lycoris.Tweaks.TweakCatalog.Find(id)?.Title);
        if (!await Ui.ConfirmAsync("ПРИМЕНИТЬ СНОВА?",
                Loc.T("Lycoris снова запишет значения, которые он ставил раньше, отдельным пакетом. Откат этого пакета вернёт нынешние значения, а «ОТКАТИТЬ ВСЁ» — исходные (до Lycoris)."),
                lines, confirmText: Loc.T("ПРИМЕНИТЬ СНОВА"),
                footnote: Loc.T("Если Windows снова сбросит настройку после обновления, Lycoris покажет это при следующем запуске.")))
            return;

        var result = await Task.Run(() => DriftReapply.ApplyAsync(items));
        if (result.Errors.Count == 0)
            Ui.Toast(Loc.Tf("Применено снова: {0}.", result.Applied), ToastKind.Success);
        else
            await Ui.AlertAsync("Часть настроек не применена", Loc.T("Остальные записаны; ошибки — ниже и в консоли."), result.Errors);
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
        Ui.Toast(Loc.Tf("«{0}»: новое значение принято. Откат по-прежнему вернёт исходное значение (до Lycoris).", Loc.Sink(item.Title)), ToastKind.Info);
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
            (_, true) => (Loc.T("ЗАПРЕЩЕНА"), Loc.T("Защита системы отключена групповой политикой. Включить её может только администратор организации; до этого откат возможен только через журнал Lycoris.")),
            (true, _) => (Loc.T("ВКЛЮЧЕНА"), Loc.Tf("Windows хранит точки восстановления для диска {0} — через них можно вернуть системные файлы, реестр и драйверы к прежнему состоянию.", drive)),
            _ => (Loc.T("ВЫКЛЮЧЕНА"), Loc.Tf("Точки восстановления для диска {0} не создаются. Изменения Lycoris всё равно откатываются через журнал, но страховки на уровне Windows нет.", drive)),
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
            PointsMessage = Loc.T("Windows показывает список точек только администратору. Кнопка ниже перезапустит Lycoris с правами и откроет список здесь; можно и открыть «Восстановление системы».");
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
                ? Loc.T("Точек восстановления пока нет. Создайте первую кнопкой «СОЗДАТЬ ТОЧКУ» вверху страницы.")
                : Loc.T("Точек восстановления нет — защита системы выключена.");
        }
        finally
        {
            PointsLoading = false;
        }
    }

    /// <summary>Buttons stay clickable in read-only mode (the click asks for rights, see RequireAdmin); only a running job blocks them.</summary>
    private bool CanAdmin() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAdmin))]
    private Task CreateRestorePointAsync() => RunBusyAsync(OperationClass.Disruptive, Loc.T("Создание точки восстановления…"), async () =>
    {
        if (!RequireAdmin(Loc.T("Создание точки восстановления"), "CreateRestorePoint")) return;
        if (ProtectionBlocked)
        {
            await Ui.AlertAsync("Защита системы запрещена", Loc.T("Групповая политика запрещает точки восстановления на этом компьютере."));
            return;
        }
        if (!ProtectionEnabled)
        {
            if (!await Ui.ConfirmAsync("Защита системы выключена", Loc.T("Чтобы создать точку восстановления, сначала нужно включить защиту системы для системного диска."),
                    [Loc.T("Windows выделит под точки восстановления часть места на диске; размер можно изменить в «Защите системы»")],
                    confirmText: Loc.T("ВКЛЮЧИТЬ И СОЗДАТЬ")))
                return;
            if (!await RestorePointService.EnableAsync())
            {
                Ui.Toast("Не удалось включить защиту системы — подробности в консоли.", ToastKind.Error);
                return;
            }
            await RefreshProtectionAsync();
        }

        var result = await RestorePointService.CreateAsync(Loc.T("Lycoris: точка, созданная вручную"));
        Ui.Toast(result.Message, result.Outcome switch
        {
            RestorePointOutcome.Created => ToastKind.Success,
            RestorePointOutcome.Skipped => ToastKind.Info,
            _ => ToastKind.Error,
        }, Loc.T("Точка восстановления"));
        await LoadPointsAsync();
    });

    private bool CanEnable() => !IsBusy && CanOfferEnable;

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableProtectionAsync() => RunBusyAsync(OperationClass.Disruptive, Loc.T("Включение защиты системы…"), async () =>
    {
        if (!RequireAdmin(Loc.T("Включение защиты системы"), "EnableProtection")) return;
        var drive = SystemProfile.SystemDrive.TrimEnd('\\');
        if (!await Ui.ConfirmAsync("Включить защиту системы?",
                Loc.Tf("Windows начнёт создавать точки восстановления для диска {0} — перед обновлениями, установкой драйверов и изменениями Lycoris.", drive),
                [
                    Loc.Tf("Защита включается для диска {0} (то же, что «Защита системы» → «Настроить» → «Включить защиту»)", drive),
                    Loc.T("Под точки выделяется часть места на диске; старые точки Windows удаляет сама, когда место заканчивается"),
                    Loc.T("Отключить защиту можно в любой момент кнопкой «Настроить»"),
                ],
                confirmText: Loc.T("ВКЛЮЧИТЬ")))
            return;

        if (await RestorePointService.EnableAsync())
            Ui.Toast(Loc.Tf("Защита системы включена для диска {0}.", drive), ToastKind.Success);
        else
            Ui.Toast("Не удалось включить защиту системы — подробности в консоли.", ToastKind.Error);
        await RefreshProtectionAsync();
    });

    [RelayCommand]
    private void ConfigureProtection() => RestorePointService.OpenSystemProtection();

    [RelayCommand]
    private void OpenSystemRestore() => RestorePointService.OpenSystemRestoreWizard();

    [RelayCommand]
    private void OpenBackups() => ShellLauncher.OpenFolder(AppPaths.BackupsReadDir, Loc.T("Резервные копии"));

    [RelayCommand]
    private Task RefreshPointsAsync() => RefreshProtectionAsync();

    /// <summary>Windows lists restore points only to an administrator: the button next to that message relaunches with rights and reads the list.</summary>
    [RelayCommand]
    private async Task ShowPointsAsync()
    {
        if (!RequireAdmin(Loc.T("Список точек восстановления"), "ShowPoints")) return;
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
                Loc.T("Lycoris перестанет создавать точку восстановления Windows перед пакетами изменений."),
                [
                    Loc.T("Журнал отката продолжит работать: каждое изменение по-прежнему можно откатить на этой странице"),
                    Loc.T("Пропадёт вторая страховка: если система не загрузится, вернуться будет не к чему"),
                    Loc.T("Пакеты изменений будут применяться быстрее — без паузы на создание точки"),
                    Loc.T("Включить обратно можно здесь же в любой момент"),
                ],
                confirmText: Loc.T("ВЫКЛЮЧИТЬ"), danger: true);
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
    private Task RevertRecordAsync(object? parameter) => RunBusyAsync(OperationClass.JournalChange, Loc.T("Откат изменения…"), async () =>
    {
        if (_journal is null || !RequireAdmin(Loc.T("Откат изменения"), "RevertRecord", (parameter as RecordItem)?.Record.Id.ToString() ?? parameter as string)) return;
        if (ResolveRecord(parameter) is not { CanRevert: true } item) return;
        var journal = _journal;
        var record = item.Record;
        // Research services.md §3.5: show the real current value and warn when it changed after Lycoris.
        var (live, newer, shadow, bottom, account) = await Task.Run(() =>
        {
            var n = journal.NewerLayer(record);
            var s = n is null ? journal.ShadowedBy(record) : null;
            // The oldest active change of the target: its original is the value from before Lycoris.
            var first = ReferenceEquals(journal.ActiveLayers(record.Target).FirstOrDefault(), record);
            var me = record.IsLegacyPerUser && UserScope.CurrentSid is { } sid ? UserScope.AccountName(sid) : null;
            return (LiveState.Read(record, isTop: n is null && s is null), n, s, first, me);
        });
        if (shadow is not null)
        {
            await Ui.AlertAsync("Сначала откатите более позднее изменение",
                Loc.Tf("«{0}» записано старой версией Lycoris без учётной записи. Сейчас это значение в профиле текущей учётной записи " +
                "задаёт «{1}», и откат старой записи заменил бы его. Сначала откатите «{1}».", Loc.Sink(item.Title), Loc.Sink(shadow.Title)));
            return;
        }
        var lines = new List<string>
        {
            $"{item.KindLabel}: {item.Target}",
            live.Drifted is null && newer is null && Loc.Sink(item.NewValue) is { Length: > 0 } applied ? Loc.Tf("Установлено Lycoris: {0}", applied) : Loc.Tf("Сейчас: {0}", Loc.Sink(live.Text)),
            newer is null ? Loc.Tf("Станет: {0}", Loc.Sink(item.OldValue)) : Loc.Tf("Останется как сейчас; исходное «{0}» перейдёт к «{1}»", Loc.Sink(item.OldValue), Loc.Sink(newer.Title)),
        };
        if (live.IsDrifted)
            lines.Add(Loc.Tf("После Lycoris значение изменили (Windows, политика или вы): Lycoris ставил «{0}». Откат заменит текущее значение исходным.", Loc.Sink(item.NewValue)));
        if (account is not null)
            lines.Add(Loc.Tf("Запись старой версии без учётной записи: исходное значение запишется в профиль {0}. Если изменение делали под другой учётной записью, у неё оно останется.", account));
        if (record.IsLegacyMerged)
            lines.Add(Loc.Tf("Также откатит: {0} — они меняли тот же параметр.", string.Join(", ", record.AlsoSources.Distinct().Select(RestoreFormat.SourceTitle))));
        var message = newer is not null
            ? (bottom
                ? Loc.Tf("Сейчас значение задаёт более позднее изменение «{0}». Откат «{1}» систему не изменит: исходное значение «{2}» перейдёт к нему, и его откат вернёт то, что было до Lycoris.", Loc.Sink(newer.Title), Loc.Sink(item.Title), Loc.Sink(item.OldValue))
                : Loc.Tf("Сейчас значение задаёт более позднее изменение «{0}». Откат «{1}» систему не изменит: исходное значение «{2}» перейдёт к нему, и его откат вернёт значение, которое было до «{1}».", Loc.Sink(newer.Title), Loc.Sink(item.Title), Loc.Sink(item.OldValue)))
            : live.IsDrifted
                ? (bottom
                    ? Loc.Tf("«{0}» изменено вне Lycoris. Если откатить, вернётся значение, которое было до Lycoris, а более позднее изменение пропадёт.", Loc.Sink(item.Title))
                    : Loc.Tf("«{0}» изменено вне Lycoris. Если откатить, вернётся значение, которое было до этого изменения («{1}»), а более позднее изменение пропадёт.", Loc.Sink(item.Title), Loc.Sink(item.OldValue)))
                : Loc.Tf("«{0}» вернётся к значению, которое было до этого изменения.", Loc.Sink(item.Title));
        if (!await Ui.ConfirmAsync("Откатить изменение?", message, lines, confirmText: Loc.T("ОТКАТИТЬ"), danger: live.IsDrifted))
            return;

        var result = await Task.Run(() => journal.RevertAsync([record], LiveState.VerifyRevert));
        await ReportAsync(result, newer is null ? Loc.T("Изменение откачено.") : Loc.Tf("Изменение откачено; значение остаётся за «{0}».", Loc.Sink(newer.Title)));
    });

    private bool CanRevertBatch(object? parameter) => CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertBatch))]
    private Task RevertBatchAsync(object? parameter) => RunBusyAsync(OperationClass.JournalChange, Loc.T("Откат пакета…"), async () =>
    {
        if (_journal is null || !RequireAdmin(Loc.T("Откат пакета"), "RevertBatch", (parameter as BatchItem)?.Id.ToString() ?? parameter as string)) return;
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
                Loc.Tf("«{0}» — {1} {2}, от последнего к первому.", Loc.Sink(batch.Title), active.Count, Loc.Plural(active.Count, "изменение откатится", "изменения откатятся", "изменений откатятся")) +
                KeptSentence(kept) + DriftSentence(drifted) + (left.Length > 0 ? Loc.Tf(" Не войдут в откат: {0}", left) : ""),
                lines,
                confirmText: Loc.T("ОТКАТИТЬ ПАКЕТ"), danger: drifted > 0))
            return;

        var result = await Task.Run(() => journal.RevertAsync(active, LiveState.VerifyRevert));
        await ReportAsync(result, Loc.Tf("Пакет «{0}» откачен.", Loc.Sink(batch.Title)), left);
    });

    /// <summary>
    /// «ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ» (the title-bar shortcut and the page's pinned button): the newest batch that still has
    /// changes to revert, through the ordinary batch revert with its confirmation. Nothing to revert = a toast.
    /// </summary>
    [RelayCommand]
    private async Task RevertLastBatchAsync()
    {
        // Read-only mode: relaunch with rights and come back to exactly this action.
        if (!RequireAdmin(Loc.T("Откат последнего пакета"), "RevertLastBatch")) return;
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
    private Task RevertAllAsync() => RunBusyAsync(OperationClass.JournalChange, Loc.T("Откат всех изменений…"), async () =>
    {
        if (_journal is null || !RequireAdmin(Loc.T("Откат всех изменений"), "RevertAll")) return;
        var journal = _journal;
        var active = journal.RevertAllCandidates().Reverse().ToList();
        if (active.Count == 0) return;
        var left = await LeftTextAsync(journal.ActiveRecords.Where(ChangeJournal.IsLeftByBulk).ToList());

        var (lines, drifted, _) = await DescribeLiveAsync(journal, active);
        if (!await Ui.ConfirmAsync("Откатить все изменения?",
                Loc.Tf("{0} {1} к исходным значениям — от последнего к первому.", active.Count, Loc.Plural(active.Count, "активное изменение Lycoris вернётся", "активных изменения Lycoris вернутся", "активных изменений Lycoris вернутся")) +
                DriftSentence(drifted) + (left.Length > 0 ? Loc.Tf(" Не войдут в откат: {0}", left) : "") +
                Loc.T(" Удалённые файлы, установленные драйверы и программы, сброс сети этим не отменяются."),
                lines,
                confirmText: Loc.T("ОТКАТИТЬ ВСЁ"), danger: true,
                footnote: Loc.T("Для некоторых изменений нужна перезагрузка.")))
            return;

        var result = await Task.Run(() => journal.RevertAllAsync(LiveState.VerifyRevert));
        // What was actually left after the revert (the journal picks the records again under its gate).
        await ReportAsync(result, Loc.T("Все изменения откачены."), await LeftTextAsync(result.Left));
    });

    /// <summary>Why records stay active (<see cref="RestoreFormat.LeftText"/>), with account names resolved off the UI thread.</summary>
    private static Task<string> LeftTextAsync(IReadOnlyList<ChangeRecord> left) => left.Count == 0
        ? Task.FromResult("")
        : Task.Run(() => RestoreFormat.LeftText(left, UserScope.CurrentSid, UserScope.AccountName));

    private static string Describe(ChangeRecord r) =>
        $"{Loc.Sink(r.Title)} · {RestoreFormat.DescribeTarget(r.Target)}: «{Loc.Sink(r.NewValue)}» → «{Loc.Sink(r.OldValue)}»";

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
                lines.Add(Loc.Tf("{0} · {1}: остаётся «{2}» — его задаёт более позднее «{3}», исходное значение перейдёт к нему", Loc.Sink(r.Title), RestoreFormat.DescribeTarget(r.Target), Loc.Sink(live.Text), Loc.Sink(keeper.Title)));
            }
            else if (live.IsDrifted)
            {
                drifted++;
                lines.Add(Loc.Tf("ИЗМЕНЕНО ВНЕ Lycoris · {0} · {1}: сейчас «{2}» (Lycoris ставил «{3}») → «{4}»", Loc.Sink(r.Title), RestoreFormat.DescribeTarget(r.Target), Loc.Sink(live.Text), Loc.Sink(r.NewValue), Loc.Sink(r.OldValue)));
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
        : Loc.Tf(" {0} {1} после Lycoris (Windows, политикой или вами) — откат заменит и {2} исходным значением; такие пункты отмечены «ИЗМЕНЕНО ВНЕ Lycoris».", drifted, Loc.Plural(drifted, "значение изменено", "значения изменены", "значений изменено"), (drifted == 1 ? Loc.T("его") : Loc.T("их")));

    private static string KeptSentence(int kept) => kept == 0
        ? ""
        : Loc.Tf(" {0} позже изменил другой пакет — {1} как есть, а исходное значение перейдёт к тому изменению.", Loc.N(kept, "{0} параметр", "{0} параметра", "{0} параметров"), (kept == 1 ? Loc.T("он останется") : Loc.T("они останутся")));

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
                ToastKind.Warning, Loc.T("ЖУРНАЛ НЕ СОХРАНЁН"));
        if (result.Success && result.Mismatches.Count == 0)
        {
            if (left.Length == 0)
                Ui.Toast((result.Reverted > 1 ? $"{success} {counts}" : success) + check, ToastKind.Success);
            else
                Ui.Toast($"{counts}{check} {left}", ToastKind.Warning, Loc.T("ОТКАЧЕНО НЕ ВСЁ"));
            return;
        }
        Ui.Toast(result.Success ? $"{counts}{check}" : Loc.Tf("{0} Ошибок: {1}.{2}", counts, result.Failed, check), ToastKind.Warning);
        var tail = left.Length > 0 ? " " + left : "";
        if (!result.Success)
            await Ui.AlertAsync("Часть изменений не откачена",
                Loc.T("Эти записи остались активными — их можно откатить повторно после перезагрузки или вернуть точкой восстановления Windows.") + tail,
                result.Errors.Concat(result.Mismatches.Select(m => Loc.Tf("НЕ СОВПАДАЕТ · {0}", m))));
        else
            await Ui.AlertAsync("Откат не совпал с исходным",
                Loc.T("После отката Lycoris прочитал значения заново, и эти отличаются от записанных до изменения: их могла сразу поменять Windows, политика или другая программа. Строки отмечены «НЕ СОВПАДАЕТ».") + tail,
                result.Mismatches);
    }
}
