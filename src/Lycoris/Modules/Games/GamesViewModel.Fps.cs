using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Games;

/// <summary>One line of the «до / после» table.</summary>
public sealed record FpsRowItem(string Label, string Before, string After, string Change, string State);

/// <summary>
/// «ЗАМЕР FPS · ДО / ПОСЛЕ» on the Games page: PresentMon records the running game for 15/30/60 s, the result is saved as «до» or
/// «после», and the table shows the change. Read-only for the system (a measurement); needs admin rights only because ETW does.
/// </summary>
public sealed partial class GamesViewModel
{
    private const int FpsCountdownSeconds = 5;
    private readonly FpsStore _fpsStore = new();
    private CancellationTokenSource? _fpsCts;

    public ObservableCollection<string> FpsTargets { get; } = [];
    public ObservableCollection<FpsRowItem> FpsRows { get; } = [];

    [ObservableProperty] private string? _fpsTarget;
    [ObservableProperty] private int _fpsSeconds = 30;
    [ObservableProperty] private bool _presentMonReady;
    [ObservableProperty] private string _presentMonNote = "";
    [ObservableProperty] private bool _isFpsMeasuring;
    [ObservableProperty] private string _fpsProgressText = "";
    [ObservableProperty] private string _fpsBeforeText = "";
    [ObservableProperty] private string _fpsAfterText = "";
    [ObservableProperty] private string _fpsWarning = "";

    public bool FpsIdle => !IsFpsMeasuring;
    public bool HasFpsRows => FpsRows.Count > 0;
    public bool HasFpsWarning => FpsWarning.Length > 0;
    public bool ShowPresentMonInstall => !PresentMonReady && PresentMonNote.Length > 0;
    public bool HasFpsTargets => FpsTargets.Count > 0;
    public bool FpsTagReady => PresentMonReady && !IsFpsMeasuring;
    public bool FpsTagMissing => !PresentMonReady && !IsFpsMeasuring;
    public string FpsStatus => IsFpsMeasuring ? Loc.T("ИДЁТ ЗАМЕР") : PresentMonReady ? Loc.T("ГОТОВ") : Loc.T("НУЖЕН PRESENTMON");

    /// <summary>Why the buttons will first ask for rights (text, never only a tooltip).</summary>
    public string FpsAdminNote => IsElevated ? ""
        : Loc.T("PresentMon читает события Windows, поэтому нужны права администратора: кнопка замера предложит перезапуск и продолжит сама.");
    public bool HasFpsAdminNote => FpsAdminNote.Length > 0;

    public bool FpsIs15 { get => FpsSeconds == 15; set { if (value) FpsSeconds = 15; } }
    public bool FpsIs30 { get => FpsSeconds == 30; set { if (value) FpsSeconds = 30; } }
    public bool FpsIs60 { get => FpsSeconds == 60; set { if (value) FpsSeconds = 60; } }

