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

    private bool CanDownloadDriver(GpuCardItem? card) => card is { HasDownload: true, IsDownloading: false } && CanModify();

    [RelayCommand(CanExecute = nameof(CanDownloadDriver))]
    private async Task DownloadDriverAsync(GpuCardItem? card)
    {
        if (card?.Package is null || !card.HasDownload || card.IsDownloading) return;
        if (!RequireAdmin($"загрузка драйвера {card.Package.VendorName}")) return;
        if (!CanModify())
        {
            Ui.Toast("Дождитесь окончания текущей операции на этой странице.", ToastKind.Warning);
            return;
        }

        // The confirmation must name the size: ask the server first when the lookup did not.
        if (card.Package.SizeBytes is null) await FillSizeAsync(card, card.Package);
        var package = card.Package!;

        var confirmed = await Ui.ConfirmAsync(
            $"СКАЧАТЬ И УСТАНОВИТЬ ДРАЙВЕР {package.VendorName.ToUpperInvariant()} {package.Version}?",
            DriverDownloadText.Lead(package),
            DriverDownloadText.Plan(package, RestorePointService.IsEnabled()),
            confirmText: "СКАЧАТЬ И УСТАНОВИТЬ",
            footnote: "Откат: точка восстановления или «Откатить драйвер» в диспетчере устройств.");
        if (!confirmed) return;

        IsDriverDownloadRunning = true;
        var ct = card.BeginDownload(_lifetime.Token);
        try
        {
            Log.Info($"Загрузка драйвера {package.VendorName} {package.Version} с {package.Url.Host}…");
            var progress = new Progress<DownloadProgress>(card.ReportDownload);
            var path = await DriverDownloads.DownloadAsync(package, progress, ct);

            card.SetDownloadStep("ТОЧКА ВОССТАНОВЛЕНИЯ", "Создание точки восстановления системы…");
            if (!await EnsureRestorePointAsync($"MONOLITH: драйвер {package.VendorName} {package.Version}"))
            {
                const string declined = "Установка отменена. Скачанный и проверенный установщик сохранён — повторно скачивать его не придётся.";
                card.FinishDownload(false, declined);
                Log.Info($"Драйвер {package.VendorName} {package.Version}: установка отменена (нет точки восстановления).");
                return;
            }

            // The file sat in the admin-only folder; check it once more right before it is started.
            card.SetDownloadStep("ПРОВЕРКА ПОДПИСИ", "Повторная проверка цифровой подписи перед запуском…");
            var (valid, signer) = await DriverDownloads.VerifySignatureAsync(path, package.Signer);
            if (!valid)
                throw new InvalidOperationException($"Подпись установщика не подтверждена перед запуском ({signer}) — запуск отменён.");

            DriverDownloads.LaunchInstaller(path);
            var done = $"Официальный установщик {package.VendorName} {package.Version} запущен ({signer}). " +
                       "Пройдите его шаги; после установки нажмите «Обновить», чтобы увидеть новую версию.";
            card.FinishDownload(true, done);
            Log.Success($"Драйвер {package.VendorName} {package.Version}: установщик запущен.");
            Ui.Toast(done, ToastKind.Success, $"ДРАЙВЕР {package.VendorName.ToUpperInvariant()}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            const string cancelled = "Загрузка отменена. Недокачанный файл удалён.";
            card.FinishDownload(false, cancelled);
            Log.Info($"Драйвер {package.VendorName} {package.Version}: загрузка отменена.");
            Ui.Toast(cancelled, ToastKind.Warning, $"ДРАЙВЕР {package.VendorName.ToUpperInvariant()}");
        }
        catch (Exception ex)
        {
            card.FinishDownload(false, ex.Message);
            Log.Error($"Драйвер {package.VendorName} {package.Version} не установлен", ex);
            Ui.Toast(ex.Message, ToastKind.Error, "ДРАЙВЕР НЕ УСТАНОВЛЕН");
        }
        finally
        {
            IsDriverDownloadRunning = false;
        }
    }

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
        Log.Info($"AMD: официальный установщик {package.Version} ({package.Url.Host}) доступен для загрузки.");
    }
}

/// <summary>Texts of the direct-download confirmation — exactly what will happen, nothing more.</summary>
internal static class DriverDownloadText
{
    public static string Lead(DriverPackage p) => p.Vendor == GpuVendor.Amd
        ? "MONOLITH скачает официальный установщик AMD Software: Adrenalin Edition прямо с сервера AMD — без браузера и сторонних сайтов, " +
          "проверит его подпись и запустит. Это компактный веб-установщик: подходящий драйвер он докачает с серверов AMD сам."
        : $"MONOLITH скачает официальный установщик драйвера прямо с сервера {p.VendorName} — без браузера и сторонних сайтов, " +
          "проверит его подпись и запустит. Саму установку вы проводите в окне установщика.";

    public static List<string> Plan(DriverPackage p, bool restorePointsEnabled)
    {
        var lines = new List<string>
        {
            $"Версия: {p.VendorName} {p.Version}",
            $"Размер: {p.SizeDisplay ?? "сервер сообщит при загрузке"}",
            $"Источник: официальный сервер {p.VendorName} {p.Url.Host} (HTTPS)",
            $"Файл: {p.FileName} → {DriverDownloads.DownloadsDir} (запись туда разрешена только администраторам)",
            $"Файл будет проверен по цифровой подписи {p.Signer}: без действительной подписи он удаляется и не запускается",
            restorePointsEnabled
                ? "Перед установкой будет создана точка восстановления (если Windows её не создаст, MONOLITH спросит, продолжать ли)"
                : "Защита системы отключена — точка восстановления создана не будет; MONOLITH спросит, продолжать ли без неё",
            p.Vendor == GpuVendor.Amd
                ? "Откроется официальный установщик AMD: он определит видеокарту, а при желании в нём можно выбрать чистую установку (сброс к заводским настройкам)"
                : "Откроется официальный установщик NVIDIA — в нём можно выбрать «Выборочная → Чистая установка»",
            "Лицензию производителя показывает сам установщик — MONOLITH ничего не принимает за вас",
            $"Нужно свободного места на системном диске: не меньше {SystemProfile.FormatBytes(DriverDownloads.RequiredFreeSpace(p.Vendor))}",
            "MONOLITH не перезагружает компьютер: если установщик попросит перезагрузку, решаете вы",
        };
        return lines;
    }
}
