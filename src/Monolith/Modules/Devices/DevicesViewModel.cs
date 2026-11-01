using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Restore;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Devices;

/// <summary>One ВРУЧНУЮ group: Мышь, Клавиатура, Мониторы, Звук, Сеть, Другие устройства.</summary>
public sealed class DeviceGroup(string key, string title, IReadOnlyList<DeviceCard> cards)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public IReadOnlyList<DeviceCard> Cards { get; } = cards;
    public int Count => Cards.Count;
}

/// <summary>
/// «УСТРОЙСТВА И СЕТЬ»: two tabs. АВТОНАСТРОЙКА — what was found, findings and one plan for every device and the network
/// («сейчас → станет», a reason, a checkbox) applied with one button through the journal. ВРУЧНУЮ — every setting with its
/// control, live value, recommendation and per-row ПРИМЕНИТЬ / ОТКАТИТЬ, plus the measurements. Windows settings that are
/// Optimize tweaks are applied from the plan through TweakEngine (their own journal source) and are read-only rows here
/// with «В ОПТИМИЗАЦИИ». Bulk undo stays on Restore.
/// </summary>
public sealed partial class DevicesViewModel : PageViewModel
{
    public const string PageKey = "devices";
    public const string TabAuto = "auto";
    public const string TabManual = "manual";
    public static IReadOnlyList<string> TabKeys { get; } = [TabAuto, TabManual];

    public override string Key => PageKey;
    public override string Index => "07";
    public override string Title => "УСТРОЙСТВА И СЕТЬ";
    public override string Subtitle => "Мышь, клавиатура, мониторы, звук и сеть: автонастройка и ручные параметры.";
    public override string Glyph => Glyphs.Mouse;

    private readonly DeviceResultStore _results = new();
    private readonly HashSet<DeviceSetting> _hooked = new(ReferenceEqualityComparer.Instance);
    private bool _journalHooked;
    private int _scanning;
    private bool _planQueued;
    private string? _pendingFocus;
    private DispatcherTimer? _highlightTimer;
    private readonly List<ObservableObject> _highlighted = [];

