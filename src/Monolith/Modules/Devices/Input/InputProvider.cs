using System.Globalization;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Tweaks;
using Monolith.UI;

namespace Monolith.Modules.Devices.Input;

/// <summary>The Windows input state the cards need besides the SPI parameters (read-only).</summary>
internal sealed record InputSystemState(bool? AccelerationOn, bool StickyKeysOn, bool FilterKeysOn, int FilterDelayMs, int FilterWaitMs,
    bool HorizontalWheel, int ScreenWidthPx)
{
    public static readonly InputSystemState Unknown = new(null, false, false, 0, 0, false, 0);

    public static InputSystemState Read()
    {
        var filterOn = SpiWriter.ReadFilterKeysOn();
        return new InputSystemState(SpiWriter.ReadMouseAcceleration(), SpiWriter.ReadStickyKeysOn(), filterOn,
            filterOn ? SpiWriter.FilterKeysDelayMs() : 0, filterOn ? SpiWriter.FilterKeysWaitMs() : 0,
            SpiWriter.HasHorizontalWheel(), SpiWriter.ScreenWidth());
    }
}

/// <summary>A DPI test result kept for the app session («mouse:dpi»).</summary>
internal sealed record DpiMeasurement(double Dpi, double SpreadPercent, double DistanceCm, IReadOnlyList<double> Runs);

/// <summary>A polling-rate result kept for the app session («mouse:polling»).</summary>
internal sealed record PollingMeasurement(int? Hz, double RawHz, double Stability, string? DeviceLabel);

/// <summary>
/// The mouse and keyboard cards (research devices_mouse-keyboard.md, design §3.1–§3.2): every per-user SystemParametersInfo
/// parameter of <see cref="SpiCatalog"/> with its control and recommendation, the two Optimize tweaks as read-only rows
/// (applied from the plan through TweakEngine), and the DPI / polling-rate measurements inline in their own rows.
/// DPI and polling rate live in the mouse's firmware: MONOLITH measures and advises, it never writes them.
/// Runs on a worker thread; reads only.
/// </summary>
internal sealed class InputProvider : IDeviceProvider
{
    public const string MouseCard = "mouse";
    public const string KeyboardCard = "keyboard";
    public const string DpiRow = "mouse-dpi";
    public const string PollingRow = "mouse-polling";
    public const string DpiTool = "mouse-dpi-test";
    public const string PollingTool = "mouse-polling-test";
    public const string DpiKey = "mouse:dpi";
    public const string PollingKey = "mouse:polling";
    public const string AccelerationTweak = "mouse-acceleration-off";
    public const string HotkeysTweak = "accessibility-hotkeys-off";
    public static string PendingSignOut => Loc.T("ОЖИДАЕТ ВЫХОДА ИЗ СИСТЕМЫ");

    /// <summary>«ПРОВЕРИТЬ DPI» — scrolls to the DPI test (a plan row that needs the measurement).</summary>
    public static readonly DeviceLink DpiToolLink = new(DeviceLinkKind.Tool, Loc.T("ПРОВЕРИТЬ DPI"), MouseCard, DpiTool);

    /// <summary>DPI and polling rate are changed only in the mouse's own program.</summary>
    public static readonly DeviceLink MouseAppLink = new(DeviceLinkKind.VendorApp, Loc.T("ОТКРЫТЬ ПРОГРАММУ МЫШИ"), MouseCard);

    private readonly Func<InputInventory> _devices;
    private readonly Func<InputSystemState> _system;

    public InputProvider() : this(InputDevices.Detect, InputSystemState.Read) { }

    /// <summary>Tests pass fake devices and system state (no enumeration of the real PC).</summary>
    internal InputProvider(Func<InputInventory> devices, Func<InputSystemState> system)
    {
        _devices = devices;
        _system = system;
    }

    public string Name => Loc.T("мышь и клавиатура");

