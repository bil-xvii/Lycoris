using System.Globalization;

namespace Lycoris.Modules.Devices.Input;

/// <summary>
/// The two mouse measurements, shown inline in their rows (one row per measurement). They start only from their button,
/// listen to raw input for a few seconds through <see cref="RawInputSession"/> and change nothing. The result is kept
/// for the app session and fed back into the mouse card (DPI row, polling row, pointer-speed suggestion) on the UI thread.
/// </summary>
internal static class InputTools
{
    private static readonly DeviceOption Ruler = new(DeviceValue.Of(1000), Loc.T("10 см по линейке"));
    private static readonly DeviceOption Card = new(DeviceValue.Of(856), Loc.T("банковская карта 85,6 мм"));

    /// <summary>«Проверка DPI»: three drags over a known distance on the pad (the pad captures the mouse; nothing else reacts).</summary>
    public static DeviceTool Dpi() => new()
    {
        Id = InputProvider.DpiTool,
        Title = Loc.T("Проверка DPI"),
        Description = Loc.T("Нужна линейка или банковская карта: три раза проведите мышь ровно вправо на выбранное расстояние."),
        ButtonText = Loc.T("ПРОВЕРИТЬ"),
        Options = [Ruler, Card],
        SelectedOption = Ruler,
        NeedsDragPad = true,
        PadText = Loc.T("Нажмите здесь левую кнопку и, не отпуская, проведите мышь вправо на всё расстояние — затем отпустите"),
        Run = RunDpiAsync,
    };

    /// <summary>«Частота опроса»: 5 s of fast circles while Lycoris is in the foreground.</summary>
    public static DeviceTool Polling() => new()
    {
        Id = InputProvider.PollingTool,
        Title = Loc.T("Измерение частоты опроса"),
        Description = Loc.T("После нажатия 5 секунд водите мышью быстрыми кругами. Окно Lycoris должно оставаться активным."),
        ButtonText = Loc.T("ИЗМЕРИТЬ"),
        Run = RunPollingAsync,
    };

    // ───────────────────────────── DPI ─────────────────────────────

    private static string DistanceText(double cm) => Math.Abs(cm - DpiEstimator.RulerCm) < 0.01 ? Loc.T("10 см") : Loc.T("85,6 мм (длинная сторона карты)");

    private static string Prompt(int pass, double cm) =>
        Loc.Tf("Проход {0} из {1}: нажмите в рамке ниже и, не отпуская, проведите мышь ровно вправо на {2}.", pass, DpiEstimator.RunsNeeded, DistanceText(cm));

    private static async Task RunDpiAsync(DeviceToolRun run, CancellationToken ct)
    {
        var option = run.Tool.SelectedOption ?? Ruler;
        var cm = option.Value.Number / 100.0;
        var inventory = await Task.Run(InputDevices.Detect, ct);
        var mice = inventory.MouseHandles;

        var valid = new List<double>();
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var gate = new RawDragGate();
        void OnPad(bool held)
        {
            if (held) gate.Press();
            else gate.Release();
        }

        run.Report(0, Prompt(1, cm));
        run.Tool.PadChanged += OnPad;
        RawCapture capture;
        try
        {
            capture = await RawInputSession.CaptureAsync(RawCaptureMode.Drag, null, finished.Token, gate, counts =>
            {
                // Runs on the capture thread: pick the physical mouse that moved the most, ignore virtual ones.
                var moved = counts.Where(c => mice.Count == 0 || mice.Contains(c.Key)).Select(c => c.Value)
                    .OrderByDescending(c => c.Reports).FirstOrDefault();
                var pass = new DpiRun(moved.X, moved.Y, cm);
                var problem = DpiEstimator.RunProblem(pass);
                int done;
                lock (valid)
                {
                    if (problem is null) valid.Add(pass.Dpi);
                    done = valid.Count;
                }
                if (done >= DpiEstimator.RunsNeeded)
                {
                    try { finished.Cancel(); }
                    catch (ObjectDisposedException) { }
                    return;
                }
                run.Report(done / (double)DpiEstimator.RunsNeeded, problem is null
                    ? Loc.Tf("Засчитано: {0}. {1}", InputAdvice.DpiText(pass.Dpi), Prompt(done + 1, cm))
                    : $"{problem} {Prompt(done + 1, cm)}");
            });
        }
        finally
        {
            run.Tool.PadChanged -= OnPad;
        }
        ct.ThrowIfCancellationRequested();

        List<double> runs;
        lock (valid) runs = [.. valid];
        if (capture.Error is { } error && runs.Count < DpiEstimator.RunsNeeded)
        {
            run.Fail(error);
            return;
        }
        if (runs.Count < DpiEstimator.RunsNeeded)
        {
            run.Fail(capture.Interrupted
                ? Loc.T("Окно Lycoris стало неактивным — проверка остановлена. Нажмите «ПРОВЕРИТЬ» ещё раз.")
                : Loc.Tf("Время вышло: засчитано проходов — {0} из {1}. Нажмите «ПРОВЕРИТЬ» ещё раз.", runs.Count, DpiEstimator.RunsNeeded));
            return;
        }

        var estimate = DpiEstimator.Combine(runs)!;
        run.Results.Set(InputProvider.DpiKey, new DpiMeasurement(estimate.Dpi, estimate.SpreadPercent, cm, estimate.Runs));
        var width = SpiWriter.ScreenWidth();
        var speed = run.Card.Settings.FirstOrDefault(s => s.Id == "mouse-speed")?.Current is { } sp ? (int)sp.Number : InputAdvice.NeutralSpeed;
        DeviceUi.Post(() => InputProvider.RefreshMeasured(run.Card, run.Results, width));

        var detail = new List<string> { DpiEstimator.RunsText(estimate) };
        if (InputAdvice.TravelText(width, estimate.Dpi, InputAdvice.PointerMultiplier(speed)) is { } travel)
            detail.Add(speed == InputAdvice.NeutralSpeed ? travel : Loc.Tf("{0} при скорости указателя {1}", travel, speed.ToString(CultureInfo.InvariantCulture)));
        if (estimate.Inconsistent) detail.Add(Loc.T("Проходы заметно расходятся — проведите мышь ровно вдоль линейки и проверьте ещё раз."));
        run.Complete(DpiEstimator.ResultText(estimate), string.Join(" · ", detail));
    }

