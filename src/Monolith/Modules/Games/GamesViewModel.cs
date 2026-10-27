using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Games;

public sealed partial class GamesViewModel : PageViewModel
{
    private readonly DispatcherTimer _ticker;
    private List<GameCardViewModel> _all = [];
    private bool _staleOffered;
    private bool _active;
    private DateTime _lastScan = DateTime.MinValue;

    public override string Key => "games";
    public override string Index => "03";
    public override string Title => "ИГРЫ";
    public override string Subtitle => "Библиотека игр и игровой сеанс: максимум ресурсов игре, пока она запущена.";
    public override string Glyph => Glyphs.Game;

    public GamesViewModel()
    {
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _ticker.Tick += (_, _) => UpdateElapsed();

        Boost.PropertyChanged += OnBoostChanged;
        Boost.Ended += () =>
        {
            CheckStale();
            _ = RefreshReadinessAsync();
            _ = RefreshPowerNoteAsync();
        };
        Settings.PropertyChanged += OnSettingsChanged;
        EditorList.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EditorListEmpty));
        if (Application.Current is { } app) app.Exit += (_, _) => Boost.StopOnExit();
        UpdateCloseList();
    }

    public GameBoostService Boost { get; } = new();
    public AppSettings Settings { get; } = AppSettings.Current;

    // ───────────────────────────── Library state ─────────────────────────────

    public ObservableCollection<GameCardViewModel> VisibleGames { get; } = [];
    public ObservableCollection<PlatformFilterItem> Platforms { get; } = [];
    public ObservableCollection<SourceStatus> Sources { get; } = [];

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasLibrary;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private string _scanText = "";
    [ObservableProperty] private string _sourcesFooter = "";

    public string CountUnit => GameText.Plural(TotalCount, "ИГРА", "ИГРЫ", "ИГР") + " В БИБЛИОТЕКЕ";
    public bool ShowFirstScan => IsScanning && !HasLibrary;
    public bool IsLibraryEmpty => HasLibrary && TotalCount == 0;
    public bool IsFilterEmpty => TotalCount > 0 && VisibleGames.Count == 0;
    public bool HasVisibleGames => VisibleGames.Count > 0;
    public bool HasGames => TotalCount > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ShowFirstScan));
    partial void OnHasLibraryChanged(bool value) => NotifyLibraryState();
    partial void OnTotalCountChanged(int value) => NotifyLibraryState();

    // ───────────────────────────── Boost state ─────────────────────────────

    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private string _powerNote = "";
    [ObservableProperty] private int _closeListCount;
    [ObservableProperty] private string _closeListSummary = "";
    [ObservableProperty] private bool _hasStaleBoost;
    [ObservableProperty] private string _staleText = "";

    public bool CanStartBoost => IsElevated && !Boost.IsActive && !Boost.IsTransitioning;
    public bool CanLaunchBoosted => IsElevated && !Boost.IsActive && !Boost.IsTransitioning;
    public string BoostTooltip => !IsElevated
        ? "Игровой сеанс меняет схему питания — нужны права администратора."
        : Boost.IsActive
            ? "Игровой сеанс уже идёт — сначала остановите его."
            : "Запустить игру и на время игры начать игровой сеанс.";
    public string StartTooltip => !IsElevated
        ? "Игровой сеанс меняет схему питания — нужны права администратора."
        : "На время игры: схема питания, приоритет игры и закрытие лишних программ. Схема питания и приоритет возвращаются сами; " +
          "закрытые программы MONOLITH заново не открывает.";
    public bool IsBoostIdle => !Boost.IsActive;
    public string BoostStatus => Boost.IsTransitioning && !Boost.IsActive ? "ЗАПУСК"
        : Boost.IsActive ? (string.IsNullOrEmpty(Boost.PhaseText) ? "АКТИВЕН" : Boost.PhaseText)
        : IsElevated ? "ВЫКЛЮЧЕН" : "НЕДОСТУПЕН";

    // ───────────────────────────── Readiness state ─────────────────────────────

    [ObservableProperty] private string _readinessText = "";
    [ObservableProperty] private bool _readinessLoaded;

    // ───────────────────────────── Per-game GPU state (hybrid PCs) ─────────────────────────────

    /// <summary>Two or more real GPUs (iGPU + dGPU) on Windows 10 1803+: only then the per-game switch is shown.</summary>
    [ObservableProperty] private bool _showGpuToggle;
    /// <summary>False when MONOLITH runs as another admin account (HKCU is not the player's): state is shown read-only.</summary>
    [ObservableProperty] private bool _gpuWritable;
    [ObservableProperty] private bool _isGpuChanging;
    private string? _dgpuName;
    private IReadOnlyDictionary<string, string> _gpuValues = new Dictionary<string, string>();

    public bool CanChangeGpu => IsElevated && GpuWritable && !IsGpuChanging;
    partial void OnGpuWritableChanged(bool value) => OnPropertyChanged(nameof(CanChangeGpu));
    partial void OnIsGpuChangingChanged(bool value) => OnPropertyChanged(nameof(CanChangeGpu));

    // ───────────────────────────── Close-list editor state ─────────────────────────────

    public ObservableCollection<string> EditorList { get; } = [];
    public ObservableCollection<RunningAppItem> RunningApps { get; } = [];
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private bool _isLoadingRunning;
    [ObservableProperty] private string _newProcessName = "";

    public bool EditorListEmpty => EditorList.Count == 0;

    // ───────────────────────────── Lifecycle ─────────────────────────────

    protected override async Task LoadAsync()
    {
        _active = true;
        var cache = await Task.Run(GameLibrary.LoadCache);
        if (cache is not null) ApplyScan(cache);

        CheckStale();
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), RefreshGpuAsync(), ScanAsync());
        await OfferStaleRestoreAsync();
    }

    protected override async Task OnReturnAsync()
    {
        _active = true;
        if (Boost.IsActive) StartTicker();
        CheckStale();
        // Library changes rarely: rescan on return only when the last scan is a few minutes old (ОБНОВИТЬ forces it).
        var rescan = DateTime.Now - _lastScan > TimeSpan.FromMinutes(3) ? ScanAsync() : Task.CompletedTask;
        // The GPU choice may have been changed in Windows Settings or undone in «Восстановление» meanwhile.
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), RefreshGpuAsync(), rescan);
    }

    public override void Deactivate()
    {
        _active = false;
        _ticker.Stop();
        IsEditorOpen = false;
    }

    // ───────────────────────────── Library ─────────────────────────────

    private async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var custom = (Settings.CustomGameFolders ?? []).ToList();
            await RunBusyAsync("Поиск игр…", async () =>
            {
                var scan = await Task.Run(() => GameLibrary.Scan(custom));
                _lastScan = DateTime.Now;
                ApplyScan(scan);
                await Task.Run(() => GameLibrary.SaveCache(scan));
            });
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void ApplyScan(LibraryScan scan)
    {
        _all = scan.Games.Select(g => new GameCardViewModel(g)).ToList();
        TotalCount = _all.Count;

        Sources.Clear();
        foreach (var s in scan.Sources.Where(s => s.Platform != GamePlatform.Custom)) Sources.Add(s);

        var selected = Platforms.FirstOrDefault(p => p.IsSelected)?.Platform;
        Platforms.Clear();
        var keep = selected is not null && _all.Any(c => c.Entry.Platform == selected);
        Platforms.Add(new PlatformFilterItem(null, TotalCount, OnPlatformSelected, !keep));
        foreach (var p in Enum.GetValues<GamePlatform>())
        {
            var count = _all.Count(c => c.Entry.Platform == p);
            if (count > 0) Platforms.Add(new PlatformFilterItem(p, count, OnPlatformSelected, keep && p == selected));
        }

        ScanText = $"ОБНОВЛЕНО {scan.ScannedAt:HH:mm}";
        var found = scan.Sources.Where(s => s.Installed && s.Platform != GamePlatform.Custom).Select(s => s.Name).ToList();
        var missing = scan.Sources.Where(s => !s.Installed && s.Platform != GamePlatform.Custom).Select(s => s.Name).ToList();
        SourcesFooter = (found.Count > 0 ? "Лаунчеры: " + string.Join(", ", found) + ". " : "") +
                        (missing.Count > 0 ? "Не найдены: " + string.Join(", ", missing) + "." : "");
        HasLibrary = true;
        ApplyFilter();
        _ = ApplyGpuStateAsync();
    }

    private void OnPlatformSelected(PlatformFilterItem item)
    {
        foreach (var p in Platforms)
            if (!ReferenceEquals(p, item) && p.IsSelected) p.IsSelected = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var platform = Platforms.FirstOrDefault(p => p.IsSelected)?.Platform;
        var q = SearchText?.Trim() ?? "";
        VisibleGames.Clear();
        foreach (var c in _all)
        {
            if (platform is not null && c.Entry.Platform != platform) continue;
            if (q.Length > 0 && !c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;
            VisibleGames.Add(c);
        }
        NotifyLibraryState();
    }

    private void NotifyLibraryState()
    {
        OnPropertyChanged(nameof(CountUnit));
        OnPropertyChanged(nameof(ShowFirstScan));
        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(IsFilterEmpty));
        OnPropertyChanged(nameof(HasVisibleGames));
        OnPropertyChanged(nameof(HasGames));
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await Task.WhenAll(ScanAsync(), RefreshReadinessAsync(), RefreshPowerNoteAsync());
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private async Task AddGame()
    {
        var dlg = new OpenFileDialog
        {
            Title = "MONOLITH — выберите .exe файл игры",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = true,
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;

        string exe;
        try { exe = Path.GetFullPath(dlg.FileName); }
        catch (Exception ex) { Ui.Toast(ex.Message, ToastKind.Error, "ДОБАВИТЬ ИГРУ"); return; }

        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Toast("Укажите исполняемый файл игры (.exe).", ToastKind.Warning, "ДОБАВИТЬ ИГРУ");
            return;
        }
        var windows = GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (GamePaths.IsUnder(exe, windows) || string.Equals(exe, AppPaths.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Toast("Системные программы Windows и сам MONOLITH нельзя добавить как игру.", ToastKind.Warning, "ДОБАВИТЬ ИГРУ");
            return;
        }
        var list = (Settings.CustomGameFolders ?? []).ToList();
        if (list.Contains(exe, StringComparer.OrdinalIgnoreCase) || _all.Any(c => string.Equals(c.Entry.ExePath, exe, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Toast("Эта игра уже есть в библиотеке.", ToastKind.Info, "ДОБАВИТЬ ИГРУ");
            return;
        }

        list.Add(exe);
        Settings.CustomGameFolders = list;
        Settings.Save();
        var entry = await Task.Run(() => GameLibrary.CustomEntry(exe));
        Log.Info($"Игра добавлена вручную: {entry.Name} ({exe})");
        Ui.Toast($"«{entry.Name}» добавлена в библиотеку.", ToastKind.Success);
        await ScanAsync();
    }

    [RelayCommand]
    private async Task RemoveGame(GameCardViewModel? card)
    {
        if (card is null || !card.IsCustom) return;
        var ok = await Ui.ConfirmAsync("УБРАТЬ ИЗ БИБЛИОТЕКИ?",
            $"«{card.Name}» исчезнет из списка MONOLITH. Файлы игры на диске не удаляются.",
            [card.PathText], "УБРАТЬ", "ОТМЕНА");
        if (!ok) return;
        Settings.CustomGameFolders = (Settings.CustomGameFolders ?? [])
            .Where(p => !string.Equals(p, card.Entry.ExePath, StringComparison.OrdinalIgnoreCase)).ToList();
        Settings.Save();
        Log.Info($"Игра убрана из библиотеки: {card.Name}");
        await ScanAsync();
    }

    [RelayCommand]
    private void OpenFolder(GameCardViewModel? card)
    {
        if (card is null) return;
        try { GameLauncher.OpenFolder(card.Entry); }
        catch (Exception ex) { Ui.Toast(ex.Message, ToastKind.Warning, "ПАПКА ИГРЫ"); }
    }

    [RelayCommand]
    private async Task Play(GameCardViewModel? card)
    {
        if (card is null) return;
        try
        {
            await Task.Run(() => GameLauncher.LaunchAsync(card.Entry));
            Ui.Toast($"Запуск «{card.Name}»…", ToastKind.Info);
        }
        catch (OperationCanceledException ex)
        {
            Ui.Toast(ex.Message, ToastKind.Warning, "ЗАПУСК ИГРЫ");
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось запустить «{card.Name}»", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ЗАПУСК ИГРЫ");
        }
    }

    // ───────────────────────────── Per-game GPU (hybrid PCs) ─────────────────────────────

    /// <summary>Reads whether this PC is hybrid and the user's per-app GPU choices (read-only, also without admin).</summary>
    private async Task RefreshGpuAsync()
    {
        try
        {
            var sp = await SystemProfile.LoadAsync();
            var hybrid = !sp.MissingFacts.Contains("gpu") && GpuPreference.IsHybrid(sp.Gpus, sp.OsBuild);
            ShowGpuToggle = hybrid;
            if (!hybrid) return;
            _dgpuName = sp.PrimaryGpu is { IsIntegrated: false } dgpu ? GamingReadiness.ShortGpuName(dgpu.Name) : null;
            var (values, writable) = await Task.Run(() => (GpuPreferenceStore.ReadAll(), GpuPreferenceStore.Location().Writable));
            _gpuValues = values;
            GpuWritable = writable;
            await ApplyGpuStateAsync();
        }
        catch (Exception ex)
        {
            Log.Warn($"Видеокарта для игр: состояние не прочитано — {ex.Message}");
        }
    }

    private async Task ApplyGpuStateAsync()
    {
        if (!ShowGpuToggle) return;
        var cards = _all;
        var values = _gpuValues;
        var (dgpu, elevated, writable) = (_dgpuName, IsElevated, GpuWritable);
        try
        {
            var states = await Task.Run(() => cards.Select(c =>
            {
                var v = GpuPreferenceStore.ValuesFor(c.Entry, values);
                return (Card: c, Values: v, Tip: GpuPreference.CardTooltip(v, dgpu, elevated, writable));
            }).ToList());
            foreach (var (card, v, tip) in states) card.SetGpu(v, tip);
        }
        catch (Exception ex)
        {
            Log.Warn($"Видеокарта для игр: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ToggleGpu(GameCardViewModel? card)
    {
        if (card is null) return;
        if (IsGpuChanging || !RequireAdmin("Высокопроизводительный GPU для игры"))
        {
            card.SyncGpuToggle();
            return;
        }
        if (!GpuWritable)
        {
            Ui.Toast("MONOLITH запущен от другой учётной записи администратора — выберите видеокарту в «Параметры → Система → Дисплей → Графика».",
                ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            card.SyncGpuToggle();
            return;
        }
        IsGpuChanging = true;
        try
        {
            // The switch already shows the clicked state; the card still holds the real one.
            if (card.IsGpuHigh) await DisableGpuAsync(card);
            else await EnableGpuAsync(card);
        }
        catch (Exception ex)
        {
            Log.Error($"Видеокарта для «{card.Name}»", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
        }
        finally
        {
            IsGpuChanging = false;
            await RefreshGpuAsync();
            card.SyncGpuToggle();
        }
    }

    private async Task EnableGpuAsync(GameCardViewModel card)
    {
        var game = card.Entry;
        var dir = GamePaths.NormalizeDir(game.InstallDir);

        // The preference only works for the exact exe that renders the game: never a launcher or a URL.
        var (exe, how) = (GpuPreference.KnownExe(game, dir), game.Platform == GamePlatform.Custom
            ? "файл игры, который вы добавили" : "основной файл игры из GOG Galaxy");
        if (exe is null)
        {
            var running = await Task.Run(() => GameProcessFinder.Find(game, null));
            if (running.Count > 0) (exe, how) = (running[0].Path, "файл игры, запущенной сейчас");
        }
        if (exe is null && card.GpuValues.Count > 0)
            (exe, how) = (card.GpuValues[0].Exe, "файл игры, для которого видеокарту уже выбирали в параметрах Windows");
        if (exe is null)
        {
            exe = await PickGameExeAsync(game, dir);
            how = "файл, который вы указали";
            if (exe is null) return;
        }
        if (!GpuPreference.IsValidExePath(exe))
        {
            Ui.Toast($"Путь «{exe}» не подходит для настройки видеокарты (нужен .exe на локальном диске).", ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return;
        }

        var current = _gpuValues.TryGetValue(exe, out var data) ? data : null;
        var record = ChangeJournal.Instance.FindActive(GpuPreference.JournalTarget(exe));
        var action = GpuPreference.Decide(true, GpuPreference.Parse(current), record?.Registry);
        if (action == GpuChange.None)
        {
            Ui.Toast($"Для «{game.Name}» уже выбрана высокая производительность.", ToastKind.Info, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return;
        }
        var after = action == GpuChange.Revert && record?.Registry is { } original
            ? (original.ValueExisted ? $"«{original.Data}» (значение до MONOLITH)" : "параметр удаляется (его не было до MONOLITH)")
            : $"«{GpuPreference.Merge(current, GpuPreference.HighPerformance)}»";
        var gpu = _dgpuName is null ? "дискретной видеокарте" : $"дискретной видеокарте ({_dgpuName})";
        var ok = await Ui.ConfirmAsync("ВЫСОКОПРОИЗВОДИТЕЛЬНЫЙ GPU ДЛЯ ИГРЫ?",
            $"Windows будет запускать «{game.Name}» на {gpu}, а не на встроенной. Это та же настройка, что «Высокая производительность» " +
            "в «Параметры → Система → Дисплей → Графика»; другие игры и общие настройки не меняются. Подействует со следующего запуска игры. " +
            "Риск: от батареи ноутбук разрядится быстрее, пока игра запущена.",
            [
                $"Файл: {exe} ({how})",
                $@"Реестр вашей учётной записи: HKCU\{GpuPreference.KeyPath}",
                $"Сейчас: {GpuPreference.ShowValue(current)} — {GpuPreference.Describe(GpuPreference.Parse(current))}",
                $"Станет: {after}",
            ],
            "ВКЛЮЧИТЬ",
            footnote: "Прежнее значение записывается в журнал. Выключите переключатель или откатите строку в «Восстановлении» — " +
                      "вернётся ровно прежняя строка, а если параметра не было, он будет удалён.");
        if (!ok) return;

        var outcome = await Task.Run(() => GpuPreferenceStore.SetAsync([exe], true, game.Name));
        ReportGpu(outcome, $"«{game.Name}» будет запускаться на {gpu} — со следующего запуска игры.");
    }

    private async Task DisableGpuAsync(GameCardViewModel card)
    {
        var game = card.Entry;
        var high = card.GpuValues.Where(v => v.IsHigh).ToList();
        if (high.Count == 0) return;

        // Undoing MONOLITH's own change needs no question (it is the undo). A choice made in Windows Settings is
        // changed only after the user saw exactly what happens — and is journaled like any other change.
        var foreign = high.Where(v => GpuPreference.Decide(false, v.Preference,
            ChangeJournal.Instance.FindActive(GpuPreference.JournalTarget(v.Exe))?.Registry) == GpuChange.Write).ToList();
        if (foreign.Count > 0)
        {
            var details = foreign.SelectMany(v => new[]
            {
                $"Файл: {v.Exe}",
                $"Сейчас: «{v.Data}» → станет: «{GpuPreference.Merge(v.Data, GpuPreference.Auto)}»",
            }).Prepend($@"Реестр вашей учётной записи: HKCU\{GpuPreference.KeyPath}");
            var ok = await Ui.ConfirmAsync("ВЕРНУТЬ ВЫБОР ВИДЕОКАРТЫ WINDOWS?",
                $"Высокую производительность для «{game.Name}» задал не MONOLITH — скорее всего, это выбор в параметрах графики Windows. " +
                "MONOLITH поставит «Пусть решает Windows», как в «Параметры → Система → Дисплей → Графика».",
                details, "ВЫКЛЮЧИТЬ",
                footnote: "Это изменение тоже записывается в журнал и откатывается в «Восстановлении».");
            if (!ok) return;
        }

        var outcome = await Task.Run(() => GpuPreferenceStore.SetAsync(high.Select(v => v.Exe).ToList(), false, game.Name));
        ReportGpu(outcome, $"«{game.Name}»: выбор видеокарты возвращён — со следующего запуска игры.");
    }

    private static void ReportGpu(GpuChangeOutcome outcome, string success)
    {
        if (outcome.Success) Ui.Toast(success, ToastKind.Success, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
        else Ui.Toast("Не удалось: " + string.Join("; ", outcome.Errors), ToastKind.Error, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
    }

    /// <summary>
    /// Launchers start Steam/Epic/Ubisoft games by URL, so the game's exe is unknown until it runs: explain that and
    /// let the user pick the exe inside the game folder (with the most likely file pre-selected).
    /// </summary>
    private async Task<string?> PickGameExeAsync(GameEntry game, string dir)
    {
        if (!GamePaths.IsSpecificDir(dir) || !await Task.Run(() => Directory.Exists(dir)))
        {
            Ui.Toast($"Папка «{game.Name}» не найдена. Запустите игру и включите переключатель снова — MONOLITH возьмёт файл работающей игры.",
                ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        var ask = await Ui.ConfirmAsync("КАКОЙ ФАЙЛ ЗАПУСКАЕТ ИГРУ?",
            $"Настройка Windows действует только для файла, который рисует картинку, а «{game.Name}» запускается через {GameText.SourceName(game.Platform)} — " +
            "точный .exe MONOLITH не знает. Проще всего: запустите игру и включите переключатель снова, MONOLITH возьмёт файл работающей игры. " +
            "Или укажите .exe в папке игры сами.",
            [dir], "УКАЗАТЬ .EXE", "ОТМЕНА");
        if (!ask) return null;

        var guess = await Task.Run(() => GpuPreferenceStore.GuessExe(dir));
        var dlg = new OpenFileDialog
        {
            Title = $"MONOLITH — .exe игры «{game.Name}»",
            Filter = "Исполняемые файлы (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = true,
            InitialDirectory = guess is null ? dir : Path.GetDirectoryName(guess) ?? dir,
            FileName = guess is null ? "" : Path.GetFileName(guess),
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return null;

        string exe;
        try { exe = Path.GetFullPath(dlg.FileName); }
        catch (Exception ex) { Ui.Toast(ex.Message, ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ"); return null; }
        if (!GamePaths.IsUnder(exe, dir) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Ui.Toast($"Выберите .exe внутри папки игры: {dir}", ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        if (GameProcessFinder.IsHelper(Path.GetFileNameWithoutExtension(exe)))
        {
            Ui.Toast($"{Path.GetFileName(exe)} — вспомогательная программа (отчёты об ошибках, античит), а не сама игра.", ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        return exe;
    }

    // ───────────────────────────── Game Boost ─────────────────────────────

    [RelayCommand]
    private Task PlayBoosted(GameCardViewModel? card) => card is null ? Task.CompletedTask : StartBoostFlowAsync(card.Entry);

    [RelayCommand]
    private Task StartBoost() => StartBoostFlowAsync(null);

    [RelayCommand]
    private async Task StopBoost()
    {
        if (!Boost.IsActive) return;
        await Boost.StopAsync("Игровой сеанс остановлен вручную, изменения отменены.");
    }

    private async Task StartBoostFlowAsync(GameEntry? game)
    {
        if (!RequireAdmin("Игровой сеанс")) return;
        if (Boost.IsActive || Boost.IsTransitioning)
        {
            Ui.Toast("Игровой сеанс уже идёт. Сначала остановите его кнопкой «СТОП».", ToastKind.Warning, "ИГРОВОЙ СЕАНС");
            return;
        }
        if (game is { IsAvailable: false })
        {
            Ui.Toast($"Файл игры не найден: {game.ExePath}", ToastKind.Warning, "ИГРОВОЙ СЕАНС");
            return;
        }

        BoostPlan plan;
        try
        {
            plan = await GameBoostService.BuildPlanAsync(game);
        }
        catch (Exception ex)
        {
            Log.Error("Игровой сеанс: не удалось подготовить сеанс", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ИГРОВОЙ СЕАНС");
            return;
        }

        var ok = await Ui.ConfirmAsync(
            game is null ? "НАЧАТЬ ИГРОВОЙ СЕАНС?" : "ИГРАТЬ С УСКОРЕНИЕМ?",
            game is null
                ? "На время сеанса MONOLITH сделает следующее:"
                : $"MONOLITH запустит «{game.Name}» и на время игры сделает следующее:",
            plan.Describe(game),
            game is null ? "ВКЛЮЧИТЬ" : "ЗАПУСТИТЬ",
            footnote: "Смена плана питания записывается в журнал и отменяется автоматически.");
        if (!ok) return;

        await Boost.StartAsync(game, plan);
        CheckStale();
    }

    private void OnBoostChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(GameBoostService.IsActive):
                if (Boost.IsActive && _active) StartTicker();
                if (!Boost.IsActive) _ticker.Stop();
                UpdateElapsed();
                break;
            case nameof(GameBoostService.StartedAt):
                UpdateElapsed();
                break;
        }
        OnPropertyChanged(nameof(CanStartBoost));
        OnPropertyChanged(nameof(CanLaunchBoosted));
        OnPropertyChanged(nameof(IsBoostIdle));
        OnPropertyChanged(nameof(BoostStatus));
        OnPropertyChanged(nameof(BoostTooltip));
    }

    private void StartTicker()
    {
        UpdateElapsed();
        _ticker.Start();
    }

    private void UpdateElapsed()
    {
        var span = Boost.IsActive ? DateTime.Now - Boost.StartedAt : TimeSpan.Zero;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        ElapsedText = $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.GameBoostPowerPlan):
                _ = RefreshPowerNoteAsync();
                break;
            case nameof(AppSettings.GameBoostCloseList):
                UpdateCloseList();
                break;
        }
    }

    private async Task RefreshPowerNoteAsync()
    {
        try
        {
            var sp = await SystemProfile.LoadAsync();
            var enabled = Settings.GameBoostPowerPlan;
            var (_, text) = await Task.Run(() => GameBoostService.DecidePower(sp, enabled));
            const string skip = "План питания не меняется";
            const string sw = "План питания: ";
            PowerNote = text.StartsWith(sw, StringComparison.Ordinal) ? text[sw.Length..]
                : text.StartsWith(skip, StringComparison.Ordinal) ? "Не меняется" + text[skip.Length..]
                : text;
        }
        catch (Exception ex)
        {
            Log.Warn($"Игровой сеанс: не удалось определить план питания — {ex.Message}");
            PowerNote = "Не удалось определить текущий план питания.";
        }
    }

    private void UpdateCloseList()
    {
        var names = (Settings.GameBoostCloseList ?? []).Select(UserProcesses.NormalizeName).Where(n => n.Length > 0).ToList();
        CloseListCount = names.Count;
        CloseListSummary = names.Count == 0
            ? "Список пуст — перед игрой ничего не закрывается."
            : string.Join(", ", names.Take(6)) + (names.Count > 6 ? $" и ещё {names.Count - 6}" : "");
    }

    // ───────────────────────────── Crash recovery ─────────────────────────────

    private void CheckStale()
    {
        if (Boost.IsActive)
        {
            HasStaleBoost = false;
            return;
        }
        var records = ChangeJournal.Instance.ActiveRecords.Where(r => r.Source == GameBoostService.Source).ToList();
        HasStaleBoost = records.Count > 0;
        StaleText = records.Count == 0 ? ""
            : "Прошлый игровой сеанс не завершился (MONOLITH был закрыт аварийно). Всё ещё действует: " +
              string.Join("; ", records.Select(r => $"{r.Title.Replace("Игровой сеанс: ", "").Replace("Игровой режим: ", "")} — «{r.NewValue}» вместо «{r.OldValue}»")) + ".";
    }

    private async Task OfferStaleRestoreAsync()
    {
        if (!HasStaleBoost || _staleOffered || !IsElevated) return;
        _staleOffered = true;
        var details = ChangeJournal.Instance.ActiveRecords.Where(r => r.Source == GameBoostService.Source)
            .Select(r => $"{r.Title}: сейчас «{r.NewValue}», было «{r.OldValue}»").ToList();
        var ok = await Ui.ConfirmAsync("ВЕРНУТЬ НАСТРОЙКИ?",
            "Прошлый игровой сеанс не завершился — MONOLITH был закрыт аварийно. Эти временные изменения всё ещё действуют. Вернуть исходные значения?",
            details, "ВЕРНУТЬ", "ПОЗЖЕ",
            footnote: "Это можно сделать и позже: здесь или в «Восстановлении».");
        if (ok) await RestoreStale();
    }

    [RelayCommand]
    private async Task RestoreStale()
    {
        if (!RequireAdmin("Вернуть настройки игрового сеанса")) return;
        await RunBusyAsync("Откат игрового сеанса…", async () =>
        {
            var r = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(GameBoostService.Source));
            if (r.Success) Ui.Toast($"Исходные настройки возвращены (изменений: {r.Reverted}).", ToastKind.Success, "ИГРОВОЙ СЕАНС");
            else Ui.Toast($"Не удалось вернуть: {string.Join("; ", r.Errors)}", ToastKind.Error, "ИГРОВОЙ СЕАНС");
        });
        CheckStale();
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync());
    }

    // ───────────────────────────── Readiness ─────────────────────────────

    private async Task RefreshReadinessAsync()
    {
        try
        {
            var sp = await SystemProfile.LoadAsync();
            var checks = await Task.Run(() => GamingReadiness.Collect(sp));
            var (passed, total) = GamingReadiness.Score(checks);
            ReadinessText = GamingReadiness.SummaryLine(passed, total);
            ReadinessLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Готовность к играм: {ex.Message}");
        }
    }

    [RelayCommand]
    private void GoDashboard() => Ui.Navigate("dashboard");

    // ───────────────────────────── Close-list editor ─────────────────────────────

    [RelayCommand]
    private async Task OpenEditor()
    {
        EditorList.Clear();
        foreach (var n in (Settings.GameBoostCloseList ?? []).Select(UserProcesses.NormalizeName).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            EditorList.Add(n);
        OnPropertyChanged(nameof(EditorListEmpty));
        NewProcessName = "";
        IsEditorOpen = true;
        await LoadRunningAsync();
    }

    [RelayCommand]
    private Task RefreshRunning() => LoadRunningAsync();

    private async Task LoadRunningAsync()
    {
        if (IsLoadingRunning) return;
        IsLoadingRunning = true;
        try
        {
            var apps = await Task.Run(UserProcesses.ListClosable);
            RunningApps.Clear();
            foreach (var a in apps)
                RunningApps.Add(new RunningAppItem(a, EditorList.Contains(a.Name, StringComparer.OrdinalIgnoreCase), OnRunningToggled));
        }
        catch (Exception ex)
        {
            Log.Warn($"Список запущенных приложений: {ex.Message}");
            Ui.Toast(ex.Message, ToastKind.Error, "ЗАПУЩЕННЫЕ ПРИЛОЖЕНИЯ");
        }
        finally
        {
            IsLoadingRunning = false;
        }
    }

    /// <summary>A running-app checkbox changed: keep the list in sync (idempotent both ways).</summary>
    private void OnRunningToggled(RunningAppItem item, bool inList)
    {
        var present = EditorList.Contains(item.Name, StringComparer.OrdinalIgnoreCase);
        if (inList && !present)
        {
            if (!AddName(item.Name)) item.IsInList = false;
        }
        else if (!inList && present)
        {
            RemoveFromList(item.Name);
        }
    }

    [RelayCommand]
    private void AddManual()
    {
        var name = UserProcesses.NormalizeName(NewProcessName);
        if (name.Length == 0) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Ui.Toast("Имя процесса содержит недопустимые символы.", ToastKind.Warning, "СПИСОК ЗАКРЫТИЯ");
            return;
        }
        if (AddName(name)) NewProcessName = "";
    }

    private bool AddName(string name)
    {
        if (UserProcesses.IsProtectedName(name))
        {
            Ui.Toast($"«{name}» — системный процесс, драйвер, античит или лаунчер. Его закрывать нельзя.", ToastKind.Warning, "СПИСОК ЗАКРЫТИЯ");
            return false;
        }
        if (!EditorList.Contains(name, StringComparer.OrdinalIgnoreCase)) EditorList.Add(name);
        foreach (var r in RunningApps.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) r.IsInList = true;
        OnPropertyChanged(nameof(EditorListEmpty));
        return true;
    }

    [RelayCommand]
    private void RemoveFromList(string? name)
    {
        if (name is null) return;
        var existing = EditorList.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) EditorList.Remove(existing);
        foreach (var r in RunningApps.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) r.IsInList = false;
        OnPropertyChanged(nameof(EditorListEmpty));
    }

    [RelayCommand]
    private void SaveEditor()
    {
        Settings.GameBoostCloseList = EditorList.ToList();
        Settings.Save();
        UpdateCloseList();
        IsEditorOpen = false;
        Log.Info(EditorList.Count == 0
            ? "Игровой сеанс: список закрываемых приложений очищен."
            : "Игровой сеанс: перед игрой закрывать " + string.Join(", ", EditorList));
        Ui.Toast(EditorList.Count == 0 ? "Список закрытия пуст." : $"Список закрытия сохранён: {EditorList.Count}.", ToastKind.Success);
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;
}
