using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Updates;
using Lycoris.UI;

namespace Lycoris.Shell;

/// <summary>
/// The user's side of «ОБНОВЛЕНИЕ x.y.z»: one object behind the title-bar button and the Settings → «О программе» block.
/// The logic is in Core/Updates; this class only decides what to ask the user and in which order:
/// check → button → confirmation (exactly what will happen) → download with progress and ОТМЕНА → re-check that nothing
/// runs → start the updater → close. A background check never shows an error.
/// </summary>
public sealed partial class UpdateController : ObservableObject
{
    public static UpdateController Instance { get; } = new(UpdateService.Instance);

    private readonly UpdateService _service;
    private readonly Func<UpdateDownloader> _downloaderFactory;
    private ReleaseInfo? _demo;
    private CancellationTokenSource? _download;

    internal UpdateController(UpdateService service, Func<UpdateDownloader>? downloader = null)
    {
        _service = service;
        _downloaderFactory = downloader ?? UpdateDownloader.CreateDefault;
        _service.Changed += () => Ui.Post(RaiseAll);
    }

    // ───────────────────────────── State shown by the UI ─────────────────────────────

    public string CurrentVersion => AppVersion.CurrentText;

    /// <summary>The newer release known now (a demo one in QA mode).</summary>
    public ReleaseInfo? Latest => _demo ?? _service.Available;

    public bool HasUpdate => Latest is not null;

    /// <summary>The title-bar button «ОБНОВЛЕНИЕ 1.0.1»: an update exists and was neither skipped nor postponed.</summary>
    public bool ButtonVisible => Latest is { } r && (_demo is not null || (!_service.IsDismissed(r) && !_service.IsSnoozed()));

    public string ButtonText => Loc.Tf("ОБНОВЛЕНИЕ {0}", Latest?.VersionText ?? "");

    public bool IsSkipped => Latest is { } r && _demo is null && _service.IsDismissed(r);

    public string? Notes => Latest?.NotesPreview();

    public string VersionLine => Latest is { } r
        ? Loc.Tf("УСТАНОВЛЕНА {0} · ДОСТУПНА {1}", CurrentVersion, r.VersionText)
        : Loc.Tf("УСТАНОВЛЕНА {0}", CurrentVersion);

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadText = "";
    /// <summary>Why the update cannot start right now (text next to the button, never only a tooltip); null = nothing blocks.</summary>
    [ObservableProperty] private string? _blockedReason;

    public bool IsBusy => IsChecking || IsDownloading;
    public bool CanStart => !IsBusy;