    partial void OnFpsSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(FpsIs15));
        OnPropertyChanged(nameof(FpsIs30));
        OnPropertyChanged(nameof(FpsIs60));
    }

    partial void OnIsFpsMeasuringChanged(bool value)
    {
        OnPropertyChanged(nameof(FpsIdle));
        OnPropertyChanged(nameof(FpsStatus));
        OnPropertyChanged(nameof(FpsTagReady));
        OnPropertyChanged(nameof(FpsTagMissing));
    }

    partial void OnPresentMonReadyChanged(bool value) => NotifyPresentMon();
    partial void OnPresentMonNoteChanged(string value) => NotifyPresentMon();
    partial void OnFpsWarningChanged(string value) => OnPropertyChanged(nameof(HasFpsWarning));

    private void NotifyPresentMon()
    {
        OnPropertyChanged(nameof(ShowPresentMonInstall));
        OnPropertyChanged(nameof(FpsStatus));
        OnPropertyChanged(nameof(FpsTagReady));
        OnPropertyChanged(nameof(FpsTagMissing));
    }

    /// <summary>PresentMon state, the windowed programs to measure and the saved pair. Read-only; safe to call on every visit.</summary>
    private async Task RefreshFpsAsync()
    {
        try
        {
            var library = _all.Select(c => Path.GetFileName(c.Entry.ExePath ?? "")).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var (status, apps, pair) = await Task.Run(() => (PresentMon.Locate(), UserProcesses.ListClosable(), _fpsStore.Read()));
            PresentMonReady = status.Ready;
            PresentMonNote = status.Problem ?? "";

            var names = apps.Where(a => a.HasWindow && !a.Name.Equals("Lycoris", StringComparison.OrdinalIgnoreCase))
                .Select(a => FpsMeasure.NormalizeProcess(a.Name)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var keep = FpsTarget is { } t && names.Contains(t, StringComparer.OrdinalIgnoreCase) ? t
                : names.FirstOrDefault(library.Contains)
                ?? (pair.Before?.Process is { } p && names.Contains(p, StringComparer.OrdinalIgnoreCase) ? p : null)
                ?? names.FirstOrDefault();
            FpsTargets.Clear();
            foreach (var n in names) FpsTargets.Add(n);
            FpsTarget = keep;
            OnPropertyChanged(nameof(HasFpsTargets));
            ShowFpsPair(pair);
        }
        catch (Exception ex)
        {
            Log.Warn("Замер FPS: состояние не прочитано — {0}", ex.Message);
        }
    }

    private void ShowFpsPair(FpsPair pair)
    {
        string When(FpsRecord? r) => r is null ? Loc.T("ещё не было")
            : Loc.Tf("{0} · {1} · {2} с", r.Process, Loc.DateTimeText(r.RecordedAt.LocalDateTime), r.RequestedSeconds);
        FpsBeforeText = Loc.Tf("ДО: {0}", When(pair.Before));
        FpsAfterText = Loc.Tf("ПОСЛЕ: {0}", When(pair.After));
        FpsRows.Clear();
        FpsWarning = "";
        if (pair.Before is { } b && pair.After is { } a)
        {
            var cmp = FpsMeasure.Compare(b, a);
            foreach (var d in cmp.Rows)
                FpsRows.Add(new(d.Label, FpsMeasure.FormatValue(d.Before, d.Unit), FpsMeasure.FormatValue(d.After, d.Unit),
                    d.IsNoise ? "≈ " + FpsMeasure.FormatPercent(d.Percent) : FpsMeasure.FormatPercent(d.Percent),
                    d.IsNoise ? "noise" : d.IsBetter ? "better" : "worse"));
            FpsWarning = cmp.Warning ?? "";
        }
        else if (pair.Before is not null || pair.After is not null)
        {
            var dash = "—";
            string V(FpsRecord? x, Func<FpsStats, double> f, string unit) => x is null ? dash : FpsMeasure.FormatValue(f(x.Stats), unit);
            FpsRows.Add(new(Loc.T("СРЕДНИЙ FPS"), V(pair.Before, s => s.AvgFps, ""), V(pair.After, s => s.AvgFps, ""), "", "noise"));
            FpsRows.Add(new(Loc.T("1% LOW"), V(pair.Before, s => s.Low1Fps, ""), V(pair.After, s => s.Low1Fps, ""), "", "noise"));
            FpsRows.Add(new(Loc.T("FRAMETIME"), V(pair.Before, s => s.AvgFrameMs, Loc.T("мс")), V(pair.After, s => s.AvgFrameMs, Loc.T("мс")), "", "noise"));
        }
        OnPropertyChanged(nameof(HasFpsRows));
    }

    [RelayCommand]
    private Task RefreshFpsTargets() => RefreshFpsAsync();

    /// <summary>Opens the Intel PresentMon row of the Apps catalog (install = winget there, with its own confirmation).</summary>
    [RelayCommand]
    private void InstallPresentMon()
    {
        Ui.Navigate("apps");
        Ui.RequestFocus("apps", "app:" + PresentMon.WingetId);
    }

    [RelayCommand]
    private void CancelFps() => _fpsCts?.Cancel();

    /// <summary>«before» or «after». Confirms what will happen, counts down 5 s so the user can switch to the game, then captures.</summary>
    [RelayCommand]
    private async Task MeasureFps(string? kind)
    {
        if (IsFpsMeasuring) return;
        kind = kind == FpsRecord.AfterKind ? FpsRecord.AfterKind : FpsRecord.BeforeKind;
        var name = kind == FpsRecord.BeforeKind ? Loc.T("Замер FPS «до»") : Loc.T("Замер FPS «после»");
        if (!RequireAdmin(name, "MeasureFps", kind)) return;
        if (!PresentMonReady) await RefreshFpsAsync();
        if (!EnsurePresentMonOrExplain()) return;
        if (FpsMeasure.NormalizeProcess(FpsTarget) is not { } target)
        {
            Ui.Toast(Loc.T("Сначала запустите игру и выберите её в списке «ЧТО ЗАМЕРЯТЬ»."), ToastKind.Warning, name);
            return;
        }
        if (!CanStartNow(OperationClass.Measure, name, quiet: true)) return;
        var seconds = FpsSeconds;
        var ok = await Ui.ConfirmAsync(name, Loc.Tf("Lycoris запишет кадры {0} в течение {1} с через Intel PresentMon. Система не меняется.", target, seconds),
            [
                Loc.Tf("Через {0} с после нажатия переключитесь в игру и играйте как обычно — окно игры должно быть на экране.", FpsCountdownSeconds),
                Loc.T("Для честного сравнения делайте «до» и «после» в одном и том же месте игры с теми же настройками графики."),
                Loc.T("Результат: средний FPS, 1% low и frametime. Хранится только на этом ПК."),
            ], confirmText: Loc.T("НАЧАТЬ ЗАМЕР"));
        if (!ok) return;

        using var cts = new CancellationTokenSource();
        _fpsCts = cts;
        var lease = await AcquireLeaseAsync(OperationClass.Measure, name, cts.Cancel, quiet: true);
        if (lease is null) { _fpsCts = null; return; }
        IsFpsMeasuring = true;
        try
        {
            using (lease)
            {
                using var scope = lease.Scope();
                for (var i = FpsCountdownSeconds; i > 0; i--)
                {
                    FpsProgressText = Loc.Tf("Переключитесь в игру — замер начнётся через {0} с", i);
                    lease.Report(FpsProgressText);
                    await Task.Delay(1000, cts.Token);
                }
                var capture = PresentMon.CaptureAsync(target, seconds, cts.Token);
                var started = DateTime.UtcNow;
                while (!capture.IsCompleted)
                {
                    var done = Math.Min(seconds, (int)(DateTime.UtcNow - started).TotalSeconds);
                    FpsProgressText = done < seconds ? Loc.Tf("Идёт замер {0}: {1} из {2} с", target, done, seconds) : Loc.T("Обработка кадров…");
                    lease.Report(FpsProgressText);
                    await Task.WhenAny(capture, Task.Delay(500));
                }
                var (stats, error) = await capture;
                if (stats is null)
                {
                    Log.Warn("{0}: {1}", name, error ?? "");
                    Ui.Toast(error ?? Loc.T("Замер не удался."), ToastKind.Warning, name);
                    return;
                }
                var record = new FpsRecord { Kind = kind, Process = target, RequestedSeconds = seconds, Stats = stats };
                var pair = await Task.Run(() => _fpsStore.Save(record));
                ShowFpsPair(pair);
                Log.Success("{0}: {1}", name, FpsMeasure.Summary(pair.Before, pair.After));
                Ui.Toast(Loc.Tf("{0} FPS в среднем, 1% low — {1}.", stats.AvgFps.ToString("0", Loc.Culture), stats.Low1Fps.ToString("0", Loc.Culture)),
                    ToastKind.Success, name);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("{0}: отменён.", name);
        }
        finally
        {
            IsFpsMeasuring = false;
            FpsProgressText = "";
            _fpsCts = null;
        }
    }

    private bool EnsurePresentMonOrExplain()
    {
        if (PresentMonReady) return true;
        if (PresentMonNote.Length > 0) Ui.Toast(PresentMonNote, ToastKind.Warning, Loc.T("ЗАМЕР FPS"));
        return false;
    }
}
