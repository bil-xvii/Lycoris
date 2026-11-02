using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Modules.Cleaner;
using Monolith.Modules.Tools;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Maintenance;

/// <summary>
/// «ОБСЛУЖИВАНИЕ»: one page with two tabs that host the existing Cleaner and Tools pages unchanged.
/// The page lifecycle (first load / return / leave) is forwarded to the active tab only; the busy state mirrors the tabs.
/// </summary>
public sealed partial class MaintenanceViewModel : PageViewModel
{
    public const string CleanerTab = "cleaner";
    public const string ToolsTab = "tools";
    /// <summary>GroupName of the tab buttons in MaintenanceView.xaml (the self-test skips them).</summary>
    public const string TabGroup = "MaintenanceTabs";

    private bool _active;

    public override string Key => "maintenance";
    public override string Index => "05";
    public override string Title => Loc.T("ОБСЛУЖИВАНИЕ");
    public override string Subtitle => Loc.T("Очистка диска, проверка и восстановление Windows.");
    public override string Glyph => Glyphs.Repair;

    public CleanerViewModel Cleaner { get; }
    public ToolsViewModel Tools { get; }
    public IReadOnlyList<PageViewModel> Tabs { get; }

    /// <summary>Key of the selected tab: <see cref="CleanerTab"/> or <see cref="ToolsTab"/>.</summary>
    [ObservableProperty] private string _tabKey = CleanerTab;

    public PageViewModel ActiveTab => TabFor(TabKey);

    public MaintenanceViewModel() : this(new CleanerViewModel(), new ToolsViewModel()) { }

    public MaintenanceViewModel(CleanerViewModel cleaner, ToolsViewModel tools)
    {
        Cleaner = cleaner;
        Tools = tools;
        Tabs = [cleaner, tools];
        foreach (var t in Tabs) t.PropertyChanged += OnTabPropertyChanged;
    }

    /// <summary>True for the keys of the hosted pages ("cleaner", "tools").</summary>
    public static bool IsTabKey(string key) => key is CleanerTab or ToolsTab;

    /// <summary>Selects a tab by its page key ("cleaner" / "tools"); other keys are ignored.</summary>
    public void SelectTab(string key)
    {
        if (IsTabKey(key)) TabKey = key;
    }

    private PageViewModel TabFor(string key) => key == ToolsTab ? Tools : Cleaner;

    partial void OnTabKeyChanging(string? oldValue, string newValue)
    {
        if (_active && oldValue is not null && oldValue != newValue) TabFor(oldValue).Leave();
    }

    partial void OnTabKeyChanged(string value)
    {
        OnPropertyChanged(nameof(ActiveTab));
        SyncBusy();
        if (_active) _ = ActiveTab.ActivateAsync();
    }

    protected override Task LoadAsync() => ActivateTabAsync();
    protected override Task OnReturnAsync() => ActivateTabAsync();

    private Task ActivateTabAsync()
    {
        _active = true;
        return ActiveTab.ActivateAsync();
    }

    public override void Deactivate()
    {
        _active = false;
        ActiveTab.Leave();
    }

    // ───────────── Shortcut targets (Dashboard tiles, admin-intent replay) ─────────────
    // The replay and the shortcuts look for «{Command}Command» on this top-level page; the logic stays in the tabs.

    /// <summary>«ОЧИСТИТЬ · N МБ» of the cleaning tab (analyses first when the last analysis is stale; confirms with the exact list).</summary>
    public ICommand CleanCommand => Cleaner.CleanCommand;

    /// <summary>A fresh read-only analysis of the cleaning tab.</summary>
    public ICommand AnalyzeCommand => Cleaner.AnalyzeCommand;

    /// <summary>A tool button by its text id («Sfc», «DismRestore», «DefragVolume:C:») — what the admin-intent replay repeats.</summary>
    public ICommand RunToolCommand => Tools.RunToolCommand;

    /// <summary>«ПРОВЕРИТЬ И ВОССТАНОВИТЬ WINDOWS»: DISM, then SFC.</summary>
    public ICommand RepairWindowsCommand => Tools.RepairWindowsCommand;

    /// <summary>
    /// The junk the cleaning tab would remove now (checked rows of the last analysis), or null before any analysis.
    /// Cached — reading it never scans. The Dashboard shows it and calls <see cref="CleanCommand"/>.
    /// </summary>
    public long? JunkBytes => Cleaner.JunkBytes;

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsBusy) or nameof(BusyText)) SyncBusy();
        // The shell shows the host's note (refused / queued): mirror the tab's.
        if (e.PropertyName == nameof(OperationNote) && sender is PageViewModel tab) OperationNote = tab.OperationNote;
        if (ReferenceEquals(sender, Cleaner))
        {
            // The sidebar badge: the junk size the last analysis found (no work only for the badge).
            if (e.PropertyName == nameof(BadgeText)) BadgeText = Cleaner.BadgeText;
            if (e.PropertyName == nameof(CleanerViewModel.JunkBytes)) OnPropertyChanged(nameof(JunkBytes));
            if (e.PropertyName == nameof(CheckedAt)) CheckedAt = Cleaner.CheckedAt;
        }
    }

    /// <summary>The shell's progress bar and status text follow the tabs (the active tab's text first).</summary>
    private void SyncBusy()
    {
        var active = ActiveTab;
        var other = ReferenceEquals(active, Cleaner) ? (PageViewModel)Tools : Cleaner;
        IsBusy = active.IsBusy || other.IsBusy;
        BusyText = active.IsBusy ? active.BusyText : other.IsBusy ? other.BusyText : null;
    }
}
