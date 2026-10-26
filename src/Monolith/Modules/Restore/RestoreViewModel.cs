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

    private void UpdateAutoPointsText() => AutoPointsText = AppSettings.Current.CreateRestorePoints
        ? "MONOLITH создаёт точку перед каждым пакетом изменений."
        : "Автоматические точки MONOLITH выключены в настройках.";

    partial void OnFilterChanged(JournalFilter value) => Rebuild();

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

        var groups = RestoreFormat.Group(batches, records, Filter);
        Batches.Clear();
        foreach (var g in groups) Batches.Add(new BatchItem(g));

        IsJournalEmpty = batches.Count == 0 && records.Count == 0;
        IsFilterEmpty = !IsJournalEmpty && Batches.Count == 0;
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
            _ => ("ВЫКЛЮЧЕНА", $"Точки восстановления для диска {drive} не создаются. Изменения MONOLITH всё равно откатываются через журнал, но страховки на уровне Windows нет."),
        };
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
    private void OpenBackups() => ShellLauncher.OpenFolder(AppPaths.BackupsDir, "Резервные копии");

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
        // Research services.md §3.5: show the real current value and warn when it changed after MONOLITH.
        var live = await Task.Run(() => LiveState.Read(item.Record));
        var lines = new List<string>
        {
            $"{item.KindLabel}: {item.Target}",
            $"Сейчас: {live.Text}",
            $"Станет: {item.OldValue}",
        };
        if (live.IsDrifted)
            lines.Add($"После MONOLITH значение изменили (Windows, политика или вы): MONOLITH ставил «{item.NewValue}». Откат заменит текущее значение исходным.");
        if (!await Ui.ConfirmAsync("Откатить изменение?",
                live.IsDrifted
                    ? $"«{item.Title}» изменено вне MONOLITH. Если откатить, вернётся значение, которое было до MONOLITH, а более позднее изменение пропадёт."
                    : $"«{item.Title}» вернётся к значению, которое было до MONOLITH.",
                lines,
                confirmText: "ОТКАТИТЬ", danger: live.IsDrifted))
            return;

        var journal = _journal;
        var result = await Task.Run(() => journal.RevertAsync([item.Record]));
        await ReportAsync(result, "Изменение откачено.");
    });

    private bool CanRevertBatch(BatchItem? batch) => batch is { CanRevert: true } && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertBatch))]
    private Task RevertBatchAsync(BatchItem? batch) => RunBusyAsync("Откат пакета…", async () =>
    {
        if (batch is null || _journal is null || !RequireAdmin("Откат пакета")) return;
        var active = _journal.Records.Where(r => !r.Reverted && (batch.IsSynthetic
            ? _journal.Batches.All(b => b.Id != r.BatchId)
            : r.BatchId == batch.Id)).ToList();
        if (active.Count == 0) return;

        var ordered = active.OrderByDescending(r => r.TimestampUtc).ToList();
        var (lines, drifted) = await DescribeLiveAsync(ordered);
        if (!await Ui.ConfirmAsync("Откатить пакет?",
                $"«{batch.Title}» — {active.Count} {RestoreFormat.Plural(active.Count, "изменение вернётся", "изменения вернутся", "изменений вернутся")} к исходным значениям, от последнего к первому." +
                DriftSentence(drifted),
                lines,
                confirmText: "ОТКАТИТЬ ПАКЕТ", danger: drifted > 0))
            return;

        var journal = _journal;
        var result = await Task.Run(() => journal.RevertAsync(active));
        await ReportAsync(result, $"Пакет «{batch.Title}» откачен.");
    });

    private bool CanRevertAll() => ActiveCount > 0 && CanAdmin();

    [RelayCommand(CanExecute = nameof(CanRevertAll))]
    private Task RevertAllAsync() => RunBusyAsync("Откат всех изменений…", async () =>
    {
        if (_journal is null || !RequireAdmin("Откат всех изменений")) return;
        var active = _journal.ActiveRecords.OrderByDescending(r => r.TimestampUtc).ToList();
        if (active.Count == 0) return;

        var (lines, drifted) = await DescribeLiveAsync(active);
        if (!await Ui.ConfirmAsync("Откатить все изменения?",
                $"{active.Count} {RestoreFormat.Plural(active.Count, "активное изменение MONOLITH вернётся", "активных изменения MONOLITH вернутся", "активных изменений MONOLITH вернутся")} к исходным значениям — от последнего к первому." +
                DriftSentence(drifted) + " Удалённые файлы, установленные драйверы и программы, сброс сети этим не отменяются.",
                lines,
                confirmText: "ОТКАТИТЬ ВСЁ", danger: true,
                footnote: "Для некоторых изменений нужна перезагрузка."))
            return;

        var journal = _journal;
        var result = await Task.Run(() => journal.RevertAllAsync());
        await ReportAsync(result, "Все изменения откачены.");
    });

    private static string Describe(ChangeRecord r) =>
        $"{r.Title} · {RestoreFormat.DescribeTarget(r.Target)}: «{r.NewValue}» → «{r.OldValue}»";

    /// <summary>Dialog lines with a live read per record; drifted ones are flagged (research services.md §3.5).</summary>
    private static async Task<(List<string> Lines, int Drifted)> DescribeLiveAsync(IReadOnlyList<ChangeRecord> records)
    {
        var live = await Task.Run(() => records.Select(LiveState.Read).ToList());
        var lines = new List<string>(records.Count);
        var drifted = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (live[i].IsDrifted)
            {
                drifted++;
                lines.Add($"ИЗМЕНЕНО ВНЕ MONOLITH · {r.Title} · {RestoreFormat.DescribeTarget(r.Target)}: сейчас «{live[i].Text}» (MONOLITH ставил «{r.NewValue}») → «{r.OldValue}»");
            }
            else
            {
                lines.Add(Describe(r));
            }
        }
        return (lines, drifted);
    }

    private static string DriftSentence(int drifted) => drifted == 0
        ? ""
        : $" {drifted} {RestoreFormat.Plural(drifted, "значение изменено", "значения изменены", "значений изменено")} после MONOLITH (Windows, политикой или вами) — откат заменит и {(drifted == 1 ? "его" : "их")} исходным значением; такие пункты отмечены «ИЗМЕНЕНО ВНЕ MONOLITH».";

    private static async Task ReportAsync(RevertResult result, string success)
    {
        if (result.Success)
        {
            Ui.Toast(result.Reverted > 1 ? $"{success} Записей: {result.Reverted}." : success, ToastKind.Success);
            return;
        }
        Ui.Toast($"Откачено: {result.Reverted}, ошибок: {result.Failed}.", ToastKind.Warning);
        await Ui.AlertAsync("Часть изменений не откачена",
            "Эти записи остались активными — их можно откатить повторно после перезагрузки или вернуть точкой восстановления Windows.",
            result.Errors);
    }
}
