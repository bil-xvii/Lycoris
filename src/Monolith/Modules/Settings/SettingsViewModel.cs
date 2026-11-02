using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Modules.Tools;
using Monolith.Shell;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Settings;

/// <summary>Preferences (stored per user in settings.json), data locations and "about".</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public override string Key => "settings";
    public override string Index => "11";
    public override string Title => Loc.T("НАСТРОЙКИ");
    public override string Subtitle => Loc.T("Поведение программы, данные и сведения о MONOLITH.");
    public override string Glyph => Glyphs.Settings;

    /// <summary>Bound directly for simple toggles; every change is saved by AppSettings itself.</summary>
    public AppSettings Settings => AppSettings.Current;

    public IReadOnlyList<IntervalOption> IntervalOptions { get; } = SettingsDefaults.IntervalChoices
        .Select(ms => new IntervalOption(ms, SettingsDefaults.FormatInterval(ms), ms switch
        {
            <= 500 => Loc.T("максимально плавно"),
            <= 1000 => Loc.T("по умолчанию"),
            <= 2000 => Loc.T("экономнее"),
            _ => Loc.T("минимальная нагрузка"),
        }))
        .ToList();

    public IReadOnlyList<DataPathItem> DataPaths { get; } =
    [
        new DataPathItem { Title = Loc.T("ЖУРНАЛ ИЗМЕНЕНИЙ"), Path = AppPaths.JournalFile, IsFile = true,
            Description = Loc.T("Исходные значения всего, что менял MONOLITH. По нему работает откат.") },
        new DataPathItem { Title = Loc.T("РЕЗЕРВНЫЕ КОПИИ"), Path = AppPaths.BackupsDir,
            Description = Loc.T("Копии разделов реестра (.reg) перед каждым пакетом изменений.") },
        new DataPathItem { Title = Loc.T("ЖУРНАЛЫ РАБОТЫ"), Path = AppPaths.LogsDir,
            Description = Loc.T("Ежедневные логи консоли и отчёты; хранятся 14 дней.") },
        new DataPathItem { Title = Loc.T("НАСТРОЙКИ"), Path = AppPaths.SettingsFile, IsFile = true,
            Description = Loc.T("Параметры MONOLITH для текущего пользователя Windows.") },
    ];

    public IReadOnlyList<PrincipleItem> Principles { get; } =
    [
        new(Loc.T("ЖУРНАЛ ДО ИЗМЕНЕНИЯ"), Loc.T("Исходное значение записывается в журнал раньше, чем меняется система. Любое изменение настроек (реестр, службы, задачи, схемы питания) откатывается — по одному, пакетом или все сразу.")),
        new(Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ"), Loc.T("Перед каждым пакетом изменений создаётся точка восстановления Windows (если защита системы включена), а затронутые разделы реестра копируются в .reg.")),
        new(Loc.T("ТОЛЬКО ДОКАЗАННОЕ"), Loc.T("Каждый пункт честно описывает свой эффект — даже если он близок к нулю. Приёмы без работающего механизма и «ускорители» не включены.")),
        new(Loc.T("ЗАЩИЩЁННЫЕ СЛУЖБЫ"), Loc.T("Центр обновления, Защитник, брандмауэр, сеть, звук и другие критичные службы не изменяются никогда.")),
        new(Loc.T("БЕЗ ЧИСТКИ РЕЕСТРА"), Loc.T("Реестр не «чистится»: Microsoft не поддерживает такие программы, пользы нет, а риск сломать систему реален.")),
        new(Loc.T("НЕОБРАТИМОЕ — ТОЛЬКО С ПОДТВЕРЖДЕНИЕМ"), Loc.T("Удаление файлов, установка драйверов и программ, сброс сети не откатываются из журнала — поэтому перед ними всегда показывается подтверждение со списком последствий.")),
    ];

    /// <summary>«Что MONOLITH не делает и почему» — the single rejected-tweaks list (Tweaks/RejectedTweaks.cs), one row each.</summary>
    public IReadOnlyList<RejectedRow> Rejected { get; } = RejectedTweaks.All.Select(r => new RejectedRow(r)).ToList();

    /// <summary>The «ЧТО MONOLITH НЕ ДЕЛАЕТ И ПОЧЕМУ» expander; opened by a palette focus request («myth:{id}»).</summary>
    [ObservableProperty] private bool _expandRejected;

    /// <summary>Id of the rejected-tweak row the view should scroll to (set after a focus request; one-shot, see SettingsView).</summary>
    [ObservableProperty] private string? _focusedRejectedId;

    /// <summary>Row of «setting:&lt;row&gt;» the view scrolls to (elevate, confirm, monitor-interval, data, reset, about, licenses); one-shot.</summary>
    [ObservableProperty] private string? _focusedSettingId;

    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private RejectedRow? _highlighted;

    public IReadOnlyList<LicenseItem> Licenses { get; }

    /// <summary>One line: version · build date · runtime.</summary>
    public string VersionLine { get; }

    public SettingsViewModel()
    {
        var app = Assembly.GetExecutingAssembly();
        VersionLine = Loc.Tf("MONOLITH {0} · сборка {1} · {2} · {3}", SettingsDefaults.VersionOf(app), BuildDate(), RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());

        const string netFoundation = "© .NET Foundation and Contributors";
        Licenses =
        [
            new LicenseItem("CommunityToolkit.Mvvm", SettingsDefaults.VersionOf(typeof(ObservableObject).Assembly), "MIT", netFoundation, Loc.T("MVVM: свойства и команды")),
            new LicenseItem("System.Management", SettingsDefaults.VersionOf(typeof(ManagementObject).Assembly), "MIT", netFoundation, Loc.T("WMI: сведения о системе, точки восстановления")),
            new LicenseItem("System.ServiceProcess.ServiceController", SettingsDefaults.VersionOf(typeof(System.ServiceProcess.ServiceController).Assembly), "MIT", netFoundation, Loc.T("Управление службами")),
            new LicenseItem(Loc.T(".NET и WPF"), Environment.Version.ToString(3), "MIT", netFoundation, Loc.T("Среда выполнения и интерфейс")),
        ];

        AppSettings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.MonitorIntervalMs)) Ui.Post(() => OnPropertyChanged(nameof(SelectedInterval)));
        };

        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            if (_highlighted is not null) _highlighted.IsHighlighted = false;
            _highlighted = null;
        };
        // Command palette («НЕ ДЕЛАЕМ») / other pages: open the rejected-tweaks list on one row. The list is static data,
        // so the request can be handled whenever it arrives.
        Ui.FocusRequested += r =>
        {
            if (r.PageKey == Key) ApplyFocus(Ui.TakeFocusRequest(Key));
        };
    }

    protected override Task LoadAsync()
    {
        ApplyFocus(Ui.TakeFocusRequest(Key));
        MarkChecked();
        return RefreshPathsAsync();
    }

    protected override Task OnReturnAsync()
    {
        RaiseSettingProperties();
        ApplyFocus(Ui.TakeFocusRequest(Key));
        return RefreshPathsAsync();
    }

    /// <summary>Handles «myth:{id}»: expands the list, highlights the row for a moment and asks the view to scroll to it.</summary>
    private void ApplyFocus(string? focusId)
    {
        if (focusId is not null && focusId.StartsWith(CommandPaletteIndex.SettingFocusPrefix, StringComparison.Ordinal))
        {
            FocusedSettingId = null; // re-raise even for the same row
            FocusedSettingId = focusId[CommandPaletteIndex.SettingFocusPrefix.Length..];
            return;
        }
        if (focusId is null || !focusId.StartsWith(RejectedTweaks.FocusPrefix, StringComparison.Ordinal)) return;
        var id = focusId[RejectedTweaks.FocusPrefix.Length..];
        var row = Rejected.FirstOrDefault(r => r.Id == id);
        if (row is null) return;

        ExpandRejected = true;
        if (_highlighted is not null && !ReferenceEquals(_highlighted, row)) _highlighted.IsHighlighted = false;
        _highlighted = row;
        row.IsHighlighted = true;
        _highlightTimer.Stop();
        _highlightTimer.Start();

        FocusedRejectedId = null; // re-raise even when the same row is requested twice
        FocusedRejectedId = id;
    }

    private static string BuildDate()
    {
        try
        {
            var file = AppPaths.ExecutablePath;
            return File.Exists(file) ? Loc.DateText(File.GetLastWriteTime(file)) : "—";
        }
        catch
        {
            return "—";
        }
    }

    private void RaiseSettingProperties()
    {
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(Settings));
    }

    // ═════════════════════════════ Language ═════════════════════════════

    private string _languageChoice = Loc.Mode;

    /// <summary>The segmented choice: "auto" | "ru" | "en". Saved at once; the language itself is read at startup.</summary>
    public string LanguageChoice
    {
        get => _languageChoice;
        set
        {
            var v = Loc.NormalizeMode(value);
            if (v == _languageChoice) return;
            _languageChoice = v;
            AppSettings.Current.Language = v;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LanguagePending));
            OnPropertyChanged(nameof(LanguageNote));
            if (LanguagePending) _ = OfferRestartAsync();
        }
    }

    /// <summary>The chosen language is not the one this run uses: it applies after a restart.</summary>
    public bool LanguagePending => _languageChoice != Loc.Mode;

    public string? LanguageNote => LanguagePending ? Loc.T("Язык изменится после перезапуска MONOLITH.") : null;

    /// <summary>Asked once, in BOTH languages (the user may not read the one that is on screen now).</summary>
    private async Task OfferRestartAsync()
    {
        var restart = await Ui.ConfirmAsync(Loc.Keep("Язык / Language"),
            Loc.Keep("Перезапустить MONOLITH сейчас, чтобы применить язык?\nRestart MONOLITH now to apply the language?"),
            confirmText: Loc.Keep("ПЕРЕЗАПУСТИТЬ / RESTART"), cancelText: Loc.Keep("ПОЗЖЕ / LATER"));
        if (restart) Ui.RestartApp();
    }

    [RelayCommand]
    private void RestartForLanguage() => Ui.RestartApp();

    // ═════════════════════════════ Monitoring ═════════════════════════════

    public IntervalOption? SelectedInterval
    {
        get
        {
            var ms = SettingsDefaults.NearestInterval(AppSettings.Current.MonitorIntervalMs);
            return IntervalOptions.FirstOrDefault(o => o.Milliseconds == ms);
        }
        set
        {
            if (value is null || value.Milliseconds == AppSettings.Current.MonitorIntervalMs) return;
            AppSettings.Current.MonitorIntervalMs = value.Milliseconds;
        }
    }

    // ═════════════════════════════ Data ═════════════════════════════

    private async Task RefreshPathsAsync()
    {
        var states = await Task.Run(() => DataPaths.Select(p => Describe(p)).ToList());
        for (var i = 0; i < DataPaths.Count; i++)
        {
            DataPaths[i].Exists = states[i].Exists;
            DataPaths[i].State = states[i].Text;
        }
    }

    private static (bool Exists, string Text) Describe(DataPathItem item)
    {
        try
        {
            if (item.IsFile)
            {
                if (!File.Exists(item.Path)) return (false, Loc.T("ещё не создан"));
                var fi = new FileInfo(item.Path);
                return (true, Loc.Tf("{0} · изменён {1}", SystemProfile.FormatBytes(fi.Length), Loc.DateTimeText(fi.LastWriteTime)));
            }
            if (!Directory.Exists(item.Path)) return (false, Loc.T("ещё не создана"));
            var files = Directory.EnumerateFiles(item.Path, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(f => new FileInfo(f)).ToList();
            if (files.Count == 0) return (true, Loc.T("пусто"));
            var size = files.Sum(f => f.Length);
            return (true, Loc.N(files.Count, "{0} файл", "{0} файла", "{0} файлов") + " · " + SystemProfile.FormatBytes(size));
        }
        catch (Exception ex)
        {
            return (false, Loc.Tf("недоступно: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private void OpenPath(DataPathItem? item)
    {
        if (item is null) return;
        if (item.IsFile) ShellLauncher.RevealFile(item.Path, item.Title);
        else ShellLauncher.OpenFolder(item.Path, item.Title);
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (!await Ui.ConfirmAsync("Сбросить настройки?",
                "Параметры MONOLITH вернутся к значениям по умолчанию.",
                SettingsDefaults.Describe(),
                confirmText: "СБРОСИТЬ"))
            return;

        SettingsDefaults.Apply(AppSettings.Current);
        AppSettings.Current.Save();
        RaiseSettingProperties();
        await RefreshPathsAsync();
        Log.Info("Настройки сброшены к значениям по умолчанию.");
        Ui.Toast("Настройки сброшены к значениям по умолчанию.", ToastKind.Success);
    }
}
