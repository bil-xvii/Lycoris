using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;

namespace Monolith.Modules.Cleaner;

/// <summary>How a status chip is drawn: muted outline, outline, solid white block, hatched warning.</summary>
public enum StatusTone { Muted, Normal, Done, Warning }

/// <summary>Russian number / size formatting for the cleaner.</summary>
public static class CleanFormat
{

    /// <summary>1 файл, 2 файла, 5 файлов.</summary>
    public static string Plural(long n, string one, string few, string many) => Loc.Plural(n, one, few, many);

    public static string Count(long n) => n.ToString("N0", Loc.Culture);

    public static string Files(long n) => $"{Count(n)} {Loc.Plural(n, "файл", "файла", "файлов")}";

    public static string Bytes(long bytes) => SystemProfile.FormatBytes(Math.Max(0, bytes));

    /// <summary>"1,4 ГБ" → ("1,4", "ГБ") for the big stat numbers.</summary>
    public static (string Value, string Unit) Split(long bytes)
    {
        var s = Bytes(bytes);
        var i = s.LastIndexOf(' ');
        return i < 0 ? (s, "") : (s[..i], s[(i + 1)..]);
    }
}

/// <summary>One row of the cleaner list.</summary>
public sealed partial class CleanItem : ObservableObject
{
    public CleanItem(CleanDefinition definition)
    {
        Definition = definition;
    }

    public CleanDefinition Definition { get; }
    public string Id => Definition.Id;
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Effect => Definition.Effect;
    public CleanTier Tier => Definition.Tier;
    public bool RequiresAdmin => Definition.RequiresAdmin;
    public bool IsLong => Definition.IsLong;

    public string TierText => Tier switch
    {
CleanTier.Safe => Loc.T("БЕЗОПАСНО"),
CleanTier.Optional => Loc.T("ПО ЖЕЛАНИЮ"),
_ => Loc.T("ЭКСПЕРТ"),
    };

    public bool IsSafeTier => Tier == CleanTier.Safe;
    public bool IsOptionalTier => Tier == CleanTier.Optional;
    public bool IsExpertTier => Tier == CleanTier.Expert;

