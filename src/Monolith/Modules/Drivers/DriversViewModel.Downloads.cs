using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

/// <summary>
/// ВИДЕОКАРТЫ И ЧИПСЕТ → direct driver download: the official NVIDIA / AMD installer is fetched from the vendor's
/// download server (DriverDownloads: host allow-list + Authenticode), a restore point is made, and the vendor's own
/// installer is started — no browser, no third-party site.
/// </summary>
public sealed partial class DriversViewModel
{
    private DriverPackage? _amdPackage;
    private Task? _amdLookup;

    [ObservableProperty] private bool _isDriverDownloadRunning;

    /// <summary>True while a download waits in the global install queue (a second click must not queue it twice).</summary>
    private bool _downloadWaiting;

    // The parameter is the card (a click) or the card's PnP id as text (the admin-intent replay, which waits for the vendor lookup).
    private bool CanDownloadDriver(object? target) =>
        target is string ? CanModify() : target is GpuCardItem { HasDownload: true, IsDownloading: false } && CanModify();

    [RelayCommand(CanExecute = nameof(CanDownloadDriver))]
    private async Task DownloadDriverAsync(object? target)
    {
        var card = CardFor(target);
        if (target is string && card is not null) await WaitForGpuLookupAsync(card);
        if (card?.Package is null || !card.HasDownload || card.IsDownloading) return;
        if (!RequireAdmin(Loc.Tf("загрузка драйвера {0}", card.Package.VendorName), "DownloadDriver", card.Gpu.PnpDeviceId)) return;
        if (!CanModify())
        {
            Ui.Toast("Дождитесь окончания текущей операции на этой странице.", ToastKind.Warning);
            return;
        }

        // The confirmation must name the size: ask the server first when the lookup did not.
        if (card.Package.SizeBytes is null) await FillSizeAsync(card, card.Package);
        var package = card.Package!;

        var confirmed = await Ui.ConfirmAsync(
            Loc.Tf("СКАЧАТЬ И УСТАНОВИТЬ ДРАЙВЕР {0} {1}?", package.VendorName.ToUpperInvariant(), package.Version),
            DriverDownloadText.Lead(package),
            DriverDownloadText.Plan(package, RestorePointService.IsEnabled()),
            confirmText: Loc.T("СКАЧАТЬ И УСТАНОВИТЬ"),
            footnote: Loc.T("Откат: точка восстановления или «Откатить драйвер» в диспетчере устройств."));
        if (!confirmed || _downloadWaiting) return;

        // One installer at a time across the whole app: waits in the visible queue behind a winget queue or another driver install.
        OperationLease? lease;
        _downloadWaiting = true;
        try
        {
            lease = await AcquireLeaseAsync(OperationClass.Install, Loc.Tf("Загрузка драйвера {0} {1}", package.VendorName, package.Version),
                () => Ui.Post(() => { if (card.CancelDownloadCommand.CanExecute(null)) card.CancelDownloadCommand.Execute(null); }));
        }
        finally
        {
            _downloadWaiting = false;
        }
        if (lease is null) return;
        using var scope = lease.Scope();
        using var leaseGuard = lease;
        CurrentLease = lease;

        IsDriverDownloadRunning = true;
        var ct = card.BeginDownload(_lifetime.Token);
        try
        {
            Log.Info("Загрузка драйвера {0} {1} с {2}…", package.VendorName, package.Version, package.Url.Host);
            var progress = new Progress<DownloadProgress>(card.ReportDownload);
            var path = await DriverDownloads.DownloadAsync(package, progress, ct);

            card.SetDownloadStep(Loc.T("ТОЧКА ВОССТАНОВЛЕНИЯ"), Loc.T("Создание точки восстановления системы…"));
            if (!await EnsureRestorePointAsync(Loc.Tf("MONOLITH: драйвер {0} {1}", package.VendorName, package.Version)))
            {
                var declined = Loc.T("Установка отменена. Скачанный и проверенный установщик сохранён — повторно скачивать его не придётся.");
                card.FinishDownload(false, declined);
                Log.Info("Драйвер {0} {1}: установка отменена (нет точки восстановления).", package.VendorName, package.Version);
                return;
            }

            // The file sat in the admin-only folder; check it once more right before it is started.
            card.SetDownloadStep(Loc.T("ПРОВЕРКА ПОДПИСИ"), Loc.T("Повторная проверка цифровой подписи перед запуском…"));
            var (valid, signer) = await DriverDownloads.VerifySignatureAsync(path, package.Signer);
            if (!valid)
                throw new InvalidOperationException(Loc.Tf("Подпись установщика не подтверждена перед запуском ({0}) — запуск отменён.", signer));

            DriverDownloads.LaunchInstaller(path);
            var done = Loc.Tf("Официальный установщик {0} {1} запущен ({2}). Пройдите его шаги; после установки нажмите «Обновить», чтобы увидеть новую версию.",
                package.VendorName, package.Version, signer);
            card.FinishDownload(true, done);
            Log.Success("Драйвер {0} {1}: установщик запущен.", package.VendorName, package.Version);
            Ui.Toast(done, ToastKind.Success, Loc.Tf("ДРАЙВЕР {0}", package.VendorName.ToUpperInvariant()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            var cancelled = Loc.T("Загрузка отменена. Недокачанный файл удалён.");
            card.FinishDownload(false, cancelled);
            Log.Info("Драйвер {0} {1}: загрузка отменена.", package.VendorName, package.Version);
            Ui.Toast(cancelled, ToastKind.Warning, Loc.Tf("ДРАЙВЕР {0}", package.VendorName.ToUpperInvariant()));
        }
        catch (Exception ex)
        {
            card.FinishDownload(false, ex.Message);
            Log.Error("Драйвер {0} {1} не установлен", ex, package.VendorName, package.Version);
            Ui.Toast(ex.Message, ToastKind.Error, Loc.T("ДРАЙВЕР НЕ УСТАНОВЛЕН"));
        }
        finally
        {
            IsDriverDownloadRunning = false;
            CurrentLease = null;
        }
    }

    /// <summary>A replayed intent: the vendor lookups run in the background after the page opened — wait for this card's.</summary>
    private async Task WaitForGpuLookupAsync(GpuCardItem card)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60) && !_lifetime.IsCancellationRequested)
        {
            if (card.HasDownload) return;
            // Nothing is being checked any more and still no installer: nothing to download.
            if (!card.IsCheckingLatest && sw.Elapsed > TimeSpan.FromSeconds(5) && InventoryLoaded) return;
            await Task.Delay(300);
        }
    }

    /// <summary>
    /// «ПРОВЕРИТЬ» (Dashboard tile, ПОДБОР card): re-reads the vendor's latest driver for the main video card and tells the
    /// result — a version check only, nothing is downloaded or changed.
    /// </summary>
    [RelayCommand]
    private async Task CheckGpuAsync()
    {
        SelectedTab = DriversTab.Hardware;
        if (!InventoryLoaded) await WaitForGpuInventoryAsync();
        await CheckNvidiaLatestAsync();
        await OfferAmdInstallerAsync();
        OnPropertyChanged(nameof(GpuTileText));
        OnPropertyChanged(nameof(GpuUpdateAvailable));
        var card = Gpus.FirstOrDefault(g => g.IsPrimary) ?? Gpus.FirstOrDefault();
        if (card is null)
        {
            Ui.Toast("Видеокарта не найдена.", ToastKind.Warning, Loc.T("ВИДЕОКАРТА"));
            return;
        }
        if (card.HasLatest)
            Ui.Toast(card.UpdateAvailable
                ? Loc.Tf("Есть новый драйвер {0} (сейчас {1}). Кнопка «СКАЧАТЬ И УСТАНОВИТЬ» — на карточке видеокарты.", card.LatestVersion, card.VersionText)
                : Loc.Tf("Установлен последний драйвер: {0}.", card.VersionText),
                ToastKind.Info, Loc.T("ВИДЕОКАРТА"));
        else
            Ui.Toast(Loc.Tf("Сейчас {0}. Сверить версию с производителем не удалось — откройте официальный сайт на карточке.", card.VersionText), ToastKind.Info, Loc.T("ВИДЕОКАРТА"));
    }

    private async Task WaitForGpuInventoryAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!InventoryLoaded && sw.Elapsed < TimeSpan.FromSeconds(60) && !_lifetime.IsCancellationRequested) await Task.Delay(200);
    }

    /// <summary>The main video card's driver version for the Dashboard tile («621.01»), or «—» before the first read.</summary>
    public string GpuTileText => (Gpus.FirstOrDefault(g => g.IsPrimary) ?? Gpus.FirstOrDefault())?.VersionText ?? "—";

    /// <summary>A newer official driver exists for the main video card (known after the vendor lookup).</summary>
    public bool GpuUpdateAvailable => (Gpus.FirstOrDefault(g => g.IsPrimary) ?? Gpus.FirstOrDefault())?.UpdateAvailable == true;

    /// <summary>Asks the vendor's server for the exact size (HEAD) and shows it on the card.</summary>
    private async Task FillSizeAsync(GpuCardItem card, DriverPackage package)
    {
        var size = await DriverDownloads.GetRemoteSizeAsync(package, _lifetime.Token);
        if (size is not null && ReferenceEquals(card.Package, package)) card.SetPackage(package with { SizeBytes = size });
        DownloadDriverCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// AMD cards: finds the official auto-detect installer on amd.com in the background (once per session) and offers
    /// it on every AMD card that needs a driver from AMD.
    /// </summary>
    private async Task OfferAmdInstallerAsync()
    {
        var cards = Gpus.Where(g => g.Vendor == GpuVendor.Amd && g.HasTool).ToList();
        if (cards.Count == 0) return;
        try
        {
            // Once per session; a lookup that found nothing (offline, page changed) is retried on the next refresh.
            if (_amdLookup is null || (_amdLookup.IsCompleted && _amdPackage is null)) _amdLookup = LookupAmdAsync();
            await _amdLookup;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        var now = DateTime.Now;
        // Cards may have been rebuilt meanwhile (refresh): apply to the current ones.
        foreach (var card in Gpus.Where(g => g.Vendor == GpuVendor.Amd && g.HasTool && !g.IsDownloading))
            card.ApplyAmdPackage(_amdPackage, now);
        DownloadDriverCommand.NotifyCanExecuteChanged();
        UpdateStats();
    }

    private async Task LookupAmdAsync()
    {
        var package = await DriverDownloads.GetAmdInstallerAsync(_lifetime.Token);
        if (package is null)
        {
            Log.Info("AMD: ссылка на официальный установщик не найдена — доступна кнопка официального сайта.");
            return;
        }
        var size = await DriverDownloads.GetRemoteSizeAsync(package, _lifetime.Token);
        _amdPackage = size is null ? package : package with { SizeBytes = size };
        Log.Info("AMD: официальный установщик {0} ({1}) доступен для загрузки.", package.Version, package.Url.Host);
    }
}

/// <summary>Texts of the direct-download confirmation — exactly what will happen, nothing more.</summary>
internal static class DriverDownloadText
{
    public static string Lead(DriverPackage p) => p.Vendor == GpuVendor.Amd
        ? Loc.T("MONOLITH скачает официальный установщик AMD Software: Adrenalin Edition прямо с сервера AMD — без браузера и сторонних сайтов, " +
          "проверит его подпись и запустит. Это компактный веб-установщик: подходящий драйвер он докачает с серверов AMD сам.")
        : Loc.Tf("MONOLITH скачает официальный установщик драйвера прямо с сервера {0} — без браузера и сторонних сайтов, проверит его подпись и запустит. Саму установку вы проводите в окне установщика.", p.VendorName);

    public static List<string> Plan(DriverPackage p, bool restorePointsEnabled)
    {
        var lines = new List<string>
        {
            Loc.Tf("Версия: {0} {1}", p.VendorName, p.Version),
            Loc.Tf("Размер: {0}", p.SizeDisplay ?? Loc.T("сервер сообщит при загрузке")),
            Loc.Tf("Источник: официальный сервер {0} {1} (HTTPS)", p.VendorName, p.Url.Host),
            Loc.Tf("Файл: {0} → {1} (запись туда разрешена только администраторам)", p.FileName, DriverDownloads.DownloadsDir),
            Loc.Tf("Файл будет проверен по цифровой подписи {0}: без действительной подписи он удаляется и не запускается", p.Signer),
            restorePointsEnabled
                ? Loc.T("Перед установкой будет создана точка восстановления (если Windows её не создаст, MONOLITH спросит, продолжать ли)")
                : Loc.T("Защита системы отключена — точка восстановления создана не будет; MONOLITH спросит, продолжать ли без неё"),
            p.Vendor == GpuVendor.Amd
                ? Loc.T("Откроется официальный установщик AMD: он определит видеокарту, а при желании в нём можно выбрать чистую установку (сброс к заводским настройкам)")
                : Loc.T("Откроется официальный установщик NVIDIA — в нём можно выбрать «Выборочная → Чистая установка»"),
            Loc.T("Лицензию производителя показывает сам установщик — MONOLITH ничего не принимает за вас"),
            Loc.Tf("Нужно свободного места на системном диске: не меньше {0}", SystemProfile.FormatBytes(DriverDownloads.RequiredFreeSpace(p.Vendor))),
            Loc.T("MONOLITH не перезагружает компьютер: если установщик попросит перезагрузку, решаете вы"),
        };
        return lines;
    }
}
