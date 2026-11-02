using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Devices;

// The model of «Устройства и сеть» (design devices_design.md §7.1, with the review amendments). Providers build it off
// the UI thread; the page merges it into its live cards. Nothing here changes the system: only DeviceSetting.Apply does,
// and only through the ChangeSession it is given.

public enum DeviceKind { Mouse, Keyboard, Monitor, AudioOutput, AudioInput, Network, Lighting, Controller, Peripheral }

/// <summary>How a recommendation enters the АВТОНАСТРОЙКА plan.</summary>
public enum RecommendPolicy
{
    /// <summary>Not in the plan (manual only).</summary>
    None,
    /// <summary>Pre-checked (SAFE-DEFAULT).</summary>
    Include,
    /// <summary>Offered unchecked (SAFE-OPTIONAL).</summary>
    Suggest,
    /// <summary>A finding: not selectable, a link only (Optimize tweak, vendor app, Windows settings).</summary>
    Advice,
}

public enum SettingControl { Slider, Choice, DualChoice, Toggle, Info }

public enum SettingTest { None, DoubleClick, KeyRepeat }

public enum ApplyOutcome { Applied, NoChange, Declined, Blocked, Failed }

public enum DeviceLinkKind
{
    /// <summary>ms-settings: page, started unelevated.</summary>
    Settings,
    /// <summary>mmsys.cpl tab 0–3.</summary>
    SoundPanel,
    /// <summary>An Optimize tweak (Target = tweak id).</summary>
    FocusTweak,
    /// <summary>Another page (Target = page key, FocusId optional).</summary>
    Navigate,
    /// <summary>A measurement tool on this page (Target = card key, FocusId = tool id).</summary>
    Tool,
    /// <summary>The first launchable vendor app of a card (Target = card key).</summary>
    VendorApp,
}

/// <summary>A setting value: a number, or a text key (display modes «2560x1440@240», endpoint ids).</summary>
public readonly record struct DeviceValue(long Number, string? Text = null)
{
    public static DeviceValue Of(long n) => new(n);
    public static DeviceValue Of(bool b) => new(b ? 1 : 0);
    public static DeviceValue Of(string s) => new(0, s);

    /// <summary>The text key, or the number in invariant digits.</summary>
    public string Key => Text ?? Number.ToString(CultureInfo.InvariantCulture);

    /// <summary>A dual choice (primary + secondary, e.g. resolution + refresh): «primary@secondary».</summary>
    public static DeviceValue Dual(DeviceValue primary, DeviceValue secondary) => new(0, $"{primary.Key}@{secondary.Key}");

    /// <summary>The two keys of a dual value; null for any other value.</summary>
    public (string Primary, string Secondary)? SplitDual()
    {
        if (Text is not { } t) return null;
        var at = t.LastIndexOf('@');
        return at > 0 && at < t.Length - 1 ? (t[..at], t[(at + 1)..]) : null;
    }

    public override string ToString() => Key;
}

public sealed record DeviceOption(DeviceValue Value, string Label)
{
    /// <summary>The label (what a ComboBox shows for the selected item).</summary>
    public override string ToString() => Label;
}

/// <summary>What MONOLITH recommends for a setting, and why (one line).</summary>
public sealed record Recommendation(DeviceValue Value, string Reason, RecommendPolicy Policy, DeviceLink? Link = null)
{
    /// <summary>The recommendation depends on a measurement that has not been made: the plan says so and links the tool.</summary>
    public bool NeedsMeasurement { get; init; }
}

public sealed record DeviceLink(DeviceLinkKind Kind, string Label, string Target, string? FocusId = null);

public sealed record DeviceNote(string Text, DeviceLink? Link = null);

/// <summary>Which hardware a card stands for (matching between providers and the vendor enrichment). Never shown with serials.</summary>
public sealed record DeviceHardwareRef(string? Vid, string? Pid, string? ContainerId, IReadOnlyList<string> InstanceIds);

/// <summary>Runs on the UI thread when there is one (tests and background callers without a dispatcher run inline).</summary>
internal static class DeviceUi
{
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }
}

