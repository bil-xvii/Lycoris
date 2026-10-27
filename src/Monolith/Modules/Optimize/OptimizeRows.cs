using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Tweaks;

namespace Monolith.Modules.Optimize;

// The Optimize page is one virtualized list of heterogeneous rows:
//   ProfilesRow → FilterRow → (SectionRow, TweakItemViewModel…)* → ExpertRow → (TweakItemViewModel…)* | EmptyRow
// Each row type has its own DataTemplate in OptimizeView.xaml.

/// <summary>The four profile tiles at the top of the list.</summary>
public sealed class ProfilesRow(OptimizeViewModel page)
{
    public OptimizeViewModel Page { get; } = page;
}

/// <summary>Category segments + search box.</summary>
public sealed class FilterRow(OptimizeViewModel page)
{
    public OptimizeViewModel Page { get; } = page;
}

/// <summary>Shown when nothing matches the filter.</summary>
public sealed class EmptyRow(OptimizeViewModel page)
{
    public OptimizeViewModel Page { get; } = page;
}

/// <summary>One profile tile: name, description and what it would do on this PC.</summary>
public sealed partial class ProfileTileViewModel : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public ProfileTileViewModel(OptimizeViewModel owner, OptimizeProfile kind, string index, string title, string sentenceName,
        string description, string hint, string? badge = null)
    {
        _owner = owner;
        Kind = kind;
        Index = index;
        Title = title;
        SentenceName = sentenceName;
        Description = description;
        Hint = hint;
        Badge = badge;
    }

    /// <summary>Longer explanation shown as the tile's tooltip.</summary>
    public string Hint { get; }

    public OptimizeProfile Kind { get; }
    public string Index { get; }
    public string Title { get; }
    /// <summary>Name for running text: «Игровой».</summary>
    public string SentenceName { get; }
    public string Description { get; }
    /// <summary>Optional small mark next to the index ("ПО УМОЛЧАНИЮ").</summary>
    public string? Badge { get; }

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

    [RelayCommand]
    private void Select() => _owner.SelectProfile(Kind);
}

/// <summary>One category segment in the filter bar (Category = null means "all").</summary>
public sealed partial class CategoryFilterViewModel : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public CategoryFilterViewModel(OptimizeViewModel owner, TweakCategory? category, string title, int count)
    {
        _owner = owner;
        Category = category;
        Title = title;
        Count = count;
    }

    public TweakCategory? Category { get; }
    public string Title { get; }
    public int Count { get; }

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Select() => _owner.SelectCategory(Category);
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
        // "Select all" covers only bulk-selectable items; an item that deletes data and is ticked by hand shows as "some".
        var bulk = _items.Where(i => i.IsBulkSelectable).ToList();
        HasSelectable = bulk.Count > 0;
        var checkedBulk = bulk.Count(i => i.IsChecked);
        var otherChecked = _items.Any(i => i.IsChecked && !i.IsBulkSelectable);
        CheckState = HasSelectable && checkedBulk == bulk.Count ? true
            : checkedBulk == 0 && !otherChecked ? false
            : null;
    }

    [RelayCommand]
    private void ToggleAll() => _owner.ToggleSection(this);
}

/// <summary>Hatched, collapsible header of the expert section.</summary>
public sealed partial class ExpertRow : ObservableObject
{
    private readonly OptimizeViewModel _owner;

    public ExpertRow(OptimizeViewModel owner) => _owner = owner;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _countText = "";

    [RelayCommand]
    private void Toggle() => _owner.ToggleExpert();
}