    // ───────────────────────────── Polling rate ─────────────────────────────

    private static async Task RunPollingAsync(DeviceToolRun run, CancellationToken ct)
    {
        var inventory = await Task.Run(InputDevices.Detect, ct);
        run.Report(0, Loc.T("Начните водить мышью быстрыми кругами."));
        var capture = await RawInputSession.CaptureAsync(RawCaptureMode.Polling, p =>
        {
            if (!p.Moving) return;
            var left = Math.Max(0, (int)Math.Ceiling(RawInputSession.PollingWindow.TotalSeconds * (1 - p.Fraction)));
            run.Report(p.Fraction, Loc.Tf("Водите мышью быстрыми кругами… ещё {0} с", left.ToString(CultureInfo.InvariantCulture)));
        }, ct);
        ct.ThrowIfCancellationRequested();

        if (capture.Error is { } error)
        {
            run.Fail(error);
            return;
        }
        if (capture.Interrupted)
        {
            run.Fail(Loc.T("Окно Lycoris стало неактивным — измерение остановлено. Нажмите «ИЗМЕРИТЬ» и не переключайтесь на другие окна."));
            return;
        }

        var mice = inventory.MouseHandles;
        var devices = capture.Devices.Where(d => mice.Count == 0 || mice.Contains(d.Key)).ToDictionary(d => d.Key, d => d.Value);
        if (PollingRateEstimator.EstimateBusiest(devices, capture.Frequency) is not { } best)
        {
            run.Fail(capture.MostlyBackground
                ? Loc.T("Окно Lycoris было неактивным — Windows не передаёт ему движения мыши. Нажмите «ИЗМЕРИТЬ» ещё раз.")
                : Loc.T("Движения мыши не было. Нажмите «ИЗМЕРИТЬ» и сразу начните водить мышью кругами."));
            return;
        }
        var estimate = best.Estimate;
        if (!estimate.EnoughMotion)
        {
            run.Fail(PollingRateEstimator.ResultText(estimate));
            return;
        }

        var label = inventory.MouseByHandle(best.Device)?.Label;
        run.Results.Set(InputProvider.PollingKey, new PollingMeasurement(estimate.Hz, estimate.RawHz, estimate.Stability, label));
        var width = SpiWriter.ScreenWidth();
        DeviceUi.Post(() => InputProvider.RefreshMeasured(run.Card, run.Results, width));

        var detail = new List<string>();
        if (label is not null) detail.Add(label);
        detail.Add(InputAdvice.PollingAdvice(estimate.Hz));
        if (estimate.Saturated) detail.Add(PollingRateEstimator.SaturationHint);
        if (capture.MostlyBackground) detail.Add(Loc.T("Часть движений пришла, когда окно Lycoris было неактивным, — повторите, не переключая окна."));
        run.Complete(PollingRateEstimator.ResultText(estimate), string.Join(" · ", detail));
    }
}