/// <summary>Labels, groups and glyphs of the device kinds.</summary>
public static class DeviceKinds
{
    public static string Label(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "МЫШЬ",
        DeviceKind.Keyboard => "КЛАВИАТУРА",
        DeviceKind.Monitor => "МОНИТОР",
        DeviceKind.AudioOutput => "ЗВУК",
        DeviceKind.AudioInput => "МИКРОФОН",
        DeviceKind.Network => "СЕТЬ",
        DeviceKind.Lighting => "ПОДСВЕТКА",
        DeviceKind.Controller => "ГЕЙМПАД",
        _ => "УСТРОЙСТВО",
    };

    /// <summary>The ВРУЧНУЮ groups: Мышь, Клавиатура, Мониторы, Звук, Сеть, Другие устройства (in this order).</summary>
    public static (int Order, string Key, string Title) Group(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => (0, "mouse", "Мышь"),
        DeviceKind.Keyboard => (1, "keyboard", "Клавиатура"),
        DeviceKind.Monitor => (2, "monitors", "Мониторы"),
        DeviceKind.AudioOutput or DeviceKind.AudioInput => (3, "audio", "Звук"),
        DeviceKind.Network => (4, "network", "Сеть"),
        _ => (5, "other", "Другие устройства"),
    };

    public static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => Glyphs.Mouse,
        DeviceKind.Keyboard => Glyphs.Keyboard,
        DeviceKind.Monitor => Glyphs.Monitor,
        DeviceKind.AudioOutput => Glyphs.Headphone,
        DeviceKind.AudioInput => Glyphs.Microphone,
        DeviceKind.Network => Glyphs.Network,
        DeviceKind.Lighting => Glyphs.Lightbulb,
        DeviceKind.Controller => Glyphs.Game,
        _ => Glyphs.Devices,
    };

    /// <summary>"kind:monitor" focus id of a kind.</summary>
    public static string FocusId(DeviceKind kind) => "kind:" + kind.ToString().ToLowerInvariant();
}

