using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Shell;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Optimize;

/// <summary>
/// The Optimize page: scenario tiles (a persistent pre-selection, the only place for scenarios) → filterable, grouped tweak
/// list with detected states → apply with a full preview and per-row undo (bulk undo lives in «Восстановление»). Every system
/// change goes through <see cref="TweakEngine"/> (journal + restore point): one apply = one batch.
/// </summary>
public sealed partial class OptimizeViewModel : PageViewModel
{
    public override string Key => "optimize";
    public override string Index => "02";
    public override string Title => "ОПТИМИЗАЦИЯ";
    public override string Subtitle => "Выберите сценарий, проверьте изменения и примените — изменённые настройки можно откатить.";
    public override string Glyph => Glyphs.Bolt;

    private const string SourcePrefix = "tweak:";

    private readonly List<TweakItemViewModel> _items = [];
    private readonly Dictionary<TweakCategory, SectionRow> _sections = [];
    private readonly Dictionary<OptimizeProfile, HashSet<string>> _profileDefaults = [];
    /// <summary>Every tile in order; <see cref="Profiles"/> holds the visible ones (the efficiency tile may be hidden).</summary>
    private readonly List<ProfileTileViewModel> _allTiles;
    /// <summary>SystemProfile.HardwareFactsKnown as of the last detection (tests set it through <see cref="MarkDetected"/>).</summary>
    private bool _hardwareFactsKnown;
    private bool _laptop;
    /// <summary>
    /// Ids whose AppliesTo rule rejects this PC (Windows 10, laptop-only, dual-CCD X3D…). Such a row stays not applicable even
    /// while a tweak it requires is pending: a pending Balanced plan does not bring the Windows 11 power mode to Windows 10.
    /// </summary>
    private HashSet<string> _hardwareExcluded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptedExpert = new(StringComparer.Ordinal);
    /// <summary>Tweak ids whose pending dependency is being evaluated right now (UI thread): guards against a Requires cycle.</summary>
    private readonly HashSet<string> _dependencyWalk = new(StringComparer.Ordinal);
    private readonly ProfilesRow _profilesRow;
    private readonly FilterRow _filterRow;
    private readonly EmptyRow _emptyRow;
    private readonly ExpertRow _expertRow;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _highlightTimer;
    private TweakItemViewModel? _highlighted;
    private HashSet<string>? _customSelection;
    private bool _expertOpen;
    private bool _expertHiddenInSearch;
    private int _visibleExpert;
    private bool _detected;
    private bool _refreshing;
    /// <summary>System Protection state cached off the UI thread by <see cref="RefreshStatesAsync"/>; null until known.</summary>
    private bool? _protectionOn;

