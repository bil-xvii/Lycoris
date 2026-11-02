using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Shell;

public sealed partial class PaletteResultItem(PaletteEntry entry) : ObservableObject
{
    public PaletteEntry Entry { get; } = entry;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Ctrl+K command palette. Opening a result only navigates (plus a focus request for tweaks/services):
/// nothing is ever applied from here.
/// </summary>
public sealed partial class CommandPaletteViewModel(Func<IReadOnlyList<PaletteEntry>> buildIndex) : ObservableObject
{
    private IReadOnlyList<PaletteEntry>? _index;

    public ObservableCollection<PaletteResultItem> Results { get; } = new();

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteResultItem? _selected;

    public bool HasNoResults => Results.Count == 0;
    public string SelectedDescription => Selected?.Entry.Description ?? "";

    partial void OnQueryChanged(string value) => Refresh();

    partial void OnSelectedChanged(PaletteResultItem? oldValue, PaletteResultItem? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
        OnPropertyChanged(nameof(SelectedDescription));
    }

    public void Open()
    {
        _index = buildIndex(); // cheap, and the Tools tab adds cards (battery report) after it loads
        if (Query.Length > 0) Query = ""; // refreshes
        else Refresh();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        i = ((i + delta) % Results.Count + Results.Count) % Results.Count;
        Selected = Results[i];
    }

    public void OpenSelected() => Activate(Selected);

    [RelayCommand]
    private void Activate(PaletteResultItem? item)
    {
        if (item is null) return;
        var e = item.Entry;
        Close();
        if (e.FocusId is "mode:simple" or "mode:expert")
        {
            AppSettings.Current.ExpertMode = e.FocusId == "mode:expert";
            Ui.Navigate("home");
            return;
        }
        if (e.FocusId == "wizard:open") { (Ui.Host as MainViewModel)?.OpenWizard(); return; }
        if (e.FocusId is { } id) Ui.RequestFocus(e.PageKey, id);
        Ui.Navigate(e.PageKey);
    }

    private void Refresh()
    {
        Results.Clear();
        if (_index is not null)
            foreach (var e in CommandPaletteSearch.Search(_index, Query))
                Results.Add(new PaletteResultItem(e));
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }
}