/// <summary>
/// One setting of a device: control, live value, staged value (what ПРИМЕНИТЬ would set), recommendation, and how to
/// apply it. A setting backed by an Optimize tweak (<see cref="TweakId"/>) is never applied here directly: the plan applies
/// the tweak through TweakEngine, and ВРУЧНУЮ shows it read-only with «В ОПТИМИЗАЦИИ».
/// </summary>
public sealed partial class DeviceSetting : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    /// <summary>One line: what it does, why, when it applies (≤ 120 characters).</summary>
    public string Hint { get; init; } = "";
    public SettingControl Control { get; init; } = SettingControl.Info;
    public long Min { get; init; }
    public long Max { get; init; } = 100;
    public long Step { get; init; } = 1;
    public IReadOnlyList<DeviceOption> Options { get; init; } = [];
    /// <summary>DualChoice: the secondary options for a primary value (refresh rates of a resolution), best first.</summary>
    public Func<DeviceValue, IReadOnlyList<DeviceOption>>? SecondaryOptions { get; init; }
    /// <summary>Slider: an extra value outside the range, shown as a toggle next to it («экран целиком»).</summary>
    public DeviceOption? SpecialOption { get; init; }
    public DeviceValue? Default { get; init; }
    public Func<DeviceValue, string>? Format { get; init; }
    /// <summary>The journal target this setting writes (for ОТКАТИТЬ and Restore → «Устройства и сеть»).</summary>
    public string? JournalTarget { get; init; }
    /// <summary>Written into the account's profile: only when MONOLITH runs as the desktop user.</summary>
    public bool PerUser { get; init; }
    /// <summary>Applied with an on-screen confirmation (display mode, HDR, button swap): last in the plan, one at a time.</summary>
    public bool NeedsCountdown { get; init; }
    /// <summary>When it takes effect, as a tag («ВЫХОД ИЗ СИСТЕМЫ»); null = at once.</summary>
    public string? AppliesWhen { get; init; }
    public DeviceLink? Link { get; init; }
    public SettingTest Test { get; init; }
    public int Order { get; init; }
    /// <summary>Toggle values (default 1/0; wheel routing uses 2/0).</summary>
    public DeviceValue On { get; init; } = DeviceValue.Of(1);
    public DeviceValue Off { get; init; } = DeviceValue.Of(0);
    /// <summary>Equality of two values (a mic level within 0,5 дБ, a refresh by its integer rate); default: exact.</summary>
    public Func<DeviceValue, DeviceValue, bool>? Same { get; init; }
    /// <summary>The Optimize tweak behind this row (live state read by the scan; applied through TweakEngine).</summary>
    public string? TweakId { get; init; }
    /// <summary>Plan policy of a tweak row (default: from the tweak's risk — Safe → Include, Optional → Suggest).</summary>
    public RecommendPolicy? TweakPolicy { get; init; }
    /// <summary>A measurement shown inline in this row (one row per measurement).</summary>
    public DeviceTool? Tool { get; init; }
    /// <summary>Re-reads only this setting after a single ПРИМЕНИТЬ / ОТКАТИТЬ (blocking; off the UI thread).</summary>
    public Func<DeviceValue?>? ReadCurrent { get; init; }
    /// <summary>Applies a value through the session. Null = read-only row.</summary>
    public Func<ChangeSession, DeviceValue, CancellationToken, Task<ApplyOutcome>>? Apply { get; init; }

    [ObservableProperty] private DeviceValue? _current;
    [ObservableProperty] private DeviceValue? _staged;
    [ObservableProperty] private Recommendation? _recommended;
    [ObservableProperty] private string? _stateTag;
    [ObservableProperty] private bool _canRevert;
    [ObservableProperty] private string? _disabledReason;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _isApplying;

    // ───────────────────────────── Derived state ─────────────────────────────

    public bool IsTweakRow => TweakId is not null;
    public bool IsReadOnly => Apply is null || TweakId is not null;
    public DeviceValue? Effective => Staged ?? Current;

    public bool SameValue(DeviceValue a, DeviceValue b)
    {
        if (Same is { } same)
        {
            try { return same(a, b); }
            catch { return a == b; }
        }
        return a == b;
    }

    public string FormatValue(DeviceValue v)
    {
        if (Format is { } f)
        {
            try { return f(v); }
            catch { /* fall back to the option label */ }
        }
        if (Options.FirstOrDefault(o => SameValue(o.Value, v)) is { } option) return option.Label;
        if (SpecialOption is { } special && SameValue(special.Value, v)) return special.Label;
        if (Control == SettingControl.Toggle)
        {
            if (SameValue(v, On)) return "вкл";
            if (SameValue(v, Off)) return "выкл";
        }
        return v.Key;
    }

    /// <summary>
    /// A value given as text (the replay of ПРИМЕНИТЬ after a relaunch with admin rights) turned back into a value this row
    /// really offers: one of its options, the toggle's two states, a slider number inside its range and step, or a
    /// resolution + refresh pair both offered. Anything else is refused — the text is never trusted as a value.
    /// </summary>
    public bool TryParseValue(string key, out DeviceValue value)
    {
        value = default;
        if (string.IsNullOrEmpty(key)) return false;
        switch (Control)
        {
            case SettingControl.Choice when Options.FirstOrDefault(o => o.Value.Key == key) is { } option:
                value = option.Value;
                return true;
            case SettingControl.Toggle when On.Key == key || Off.Key == key:
                value = On.Key == key ? On : Off;
                return true;
            case SettingControl.Slider:
                if (SpecialOption is { } special && special.Value.Key == key) { value = special.Value; return true; }
                if (long.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                    && n >= Min && n <= Max && (Step <= 1 || (n - Min) % Step == 0))
                {
                    value = DeviceValue.Of(n);
                    return true;
                }
                return false;
            case SettingControl.DualChoice:
                var at = key.LastIndexOf('@');
                if (at <= 0 || at >= key.Length - 1) return false;
                var primary = Options.FirstOrDefault(o => o.Value.Key == key[..at]);
                var secondary = primary is null ? null : SecondaryFor(primary.Value).FirstOrDefault(o => o.Value.Key == key[(at + 1)..]);
                if (primary is null || secondary is null) return false;
                value = DeviceValue.Dual(primary.Value, secondary.Value);
                return true;
        }
        return false;
    }

    /// <summary>The value ПРИМЕНИТЬ would write, as text for the admin-intent parameter.</summary>
    public string? StagedKey => Staged?.Key;

    public bool IsDirty => Staged is { } s && (Current is not { } c || !SameValue(s, c));
    public bool CanApply => !IsReadOnly && IsDirty && DisabledReason is null && !IsApplying;
    /// <summary>The row's control can be used (moving it only stages a value; ПРИМЕНИТЬ applies).</summary>
    public bool ControlsEnabled => !IsReadOnly && DisabledReason is null && !IsApplying;
    /// <summary>
    /// The live value. An Info row whose only value comes from its own measurement (DPI, polling rate, ping) says «не измерено»
    /// until it ran; a row with a Windows value and a measurement beside it (the microphone level) shows that value or «—».
    /// </summary>
    public string CurrentText => Current is { } c ? FormatValue(c) : Tool is not null && Control == SettingControl.Info ? "не измерено" : "—";
    public bool MatchesRecommendation => Recommended is { } r && Current is { } c && SameValue(c, r.Value);

    /// <summary>«рек. 10» — for selectable recommendations only (not for findings or tweak rows).</summary>
    public string? RecommendedText => Recommended is { Policy: RecommendPolicy.Include or RecommendPolicy.Suggest } r && TweakId is null
        ? r.NeedsMeasurement ? "рек.: сначала измерьте" : $"рек. {FormatValue(r.Value)}"
        : null;

    public string? DefaultText => Default is { } d ? $"по умолч. {FormatValue(d)}" : null;

    // Slider
    public double SliderValue
    {
        get => Effective is { } e && !IsSpecial(e) ? Math.Clamp(e.Number, Min, Max) : Min;
        set
        {
            var step = Math.Max(1, Step);
            var snapped = Min + (long)Math.Round((value - Min) / step) * step;
            Staged = DeviceValue.Of(Math.Clamp(snapped, Min, Max));
        }
    }

    public string SliderText => Effective is { } e ? FormatValue(e) : "—";

    private bool IsSpecial(DeviceValue v) => SpecialOption is { } s && SameValue(s.Value, v);

    public bool SpecialSelected
    {
        get => Effective is { } e && IsSpecial(e);
        set
        {
            if (SpecialOption is not { } s) return;
            Staged = value ? s.Value : DeviceValue.Of(Math.Clamp(Current is { } c && !IsSpecial(c) ? c.Number : Default?.Number ?? Min, Min, Max));
        }
    }

    // Toggle
    public bool ToggleValue
    {
        get => Effective is { } e && SameValue(e, On);
        set => Staged = value ? On : Off;
    }

    // Choice
    public DeviceOption? SelectedOption
    {
        get => Effective is { } e ? Options.FirstOrDefault(o => SameValue(o.Value, e)) : null;
        set
        {
            if (value is not null) Staged = value.Value;
        }
    }

    // DualChoice: primary → its secondary list; a secondary that the new primary does not offer resets to the first (best).
    public DeviceOption? SelectedPrimary
    {
        get => Effective?.SplitDual() is { } split ? Options.FirstOrDefault(o => o.Value.Key == split.Primary) : null;
        set
        {
            if (value is null) return;
            var list = SecondaryFor(value.Value);
            var keep = Effective?.SplitDual()?.Secondary;
            var secondary = list.FirstOrDefault(o => o.Value.Key == keep) ?? list.FirstOrDefault();
            if (secondary is not null) Staged = DeviceValue.Dual(value.Value, secondary.Value);
        }
    }

    public IReadOnlyList<DeviceOption> SecondaryList => SelectedPrimary is { } p ? SecondaryFor(p.Value) : [];

    public DeviceOption? SelectedSecondary
    {
        get => Effective?.SplitDual() is { } split ? SecondaryList.FirstOrDefault(o => o.Value.Key == split.Secondary) : null;
        set
        {
            if (value is not null && SelectedPrimary is { } p) Staged = DeviceValue.Dual(p.Value, value.Value);
        }
    }

    public DeviceValue? StagedPrimary => Staged?.SplitDual() is { } s ? Options.FirstOrDefault(o => o.Value.Key == s.Primary)?.Value : null;
    public DeviceValue? StagedSecondary => Staged?.SplitDual() is { } s && StagedPrimary is { } p
        ? SecondaryFor(p).FirstOrDefault(o => o.Value.Key == s.Secondary)?.Value : null;

    private IReadOnlyList<DeviceOption> SecondaryFor(DeviceValue primary)
    {
        try { return SecondaryOptions?.Invoke(primary) ?? []; }
        catch { return []; }
    }

    private static readonly string[] Derived =
    [
        nameof(Effective), nameof(IsDirty), nameof(CanApply), nameof(CurrentText), nameof(MatchesRecommendation), nameof(RecommendedText),
        nameof(SliderValue), nameof(SliderText), nameof(SpecialSelected), nameof(ToggleValue), nameof(SelectedOption),
        nameof(SelectedPrimary), nameof(SecondaryList), nameof(SelectedSecondary), nameof(StagedPrimary), nameof(StagedSecondary),
    ];

    private void RaiseDerived()
    {
        foreach (var name in Derived) OnPropertyChanged(name);
    }

    partial void OnCurrentChanged(DeviceValue? value) => RaiseDerived();
    partial void OnStagedChanged(DeviceValue? value) => RaiseDerived();
    partial void OnRecommendedChanged(Recommendation? value) => RaiseDerived();
    partial void OnDisabledReasonChanged(string? value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ControlsEnabled));
    }

    partial void OnIsApplyingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(ControlsEnabled));
    }

    /// <summary>A tweak-backed row: «применено» / «не применено».</summary>
    public static string TweakStateText(DeviceValue v) => v.Number != 0 ? "применено" : "не применено";

    /// <summary>
    /// A row backed by an Optimize tweak: live state and plan row come from the tweak itself (read by the scan).
    /// <paramref name="format"/> words the state for this row (1 = the tweak is applied), e.g. «выкл» / «вкл» for
    /// «Повышенная точность указателя» backed by «mouse-acceleration-off»; default «применено» / «не применено».
    /// </summary>
    public static DeviceSetting ForTweak(string tweakId, string title, string hint, int order = 0, RecommendPolicy? policy = null,
        Func<DeviceValue, string>? format = null) => new()
    {
        Id = "tweak:" + tweakId, Title = title, Hint = hint, Control = SettingControl.Info, TweakId = tweakId, TweakPolicy = policy,
        Order = order, Format = format ?? TweakStateText,
    };
}

