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
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>1 файл, 2 файла, 5 файлов.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        var abs = Math.Abs(n) % 100;
        var last = abs % 10;
        if (abs is >= 11 and <= 19) return many;
        return last switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }

    public static string Count(long n) => n.ToString("N0", Ru);

    public static string Files(long n) => $"{Count(n)} {Plural(n, "файл", "файла", "файлов")}";

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
        CleanTier.Safe => "БЕЗОПАСНО",
        CleanTier.Optional => "ПО ЖЕЛАНИЮ",
        _ => "ЭКСПЕРТ",
    };

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isSelectable = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanToggle))] private bool _isLocked;
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private bool _isAppRunning;
    [ObservableProperty] private string _closeAppTag = "ЗАКРОЙТЕ ПРИЛОЖЕНИЕ";
    [ObservableProperty] private string _sizeText = "—";
    [ObservableProperty] private string _countText = "не проверено";
    [ObservableProperty] private string _statusText = "НЕ ПРОВЕРЕНО";
    [ObservableProperty] private StatusTone _statusTone = StatusTone.Muted;
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
        if (!IsSelectable) IsChecked = false;
    }

    public void SetAnalyzing()
    {
        _ready = false;
        IsWorking = true;
        StatusText = "АНАЛИЗ…";
        StatusTone = StatusTone.Normal;
    }

    public void SetCleaning()
    {
        _ready = false;
        IsWorking = true;
        StatusText = "ОЧИСТКА…";
        StatusTone = StatusTone.Normal;
    }

    public void SetCancelled()
    {
        _ready = false;
        IsWorking = false;
        if (Analysis is null)
        {
            StatusText = "НЕ ПРОВЕРЕНО";
            StatusTone = StatusTone.Muted;
        }
        else
        {
            StatusText = "ОТМЕНЕНО";
            StatusTone = StatusTone.Muted;
        }
    }

    /// <summary>True when the confirmation must not rely on the last analysis (old, app was running, blocked, already cleaned).</summary>
    public bool NeedsFreshAnalysis(TimeSpan staleAfter) =>
        Analysis is not { } a || WasCleaned || a.HasRunningApps || a.Blocker is not null || DateTime.UtcNow - a.AtUtc > staleAfter;

    public bool WasCleaned { get; private set; }

    partial void OnIsCheckedChanged(bool value)
    {
        if (_ready) UpdateReadyStatus();
    }

    private void UpdateReadyStatus()
    {
        if (Definition.Method == CleanMethod.ComponentStore)
        {
            StatusText = IsChecked ? "ЗАПУСК ПРИ ОЧИСТКЕ" : "ДОЛГАЯ ОПЕРАЦИЯ";
            StatusTone = IsChecked ? StatusTone.Normal : StatusTone.Muted;
            return;
        }
        StatusText = IsChecked ? "К ОЧИСТКЕ" : "НЕ ВЫБРАНО";
        StatusTone = IsChecked ? StatusTone.Normal : StatusTone.Muted;
    }

    public void ApplyAnalysis(ItemAnalysis a, CleanEnvironment env)
    {
        Analysis = a;
        WasCleaned = false;
        _ready = false;
        IsWorking = false;
        IsAppRunning = a.HasRunningApps;
        CloseAppTag = a.RunningApps.Count == 1 ? $"ЗАКРОЙТЕ {a.RunningApps[0].ToUpperInvariant()}" : "ЗАКРОЙТЕ ПРИЛОЖЕНИЯ";

        switch (Definition.Method)
        {
            case CleanMethod.RecycleBin:
                SizeText = a.Unavailable is null ? CleanFormat.Bytes(a.Bytes) : "—";
                CountText = a.Items is { } items ? $"{CleanFormat.Count(items)} {CleanFormat.Plural(items, "объект", "объекта", "объектов")}" : "";
                break;
            case CleanMethod.DeliveryOptimization:
                SizeText = a.Unavailable is not null || a.SizeUnknown ? "—" : CleanFormat.Bytes(a.Bytes);
                CountText = a.SizeUnknown ? "размер неизвестен" : "по данным службы";
                break;
            case CleanMethod.ComponentStore:
                SizeText = "—";
                CountText = "размер определит DISM";
                break;
            default:
                if (a.PartialAccess && a.Files == 0)
                {
                    // The folder could not be read at all: the size is unknown, not zero.
                    SizeText = "—";
                    CountText = "нет доступа к папке";
                }
                else
                {
                    SizeText = CleanFormat.Bytes(a.Bytes);
                    CountText = a.PartialAccess ? $"{CleanFormat.Files(a.Files)} · частично" : CleanFormat.Files(a.Files);
                }
                break;
        }

        string? note = null;
        if (Definition.IsUserScope && env.UserScopeBlocked)
        {
            StatusText = "ДРУГАЯ УЧЁТНАЯ ЗАПИСЬ";
            StatusTone = StatusTone.Warning;
            note = $"MONOLITH запущен от имени {env.ForeignSessionUser}: пользовательские кэши принадлежат другой учётной записи и не трогаются.";
        }
        else if (Definition.RequiresAdmin && !env.IsElevated)
        {
            // The hatched "НУЖНЫ ПРАВА АДМИНИСТРАТОРА" tag and the read-only banner already explain why.
            StatusText = "ТОЛЬКО ПРОСМОТР";
            StatusTone = StatusTone.Muted;
        }
        else if (a.Unavailable is { } unavailable)
        {
            StatusText = "НЕДОСТУПНО";
            StatusTone = StatusTone.Muted;
            note = unavailable;
        }
        else if (a.Blocker is { } blocker)
        {
            StatusText = "ОТЛОЖЕНО";
            StatusTone = StatusTone.Warning;
            note = blocker;
        }
        else if (a.HasRunningApps)
        {
            StatusText = "ЖДЁТ ЗАКРЫТИЯ";
            StatusTone = StatusTone.Warning;
            note = $"{string.Join(", ", a.RunningApps)} {(a.RunningApps.Count == 1 ? "запущен" : "запущены")} — закройте, включая значок в трее, " +
                   "иначе этот кэш будет пропущен. MONOLITH не закрывает программы сам.";
        }
        else if (a.RefusedTargets > 0 && a.Files == 0 && Definition.Method is CleanMethod.Files or CleanMethod.Tools)
        {
            SizeText = "—";
            CountText = "путь отклонён";
            StatusText = "ЗАЩИТА ПУТЕЙ";
            StatusTone = StatusTone.Muted;
        }
        else if (Definition.Method == CleanMethod.ComponentStore || a.HasWork(Definition.Method))
        {
            _ready = true;
            UpdateReadyStatus();
        }
        else
        {
            StatusText = "ЧИСТО";
            StatusTone = StatusTone.Muted;
        }

        if (note is null && a.RefusedNote is not null) note = a.RefusedNote;
        if (note is null && a.PartialAccess && env.IsElevated) note = "Часть файлов защищена системой и будет пропущена.";
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
            StatusText = "ПРОПУЩЕНО";
            StatusTone = StatusTone.Warning;
            Note = skipped;
            return;
        }

        var parts = new List<string>();
        var freed = r.Freed;
        if (freed > 0 || r.Tally.Deleted > 0 || r.ToolItems > 0)
            parts.Add($"Освобождено {(r.Approximate ? "≈" : "")}{CleanFormat.Bytes(freed)}.");
        if (r.Tally.Deleted > 0) parts.Add($"Удалено: {CleanFormat.Files(r.Tally.Deleted)}.");
        if (r.Tally.InUse > 0) parts.Add($"{CleanFormat.Count(r.Tally.InUse)} {CleanFormat.Plural(r.Tally.InUse, "файл занят", "файла заняты", "файлов заняты")} программами — пропущено.");
        if (r.Tally.Denied > 0) parts.Add($"Нет доступа: {CleanFormat.Files(r.Tally.Denied)}.");
        if (r.Tally.Changed > 0) parts.Add($"Изменились после анализа и оставлены: {CleanFormat.Files(r.Tally.Changed)}.");
        parts.AddRange(r.Notes);
        if (r.Error is { } error) parts.Add(error);

        if (r.Error is not null && freed == 0 && r.Tally.Deleted == 0)
        {
            StatusText = "ОШИБКА";
            StatusTone = StatusTone.Warning;
        }
        else if (r.Tally.Cancelled)
        {
            StatusText = "ПРЕРВАНО";
            StatusTone = StatusTone.Warning;
        }
        else
        {
            StatusText = "ОЧИЩЕНО";
            StatusTone = StatusTone.Done;
        }
        Note = parts.Count == 0 ? "Удалять было нечего." : string.Join(" ", parts);

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
            SizeText = Definition.Method is CleanMethod.ComponentStore ? (freed > 0 ? CleanFormat.Bytes(freed) : "—") : CleanFormat.Bytes(left);
            CountText = Definition.Method switch
            {
                CleanMethod.Files => left == 0 ? "пусто" : $"осталось {CleanFormat.Files(leftFiles)}",
                CleanMethod.RecycleBin => a.Items is 0 ? "пусто" : CountText,
                CleanMethod.ComponentStore => freed > 0 ? "освобождено" : "без изменений",
                _ => "после очистки",
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
            ? $"{selected} ИЗ {Items.Count}"
            : $"{CleanFormat.Bytes(size).ToUpperInvariant()} · {selected} ИЗ {Items.Count}";
    }
}

/// <summary>Free space of one fixed drive.</summary>
public sealed record DriveCard(string Name, string Label, string FreeText, string Caption, double UsedPercent, bool IsSystem);
