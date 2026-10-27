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
    /// <summary>Batches shown at once; «ПОКАЗАТЬ ЕЩЁ» adds the next page.</summary>
    private const int PageSize = 20;

    private ChangeJournal? _journal;
    private bool _rebuildQueued;
    private int _visibleBatches = PageSize;

    public override string Key => "restore";
    public override string Index => "10";
    public override string Title => "ВОССТАНОВЛЕНИЕ";
    public override string Subtitle => "Журнал всех изменений MONOLITH, откат и точки восстановления Windows.";
    public override string Glyph => Glyphs.History;

    public ObservableCollection<BatchItem> Batches { get; } = new();
    public ObservableCollection<RestorePointItem> RestorePoints { get; } = new();

    // ── journal ──
    [ObservableProperty] private JournalFilter _filter = JournalFilter.All;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _revertedCount;
    [ObservableProperty] private int _batchCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _activeCaption = "";
    [ObservableProperty] private string _revertedCaption = "";
    [ObservableProperty] private string _batchCaption = "";
    [ObservableProperty] private bool _journalLoaded;
    [ObservableProperty] private bool _isJournalEmpty;
    [ObservableProperty] private bool _isFilterEmpty;
    [ObservableProperty] private string _filterEmptyText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMoreBatches))] private int _hiddenBatchCount;
    [ObservableProperty] private string _showMoreText = "";
    public bool HasMoreBatches => HiddenBatchCount > 0;

    // ── system restore ──
    [ObservableProperty] private bool _protectionEnabled;
    [ObservableProperty] private bool _protectionBlocked;
    [ObservableProperty] private string _protectionTitle = "—";
    [ObservableProperty] private string _protectionText = "Чтение состояния защиты системы…";
    [ObservableProperty] private bool _canOfferEnable;
    [ObservableProperty] private bool _pointsLoading;
    [ObservableProperty] private string _pointsMessage = "";
    [ObservableProperty] private string _pointsCountText = "";
    [ObservableProperty] private string _autoPointsText = "";

    public string BackupsPath => AppPaths.BackupsDir;

    public RestoreViewModel()
    {
        PropertyChanged += OnSelfPropertyChanged;
        AppSettings.Current.PropertyChanged += OnSettingsChanged;
        UpdateAutoPointsText();
    }

    protected override async Task LoadAsync()
    {
        _journal = await Task.Run(() => ChangeJournal.Instance);
        _journal.Changed += OnJournalChanged;
        Rebuild();
        await RefreshProtectionAsync();
    }

    protected override async Task OnReturnAsync()
    {
        Rebuild();
        await RefreshProtectionAsync();
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IsBusy)) NotifyCommands();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.CreateRestorePoints)) Ui.Post(UpdateAutoPointsText);
    }

    private void UpdateAutoPointsText() => AutoPointsText = !AppSettings.Current.CreateRestorePoints
        ? "Автоматические точки MONOLITH выключены в настройках."
        : ProtectionEnabled
            ? "MONOLITH создаёт точку перед пакетами изменений; одиночные переключатели и «Ускорение игры» — без неё."
            : "Пока защита системы выключена, MONOLITH не может создавать точки — откат только через журнал.";

    partial void OnFilterChanged(JournalFilter value)
    {
        _visibleBatches = PageSize;
        Rebuild();
    }

    [RelayCommand]
    private void ShowMoreBatches()
    {
        _visibleBatches += PageSize;
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
            }, DispatcherPriority.Background);
        });
    }

    private void Rebuild()
    {
        if (_journal is null) return;
        var batches = _journal.Batches;
        var records = _journal.Records;

        ActiveCount = records.Count(r => !r.Reverted);
        RevertedCount = records.Count(r => r.Reverted);
        TotalCount = records.Count;
        BatchCount = batches.Count;
        ActiveCaption = ActiveCount == 0 ? "нечего откатывать" : RestoreFormat.Plural(ActiveCount, "изменение можно откатить", "изменения можно откатить", "изменений можно откатить");
        RevertedCaption = RevertedCount == 0 ? "откатов не было" : "возвращено к исходному";
        BatchCaption = BatchCount == 0 ? "действий не было" : RestoreFormat.Plural(BatchCount, "действие MONOLITH", "действия MONOLITH", "действий MONOLITH");

        // A long journal (hundreds of batches) is shown page by page: every batch card is a full visual tree,
        // and building them all at once froze the UI for more than a second.
        var groups = RestoreFormat.Group(batches, records, Filter);
        var shown = Math.Min(groups.Count, _visibleBatches);
        Batches.Clear();
        for (var i = 0; i < shown; i++) Batches.Add(new BatchItem(groups[i]));
        HiddenBatchCount = groups.Count - shown;
        ShowMoreText = $"ПОКАЗАТЬ ЕЩЁ {Math.Min(PageSize, HiddenBatchCount)} ИЗ {HiddenBatchCount}";

        IsJournalEmpty = batches.Count == 0 && records.Count == 0;
        IsFilterEmpty = !IsJournalEmpty && groups.Count == 0;
        FilterEmptyText = Filter == JournalFilter.Active
            ? "Активных изменений нет — система в исходном состоянии относительно MONOLITH."
            : "Откаченных изменений пока нет.";
        JournalLoaded = true;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RevertRecordCommand.NotifyCanExecuteChanged();
        RevertBatchCommand.NotifyCanExecuteChanged();
        RevertAllCommand.NotifyCanExecuteChanged();
        CreateRestorePointCommand.NotifyCanExecuteChanged();
        EnableProtectionCommand.NotifyCanExecuteChanged();
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
            _ => ("ВЫКЛЮЧЕНА", $"Точки восстановления для диска {drive} не создаются. Изменения настроек MONOLITH всё равно откатываются через журнал, но страховки на уровне Windows нет."),
        };
        UpdateAutoPointsText();
        NotifyCommands();
        if (Ui.Host is MainViewModel main) main.RestoreEnabled = enabled;
        await LoadPointsAsync();
    }

    private async Task LoadPointsAsync()
    {
        if (!Elevation.IsElevated)
        {
            RestorePoints.Clear();
            PointsCountText = "";
            PointsMessage = "Windows показывает список точек только администратору. Перезапустите MONOLITH с правами администратора, чтобы увидеть их здесь, или откройте «Восстановление системы».";
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
                ? "Точек восстановления пока нет. Создайте первую кнопкой «Создать точку восстановления»."
                : "Точек восстановления нет — защита системы выключена.";
        }
        finally
        {
            PointsLoading = false;
        }
    }

    private bool CanAdmin() => Elevation.IsElevated && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAdmin))]
    private Task CreateRestorePointAsync() => RunBusyAsync("Создание точки восстановления…", async () =>
    {
        if (!RequireAdmin("Создание точки восстановления")) return;
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

    private bool CanEnable() => Elevation.IsElevated && !IsBusy && CanOfferEnable;

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private Task EnableProtectionAsync() => RunBusyAsync("Включение защиты системы…", async () =>
    {
        if (!RequireAdmin("Включение защиты системы")) return;
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
    private Task OpenBackupsAsync() => ShellLauncher.OpenFolderAsync(AppPaths.BackupsDir, "Резервные копии");

    [RelayCommand]
    private Task RefreshPointsAsync() => RefreshProtectionAsync();

    [RelayCommand]
    private void OpenSettings() => Ui.Navigate("settings");

    // ═════════════════════════════ Revert ═════════════════════════════

    private bool CanRevertRecord(RecordItem? item) => item is { IsActive: true } && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertRecord))]
    private Task RevertRecordAsync(RecordItem? item) => RunBusyAsync("Откат изменения…", async () =>
    {
        if (item is null || _journal is null || !RequireAdmin("Откат изменения")) return;
        var record = item.Record;
        if (record.Reverted) return;

        // The real current value, read now — never the journal's copy of what MONOLITH once set.
        var live = await Task.Run(() => RestoreLive.Read(record));
        var message = live.State switch
        {
            LiveState.Drifted => $"«{item.Title}» вернётся к значению, которое было до MONOLITH. После MONOLITH это значение уже " +
                                 "изменили — вы, Windows или политика. Откат заменит и это изменение.",
            LiveState.Gone => $"«{item.Title}»: {live.Text}, поэтому возвращать нечего. MONOLITH попробует закрыть запись журнала.",
            _ => $"«{item.Title}» вернётся к значению, которое было до MONOLITH.",
        };
        if (!await Ui.ConfirmAsync("Откатить изменение?", message, RestoreFormat.RevertLines(record, live),
                confirmText: live.IsDrifted ? "ВСЁ РАВНО ОТКАТИТЬ" : "ОТКАТИТЬ", danger: live.IsDrifted,
                footnote: live.IsDrifted ? "Если текущее значение вас устраивает, нажмите «Отмена» — запись останется в журнале." : null))
            return;

        var journal = _journal;
        var result = await Task.Run(() => journal.RevertAsync([record]));
        await ReportAsync(result, "Изменение откачено.");
    });

    private bool CanRevertBatch(BatchItem? batch) => batch is { CanRevert: true } && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertBatch))]
    private Task RevertBatchAsync(BatchItem? batch) => RunBusyAsync("Откат пакета…", async () =>
    {
        if (batch is null || _journal is null || !RequireAdmin("Откат пакета")) return;
        var journal = _journal;
        var active = journal.Records.Where(r => !r.Reverted && (batch.IsSynthetic
            ? journal.Batches.All(b => b.Id != r.BatchId)
            : r.BatchId == batch.Id)).OrderByDescending(r => r.TimestampUtc).ToList();
        if (active.Count == 0) return;

        var plan = await ChooseRecordsAsync(active);
        if (plan is null) return;
        var (include, skipped) = plan.Value;

        if (!await Ui.ConfirmAsync("Откатить пакет?",
                $"«{batch.Title}» — {include.Count} {RestoreFormat.Plural(include.Count, "изменение вернётся", "изменения вернутся", "изменений вернутся")} к исходным значениям, от последнего к первому.",
                include.Select(RestoreFormat.BatchLine),
                confirmText: "ОТКАТИТЬ ПАКЕТ",
                danger: include.Any(l => l.Live.IsDrifted),
                footnote: Footnote(skipped)))
            return;

        var result = await Task.Run(() => journal.RevertAsync(include.Select(l => l.Record)));
        await ReportAsync(result, $"Пакет «{batch.Title}» откачен.");
    });

    private bool CanRevertAll() => ActiveCount > 0 && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private Task RevertAllAsync() => RunBusyAsync("Откат всех изменений…", async () =>
    {
        if (_journal is null || !RequireAdmin("Откат всех изменений")) return;
        var journal = _journal;
        var active = journal.ActiveRecords.OrderByDescending(r => r.TimestampUtc).ToList();
        if (active.Count == 0) return;

        var plan = await ChooseRecordsAsync(active);
        if (plan is null) return;
        var (include, skipped) = plan.Value;

        if (!await Ui.ConfirmAsync("Откатить все изменения?",
                $"{RestoreFormat.ActiveChangesWillReturn(include.Count)} к исходным значениям — от последнего к первому. " +
                "Возвращаются только настройки из журнала (реестр, службы, задачи, схемы питания): удалённые файлы, " +
                "установленные драйверы и программы, сброс сети этим не отменяются.",
                include.Select(RestoreFormat.BatchLine),
                confirmText: "ОТКАТИТЬ ВСЁ", danger: true,
                footnote: Footnote(skipped)))
            return;

        // Exactly the confirmed records — not whatever is active by the time the revert starts.
        var result = await Task.Run(() => journal.RevertAsync(include.Select(l => l.Record)));
        await ReportAsync(result, "Все подтверждённые изменения откачены.");
    });

    /// <summary>
    /// Reads the live value of every record. When some were changed after MONOLITH (drift), asks once whether to
    /// include them; the safe default («ПРОПУСТИТЬ ИХ», also on Esc) leaves them as they are. Null = nothing left to revert.
    /// </summary>
    private static async Task<(List<LiveRecord> Include, int Skipped)?> ChooseRecordsAsync(List<ChangeRecord> active)
    {
        var live = await Task.Run(() => active.Select(r => new LiveRecord(r, RestoreLive.Read(r))).ToList());
        var drifted = live.Where(l => l.Live.IsDrifted).ToList();
        if (drifted.Count == 0) return (live, 0);

        var takeDrifted = await Ui.ConfirmAsync("Значения изменены после MONOLITH",
            $"У {drifted.Count} из {live.Count} {RestoreFormat.Plural(live.Count, "записи", "записей", "записей")} текущее значение отличается от " +
            "того, что установил MONOLITH: его поменяли вы, Windows или политика. Вернуть и их к значениям, которые были до MONOLITH?",
            drifted.Select(RestoreFormat.BatchLine),
            confirmText: "ВЕРНУТЬ И ИХ", cancelText: "ПРОПУСТИТЬ ИХ", danger: true,
            footnote: "«Пропустить их» оставит эти значения как есть. Остальные изменения вы подтвердите следующим шагом.");
        if (takeDrifted) return (live, 0);

        var rest = live.Where(l => !l.Live.IsDrifted).ToList();
        if (rest.Count == 0)
        {
            Ui.Toast("Откатывать нечего: все значения изменены после MONOLITH и оставлены как есть.", ToastKind.Info);
            return null;
        }
        return (rest, drifted.Count);
    }

    private static string Footnote(int skipped) => skipped == 0
        ? "Для некоторых изменений нужна перезагрузка."
        : $"Пропущено (изменено после MONOLITH): {skipped}. Для некоторых изменений нужна перезагрузка.";

    private static async Task ReportAsync(RevertResult result, string success)
    {
        if (result.Success)
        {
            Ui.Toast(result.Reverted > 1 ? $"{success} Записей: {result.Reverted}." : success, ToastKind.Success);
            return;
        }
        Ui.Toast($"Откачено: {result.Reverted}, ошибок: {result.Failed}.", ToastKind.Warning);
        await Ui.AlertAsync("Часть изменений не откачена",
            "Эти записи остались активными. Если служба или задача удалена из Windows, возвращать нечего. В остальных случаях " +
            "повторите откат после перезагрузки или верните систему точкой восстановления Windows.",
            result.Errors);
    }
}