    public DevicesViewModel()
    {
        _selectedTab = AppSettings.Current.DevicesTab == TabManual ? TabManual : TabAuto;
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key) ApplyFocus(Ui.TakeFocusRequest(Key));
        };
    }

    // ───────────────────────────── State ─────────────────────────────

    /// <summary><see cref="TabAuto"/> or <see cref="TabManual"/> (remembered between starts).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualMode), nameof(IsAutoMode))]
    private string _selectedTab;

    public bool IsManualMode => SelectedTab == TabManual;
    public bool IsAutoMode => !IsManualMode;

    partial void OnSelectedTabChanged(string value)
    {
        if (value is not (TabAuto or TabManual))
        {
            SelectedTab = TabAuto;
            return;
        }
        if (RememberTab) AppSettings.Current.DevicesTab = value;
    }

    /// <summary>Off while --snapshot shoots both tabs (the user's last tab stays as it was).</summary>
    internal static bool RememberTab { get; set; } = true;

    public ObservableCollection<DeviceCard> Cards { get; } = [];
    public ObservableCollection<DeviceGroup> Groups { get; } = [];
    /// <summary>Rows that change something and are pre-checked or required (Include), then the ones with a confirmation.</summary>
    public ObservableCollection<PlanItem> PlanItems { get; } = [];
    /// <summary>Offered unchecked (Suggest): under «ещё по желанию».</summary>
    public ObservableCollection<PlanItem> OptionalItems { get; } = [];
    /// <summary>Need a measurement first.</summary>
    public ObservableCollection<PlanItem> MeasureItems { get; } = [];
    /// <summary>Findings: advice with a link, nothing to apply here.</summary>
    public ObservableCollection<PlanItem> Findings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCards), nameof(NoDevices), nameof(IsPlanEmpty), nameof(ShowPlan), nameof(ShowMeasureOnly))]
    private bool _hasScanned;

    [ObservableProperty] private bool _isScanning;
    /// <summary>The «ещё по желанию» rows are shown (collapsed by default).</summary>
    [ObservableProperty] private bool _optionalExpanded;
    [ObservableProperty] private string? _failedText;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _planCount;
    [ObservableProperty] private int _changedCount;
    /// <summary>Why per-user rows are disabled (another account / cannot tell), shown once under the plan; null when fine.</summary>
    [ObservableProperty] private string? _accountNote;
    /// <summary>The element the view should bring into view (a card, a setting, a plan row or a tool).</summary>
    [ObservableProperty] private object? _focusTarget;

    public bool HasCards => Cards.Count > 0;
    public bool NoDevices => HasScanned && Cards.Count == 0;
    /// <summary>Something can be applied: the plan card with its button (measurement rows go inside it).</summary>
    public bool ShowPlan => HasCards && (PlanItems.Count + OptionalItems.Count) > 0;
    /// <summary>
    /// Nothing recommended: one line, so the page reaches «менять нечего» even while measurements are still to do or
    /// rows are only offered «по желанию» (those stay in the plan card below, unchecked).
    /// </summary>
    public bool IsPlanEmpty => HasScanned && HasCards && PlanItems.Count == 0;
    /// <summary>Nothing to apply at all but some rows need a measurement first: those rows alone, without the apply bar.</summary>
    public bool ShowMeasureOnly => IsPlanEmpty && !ShowPlan && MeasureItems.Count > 0;
    /// <summary>The plan card shows its own «РЕКОМЕНДУЕТСЯ ИЗМЕНИТЬ» header only when it has such rows.</summary>
    public bool ShowPlanHeader => PlanItems.Count > 0;
    /// <summary>Only optional rows and measurements in the plan card: the measurement rows get their own header there.</summary>
    public bool ShowMeasureHeaderInPlan => ShowPlan && !ShowPlanHeader && MeasureItems.Count > 0;
    public string PlanEmptyText => (MeasureItems.Count > 0, OptionalItems.Count > 0) switch
    {
        (true, true) => "Менять нечего: всё, что видно без замеров, совпадает с рекомендуемым; ниже — только по желанию.",
        (true, false) => "Менять нечего: всё, что видно без замеров, совпадает с рекомендуемым.",
        (false, true) => "Менять нечего: все настройки совпадают с рекомендуемыми; ниже — только по желанию.",
        _ => "Все настройки устройств и сети совпадают с рекомендуемыми.",
    };
    public string MeasureHeader => $"НУЖНО ИЗМЕРЕНИЕ · {MeasureItems.Count}";
    public bool HasFindings => Findings.Count > 0;
    public bool HasOptional => OptionalItems.Count > 0;
    public bool HasMeasure => MeasureItems.Count > 0;
    public int OptionalCount => OptionalItems.Count;
    public int FindingCount => Findings.Count;
    public string PlanHeader => $"РЕКОМЕНДУЕТСЯ ИЗМЕНИТЬ · {PlanCount}";
    public string OptionalHeader => $"ЕЩЁ ПО ЖЕЛАНИЮ · {OptionalItems.Count}";
    public string FindingsHeader => $"НАХОДКИ · {Findings.Count}";
    public string SelectedText => $"Выбрано {SelectedCount} из {SelectableCount}";
    /// <summary>Rows the user can actually tick (a per-user row for another account or a read-only row is shown, not counted).</summary>
    public int SelectableCount => PlanItems.Concat(OptionalItems).Count(i => i.Selectable);
    public string ChangedText => $"Изменено на этой странице: {ChangedCount}. Откатить — кнопкой «ОТКАТИТЬ» во вкладке «Вручную» или в «Восстановлении».";
    public string FoundHeader => $"НАЙДЕНО · {Cards.Count}";

    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedText));
        ApplyPlanCommand.NotifyCanExecuteChanged();
    }

    partial void OnPlanCountChanged(int value)
    {
        OnPropertyChanged(nameof(PlanHeader));
        OnPropertyChanged(nameof(SelectedText));
    }

    partial void OnChangedCountChanged(int value) => OnPropertyChanged(nameof(ChangedText));

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(IsBusy))
        {
            ApplyPlanCommand.NotifyCanExecuteChanged();
            RefreshCommand.NotifyCanExecuteChanged();
        }
    }

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        if (!_journalHooked)
        {
            _journalHooked = true;
            ChangeJournal.Instance.Changed += () => Ui.Post(RefreshJournalState);
        }
        ApplyFocus(Ui.TakeFocusRequest(Key));
        await RunBusyAsync("Поиск устройств…", () => ScanAsync());
        ApplyFocus(Ui.TakeFocusRequest(Key));
    }

    /// <summary>Back on the page: a quiet re-scan (no busy bar) so changes made elsewhere (Optimize, Windows) show up.</summary>
    protected override async Task OnReturnAsync()
    {
        ApplyFocus(Ui.TakeFocusRequest(Key));
        if (IsBusy || AnyToolRunning) return;
        await ScanAsync();
    }

    /// <summary>Leaving the page stops every measurement (a raw-input listener or a capture stream never outlives the page).</summary>
    public override void Deactivate()
    {
        foreach (var tool in AllTools()) tool.Cancel();
    }

    private IEnumerable<DeviceTool> AllTools() =>
        Cards.SelectMany(c => c.Tools.Concat(c.Settings.Select(s => s.Tool).OfType<DeviceTool>()));

    private bool AnyToolRunning => AllTools().Any(t => t.IsRunning);

    // ───────────────────────────── Scan + merge ─────────────────────────────

    /// <summary>
    /// A scan; one asked for while a scan runs (the re-scan after «НАСТРОИТЬ АВТОМАТИЧЕСКИ», a single row's re-read) is not
    /// lost: the running scan runs once more when it finishes. Always called on the UI thread.
    /// </summary>
    private async Task ScanAsync()
    {
        _rescanWanted = true;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return; // the running scan picks the request up
        IsScanning = true;
        try
        {
            while (_rescanWanted)
            {
                _rescanWanted = false;
                await ScanOnceAsync();
            }
        }
        finally
        {
            IsScanning = false;
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    private bool _rescanWanted;

    private async Task ScanOnceAsync()
    {
        var profile = await SystemProfile.LoadAsync();
        // Both are blocking (process tokens / WMI): read off the UI thread.
        var (interactive, accountProblem) = await Task.Run(() => (Elevation.InteractiveUserState, Elevation.InteractiveUserProblem));
        _lastInteractive = interactive;
        var ctx = new DeviceScanContext
        {
            Profile = profile, IsElevated = Elevation.IsElevated, InteractiveUser = interactive, Results = _results,
        };
        var result = await DeviceScan.RunAsync(ctx);
        Merge(result.Cards);
        FailedText = result.Failed.Count == 0 ? null
            : $"Не удалось прочитать: {string.Join(", ", result.Failed)}. Нажмите «ОБНОВИТЬ», чтобы попробовать ещё раз.";
        AccountNote = interactive == true || !Cards.SelectMany(c => c.Settings).Any(DevicePlan.IsPerUser) ? null : accountProblem;
        HasScanned = true;
        RefreshJournalState();
        RebuildPlan();
        Log.Info($"Устройства и сеть: найдено {Cards.Count}, к изменению {PlanCount + OptionalItems.Count}.");
        if (_pendingFocus is { } focus)
        {
            _pendingFocus = null;
            ApplyFocus(focus);
        }
    }

    /// <summary>
    /// Puts a scan into the live cards. A card whose shape did not change (same settings, options and tools) is updated in
    /// place: staged values, open tools, highlights and the scroll position stay. Other cards are replaced, keeping the
    /// staged values of settings that still exist.
    /// </summary>
    internal void Merge(IReadOnlyList<DeviceCard> fresh)
    {
        var old = Cards.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        var merged = new List<DeviceCard>(fresh.Count);
        foreach (var f in fresh)
        {
            if (old.TryGetValue(f.Key, out var o) && SameShape(o, f))
            {
                UpdateInPlace(o, f);
                merged.Add(o);
            }
            else
            {
                if (o is not null) CarryStaged(o, f);
                merged.Add(f);
            }
        }

        var sameOrder = merged.Count == Cards.Count && merged.Zip(Cards).All(p => ReferenceEquals(p.First, p.Second));
        if (!sameOrder)
        {
            foreach (var gone in Cards.Where(c => !merged.Contains(c))) Unhook(gone);
            Cards.Clear();
            foreach (var c in merged) Cards.Add(c);
            RebuildGroups();
        }
        foreach (var c in Cards) Hook(c);
        // A staged value that is now live (just applied) is no longer staged.
        foreach (var s in Cards.SelectMany(c => c.Settings))
            if (s is { Staged: { } st, Current: { } cur } && s.SameValue(st, cur)) s.Staged = null;
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(NoDevices));
        OnPropertyChanged(nameof(FoundHeader));
    }

    private static bool SameShape(DeviceCard a, DeviceCard b) =>
        a.Kind == b.Kind && a.Title == b.Title && a.Subtitle == b.Subtitle
        && a.Settings.Count == b.Settings.Count
        && a.Settings.Zip(b.Settings).All(p => p.First.Id == p.Second.Id && p.First.Control == p.Second.Control
                                               && p.First.Options.Select(o => o.Value.Key).SequenceEqual(p.Second.Options.Select(o => o.Value.Key))
                                               && (p.First.Tool?.Id == p.Second.Tool?.Id))
        && a.Tools.Select(t => t.Id).SequenceEqual(b.Tools.Select(t => t.Id));

    private static void UpdateInPlace(DeviceCard live, DeviceCard fresh)
    {
        if (!live.Facts.SequenceEqual(fresh.Facts)) live.Facts = fresh.Facts;
        if (!live.Notes.SequenceEqual(fresh.Notes)) live.Notes = fresh.Notes;
        foreach (var (l, f) in live.Settings.Zip(fresh.Settings))
        {
            if (l.Current != f.Current) l.Current = f.Current;
            if (l.Recommended != f.Recommended) l.Recommended = f.Recommended;
            if (l.StateTag != f.StateTag) l.StateTag = f.StateTag;
            if (l.DisabledReason != f.DisabledReason) l.DisabledReason = f.DisabledReason;
        }
        var sameApps = live.VendorApps.Count == fresh.VendorApps.Count
                       && live.VendorApps.Zip(fresh.VendorApps).All(p => p.First.AppName == p.Second.AppName && p.First.ButtonText == p.Second.ButtonText
                                                                         && p.First.Note == p.Second.Note);
        if (!sameApps)
        {
            live.VendorApps.Clear();
            foreach (var app in fresh.VendorApps) live.VendorApps.Add(app);
        }
    }

    private static void CarryStaged(DeviceCard old, DeviceCard fresh)
    {
        foreach (var f in fresh.Settings)
            if (old.Settings.FirstOrDefault(s => s.Id == f.Id) is { Staged: { } staged, IsDirty: true })
                f.Staged = staged;
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var g in Cards.GroupBy(c => DeviceKinds.Group(c.Kind)).OrderBy(g => g.Key.Order))
            Groups.Add(new DeviceGroup(g.Key.Key, g.Key.Title, g.ToList()));
    }

    // ───────────────────────────── Plan ─────────────────────────────

    private void Hook(DeviceCard card)
    {
        foreach (var s in card.Settings)
            if (_hooked.Add(s)) s.PropertyChanged += OnSettingChanged;
    }

    private void Unhook(DeviceCard card)
    {
        foreach (var s in card.Settings)
            if (_hooked.Remove(s)) s.PropertyChanged -= OnSettingChanged;
    }

    /// <summary>A measurement finished or a value was re-read: rebuild the plan once, on the UI thread, after the change.</summary>
    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DeviceSetting.Recommended) or nameof(DeviceSetting.Current) or nameof(DeviceSetting.DisabledReason))) return;
        if (_planQueued) return;
        _planQueued = true;
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null)
        {
            _planQueued = false;
            RebuildPlan();
            return;
        }
        d.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _planQueued = false;
            RebuildPlan();
        });
    }

    internal void RebuildPlan()
    {
        var previous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in PlanItems.Concat(OptionalItems)) previous[item.Key] = item.IsSelected;
        foreach (var item in PlanItems.Concat(OptionalItems)) item.PropertyChanged -= OnPlanItemChanged;
        // Fails closed: until the account is known to be the desktop user, per-user rows are not selectable.
        var plan = DevicePlan.Build(Cards, _lastInteractive == true, previous);

        Replace(PlanItems, plan.Where(i => i.HasChange && i.Policy != RecommendPolicy.Suggest));
        Replace(OptionalItems, plan.Where(i => i.HasChange && i.Policy == RecommendPolicy.Suggest));
        Replace(MeasureItems, plan.Where(i => i.Kind == PlanItemKind.Measurement));
        Replace(Findings, plan.Where(i => i.Kind == PlanItemKind.Advice));
        foreach (var item in PlanItems.Concat(OptionalItems)) item.PropertyChanged += OnPlanItemChanged;

        PlanCount = PlanItems.Count(i => i.Selectable); // the header and the tab count only what can be applied
        UpdateSelectedCount();
        foreach (var name in new[]
                 {
                     nameof(ShowPlan), nameof(IsPlanEmpty), nameof(ShowMeasureOnly), nameof(ShowPlanHeader), nameof(ShowMeasureHeaderInPlan), nameof(PlanEmptyText), nameof(MeasureHeader),
                     nameof(HasFindings), nameof(HasOptional), nameof(HasMeasure), nameof(OptionalCount),
                     nameof(FindingCount), nameof(OptionalHeader), nameof(FindingsHeader), nameof(SelectedText), nameof(SelectableCount),
                 })
            OnPropertyChanged(name);
    }

    private bool? _lastInteractive;

    private static void Replace(ObservableCollection<PlanItem> target, IEnumerable<PlanItem> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void OnPlanItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlanItem.IsSelected)) UpdateSelectedCount();
    }

    private void UpdateSelectedCount() =>
        SelectedCount = PlanItems.Concat(OptionalItems).Count(i => i is { Selectable: true, IsSelected: true });

    // ───────────────────────────── Journal state ─────────────────────────────

    private void RefreshJournalState()
    {
        foreach (var s in Cards.SelectMany(c => c.Settings))
            s.CanRevert = !s.IsTweakRow && DeviceApply.RevertTarget(s) is not null;
        var sid = UserScope.CurrentSid;
        ChangedCount = ChangeJournal.Instance.ActiveRecords.Count(r => r.Source == DeviceTargets.Source && !ChangeJournal.IsOtherUser(r, sid));
    }

    // ───────────────────────────── Commands ─────────────────────────────

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RunBusyAsync("Поиск устройств…", () => ScanAsync());

    [RelayCommand]
    private void ShowTab(string? tab)
    {
        if (tab is TabAuto or TabManual) SelectedTab = tab;
    }

    private bool CanApplyPlan() => !IsBusy && SelectedCount > 0;

    /// <summary>«НАСТРОИТЬ АВТОМАТИЧЕСКИ»: the checked rows through the journal (restore point, one batch).</summary>
    [RelayCommand(CanExecute = nameof(CanApplyPlan))]
    private async Task ApplyPlanAsync()
    {
        if (!RequireAdmin("Настроить автоматически")) return;
        var selected = PlanItems.Concat(OptionalItems).Where(i => i is { Selectable: true, IsSelected: true }).ToList();
        PlanApplyResult? result = null;
        await RunBusyAsync("Настройка устройств и сети…", async () =>
        {
            var progress = new Progress<string>(text => BusyText = text);
            result = await DeviceApply.ApplyPlanAsync(selected, progress, CancellationToken.None);
        });
        if (result is null) return;
        await ReportPlanAsync(result);
        await ScanAsync();
    }

    private static async Task ReportPlanAsync(PlanApplyResult r)
    {
        var text = DeviceApply.SummaryText(r);
        if (r.Errors.Count == 0)
        {
            Ui.Toast(text, r.Applied > 0 ? ToastKind.Success : ToastKind.Info, "АВТОНАСТРОЙКА");
            return;
        }
        await Ui.AlertAsync(r.Applied > 0 ? "Применено частично" : "Не применено", text + " Подробности — в консоли и журнале.", r.Errors);
    }

    /// <summary>ПРИМЕНИТЬ of one row (only this setting; no restore point).</summary>
    [RelayCommand]
    private async Task ApplySettingAsync(DeviceSetting? s)
    {
        if (s is not { CanApply: true, Staged: { } value }) return;
        if (!RequireAdmin(s.Title)) return;
        s.IsApplying = true;
        try
        {
            var (outcome, errors) = await DeviceApply.ApplyOneAsync(s, value, CancellationToken.None);
            switch (outcome)
            {
                case ApplyOutcome.Applied:
                    Ui.Toast("Готово. Отменить — «ОТКАТИТЬ» в этой строке или в «Восстановлении».", ToastKind.Success, s.Title);
                    break;
                case ApplyOutcome.NoChange:
                    Ui.Toast("Уже так — менять нечего.", ToastKind.Info, s.Title);
                    break;
                case ApplyOutcome.Declined:
                    Ui.Toast("Не подтверждено — возвращено прежнее значение.", ToastKind.Info, s.Title);
                    break;
                case ApplyOutcome.Blocked:
                    Ui.Toast(errors.Count > 0 ? string.Join("\n", errors) : "Сейчас это изменение недоступно.", ToastKind.Warning, s.Title);
                    break;
                default:
                    Ui.Toast(errors.Count > 0 ? string.Join("\n", errors) : "Не применено.", ToastKind.Error, s.Title);
                    break;
            }
            if (outcome is ApplyOutcome.Applied or ApplyOutcome.NoChange or ApplyOutcome.Declined) s.Staged = null;
        }
        catch (Exception ex)
        {
            Log.Error($"{s.Title}: не применено", ex);
            Ui.Toast(ex.Message, ToastKind.Error, s.Title);
        }
        finally
        {
            s.IsApplying = false;
        }
        await RereadAsync(s);
    }

    /// <summary>ОТКАТИТЬ of one row: this page's own top layer of the setting, for this account, with admin rights.</summary>
    [RelayCommand]
    private async Task RevertSettingAsync(DeviceSetting? s)
    {
        if (s is null) return;
        if (!RequireAdmin($"Откат: {s.Title}")) return;
        s.IsApplying = true;
        try
        {
            var result = await DeviceApply.RevertOneAsync(s);
            if (result is null)
                Ui.Toast("Откатывать нечего.", ToastKind.Info, s.Title);
            else if (result.Success)
                Ui.Toast(result.VerificationText ?? RestoreFormat.RevertCounts(result), ToastKind.Success, "ОТКАТ");
            else
                Ui.Toast(string.Join("\n", result.Errors), ToastKind.Error, "ОТКАТ НЕ ВЫПОЛНЕН");
        }
        finally
        {
            s.IsApplying = false;
        }
        await RereadAsync(s);
    }

    /// <summary>After one row changed: re-read only that setting when it can, otherwise a quiet re-scan.</summary>
    private async Task RereadAsync(DeviceSetting s)
    {
        if (s.ReadCurrent is { } read)
        {
            try
            {
                var value = await Task.Run(read);
                s.Current = value;
                if (s is { Staged: { } st } && value is { } v && s.SameValue(st, v)) s.Staged = null;
                RefreshJournalState();
                RebuildPlan();
                return;
            }
            catch (Exception ex)
            {
                Log.Warn($"{s.Title}: не удалось перечитать — {ex.Message}");
            }
        }
        await ScanAsync();
    }

    [RelayCommand]
    private async Task RunToolAsync(DeviceTool? tool)
    {
        if (tool is null) return;
        if (tool.IsRunning)
        {
            tool.Cancel();
            return;
        }
        var card = Cards.FirstOrDefault(c => c.Tools.Contains(tool) || c.Settings.Any(s => ReferenceEquals(s.Tool, tool)));
        if (card is null) return;
        await tool.StartAsync(card, _results);
    }

    [RelayCommand]
    private void CancelTool(DeviceTool? tool) => tool?.Cancel();

    [RelayCommand]
    private async Task OpenLinkAsync(DeviceLink? link)
    {
        if (link is null) return;
        if (DeviceApply.ValidateLink(link) is { } problem)
        {
            Log.Warn($"Ссылка «{link.Label}» отклонена: {problem}.");
            return;
        }
        switch (link.Kind)
        {
            case DeviceLinkKind.Tool:
                ApplyFocus(link.FocusId is { Length: > 0 } toolId ? "tool:" + toolId : link.Target);
                break;
            case DeviceLinkKind.VendorApp:
                var app = Cards.FirstOrDefault(c => string.Equals(c.Key, link.Target, StringComparison.OrdinalIgnoreCase))
                    ?.VendorApps.FirstOrDefault(a => a.CanLaunch);
                if (app is not null) await DeviceApply.OpenVendorAsync(app);
                else Ui.Toast("Программа производителя не найдена.", ToastKind.Info);
                break;
            default:
                await DeviceApply.OpenExternalAsync(link);
                break;
        }
    }

    [RelayCommand]
    private Task OpenVendorAsync(VendorAppLink? app) => app is null ? Task.CompletedTask : DeviceApply.OpenVendorAsync(app);

    /// <summary>«В ОПТИМИЗАЦИИ»: the tweak's row on Optimize (nothing is applied by the click).</summary>
    [RelayCommand]
    private Task OpenTweakAsync(string? tweakId) =>
        string.IsNullOrEmpty(tweakId) ? Task.CompletedTask : DeviceApply.OpenExternalAsync(new DeviceLink(DeviceLinkKind.FocusTweak, "", tweakId));

    /// <summary>A device in the АВТОНАСТРОЙКА summary: its card in ВРУЧНУЮ.</summary>
    [RelayCommand]
    private void FocusCard(DeviceCard? card)
    {
        if (card is not null) ApplyFocus(card.Key);
    }

    // ───────────────────────────── Focus (deep links, Ctrl+K, Dashboard) ─────────────────────────────

    /// <summary>
    /// "tab:auto" / "tab:manual" switch tabs. "kind:monitor": in АВТОНАСТРОЙКА the first plan row of a monitor, otherwise the
    /// first monitor card with a plan row (else the first monitor card) in ВРУЧНУЮ with its first changeable row. A card key,
    /// a setting id, a journal target ("spi:mouse-speed", from Restore) or "tool:&lt;id&gt;" open ВРУЧНУЮ at that item.
    /// </summary>
    internal void ApplyFocus(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        switch (id)
        {
            case "tab:auto" or "tab:recommended":
                SelectedTab = TabAuto;
                return;
            case "tab:manual":
                SelectedTab = TabManual;
                return;
        }
        if (!HasScanned)
        {
            _pendingFocus = id; // applied after the first scan
            return;
        }
        var target = Resolve(id, IsAutoMode);
        if (target is null) return;
        if (target is PlanItem) SelectedTab = TabAuto;
        else SelectedTab = TabManual;
        Highlight(target);
    }

    /// <summary>What a focus id points at (pure over the current cards and plan).</summary>
    internal ObservableObject? Resolve(string id, bool autoTab)
    {
        if (id.StartsWith("kind:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<DeviceKind>(id[5..], ignoreCase: true, out var kind)) return null;
            var planRows = PlanItems.Concat(OptionalItems).Concat(MeasureItems).Concat(Findings).ToList();
            if (autoTab && planRows.FirstOrDefault(p => p.Card.Kind == kind) is { } row) return row;
            var cards = Cards.Where(c => c.Kind == kind).ToList();
            var card = cards.FirstOrDefault(c => planRows.Any(p => ReferenceEquals(p.Card, c))) ?? cards.FirstOrDefault();
            if (card is null) return null;
            var setting = card.Settings.FirstOrDefault(s => planRows.Any(p => ReferenceEquals(p.Setting, s)))
                          ?? card.Settings.FirstOrDefault(s => s.Control == SettingControl.DualChoice)
                          ?? card.Settings.FirstOrDefault(s => !s.IsReadOnly);
            return (ObservableObject?)setting ?? card;
        }
        if (id.StartsWith("tool:", StringComparison.Ordinal))
            return AllTools().FirstOrDefault(t => t.Id == id[5..]);
        if (Cards.FirstOrDefault(c => string.Equals(c.Key, id, StringComparison.OrdinalIgnoreCase)) is { } byKey) return byKey;
        var settings = Cards.SelectMany(c => c.Settings).ToList();
        if (settings.FirstOrDefault(s => s.Id == id) is { } byId) return byId;
        if (settings.FirstOrDefault(s => string.Equals(s.JournalTarget, id, StringComparison.OrdinalIgnoreCase)) is { } byTarget) return byTarget;
        if (AllTools().FirstOrDefault(t => t.Id == id) is { } tool) return tool;
        // A journal target or palette entry without a matching row: the device kind it belongs to.
        if (id.StartsWith("spi:", StringComparison.Ordinal))
            return Resolve(id[4..].StartsWith("keyboard", StringComparison.Ordinal) ? "kind:keyboard" : "kind:mouse", false);
        if (id.StartsWith("display:", StringComparison.Ordinal) || id.StartsWith("ddc:", StringComparison.Ordinal)
            || id.StartsWith("display-", StringComparison.Ordinal)) return Resolve("kind:monitor", false);
        if (id.StartsWith("audio:", StringComparison.Ordinal) || id.StartsWith("mic-", StringComparison.Ordinal)) return Resolve("kind:audioinput", false);
        if (id.StartsWith("nicprop:", StringComparison.Ordinal)) return Resolve("kind:network", false);
        return null;
    }

    private void Highlight(ObservableObject target)
    {
        foreach (var h in _highlighted) SetHighlighted(h, false);
        _highlighted.Clear();
        SetHighlighted(target, true);
        _highlighted.Add(target);
        // A tool sits inside a setting row: the row lights up too, then the card.
        if (target is DeviceTool tool && Cards.SelectMany(c => c.Settings).FirstOrDefault(s => ReferenceEquals(s.Tool, tool)) is { } row)
        {
            SetHighlighted(row, true);
            _highlighted.Add(row);
        }
        var owner = target switch
        {
            DeviceSetting s => Cards.FirstOrDefault(c => c.Settings.Contains(s)),
            DeviceTool t => Cards.FirstOrDefault(c => c.Tools.Contains(t) || c.Settings.Any(s => ReferenceEquals(s.Tool, t))),
            _ => null,
        };
        if (owner is not null)
        {
            SetHighlighted(owner, true);
            _highlighted.Add(owner);
        }
        FocusTarget = null; // re-raise when the same item is asked for twice
        FocusTarget = target;
        _highlightTimer?.Stop();
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer?.Stop();
            foreach (var h in _highlighted) SetHighlighted(h, false);
            _highlighted.Clear();
        };
        _highlightTimer.Start();
    }

    private static void SetHighlighted(ObservableObject o, bool on)
    {
        switch (o)
        {
            case DeviceCard c: c.IsHighlighted = on; break;
            case DeviceSetting s: s.IsHighlighted = on; break;
            case DeviceTool t: t.IsHighlighted = on; break;
            case PlanItem p: p.IsHighlighted = on; break;
        }
    }
}
