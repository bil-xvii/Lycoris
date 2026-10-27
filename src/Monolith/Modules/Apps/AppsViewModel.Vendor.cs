using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Apps;

/// <summary>
/// The safe way forward when winget cannot install a package (CLAUDE.md rules 3, 8, 9): MONOLITH never bypasses the
/// winget hash. Instead it downloads the vendor's own installer through <see cref="VendorDownloads"/> (HTTPS host
/// allow-list, valid signature of the expected vendor, admin-only folder), shows exactly what will happen first,
/// and starts the installer as the signed-in user — the installer asks for administrator rights itself.
/// Packages without vendor data get the official website, opened as the signed-in user.
/// </summary>
public sealed partial class AppsViewModel
{
    private CancellationTokenSource? _vendorCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartQueue), nameof(CanCloseQueue))]
    private bool _isVendorDownloadRunning;

    partial void OnIsVendorDownloadRunningChanged(bool value) => UpdateBusyItems();

    /// <summary>The button of a finished queue row: vendor download, official site, or cancel the running download.</summary>
    [RelayCommand(CanExecute = nameof(CanRunFallback), AllowConcurrentExecutions = true)]
    private async Task RunFallback(QueueItemViewModel? job)
    {
        if (job is null) return;
        if (job.IsDownloading)
        {
            _vendorCts?.Cancel();
            return;
        }
        switch (job.Fallback)
        {
            case QueueFallback.Website:
                await OpenLinkAsync(job.App.Entry.Website);
                return;
            case QueueFallback.VendorDownload:
                await DownloadFromVendorAsync(job.App, job, afterHashMismatch: true);
                return;
        }
    }

    private bool CanRunFallback(QueueItemViewModel? job) =>
        job is not null && (job.IsDownloading || job.Fallback == QueueFallback.Website || (!IsQueueRunning && !IsVendorDownloadRunning));

    /// <summary>
    /// Downloads the vendor's installer for <paramref name="item"/>, verifies its signature and starts it as the
    /// signed-in user. <paramref name="job"/> is the failed queue row (null when started from the card: the download
    /// then gets its own one-row queue panel, so progress, log and result appear in the usual place).
    /// </summary>
    private async Task DownloadFromVendorAsync(AppItemViewModel item, QueueItemViewModel? job, bool afterHashMismatch)
    {
        var entry = item.Entry;
        if (IsQueueRunning || IsVendorDownloadRunning)
        {
            Ui.Toast("Дождитесь окончания текущей установки или загрузки.", ToastKind.Warning);
            return;
        }
        if (!Elevation.IsElevated)
        {
            // Without admin rights the file cannot be kept where nobody can swap it between the check and the start.
            Ui.Toast("Без прав администратора MONOLITH не может сохранить установщик в защищённую папку и проверить его до запуска — " +
                     "открывается официальный сайт.", ToastKind.Warning, entry.Name.ToUpperInvariant());
            await OpenLinkAsync(entry.Website);
            return;
        }
        if (entry.VendorInstaller?.ToRequest(entry.Name) is not { } request)
        {
            await OpenLinkAsync(entry.Website);
            return;
        }

        var restore = AppSettings.Current.CreateRestorePoints && await Task.Run(RestorePointService.IsEnabled);
        var confirmed = await Ui.ConfirmAsync(
            $"СКАЧАТЬ {entry.Name.ToUpperInvariant()} У ПРОИЗВОДИТЕЛЯ?",
            AppTexts.VendorLead(entry, afterHashMismatch),
            AppTexts.VendorPlan(request, restore),
            confirmText: "СКАЧАТЬ И ЗАПУСТИТЬ", cancelText: "ОТМЕНА",
            footnote: "Если подпись не подтвердится, файл будет удалён и не запустится.");
        if (!confirmed || IsQueueRunning || IsVendorDownloadRunning) return;

        if (job is null)
        {
            foreach (var old in Queue) if (ReferenceEquals(old.App.Job, old)) old.App.Job = null;
            Queue.Clear();
            QueueLog.Clear();
            job = new QueueItemViewModel(item, QueueOperation.Install);
            Queue.Add(job);
            HasQueue = true;
            QueueTitle = "ЗАГРУЗКА У ПРОИЗВОДИТЕЛЯ";
            QueueCounter = "1 / 1";
        }
        item.Job = job;

        _vendorCts = new CancellationTokenSource();
        var ct = _vendorCts.Token;
        job.Detail = $"Официальный установщик с {request.Url.Host}; подпись должна быть от {string.Join(" или ", request.Signers)}.";
        job.IsDownloading = true;
        job.State = QueueState.Running;
        job.StatusText = "ЗАГРУЗКА…";
        IsVendorDownloadRunning = true;
        QueueProgress = 0;
        AppendLog("");
        AppendLog($"▌ {entry.Name} · установщик производителя · {request.Url.Host}");
        Log.Info($"{entry.Name}: загрузка официального установщика с {request.Url.Host}…");
        BeginBusy($"{entry.Name}: загрузка установщика…");
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                job.StatusText = p.Verifying ? "ПРОВЕРКА ПОДПИСИ…" : $"ЗАГРУЗКА {VendorDownloads.FormatProgress(p)}";
                QueueProgress = p.Verifying ? 100 : p.Percent;
                SetBusyText($"{entry.Name}: {VendorDownloads.FormatProgress(p)}");
            });
            var path = await Task.Run(() => VendorDownloads.DownloadAsync(request, progress, ct), CancellationToken.None);
            AppendLog($"Скачано и проверено: {path}");

            if (restore && !await CreateRestorePointAsync(entry))
            {
                job.State = QueueState.Cancelled;
                job.StatusText = "ОТМЕНЕНО: нет точки восстановления";
                job.Detail = $"Установщик скачан и проверен, но не запущен. Он сохранён в {Path.GetDirectoryName(path)} — повторно скачивать не придётся.";
                job.Fallback = QueueFallback.VendorDownload;
                return;
            }

            // The file sat in the admin-only folder; check it once more right before it is started.
            job.StatusText = "ПРОВЕРКА ПОДПИСИ…";
            var (valid, signer) = await VendorDownloads.VerifySignatureAsync(path, request.Signers);
            if (!valid || !VendorDownloads.IsDirectlyInFolder(path, request.Folder))
            {
                VendorDownloads.TryDelete(path);
                throw new InvalidOperationException($"Подпись установщика не подтверждена перед запуском ({signer}) — файл удалён, запуск отменён.");
            }

            // Never with MONOLITH's admin token: the vendor installer asks for the rights it needs itself.
            if (await UnelevatedShell.StartAsUserAsync(path, "", Path.GetDirectoryName(path)))
            {
                job.Detail = $"Официальный установщик проверен ({signer}) и запущен от вашего имени — права администратора он попросит сам. " +
                             "Пройдите его шаги, затем нажмите «ПРОВЕРИТЬ» вверху страницы, чтобы MONOLITH увидел программу.";
                job.Fallback = QueueFallback.None;
                job.State = QueueState.Unknown;
                job.StatusText = "ЗАПУЩЕН УСТАНОВЩИК ПРОИЗВОДИТЕЛЯ";
                Log.Success($"{entry.Name}: официальный установщик запущен от имени пользователя ({signer}).");
                Ui.Toast($"Установщик {entry.Name} проверен и запущен. Пройдите его шаги.", ToastKind.Success, entry.Name.ToUpperInvariant());
            }
            else
            {
                job.Detail = $"Установщик скачан и проверен ({signer}), но запустить его от вашего имени не удалось (рабочий стол Windows недоступен). " +
                             $"Запустите вручную файл {path} или скачайте программу с официального сайта.";
                job.Fallback = QueueFallback.Website;
                job.State = QueueState.Failed;
                job.StatusText = "ОШИБКА: установщик не запущен";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Detail = "Загрузка прервана, недокачанный файл удалён. Её можно повторить.";
            job.Fallback = QueueFallback.VendorDownload;
            job.State = QueueState.Cancelled;
            job.StatusText = "ЗАГРУЗКА ОТМЕНЕНА";
            Log.Info($"{entry.Name}: загрузка установщика отменена.");
        }
        catch (Exception ex)
        {
            job.Detail = $"{ex.Message} Можно скачать программу с официального сайта.";
            job.Fallback = QueueFallback.Website;
            job.State = QueueState.Failed;
            job.StatusText = "ОШИБКА: установщик производителя не скачан";
            Log.Error($"{entry.Name}: загрузка установщика у производителя", ex);
        }
        finally
        {
            job.IsDownloading = false;
            if (job.Detail is { } detail) AppendLog(detail);
            QueueProgress = 100;
            _vendorCts?.Dispose();
            _vendorCts = null;
            IsVendorDownloadRunning = false;
            EndBusy();
        }
    }

    /// <summary>Restore point before an installer runs; false when it failed and the user chose not to continue.</summary>
    private async Task<bool> CreateRestorePointAsync(CatalogEntry entry)
    {
        AppendLog("Создание точки восстановления…");
        SetBusyText("Создание точки восстановления…");
        var rp = await RestorePointService.CreateAsync($"MONOLITH: установка {entry.Name}");
        AppendLog(rp.Message);
        return rp.Outcome != RestorePointOutcome.Failed || await Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            $"{rp.Message} Без неё откатить установку можно будет только удалением программы в «Параметры → Приложения». Продолжить?",
            confirmText: "ПРОДОЛЖИТЬ", cancelText: "ОТМЕНИТЬ", danger: true);
    }
}
