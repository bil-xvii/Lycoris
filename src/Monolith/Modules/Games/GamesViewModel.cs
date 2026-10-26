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
    public override string Subtitle => "Библиотека игр и игровой режим: максимум ресурсов игре, пока она запущена.";
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
        ? "Игровой режим меняет схему питания — нужны права администратора."
        : Boost.IsActive
            ? "Игровой режим уже работает — сначала остановите текущий сеанс."
            : "Запустить игру и на время игры включить игровой режим.";
    public bool IsBoostIdle => !Boost.IsActive;
    public string BoostStatus => Boost.IsTransitioning && !Boost.IsActive ? "ЗАПУСК"
        : Boost.IsActive ? (string.IsNullOrEmpty(Boost.PhaseText) ? "АКТИВЕН" : Boost.PhaseText)
        : IsElevated ? "ВЫКЛЮЧЕН" : "НЕДОСТУПЕН";

    // ───────────────────────────── Readiness state ─────────────────────────────

    public ObservableCollection<ReadinessCheck> Readiness { get; } = [];
    [ObservableProperty] private int _readinessPassed;
    [ObservableProperty] private int _readinessTotal;
    [ObservableProperty] private double _readinessPercent;
    [ObservableProperty] private bool _readinessLoaded;

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
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), ScanAsync());
        await OfferStaleRestoreAsync();
    }

    protected override async Task OnReturnAsync()
    {
        _active = true;
        if (Boost.IsActive) StartTicker();
        CheckStale();
        // Library changes rarely: rescan on return only when the last scan is a few minutes old (ОБНОВИТЬ forces it).
        var rescan = DateTime.Now - _lastScan > TimeSpan.FromMinutes(3) ? ScanAsync() : Task.CompletedTask;
        await Task.WhenAll(RefreshReadinessAsync(), RefreshPowerNoteAsync(), rescan);
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
            await Task.Run(() => GameLauncher.Launch(card.Entry));
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

    // ───────────────────────────── Game Boost ─────────────────────────────

    [RelayCommand]
    private Task PlayBoosted(GameCardViewModel? card) => card is null ? Task.CompletedTask : StartBoostFlowAsync(card.Entry);

    [RelayCommand]
    private Task StartBoost() => StartBoostFlowAsync(null);

    [RelayCommand]
    private async Task StopBoost()
    {
        if (!Boost.IsActive) return;
        await Boost.StopAsync("Игровой режим остановлен вручную, изменения отменены.");
    }

    private async Task StartBoostFlowAsync(GameEntry? game)
    {
        if (!RequireAdmin("Игровой режим")) return;
        if (Boost.IsActive || Boost.IsTransitioning)
        {
            Ui.Toast("Игровой режим уже работает. Сначала остановите текущий сеанс кнопкой «СТОП».", ToastKind.Warning, "ИГРОВОЙ РЕЖИМ");
            return;
        }
        if (game is { IsAvailable: false })
        {
            Ui.Toast($"Файл игры не найден: {game.ExePath}", ToastKind.Warning, "ИГРОВОЙ РЕЖИМ");
            return;
        }

        BoostPlan plan;
        try
        {
            plan = await GameBoostService.BuildPlanAsync(game);
        }
        catch (Exception ex)
        {
            Log.Error("Игровой режим: не удалось подготовить сеанс", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ИГРОВОЙ РЕЖИМ");
            return;
        }

        var ok = await Ui.ConfirmAsync(
            game is null ? "ВКЛЮЧИТЬ ИГРОВОЙ РЕЖИМ?" : "ИГРАТЬ С УСКОРЕНИЕМ?",
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
            Log.Warn($"Игровой режим: не удалось определить план питания — {ex.Message}");
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
            : "Прошлый сеанс игрового режима не завершился (MONOLITH был закрыт аварийно). Всё ещё действует: " +
              string.Join("; ", records.Select(r => $"{r.Title.Replace("Игровой режим: ", "")} — «{r.NewValue}» вместо «{r.OldValue}»")) + ".";
    }

    private async Task OfferStaleRestoreAsync()
    {
        if (!HasStaleBoost || _staleOffered || !IsElevated) return;
        _staleOffered = true;
        var details = ChangeJournal.Instance.ActiveRecords.Where(r => r.Source == GameBoostService.Source)
            .Select(r => $"{r.Title}: сейчас «{r.NewValue}», было «{r.OldValue}»").ToList();
        var ok = await Ui.ConfirmAsync("ВЕРНУТЬ НАСТРОЙКИ?",
            "Прошлый сеанс игрового режима не завершился — MONOLITH был закрыт аварийно. Эти временные изменения всё ещё действуют. Вернуть исходные значения?",
            details, "ВЕРНУТЬ", "ПОЗЖЕ",
            footnote: "Это можно сделать и позже: здесь или в «Центре отката».");
        if (ok) await RestoreStale();
    }

    [RelayCommand]
    private async Task RestoreStale()
    {
        if (!RequireAdmin("Вернуть настройки игрового режима")) return;
        await RunBusyAsync("Откат игрового режима…", async () =>
        {
            var r = await Task.Run(() => ChangeJournal.Instance.RevertSourceAsync(GameBoostService.Source));
            if (r.Success) Ui.Toast($"Исходные настройки возвращены (изменений: {r.Reverted}).", ToastKind.Success, "ИГРОВОЙ РЕЖИМ");
            else Ui.Toast($"Не удалось вернуть: {string.Join("; ", r.Errors)}", ToastKind.Error, "ИГРОВОЙ РЕЖИМ");
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
            Readiness.Clear();
            foreach (var c in checks) Readiness.Add(c);
            var (passed, total) = GamingReadiness.Score(checks);
            ReadinessPassed = passed;
            ReadinessTotal = total;
            ReadinessPercent = total == 0 ? 0 : passed * 100.0 / total;
            ReadinessLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Готовность к играм: {ex.Message}");
        }
    }

    [RelayCommand]
    private void GoOptimize() => Ui.Navigate("optimize");

    [RelayCommand]
    private void GoDrivers() => Ui.Navigate("drivers");

    [RelayCommand]
    private void GoRestore() => Ui.Navigate("restore");

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
            ? "Игровой режим: список закрываемых приложений очищен."
            : "Игровой режим: перед игрой закрывать " + string.Join(", ", EditorList));
        Ui.Toast(EditorList.Count == 0 ? "Список закрытия пуст." : $"Список закрытия сохранён: {EditorList.Count}.", ToastKind.Success);
    }

    [RelayCommand]
    private void CancelEditor() => IsEditorOpen = false;
}