    partial void OnIsCheckingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanStart)); }
    partial void OnIsDownloadingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanStart)); }

    /// <summary>Re-reads the state (the Settings page calls it when it opens).</summary>
    public void Refresh() => RaiseAll();

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Latest));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(ButtonVisible));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(VersionLine));
        RefreshStatus();
    }

    private string? _lastFailure;

    private void RefreshStatus()
    {
        var last = SettingsStamp();
        StatusText = Latest is { } r
            ? IsSkipped
                ? Loc.Tf("Версия {0} пропущена. Обновиться можно в любой момент.", r.VersionText)
                : Loc.Tf("Доступна новая версия {0}.", r.VersionText)
            : _lastFailure is not null
                ? _lastFailure
                : last is null
                    ? (AppSettings.Current.AutoCheckUpdates ? Loc.T("Проверка ещё не выполнялась.") : Loc.T("Автоматическая проверка выключена. Нажмите «ПРОВЕРИТЬ ОБНОВЛЕНИЯ».") )
                    : Loc.Tf("Установлена последняя версия · проверено {0}", last);
    }

    private static string? SettingsStamp() =>
        SettingsUpdateStore.ParseUtc(AppSettings.Current.UpdateLastCheckUtc) is { } t ? Loc.DateTimeText(t.ToLocalTime()) : null;

    // ───────────────────────────── QA ─────────────────────────────

    /// <summary>QA only («--demo-update»): pretends 1.0.1 exists; nothing is requested, nothing can be installed.</summary>
    public void EnableDemo()
    {
        _demo = new ReleaseInfo
        {
            Tag = "v" + AppVersion.Current.Major + "." + AppVersion.Current.Minor + "." + (AppVersion.Current.Patch + 1),
            Name = "Demo",
            Notes = "## Что нового\n- Автоматические обновления: кнопка «ОБНОВЛЕНИЕ» в заголовке окна.\n- Исправлено зависание страницы «Драйверы» при отсутствии сети.\n- Улучшен перевод на английский язык.", // loc:ignore (demo text, shown as written by the owner)
            ExeUrl = "https://github.com/bil-xvii/Lycoris-releases/releases/download/demo/Lycoris.exe",
            ExeSize = 72L << 20,
        };
        StatusText = Loc.Tf("Доступна новая версия {0}.", _demo.VersionText);
        RaiseAll();
    }

    // ───────────────────────────── Checks ─────────────────────────────

    /// <summary>Called once from the main window: after ~15 s one low-priority background check (if due).</summary>
    public void ScheduleStartupCheck()
    {
        if (_demo is not null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                if (!_service.IsDue()) { _service.Reevaluate(); Ui.Post(RaiseAll); return; }
                await OperationCoordinator.Instance.RunAsync(
                    new OperationRequest(OperationClass.Background, Loc.T("Проверка обновлений")) { Owner = "updates" },
                    async lease => await _service.CheckAsync(manual: false, lease.Token).ConfigureAwait(false)).ConfigureAwait(false);
                Ui.Post(RaiseAll);
            }
            catch (Exception ex)
            {
                Log.Info("Проверка обновлений не выполнена: {0}", ex.GetType().Name);
            }
        });
    }

    /// <summary>A minute after the start: remove what an earlier update left behind (the .old copy, staged downloads).</summary>
    public void ScheduleCleanup()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1)).ConfigureAwait(false);
                UpdateCleanup.Run(AppPaths.ExecutablePath, UpdateDownloader.DefaultFolder, AppVersion.Current);
            }
            catch (Exception ex) { Log.Info("Очистка остатков обновления не выполнена: {0}", ex.GetType().Name); }
        });
    }

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (IsBusy) return;
        if (_demo is not null) { Ui.Toast("Демо-режим: проверка не выполняется.", ToastKind.Info); return; }
        IsChecking = true;
        _lastFailure = null;
        StatusText = Loc.T("Проверяем…");
        try
        {
            var result = await Task.Run(() => OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.Background, Loc.T("Проверка обновлений")) { Owner = "updates" },
                lease => _service.CheckAsync(manual: true, lease.Token)));
            switch (result.Status)
            {
                case UpdateCheckStatus.Available:
                    // An explicit request overrides an earlier «пропустить» / «напомнить позже».
                    _service.ClearReminders();
                    break;
                case UpdateCheckStatus.Failed:
                    _lastFailure = result.Message;
                    Ui.Toast(result.Message, ToastKind.Warning, "ОБНОВЛЕНИЯ");
                    break;
                default:
                    Ui.Toast("Установлена последняя версия Lycoris.", ToastKind.Success, "ОБНОВЛЕНИЯ");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Info("Проверка обновлений не выполнена: {0}", ex.GetType().Name);
            _lastFailure = Loc.T("Не удалось проверить обновления.");
        }
        finally
        {
            IsChecking = false;
            RaiseAll();
        }
    }

    [RelayCommand]
    private void RemindLater()
    {
        if (_demo is not null) { _demo = null; RaiseAll(); return; }
        _service.Snooze();
        RaiseAll();
        Ui.Toast("Напомним об обновлении через сутки. Кнопка «ОБНОВИТЬ СЕЙЧАС» остаётся в «Настройки → Обновления».", ToastKind.Info, "ОБНОВЛЕНИЯ");
    }

    [RelayCommand]
    private void SkipVersion()
    {
        if (Latest is not { } r) return;
        if (_demo is not null) { _demo = null; RaiseAll(); return; }
        _service.Dismiss(r);
        RaiseAll();
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    // ───────────────────────────── The update itself ─────────────────────────────

    /// <summary>Why an update must not start now: a running task of a conflicting class or a game session. Pure; null = free.</summary>
    public static string? BlockerText(IEnumerable<OperationInfo> operations, bool gameSessionActive)
    {
        var blocker = operations.FirstOrDefault(o => o.IsListed);
        if (blocker is not null)
            return Loc.Tf("Сейчас выполняется задача «{0}». Обновление перезапускает Lycoris и не должно прерывать её — дождитесь окончания (список «ЗАДАЧИ» внизу окна).", blocker.Name);
        if (gameSessionActive)
            return Loc.T("Идёт игровой сеанс (игровой режим Lycoris). Обновление перезапускает программу — остановите игровой сеанс на странице «Игры» и повторите.");
        return null;
    }

    private static string? CurrentBlocker() =>
        BlockerText(OperationCoordinator.Instance.Snapshot(), GameBoostState.IsActive);

    [RelayCommand]
    private async Task UpdateNowAsync()
    {
        if (IsBusy || Latest is not { } release) return;
        if (_demo is not null)
        {
            await Ui.AlertAsync("ДЕМО-РЕЖИМ", "Это демонстрация кнопки обновления: ничего не скачивается и не заменяется.");
            return;
        }

        // 1. Nothing may run: refuse with the reason as text, offer to retry.
        while (CurrentBlocker() is { } reason)
        {
            BlockedReason = reason;
            var retry = await Ui.ConfirmAsync("ОБНОВЛЕНИЕ СЕЙЧАС НЕВОЗМОЖНО", reason, confirmText: "ПОВТОРИТЬ", cancelText: "ЗАКРЫТЬ");
            if (!retry) return;
        }
        BlockedReason = null;

        // 2. Can this copy be replaced at all?
        var plan = UpdateApplier.Plan(new SystemUpdateEnvironment(), AppPaths.ExecutablePath, Elevation.IsElevated);
        if (plan.Route == UpdateRoute.Unsupported)
        {
            BlockedReason = plan.Reason;
            await Ui.AlertAsync("ОБНОВЛЕНИЕ НЕВОЗМОЖНО", plan.Reason ?? "", release.PageUrl.Length > 0 ? [release.PageUrl] : null);
            return;
        }

        // 3. Exactly what will happen.
        var lines = new List<string>
        {
            Loc.Tf("Версия: {0} → {1}", CurrentVersion, release.VersionText),
            Loc.Tf("Размер: {0}", release.ExeSize > 0 ? SystemProfile.FormatBytes(release.ExeSize) : Loc.T("неизвестен")),
            Loc.Tf("Источник: github.com/{0} (только этот адрес)", UpdateService.Repository),
            Loc.T("Проверка: контрольная сумма SHA-256, формат файла и (если файл подписан) подпись того же издателя"),
            Loc.T("Lycoris закроется и откроется заново сам; настройки и журнал изменений сохраняются"),
            Loc.T("Запущенные задачи не прерываются: обновление начнётся, только когда их нет"),
            Loc.T("Прежний Lycoris.exe хранится рядом как Lycoris.exe.old; при сбое запуска новой версии он возвращается автоматически"),
        };
        if (plan.Route == UpdateRoute.Elevated)
            lines.Add(Loc.T("Папка программы защищена: Windows запросит разрешение администратора (UAC) на замену файла"));
        var ok = await Ui.ConfirmAsync("ОБНОВИТЬ Lycoris?",
            Loc.Tf("Будет установлена версия {0} с официальной страницы релизов.", release.VersionText),
            lines, confirmText: "СКАЧАТЬ И ОБНОВИТЬ");
        if (!ok) return;

        // 4. Download and verify (low priority, no dialog while it runs; ОТМЕНА is next to the progress).
        StagedUpdate staged;
        using (_download = new CancellationTokenSource())
        {
            IsDownloading = true;
            DownloadPercent = 0;
            DownloadText = Loc.T("Подключение…");
            try
            {
                var progress = new Progress<DownloadProgress>(p =>
                {
                    DownloadPercent = p.Percent;
                    DownloadText = VendorDownloads.FormatProgress(p);
                });
                var token = _download.Token;
                staged = await OperationCoordinator.Instance.RunAsync(
                    new OperationRequest(OperationClass.Background, Loc.T("Загрузка обновления Lycoris")) { Owner = "updates" },
                    _ => _downloaderFactory().DownloadAsync(release, UpdateDownloader.DefaultFolder, progress, token), token);
            }
            catch (OperationCanceledException)
            {
                Log.Info("Загрузка обновления отменена пользователем.");
                Ui.Toast("Загрузка обновления отменена. Ничего не изменено.", ToastKind.Info, "ОБНОВЛЕНИЕ");
                return;
            }
            catch (UpdateRefusedException ex)
            {
                Log.Warn("Обновление отклонено: {0}", ex.Message);
                await Ui.AlertAsync("ОБНОВЛЕНИЕ ОТМЕНЕНО", ex.Message + "\n\n" + Loc.T("Установленная версия не затронута."));
                return;
            }
            catch (InvalidOperationException ex)
            {
                Log.Warn("Загрузка обновления не удалась: {0}", ex.Message);
                await Ui.AlertAsync("НЕ УДАЛОСЬ СКАЧАТЬ ОБНОВЛЕНИЕ", ex.Message + "\n\n" + Loc.T("Установленная версия не затронута."));
                return;
            }
            finally
            {
                IsDownloading = false;
                _download = null;
            }
        }

        // 5. Something may have started meanwhile: the same rule, once more, right before the restart.
        while (CurrentBlocker() is { } reason)
        {
            BlockedReason = reason;
            var retry = await Ui.ConfirmAsync("ОБНОВЛЕНИЕ СКАЧАНО, НО ЖДЁТ", reason + "\n\n" + Loc.T("Файл уже проверен и сохранён."), confirmText: "ПОВТОРИТЬ", cancelText: "ПОЗЖЕ");
            if (!retry) return;
        }
        BlockedReason = null;

        // 6. Hand over to the new exe and close this copy normally (journal saved in App.OnExit).
        var launch = await UpdateLauncher.StartAsync(plan, staged, Environment.ProcessId);
        if (!launch.Started)
        {
            await Ui.AlertAsync("ОБНОВЛЕНИЕ НЕ ЗАПУЩЕНО", (launch.Error ?? "") + "\n\n" + Loc.T("Установленная версия не затронута."));
            return;
        }
        Log.Info("Обновление до {0}: прежняя копия закрывается.", release.VersionText);
        Application.Current.Shutdown();
    }
}
