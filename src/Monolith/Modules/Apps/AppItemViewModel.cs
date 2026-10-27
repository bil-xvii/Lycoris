using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Core;

namespace Monolith.Modules.Apps;

public enum TagKind { Normal, Solid, Hatch, Muted }

/// <summary>A small badge on a card: text + look + plain-language explanation.</summary>
public sealed record AppTagInfo(string Text, TagKind Kind, string? ToolTip = null);

/// <summary>What the main button of a card does.</summary>
public enum AppAction
{
    Install, Upgrade, Installed, SystemComponent, Website,
    /// <summary>winget cannot install it now; the signed installer is downloaded straight from the vendor.</summary>
    VendorDownload,
}

public enum QueueOperation { Install, Upgrade }

public enum QueueState { Queued, Running, Done, Skipped, Failed, Cancelled, Unknown }

/// <summary>The safe way forward a finished queue row offers when winget could not do the job.</summary>
public enum QueueFallback
{
    None,
    /// <summary>Download the vendor's installer directly and check its signature (<see cref="CatalogEntry.VendorInstaller"/>).</summary>
    VendorDownload,
    /// <summary>Open the official download page as the signed-in user.</summary>
    Website,
}

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
        nameof(IsPrimaryAction), nameof(CanRunAction), nameof(CanSelect), nameof(HasVariantPicker), nameof(DisplayTags), nameof(ShowSiteButton),
        nameof(CanUpgrade), nameof(ShownAdvice), nameof(HasAdviceCaption), nameof(AdviceReason), nameof(IsDimmed), nameof(IsSuggested))]
    private string? _installedVersion;

    /// <summary>At least one detection pass (or the cache) has been applied.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunAction), nameof(CanSelect), nameof(IsChecking), nameof(DisplayTags),
        nameof(ShownAdvice), nameof(HasAdviceCaption), nameof(AdviceReason), nameof(IsDimmed), nameof(IsSuggested))]
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

    /// <summary>
    /// Install advice for this PC (<see cref="AppAdvisor.Assess"/>: judged as if the entry were not installed).
    /// The card shows it only while the state is known and the program is not installed (<see cref="ShownAdvice"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTags), nameof(ShownAdvice), nameof(HasAdviceCaption), nameof(AdviceReason), nameof(IsDimmed),
        nameof(IsSuggested))]
    private AppAdvice? _advice;

    /// <summary>
    /// Installed, but not as a winget package (found only by the read-only cross-checks — e.g. the .NET Desktop
    /// Runtime that came with the .NET SDK). winget cannot upgrade it, so the card offers no update.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Action), nameof(ActionText), nameof(ActionToolTip), nameof(IsPrimaryAction), nameof(CanRunAction),
        nameof(CanSelect), nameof(DisplayTags), nameof(ShowSiteButton), nameof(CanUpgrade))]
    private bool _isUnmanaged;

    public bool IsInstalled => InstalledVersion is not null;
    public bool IsChecking => !IsKnown;

    /// <summary>Installed through winget and allowed to be upgraded by it.</summary>
    public bool CanUpgrade => IsInstalled && Entry.CanUpgrade && !IsUnmanaged;

    /// <summary>The advice on the card: only when the state is known and the program is not installed (never for Edge).</summary>
    public AppAdvice? ShownAdvice => IsKnown && !IsInstalled && !Entry.SystemComponent ? Advice : null;

    /// <summary>One muted line with the reason under the description: every level except "ПО ЖЕЛАНИЮ".</summary>
    public bool HasAdviceCaption => ShownAdvice is { Level: not AdviceLevel.Optional, Reason.Length: > 0 };
    public string? AdviceReason => ShownAdvice?.Reason;

    /// <summary>"НЕ НУЖНО": the card is dimmed (full opacity on hover).</summary>
    public bool IsDimmed => ShownAdvice?.Level == AdviceLevel.NotNeeded;

    /// <summary>"НУЖНО" / "РЕКОМЕНДУЕТСЯ", not installed and installable through winget: shown in the «РЕКОМЕНДУЕМ» tab.</summary>
    public bool IsSuggested => ShownAdvice is { IsSuggested: true } && Entry.CanInstall;

    /// <summary>State tag (checking / installed / install advice) followed by the static catalog tags.</summary>
    public IReadOnlyList<AppTagInfo> DisplayTags
    {
        get
        {
            var list = new List<AppTagInfo>(Tags.Count + 1);
            if (IsChecking)
                list.Add(new("ПРОВЕРКА…", TagKind.Muted, "Проверяем, установлена ли программа."));
            else if (IsInstalled)
                list.Add(new(InstalledText, TagKind.Solid, Entry.Id == "Microsoft.DirectX"
                    ? "Найдены классические библиотеки DirectX: d3dx9_43.dll и xaudio2_7.dll."
                    : IsUnmanaged
                        ? AppTexts.UnmanagedExplanation
                        : "Программа найдена среди установленных (winget export или системные записи Windows)."));
            else if (ShownAdvice is { } advice)
                list.Add(AppTexts.AdviceTag(advice));
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
            if (IsInstalled) return CanUpgrade ? AppAction.Upgrade : AppAction.Installed;
            if (Entry.WebsiteOnly) return Entry.VendorInstaller is not null ? AppAction.VendorDownload : AppAction.Website;
            return AppAction.Install;
        }
    }

    public string ActionText => Action switch
    {
        AppAction.Install => "УСТАНОВИТЬ",
        AppAction.Upgrade => "ОБНОВИТЬ",
        AppAction.SystemComponent => "ВСТРОЕН В WINDOWS",
        AppAction.Website => "СКАЧАТЬ С САЙТА",
        // Without admin rights the verified copy cannot be kept safe: the button opens the official site instead.
        AppAction.VendorDownload => Elevation.IsElevated ? "СКАЧАТЬ НАПРЯМУЮ" : "СКАЧАТЬ С САЙТА",
        _ => "УСТАНОВЛЕНО",
    };

    public bool IsPrimaryAction => Action is AppAction.Install or AppAction.Website or AppAction.VendorDownload;

    /// <summary>The separate "САЙТ" link is redundant when the main button already opens the download page.</summary>
    public bool ShowSiteButton => Action != AppAction.Website;

    public bool CanRunAction => Action switch
    {
        AppAction.Website => true,
        AppAction.VendorDownload => !QueueBusy,
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

    /// <summary>Became "installed outside winget" with the same version: a ticked card would stay counted but do nothing.</summary>
    partial void OnIsUnmanagedChanged(bool value)
    {
        if (!CanSelect && IsSelected) IsSelected = false;
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

/// <summary>
/// A category tab above the catalog. <see cref="Category"/> null = "ВСЕ"; <see cref="IsRecommended"/> = «РЕКОМЕНДУЕМ»
/// (only "НУЖНО" / "РЕКОМЕНДУЕТСЯ" cards that are not installed yet, across all groups).
/// </summary>
public sealed partial class CategoryFilterViewModel(AppCategory? category, string title, bool isRecommended = false) : ObservableObject
{
    public AppCategory? Category { get; } = category;
    public string Title { get; } = title;
    public bool IsRecommended { get; } = isRecommended;
    public string? ToolTip => IsRecommended ? AppTexts.RecommendedTabToolTip : null;

    /// <summary>The card belongs to this tab (the search is applied separately).</summary>
    public bool Includes(AppItemViewModel item) =>
        IsRecommended ? item.IsSuggested : Category is null || item.Entry.Category == Category;

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

    /// <summary>For a finished row: what happened and what the user can do, in plain Russian (shown under the status).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetail))]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFallback), nameof(FallbackText), nameof(FallbackToolTip))]
    private QueueFallback _fallback;

    /// <summary>The vendor installer for this row is being downloaded (the fallback button then cancels it).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFallback), nameof(FallbackText), nameof(FallbackToolTip), nameof(ShowDetail))]
    private bool _isDownloading;

    public bool NeedsReboot { get; set; }
    public bool IsActive => State is QueueState.Queued or QueueState.Running;
    public bool IsRunning => State == QueueState.Running;
    public bool IsFinished => !IsActive;
    public bool ShowDetail => !string.IsNullOrWhiteSpace(Detail) && (IsFinished || IsDownloading);
    public bool HasFallback => IsDownloading || (Fallback != QueueFallback.None && IsFinished);

    public string FallbackText => IsDownloading ? "ОТМЕНИТЬ ЗАГРУЗКУ"
        : Fallback == QueueFallback.VendorDownload ? "СКАЧАТЬ У ПРОИЗВОДИТЕЛЯ"
        : "ОТКРЫТЬ САЙТ";

    public string FallbackToolTip => IsDownloading
        ? "Прервать загрузку. Недокачанный файл будет удалён."
        : Fallback == QueueFallback.VendorDownload
            ? AppTexts.VendorDownloadToolTip(App.Entry)
            : $"Открыть официальную страницу загрузки: {App.Entry.Website}";

    partial void OnStateChanged(QueueState value)
    {
        OnPropertyChanged(nameof(ShowDetail));
        OnPropertyChanged(nameof(HasFallback));
        App.NotifyJobChanged();
    }
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
