using System.ComponentModel;
using System.IO;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Modules.Tools;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Settings;

/// <summary>Preferences (stored per user in settings.json), data locations and "about".</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private bool _confirmingRestorePoints;

    public override string Key => "settings";
    public override string Index => "12";
    public override string Title => "НАСТРОЙКИ";
    public override string Subtitle => "Поведение программы, безопасность и сведения о MONOLITH.";
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
            Description = "Параметры с этой страницы для текущего пользователя Windows." },
    ];

    public IReadOnlyList<PrincipleItem> Principles { get; } =
    [
        new("01", "ЖУРНАЛ ДО ИЗМЕНЕНИЯ", "Исходное значение записывается в журнал раньше, чем меняется система. Любое изменение откатывается — по одному, пакетом или все сразу."),
        new("02", "ТОЧКА ВОССТАНОВЛЕНИЯ", "Перед каждым пакетом изменений создаётся точка восстановления Windows, а затронутые разделы реестра копируются в .reg."),
        new("03", "ТОЛЬКО ДОКАЗАННОЕ", "Только задокументированные настройки с подтверждённым эффектом. Мифы и «ускорители» не включены — даже скрыто."),
        new("04", "ЗАЩИЩЁННЫЕ СЛУЖБЫ", "Центр обновления, Защитник, брандмауэр, сеть, звук и другие критичные службы не изменяются никогда."),
        new("05", "БЕЗ ЧИСТКИ РЕЕСТРА", "Реестр не «чистится»: Microsoft не поддерживает такие программы, пользы нет, а риск сломать систему реален."),
        new("06", "НЕОБРАТИМОЕ — ТОЛЬКО С ПОДТВЕРЖДЕНИЕМ", "Удаление файлов, установка драйверов и программ, сброс сети не откатываются из журнала — поэтому перед ними всегда показывается подтверждение со списком последствий."),
    ];

    public IReadOnlyList<LicenseItem> Licenses { get; }

    public string VersionText { get; }
    public string BuildText { get; }
    public string RuntimeText { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    [ObservableProperty] private string _osText = SystemProfile.Current.OsText;
    public string ModeText => Elevation.IsElevated ? "Администратор — изменения разрешены" : "Просмотр — без прав администратора";

    public SettingsViewModel()
    {
        var app = Assembly.GetExecutingAssembly();
        VersionText = SettingsDefaults.VersionOf(app);
        BuildText = BuildDate();

        const string netFoundation = "© .NET Foundation and Contributors";
        Licenses =
        [
            new LicenseItem("CommunityToolkit.Mvvm", SettingsDefaults.VersionOf(typeof(ObservableObject).Assembly), "MIT", netFoundation, "MVVM: свойства и команды"),
            new LicenseItem("System.Management", SettingsDefaults.VersionOf(typeof(ManagementObject).Assembly), "MIT", netFoundation, "WMI: сведения о системе, точки восстановления"),
            new LicenseItem("System.ServiceProcess.ServiceController", SettingsDefaults.VersionOf(typeof(System.ServiceProcess.ServiceController).Assembly), "MIT", netFoundation, "Управление службами"),
            new LicenseItem(".NET и WPF", Environment.Version.ToString(3), "MIT", netFoundation, "Среда выполнения и интерфейс"),
        ];

        AppSettings.Current.PropertyChanged += OnSettingsChanged;
    }

    protected override async Task LoadAsync()
    {
        var profile = await SystemProfile.LoadAsync();
        OsText = profile.OsText;
        await RefreshPathsAsync();
    }

    protected override Task OnReturnAsync()
    {
        RaiseSettingProperties();
        return RefreshPathsAsync();
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

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Ui.Post(() =>
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CreateRestorePoints):
                if (!_confirmingRestorePoints) OnPropertyChanged(nameof(CreateRestorePoints));
                break;
            case nameof(AppSettings.MonitorIntervalMs):
                OnPropertyChanged(nameof(SelectedInterval));
                break;
            case nameof(AppSettings.ShowConsole):
                // The shell reads ShowConsole at start; keep the open console in sync right away.
                if (Ui.Host is MainViewModel main && main.ConsoleExpanded != AppSettings.Current.ShowConsole)
                    main.ConsoleExpanded = AppSettings.Current.ShowConsole;
                break;
        }
    });

    private void RaiseSettingProperties()
    {
        OnPropertyChanged(nameof(CreateRestorePoints));
        OnPropertyChanged(nameof(SelectedInterval));
        OnPropertyChanged(nameof(Settings));
    }

    // ═════════════════════════════ Safety ═════════════════════════════

    /// <summary>Turning restore points off asks first; turning them on is immediate.</summary>
    public bool CreateRestorePoints
    {
        get => AppSettings.Current.CreateRestorePoints;
        set
        {
            if (value == AppSettings.Current.CreateRestorePoints) return;
            if (value)
            {
                AppSettings.Current.CreateRestorePoints = true;
                return;
            }
            // Keep the toggle on until the user confirms (re-raised after the binding finishes its update).
            Application.Current?.Dispatcher.BeginInvoke(async () =>
            {
                OnPropertyChanged(nameof(CreateRestorePoints));
                await ConfirmDisableRestorePointsAsync();
            });
        }
    }

    private async Task ConfirmDisableRestorePointsAsync()
    {
        if (_confirmingRestorePoints) return;
        _confirmingRestorePoints = true;
        try
        {
            var ok = await Ui.ConfirmAsync(
                "Не создавать точки восстановления?",
                "MONOLITH перестанет создавать точку восстановления Windows перед пакетами изменений.",
                [
                    "Журнал отката продолжит работать: каждое изменение по-прежнему можно откатить из «Восстановления»",
                    "Пропадёт вторая страховка: если система не загрузится, вернуться будет не к чему",
                    "Пакеты изменений будут применяться быстрее — без паузы на создание точки",
                    "Включить обратно можно здесь же в любой момент",
                ],
                confirmText: "ВЫКЛЮЧИТЬ", danger: true);
            if (ok)
            {
                AppSettings.Current.CreateRestorePoints = false;
                Log.Warn("Создание точек восстановления перед изменениями выключено в настройках.");
            }
        }
        finally
        {
            _confirmingRestorePoints = false;
            OnPropertyChanged(nameof(CreateRestorePoints));
        }
    }

    // ═════════════════════════════ Interface ═════════════════════════════

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

    [RelayCommand]
    private void OpenGames() => Ui.Navigate("games");

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