/// <summary>A measurement (polling rate, DPI, mic level, ping…). Never starts by itself; never changes the system.</summary>
public sealed partial class DeviceTool : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public string ButtonText { get; init; } = "ИЗМЕРИТЬ";
    public IReadOnlyList<DeviceOption> Options { get; init; } = [];
    public bool HasOptions => Options.Count > 0;
    /// <summary>The run happens on a drag pad in the row (DPI test): the pad captures the mouse so nothing else reacts.</summary>
    public bool NeedsDragPad { get; init; }
    public string PadText { get; init; } = "Зажмите левую кнопку здесь и, не отпуская, проведите мышь вправо";
    public required Func<DeviceToolRun, CancellationToken, Task> Run { get; init; }

    [ObservableProperty] private DeviceOption? _selectedOption;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _progress;
    /// <summary>A live level 0..1 (mic meter); null = no meter.</summary>
    [ObservableProperty] private double? _level;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private string? _resultText;
    [ObservableProperty] private string? _resultDetail;
    [ObservableProperty] private bool _isHighlighted;
    [ObservableProperty] private bool _padHeld;

    /// <summary>The drag pad was pressed (true) or released (false) — the tool pairs raw input with these.</summary>
    public event Action<bool>? PadChanged;

    partial void OnPadHeldChanged(bool value)
    {
        try { PadChanged?.Invoke(value); }
        catch (Exception ex) { Log.Warn($"{Title}: {ex.Message}"); }
    }

    partial void OnLevelChanged(double? value) => OnPropertyChanged(nameof(LevelPercent));

    public double LevelPercent => Math.Clamp((Level ?? 0) * 100, 0, 100);
    public string RunButtonText => IsRunning ? "ОТМЕНА" : ButtonText;
    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(RunButtonText));

    private CancellationTokenSource? _cts;

    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Runs the tool once (UI thread). Errors become the result text; expected failures never log errors.</summary>
    internal async Task StartAsync(DeviceCard card, DeviceResultStore results)
    {
        if (IsRunning) return;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        Progress = 0;
        Level = null;
        StatusText = null;
        ResultText = null;
        ResultDetail = null;
        var run = new DeviceToolRun(this, card, results, cts.Token);
        try
        {
            await Run(run, cts.Token);
        }
        catch (OperationCanceledException)
        {
            DeviceUi.Post(() => StatusText = "Отменено.");
        }
        catch (Exception ex)
        {
            Log.Warn($"{Title}: {ex.Message}");
            DeviceUi.Post(() => ResultText = ex.Message);
        }
        finally
        {
            _cts = null;
            DeviceUi.Post(() =>
            {
                IsRunning = false;
                PadHeld = false;
                Level = null;
            });
        }
    }
}

