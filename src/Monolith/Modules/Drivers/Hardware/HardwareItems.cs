using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.UI;

namespace Monolith.Modules.Drivers.Hardware;

/// <summary>Per-item state of an installation run.</summary>
public enum HwJobState { None, Queued, Running, Done, Failed, Skipped }

/// <summary>A component group card ("ВИДЕОКАРТЫ", "ПЕРИФЕРИЯ"…).</summary>
public sealed partial class HwGroupItem : ObservableObject
{
    internal HwGroupItem(HwGroup g, int index, Action<HwActionItem> onSelection)
    {
        Kind = g.Kind;
        Title = g.Title;
        _onSelection = onSelection;
        Update(g, index);
    }

    private readonly Action<HwActionItem> _onSelection;

    internal HwGroupKind Kind { get; }
    public string Title { get; }
    public ObservableCollection<HwComponentItem> Components { get; } = [];

    /// <summary>At least one component passes the summary filter (a group without shown components is hidden).</summary>
    [ObservableProperty] private bool _hasShown = true;

    [ObservableProperty] private string _index = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _summaryKey = "muted";
    [ObservableProperty] private string? _footer;
    [ObservableProperty] private string? _wuExtraText;

    internal void Update(HwGroup g, int index)
    {
        Index = index.ToString("00");
        Footer = g.Footer;
        WuExtraText = g.WuExtra > 0
            ? Loc.N(g.WuExtra, "Windows Update предлагает ещё {0} драйвер для устройств этого типа.", "Windows Update предлагает ещё {0} драйвера для устройств этого типа.", "Windows Update предлагает ещё {0} драйверов для устройств этого типа.")
            : null;
        HwItemSync.Sync(Components, g.Components, c => c.Key, c => c.Key,
            c => new HwComponentItem(c, _onSelection), (item, c) => item.Update(c));

        var attention = g.Components.Count(c => HardwareRules.NeedsAttention(c.Status));
        var checking = g.Components.Any(c => c.Status == HwStatus.Unchecked && (c.StatusText == Loc.T("ИДЁТ ПОИСК") || c.StatusText == Loc.T("ПРОВЕРКА ВЕРСИИ")));
        if (attention > 0)
        {
            SummaryText = Loc.N(attention, "{0} РЕКОМЕНДАЦИЯ", "{0} РЕКОМЕНДАЦИИ", "{0} РЕКОМЕНДАЦИЙ");
            SummaryKey = "attention";
        }
        else if (checking)
        {
            SummaryText = Loc.T("ПРОВЕРКА…");
            SummaryKey = "muted";
        }
        else
        {
            var n = g.Components.Count;
            SummaryText = Loc.N(n, "{0} КОМПОНЕНТ", "{0} КОМПОНЕНТА", "{0} КОМПОНЕНТОВ");
            SummaryKey = "muted";
        }
    }
}

/// <summary>One detected device with its status and recommended actions.</summary>
public sealed partial class HwComponentItem : ObservableObject
{
    private readonly Action<HwActionItem> _onSelection;

    internal HwComponentItem(HwComponent c, Action<HwActionItem> onSelection)
    {
        Key = c.Key;
        _onSelection = onSelection;
        Update(c);
    }

    internal string Key { get; }
    public ObservableCollection<HwActionItem> Actions { get; } = [];

    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string? _note;
    [ObservableProperty] private HwStatus _status;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _statusKey = "muted";
    [ObservableProperty] private bool _hasActions;
    /// <summary>Passes the summary filter (РЕКОМЕНДАЦИЙ / АКТУАЛЬНО); the row is hidden otherwise.</summary>
    [ObservableProperty] private bool _isShown = true;
    /// <summary>Opened from a link («УСТАНОВИТЬ В «ДРАЙВЕРАХ»» on «Устройства и сеть»): framed for a moment.</summary>
    [ObservableProperty] private bool _isHighlighted;

    internal void Update(HwComponent c)
    {
        Kind = c.Kind;
        Name = c.Name;
        Details = c.Details;
        Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note;
        Status = c.Status;
        StatusText = c.StatusText ?? HardwareRules.StatusText(c.Status);
        StatusKey = HardwareRules.StatusKey(c.Status);
        HwItemSync.Sync(Actions, c.Actions, a => a.Key, a => a.Key, a => new HwActionItem(a, _onSelection), (item, a) => item.Update(a));
        HasActions = Actions.Count > 0;
    }
}