    public Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct) => Task.Run(() => Build(ctx), ct);

    internal IReadOnlyList<DeviceCard> Build(DeviceScanContext ctx)
    {
        InputInventory inventory;
        try { inventory = _devices(); }
        catch (Exception ex)
        {
            Log.Warn("Мышь и клавиатура: устройства не прочитаны — {0}", ex.Message);
            inventory = InputInventory.Empty;
        }
        InputSystemState system;
        try { system = _system(); }
        catch (Exception ex)
        {
            Log.Warn("Мышь и клавиатура: состояние ввода не прочитано — {0}", ex.Message);
            system = InputSystemState.Unknown;
        }
        var spi = DeviceWriters.Spi;
        return [BuildMouse(ctx, spi, inventory, system), BuildKeyboard(ctx, spi, inventory, system)];
    }

    // ───────────────────────────── Mouse ─────────────────────────────

    private static DeviceCard BuildMouse(DeviceScanContext ctx, ISpiWriter? spi, InputInventory inv, InputSystemState sys)
    {
        var rows = new List<DeviceSetting>();
        var speed = SpiRow(ctx, spi, "mouse-speed", 10, SettingControl.Slider,
            Loc.T("Во сколько раз курсор быстрее руки; 10 — без пропуска пикселей. Игры с сырым вводом её не учитывают"),
            min: 1, max: 20, @default: InputAdvice.NeutralSpeed, format: v => InputAdvice.SpeedText(v.Number),
            confirm: v => v <= InputAdvice.LowSpeedMax ? LowSpeedConfirm : null,
            manualHint: Loc.T("Вернуть вручную: Параметры → Bluetooth и устройства → Мышь → Скорость указателя мыши."));
        rows.Add(speed);

        var epp = DeviceSetting.ForTweak(AccelerationTweak, Loc.T("Повышенная точность указателя"),
            Loc.T("Ускорение: при быстром движении курсор уходит дальше. Без него он движется строго пропорционально руке"),
            order: 20, format: v => v.Number != 0 ? Loc.T("выкл") : Loc.T("вкл"));
        epp.StateTag = AccelerationPending(sys) ? PendingSignOut : null;
        rows.Add(epp);

        rows.Add(new DeviceSetting
        {
            Id = DpiRow, Title = Loc.T("DPI мыши"), Order = 30, Control = SettingControl.Info,
            Hint = Loc.T("Сколько точек мышь сообщает на дюйм движения. Обычно удобно 800–1600 DPI; меняется в программе мыши"),
            Format = v => InputAdvice.DpiText(v.Number), Tool = InputTools.Dpi(),
        });
        rows.Add(new DeviceSetting
        {
            Id = PollingRow, Title = Loc.T("Частота опроса"), Order = 40, Control = SettingControl.Info,
            Hint = Loc.T("Сколько раз в секунду мышь сообщает о движении. Обычно 1000 Гц; меняется в программе мыши"),
            Format = v => Loc.Tf("≈ {0} Гц", v.Number.ToString(CultureInfo.InvariantCulture)), Tool = InputTools.Polling(),
        });

        rows.Add(SpiRow(ctx, spi, "wheel-lines", 50, SettingControl.Slider,
            Loc.T("На сколько строк прокручивает один щелчок колёсика. На игры не влияет"),
            min: 1, max: 100, @default: 3, format: v => InputAdvice.WheelLinesText(v.Number),
            special: new DeviceOption(DeviceValue.Of(SpiCatalog.PageScroll), Loc.T("экран целиком"))));
        if (sys.HorizontalWheel)
            rows.Add(SpiRow(ctx, spi, "wheel-chars", 55, SettingControl.Slider,
                Loc.T("На сколько символов сдвигает текст один наклон колёсика вбок"),
                min: 1, max: 100, @default: 3, format: v => InputAdvice.WheelCharsText(v.Number)));

        var routing = SpiRow(ctx, spi, "wheel-routing", 60, SettingControl.Toggle,
            Loc.T("Колёсико прокручивает окно под курсором, даже неактивное — не нужно сначала щёлкать по нему"),
            @default: 2, on: DeviceValue.Of(2), off: DeviceValue.Of(0));
        if (routing is { Apply: not null, Current.Number: 0 })
            routing.Recommended = new Recommendation(DeviceValue.Of(2),
                Loc.T("Колёсико прокручивает окно под курсором, не нужно сначала щёлкать по нему (так в Windows по умолчанию)."), RecommendPolicy.Suggest);
        rows.Add(routing);

        rows.Add(SpiRow(ctx, spi, "double-click", 70, SettingControl.Slider,
            Loc.T("Сколько времени даётся на второй щелчок. Проверьте на поле ниже"),
            min: 200, max: 900, step: 10, @default: 500, test: SettingTest.DoubleClick,
            format: v => Loc.Tf("{0} мс", v.Number.ToString(CultureInfo.InvariantCulture))));

        rows.Add(SpiRow(ctx, spi, "swap-buttons", 80, SettingControl.Toggle,
            Loc.T("Для левшей. Подтвердите новой основной кнопкой за 15 с, иначе всё вернётся. Не дублируйте замену в программе мыши"),
            @default: 0, countdown: true, confirm: _ => SwapConfirm,
            manualHint: Loc.T("Вернуть вручную: Параметры → Bluetooth и устройства → Мышь → Основная кнопка мыши.")));

        var trails = SpiRow(ctx, spi, "mouse-trails", 90, SettingControl.Choice,
            Loc.T("Копии курсора тянутся за ним. Помогают найти курсор, но на быстром мониторе только мешают"),
            @default: 0, format: v => InputAdvice.TrailsText(v.Number),
            options: [new DeviceOption(DeviceValue.Of(0), Loc.T("выкл")), .. Enumerable.Range(2, 6).Select(n => new DeviceOption(DeviceValue.Of(n), InputAdvice.TrailsText(n)))],
            same: (a, b) => InputAdvice.NormalizeTrails(a.Number) == InputAdvice.NormalizeTrails(b.Number));
        if (trails is { Apply: not null, Current.Number: > 1 })
            trails.Recommended = new Recommendation(DeviceValue.Of(0),
                Loc.T("Шлейф нужен, чтобы находить курсор; на быстром мониторе он только мешает следить за ним."), RecommendPolicy.Suggest);
        rows.Add(trails);

        var sonar = SpiRow(ctx, spi, "mouse-sonar", 100, SettingControl.Toggle,
            Loc.T("Ctrl рисует круги вокруг курсора; в играх Ctrl нажимают часто"), @default: 0);
        if (sonar is { Apply: not null, Current.Number: 1 })
            sonar.Recommended = new Recommendation(DeviceValue.Of(0),
                Loc.T("Ctrl — частая клавиша в играх: после каждого отдельного нажатия вокруг курсора появляются круги."), RecommendPolicy.Suggest);
        rows.Add(sonar);

        rows.Add(SpiRow(ctx, spi, "mouse-vanish", 110, SettingControl.Toggle,
            Loc.T("Курсор прячется, пока вы печатаете, и появляется снова при движении мыши"), @default: 1));

        var card = new DeviceCard
        {
            Key = MouseCard, Kind = DeviceKind.Mouse, Order = 10, Title = Loc.T("Мышь"),
            Subtitle = inv.Mice.Count == 0 ? null : string.Join(", ", inv.Mice.Select(m => m.Label)),
            Hardware = inv.Mice.Select(m => m.ToHardwareRef()).ToList(),
            Settings = [.. rows],
        };

        RefreshMeasured(card, ctx.Results, sys.ScreenWidthPx);
        // The DPI hint and the pointer-speed suggestion depend on the live speed: recompute when it is re-read.
        var results = ctx.Results;
        var width = sys.ScreenWidthPx;
        speed.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DeviceSetting.Current)) RefreshMeasured(card, results, width);
        };
        return card;
    }

    /// <summary>
    /// Puts the measurements into the mouse card: the DPI and polling rows (value + advice) and the pointer-speed
    /// suggestion that depends on the DPI. Called when the card is built, after a measurement (UI thread) and when the
    /// pointer speed is re-read. While the pointer-speed suggestion stands it already names the DPI to set, so there is
    /// no second, contradicting DPI-range advice.
    /// </summary>
    internal static void RefreshMeasured(DeviceCard card, DeviceResultStore results, int screenWidthPx)
    {
        var dpi = results.Get<DpiMeasurement>(DpiKey);
        var poll = results.Get<PollingMeasurement>(PollingKey);
        Recommendation? speedRec = null;
        if (Find(card, "mouse-speed") is { Apply: not null } speed)
        {
            speedRec = InputAdvice.PointerSpeedRecommendation(speed.Current is { } c ? (int)c.Number : null, dpi?.Dpi, DpiToolLink);
            speed.Recommended = speedRec;
        }
        if (Find(card, DpiRow) is { } dpiRow)
        {
            dpiRow.Current = dpi is null ? null : DeviceValue.Of((long)Math.Round(dpi.Dpi));
            var (text, outside) = InputAdvice.DpiAdvice(dpi?.Dpi, screenWidthPx);
            dpiRow.Recommended = dpi is not null && outside && speedRec is null
                ? new Recommendation(DeviceValue.Of(InputAdvice.DpiHigh), text, RecommendPolicy.Advice, MouseAppLink)
                : null;
        }
        if (Find(card, PollingRow) is { } pollRow)
        {
            pollRow.Current = poll?.Hz is { } hz ? DeviceValue.Of(hz)
                : poll is { RawHz: > 0 } raw ? DeviceValue.Of((long)Math.Round(raw.RawHz))
                : null;
            pollRow.Recommended = InputAdvice.PollingNeedsAdvice(poll?.Hz)
                ? new Recommendation(DeviceValue.Of(1000), InputAdvice.PollingAdvice(poll!.Hz), RecommendPolicy.Advice, MouseAppLink)
                : null;
        }
    }

    private static DeviceSetting? Find(DeviceCard card, string id) => card.Settings.FirstOrDefault(s => s.Id == id);

    /// <summary>The Optimize tweak «mouse-acceleration-off» is in the registry but the session still accelerates (sign-out pending).</summary>
    private static bool AccelerationPending(InputSystemState sys)
    {
        if (sys.AccelerationOn != true) return false;
        try { return TweakCatalog.Find(AccelerationTweak)?.Detect() == TweakState.Applied; }
        catch { return false; }
    }

    // ───────────────────────────── Keyboard ─────────────────────────────

    private static DeviceCard BuildKeyboard(DeviceScanContext ctx, ISpiWriter? spi, InputInventory inv, InputSystemState sys)
    {
        var filter = sys.FilterKeysOn
            ? sys.FilterDelayMs > 0
                ? Loc.Tf("Сейчас задержку задаёт «Фильтрация ввода» ({0} мс).", sys.FilterDelayMs.ToString(CultureInfo.InvariantCulture))
                : Loc.T("Сейчас задержку задаёт «Фильтрация ввода».")
            : null;

        var delay = SpiRow(ctx, spi, "keyboard-delay", 10, SettingControl.Slider,
            Loc.T("Пауза перед повтором зажатой клавиши. 0 — быстрее навигация, но легче случайный повтор"),
            min: 0, max: 3, @default: 1, test: SettingTest.KeyRepeat, format: v => InputAdvice.KeyboardDelayText(v.Number));
        if (filter is not null) delay.DisabledReason ??= filter;

        var rate = SpiRow(ctx, spi, "keyboard-speed", 20, SettingControl.Slider,
            Loc.T("Сколько раз в секунду повторяется зажатая клавиша. Быстрее всего — около 30 раз"),
            min: 0, max: 31, @default: 31, format: v => InputAdvice.RepeatSpeedText(v.Number));
        if (sys.FilterKeysOn) rate.DisabledReason ??= Loc.T("Сейчас скорость повтора задаёт «Фильтрация ввода».");
        else if (rate is { Apply: not null, Current.Number: < 31 })
            rate.Recommended = new Recommendation(DeviceValue.Of(31),
                Loc.Tf("Зажатая клавиша повторяется быстрее всего ({0}): удобнее стирать и листать. На игры не влияет.", InputAdvice.RepeatRateText(31)),
                RecommendPolicy.Suggest);

        var hotkeys = DeviceSetting.ForTweak(HotkeysTweak, Loc.T("Сочетания клавиш специальных возможностей"),
            Loc.T("Пять нажатий Shift не откроют окно «Залипание клавиш» поверх игры; сами функции остаются"),
            order: 30, format: v => v.Number != 0 ? Loc.T("выкл") : Loc.T("вкл"));

        var notes = new List<DeviceNote>();
        var link = new DeviceLink(DeviceLinkKind.Settings, Loc.T("ОТКРЫТЬ ПАРАМЕТРЫ"), "ms-settings:easeofaccess-keyboard");
        if (sys.StickyKeysOn)
            notes.Add(new DeviceNote(Loc.T("Включено «Залипание клавиш»: Shift, Ctrl и Alt остаются нажатыми после отпускания — в играх это мешает."), link));
        if (sys.FilterKeysOn)
            notes.Add(new DeviceNote(sys.FilterWaitMs > 0
                ? Loc.Tf("Включена «Фильтрация ввода»: нажатие засчитывается через {0} мс — в играх клавиши срабатывают с задержкой.", sys.FilterWaitMs.ToString(CultureInfo.InvariantCulture))
                : Loc.T("Включена «Фильтрация ввода»: быстрые и повторные нажатия могут теряться — в играх это мешает."), link));

        var named = inv.Keyboards.Count == 1 && inv.Keyboards[0].Name is { Length: > 0 } ? inv.Keyboards[0] : null;
        return new DeviceCard
        {
            Key = KeyboardCard, Kind = DeviceKind.Keyboard, Order = 20,
            Title = named?.Name ?? Loc.T("Клавиатура"),
            Subtitle = named is not null ? named.IdText
                : inv.Keyboards.Count == 0 ? null
                : string.Join(", ", inv.Keyboards.Select(k => k.Label)),
            Hardware = inv.Keyboards.Select(k => k.ToHardwareRef()).ToList(),
            Settings = [delay, rate, hotkeys],
            Notes = notes,
        };
    }

    // ───────────────────────────── One SPI row ─────────────────────────────

    private static readonly CountdownRequest SwapConfirm = new(Loc.T("Оставить новое назначение кнопок?"),
        Loc.T("Нажмите «ОСТАВИТЬ» той кнопкой мыши, которая теперь основная. Иначе через 15 с назначение вернётся."),
        15, CountdownRequest.Keep, CountdownRequest.Revert, null);

    private static readonly CountdownRequest LowSpeedConfirm = new(Loc.T("Оставить эту скорость указателя?"),
        Loc.T("Курсор стал очень медленным. Если им удобно пользоваться, нажмите «ОСТАВИТЬ» — иначе через 15 с вернётся прежняя скорость."),
        15, CountdownRequest.Keep, CountdownRequest.Revert, null);

    /// <summary>
    /// A row of one <see cref="SpiCatalog"/> parameter: per-user, journal target spi:&lt;id&gt;, live value read now. Without
    /// a writer (or for another account) the row is read-only / disabled. <paramref name="confirm"/> returns the countdown a
    /// value needs (button swap, very low pointer speed), or null.
    /// </summary>
    private static DeviceSetting SpiRow(DeviceScanContext ctx, ISpiWriter? spi, string id, int order, SettingControl control, string hint,
        long min = 0, long max = 1, long step = 1, long? @default = null, Func<DeviceValue, string>? format = null,
        IReadOnlyList<DeviceOption>? options = null, DeviceOption? special = null, SettingTest test = SettingTest.None,
        DeviceValue? on = null, DeviceValue? off = null, Func<DeviceValue, DeviceValue, bool>? same = null, bool countdown = false,
        Func<int, CountdownRequest?>? confirm = null, string? manualHint = null)
    {
        var entry = SpiCatalog.Find(id) ?? throw new InvalidOperationException($"нет параметра «{id}» в каталоге"); // loc:ignore programmer error, never shown
        var writable = spi is not null;
        var current = Read(spi, entry);
        return new DeviceSetting
        {
            Id = id, Title = entry.Title, Hint = hint, Order = order,
            Control = writable ? control : SettingControl.Info,
            Min = min, Max = max, Step = step, Options = options ?? [], SpecialOption = special,
            Default = @default is { } d ? DeviceValue.Of(d) : null,
            Format = format ?? (v => entry.Describe((int)v.Number)),
            JournalTarget = DeviceTargets.Spi(id), PerUser = true, NeedsCountdown = countdown,
            Test = writable ? test : SettingTest.None,
            On = on ?? DeviceValue.Of(1), Off = off ?? DeviceValue.Of(0), Same = same,
            ReadCurrent = writable ? () => Read(DeviceWriters.Spi, entry) : null,
            Apply = writable ? ApplyFor(id, entry.Title, confirm, manualHint) : null,
            Current = current,
            DisabledReason = ctx.IsInteractiveUser ? null : AccountReason(ctx),
            StateTag = PendingTag(entry, current, ctx),
        };
    }

    private static DeviceValue? Read(ISpiWriter? spi, SpiEntry entry)
    {
        if (spi is null) return null;
        try { return spi.Get(entry) is { } v ? DeviceValue.Of(v) : null; }
        catch (Exception ex)
        {
            Log.Warn("{0}: не прочитано — {1}", entry.Title, ex.Message);
            return null;
        }
    }

    /// <summary>Per-user rows for another account (or an unknown one): one short reason; the page shows the full text once.</summary>
    internal static string AccountReason(DeviceScanContext ctx) => ctx.InteractiveUser == false
        ? Loc.T("Недоступно: MONOLITH запущен от другой учётной записи.")
        : Loc.T("Недоступно: не удалось определить пользователя рабочего стола.");

    /// <summary>
    /// «ОЖИДАЕТ ВЫХОДА ИЗ СИСТЕМЫ» when the account's saved value differs from the live one (a registry-only change waits for
    /// the next sign-in). Only for the desktop user: another account's HKCU says nothing about this session.
    /// </summary>
    private static string? PendingTag(SpiEntry entry, DeviceValue? live, DeviceScanContext ctx)
    {
        if (!ctx.IsInteractiveUser || entry.Persisted is not { } p || live is not { } current) return null;
        try
        {
            var saved = RegistryHelper.Read(RegHive.HKCU, p.Path, p.Name) switch
            {
                int i => i,
                string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
                _ => (int?)null,
            };
            return saved is { } value && value != current.Number ? PendingSignOut : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The Apply of an SPI row: SetUserParameter, or — for a value that needs it — the confirmed variant.</summary>
    private static Func<ChangeSession, DeviceValue, CancellationToken, Task<ApplyOutcome>> ApplyFor(string id, string title,
        Func<int, CountdownRequest?>? confirm, string? manualHint) => async (session, value, ct) =>
    {
        var v = checked((int)value.Number);
        if (confirm?.Invoke(v) is not { } request)
        {
            var before = session.Applied;
            if (!session.SetUserParameter(id, v, DeviceTargets.Source, title)) return ApplyOutcome.Failed;
            return session.Applied > before ? ApplyOutcome.Applied : ApplyOutcome.NoChange;
        }
        var outcome = await session.SetUserParameterConfirmedAsync(id, v, request, DeviceTargets.Source, title, ct, manualHint);
        return Map(session, title, outcome);
    };

    /// <summary>The outcome of a confirmed change for the page; a failed automatic return is shown as an alert with the manual way back.</summary>
    private static ApplyOutcome Map(ChangeSession session, string title, DisplayApplyOutcome outcome)
    {
        switch (outcome.Result)
        {
            case DisplayApplyResult.Kept: return ApplyOutcome.Applied;
            case DisplayApplyResult.Unchanged: return ApplyOutcome.NoChange;
            case DisplayApplyResult.Reverted: return ApplyOutcome.Declined;
            case DisplayApplyResult.Blocked: return ApplyOutcome.Blocked;
        }
        var message = outcome.Message ?? Loc.T("не применено");
        if (!session.Errors.Any(e => e.Contains(message, StringComparison.Ordinal)))
        {
            // Applied, not confirmed, and the old value could not be put back by itself: say how to do it by hand.
            session.Errors.Add(Loc.Tf("{0}: {1}", title, message));
            _ = Ui.AlertAsync(Loc.Tf("{0}: прежнее значение не вернулось", title), message);
        }
        return ApplyOutcome.Failed;
    }
}