/// <summary>What a running tool reports. Every call is marshalled to the UI thread.</summary>
public sealed class DeviceToolRun(DeviceTool tool, DeviceCard card, DeviceResultStore results, CancellationToken token)
{
    public DeviceTool Tool { get; } = tool;
    public DeviceCard Card { get; } = card;
    public DeviceResultStore Results { get; } = results;
    public CancellationToken Token { get; } = token;

    public void Report(double progress, string status, double? level = null) => DeviceUi.Post(() =>
    {
        Tool.Progress = Math.Clamp(progress, 0, 1);
        Tool.StatusText = status;
        Tool.Level = level;
    });

    public void Complete(string result, string? detail = null) => DeviceUi.Post(() =>
    {
        Tool.Progress = 1;
        Tool.StatusText = null;
        Tool.ResultText = result;
        Tool.ResultDetail = detail;
    });

    public void Fail(string message) => DeviceUi.Post(() =>
    {
        Tool.StatusText = null;
        Tool.ResultText = message;
        Tool.ResultDetail = null;
    });
}

/// <summary>A vendor app of a device: launched from here (signed, unelevated), installed from Drivers.</summary>
public sealed partial class VendorAppLink : ObservableObject
{
    public required string AppName { get; init; }
    public bool IsInstalled { get; init; }
    public string? Note { get; init; }
    public string? Tooltip { get; init; }
    /// <summary>Starts the app (null = no verified way to start it).</summary>
    public Func<Task<bool>>? Launch { get; init; }
    /// <summary>Not installed: «УСТАНОВИТЬ В «ДРАЙВЕРАХ»».</summary>
    public bool OfferInstall { get; init; }
    /// <summary>Focus id of the matching row on Drivers → ПОДБОР ДЛЯ ВАШЕГО ПК ("periph:046D").</summary>
    public string? InstallFocusId { get; init; }

