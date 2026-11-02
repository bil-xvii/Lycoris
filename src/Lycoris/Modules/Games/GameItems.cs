using CommunityToolkit.Mvvm.ComponentModel;
using Lycoris.Core;

namespace Lycoris.Modules.Games;

/// <summary>A game card in the library grid.</summary>
public sealed partial class GameCardViewModel : ObservableObject
{
    public GameCardViewModel(GameEntry entry)
    {
        Entry = entry;
        Initials = GameText.Initials(entry.Name);
        CoverVariant = (int)(StableHash(entry.Id) % 3);
    }

    public GameEntry Entry { get; }
    public string Name => Entry.Name;
    public string Initials { get; }
    public string PlatformLabel => GameText.PlatformLabel(Entry.Platform);
    public string SizeText => Entry.SizeBytes > 0 ? SystemProfile.FormatBytes(Entry.SizeBytes) : "";
    public bool HasSize => Entry.SizeBytes > 0;
    public string PathText => Entry.Platform == GamePlatform.Custom && Entry.ExePath is { } exe ? exe : Entry.InstallDir.TrimEnd('\\');
    public bool IsCustom => Entry.Platform == GamePlatform.Custom;
    public bool IsAvailable => Entry.IsAvailable;
    public bool IsMissing => !Entry.IsAvailable;

    /// <summary>0 = dot grid, 1 = diagonal hatch, 2 = plain raised block (deterministic per game).</summary>
    public int CoverVariant { get; }

    // ───────────── Per-game GPU preference (hybrid PCs only; the switch itself is handled by GamesViewModel) ─────────────

    /// <summary>Windows' per-app GPU preference values that belong to this game (read from the registry).</summary>
    public IReadOnlyList<GpuPreferenceValue> GpuValues { get; private set; } = [];

    /// <summary>True when one of the game's exes is set to «высокая производительность».</summary>
    [ObservableProperty] private bool _isGpuHigh;
    /// <summary>Null until the state was read (no empty tooltip box).</summary>
    [ObservableProperty] private string? _gpuTooltip;

    public void SetGpu(IReadOnlyList<GpuPreferenceValue> values, string tooltip)
    {
        GpuValues = values;
        IsGpuHigh = values.Any(v => v.IsHigh);
        GpuTooltip = tooltip;
        SyncGpuToggle();
    }

    /// <summary>The switch flips itself on click; this pushes the real state back into it (after cancel / failure).</summary>
    public void SyncGpuToggle() => OnPropertyChanged(nameof(IsGpuHigh));

    /// <summary>FNV-1a — stable across runs (string.GetHashCode is randomized per process).</summary>
    private static uint StableHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s.ToLowerInvariant())
        {
            h ^= c;
            h *= 16777619u;
        }
        return h;
    }
}

/// <summary>Platform filter segment ("ВСЕ", "STEAM"…).</summary>
public sealed partial class PlatformFilterItem : ObservableObject
{
    private readonly Action<PlatformFilterItem> _onSelected;

    public PlatformFilterItem(GamePlatform? platform, int count, Action<PlatformFilterItem> onSelected, bool selected)
    {
        Platform = platform;
        Count = count;
        _onSelected = onSelected;
        _isSelected = selected;
    }

    public GamePlatform? Platform { get; }
    public int Count { get; }
    public string Label => Platform is { } p ? GameText.PlatformLabel(p) : Loc.T("ВСЕ");

    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _onSelected(this);
    }
}

/// <summary>A running app in the close-list editor.</summary>
public sealed partial class RunningAppItem : ObservableObject
{
    private readonly Action<RunningAppItem, bool> _onToggled;

    public RunningAppItem(RunningApp app, bool inList, Action<RunningAppItem, bool> onToggled)
    {
        App = app;
        _isInList = inList;
        _onToggled = onToggled;
    }

    partial void OnIsInListChanged(bool value) => _onToggled(this, value);

    public RunningApp App { get; }
    public string Name => App.Name;
    public string Description => string.IsNullOrWhiteSpace(App.Description) ? Loc.T("Без описания") : App.Description;
    public string Meta => $"{Loc.N(App.Count, "{0} ПРОЦЕСС", "{0} ПРОЦЕССА", "{0} ПРОЦЕССОВ")} · {SystemProfile.FormatBytes(App.MemoryBytes)}";
    public bool HasWindow => App.HasWindow;

    [ObservableProperty] private bool _isInList;
}
