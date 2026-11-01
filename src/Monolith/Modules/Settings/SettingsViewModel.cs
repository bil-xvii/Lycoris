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
    public override string Title => "НАСТРОЙКИ";
    public override string Subtitle => "Поведение программы, данные и сведения о MONOLITH.";
    public override string Glyph => Glyphs.Settings;

    /// <summary>Bound directly for simple toggles; every change is saved by AppSettings itself.</summary>
    public AppSettings Settings => AppSettings.Current;

    public IReadOnlyList<IntervalOption> IntervalOptions { get; } = SettingsDefaults.IntervalChoices
        .Select(ms => new IntervalOption(ms, SettingsDefaults.FormatInterval(ms), ms switch
        {
            <= 500 => "максимально плавно",
            <= 1000 => "по умолчанию",
            <= 2000 => "экономнее",
            _ => "минимальная нагрузка",
        }))
        .ToList();

    public IReadOnlyList<DataPathItem> DataPaths { get; } =
    [
        new DataPathItem { Title = "ЖУРНАЛ ИЗМЕНЕНИЙ", Path = AppPaths.JournalFile, IsFile = true,
            Description = "Исходные значения всего, что менял MONOLITH. По нему работает откат." },
        new DataPathItem { Title = "РЕЗЕРВНЫЕ КОПИИ", Path = AppPaths.BackupsDir,
            Description = "Копии разделов реестра (.reg) перед каждым пакетом изменений." },
        new DataPathItem { Title = "ЖУРНАЛЫ РАБОТЫ", Path = AppPaths.LogsDir,
            Description = "Ежедневные логи консоли и отчёты; хранятся 14 дней." },
        new DataPathItem { Title = "НАСТРОЙКИ", Path = AppPaths.SettingsFile, IsFile = true,
            Description = "Параметры MONOLITH для текущего пользователя Windows." },
    ];

    public IReadOnlyList<PrincipleItem> Principles { get; } =
    [
        new("ЖУРНАЛ ДО ИЗМЕНЕНИЯ", "Исходное значение записывается в журнал раньше, чем меняется система. Любое изменение настроек (реестр, службы, задачи, схемы питания) откатывается — по одному, пакетом или все сразу."),
        new("ТОЧКА ВОССТАНОВЛЕНИЯ", "Перед каждым пакетом изменений создаётся точка восстановления Windows (если защита системы включена), а затронутые разделы реестра копируются в .reg."),
        new("ТОЛЬКО ДОКАЗАННОЕ", "Каждый пункт честно описывает свой эффект — даже если он близок к нулю. Приёмы без работающего механизма и «ускорители» не включены."),
        new("ЗАЩИЩЁННЫЕ СЛУЖБЫ", "Центр обновления, Защитник, брандмауэр, сеть, звук и другие критичные службы не изменяются никогда."),
        new("БЕЗ ЧИСТКИ РЕЕСТРА", "Реестр не «чистится»: Microsoft не поддерживает такие программы, пользы нет, а риск сломать систему реален."),
        new("НЕОБРАТИМОЕ — ТОЛЬКО С ПОДТВЕРЖДЕНИЕМ", "Удаление файлов, установка драйверов и программ, сброс сети не откатываются из журнала — поэтому перед ними всегда показывается подтверждение со списком последствий."),
    ];

    /// <summary>«Что MONOLITH не делает и почему» — the single rejected-tweaks list (Tweaks/RejectedTweaks.cs), one row each.</summary>
    public IReadOnlyList<RejectedRow> Rejected { get; } = RejectedTweaks.All.Select(r => new RejectedRow(r)).ToList();

    /// <summary>The «ЧТО MONOLITH НЕ ДЕЛАЕТ И ПОЧЕМУ» expander; opened by a palette focus request («myth:{id}»).</summary>
    [ObservableProperty] private bool _expandRejected;

    /// <summary>Id of the rejected-tweak row the view should scroll to (set after a focus request; one-shot, see SettingsView).</summary>
    [ObservableProperty] private string? _focusedRejectedId;

    private readonly DispatcherTimer _highlightTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private RejectedRow? _highlighted;

    public IReadOnlyList<LicenseItem> Licenses { get; }

    /// <summary>One line: version · build date · runtime.</summary>
    public string VersionLine { get; }

    public SettingsViewModel()
    {
        var app = Assembly.GetExecutingAssembly();
        VersionLine = $"MONOLITH {SettingsDefaults.VersionOf(app)} · сборка {BuildDate()} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";

        const string netFoundation = "© .NET Foundation and Contributors";
        Licenses =
        [
            new LicenseItem("CommunityToolkit.Mvvm", SettingsDefaults.VersionOf(typeof(ObservableObject).Assembly), "MIT", netFoundation, "MVVM: свойства и команды"),
            new LicenseItem("System.Management", SettingsDefaults.VersionOf(typeof(ManagementObject).Assembly), "MIT", netFoundation, "WMI: сведения о системе, точки восстановления"),
            new LicenseItem("System.ServiceProcess.ServiceController", SettingsDefaults.VersionOf(typeof(System.ServiceProcess.ServiceController).Assembly), "MIT", netFoundation, "Управление службами"),
            new LicenseItem(".NET и WPF", Environment.Version.ToString(3), "MIT", netFoundation, "Среда выполнения и интерфейс"),
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
            return File.Exists(file) ? File.GetLastWriteTime(file).ToString("dd.MM.yyyy") : "—";
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
                if (!File.Exists(item.Path)) return (false, "ещё не создан");
                var fi = new FileInfo(item.Path);
                return (true, $"{SystemProfile.FormatBytes(fi.Length)} · изменён {fi.LastWriteTime:dd.MM.yyyy HH:mm}");
            }
            if (!Directory.Exists(item.Path)) return (false, "ещё не создана");
            var files = Directory.EnumerateFiles(item.Path, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(f => new FileInfo(f)).ToList();
            if (files.Count == 0) return (true, "пусто");
            var size = files.Sum(f => f.Length);
            return (true, $"{files.Count} {RestoreFormat.Plural(files.Count, "файл", "файла", "файлов")} · {SystemProfile.FormatBytes(size)}");
        }
        catch (Exception ex)
        {
            return (false, "недоступно: " + ex.Message);
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
