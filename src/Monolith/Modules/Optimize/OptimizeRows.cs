using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Tweaks;

namespace Monolith.Modules.Optimize;

// The Optimize page is one virtualized list of heterogeneous rows:
//   (SectionRow, TweakItemViewModel…)* | ExpertRow → (TweakItemViewModel…)* | EmptyRow
// The profile tiles and the filter bar are fixed above the list (they bind to the page directly).
// Each row type has its own DataTemplate in OptimizeView.xaml.

/// <summary>Shown when nothing matches the filter.</summary>
public sealed class EmptyRow(OptimizeViewModel page)
{
    public OptimizeViewModel Page { get; } = page;
}

/// <summary>One scenario tile: name, one-line description, tooltip and what it would do on this PC.</summary>
public sealed partial class ProfileTileViewModel : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    /// <summary>Texts come from <see cref="OptimizeLogic"/> (static per scenario); the efficiency tile is retitled for laptops.</summary>
    public ProfileTileViewModel(OptimizeViewModel owner, OptimizeProfile kind, string index, string? badge = null)
    {
        _owner = owner;
        Kind = kind;
        Index = index;
        Badge = badge;
        RetitleEfficiency(laptop: false);
    }

    /// <summary>Longer explanation shown as the tile's tooltip: what the scenario does and what it does not touch.</summary>
    [ObservableProperty] private string _hint = "";
    /// <summary>UPPER-CASE tile title («ТИХИЙ ПК»); the view shows it in sentence case.</summary>
    [ObservableProperty] private string _title = "";
    /// <summary>Name for running text and batch titles: «Игры», «Ноутбук: автономность».</summary>
    [ObservableProperty] private string _sentenceName = "";
    /// <summary>One trimmed line under the title.</summary>
    [ObservableProperty] private string _description = "";

    public OptimizeProfile Kind { get; }
    public string Index { get; }
    /// <summary>Optional small mark next to the title ("ПО УМОЛЧАНИЮ").</summary>
    public string? Badge { get; }

    /// <summary>
    /// Chooses the laptop or desktop wording («Ноутбук: автономность» / «Тихий ПК» and the matching hint). Called once the
    /// hardware facts are known; a no-op for the other scenarios, whose texts do not depend on the PC.
    /// </summary>
    public void RetitleEfficiency(bool laptop)
    {
        Title = OptimizeLogic.ScenarioTitle(Kind, laptop);
        SentenceName = OptimizeLogic.ScenarioName(Kind, laptop);
        Description = OptimizeLogic.ScenarioDescription(Kind, laptop);
        Hint = OptimizeLogic.ScenarioHint(Kind, laptop);
    }

    [ObservableProperty] private bool _isSelected;
    /// <summary>How many of the profile's tweaks would change something now (null = not detected yet).</summary>
    [ObservableProperty] private int? _toChange;
    [ObservableProperty] private int? _applied;
    [ObservableProperty] private int _total;

    public string ToChangeText => ToChange?.ToString() ?? "—";
    public string AppliedText => Applied?.ToString() ?? "—";
    public string TotalText => $"{Total} {OptimizeLogic.Plural(Total, "ПУНКТ", "ПУНКТА", "ПУНКТОВ")}";

    partial void OnToChangeChanged(int? value) => OnPropertyChanged(nameof(ToChangeText));
    partial void OnAppliedChanged(int? value) => OnPropertyChanged(nameof(AppliedText));
    partial void OnTotalChanged(int value) => OnPropertyChanged(nameof(TotalText));

    /// <summary>How many changes the button would apply: the page's pending count on the selected tile, else this profile's.</summary>
    public int PendingChanges => IsSelected ? _owner.ChangeCount : ToChange ?? 0;

    /// <summary>Applies (selecting first) rather than only selects: there is something to change, or this is the selected set.</summary>
    public bool ActApplies => (Kind != OptimizeProfile.Custom || IsSelected) && PendingChanges > 0;

    /// <summary>The tile's one button: «ПРИМЕНИТЬ · N» (current tile: all pending), «ВЫБРАТЬ» (nothing to change / own set) or «ВСЁ ПРИМЕНЕНО».</summary>
    public string ActText => ActApplies ? $"ПРИМЕНИТЬ · {PendingChanges}" : IsSelected ? "ВСЁ ПРИМЕНЕНО" : "ВЫБРАТЬ";

    /// <summary>The line under the tile's button saying why it only selects.</summary>
    public string ActNote => ActApplies ? "" : IsSelected ? "Менять нечего" : Kind == OptimizeProfile.Custom ? "Отметьте пункты ниже" : "Уже применён";

    partial void OnIsSelectedChanged(bool value) => RefreshAct();

    internal void RefreshAct()
    {
        OnPropertyChanged(nameof(PendingChanges));
        OnPropertyChanged(nameof(ActApplies));
        OnPropertyChanged(nameof(ActText));
        OnPropertyChanged(nameof(ActNote));
        ActCommand.NotifyCanExecuteChanged();
    }

    private bool CanAct() => _owner.CanActOnTile && !(IsSelected && !ActApplies);

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task ActAsync()
    {
        if (ActApplies) return _owner.ApplyProfileCommand.ExecuteAsync(Kind.ToString());
        _owner.SelectProfile(Kind);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Select() => _owner.SelectProfile(Kind);
}

