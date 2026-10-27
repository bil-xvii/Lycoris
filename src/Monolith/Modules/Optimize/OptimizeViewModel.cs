using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Shell;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Optimize;

/// <summary>
/// The Optimize page: profile tiles → filterable, grouped tweak list with detected states → apply with a full
/// preview, per-row and bulk undo. Every system change goes through <see cref="TweakEngine"/> (journal + restore point).
/// </summary>
public sealed partial class OptimizeViewModel : PageViewModel
{
    public override string Key => "optimize";
    public override string Index => "02";
    public override string Title => "ОПТИМИЗАЦИЯ";
    public override string Subtitle => "Выберите профиль, проверьте и примените. Настройки откатываются в один клик; необратимое помечено.";
    public override string Glyph => Glyphs.Bolt;

    private readonly List<TweakItemViewModel> _items = [];
    private readonly Dictionary<TweakCategory, SectionRow> _sections = [];
    private readonly Dictionary<OptimizeProfile, HashSet<string>> _profileDefaults = [];
    private readonly HashSet<string> _acceptedExpert = new(StringComparer.Ordinal);
    private readonly ProfilesRow _profilesRow;
    private readonly FilterRow _filterRow;
    private readonly EmptyRow _emptyRow;
    private readonly ExpertRow _expertRow;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _saveTimer;
    private HashSet<string>? _customSelection;
    private bool _expertOpen;
    private bool _expertHiddenInSearch;
    private int _visibleExpert;
    private bool _detected;
    private bool _refreshing;
    /// <summary>System Protection state read with the last detection (null = not read yet).</summary>
    private bool? _restoreProtectionOn;

