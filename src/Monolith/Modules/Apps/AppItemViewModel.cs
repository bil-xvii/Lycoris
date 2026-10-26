using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Monolith.Modules.Apps;

public enum TagKind { Normal, Solid, Hatch, Muted }

/// <summary>A small badge on a card: text + look + plain-language explanation.</summary>
public sealed record AppTagInfo(string Text, TagKind Kind, string? ToolTip = null);

/// <summary>What the main button of a card does.</summary>
public enum AppAction { Install, Upgrade, Installed, SystemComponent, Website }

public enum QueueOperation { Install, Upgrade }

public enum QueueState { Queued, Running, Done, Skipped, Failed, Cancelled, Unknown }

/// <summary>One catalog card.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    public AppItemViewModel(CatalogEntry entry)
    {
        Entry = entry;
        Variants = entry.Variants;
        _selectedVariant = Variants.FirstOrDefault();
        Tags = AppTexts.BuildTags(entry);
    }

    public CatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string Description => Entry.Description;
    public IReadOnlyList<AppVariant> Variants { get; }
    public bool HasVariants => Variants.Count > 0;
    public IReadOnlyList<AppTagInfo> Tags { get; }
    public bool IsStore => Entry.Source == PackageSource.MsStore;
    public string SiteToolTip => Entry.Website;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageId))]
    private AppVariant? _selectedVariant;

    /// <summary>Package id that will be installed (the chosen region for Riot games).</summary>
    public string PackageId => SelectedVariant?.Id ?? Entry.Id;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>null = not installed; "" = installed, version unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(InstalledText), nameof(Action), nameof(ActionText), nameof(ActionToolTip),
        nameof(IsPrimaryAction), nameof(CanRunAction), nameof(CanSelect), nameof(HasVariantPicker), nameof(DisplayTags), nameof(ShowSiteButton))]
    private string? _installedVersion;

    /// <summary>At least one detection pass (or the cache) has been applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction), nameof(CanSelect), nameof(IsChecking), nameof(DisplayTags))]
    private bool _isKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction), nameof(CanSelect))]
    private bool _wingetReady;

    /// <summary>The install queue is running (all install buttons wait for it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction))]
    private bool _queueBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveJob), nameof(HasJob))]
    private QueueItemViewModel? _job;

    /// <summary>NVIDIA-only package on a PC where no NVIDIA graphics card was found.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTags))]
    private bool _nvidiaMissing;

    public bool IsInstalled => InstalledVersion is not null;
    public bool IsChecking => !IsKnown;

    /// <summary>State tags (checking / installed / no NVIDIA) followed by the static catalog tags.</summary>
    public IReadOnlyList<AppTagInfo> DisplayTags
    {
        get
        {
            var list = new List<AppTagInfo>(Tags.Count + 2);
            if (IsChecking)
                list.Add(new("ПРОВЕРКА…", TagKind.Muted, "Проверяем, установлена ли программа."));
            else if (IsInstalled)
                list.Add(new(InstalledText, TagKind.Solid, Entry.Id == "Microsoft.DirectX"
                    ? "Найдены классические библиотеки DirectX: d3dx9_43.dll и xaudio2_7.dll."
                    : "Программа найдена среди установленных (winget export или системные записи Windows)."));
            if (NvidiaMissing)
                list.Add(new("NVIDIA НЕ НАЙДЕНА", TagKind.Hatch, "В этом компьютере не найдена видеокарта NVIDIA — пакет, скорее всего, не нужен."));
            list.AddRange(Tags);
            return list;
        }
    }
    public bool HasJob => Job is not null;
    public bool HasActiveJob => Job is { IsActive: true };
    public bool HasVariantPicker => HasVariants && !IsInstalled;

    public string InstalledText => string.IsNullOrEmpty(InstalledVersion) ? "УСТАНОВЛЕНО" : $"УСТАНОВЛЕНО {InstalledVersion}";

    public AppAction Action
    {
        get
        {
            if (Entry.SystemComponent) return AppAction.SystemComponent;
            if (IsInstalled) return Entry.CanUpgrade ? AppAction.Upgrade : AppAction.Installed;
            return Entry.WebsiteOnly ? AppAction.Website : AppAction.Install;
        }
    }

    public string ActionText => Action switch
    {
        AppAction.Install => "УСТАНОВИТЬ",
        AppAction.Upgrade => "ОБНОВИТЬ",
        AppAction.SystemComponent => "ВСТРОЕН В WINDOWS",
        AppAction.Website => "СКАЧАТЬ С САЙТА",
        _ => "УСТАНОВЛЕНО",
    };

    public bool IsPrimaryAction => Action is AppAction.Install or AppAction.Website;

    /// <summary>The separate "САЙТ" link is redundant when the main button already opens the download page.</summary>
    public bool ShowSiteButton => Action != AppAction.Website;

    public bool CanRunAction => Action switch
    {
        AppAction.Website => true,
        AppAction.Install or AppAction.Upgrade => IsKnown && WingetReady && !QueueBusy,
        _ => false,
    };

    /// <summary>Can be ticked for the batch queue (install or upgrade through winget).</summary>
    public bool CanSelect => Action is AppAction.Install or AppAction.Upgrade && IsKnown && WingetReady;

    public string ActionToolTip => AppTexts.ActionToolTip(this);

    partial void OnInstalledVersionChanged(string? value)
    {
        if (!CanSelect && IsSelected) IsSelected = false;
    }

    partial void OnWingetReadyChanged(bool value)
    {
        if (!value && IsSelected) IsSelected = false;
    }
}

/// <summary>One catalog section ("ЛАУНЧЕРЫ").</summary>
public sealed partial class AppGroupViewModel : ObservableObject
{
    public AppGroupViewModel(CategoryInfo info, IEnumerable<AppItemViewModel> items)
    {
        Info = info;
        Items = new ObservableCollection<AppItemViewModel>(items);
    }

    public CategoryInfo Info { get; }
    public string Title => Info.Title;
    public string Description => Info.Description;
    public ObservableCollection<AppItemViewModel> Items { get; }

    [ObservableProperty] private int _visibleCount;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private int _installedCount;
}

/// <summary>A category tab above the catalog. <see cref="Category"/> null = "ВСЕ".</summary>
public sealed partial class CategoryFilterViewModel(AppCategory? category, string title) : ObservableObject
{
    public AppCategory? Category { get; } = category;
    public string Title { get; } = title;

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>One package in the install queue.</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(AppItemViewModel app, QueueOperation operation)
    {
        App = app;
        Operation = operation;
        PackageId = app.PackageId;
    }

    public AppItemViewModel App { get; }
    public QueueOperation Operation { get; }
    public string PackageId { get; }
    public string Name => App.Name;
    public string SourceName => App.Entry.SourceName;
    public string OperationText => Operation == QueueOperation.Install ? "установка" : "обновление";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(IsRunning), nameof(IsFinished))]
    private QueueState _state = QueueState.Queued;

    [ObservableProperty] private string _statusText = "В ОЧЕРЕДИ";
    [ObservableProperty] private string? _detail;

    public bool NeedsReboot { get; set; }
    public bool IsActive => State is QueueState.Queued or QueueState.Running;
    public bool IsRunning => State == QueueState.Running;
    public bool IsFinished => !IsActive;

    partial void OnStateChanged(QueueState value) => App.NotifyJobChanged();
}

public sealed partial class AppItemViewModel
{
    /// <summary>The job's state changed: refresh the card bindings that depend on it.</summary>
    internal void NotifyJobChanged()
    {
        OnPropertyChanged(nameof(HasActiveJob));
        OnPropertyChanged(nameof(HasJob));
    }
}
