using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.Modules.Cleaner;
using Monolith.Modules.Dashboard.Checks;
using Monolith.Modules.Devices;
using Monolith.Modules.Drivers;
using Monolith.Modules.Games;
using Monolith.Modules.Optimize;
using Monolith.Modules.Restore;
using Monolith.Shell;
using Monolith.UI;

namespace Monolith.Modules.Dashboard;

/// <summary>
/// The launchpad of «Панель»: six tiles with a live value and ONE labelled button each, the score strip with its chips and
/// «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ». Nothing here duplicates a page's logic: a tile calls the owning page's own command (found
/// through the shell's page list), so the confirmation, the journal and the admin checks are exactly those of that page.
/// Pages the tiles need are loaded in the background (their first load only reads); the vendor-site lookup for the video
/// card driver waits for the button.
/// </summary>
public sealed partial class DashboardViewModel
{
    private static MainViewModel? Main => Ui.Host as MainViewModel;
    private static T? PageOf<T>() where T : PageViewModel => Main?.Pages.OfType<T>().FirstOrDefault();
    private static CleanerViewModel? CleanerPage => Main?.Maintenance.Cleaner;

    public IReadOnlyList<LaunchTile> Tiles { get; }

    private LaunchTile _gameTile = null!, _optimizeTile = null!, _cleanTile = null!, _gpuTile = null!, _undoTile = null!, _pingTile = null!;
    private OptimizeViewModel? _optimize;
    private bool _attached;