/// <summary>One category segment in the filter bar (Category = null means "all").</summary>
public sealed partial class CategoryFilterViewModel : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public CategoryFilterViewModel(OptimizeViewModel owner, TweakCategory? category, string title, int count, bool isExpert = false)
    {
        _owner = owner;
        Category = category;
        Title = title;
        Count = count;
        IsExpert = isExpert;
    }

    public TweakCategory? Category { get; }
    /// <summary>The «ЭКСПЕРТ · N» segment (only expert items).</summary>
    public bool IsExpert { get; }
    public string Title { get; }
    public int Count { get; }

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select()
    {
        if (IsExpert) _owner.SelectExpertTab();
        else _owner.SelectCategory(Category);
    }
}

/// <summary>Section header for one category: title, short explanation, counters and "select all".</summary>
public sealed partial class SectionRow : ObservableObject
{
    private readonly OptimizeViewModel _owner;
    private IReadOnlyList<TweakItemViewModel> _items = [];

    public SectionRow(OptimizeViewModel owner, TweakCategory category)
    {
        _owner = owner;
        Category = category;
        Title = OptimizeLogic.CategoryTitle(category);
        Description = OptimizeLogic.CategoryDescription(category);
    }

    public TweakCategory Category { get; }
    public string Title { get; }
    public string Description { get; }

    [ObservableProperty] private string _countText = "";
    /// <summary>true = all selectable items checked, false = none, null = some.</summary>
    [ObservableProperty] private bool? _checkState = false;
    [ObservableProperty] private bool _hasSelectable;
    /// <summary>The visible caption of the section's select button («ОТМЕТИТЬ ВСЕ» / «СНЯТЬ ВСЕ»).</summary>
    [ObservableProperty] private string _toggleAllText = "ОТМЕТИТЬ ВСЕ";

    public IReadOnlyList<TweakItemViewModel> Items => _items;

    internal void SetItems(IReadOnlyList<TweakItemViewModel> items)
    {
        _items = items;
        Refresh();
    }

    internal void Refresh()
    {
        var applied = _items.Count(i => i.IsApplied);
        CountText = $"{_items.Count} {OptimizeLogic.Plural(_items.Count, "ПУНКТ", "ПУНКТА", "ПУНКТОВ")} · {applied} ПРИМЕНЕНО";
        var selectable = _items.Where(IsSelectable).Select(i => (i.Tweak, i.IsChecked)).ToList();
        HasSelectable = selectable.Count > 0;
        var anyChecked = selectable.Any(r => r.IsChecked);
        CheckState = !anyChecked ? false : OptimizeLogic.AllSelected(selectable) ? true : null;
        ToggleAllText = !HasSelectable ? "НЕТ ДОСТУПНЫХ" : CheckState == true ? "СНЯТЬ ВСЕ" : "ОТМЕТИТЬ ВСЕ";
    }

    /// <summary>
    /// Rows the header checkbox selects and judges: applicable, not expert (own warning) and without irreversible data
    /// loss (picked one by one). Dependent rows made applicable by a pending dependency follow their dependency instead.
    /// </summary>
    internal static bool IsSelectable(TweakItemViewModel i) => i.IsApplicable && !i.IsAdvanced && !i.HasDataLoss;

    [RelayCommand]
    private void ToggleAll() => _owner.ToggleSection(this);
}

/// <summary>Hatched, always-visible header of the expert items (what makes them expert is written out, not hidden).</summary>
public sealed partial class ExpertRow : ObservableObject
{
    public ExpertRow(OptimizeViewModel owner) => Page = owner;

    public OptimizeViewModel Page { get; }
    [ObservableProperty] private string _countText = "";
}