    public OptimizeViewModel()
    {
        _profilesRow = new ProfilesRow(this);
        _filterRow = new FilterRow(this);
        _emptyRow = new EmptyRow(this);
        _expertRow = new ExpertRow(this);

        // One row, one radio group: РЕКОМЕНДУЕМЫЙ · ИГРЫ · РАБОТА И УЧЁБА · СТРИМИНГ И ЗАПИСЬ · ТИХИЙ ПК / НОУТБУК: АВТОНОМНОСТЬ · СВОЙ НАБОР.
        // The efficiency tile joins the row only after detection shows it has a member beyond a plan switch (UpdateCounts).
        _allTiles =
        [
            new ProfileTileViewModel(this, OptimizeProfile.Recommended, "01", "ПО УМОЛЧАНИЮ"),
            new ProfileTileViewModel(this, OptimizeProfile.Gaming, "02"),
            new ProfileTileViewModel(this, OptimizeProfile.Normal, "03"),
            new ProfileTileViewModel(this, OptimizeProfile.Streaming, "04"),
            new ProfileTileViewModel(this, OptimizeProfile.Efficiency, "05"),
            new ProfileTileViewModel(this, OptimizeProfile.Custom, "06"),
        ];
        Profiles = new ObservableCollection<ProfileTileViewModel>(_allTiles.Where(t => t.Kind != OptimizeProfile.Efficiency));
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
        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            if (_highlighted is not null) _highlighted.IsHighlighted = false;
            _highlighted = null;
        };
        // Already on the page (or loaded) when the request arrives: handle it now. Before the first load
        // the catalog is empty and LoadAsync takes the request instead.
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key && TotalCount > 0) ApplyFocus(Ui.TakeFocusRequest(Key));
        };

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
    [ObservableProperty] private int _appliedTotal;
    [ObservableProperty] private int _applicableTotal;
    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private string _barNote = "Загрузка каталога…";
    /// <summary>The row the view should scroll into view (set by <see cref="ApplyFocus"/>).</summary>
    [ObservableProperty] private TweakItemViewModel? _focusedItem;
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
    public string ApplyTooltip => IsReadOnly ? "Нужны права администратора" : "Показать полный список изменений и применить их";

    private ProfileTileViewModel CurrentTile => _allTiles.First(p => p.Kind == Profile);

    /// <summary>The efficiency tile is in the row (it has a real member on this PC and the hardware facts are known).</summary>
    internal bool IsEfficiencyTileVisible => Profiles.Any(t => t.Kind == OptimizeProfile.Efficiency);

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

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        var (tweaks, saved) = await Task.Run(() => (TweakCatalog.All, CustomSelectionStore.Load()));
        BuildCatalog(tweaks);

        var profile = OptimizeLogic.ParseProfile(AppSettings.Current.LastProfile);
        _customSelection = saved;
        // A "custom" start without a saved set begins from the safe Recommended selection.
        ApplySelection(profile == OptimizeProfile.Custom ? saved ?? DefaultsOf(OptimizeProfile.Recommended) : DefaultsOf(profile));
        Profile = profile;
        UpdateTileSelection();

        RebuildRows();
        UpdateCounts();
        ApplyFocus(Ui.TakeFocusRequest(Key));
        await RefreshStatesAsync();
    }

    protected override Task OnReturnAsync()
    {
        ApplyFocus(Ui.TakeFocusRequest(Key));
        return RefreshStatesAsync();
    }

    /// <summary>
    /// Shows one tweak: clears the search and category filter, opens the expert section if needed, expands the row and
    /// frames it for a moment. Only changes what is shown — never the selection or the system.
    /// </summary>
    private void ApplyFocus(string? id)
    {
        if (id is null) return;
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        _searchTimer.Stop();
        SearchText = "";
        _searchTimer.Stop(); // the setter restarted the debounce; rebuild right away instead
        SelectCategory(null);
        if (item.IsAdvanced)
        {
            _expertOpen = true;
            _expertHiddenInSearch = false;
        }
        RebuildRows();
        item.IsExpanded = true;

        if (_highlighted is not null && !ReferenceEquals(_highlighted, item)) _highlighted.IsHighlighted = false;
        _highlighted = item;
        item.IsHighlighted = true;
        _highlightTimer.Stop();
        _highlightTimer.Start();

        FocusedItem = null; // re-raise even when the same item is requested twice
        FocusedItem = item;
    }

    public override void Deactivate()
    {
        if (_saveTimer.IsEnabled) FlushCustomSelection();
    }

    /// <summary>The rows in catalog order (tests: selection rules with a synthetic catalog).</summary>
    internal IReadOnlyList<TweakItemViewModel> Items => _items;

    /// <summary>Builds the rows for <paramref name="tweaks"/> (internal so tests can load a synthetic catalog).</summary>
    internal void BuildCatalog(IReadOnlyList<Tweak> tweaks)
    {
        _items.Clear();
        foreach (var t in tweaks) _items.Add(new TweakItemViewModel(t, this));

        foreach (var p in OptimizeLogic.CatalogProfiles)
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

    /// <summary>Test hook: the defaults of a profile for a synthetic catalog (the real ones come from <see cref="TweakCatalog.DefaultsFor"/>).</summary>
    internal void SetProfileDefaults(OptimizeProfile profile, IEnumerable<string> ids)
    {
        _profileDefaults[profile] = ids.ToHashSet(StringComparer.Ordinal);
        UpdateCounts();
    }

    /// <summary>
    /// Test hook: counts as if detection has finished (tile «изменений» figures are shown only then) with the given hardware facts
    /// (the efficiency tile is retitled for a laptop and shown only when it has a member beyond a plan switch).
    /// </summary>
    internal void MarkDetected(bool hardwareFactsKnown = true, bool laptop = false)
    {
        _detected = true;
        _hardwareFactsKnown = hardwareFactsKnown;
        _laptop = laptop;
        ReconcileScenarioTiles();
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
                var sp = await SystemProfile.LoadAsync();
                _hardwareFactsKnown = sp.HardwareFactsKnown;
                _laptop = sp.HardwareFactsKnown && sp.IsLaptop;
                var tweaks = _items.Select(i => i.Tweak).ToList();
                _hardwareExcluded = tweaks.Where(t => TweakEngine.Applicability(t, sp) == false).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
                var states = await TweakEngine.DetectAllAsync(tweaks);
                var revertable = await Task.Run(() => tweaks.Where(TweakEngine.CanRevert).Select(t => t.Id).ToHashSet(StringComparer.Ordinal));
                _protectionOn = await Task.Run(RestorePointService.IsEnabled);
                foreach (var i in _items)
                {
                    i.State = states.TryGetValue(i.Id, out var s) ? s : TweakState.Unknown;
                    i.CanRevert = revertable.Contains(i.Id);
                }
                // Only after every state is known: a not-applicable row loses its check — unless it is a dependent row
                // whose dependency is checked and pending (SyncDependents re-checks those once more, for chains).
                foreach (var i in _items)
                {
                    if (i.IsChecked && !i.IsApplicableNow) i.SetChecked(false);
                }
                SyncDependents();
                foreach (var i in _items)
                {
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
            ReconcileScenarioTiles();
        }
    }

    /// <summary>
    /// After detection: the efficiency tile gets its laptop/desktop wording and joins or leaves the row (UpdateCounts); when
    /// it leaves while selected — e.g. the saved scenario from another PC — the page falls back to the default scenario.
    /// </summary>
    private void ReconcileScenarioTiles()
    {
        foreach (var t in _allTiles) t.RetitleEfficiency(_laptop);
        UpdateCounts();
        if (Profile == OptimizeProfile.Efficiency && !IsEfficiencyTileVisible) SelectProfile(OptimizeProfile.Recommended);
        OnPropertyChanged(nameof(ApplyButtonText));
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

    /// <summary>
    /// Checks exactly the given ids (not-applicable never — unless a tweak it requires is checked and pending in the same
    /// set; expert only if its warning was accepted this session). At most one member per exclusive group stays checked.
    /// </summary>
    private void ApplySelection(IReadOnlySet<string> ids)
    {
        foreach (var i in _items)
        {
            var want = ids.Contains(i.Id) && i.IsApplicable && (!i.IsAdvanced || _acceptedExpert.Contains(i.Id));
            i.SetChecked(want);
        }
        EnforceExclusiveGroups();
        // Second pass: dependent rows that are not applicable on their own but whose dependency is now pending.
        foreach (var i in _items)
        {
            if (i.IsApplicable || i.Tweak.Requires.Count == 0) continue;
            i.SetChecked(ids.Contains(i.Id) && i.IsApplicableNow && (!i.IsAdvanced || _acceptedExpert.Contains(i.Id)));
        }
        EnforceExclusiveGroups();
        SyncDependents();
    }

    // ───────────────────────────── Exclusive groups & dependencies ─────────────────────────────

    /// <summary>
    /// <paramref name="item"/> was just checked: the other checked members of its exclusive group (same system target)
    /// are unchecked, so one apply never writes two values to one target. Uses SetChecked — no re-entry into the setter.
    /// </summary>
    internal void EnforceExclusive(TweakItemViewModel item)
    {
        if (item.Tweak.ExclusiveGroup is null || !item.IsChecked) return;
        var ids = OptimizeLogic.MembersToUncheck(item.Tweak, _items.Select(i => (i.Tweak, i.IsChecked))).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var other in _items)
        {
            if (ids.Contains(other.Id)) other.SetChecked(false);
        }
    }

    /// <summary>After a bulk selection: at most one checked member per exclusive group (the first in catalog order stays).</summary>
    internal void EnforceExclusiveGroups()
    {
        var surplus = OptimizeLogic.ExclusiveSurplus(_items.Where(i => i.IsChecked).Select(i => i.Tweak));
        if (surplus.Count == 0) return;
        var ids = surplus.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var i in _items)
        {
            if (ids.Contains(i.Id)) i.SetChecked(false);
        }
    }

    /// <summary>
    /// Re-reads every dependent row (it shows «Применится после «…»» or the not-applicable reason) and unchecks the ones
    /// whose dependency is no longer checked and pending. Repeats for chains until nothing changes.
    /// </summary>
    internal void SyncDependents()
    {
        var dependents = _items.Where(i => i.Tweak.Requires.Count > 0).ToList();
        for (var guard = 0; guard <= dependents.Count; guard++)
        {
            var changed = false;
            foreach (var i in dependents)
            {
                if (i.IsChecked && !i.IsApplicableNow) changed |= i.SetChecked(false);
            }
            if (!changed) break;
        }
        foreach (var i in dependents) i.NotifyDependencyChanged();
    }

    /// <summary>
    /// The first tweak <paramref name="item"/> requires that is checked and pending in this same apply, or null. «Pending»
    /// follows the chain (the dependency may itself be pending only through its own dependency); a tweak already on the
    /// walk is never pending through itself, so a Requires cycle in a catalog ends here instead of in a stack overflow.
    /// </summary>
    internal TweakItemViewModel? PendingDependencyOf(TweakItemViewModel item)
    {
        if (IsHardwareExcluded(item.Id) || !_dependencyWalk.Add(item.Id)) return null;
        try
        {
            var id = OptimizeLogic.PendingDependency(item.Tweak, IsPendingRow);
            return id is null ? null : FindItem(id);
        }
        finally
        {
            _dependencyWalk.Remove(item.Id);
        }
    }

    /// <summary>Whether this PC's hardware/OS rules out the tweak (its AppliesTo), so no pending dependency can make it applicable.</summary>
    internal bool IsHardwareExcluded(string id) => _hardwareExcluded.Contains(id);

    /// <summary>Test hook: ids whose AppliesTo rejects the PC (the real set comes from detection).</summary>
    internal void SetHardwareExcluded(params string[] ids) => _hardwareExcluded = ids.ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether the row with this id is checked and will change in this apply (unknown ids are not pending).</summary>
    internal bool IsPendingRow(string id) => FindItem(id) is { IsPending: true };

    private TweakItemViewModel? FindItem(string id) => _items.FirstOrDefault(i => i.Id == id);

    private void UpdateTileSelection()
    {
        foreach (var t in Profiles) t.IsSelected = t.Kind == Profile;
    }

    /// <summary>Called after the user changed the selection by hand: the set becomes "СВОЙ НАБОР".</summary>
    internal void OnSelectionEditedByUser()
    {
        if (Profile != OptimizeProfile.Custom) Profile = OptimizeProfile.Custom;
        SyncDependents();
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
        var advanced = _items.Where(i => i.IsAdvanced).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var ids = _customSelection.Where(id => !advanced.Contains(id)).ToList();
        _ = Task.Run(() => CustomSelectionStore.Save(ids));
    }

    /// <summary>The checkbox of an expert tweak was clicked: show its warning first.</summary>
    internal async Task ConfirmExpertAsync(TweakItemViewModel item)
    {
        if (!item.IsApplicableNow) return;
        var details = OptimizeLogic.PlannedChanges(item.Tweak).Select(c => $"{c.Target} → {c.NewValue}").ToList();
        if (item.PendingDependency is { } dependency) details.Add(OptimizeLogic.DependencyNote(dependency.Title));
        if (item.HasRestart) details.Add($"После применения {item.Tweak.RestartText}.");
        var ok = await Ui.ConfirmAsync("ЭКСПЕРТНАЯ НАСТРОЙКА",
            $"«{item.Title}». {item.Description}\n\n{item.WarningText}",
            details, confirmText: "ПОНИМАЮ РИСК", cancelText: "ОТМЕНА", danger: true,
            footnote: "Не входит ни в один профиль. Откат — кнопкой «ОТКАТИТЬ» или в «Восстановлении».");
        if (!ok) return;
        _acceptedExpert.Add(item.Id);
        if (!item.SetChecked(true)) return;
        EnforceExclusive(item);
        OnSelectionEditedByUser();
    }

    internal void ToggleSection(SectionRow section)
    {
        // Expert items need their own warning, data-loss items must be picked one by one: bulk select skips both.
        var selectable = section.Items.Where(SectionRow.IsSelectable).ToList();
        if (selectable.Count == 0) return;
        var check = !OptimizeLogic.AllSelected(selectable.Select(i => (i.Tweak, i.IsChecked)).ToList());
        foreach (var i in selectable) i.SetChecked(check);
        // One member per group (the first in catalog order), then dependents the pending members make applicable.
        EnforceExclusiveGroups();
        if (check)
        {
            foreach (var i in section.Items)
            {
                if (!i.IsApplicable && !i.IsAdvanced && !i.HasDataLoss && i.IsApplicableNow) i.SetChecked(true);
            }
            EnforceExclusiveGroups();
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
            for (var k = 0; k < group.Count; k++) { group[k].IsFirstInGroup = k == 0; group[k].IsLastInGroup = k == group.Count - 1; }
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
                for (var k = 0; k < expert.Count; k++) { expert[k].IsFirstInGroup = k == 0; expert[k].IsLastInGroup = k == expert.Count - 1; }
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
        AppliedTotal = _items.Count(i => i.IsApplied);
        ApplicableTotal = _items.Count(i => i.State is not null && i.IsApplicable);
        OnPropertyChanged(nameof(AppliedSummary));

        var visible = new List<ProfileTileViewModel>(_allTiles.Count);
        foreach (var tile in _allTiles)
        {
            var defaults = DefaultsOf(tile.Kind);
            var members = tile.Kind == OptimizeProfile.Custom
                ? _items.Where(i => i.IsChecked).ToList()
                : _items.Where(i => !i.IsAdvanced && !IsHardwareExcluded(i.Id) && OptimizeLogic.IsProfileMember(i.Tweak, i.IsApplicable, defaults,
                    rid => FindItem(rid) is { IsApplicable: true, IsAdvanced: false, State: { } s } && OptimizeLogic.WillChange(s))).ToList();
            tile.Total = members.Count;
            // A member that is not applicable on its own is there through its dependency: selecting the profile changes it too.
            tile.ToChange = _detected ? members.Count(i => i.WillChange || !i.IsApplicable) : null;
            tile.Applied = _detected ? members.Count(i => i.IsApplied) : null;
            // The efficiency tile needs a member that makes something quieter (not only a plan switch) and known hardware facts.
            if (tile.Kind != OptimizeProfile.Efficiency
                || (_detected && OptimizeLogic.EfficiencyTileVisible(_hardwareFactsKnown, members.Select(m => m.Tweak))))
                visible.Add(tile);
        }
        OptimizeLogic.Sync(Profiles, visible);

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
        if (SelectedCount == 0) return "Выберите сценарий сверху или отметьте пункты в списке.";
        if (ChangeCount == 0) return "Все выбранные пункты уже применены — менять нечего.";
        var restart = RestartCount > 0
            ? $" · {RestartCount} {OptimizeLogic.Plural(RestartCount, "требует", "требуют", "требуют")} перезапуска"
            : "";
        var rp = !AppSettings.Current.CreateRestorePoints
            ? " · точка восстановления отключена в «Восстановлении»"
            : _protectionOn == false
                ? " · защита системы выключена — точки восстановления не будет, откат через журнал"
                : _protectionOn == true
                    ? " · перед изменениями будет создана точка восстановления"
                    : "";
        return $"Сценарий «{CurrentTile.SentenceName}»{restart}{rp}.";
    }

    // ───────────────────────────── Commands: state ─────────────────────────────

    private bool CanApply() => CanModify && !IsDetecting && ChangeCount > 0;

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(CanModify));
        RefreshCommandStates();
        foreach (var i in _items) i.RefreshCommands();
    }

    private void RefreshCommandStates()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        ExportProfileCommand.NotifyCanExecuteChanged();
        ImportProfileCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private Task RecheckAsync() => RefreshStatesAsync();

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

        // Irreversible data loss is always confirmed, even when the user turned confirmations off.
        var lossy = targets.Where(t => t.HasDataLoss).ToList();
        if (AppSettings.Current.ConfirmBeforeApply || lossy.Count > 0)
        {
            var footnote = await RestorePointFootnoteAsync();
            var message = (lossy.Count > 0
                              ? "MONOLITH изменит только перечисленные настройки. Перед каждым изменением исходное значение сохраняется в журнал, поэтому настройки можно откатить. Исключение: удалённые данные не восстанавливаются."
                              : "MONOLITH изменит только перечисленные настройки. Перед каждым изменением исходное значение сохраняется в журнал, поэтому любой пункт можно откатить.")
                          + (alreadyApplied > 0 ? $" Уже применённые пункты ({alreadyApplied}) не трогаются." : "");
            var ok = await Ui.ConfirmAsync(
                $"ПРИМЕНИТЬ {targets.Count} {OptimizeLogic.Plural(targets.Count, "ИЗМЕНЕНИЕ", "ИЗМЕНЕНИЯ", "ИЗМЕНЕНИЙ")}?",
                message, targets.Select(DescribeForConfirm), confirmText: "ПРИМЕНИТЬ", cancelText: "ОТМЕНА",
                danger: targets.Any(t => t.IsAdvanced) || lossy.Count > 0, footnote: footnote);
            if (!ok) return;
        }

        // The scenario as applied: the result dialog names its manual follow-ups even if the selection changes meanwhile.
        var scenario = Profile;
        var batchTitle = OptimizeLogic.BatchTitle(scenario, _laptop);
        TweakRunResult? result = null;
        await RunBusyAsync("Применение изменений…", async () =>
        {
            var progress = new Progress<string>(p => BusyText = p.EndsWith('…') ? p : $"Применение: {p}");
            result = await TweakEngine.ApplyAsync(targets.Select(t => t.Tweak).ToList(), batchTitle, progress);
        });

        await RefreshStatesAsync();
        if (result is null) return;
        await ShowApplyResultAsync(result, targets, scenario);
        if (result.Applied > 0 && result.Restart != RestartRequirement.None)
            await OfferRestartAsync(result.Restart);
    }

    private string DescribeForConfirm(TweakItemViewModel t)
    {
        var parts = new List<string> { t.Title };
        if (t.State == TweakState.Partial) parts[0] += " (сейчас применено частично)";
        parts.Add(t.RiskText.ToLowerInvariant());
        if (t.HasRestart) parts.Add(t.Tweak.RestartText);
        if (t.DataLossNote is { } loss) parts.Add(loss);
        // Another member of the same group is applied by MONOLITH: this change gets its own journal layer on top of it,
        // so undoing it brings that member's value back exactly.
        // The Windows Update group is one-of-two by refusal (the second item refuses while the first is active), not by
        // layering, so there is nothing to «replace».
        if (t.Tweak.ExclusiveGroup != TweakCatalog.WuFeatureUpdatesGroup
            && OptimizeLogic.ReplacesNote(_items.Where(i => i.CanRevert && t.Tweak.SharesGroupWith(i.Tweak)).Select(i => i.Title)) is { } replaces)
            parts.Add(replaces);
        if (t.DependencySatisfiedByPending && t.PendingDependency is { } dependency) parts.Add($"после «{dependency.Title}»");
        return string.Join(" · ", parts);
    }

    private static async Task<string> RestorePointFootnoteAsync()
    {
        if (!AppSettings.Current.CreateRestorePoints)
            return "Точка восстановления отключена в разделе «Восстановление». Откат — через журнал MONOLITH.";
        var enabled = await Task.Run(RestorePointService.IsEnabled);
        return enabled
            ? "Перед изменениями будет создана точка восстановления Windows."
            : "Защита системы выключена — точки восстановления не будет. Откат — через журнал MONOLITH.";
    }

    private async Task ShowApplyResultAsync(TweakRunResult result, IReadOnlyList<TweakItemViewModel> targets, OptimizeProfile scenario)
    {
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(result.RestorePoint)
                ? "Точка восстановления не создавалась (отключено в разделе «Восстановление»). Откат — через журнал MONOLITH."
                : result.RestorePoint!,
        };
        foreach (var t in targets)
        {
            if (result.SkippedIds.Contains(t.Id)) continue; // listed below with the reason the engine gave
            lines.Add(t.State switch
            {
                TweakState.Applied => $"ПРИМЕНЕНО — {t.Title}",
                TweakState.Partial => $"ЧАСТИЧНО — {t.Title}",
                TweakState.NotApplicable => $"ПРОПУЩЕНО — {t.Title}: {t.NotApplicableText}",
                _ => $"НЕ ПОДТВЕРЖДЕНО — {t.Title}",
            });
        }
        lines.AddRange(result.SkippedReasons.Select(r => $"ПРОПУЩЕНО — {r}"));
        lines.AddRange(result.Errors.Select(e => $"ОШИБКА — {e}"));
        // What the scenario leaves to the user because MONOLITH must not write it (vendor fan curves, charge limits, DND blobs).
        // Each line carries the «ВРУЧНУЮ —» label, like the «ПРИМЕНЕНО —» lines above it (the dialog lists bullets, not headers).
        var manual = OptimizeLogic.ManualSteps(scenario, _laptop);
        if (manual.Count > 0)
        {
            lines.AddRange(manual.Select(m => $"{OptimizeLogic.ManualStepsHeader} — {m}"));
        }

        var verified = targets.Count(t => t.IsApplied);
        var title = result.Errors.Count > 0 ? "ГОТОВО С ОШИБКАМИ" : verified == targets.Count ? "ГОТОВО" : "ГОТОВО ЧАСТИЧНО";
        var message = $"Подтверждено {verified} из {targets.Count} {OptimizeLogic.Plural(targets.Count, "пункта", "пунктов", "пунктов")}, " +
                      $"изменено параметров системы: {result.Applied}." +
                      (result.Skipped > 0 ? $" Пропущено как неподходящие для этого ПК: {result.Skipped}." : "") +
                      (result.SkippedReasons.Count > 0 ? $" Пропущено из-за зависимостей: {result.SkippedReasons.Count}." : "") +
                      " Откатить можно кнопкой «ОТКАТИТЬ» у пункта или в разделе «Восстановление».";
        await Ui.AlertAsync(title, message, lines);
    }

    // ───────────────────────────── Revert ─────────────────────────────

    internal async Task RevertOneAsync(TweakItemViewModel item)
    {
        if (!RequireAdmin("Откат изменений")) return;
        var journal = ChangeJournal.Instance;
        // The same selection the revert makes: older per-user records under this account's own later change stay.
        var (records, left) = journal.SourceCandidates([SourcePrefix + item.Id]);
        if (records.Count == 0)
        {
            var why = left.Count > 0 ? await Task.Run(() => RestoreFormat.LeftText(left, UserScope.CurrentSid, UserScope.AccountName)) : "";
            Ui.Toast(why.Length > 0 ? why : "Для этого пункта нет изменений MONOLITH, которые можно откатить.", ToastKind.Info);
            await RefreshStatesAsync();
            return;
        }

        // Settings a later change (another tweak or page) has set again keep that value: only this tweak's layer goes.
        var own = new HashSet<ChangeRecord>(records, ChangeJournal.ByReference);
        var (keepers, account, leftText) = await Task.Run(() => (
            records.ToDictionary(r => r, r => journal.ActiveLayers(r.Target)
                .SkipWhile(x => !ReferenceEquals(x, r)).Skip(1).FirstOrDefault(x => !own.Contains(x)), ChangeJournal.ByReference),
            records.Any(r => r.IsLegacyPerUser) && UserScope.CurrentSid is { } sid ? UserScope.AccountName(sid) : null,
            RestoreFormat.LeftText(left, UserScope.CurrentSid, UserScope.AccountName)));
        var details = records.Select(r => keepers[r] is { } keeper
            ? $"{OptimizeLogic.DescribeTarget(r.Target)}: останется «{keeper.NewValue}» — его позже задал «{keeper.Title}», исходное значение перейдёт к нему"
            : DescribeRecord(r)).ToList();
        if (AlsoRevertsLine(records.Where(r => r.IsLegacyMerged), [item.Id]) is { } also) details.Add(also);
        if (account is not null)
            details.Add($"Часть записей сделана старой версией MONOLITH без учётной записи: исходные значения запишутся в профиль {account}. " +
                        "Если изменения делали под другой учётной записью, у неё они останутся.");
        if (leftText.Length > 0) details.Add("Не войдут в откат: " + leftText);
        var ok = await Ui.ConfirmAsync("ОТКАТИТЬ ИЗМЕНЕНИЕ?",
            $"«{item.Title}»: MONOLITH вернёт значения, которые были до применения этого пункта.",
            details, confirmText: "ОТКАТИТЬ", cancelText: "ОТМЕНА",
            footnote: item.HasRestart ? $"После отката {item.Tweak.RestartText}." : null);
        if (!ok) return;

        RevertResult? result = null;
        await RunBusyAsync($"Откат: {item.Title}…", async () =>
            result = await Task.Run(() => TweakEngine.RevertAsync(item.Tweak, LiveState.VerifyRevert)));
        await RefreshStatesAsync();
        await ReportRevertAsync(result, [item]);
    }

    /// <summary>
    /// A merged journal record from an older build keeps the original value from before the first tweak touched the
    /// target, so reverting it also undoes every other tweak recorded on it. Names those tweaks, or returns null.
    /// (New changes of a journaled target get their own layer and never need this line.)
    /// </summary>
    private string? AlsoRevertsLine(IEnumerable<ChangeRecord> records, IReadOnlyCollection<string> ids)
    {
        var others = OptimizeLogic.OtherTweakSources(records.SelectMany(r => r.AlsoSources.Prepend(r.Source)), SourcePrefix, ids);
        if (others.Count == 0) return null;
        var titles = others.Select(id => _items.FirstOrDefault(i => i.Id == id)?.Title is { } t
            ? $"«{t}»"
            : $"изменение раздела «{OptimizeLogic.SourceLabel(id)}»");
        return "Также откатит: " + string.Join(", ", titles) + " — они меняли те же параметры.";
    }

    private static string DescribeRecord(ChangeRecord r) =>
        $"{OptimizeLogic.DescribeTarget(r.Target)}: {r.NewValue} → {r.OldValue}";

    private async Task ReportRevertAsync(RevertResult? result, IReadOnlyList<TweakItemViewModel> targets)
    {
        if (result is null) return;
        var check = result.VerificationText is { } v ? " " + v : "";
        // Only values actually put back count as returned; hand-overs left a later change working and the system as it was.
        var counts = RestoreFormat.RevertCounts(result, "Возвращено параметров");
        var left = RestoreFormat.LeftText(result.Left, UserScope.CurrentSid, UserScope.CachedAccountName);
        var tail = left.Length > 0 ? " " + left : "";
        if (!result.JournalSaved)
            Ui.Toast("Откат выполнен, но журнал не удалось сохранить на диск — после перезапуска записи могут снова показаться активными.",
                ToastKind.Warning, "ЖУРНАЛ НЕ СОХРАНЁН");
        if (result.Success && result.Mismatches.Count == 0)
        {
            if (left.Length == 0) Ui.Toast(counts + check, ToastKind.Success, "ОТКАТ ВЫПОЛНЕН");
            else Ui.Toast(counts + check + tail, ToastKind.Warning, "ОТКАЧЕНО НЕ ВСЁ");
        }
        else if (result.Success)
        {
            await Ui.AlertAsync("ОТКАТ НЕ СОВПАЛ С ИСХОДНЫМ",
                $"{counts}{check} После отката MONOLITH прочитал значения заново: эти отличаются от исходных " +
                "(их могла сразу изменить Windows, политика или другая программа). Подробности — в разделе «Восстановление»." + tail,
                result.Mismatches);
        }
        else
        {
            await Ui.AlertAsync("ОТКАТ С ОШИБКАМИ",
                $"{counts} Не удалось: {result.Failed}.{check} Повторить можно здесь или в разделе «Восстановление»." + tail,
                result.Errors.Concat(result.Mismatches.Select(m => "НЕ СОВПАДАЕТ · " + m)));
        }
        if (result.Restored == 0) return; // nothing changed in the system: no restart to offer
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