    private IReadOnlyList<LaunchTile> BuildTiles()
    {
        _gameTile = new("game", "ИГРОВОЙ СЕАНС", Glyphs.Rocket, ToggleGameCommand, LaunchpadLogic.Game(false));
        _optimizeTile = new("optimize", "ОПТИМИЗАЦИЯ", Glyphs.Bolt, ApplyRecommendedCommand, LaunchpadLogic.Optimize(null, 0));
        _cleanTile = new("clean", "ОЧИСТКА", Glyphs.Delete, CleanJunkCommand, LaunchpadLogic.Clean(false, "—", "", "—", ""));
        _gpuTile = new("gpu", "ВИДЕОКАРТА", Glyphs.Monitor, GpuActionCommand, LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.Unchecked, "", null, null, null, null));
        _undoTile = new("undo", "ОТКАТ", Glyphs.History, RevertLastCommand, LaunchpadLogic.Undo(0, null, null));
        _pingTile = new("ping", "ПИНГ", Glyphs.Network, MeasurePingCommand, LaunchpadLogic.Ping(null));
        return [_gameTile, _optimizeTile, _cleanTile, _gpuTile, _undoTile, _pingTile];
    }

    // ───────────────────────────── Score strip: chips + fix-all ─────────────────────────────

    [ObservableProperty] private CheckFilter _filter = CheckFilter.Warning;
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FixSafeText), nameof(HasSafeFixes))] private int _safeFixCount;
    private bool _filterChosen;

    public string FixSafeText => LaunchpadLogic.FixSafeText(SafeFixCount);
    /// <summary>White (primary) only when the button really has something to do.</summary>
    public bool HasSafeFixes => SafeFixCount > 0;

    public bool ShowWarnings { get => Filter == CheckFilter.Warning; set { if (value) Choose(CheckFilter.Warning); } }
    public bool ShowInfos { get => Filter == CheckFilter.Info; set { if (value) Choose(CheckFilter.Info); } }
    public bool ShowOk { get => Filter == CheckFilter.Ok; set { if (value) Choose(CheckFilter.Ok); } }

    /// <summary>The chip the user clicked: kept across re-checks (the default filter only applies until the user picks one).</summary>
    private void Choose(CheckFilter filter)
    {
        _filterChosen = true;
        Filter = filter;
    }

    partial void OnFilterChanged(CheckFilter value)
    {
        OnPropertyChanged(nameof(ShowWarnings));
        OnPropertyChanged(nameof(ShowInfos));
        OnPropertyChanged(nameof(ShowOk));
        RebuildVisible();
    }

    private static CheckFilter FilterOf(CheckSeverity s) => s switch
    {
        CheckSeverity.Warning => CheckFilter.Warning,
        CheckSeverity.Info => CheckFilter.Info,
        _ => CheckFilter.Ok,
    };

    /// <summary>Title of the list card: «Проблемы · 2», «Советы · 2», «В норме · 19».</summary>
    public string ListTitle => $"{Filter switch { CheckFilter.Warning => "Проблемы", CheckFilter.Info => "Советы", _ => "В норме" }} · {Visible.Count}";

    /// <summary>One line of the key facts (CPU · GPU · RAM · display); the full table is behind «ВСЕ СВЕДЕНИЯ».</summary>
    [ObservableProperty] private string _systemSummary = "";

    private void UpdateSystemSummary()
    {
        string? Value(string label) => SystemRows.FirstOrDefault(r => r.Label == label)?.Value;
        var parts = new[] { Value("ПРОЦЕССОР"), Value("ВИДЕОКАРТА"), Value("ПАМЯТЬ"), Value("ДИСПЛЕЙ") }
            .Where(v => !string.IsNullOrWhiteSpace(v) && v != DashFormat.Missing);
        SystemSummary = string.Join(" · ", parts);
    }

    private void RebuildVisible()
    {
        Visible.Clear();
        foreach (var item in _all.Where(i => FilterOf(i.Severity) == Filter)) Visible.Add(item);
        EmptyText = HasResults && Visible.Count == 0 ? LaunchpadLogic.EmptyText(Filter) : "";
        OnPropertyChanged(nameof(ListTitle));
    }

    // ───────────────────────────── Live values from the pages ─────────────────────────────

    /// <summary>
    /// Hooks the live sources once the shell exists (never in unit tests): the game session flag, the journal, and — in the
    /// background — the Optimize and Cleaner pages whose first load only reads the system.
    /// </summary>
    private void AttachHost()
    {
        if (_attached || Main is not { } main) return;
        _attached = true;
        GameBoostState.Changed += _ => Ui.Post(UpdateGameTile);
        try { ChangeJournal.Instance.Changed += () => Ui.Post(UpdateUndoTile); }
        catch (Exception ex) { Log.Warn($"Панель: журнал недоступен для плитки отката: {ex.Message}"); }
        UpdateGameTile();
        UpdateUndoTile();
        _ = WarmUpAsync(main);
    }

    private async Task WarmUpAsync(MainViewModel main)
    {
        try
        {
            if (main.Pages.OfType<OptimizeViewModel>().FirstOrDefault() is { } optimize)
            {
                _optimize = optimize;
                foreach (var tile in optimize.Profiles)
                    tile.PropertyChanged += (_, e) =>
                    {
                        if (e.PropertyName is nameof(ProfileTileViewModel.ToChange) or nameof(ProfileTileViewModel.Total)) Ui.Post(UpdateOptimizeTile);
                    };
                await optimize.EnsureLoadedAsync();
                Ui.Post(UpdateOptimizeTile);
            }
        }
        catch (Exception ex) { Log.Warn($"Панель: число оптимизаций не прочитано: {ex.Message}"); }

        try
        {
            // The dry run reads a lot of folders: let the first screen settle first.
            await Task.Delay(1500);
            if (CleanerPage is { } cleaner)
            {
                cleaner.PropertyChanged += OnCleanerChanged;
                UpdateCleanTile();
                await cleaner.EnsureLoadedAsync();
                Ui.Post(UpdateCleanTile);
            }
        }
        catch (Exception ex) { Log.Warn($"Панель: размер мусора не посчитан: {ex.Message}"); }
    }

    private void OnCleanerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CleanerViewModel.FoundValue) or nameof(CleanerViewModel.FoundUnit) or nameof(CleanerViewModel.SelectedValue)
            or nameof(CleanerViewModel.SelectedUnit) or nameof(CleanerViewModel.IsWorking))
            Ui.Post(UpdateCleanTile);
    }

    private void UpdateGameTile() => _gameTile.Apply(LaunchpadLogic.Game(GameBoostState.IsActive));

    private void UpdateOptimizeTile()
    {
        var recommended = _optimize?.Profiles.FirstOrDefault(p => p.Kind == OptimizeProfile.Recommended);
        _optimizeTile.Apply(LaunchpadLogic.Optimize(recommended?.ToChange, recommended?.Total ?? 0));
    }

    /// <summary>The size the cleaner would delete now («654,8 МБ»); null until a dry run found something selected.</summary>
    internal string? CleanSize { get; private set; }

    private void UpdateCleanTile()
    {
        if (CleanerPage is not { } c) return;
        _cleanTile.Apply(LaunchpadLogic.Clean(c.IsWorking, c.SelectedValue, c.SelectedUnit, c.FoundValue, c.FoundUnit));
        var size = c.IsWorking ? null : LaunchpadLogic.CleanSize(c.SelectedValue, c.SelectedUnit);
        if (size == CleanSize) return;
        CleanSize = size;
        foreach (var item in _all) item.RefreshLabels();
    }

    /// <summary>Live button text of a problem row: the cleanup row says how much it will clean.</summary>
    private string? ActionLabel(CheckAction action) =>
        action is { Kind: CheckActionKind.Navigate, Target: "cleaner" } && CleanSize is { } size ? $"ОЧИСТИТЬ {size}" : null;

    private void UpdateUndoTile()
    {
        try
        {
            var journal = ChangeJournal.Instance;
            var active = journal.ActiveRecords;
            var revertible = active.Where(ChangeJournal.IsBulkRevertible).Select(r => r.BatchId).ToHashSet();
            var last = journal.Batches.Where(b => revertible.Contains(b.Id)).OrderByDescending(b => b.TimestampUtc).FirstOrDefault();
            var title = last is null ? null : last.Title.Length > 0 ? last.Title : "Без названия";
            _undoTile.Apply(LaunchpadLogic.Undo(active.Count, title, last?.Timestamp));
        }
        catch (Exception ex) { Log.Warn($"Панель: плитка отката: {ex.Message}"); }
    }

    // ───────────────────────────── Tile commands ─────────────────────────────

    /// <summary>ИГРОВОЙ СЕАНС: «СТАРТ» / «СТОП» — the Games page's own commands (plan, confirmation, journal).</summary>
    [RelayCommand]
    private async Task ToggleGameAsync()
    {
        if (PageOf<GamesViewModel>() is not { } games)
        {
            Ui.Navigate("games");
            return;
        }
        if (GameBoostState.IsActive)
        {
            await games.StopBoostCommand.ExecuteAsync(null);
            return;
        }
        if (!RequireAdmin("Игровой сеанс", "ToggleGame")) return;
        await games.EnsureLoadedAsync();
        await games.StartBoostCommand.ExecuteAsync(null);
    }

    /// <summary>ОПТИМИЗАЦИЯ: selects the recommended profile on the Optimize page and presses its «ПРИМЕНИТЬ» (full list + confirmation).</summary>
    [RelayCommand]
    private async Task ApplyRecommendedAsync()
    {
        if (_optimize is not { } optimize)
        {
            Ui.Navigate("optimize");
            return;
        }
        var recommended = optimize.Profiles.FirstOrDefault(p => p.Kind == OptimizeProfile.Recommended);
        if (recommended?.ToChange is null or 0)
        {
            Ui.Navigate("optimize"); // nothing to apply (or not read yet): show the list
            return;
        }
        if (!RequireAdmin("Применение оптимизаций", "ApplyRecommended")) return;
        await optimize.EnsureLoadedAsync();
        optimize.SelectProfile(OptimizeProfile.Recommended);
        if (optimize.ApplyCommand.CanExecute(null)) await optimize.ApplyCommand.ExecuteAsync(null);
        else Ui.Toast("Применять сейчас нечего или страница «Оптимизация» занята. Откройте её и проверьте список.", ToastKind.Info, "ОПТИМИЗАЦИЯ");
    }

    /// <summary>ОЧИСТКА: the analysis (read-only) when nothing is known yet, otherwise the Cleaner's own «ОЧИСТИТЬ» with its confirmation.</summary>
    [RelayCommand]
    private async Task CleanJunkAsync()
    {
        if (CleanerPage is not { } cleaner)
        {
            Ui.Navigate("cleaner");
            return;
        }
        await cleaner.EnsureLoadedAsync();
        if (CleanSize is null)
        {
            if (cleaner.AnalyzeCommand.CanExecute(null)) await cleaner.AnalyzeCommand.ExecuteAsync(null);
            return;
        }
        if (cleaner.CleanCommand.CanExecute(null)) await cleaner.CleanCommand.ExecuteAsync(null);
        else Ui.Toast("Очистка занята другой работой — дождитесь её окончания.", ToastKind.Info, "ОЧИСТКА");
    }

    /// <summary>ОТКАТ: the Restore page's «ОТКАТИТЬ ПОСЛЕДНИЙ ПАКЕТ»; with nothing to revert the button opens Restore.</summary>
    [RelayCommand]
    private async Task RevertLastAsync()
    {
        if (Main?.Restore is not { } restore || Main.ActiveChanges == 0)
        {
            Ui.Navigate("restore");
            return;
        }
        await restore.EnsureLoadedAsync();
        if (restore.RevertLastBatchCommand.CanExecute(null)) await restore.RevertLastBatchCommand.ExecuteAsync(null);
    }

    /// <summary>ПИНГ: opens the ping tool of «Устройства и сеть» on its row and starts it (a measurement; changes nothing).</summary>
    [RelayCommand]
    private async Task MeasurePingAsync()
    {
        if (Main?.Devices is not { } devices)
        {
            Ui.Navigate("devices");
            return;
        }
        Ui.RequestFocus("devices", "tool:net-ping");
        Ui.Navigate("devices");
        DeviceTool? tool = null;
        var wait = Stopwatch.StartNew();
        while (tool is null && wait.Elapsed < TimeSpan.FromSeconds(15))
        {
            tool = devices.Cards.SelectMany(c => c.Tools).FirstOrDefault(t => t.Id == "net-ping");
            if (tool is null) await Task.Delay(300);
        }
        if (tool is null) return; // the page is open on the right place anyway
        tool.PropertyChanged -= OnPingChanged;
        tool.PropertyChanged += OnPingChanged;
        if (!tool.IsRunning && devices.RunToolCommand.CanExecute(tool)) await devices.RunToolCommand.ExecuteAsync(tool);
    }

    private void OnPingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is DeviceTool tool && e.PropertyName == nameof(DeviceTool.ResultText))
            Ui.Post(() => _pingTile.Apply(LaunchpadLogic.Ping(tool.ResultText)));
    }

    // ───────────────────────────── ВИДЕОКАРТА ─────────────────────────────

    private LaunchpadLogic.GpuState _gpuState = LaunchpadLogic.GpuState.Unchecked;
    private string _gpuInstalled = "";
    private string? _gpuModel;
    private DateTime? _gpuDate;
    private string? _gpuNew;
    private DateTime? _gpuCheckedAt;
    private GpuCardItem? _gpuCard;

    private void UpdateGpuInstalled(SystemProfile profile)
    {
        var gpu = profile.PrimaryGpu;
        _gpuInstalled = gpu is null ? "" : HealthRules.DriverVersionText(gpu);
        _gpuModel = gpu?.Name;
        _gpuDate = gpu?.DriverDate;
        UpdateGpuTile();
    }

    private void UpdateGpuTile() =>
        _gpuTile.Apply(LaunchpadLogic.Gpu(_gpuState, _gpuInstalled, _gpuModel, _gpuDate, _gpuNew, _gpuCheckedAt));

    /// <summary>«ПРОВЕРИТЬ ВЕРСИЮ» asks the vendor; when a newer driver exists the same button becomes «СКАЧАТЬ И УСТАНОВИТЬ x».</summary>
    [RelayCommand]
    private async Task GpuActionAsync()
    {
        if (_gpuState == LaunchpadLogic.GpuState.UpdateReady)
        {
            await GpuDownloadAsync();
            return;
        }
        if (_gpuState == LaunchpadLogic.GpuState.Checking) return;
        if (PageOf<DriversViewModel>() is not { } drivers)
        {
            Ui.Navigate("drivers");
            return;
        }
        await CheckGpuAsync(drivers);
    }

    /// <summary>The Drivers page's own download command (host allow-list, signature, restore point, confirmation).</summary>
    [RelayCommand]
    private async Task GpuDownloadAsync()
    {
        if (PageOf<DriversViewModel>() is not { } drivers)
        {
            Ui.Navigate("drivers");
            return;
        }
        if (!RequireAdmin("Загрузка драйвера видеокарты", "GpuDownload")) return;
        if (_gpuState != LaunchpadLogic.GpuState.UpdateReady) await CheckGpuAsync(drivers); // e.g. right after the relaunch
        if (_gpuState == LaunchpadLogic.GpuState.UpdateReady && _gpuCard is { HasDownload: true } card && drivers.DownloadDriverCommand.CanExecute(card))
            await drivers.DownloadDriverCommand.ExecuteAsync(card);
    }

    private async Task CheckGpuAsync(DriversViewModel drivers)
    {
        _gpuState = LaunchpadLogic.GpuState.Checking;
        UpdateGpuTile();
        try
        {
            await drivers.EnsureLoadedAsync();
            var wait = Stopwatch.StartNew();
            GpuCardItem? card;
            while (true)
            {
                card = drivers.Gpus.FirstOrDefault(g => g.IsPrimary && !g.IsInformational) ?? drivers.Gpus.FirstOrDefault(g => !g.IsInformational);
                var asksVendor = card is { Vendor: GpuVendor.Nvidia or GpuVendor.Amd };
                var settled = card is null
                    || card.HasLatest || card.HasDownload
                    || !asksVendor
                    || (card.Vendor == GpuVendor.Nvidia && wait.Elapsed > TimeSpan.FromSeconds(3) && !card.IsCheckingLatest);
                if (settled || wait.Elapsed > TimeSpan.FromSeconds(25)) break;
                await Task.Delay(400);
            }

            _gpuCard = card;
            _gpuCheckedAt = DateTime.Now;
            if (card is { HasDownload: true, Package: { } package } && (card.UpdateAvailable || card.IsMissingDriver))
            {
                _gpuNew = package.Version;
                _gpuState = LaunchpadLogic.GpuState.UpdateReady;
                Ui.Toast($"Доступен драйвер {package.Version}. Кнопка «СКАЧАТЬ И УСТАНОВИТЬ» покажет, что именно будет сделано.", ToastKind.Info, "ВИДЕОКАРТА");
            }
            else if (card is { HasLatest: true })
            {
                _gpuState = LaunchpadLogic.GpuState.UpToDate;
                Ui.Toast("Установлена последняя версия драйвера.", ToastKind.Success, "ВИДЕОКАРТА");
            }
            else
            {
                _gpuState = LaunchpadLogic.GpuState.Unknown;
                Ui.Toast("Автоматически сравнить версию не удалось — открываю «Драйверы», там есть сайт производителя.", ToastKind.Info, "ВИДЕОКАРТА");
                Ui.RequestFocus("drivers", "tab:overview");
                Ui.Navigate("drivers");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Панель: проверка драйвера видеокарты: {ex.Message}");
            _gpuState = LaunchpadLogic.GpuState.Unknown;
            Ui.Toast(ex.Message, ToastKind.Warning, "ВИДЕОКАРТА");
        }
        finally
        {
            if (_gpuState == LaunchpadLogic.GpuState.Checking) _gpuState = LaunchpadLogic.GpuState.Unchecked;
            UpdateGpuTile();
        }
    }
}