    public OptimizeViewModel()
    {
        _profilesRow = new ProfilesRow(this);
        _filterRow = new FilterRow(this);
        _emptyRow = new EmptyRow(this);
        _expertRow = new ExpertRow(this);

        Profiles =
        [
            new ProfileTileViewModel(this, OptimizeProfile.Normal, "01", "ОБЫЧНЫЙ", "Обычный",
                "Повседневная работа: тише и чище.",
                "Для работы и учёбы: меньше рекламы, подсказок и фоновой активности. Только безопасные пункты."),
            new ProfileTileViewModel(this, OptimizeProfile.Gaming, "02", "ИГРОВОЙ", "Игровой",
                "Для игр: меньше фоновых помех.",
                "Для игр: Windows меньше отвлекает и мешает во время игры. Прирост FPS от этих пунктов обычно небольшой — честные цифры " +
                "указаны в описании каждого. Только проверенные пункты без компромиссов в безопасности."),
            new ProfileTileViewModel(this, OptimizeProfile.Recommended, "03", "РЕКОМЕНДУЕМЫЙ", "Рекомендуемый",
                "Безопасный баланс для любого ПК.",
                "Проверенные настройки без компромиссов: подходят и для работы, и для игр. Выбран по умолчанию.", "ПО УМОЛЧАНИЮ"),
            new ProfileTileViewModel(this, OptimizeProfile.Custom, "04", "СВОЙ НАБОР", "Свой набор",
                "Только то, что вы отметили сами.",
                "Ваш собственный набор: отметьте нужные пункты в списке ниже. Выбор запоминается между запусками."),
        ];
        UpdateTileSelection();

        Rows.Add(_profilesRow);
        Rows.Add(_filterRow);

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RebuildRows();
        };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => FlushCustomSelection();

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsBusy)) OnBusyChanged();
        };
    }

    // ───────────────────────────── Bindable state ─────────────────────────────

    /// <summary>Everything the virtualized list shows, in order (see OptimizeRows.cs).</summary>
    public ObservableCollection<object> Rows { get; } = [];
    public ObservableCollection<ProfileTileViewModel> Profiles { get; }
    public ObservableCollection<CategoryFilterViewModel> Categories { get; } = [];

    [ObservableProperty] private OptimizeProfile _profile = OptimizeProfile.Recommended;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private TweakCategory? _categoryFilter;
    [ObservableProperty] private bool _isDetecting;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _changeCount;
    [ObservableProperty] private int _restartCount;
    [ObservableProperty] private int _revertableCount;
    [ObservableProperty] private int _appliedTotal;
    [ObservableProperty] private int _applicableTotal;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private string _barNote = "Загрузка каталога…";
    /// <summary>A restart the user postponed ("ПОЗЖЕ"): shown as a reminder button in the action bar.</summary>
    [ObservableProperty] private RestartRequirement _pendingRestart;

    public int TotalCount => _items.Count;
    public bool CanModify => IsElevated && !IsBusy;
    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasPendingRestart => PendingRestart != RestartRequirement.None;
    public string PendingRestartText => PendingRestart switch
    {
        RestartRequirement.Explorer => "НУЖЕН ПЕРЕЗАПУСК ПРОВОДНИКА",
        RestartRequirement.SignOut => "НУЖЕН ВЫХОД ИЗ СИСТЕМЫ",
        RestartRequirement.Reboot => "НУЖНА ПЕРЕЗАГРУЗКА",
        _ => "",
    };
    public string AppliedSummary => _detected ? $"{AppliedTotal} ИЗ {ApplicableTotal}" : "—";
    public string ApplyButtonText => ChangeCount > 0 ? $"ПРИМЕНИТЬ · {ChangeCount}" : "ПРИМЕНИТЬ";
    public string RevertButtonText => RevertableCount > 0 ? $"ОТКАТИТЬ ВЫБРАННОЕ · {RevertableCount}" : "ОТКАТИТЬ ВЫБРАННОЕ";

    private ProfileTileViewModel CurrentTile => Profiles.First(p => p.Kind == Profile);

    partial void OnProfileChanged(OptimizeProfile value)
    {
        AppSettings.Current.LastProfile = value.ToString();
        UpdateTileSelection();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearch));
        if (!HasSearch) _expertHiddenInSearch = false;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnIsDetectingChanged(bool value) => RefreshCommandStates();

    partial void OnPendingRestartChanged(RestartRequirement value)
    {
        OnPropertyChanged(nameof(HasPendingRestart));
        OnPropertyChanged(nameof(PendingRestartText));
    }

    partial void OnChangeCountChanged(int value) => OnPropertyChanged(nameof(ApplyButtonText));
    partial void OnRevertableCountChanged(int value) => OnPropertyChanged(nameof(RevertButtonText));

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        var (tweaks, saved) = await Task.Run(() => (TweakCatalog.All, CustomSelectionStore.Load()));
        // Items that delete data are never restored from a saved selection: they must be ticked deliberately each time.
        saved?.ExceptWith(TweakCatalog.DataLossIds);
        BuildCatalog(tweaks);

        var profile = OptimizeLogic.ParseProfile(AppSettings.Current.LastProfile);
        _customSelection = saved;
        // A "custom" start without a saved set begins from the safe Recommended selection.
        ApplySelection(profile == OptimizeProfile.Custom ? saved ?? DefaultsOf(OptimizeProfile.Recommended) : DefaultsOf(profile));
        Profile = profile;
        UpdateTileSelection();

        RebuildRows();
        UpdateCounts();
        await RefreshStatesAsync();
    }

    protected override Task OnReturnAsync() => RefreshStatesAsync();

    public override void Deactivate()
    {
        if (_saveTimer.IsEnabled) FlushCustomSelection();
    }

    private void BuildCatalog(IReadOnlyList<Tweak> tweaks)
    {
        _items.Clear();
        foreach (var t in tweaks) _items.Add(new TweakItemViewModel(t, this));

        foreach (var p in new[] { OptimizeProfile.Normal, OptimizeProfile.Gaming, OptimizeProfile.Recommended })
            _profileDefaults[p] = TweakCatalog.DefaultsFor(OptimizeLogic.ToKind(p)!.Value).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

        _sections.Clear();
        foreach (var c in _items.Where(i => !i.IsAdvanced).Select(i => i.Category).Distinct())
            _sections[c] = new SectionRow(this, c);

        Categories.Clear();
        Categories.Add(new CategoryFilterViewModel(this, null, "ВСЕ", _items.Count) { IsSelected = true });
        foreach (var c in Enum.GetValues<TweakCategory>())
        {
            var count = _items.Count(i => i.Category == c);
            if (count > 0) Categories.Add(new CategoryFilterViewModel(this, c, OptimizeLogic.CategoryTitle(c), count));
        }
        CategoryFilter = null;
        OnPropertyChanged(nameof(TotalCount));
    }

    // ───────────────────────────── Detection ─────────────────────────────

    /// <summary>Reads the current state of every tweak off the UI thread, plus which ones MONOLITH can undo.</summary>
    private async Task RefreshStatesAsync()
    {
        if (_items.Count == 0 || _refreshing || IsBusy) return;
        _refreshing = true;
        IsDetecting = true;
        UpdateCounts();
        try
        {
            await RunBusyAsync("Проверка текущего состояния…", async () =>
            {
                await SystemProfile.LoadAsync();
                var tweaks = _items.Select(i => i.Tweak).ToList();
                var states = await TweakEngine.DetectAllAsync(tweaks);
                var revertable = await Task.Run(() => tweaks.Where(TweakEngine.CanRevert).Select(t => t.Id).ToHashSet(StringComparer.Ordinal));
                _restoreProtectionOn = await Task.Run(RestorePointService.IsEnabled);
                foreach (var i in _items)
                {
                    i.State = states.TryGetValue(i.Id, out var s) ? s : TweakState.Unknown;
                    i.CanRevert = revertable.Contains(i.Id);
                    if (!i.IsApplicable) i.SetChecked(false);
                    if (i.IsExpanded) _ = LoadChangesAsync(i);
                }
            });
        }
        finally
        {
            // If detection failed, don't leave rows stuck at "ПРОВЕРКА…".
            foreach (var i in _items) i.State ??= TweakState.Unknown;
            _detected = true;
            _refreshing = false;
            IsDetecting = false;
            UpdateCounts();
        }
    }

    /// <summary>Fills the "what will change" preview of an expanded row with the values read right now.</summary>
    internal async Task LoadChangesAsync(TweakItemViewModel item)
    {
        var planned = OptimizeLogic.PlannedChanges(item.Tweak);
        item.HasPlannedChanges = planned.Count > 0;
        if (planned.Count == 0)
        {
            item.Changes = [];
            return;
        }
        if (item.Changes.Count == 0)
            item.Changes = planned.Select(p => new ChangeLineViewModel(p.Target, "чтение…", p.NewValue)).ToList();

        var lines = await Task.Run(() => planned.Select(p => new ChangeLineViewModel(p.Target, SafeRead(p.ReadCurrent), p.NewValue)).ToList());
        item.Changes = lines;
    }

    private static string SafeRead(Func<string>? read)
    {
        try { return read?.Invoke() ?? "—"; }
        catch { return "не удалось прочитать"; }
    }

    // ───────────────────────────── Selection & profiles ─────────────────────────────

    internal void SelectProfile(OptimizeProfile profile)
    {
        if (profile == OptimizeProfile.Custom)
        {
            // Custom: bring back the user's own set (if they made one); otherwise keep what is checked now.
            if (Profile != OptimizeProfile.Custom && _customSelection is { Count: > 0 } saved) ApplySelection(saved);
            Profile = profile;
            RememberCustomSelection();
        }
        else
        {
            ApplySelection(DefaultsOf(profile));
            Profile = profile;
        }
        UpdateTileSelection();
        UpdateCounts();
    }

    private IReadOnlySet<string> DefaultsOf(OptimizeProfile profile) =>
        _profileDefaults.TryGetValue(profile, out var ids) ? ids : new HashSet<string>();

    /// <summary>Checks exactly the given ids (not-applicable never; expert only if its warning was accepted this session).</summary>
    private void ApplySelection(IReadOnlySet<string> ids)
    {
        foreach (var i in _items)
        {
            var want = ids.Contains(i.Id) && i.IsApplicable && (!i.IsAdvanced || _acceptedExpert.Contains(i.Id));
            i.SetChecked(want);
        }
    }

    private void UpdateTileSelection()
    {
        foreach (var t in Profiles) t.IsSelected = t.Kind == Profile;
    }

    /// <summary>Called after the user changed the selection by hand: the set becomes "СВОЙ НАБОР".</summary>
    internal void OnSelectionEditedByUser()
    {
        if (Profile != OptimizeProfile.Custom) Profile = OptimizeProfile.Custom;
        RememberCustomSelection();
        UpdateCounts();
    }

    private void RememberCustomSelection()
    {
        _customSelection = _items.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushCustomSelection()
    {
        _saveTimer.Stop();
        if (_customSelection is null) return;
        // Expert items need their warning accepted again and data-deleting items must be ticked again: neither is remembered.
        var notRemembered = _items.Where(i => i.IsAdvanced || i.HasDataLoss).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var ids = _customSelection.Where(id => !notRemembered.Contains(id)).ToList();
        _ = Task.Run(() => CustomSelectionStore.Save(ids));
    }

    /// <summary>The checkbox of an expert tweak was clicked: show its warning first.</summary>
    internal async Task ConfirmExpertAsync(TweakItemViewModel item)
    {
        if (!item.IsApplicable) return;
        var details = OptimizeLogic.PlannedChanges(item.Tweak).Select(c => $"{c.Target} → {c.NewValue}").ToList();
        if (item.HasRestart) details.Add($"После применения {item.Tweak.RestartText}.");
        var ok = await Ui.ConfirmAsync("ЭКСПЕРТНАЯ НАСТРОЙКА",
            $"«{item.Title}». {item.Description}\n\n{item.WarningText}",
            details, confirmText: "ПОНИМАЮ РИСК", cancelText: "ОТМЕНА", danger: true,
            footnote: "Не входит ни в один профиль. Откат — кнопкой «ОТКАТИТЬ» или в «Восстановлении».");
        if (!ok) return;
        _acceptedExpert.Add(item.Id);
        if (item.SetChecked(true)) OnSelectionEditedByUser();
    }

    /// <summary>
    /// "Select all" of a section. Checking never picks expert items or items that delete data (each needs a deliberate
    /// click of its own); unchecking clears every item of the section.
    /// </summary>
    internal void ToggleSection(SectionRow section)
    {
        var bulk = section.Items.Where(i => i.IsBulkSelectable).ToList();
        if (bulk.Count == 0) return;
        if (!bulk.All(i => i.IsChecked))
        {
            foreach (var i in bulk) i.SetChecked(true);
        }
        else
        {
            foreach (var i in section.Items.Where(i => i.IsApplicable)) i.SetChecked(false);
        }
        OnSelectionEditedByUser();
    }

    // ───────────────────────────── Filtering ─────────────────────────────

    internal void SelectCategory(TweakCategory? category)
    {
        CategoryFilter = category;
        foreach (var c in Categories) c.IsSelected = c.Category == category;
        RebuildRows();
    }

    internal void ToggleExpert()
    {
        if (_expertRow.IsOpen)
        {
            _expertOpen = false;
            _expertHiddenInSearch = HasSearch;
        }
        else
        {
            _expertOpen = true;
            _expertHiddenInSearch = false;
        }
        RebuildRows();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SearchText = "";
        _searchTimer.Stop(); // rebuild right away below instead of after the search debounce
        SelectCategory(null);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private void RebuildRows()
    {
        var desired = new List<object> { _profilesRow, _filterRow };
        var visible = _items.Where(i => (CategoryFilter is null || i.Category == CategoryFilter) && OptimizeLogic.Matches(i.Tweak, SearchText)).ToList();

        foreach (var category in Enum.GetValues<TweakCategory>())
        {
            var group = visible.Where(i => !i.IsAdvanced && i.Category == category).ToList();
            if (group.Count == 0) continue;
            if (!_sections.TryGetValue(category, out var section)) _sections[category] = section = new SectionRow(this, category);
            section.SetItems(group);
            desired.Add(section);
            for (var k = 0; k < group.Count; k++) group[k].IsFirstInGroup = k == 0;
            desired.AddRange(group);
        }

        var expert = visible.Where(i => i.IsAdvanced).ToList();
        _visibleExpert = expert.Count;
        if (expert.Count > 0)
        {
            // A search shows matching expert items even when the section is collapsed (unless the user hid it again).
            var open = _expertOpen || (HasSearch && !_expertHiddenInSearch);
            _expertRow.IsOpen = open;
            desired.Add(_expertRow);
            if (open)
            {
                for (var k = 0; k < expert.Count; k++) expert[k].IsFirstInGroup = k == 0;
                desired.AddRange(expert);
            }
        }

        if (visible.Count == 0) desired.Add(_emptyRow);
        VisibleCount = visible.Count;
        OptimizeLogic.Sync(Rows, desired);
        UpdateExpertRow();
    }

    // ───────────────────────────── Counters ─────────────────────────────

    private void UpdateCounts()
    {
        SelectedCount = _items.Count(i => i.IsChecked);
        ChangeCount = _items.Count(i => i.IsPending);
        RestartCount = _items.Count(i => i.IsPending && i.HasRestart);
        RevertableCount = _items.Count(i => i.IsChecked && i.CanRevert);
        AppliedTotal = _items.Count(i => i.IsApplied);
        ApplicableTotal = _items.Count(i => i.State is not null && i.IsApplicable);
        OnPropertyChanged(nameof(AppliedSummary));

        foreach (var tile in Profiles)
        {
            var defaults = DefaultsOf(tile.Kind);
            var members = tile.Kind == OptimizeProfile.Custom
                ? _items.Where(i => i.IsChecked).ToList()
                : _items.Where(i => defaults.Contains(i.Id) && i.IsApplicable && !i.IsAdvanced).ToList();
            tile.Total = members.Count;
            tile.ToChange = _detected ? members.Count(i => i.WillChange) : null;
            tile.Applied = _detected ? members.Count(i => i.IsApplied) : null;
        }

        foreach (var s in _sections.Values) s.Refresh();
        UpdateExpertRow();
        BarNote = BuildBarNote();
        RefreshCommandStates();
    }

    private void UpdateExpertRow()
    {
        var all = _items.Where(i => i.IsAdvanced).ToList();
        var chosen = all.Count(i => i.IsChecked);
        var count = _visibleExpert < all.Count
            ? $"{_visibleExpert} ИЗ {all.Count} {OptimizeLogic.Plural(all.Count, "ПУНКТА", "ПУНКТОВ", "ПУНКТОВ")}"
            : $"{all.Count} {OptimizeLogic.Plural(all.Count, "ПУНКТ", "ПУНКТА", "ПУНКТОВ")}";
        _expertRow.CountText = count + (chosen > 0 ? $" · ВЫБРАНО {chosen}" : "");
    }

    private string BuildBarNote()
    {
        if (_items.Count == 0) return _detected ? "Каталог оптимизаций пуст." : "Загрузка каталога…";
        if (IsReadOnly) return "Режим просмотра: применить и откатить изменения можно только с правами администратора.";
        if (!_detected || IsDetecting) return "Проверка текущего состояния системы…";
        if (SelectedCount == 0) return "Выберите профиль сверху или отметьте пункты в списке.";
        if (ChangeCount == 0) return "Все выбранные пункты уже применены — менять нечего.";
        var restart = RestartCount > 0
            ? $" · {RestartCount} {OptimizeLogic.Plural(RestartCount, "требует", "требуют", "требуют")} перезапуска"
            : "";
        var loss = _items.Any(i => i.IsPending && i.HasDataLoss) ? " · есть необратимое удаление данных" : "";
        var rp = " · " + OptimizeLogic.RestorePointBarNote(AppSettings.Current.CreateRestorePoints, _restoreProtectionOn);
        return $"Профиль «{CurrentTile.SentenceName}»{restart}{loss}{rp}.";
    }

    // ───────────────────────────── Commands: state ─────────────────────────────

    private bool CanApply() => CanModify && !IsDetecting && ChangeCount > 0;
    private bool CanRevertSelected() => CanModify && !IsDetecting && RevertableCount > 0;

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanModify));
        RefreshCommandStates();
        foreach (var i in _items) i.RefreshCommands();
    }

    private void RefreshCommandStates()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        RevertSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private Task RecheckAsync() => RefreshStatesAsync();

    [RelayCommand]
    private void OpenJournal() => Ui.Navigate("restore");

    [RelayCommand]
    private void RestartElevated()
    {
        if (Elevation.TryRelaunchElevated(["--page", "optimize"]))
            Application.Current.Shutdown();
    }

    [RelayCommand]
    private Task ResumePendingRestartAsync() => OfferRestartAsync(PendingRestart);

    // ───────────────────────────── Apply ─────────────────────────────

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (!RequireAdmin("Применение оптимизаций")) return;
        var targets = _items.Where(i => i.IsPending).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast("Все выбранные пункты уже применены — менять нечего.", ToastKind.Info);
            return;
        }
        var alreadyApplied = _items.Count(i => i.IsChecked && i.IsApplied);
        var lossy = targets.Where(t => t.HasDataLoss).ToList();

        // Deleting data cannot be undone: always ask, separately and as a danger, even with confirmations turned off.
        if (lossy.Count > 0 && !await ConfirmDataLossAsync(lossy)) return;

        if (AppSettings.Current.ConfirmBeforeApply)
        {
            var footnote = await RestorePointFootnoteAsync();
            var message = "MONOLITH изменит только перечисленные настройки. Перед каждым изменением исходное значение сохраняется в журнал — " +
                          "настройку можно откатить кнопкой «ОТКАТИТЬ» или в «Восстановлении»."
                          + (lossy.Count > 0 ? " Исключение — удалённые данные: их откат не вернёт." : "")
                          + (alreadyApplied > 0 ? $" Уже применённые пункты ({alreadyApplied}) не трогаются." : "");
            var ok = await Ui.ConfirmAsync(
                $"ПРИМЕНИТЬ {targets.Count} {OptimizeLogic.Plural(targets.Count, "ИЗМЕНЕНИЕ", "ИЗМЕНЕНИЯ", "ИЗМЕНЕНИЙ")}?",
                message, targets.Select(DescribeForConfirm), confirmText: "ПРИМЕНИТЬ", cancelText: "ОТМЕНА",
                danger: targets.Any(t => t.IsAdvanced) || lossy.Count > 0, footnote: footnote);
            if (!ok) return;
        }

        var batchTitle = Profile == OptimizeProfile.Custom
            ? "Оптимизация: свой набор"
            : $"Оптимизация: профиль «{CurrentTile.SentenceName}»";
        TweakRunResult? result = null;
        await RunBusyAsync("Применение изменений…", async () =>
        {
            var progress = new Progress<string>(p => BusyText = p.EndsWith('…') ? p : $"Применение: {p}");
            result = await TweakEngine.ApplyAsync(targets.Select(t => t.Tweak).ToList(), batchTitle, progress);
        });

        await RefreshStatesAsync();
        if (result is null) return;
        await ShowApplyResultAsync(result, targets);
        if (result.Applied > 0 && result.Restart != RestartRequirement.None)
            await OfferRestartAsync(result.Restart);
    }

    private static string DescribeForConfirm(TweakItemViewModel t)
    {
        var parts = new List<string> { t.Title };
        if (t.State == TweakState.Partial) parts[0] += " (сейчас применено частично)";
        parts.Add(t.RiskText.ToLowerInvariant());
        if (t.HasRestart) parts.Add(t.Tweak.RestartText);
        if (t.HasDataLoss) parts.Add("удаляет данные безвозвратно");
        return string.Join(" · ", parts);
    }

    /// <summary>The dedicated danger confirmation for items that delete user data (shown even when confirmations are off).</summary>
    private static Task<bool> ConfirmDataLossAsync(IReadOnlyList<TweakItemViewModel> lossy)
    {
        var message = lossy.Count == 1
            ? $"Пункт «{lossy[0].Title}» не только меняет настройку, но и удаляет данные. Настройку можно будет откатить, удалённые данные — нет."
            : "Эти пункты не только меняют настройки, но и удаляют данные. Настройки можно будет откатить, удалённые данные — нет.";
        return Ui.ConfirmAsync("ДАННЫЕ БУДУТ УДАЛЕНЫ НАВСЕГДА", message,
            lossy.Select(t => $"«{t.Title}»: {t.DataLossNote}"),
            confirmText: "ПОНИМАЮ, ПРОДОЛЖИТЬ", cancelText: "ОТМЕНА", danger: true,
            footnote: $"Не хотите терять данные — нажмите «ОТМЕНА» и снимите отметку {(lossy.Count == 1 ? "с этого пункта" : "с этих пунктов")}. " +
                      "Это предупреждение показывается всегда, даже если подтверждения отключены в настройках.");
    }

    private async Task<string> RestorePointFootnoteAsync()
    {
        var settingOn = AppSettings.Current.CreateRestorePoints;
        if (!settingOn) return OptimizeLogic.RestorePointFootnote(false, false);
        // Read it again right before applying: protection may have been switched on or off since the last check.
        _restoreProtectionOn = await Task.Run(RestorePointService.IsEnabled);
        return OptimizeLogic.RestorePointFootnote(true, _restoreProtectionOn == true);
    }

    private async Task ShowApplyResultAsync(TweakRunResult result, IReadOnlyList<TweakItemViewModel> targets)
    {
        var lines = new List<string>
        {
            OptimizeLogic.RestorePointResultLine(result.RestorePoint, AppSettings.Current.CreateRestorePoints),
        };
        foreach (var t in targets)
        {
            lines.Add(t.State switch
            {
                TweakState.Applied => $"ПРИМЕНЕНО — {t.Title}",
                TweakState.Partial => $"ЧАСТИЧНО — {t.Title}",
                TweakState.NotApplicable => $"ПРОПУЩЕНО — {t.Title}: {t.NotApplicableText}",
                _ => $"НЕ ПОДТВЕРЖДЕНО — {t.Title}",
            });
        }
        lines.AddRange(result.Errors.Select(e => $"ОШИБКА — {e}"));

        var verified = targets.Count(t => t.IsApplied);
        var title = result.Errors.Count > 0 ? "ГОТОВО С ОШИБКАМИ" : verified == targets.Count ? "ГОТОВО" : "ГОТОВО ЧАСТИЧНО";
        var message = $"Подтверждено {verified} из {targets.Count} {OptimizeLogic.Plural(targets.Count, "пункта", "пунктов", "пунктов")}, " +
                      $"изменено параметров системы: {result.Applied}." +
                      (result.Skipped > 0 ? $" Пропущено как неподходящие для этого ПК: {result.Skipped}." : "") +
                      " Откатить настройки можно кнопкой «ОТКАТИТЬ» у пункта или в разделе «Восстановление»." +
                      (targets.Any(t => t.HasDataLoss) ? " Удалённые данные откат не вернёт." : "");
        await Ui.AlertAsync(title, message, lines);
    }

    // ───────────────────────────── Revert ─────────────────────────────

    /// <summary>
    /// Row «ОТКАТИТЬ». Uses the same rule as <see cref="TweakEngine.CanRevert"/> and <see cref="TweakEngine.RevertAsync(Tweak)"/>:
    /// every active record the item touched, including records it shares with another item that changed the same setting
    /// first (e.g. «Максимальная производительность» after «Высокая производительность»: both change the active power plan).
    /// Restoring a shared record also undoes the other item, so the confirmation names it.
    /// </summary>
    internal async Task RevertOneAsync(TweakItemViewModel item)
    {
        if (!RequireAdmin("Откат изменений")) return;
        var own = new[] { OptimizeLogic.SourceOf(item.Id) };
        var records = OptimizeLogic.RecordsTouchedBy(ChangeJournal.Instance.ActiveRecords, own);
        if (records.Count == 0)
        {
            Ui.Toast("Для этого пункта нет изменений MONOLITH, которые можно откатить.", ToastKind.Info);
            await RefreshStatesAsync();
            return;
        }

        var shared = OptimizeLogic.SharedSources(records, own);
        var details = records.Select(DescribeRecord).ToList();
        if (shared.Count > 0) details.Add(AlsoRevertsLine(shared));
        var affected = new List<TweakItemViewModel> { item };
        affected.AddRange(ItemsOf(shared));
        var restart = affected.Select(t => t.Tweak.Restart).Max();

        var ok = await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?",
            $"«{item.Title}»: MONOLITH вернёт значения, которые были до применения." +
            (shared.Count > 0 ? " Этот параметр меняли и другие пункты — исходное значение одно, поэтому вернётся и их изменение." : ""),
            details, confirmText: "ОТКАТИТЬ", cancelText: "ОТМЕНА",
            footnote: restart != RestartRequirement.None ? $"После отката {RestartTextOf(restart)}." : null);
        if (!ok) return;

        RevertResult? result = null;
        await RunBusyAsync($"Откат: {item.Title}…", async () => result = await TweakEngine.RevertAsync(item.Tweak));
        await RefreshStatesAsync();
        await ReportRevertAsync(result, affected);
    }

    [RelayCommand(CanExecute = nameof(CanRevertSelected))]
    private async Task RevertSelectedAsync()
    {
        if (!RequireAdmin("Откат изменений")) return;
        var targets = _items.Where(i => i.IsChecked && i.CanRevert).ToList();
        if (targets.Count == 0)
        {
            Ui.Toast("Среди выбранных пунктов нет изменений MONOLITH, которые можно откатить.", ToastKind.Info);
            return;
        }

        var active = ChangeJournal.Instance.ActiveRecords;
        var details = targets.Select(t =>
        {
            var n = OptimizeLogic.RecordsTouchedBy(active, [OptimizeLogic.SourceOf(t.Id)]).Count;
            return $"{t.Title} — {n} {OptimizeLogic.Plural(n, "параметр", "параметра", "параметров")}";
        }).ToList();
        var own = targets.Select(t => OptimizeLogic.SourceOf(t.Id)).ToHashSet(StringComparer.Ordinal);
        var shared = OptimizeLogic.SharedSources(OptimizeLogic.RecordsTouchedBy(active, own), own);
        if (shared.Count > 0) details.Add(AlsoRevertsLine(shared));
        var affected = targets.Concat(ItemsOf(shared)).ToList();

        var restartNote = affected.Any(t => t.HasRestart) ? " Для части пунктов после отката понадобится перезапуск." : "";
        var ok = await Ui.ConfirmAsync(
            $"ОТКАТИТЬ {targets.Count} {OptimizeLogic.Plural(targets.Count, "ПУНКТ", "ПУНКТА", "ПУНКТОВ")}?",
            "MONOLITH вернёт значения, сохранённые в журнале перед применением." + restartNote,
            details, confirmText: "ОТКАТИТЬ", cancelText: "ОТМЕНА");
        if (!ok) return;

        RevertResult? result = null;
        await RunBusyAsync("Откат выбранных изменений…", async () => result = await TweakEngine.RevertAsync(targets.Select(t => t.Tweak)));
        await RefreshStatesAsync();
        await ReportRevertAsync(result, affected);
    }

    private static string DescribeRecord(ChangeRecord r) =>
        $"{OptimizeLogic.DescribeTarget(r.Target)}: {r.NewValue} → {r.OldValue}";

    /// <summary>"Также откатится: «…», «…»" for the other sources of shared journal records.</summary>
    private string AlsoRevertsLine(IReadOnlyList<string> sources) =>
        "Также откатится: " + string.Join(", ", sources.Select(s =>
            OptimizeLogic.DescribeSource(s, id => _items.FirstOrDefault(i => i.Id == id)?.Title))) +
        " — тот же параметр, одно исходное значение.";

    /// <summary>Optimize items behind "tweak:…" sources (other sources are not on this page).</summary>
    private IEnumerable<TweakItemViewModel> ItemsOf(IEnumerable<string> sources)
    {
        var ids = sources.Where(s => s.StartsWith("tweak:", StringComparison.Ordinal)).Select(s => s["tweak:".Length..])
                         .ToHashSet(StringComparer.Ordinal);
        return _items.Where(i => ids.Contains(i.Id));
    }

    private static string RestartTextOf(RestartRequirement restart) => restart switch
    {
        RestartRequirement.Explorer => "нужен перезапуск Проводника",
        RestartRequirement.SignOut => "нужен выход из системы",
        RestartRequirement.Reboot => "нужна перезагрузка",
        _ => "",
    };

    private async Task ReportRevertAsync(RevertResult? result, IReadOnlyList<TweakItemViewModel> targets)
    {
        if (result is null) return;
        if (result.Success)
        {
            Ui.Toast($"Возвращено параметров: {result.Reverted}.", ToastKind.Success, "ОТКАТ ВЫПОЛНЕН");
        }
        else
        {
            await Ui.AlertAsync("ОТКАТ С ОШИБКАМИ",
                $"Возвращено параметров: {result.Reverted}, не удалось: {result.Failed}. Повторить можно здесь или в разделе «Восстановление».",
                result.Errors);
        }
        if (result.Reverted == 0) return;
        var restart = targets.Select(t => t.Tweak.Restart).DefaultIfEmpty(RestartRequirement.None).Max();
        if (restart != RestartRequirement.None) await OfferRestartAsync(restart);
    }

    // ───────────────────────────── Restart / reboot ─────────────────────────────

    private async Task OfferRestartAsync(RestartRequirement kind)
    {
        switch (kind)
        {
            case RestartRequirement.Explorer:
            {
                var ok = await Ui.ConfirmAsync("ПЕРЕЗАПУСТИТЬ ПРОВОДНИК?",
                    "Часть изменений вступит в силу после перезапуска Проводника. Панель задач и рабочий стол пропадут на несколько секунд и вернутся сами.",
                    [
                        "Открытые окна папок закроются.",
                        "Незавершённое копирование или перемещение файлов в Проводнике прервётся.",
                        "Программы и документы останутся открытыми.",
                    ],
                    confirmText: "ПЕРЕЗАПУСТИТЬ", cancelText: "ПОЗЖЕ");
                if (!ok)
                {
                    Postpone(kind, "Изменения интерфейса вступят в силу после перезапуска Проводника или выхода из системы.");
                    return;
                }
                SessionActionResult? r = null;
                await RunBusyAsync("Перезапуск Проводника…", async () => r = await SessionActions.RestartExplorerAsync());
                if (r is null)
                {
                    Postpone(kind, null);
                    return;
                }
                Ui.Toast(r.Message, r.Success ? ToastKind.Success : ToastKind.Warning, "ПРОВОДНИК");
                PendingRestart = r.Success ? RestartRequirement.None : kind;
                return;
            }
            case RestartRequirement.SignOut:
            case RestartRequirement.Reboot:
            {
                var signOut = kind == RestartRequirement.SignOut;
                var ok = await Ui.ConfirmAsync(signOut ? "НУЖЕН ВЫХОД ИЗ СИСТЕМЫ" : "НУЖНА ПЕРЕЗАГРУЗКА",
                    signOut
                        ? "Часть изменений вступит в силу после выхода из системы и повторного входа. Перезагрузка тоже подойдёт."
                        : "Часть изменений вступит в силу только после перезагрузки компьютера.",
                    confirmText: "ПЕРЕЗАГРУЗИТЬ СЕЙЧАС", cancelText: "ПОЗЖЕ");
                if (ok)
                {
                    var sure = await Ui.ConfirmAsync("ПЕРЕЗАГРУЗИТЬ КОМПЬЮТЕР?",
                        $"Сохраните открытые документы. Через {SessionActions.RebootDelaySeconds} секунд после подтверждения Windows закроет все программы и перезагрузится.",
                        [
                            "Несохранённые данные в открытых программах будут потеряны.",
                            $"Передумали — нажмите Win+R, введите shutdown /a и нажмите Enter (в течение {SessionActions.RebootDelaySeconds} секунд).",
                        ],
                        confirmText: "ПЕРЕЗАГРУЗИТЬ", cancelText: "ОТМЕНА", danger: true);
                    if (sure)
                    {
                        var r = await SessionActions.ScheduleRebootAsync();
                        Ui.Toast(r.Message, r.Success ? ToastKind.Warning : ToastKind.Error, "ПЕРЕЗАГРУЗКА");
                        PendingRestart = r.Success ? RestartRequirement.None : kind;
                        return;
                    }
                }
                Postpone(kind, signOut
                    ? "Изменения вступят в силу после выхода из системы или перезагрузки."
                    : "Изменения вступят в силу после перезагрузки.");
                return;
            }
        }
    }

    private void Postpone(RestartRequirement kind, string? toast)
    {
        if (kind > PendingRestart) PendingRestart = kind;
        if (toast is not null) Ui.Toast(toast, ToastKind.Info);
    }
}
