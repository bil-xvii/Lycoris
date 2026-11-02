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
                // Website-only entries (LatencyMon…) never went through winget: no "winget refused" lead for them.
                await DownloadFromVendorAsync(job.App, job, afterHashMismatch: !job.App.Entry.WebsiteOnly);
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
        // Started from the card: the ПОКА НЕ НУЖНО / НЕ НУЖНО reason goes first, as in the winget confirmation (a queue row
        // already went through it). The download is never blocked.
        var notes = job is null ? AppTexts.AdviceNotes([(entry, item.ShownAdvice)]) : [];
        var plan = notes.ToList();
        plan.AddRange(AppTexts.VendorPlan(request, restore));
        var confirmed = await Ui.ConfirmAsync(
            Loc.Tf("СКАЧАТЬ {0} У ПРОИЗВОДИТЕЛЯ?", entry.Name.ToUpperInvariant()),
            AppTexts.AdviceLead(notes.Count) + AppTexts.VendorLead(entry, afterHashMismatch),
            plan,
            confirmText: Loc.T("СКАЧАТЬ И ЗАПУСТИТЬ"), cancelText: Loc.T("ОТМЕНА"),
            footnote: Loc.T("Если подпись не подтвердится, файл будет удалён и не запустится."));
        if (!confirmed || IsQueueRunning || IsVendorDownloadRunning || _installWaiting) return;

        // One installer at a time across the whole app: waits in the visible queue behind a running winget queue or driver install.
        // The lease is released in the finally below; the signed installer, once started for the user, runs on its own.
        var lease = await AcquireInstallAsync(Loc.Tf("Загрузка установщика {0}", entry.Name), () => Ui.Post(() => _vendorCts?.Cancel()));
        if (lease is null) return;
        var scope = lease.Scope();
        CurrentLease = lease;

        if (job is null)
        {
            foreach (var old in Queue) if (ReferenceEquals(old.App.Job, old)) old.App.Job = null;
            Queue.Clear();
            QueueLog.Clear();
            job = new QueueItemViewModel(item, QueueOperation.Install);
            Queue.Add(job);
            HasQueue = true;
            QueueTitle = Loc.T("ЗАГРУЗКА У ПРОИЗВОДИТЕЛЯ");
            QueueCounter = "1 / 1";
        }
        item.Job = job;

        _vendorCts = new CancellationTokenSource();
        var ct = _vendorCts.Token;
        job.Detail = Loc.Tf("Официальный установщик с {0}; подпись должна быть от {1}.", request.Url.Host, string.Join(Loc.T(" или "), request.Signers));
        job.IsDownloading = true;
        job.State = QueueState.Running;
        job.StatusText = Loc.T("ЗАГРУЗКА…");
        IsVendorDownloadRunning = true;
        QueueProgress = 0;
        AppendLog("");
        AppendLog(Loc.Tf("▌ {0} · установщик производителя · {1}", entry.Name, request.Url.Host));
        Log.Info("{0}: загрузка официального установщика с {1}…", entry.Name, request.Url.Host);
        BeginBusy(Loc.Tf("{0}: загрузка установщика…", entry.Name));
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                job.StatusText = p.Verifying ? Loc.T("ПРОВЕРКА ПОДПИСИ…") : Loc.Tf("ЗАГРУЗКА {0}", VendorDownloads.FormatProgress(p));
                QueueProgress = p.Verifying ? 100 : p.Percent;
                SetBusyText($"{entry.Name}: {VendorDownloads.FormatProgress(p)}");
            });
            var path = await Task.Run(() => VendorDownloads.DownloadAsync(request, progress, ct), CancellationToken.None);
            // ОТМЕНА must still stop everything after the transfer: signature check, restore point and the launch.
            ct.ThrowIfCancellationRequested();
            AppendLog(Loc.Tf("Скачано и проверено: {0}", path));

            if (restore && !await CreateRestorePointAsync(entry))
            {
                job.State = QueueState.Cancelled;
                job.StatusText = Loc.T("ОТМЕНЕНО: нет точки восстановления");
                job.Detail = Loc.T("Установщик скачан и проверен, но не запущен: вы не стали продолжать без точки восстановления. При повторе MONOLITH скачает свежую копию.");
                job.Fallback = QueueFallback.VendorDownload;
                return;
            }

            ct.ThrowIfCancellationRequested();

            // The file sat in the admin-only folder; check it once more right before it is started.
            job.StatusText = Loc.T("ПРОВЕРКА ПОДПИСИ…");
            var (valid, signer) = await VendorDownloads.VerifySignatureAsync(path, request.Signers);
            if (!valid || !VendorDownloads.IsDirectlyInFolder(path, request.Folder))
            {
                VendorDownloads.TryDelete(path);
                throw new InvalidOperationException(Loc.Tf("Подпись установщика не подтверждена перед запуском ({0}) — файл удалён, запуск отменён.", signer));
            }

            ct.ThrowIfCancellationRequested();
            job.IsDownloading = false; // from here on nothing can be cancelled any more
            // Never with MONOLITH's admin token: the vendor installer asks for the rights it needs itself.
            if (await UnelevatedShell.StartAsUserAsync(path, "", Path.GetDirectoryName(path)))
            {
                job.Detail = Loc.Tf("Официальный установщик проверен ({0}) и запущен от вашего имени — права администратора он попросит сам. " +
                             "Пройдите его шаги, затем нажмите «ПРОВЕРИТЬ» вверху страницы, чтобы MONOLITH увидел программу.", signer);
                job.Fallback = QueueFallback.None;
                job.State = QueueState.Unknown;
                job.StatusText = Loc.T("ЗАПУЩЕН УСТАНОВЩИК ПРОИЗВОДИТЕЛЯ");
                Log.Success("{0}: официальный установщик запущен от имени пользователя ({1}).", entry.Name, signer);
                Ui.Toast(Loc.Tf("Установщик {0} проверен и запущен. Пройдите его шаги.", entry.Name), ToastKind.Success, entry.Name.ToUpperInvariant());
            }
            else
            {
                job.Detail = Loc.Tf("Установщик скачан и проверен ({0}), но запустить его от вашего имени не удалось (рабочий стол Windows недоступен). " +
                             "Запустите вручную файл {1} или скачайте программу с официального сайта.", signer, path);
                job.Fallback = QueueFallback.Website;
                job.State = QueueState.Failed;
                job.StatusText = Loc.T("ОШИБКА: установщик не запущен");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Detail = Loc.T("Загрузка прервана, установщик не запущен (недокачанный файл удалён). Её можно повторить.");
            job.Fallback = QueueFallback.VendorDownload;
            job.State = QueueState.Cancelled;
            job.StatusText = Loc.T("ЗАГРУЗКА ОТМЕНЕНА");
            Log.Info("{0}: загрузка установщика отменена.", entry.Name);
        }
        catch (Exception ex)
        {
            job.Detail = Loc.Tf("{0} Можно скачать программу с официального сайта.", ex.Message);
            job.Fallback = QueueFallback.Website;
            job.State = QueueState.Failed;
            job.StatusText = Loc.T("ОШИБКА: установщик производителя не скачан");
            Log.Error("{0}: загрузка установщика у производителя", ex, entry.Name);
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
            CurrentLease = null;
            scope.Dispose();
            lease.Dispose();
        }
    }

    /// <summary>Restore point before an installer runs; false when it failed and the user chose not to continue.</summary>
    private async Task<bool> CreateRestorePointAsync(CatalogEntry entry)
    {
        AppendLog(Loc.T("Создание точки восстановления…"));
        SetBusyText(Loc.T("Создание точки восстановления…"));
        var rp = await RestorePointService.CreateAsync(Loc.Tf("MONOLITH: установка {0}", entry.Name));
        AppendLog(rp.Message);
        return rp.Outcome != RestorePointOutcome.Failed || await AskWithoutLeaseAsync(() => Ui.ConfirmAsync(
            "ТОЧКА ВОССТАНОВЛЕНИЯ НЕ СОЗДАНА",
            Loc.Tf("{0} Без неё откатить установку можно будет только удалением программы в «Параметры → Приложения». Продолжить?", rp.Message),
            confirmText: Loc.T("ПРОДОЛЖИТЬ"), cancelText: Loc.T("ОТМЕНИТЬ"), danger: true));
    }
}
