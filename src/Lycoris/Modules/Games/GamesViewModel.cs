using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Shell;
using Lycoris.UI;

namespace Lycoris.Modules.Games;

public sealed partial class GamesViewModel : PageViewModel
{
    private readonly DispatcherTimer _ticker;
    private List<GameCardViewModel> _all = [];
    private bool _staleOffered;
    private bool _active;
    private DateTime _lastScan = DateTime.MinValue;

    public override string Key => "games";
    public override string Index => "03";
    public override string Title => Loc.Key("ИГРЫ");
    public override string Subtitle => Loc.Key("Библиотека игр и игровой сеанс: максимум ресурсов игре, пока она запущена.");
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

    public string CountUnit => Loc.Tf("{0} В БИБЛИОТЕКЕ", Loc.Plural(TotalCount, "ИГРА", "ИГРЫ", "ИГР"));
    public bool ShowFirstScan => IsScanning && !HasLibrary;
    public bool IsLibraryEmpty => HasLibrary && TotalCount == 0;
    public bool IsFilterEmpty => TotalCount > 0 && VisibleGames.Count == 0;
    public bool HasVisibleGames => VisibleGames.Count > 0;
    public bool HasGames => TotalCount > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(ShowFirstScan));
    partial void OnHasLibraryChanged(bool value) => NotifyLibraryState();
    partial void OnTotalCountChanged(int value) => NotifyLibraryState();
    partial void OnShowCloseModeChoiceChanged(bool value) => UpdateCloseList();

    // ───────────────────────────── Boost state ─────────────────────────────

    [ObservableProperty] private string _elapsedText = "00:00:00";
    [ObservableProperty] private string _powerNote = "";
    [ObservableProperty] private int _closeListCount;
    [ObservableProperty] private string _closeListSummary = "";
    /// <summary>The ЗАКРЫТЬ | РЕЖИМ ЭФФЕКТИВНОСТИ choice exists on Windows 11 only (EcoQoS; before it the same call meant LowQoS).</summary>
    [ObservableProperty] private bool _showCloseModeChoice;

    /// <summary>Close-list mode segments. A RadioButton group unchecks the other one by itself: only «true» is acted on.</summary>
    public bool CloseModeIsClose
    {
        get => !CloseModeIsEcoQos;
        set { if (value) SetCloseMode(AppSettings.CloseModeClose); }
    }

    public bool CloseModeIsEcoQos
    {
        get => Settings.GameBoostUsesEcoQos;
        set { if (value) SetCloseMode(AppSettings.CloseModeEcoQos); }
    }

    private void SetCloseMode(string mode)
    {
        if (string.Equals(Settings.GameBoostCloseMode, mode, StringComparison.OrdinalIgnoreCase)) return;
        Settings.GameBoostCloseMode = mode;
        Log.Info(mode == AppSettings.CloseModeEcoQos
            ? Loc.T("Игровой сеанс: программы из списка переводятся в режим эффективности, а не закрываются.")
            : Loc.T("Игровой сеанс: программы из списка закрываются."));
    }

    /// <summary>Shown under the segments while efficiency mode is chosen (the full explanation also sits in the segment's tooltip).</summary>
    public static string CloseModeEcoQosCaption =>
        Loc.T("Режим эффективности: программа продолжает работать, но Windows переводит её на экономные частоты и E-ядра (то же, что «Режим " +
        "эффективности» в Диспетчере задач, без снижения приоритета). Заметнее на процессорах с P/E-ядрами и ноутбуках; на остальных эффект " +
        "небольшой. Программы, которые на старте сеанса играют звук, а также голос, стрим, оверлеи и периферия не трогаются. Сбрасывается после игры.");
    [ObservableProperty] private bool _hasStaleBoost;
    [ObservableProperty] private string _staleText = "";
    [ObservableProperty] private string _staleActionText = Loc.T("ВЕРНУТЬ НАСТРОЙКИ");

    // Without admin rights the buttons stay enabled: a click asks for them (RequireAdmin) and repeats itself after the relaunch.
    public bool CanStartBoost => !Boost.IsActive && !Boost.IsTransitioning;
    public bool CanLaunchBoosted => !Boost.IsActive && !Boost.IsTransitioning;
    /// <summary>Why «СТАРТ» will ask for something first (text next to the button, never only a tooltip); empty when nothing is needed.</summary>
    public string StartNote => Boost.IsActive ? ""
        : !IsElevated ? Loc.T("Игровой сеанс меняет схему питания, поэтому нужны права администратора: СТАРТ предложит перезапуск с правами и продолжит сам.")
        : "";
    public bool HasStartNote => StartNote.Length > 0;
    /// <summary>Why the «С УСКОРЕНИЕМ» buttons are grey while a session is running.</summary>
    public string BoostActiveNote => Boost.IsActive ? Loc.T("Игровой сеанс уже идёт — «С УСКОРЕНИЕМ» снова заработает после остановки.") : "";
    public bool HasBoostActiveNote => Boost.IsActive;
    public bool IsBoostIdle => !Boost.IsActive;
    public string BoostStatus => Boost.IsTransitioning && !Boost.IsActive ? Loc.T("ЗАПУСК")
        : Boost.IsActive ? (string.IsNullOrEmpty(Boost.PhaseText) ? Loc.T("АКТИВЕН") : Boost.PhaseText)
        : Loc.T("ВЫКЛЮЧЕН");

    // ───────────────────────────── Readiness state ─────────────────────────────

    [ObservableProperty] private string _readinessText = "";
    [ObservableProperty] private bool _readinessLoaded;

    // ───────────────────────────── Per-game GPU state (hybrid PCs) ─────────────────────────────

    /// <summary>Two or more real GPUs (iGPU + dGPU) on Windows 10 1803+: only then the per-game switch is shown.</summary>
    [ObservableProperty] private bool _showGpuToggle;
    /// <summary>False when Lycoris runs as another admin account (HKCU is not the player's): state is shown read-only.</summary>
    [ObservableProperty] private bool _gpuWritable;
    [ObservableProperty] private bool _isGpuChanging;
    private string? _dgpuName;
    private IReadOnlyDictionary<string, string> _gpuValues = new Dictionary<string, string>();

    public bool CanChangeGpu => GpuWritable && !IsGpuChanging;
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
        await RecoverStaleEcoQosAsync(); // before the slow scan and dialogs, so a quick ИГРАТЬ cannot overwrite the marker
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), RefreshGpuAsync(), ScanAsync(), RefreshCloseModeAsync(), RefreshPriorityRulesAsync());
        MarkChecked();
        await OfferStaleRestoreAsync();
    }

    private async Task RefreshCloseModeAsync()
    {
        try
        {
            var sp = await SystemProfile.LoadAsync();
            ShowCloseModeChoice = sp.IsWindows11;
        }
        catch (Exception ex)
        {
            Log.Warn("Игровой сеанс: версия Windows не определена — {0}", ex.Message);
        }
    }

    private bool _ecoRecovered;

    /// <summary>
    /// Lycoris died during a session with efficiency mode on: the marker in the admin-only folder names the tagged processes;
    /// those still running (same PID and start time) go back to the system-managed state. Once per run, elevated only.
    /// </summary>
    private async Task RecoverStaleEcoQosAsync()
    {
        if (_ecoRecovered || !IsElevated || Boost.IsActive) return;
        _ecoRecovered = true;
        try
        {
            var text = await Task.Run(GameBoostService.RecoverStaleEcoQos);
            if (text is not null) Ui.Toast(text, ToastKind.Warning, "ИГРОВОЙ СЕАНС");
        }
        catch (Exception ex)
        {
            Log.Warn("Игровой сеанс: не удалось снять режим эффективности прошлого сеанса — {0}", ex.Message);
        }
    }

    protected override async Task OnReturnAsync()
    {
        _active = true;
        if (Boost.IsActive) StartTicker();
        CheckStale();
        // Library changes rarely: rescan on return only when the last scan is a few minutes old (ОБНОВИТЬ forces it).
        var rescan = DateTime.Now - _lastScan > TimeSpan.FromMinutes(3) ? ScanAsync() : Task.CompletedTask;
        // The GPU choice may have been changed in Windows Settings or undone in «Восстановление» meanwhile.
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), RefreshGpuAsync(), rescan, RefreshPriorityRulesAsync());
        MarkChecked();
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
            await RunBusyAsync(OperationClass.Measure, Loc.Key("Поиск игр…"), async () =>
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
        BadgeText = TotalCount > 0 ? TotalCount.ToString() : null; // the sidebar badge = games in the library

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

        ScanText = Loc.Tf("ОБНОВЛЕНО {0}", Loc.TimeText(scan.ScannedAt));
        var found = scan.Sources.Where(s => s.Installed && s.Platform != GamePlatform.Custom).Select(s => s.Name).ToList();
        var missing = scan.Sources.Where(s => !s.Installed && s.Platform != GamePlatform.Custom).Select(s => s.Name).ToList();
        SourcesFooter = (found.Count > 0 ? Loc.Tf("Лаунчеры: {0}. ", string.Join(", ", found)) : "") +
                        (missing.Count > 0 ? Loc.Tf("Не найдены: {0}.", string.Join(", ", missing)) : "");
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
        await Task.WhenAll(ScanAsync(), RefreshReadinessAsync(), RefreshPowerNoteAsync(), RefreshPriorityRulesAsync());
        MarkChecked();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private async Task AddGame()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Lycoris — выберите .exe файл игры"),
            Filter = Loc.T("Исполняемые файлы (*.exe)|*.exe"),
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
            Ui.Toast("Системные программы Windows и сам Lycoris нельзя добавить как игру.", ToastKind.Warning, "ДОБАВИТЬ ИГРУ");
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
        Log.Info("Игра добавлена вручную: {0} ({1})", entry.Name, exe);
        Ui.Toast(Loc.Tf("«{0}» добавлена в библиотеку.", entry.Name), ToastKind.Success);
        await ScanAsync();
    }

    [RelayCommand]
    private async Task RemoveGame(GameCardViewModel? card)
    {
        if (card is null || !card.IsCustom) return;
        var ok = await Ui.ConfirmAsync("УБРАТЬ ИЗ БИБЛИОТЕКИ?",
            Loc.Tf("«{0}» исчезнет из списка Lycoris. Файлы игры на диске не удаляются.", card.Name),
            [card.PathText], "УБРАТЬ", "ОТМЕНА");
        if (!ok) return;
        Settings.CustomGameFolders = (Settings.CustomGameFolders ?? [])
            .Where(p => !string.Equals(p, card.Entry.ExePath, StringComparison.OrdinalIgnoreCase)).ToList();
        Settings.Save();
        Log.Info("Игра убрана из библиотеки: {0}", card.Name);
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
            Ui.Toast(Loc.Tf("Запуск «{0}»…", card.Name), ToastKind.Info);
        }
        catch (OperationCanceledException ex)
        {
            Ui.Toast(ex.Message, ToastKind.Warning, "ЗАПУСК ИГРЫ");
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось запустить «{0}»", ex, card.Name);
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
            Log.Warn("Видеокарта для игр: состояние не прочитано — {0}", ex.Message);
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
            Log.Warn("Видеокарта для игр: {0}", ex.Message);
        }
    }

    /// <summary>Replay target of the GPU switch after a relaunch with admin rights (parameter = the game's id).</summary>
    [RelayCommand]
    private Task ToggleGpuGame(string? id) => CardById(id) is { } card ? ToggleGpu(card) : Task.CompletedTask;

    private GameCardViewModel? CardById(string? id) => id is null ? null : _all.FirstOrDefault(c => c.Entry.Id == id);

    [RelayCommand]
    private async Task ToggleGpu(GameCardViewModel? card)
    {
        if (card is null) return;
        if (IsGpuChanging || !RequireAdmin(Loc.Key("Высокопроизводительный GPU для игры"), "ToggleGpuGame", card.Entry.Id))
        {
            card.SyncGpuToggle();
            return;
        }
        if (!GpuWritable)
        {
            Ui.Toast("Lycoris запущен от другой учётной записи администратора — выберите видеокарту в «Параметры → Система → Дисплей → Графика».",
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
            Log.Error("Видеокарта для «{0}»", ex, card.Name);
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
            ? Loc.T("файл игры, который вы добавили") : Loc.T("основной файл игры из GOG Galaxy"));
        if (exe is null)
        {
            var running = await Task.Run(() => GameProcessFinder.Find(game, null));
            if (running.Count > 0) (exe, how) = (running[0].Path, Loc.T("файл игры, запущенной сейчас"));
        }
        if (exe is null && card.GpuValues.Count > 0)
            (exe, how) = (card.GpuValues[0].Exe, Loc.T("файл игры, для которого видеокарту уже выбирали в параметрах Windows"));
        if (exe is null)
        {
            exe = await PickGameExeAsync(game, dir);
            how = Loc.T("файл, который вы указали");
            if (exe is null) return;
        }
        if (!GpuPreference.IsValidExePath(exe))
        {
            Ui.Toast(Loc.Tf("Путь «{0}» не подходит для настройки видеокарты (нужен .exe на локальном диске).", exe), ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return;
        }

        var current = _gpuValues.TryGetValue(exe, out var data) ? data : null;
        var record = ChangeJournal.Instance.FindActive(GpuPreference.JournalTarget(exe));
        var action = GpuPreference.Decide(true, GpuPreference.Parse(current), record?.Registry);
        if (action == GpuChange.None)
        {
            Ui.Toast(Loc.Tf("Для «{0}» уже выбрана высокая производительность.", game.Name), ToastKind.Info, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return;
        }
        var after = action == GpuChange.Revert && record?.Registry is { } original
            ? (original.ValueExisted ? Loc.Tf("«{0}» (значение до Lycoris)", original.Data) : Loc.T("параметр удаляется (его не было до Lycoris)"))
            : $"«{GpuPreference.Merge(current, GpuPreference.HighPerformance)}»";
        var gpu = GpuPreference.DiscreteCardPhrase(_dgpuName);
        var ok = await Ui.ConfirmAsync("ВЫСОКОПРОИЗВОДИТЕЛЬНЫЙ GPU ДЛЯ ИГРЫ?",
            Loc.Tf("Windows будет запускать «{0}» на {1}, а не на встроенной. Это та же настройка, что «Высокая производительность» " +
            "в «Параметры → Система → Дисплей → Графика»; другие игры и общие настройки не меняются. Подействует со следующего запуска игры. " +
            "Риск: от батареи ноутбук разрядится быстрее, пока игра запущена.", game.Name, gpu),
            [
                Loc.Tf("Файл: {0} ({1})", exe, how),
                Loc.Tf(@"Реестр вашей учётной записи: HKCU\{0}", GpuPreference.KeyPath),
                Loc.Tf("Сейчас: {0} — {1}", GpuPreference.ShowValue(current), GpuPreference.Describe(GpuPreference.Parse(current))),
                Loc.Tf("Станет: {0}", after),
            ],
            "ВКЛЮЧИТЬ",
            footnote: "Прежнее значение записывается в журнал. Выключите переключатель или откатите строку в «Восстановлении» — " +
                      "вернётся ровно прежняя строка, а если параметра не было, он будет удалён.");
        if (!ok) return;

        var outcome = await Task.Run(() => GpuPreferenceStore.SetAsync([exe], true, game.Name));
        ReportGpu(outcome, Loc.Tf("«{0}» будет запускаться на {1} — со следующего запуска игры.", game.Name, gpu));
    }

    private async Task DisableGpuAsync(GameCardViewModel card)
    {
        var game = card.Entry;
        var high = card.GpuValues.Where(v => v.IsHigh).ToList();
        if (high.Count == 0) return;

        // Undoing Lycoris's own change needs no question (it is the undo). A choice made in Windows Settings is
        // changed only after the user saw exactly what happens — and is journaled like any other change.
        var foreign = high.Where(v => GpuPreference.Decide(false, v.Preference,
            ChangeJournal.Instance.FindActive(GpuPreference.JournalTarget(v.Exe))?.Registry) == GpuChange.Write).ToList();
        if (foreign.Count > 0)
        {
            var details = foreign.SelectMany(v => new[]
            {
                Loc.Tf("Файл: {0}", v.Exe),
                Loc.Tf("Сейчас: «{0}» → станет: «{1}»", v.Data, GpuPreference.Merge(v.Data, GpuPreference.Auto)),
            }).Prepend(Loc.Tf(@"Реестр вашей учётной записи: HKCU\{0}", GpuPreference.KeyPath));
            var ok = await Ui.ConfirmAsync("ВЕРНУТЬ ВЫБОР ВИДЕОКАРТЫ WINDOWS?",
                Loc.Tf("Высокую производительность для «{0}» задал не Lycoris — скорее всего, это выбор в параметрах графики Windows. " +
                "Lycoris поставит «Пусть решает Windows», как в «Параметры → Система → Дисплей → Графика».", game.Name),
                details, "ВЫКЛЮЧИТЬ",
                footnote: "Это изменение тоже записывается в журнал и откатывается в «Восстановлении».");
            if (!ok) return;
        }

        var outcome = await Task.Run(() => GpuPreferenceStore.SetAsync(high.Select(v => v.Exe).ToList(), false, game.Name));
        ReportGpu(outcome, Loc.Tf("«{0}»: выбор видеокарты возвращён — со следующего запуска игры.", game.Name));
    }

    private static void ReportGpu(GpuChangeOutcome outcome, string success)
    {
        if (outcome.Success) Ui.Toast(success, ToastKind.Success, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
        else Ui.Toast(Loc.Tf("Не удалось: {0}", string.Join("; ", outcome.Errors)), ToastKind.Error, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
    }

    /// <summary>
    /// Launchers start Steam/Epic/Ubisoft games by URL, so the game's exe is unknown until it runs: explain that and
    /// let the user pick the exe inside the game folder (with the most likely file pre-selected).
    /// </summary>
    private async Task<string?> PickGameExeAsync(GameEntry game, string dir)
    {
        if (!GamePaths.IsSpecificDir(dir) || !await Task.Run(() => Directory.Exists(dir)))
        {
            Ui.Toast(Loc.Tf("Папка «{0}» не найдена. Запустите игру и включите переключатель снова — Lycoris возьмёт файл работающей игры.", game.Name),
                ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        var ask = await Ui.ConfirmAsync("КАКОЙ ФАЙЛ ЗАПУСКАЕТ ИГРУ?",
            Loc.Tf("Настройка Windows действует только для файла, который рисует картинку, а «{0}» запускается через {1} — " +
            "точный .exe Lycoris не знает. Проще всего: запустите игру и включите переключатель снова, Lycoris возьмёт файл работающей игры. " +
            "Или укажите .exe в папке игры сами.", game.Name, GameText.SourceName(game.Platform)),
            [dir], "УКАЗАТЬ .EXE", "ОТМЕНА");
        if (!ask) return null;

        var guess = await Task.Run(() => GpuPreferenceStore.GuessExe(dir));
        var dlg = new OpenFileDialog
        {
            Title = Loc.Tf("Lycoris — .exe игры «{0}»", game.Name),
            Filter = Loc.T("Исполняемые файлы (*.exe)|*.exe"),
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
            Ui.Toast(Loc.Tf("Выберите .exe внутри папки игры: {0}", dir), ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        if (GameProcessFinder.IsHelper(Path.GetFileNameWithoutExtension(exe)))
        {
            Ui.Toast(Loc.Tf("{0} — вспомогательная программа (отчёты об ошибках, античит), а не сама игра.", Path.GetFileName(exe)), ToastKind.Warning, "ВИДЕОКАРТА ДЛЯ ИГРЫ");
            return null;
        }
        return exe;
    }

    // ───────────────────────────── Game Boost ─────────────────────────────

    [RelayCommand]
    private Task PlayBoosted(GameCardViewModel? card) => card is null ? Task.CompletedTask : StartBoostFlowAsync(card.Entry);

    /// <summary>Replay target of «С УСКОРЕНИЕМ» after a relaunch with admin rights (parameter = the game's id).</summary>
    [RelayCommand]
    private Task PlayBoostedGame(string? id) => CardById(id) is { } card ? StartBoostFlowAsync(card.Entry) : Task.CompletedTask;

    /// <summary>Starts a game session without a game (the Dashboard tile «ИГРОВОЙ СЕАНС · СТАРТ» calls this after <c>EnsureLoadedAsync</c>).</summary>
    [RelayCommand]
    private Task StartBoost() => StartBoostFlowAsync(null);

    [RelayCommand]
    private async Task StopBoost()
    {
        if (!Boost.IsActive) return;
        await Boost.StopAsync(Loc.T("Игровой сеанс остановлен вручную, изменения отменены."));
    }

    private async Task StartBoostFlowAsync(GameEntry? game)
    {
        if (!(game is null ? RequireAdmin(Loc.Key("Игровой сеанс"), "StartBoost") : RequireAdmin(Loc.Key("Игровой сеанс"), "PlayBoostedGame", game.Id))) return;
        if (Boost.IsActive || Boost.IsTransitioning)
        {
            Ui.Toast("Игровой сеанс уже идёт. Сначала остановите его кнопкой «СТОП».", ToastKind.Warning, "ИГРОВОЙ СЕАНС");
            return;
        }
        if (game is { IsAvailable: false })
        {
            Ui.Toast(Loc.Tf("Файл игры не найден: {0}", game.ExePath), ToastKind.Warning, "ИГРОВОЙ СЕАНС");
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
            game is null ? Loc.T("НАЧАТЬ ИГРОВОЙ СЕАНС?") : Loc.T("ИГРАТЬ С УСКОРЕНИЕМ?"),
            game is null
                ? Loc.T("На время сеанса Lycoris сделает следующее:")
                : Loc.Tf("Lycoris запустит «{0}» и на время игры сделает следующее:", game.Name),
            plan.Describe(game),
            game is null ? Loc.T("ВКЛЮЧИТЬ") : Loc.T("ЗАПУСТИТЬ"),
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
        OnPropertyChanged(nameof(StartNote));
        OnPropertyChanged(nameof(HasStartNote));
        OnPropertyChanged(nameof(BoostActiveNote));
        OnPropertyChanged(nameof(HasBoostActiveNote));
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
            case nameof(AppSettings.GameBoostCloseMode):
                OnPropertyChanged(nameof(CloseModeIsClose));
                OnPropertyChanged(nameof(CloseModeIsEcoQos));
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
            var skip = BoostPolicy.SkipPrefix;
            // The option is titled «Схема питания»: the texts drop their subject (plan switch, or the Windows 11 power mode
            // where the plan cannot be switched — the mode text itself says the scheme stays).
            PowerNote = text.StartsWith(BoostPolicy.SwitchPrefix, StringComparison.Ordinal) ? text[BoostPolicy.SwitchPrefix.Length..]
                : text.StartsWith(BoostPolicy.SwitchModePrefix, StringComparison.Ordinal) ? text[BoostPolicy.SwitchModePrefix.Length..]
                : text.StartsWith(skip, StringComparison.Ordinal) ? Loc.T("Не меняется: ") + text[skip.Length..]
                : text;
        }
        catch (Exception ex)
        {
            Log.Warn("Игровой сеанс: не удалось определить план питания — {0}", ex.Message);
            PowerNote = Loc.T("Не удалось определить текущий план питания.");
        }
    }

    private void UpdateCloseList()
    {
        var names = (Settings.GameBoostCloseList ?? []).Select(UserProcesses.NormalizeName).Where(n => n.Length > 0).ToList();
        CloseListCount = names.Count;
        var eco = Settings.GameBoostUsesEcoQos && ShowCloseModeChoice;
        CloseListSummary = names.Count == 0
            ? (eco ? Loc.T("Список пуст — перед игрой никому не назначается режим эффективности.") : Loc.T("Список пуст — перед игрой ничего не закрывается."))
            : string.Join(", ", names.Take(6)) + (names.Count > 6 ? Loc.Tf(" и ещё {0}", names.Count - 6) : "");
    }

    // ───────────────────────────── Crash recovery ─────────────────────────────

    private void CheckStale()
    {
        if (Boost.IsActive)
        {
            HasStaleBoost = false;
            return;
        }
        var records = StaleRecords();
        HasStaleBoost = records.Count > 0;
        // A plan chosen after the crash (e.g. in Optimize) is a newer layer: it stays, the session's record only hands over.
        StaleText = GameBoostService.StaleBanner(records, NewerLayer);
        StaleActionText = GameBoostService.AllKept(records, NewerLayer) ? Loc.T("ЗАКРЫТЬ ЗАПИСЬ СЕАНСА") : Loc.T("ВЕРНУТЬ НАСТРОЙКИ");
    }

    private static List<ChangeRecord> StaleRecords() =>
        ChangeJournal.Instance.ActiveRecords.Where(r => r.Source == GameBoostService.Source).ToList();

    private static ChangeRecord? NewerLayer(ChangeRecord r) => ChangeJournal.Instance.NewerLayer(r);

    private async Task OfferStaleRestoreAsync()
    {
        if (!HasStaleBoost || _staleOffered || !IsElevated) return;
        _staleOffered = true;
        var records = StaleRecords();
        if (records.Count == 0) return;
        var ok = await Ui.ConfirmAsync("НЕЗАВЕРШЁННЫЙ ИГРОВОЙ СЕАНС",
            GameBoostService.StalePrompt(records, NewerLayer), GameBoostService.StaleLines(records, NewerLayer),
            GameBoostService.AllKept(records, NewerLayer) ? Loc.T("ЗАКРЫТЬ ЗАПИСЬ") : Loc.T("ВЕРНУТЬ"), "ПОЗЖЕ",
            footnote: "Это можно сделать и позже: здесь или в «Восстановлении».");
        if (ok) await RestoreStale();
    }

    [RelayCommand]
    private async Task RestoreStale()
    {
        if (!RequireAdmin(Loc.Key("Вернуть настройки игрового сеанса"), "RestoreStale")) return;
        await RunBusyAsync(OperationClass.JournalChange, Loc.Key("Откат игрового сеанса…"), async () =>
        {
            var r = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(GameBoostService.Source));
            if (r.Success) Ui.Toast(GameBoostService.StaleRevertedText(r), ToastKind.Success, "ИГРОВОЙ СЕАНС");
            else Ui.Toast(Loc.Tf("Не удалось вернуть: {0}", string.Join("; ", r.Errors)), ToastKind.Error, "ИГРОВОЙ СЕАНС");
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
            Log.Warn("Готовность к играм: {0}", ex.Message);
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
            Log.Warn("Список запущенных приложений: {0}", ex.Message);
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
            Ui.Toast(Loc.Tf("«{0}» — системный процесс, драйвер, античит или лаунчер. Его закрывать нельзя.", name), ToastKind.Warning, "СПИСОК ЗАКРЫТИЯ");
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
        if (EditorList.Count == 0) Log.Info("Игровой сеанс: список закрываемых приложений очищен.");
        else Log.Info("Игровой сеанс: перед игрой закрывать {0}", string.Join(", ", EditorList));
        Ui.Toast(EditorList.Count == 0 ? Loc.T("Список закрытия пуст.") : Loc.Tf("Список закрытия сохранён: {0}.", EditorList.Count), ToastKind.Success);
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;
}