    /// <summary>True while the row is blocked only by missing admin rights — the row then offers «ПОЛУЧИТЬ ПРАВА».</summary>
    [ObservableProperty] private bool _needsRights;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isSelectable = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isLocked;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isAppRunning;
    [ObservableProperty] private string _closeAppTag = Loc.T("ЗАКРОЙТЕ ПРИЛОЖЕНИЕ");
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _countText = Loc.T("не проверено");
    [ObservableProperty] private string _statusText = Loc.T("НЕ ПРОВЕРЕНО");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ToneIsMuted), nameof(ToneIsNormal), nameof(ToneIsDone), nameof(ToneIsWarning))]
    private StatusTone _statusTone = StatusTone.Muted;

    // Which status tag the row shows (Tag.Muted / Tag / Tag.Solid / Tag.Hatch) — never white, never an outlined capsule.
    public bool ToneIsMuted => StatusTone == StatusTone.Muted;
    public bool ToneIsNormal => StatusTone == StatusTone.Normal;
    public bool ToneIsDone => StatusTone == StatusTone.Done;
    public bool ToneIsWarning => StatusTone == StatusTone.Warning;
    [ObservableProperty] private string? _note;
    [ObservableProperty] private bool _isWorking;

    public bool CanToggle => IsSelectable && !IsLocked;

    /// <summary>The status chip follows the checkbox ("К ОЧИСТКЕ" / "НЕ ВЫБРАНО") while the item has work.</summary>
    private bool _ready;

    /// <summary>Last dry-run result (the manifest the user approves).</summary>
    public ItemAnalysis? Analysis { get; private set; }

    /// <summary>Bytes a cleaning run would take now (0 before analysis).</summary>
    public long CleanableBytes => Analysis?.CleanableBytes ?? 0;
    public long FoundBytes => Analysis?.Bytes ?? 0;
    public long FoundFiles => Analysis?.Files ?? 0;

    [RelayCommand]
    private void Toggle()
    {
        if (CanToggle) IsChecked = !IsChecked;
    }

    /// <summary>Applies admin / foreign-account rules; unchecks items that cannot run.</summary>
    public void ApplyAccess(CleanEnvironment env)
    {
        var blockedAdmin = Definition.RequiresAdmin && !env.IsElevated;
        var blockedUser = Definition.IsUserScope && env.UserScopeBlocked;
        IsSelectable = !blockedAdmin && !blockedUser;
        NeedsRights = blockedAdmin && !blockedUser;
        if (!IsSelectable) IsChecked = false;
    }

    public void SetAnalyzing()
    {
        _ready = false;
        IsWorking = true;
        StatusText = Loc.T("АНАЛИЗ…");
        StatusTone = StatusTone.Normal;
    }

    public void SetCleaning()
    {
        _ready = false;
        IsWorking = true;
        StatusText = Loc.T("ОЧИСТКА…");
        StatusTone = StatusTone.Normal;
    }

    public void SetCancelled()
    {
        _ready = false;
        IsWorking = false;
        if (Analysis is null)
        {
            StatusText = Loc.T("НЕ ПРОВЕРЕНО");
            StatusTone = StatusTone.Muted;
        }
        else
        {
            StatusText = Loc.T("ОТМЕНЕНО");
            StatusTone = StatusTone.Muted;
        }
    }

    /// <summary>True when the confirmation must not rely on the last analysis (old, app was running, blocked, already cleaned).</summary>
    public bool NeedsFreshAnalysis(TimeSpan staleAfter) =>
        Analysis is not { } a || WasCleaned || a.HasRunningApps || a.Blocker is not null || DateTime.UtcNow - a.AtUtc > staleAfter;

    public bool WasCleaned { get; private set; }

    /// <summary>Frees the per-file lists of the current analysis; the counters shown in the UI stay.</summary>
    internal void ReleaseManifests()
    {
        if (Analysis is not { } a || a.Manifests.Count == 0) return;
        a.Manifests.Clear();
        a.Manifests.TrimExcess();
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_ready) UpdateReadyStatus();
    }

    private void UpdateReadyStatus()
    {
        if (Definition.Method == CleanMethod.ComponentStore)
        {
            StatusText = IsChecked ? Loc.T("ЗАПУСК ПРИ ОЧИСТКЕ") : Loc.T("ДОЛГАЯ ОПЕРАЦИЯ");
            StatusTone = IsChecked ? StatusTone.Normal : StatusTone.Muted;
            return;
        }
        StatusText = IsChecked ? Loc.T("К ОЧИСТКЕ") : Loc.T("НЕ ВЫБРАНО");
        StatusTone = IsChecked ? StatusTone.Normal : StatusTone.Muted;
    }

    public void ApplyAnalysis(ItemAnalysis a, CleanEnvironment env)
    {
        Analysis = a;
        WasCleaned = false;
        _ready = false;
        IsWorking = false;
        IsAppRunning = a.HasRunningApps;
        CloseAppTag = a.RunningApps.Count == 1 ? Loc.Tf("ЗАКРОЙТЕ {0}", a.RunningApps[0].ToUpperInvariant()) : Loc.T("ЗАКРОЙТЕ ПРИЛОЖЕНИЯ");

        switch (Definition.Method)
        {
            case CleanMethod.RecycleBin:
                SizeText = a.Unavailable is null ? CleanFormat.Bytes(a.Bytes) : "—";
                CountText = a.Items is { } items ? $"{CleanFormat.Count(items)} {Loc.Plural(items, "объект", "объекта", "объектов")}" : "";
                break;
            case CleanMethod.DeliveryOptimization:
                SizeText = a.Unavailable is not null || a.SizeUnknown ? "—" : CleanFormat.Bytes(a.Bytes);
                CountText = a.SizeUnknown ? Loc.T("размер неизвестен") : Loc.T("по данным службы");
                break;
            case CleanMethod.ComponentStore:
                SizeText = "—";
                CountText = Loc.T("размер определит DISM");
                break;
            default:
                if (a.PartialAccess && a.Files == 0)
                {
                    // The folder could not be read at all: the size is unknown, not zero.
                    SizeText = "—";
                    CountText = Loc.T("нет доступа к папке");
                }
                else
                {
                    SizeText = CleanFormat.Bytes(a.Bytes);
                    CountText = a.PartialAccess ? Loc.Tf("{0} · частично", CleanFormat.Files(a.Files)) : CleanFormat.Files(a.Files);
                }
                break;
        }

        string? note = null;
        if (Definition.IsUserScope && env.UserScopeBlocked)
        {
            StatusText = Loc.T("ДРУГАЯ УЧЁТНАЯ ЗАПИСЬ");
            StatusTone = StatusTone.Warning;
            note = Loc.Tf("Lycoris запущен от имени {0}: пользовательские кэши принадлежат другой учётной записи и не трогаются.", env.ForeignSessionUser);
        }
        else if (Definition.RequiresAdmin && !env.IsElevated)
        {
            // The row shows «НУЖНЫ ПРАВА» and its own «ПОЛУЧИТЬ ПРАВА» button; nothing is disabled-and-silent.
            StatusText = Loc.T("НУЖНЫ ПРАВА");
            StatusTone = StatusTone.Muted;
        }
        else if (a.Unavailable is { } unavailable)
        {
            StatusText = Loc.T("НЕДОСТУПНО");
            StatusTone = StatusTone.Muted;
            note = unavailable;
        }
        else if (a.Blocker is { } blocker)
        {
            StatusText = Loc.T("ОТЛОЖЕНО");
            StatusTone = StatusTone.Warning;
            note = blocker;
        }
        else if (a.HasRunningApps)
        {
            StatusText = Loc.T("ЖДЁТ ЗАКРЫТИЯ");
            StatusTone = StatusTone.Warning;
            note = a.RunningApps.Count == 1
                ? Loc.Tf("{0} запущен — закройте, включая значок в трее, иначе этот кэш будет пропущен. Lycoris не закрывает программы сам.", a.RunningApps[0])
                : Loc.Tf("{0} запущены — закройте, включая значок в трее, иначе этот кэш будет пропущен. Lycoris не закрывает программы сам.", string.Join(", ", a.RunningApps));
        }
        else if (a.RefusedTargets > 0 && a.Files == 0 && Definition.Method is CleanMethod.Files or CleanMethod.Tools)
        {
            SizeText = "—";
            CountText = Loc.T("путь отклонён");
            StatusText = Loc.T("ЗАЩИТА ПУТЕЙ");
            StatusTone = StatusTone.Muted;
        }
        else if (Definition.Method == CleanMethod.ComponentStore || a.HasWork(Definition.Method))
        {
            _ready = true;
            UpdateReadyStatus();
        }
        else
        {
            StatusText = Loc.T("ЧИСТО");
            StatusTone = StatusTone.Muted;
        }

        if (note is null && a.RefusedNote is not null) note = a.RefusedNote;
        if (note is null && a.PartialAccess && env.IsElevated) note = Loc.T("Часть файлов защищена системой и будет пропущена.");
        Note = note;
        OnPropertyChanged(nameof(CleanableBytes));
        OnPropertyChanged(nameof(FoundBytes));
    }

    public void ApplyCleanResult(ItemCleanResult r)
    {
        IsWorking = false;
        WasCleaned = true;
        _ready = false;
        if (r.Skipped is { } skipped)
        {
            StatusText = Loc.T("ПРОПУЩЕНО");
            StatusTone = StatusTone.Warning;
            Note = skipped;
            return;
        }

        var parts = new List<string>();
        var freed = r.Freed;
        if (freed > 0 || r.Tally.Deleted > 0 || r.ToolItems > 0)
            parts.Add(Loc.Tf("Освобождено {0}{1}.", r.Approximate ? "≈" : "", CleanFormat.Bytes(freed)));
        if (r.Tally.Deleted > 0) parts.Add(Loc.Tf("Удалено: {0}.", CleanFormat.Files(r.Tally.Deleted)));
        if (r.Tally.InUse > 0) parts.Add(Loc.N(r.Tally.InUse, "{0:N0} файл занят программами — пропущено.", "{0:N0} файла заняты программами — пропущено.", "{0:N0} файлов заняты программами — пропущено."));
        if (r.Tally.Denied > 0) parts.Add(Loc.Tf("Нет доступа: {0}.", CleanFormat.Files(r.Tally.Denied)));
        if (r.Tally.Changed > 0) parts.Add(Loc.Tf("Изменились после анализа и оставлены: {0}.", CleanFormat.Files(r.Tally.Changed)));
        parts.AddRange(r.Notes);
        if (r.Error is { } error) parts.Add(error);

        if (r.Error is not null && freed == 0 && r.Tally.Deleted == 0)
        {
            StatusText = Loc.T("ОШИБКА");
            StatusTone = StatusTone.Warning;
        }
        else if (r.Tally.Cancelled)
        {
            StatusText = Loc.T("ПРЕРВАНО");
            StatusTone = StatusTone.Warning;
        }
        else
        {
            StatusText = Loc.T("ОЧИЩЕНО");
            StatusTone = StatusTone.Done;
        }
        Note = parts.Count == 0 ? Loc.T("Удалять было нечего.") : string.Join(" ", parts);

        // What is left: the analysed size minus what was removed (locked files and skipped folders stay).
        if (Analysis is { } a)
        {
            var removedLogical = r.Tally.DeletedLogicalBytes + r.ToolFreed;
            var left = Math.Max(0, a.Bytes - removedLogical);
            var leftFiles = Math.Max(0, a.Files - r.Tally.Deleted);
            a.Bytes = left;
            a.Files = leftFiles;
            a.CleanableBytes = 0;
            a.CleanableFiles = 0;
            a.CleanableDirs = 0;
            if (Definition.Method == CleanMethod.RecycleBin && r.ToolItems > 0) a.Items = 0;
            // The file lists are never used again (WasCleaned forces a new analysis): free them.
            a.Manifests.Clear();
            a.Manifests.TrimExcess();
            SizeText = Definition.Method is CleanMethod.ComponentStore ? (freed > 0 ? CleanFormat.Bytes(freed) : "—") : CleanFormat.Bytes(left);
            CountText = Definition.Method switch
            {
                CleanMethod.Files => left == 0 ? Loc.T("пусто") : Loc.Tf("осталось {0}", CleanFormat.Files(leftFiles)),
                CleanMethod.RecycleBin => a.Items is 0 ? Loc.T("пусто") : CountText,
                CleanMethod.ComponentStore => freed > 0 ? Loc.T("освобождено") : Loc.T("без изменений"),
                _ => Loc.T("после очистки"),
            };
            OnPropertyChanged(nameof(CleanableBytes));
            OnPropertyChanged(nameof(FoundBytes));
        }
    }
}

/// <summary>A category block of the list.</summary>
public sealed partial class CleanGroup : ObservableObject
{
    public CleanGroup(CleanCategory category, int index)
    {
        Category = category;
        Index = index.ToString("00", CultureInfo.InvariantCulture);
        Title = CleanCatalog.CategoryTitle(category);
        Hint = CleanCatalog.CategoryHint(category);
    }

    public CleanCategory Category { get; }
    public string Index { get; }
    public string Title { get; }
    public string Hint { get; }
    public ObservableCollection<CleanItem> Items { get; } = [];

    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private string? _missingText;

    public void Refresh()
    {
        var analysed = Items.Where(i => i.Analysis is not null).ToList();
        var selected = Items.Count(i => i.IsChecked);
        var size = analysed.Sum(i => i.FoundBytes);
        Meta = analysed.Count == 0
            ? Loc.Tf("{0} ИЗ {1}", selected, Items.Count)
            : Loc.Tf("{0} · {1} ИЗ {2}", CleanFormat.Bytes(size).ToUpperInvariant(), selected, Items.Count);
    }
}

/// <summary>Free space of one fixed drive.</summary>
public sealed record DriveCard(string Name, string Label, string FreeText, string Caption, double UsedPercent, bool IsSystem);