    public bool CanLaunch => Launch is not null;
    public bool ShowInstall => !CanLaunch && OfferInstall;
    public bool IsVisible => CanLaunch || ShowInstall;
    public string ButtonText => CanLaunch ? $"ОТКРЫТЬ {AppName.ToUpper(CultureInfo.CurrentCulture)}" : "УСТАНОВИТЬ В «ДРАЙВЕРАХ»";
}

/// <summary>One detected device (or the network connection): settings, tools, notes and vendor apps.</summary>
public sealed partial class DeviceCard : ObservableObject
{
    /// <summary>"mouse", "keyboard", "display:&lt;monitor path&gt;", "audio:&lt;endpoint id&gt;", "net:&lt;guid&gt;", "periph:&lt;group&gt;".</summary>
    public required string Key { get; init; }
    public required DeviceKind Kind { get; init; }
    public required string Title { get; init; }
    public string? Subtitle { get; init; }
    public int Order { get; init; }
    public string? GlyphOverride { get; init; }
    public List<DeviceHardwareRef> Hardware { get; init; } = [];
    public ObservableCollection<DeviceSetting> Settings { get; init; } = [];
    public ObservableCollection<DeviceTool> Tools { get; init; } = [];
    public ObservableCollection<VendorAppLink> VendorApps { get; init; } = [];

    /// <summary>Facts needed to configure it («2560×1440 · 239,97 Гц · DisplayPort»), numbers only — no serials.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FactsText))]
    private IReadOnlyList<string> _facts = [];

    /// <summary>Notes shown only when actionable.</summary>
    [ObservableProperty] private IReadOnlyList<DeviceNote> _notes = [];
    [ObservableProperty] private bool _isHighlighted;

    public string Glyph => GlyphOverride ?? DeviceKinds.Glyph(Kind);
    public string KindLabel => DeviceKinds.Label(Kind);
    /// <summary>The kind label is hidden when the title already says it («Мышь»).</summary>
    public bool ShowKindLabel => !string.Equals(Title.Trim(), KindLabel, StringComparison.OrdinalIgnoreCase);
    public string FactsText => string.Join(" · ", Facts);
    public string GroupTitle => DeviceKinds.Group(Kind).Title;
}

/// <summary>Measurement results kept for the app session (a re-scan keeps them). Thread-safe.</summary>
public sealed class DeviceResultStore
{
    private readonly ConcurrentDictionary<string, object> _values = new(StringComparer.Ordinal);

    public T? Get<T>(string key) => _values.TryGetValue(key, out var v) && v is T t ? t : default;
    public void Set<T>(string key, T value) where T : notnull => _values[key] = value;
    public bool Remove(string key) => _values.TryRemove(key, out _);
}