/// <summary>A recommended action: install (checkbox) or open the official page / tab / settings.</summary>
public sealed partial class HwActionItem : ObservableObject
{
    private readonly Action<HwActionItem> _onSelection;
    private bool _userTouched;

    internal HwActionItem(HwAction a, Action<HwActionItem> onSelection)
    {
        Key = a.Key;
        _onSelection = onSelection;
        Action = a;
        _isInstallable = a.IsInstallable;
        _isSelected = a.IsInstallable && a.PreSelect;
        Update(a);
    }

    internal string Key { get; }
    internal HwAction Action { get; private set; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private bool _isOptional;
    [ObservableProperty] private string? _buttonText;
    [ObservableProperty] private string _glyph = Glyphs.Info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isInstallable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    private bool _isLocked;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _jobText;
    [ObservableProperty] private HwJobState _jobState;
    [ObservableProperty] private bool _alreadyInstalled;

    public bool CanToggle => IsInstallable && !IsLocked;
    public bool HasButton => !string.IsNullOrEmpty(ButtonText);

    partial void OnButtonTextChanged(string? value) => OnPropertyChanged(nameof(HasButton));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !IsInstallable)
        {
            IsSelected = false;
            return;
        }
        _userTouched = true;
        _onSelection(this);
    }

    internal void Update(HwAction a)
    {
        var wasInstallable = IsInstallable;
        Action = a;
        Title = a.Title;
        Reason = a.Reason;
        Publisher = a.Publisher;
        SourceText = a.AlreadyInstalled ? Loc.T("УСТАНОВЛЕНО") : HardwareRules.SourceText(a.Source);
        IsOptional = a.Optional && !a.AlreadyInstalled;
        AlreadyInstalled = a.AlreadyInstalled;
        ButtonText = a.ButtonText;
        Glyph = a.Kind switch
        {
            HwActionKind.GoToTab or HwActionKind.GoToPage => "",
            HwActionKind.OpenPage or HwActionKind.OpenStore => "",
            HwActionKind.OpenSettings => "",
            _ when a.AlreadyInstalled => "",
            _ => "",
        };
        IsInstallable = a.IsInstallable;
        if (!IsInstallable && IsSelected) IsSelected = false;
        // A package that just became installable (e.g. the vendor answered) gets its default selection,
        // unless the user already decided.
        if (IsInstallable && !wasInstallable && !_userTouched && a.PreSelect && !IsSelected)
        {
            IsSelected = true;
            _userTouched = false;
        }
    }

    internal void SetJob(HwJobState state, string? text)
    {
        JobState = state;
        JobText = text;
    }
}

/// <summary>One line of the "РЕЗУЛЬТАТ УСТАНОВКИ" card.</summary>
public sealed partial class HwResultItem : ObservableObject
{
    public HwResultItem(string title, string resultText, bool ok, string? detail = null)
    {
        _title = title;
        _resultText = resultText;
        _isOk = ok;
        _detail = detail;
    }

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _resultText;
    [ObservableProperty] private bool _isOk;
    [ObservableProperty] private string? _detail;

    /// <summary>Driver installers: the version expected after the vendor installer (checked by the rescan).</summary>
    internal string? ExpectedVersion { get; init; }
    internal string? GpuPnpId { get; init; }
}

internal static class HwItemSync
{
    /// <summary>Keyed in-place sync of a bound collection: keeps existing items (and their checkbox state), moves, inserts, removes.</summary>
    public static void Sync<TItem, TRecord>(ObservableCollection<TItem> target, IReadOnlyList<TRecord> source,
        Func<TItem, string> itemKey, Func<TRecord, string> recordKey, Func<TRecord, TItem> create, Action<TItem, TRecord> update)
    {
        var keys = new HashSet<string>(source.Select(recordKey), StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(itemKey(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < source.Count; i++)
        {
            var key = recordKey(source[i]);
            var found = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (itemKey(target[j]) != key) continue;
                found = j;
                break;
            }
            if (found < 0)
            {
                target.Insert(i, create(source[i]));
                continue;
            }
            if (found != i) target.Move(found, i);
            update(target[i], source[i]);
        }
    }
}
