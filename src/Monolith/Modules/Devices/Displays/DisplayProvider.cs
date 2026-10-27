using System.Globalization;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Core.Devices;
using Monolith.Modules.Dashboard.Checks;
using Monolith.Modules.Dashboard.Monitoring;
using Monolith.Modules.Games;

namespace Monolith.Modules.Devices.Displays;

/// <summary>What the monitor cards need to know about the PC besides the monitors themselves.</summary>
public sealed record DisplayEnvironment
{
    public bool IsWindows11 { get; init; } = true;
    /// <summary>A laptop running on battery right now: no refresh-rate recommendation.</summary>
    public bool OnBattery { get; init; }
    /// <summary>Adapter keys (MonitorMath.LuidKey) of integrated graphics on a PC that also has a graphics card.</summary>
    public IReadOnlySet<string> IntegratedAdapterKeys { get; init; } = new HashSet<string>();
    /// <summary>«Калибровка HDR в Windows» is installed (null = cannot tell: no note).</summary>
    public bool? HdrCalibrationInstalled { get; init; }
}

/// <summary>
/// «Мониторы»: one card per active monitor (design devices_design.md §3.3, research devices_displays.md). Resolution and
/// refresh, HDR and DDC/CI brightness/contrast are changed through ChangeSession (journal first, on-screen confirmation
/// for anything that can blank the screen); Auto HDR and VRR for games are Optimize tweaks shown once with
/// «В ОПТИМИЗАЦИИ». Everything else is read-only facts and notes shown only when actionable.
/// </summary>
public sealed class DisplayProvider : IDeviceProvider
{
    public string Name => "мониторы";

    public const string SettingMode = "display-mode";
    public const string SettingHdr = "display-hdr";
    public const string SettingBrightness = "display-brightness";
    public const string SettingContrast = "display-contrast";
    public const string SettingVrr = "display-vrr";
    public const string AutoHdrTweak = "auto-hdr";
    public const string VrrTweak = "vrr-optimization";

    private const string NoDisplayWriter = "Эта версия MONOLITH не умеет менять параметры экрана.";

    public Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct) => Task.Run(() =>
    {
        var monitors = DisplayInventory.Read(ctx.Profile.OsBuild);
        var env = new DisplayEnvironment
        {
            IsWindows11 = ctx.Profile.IsWindows11,
            OnBattery = GamesNative.IsOnBattery(),
            IntegratedAdapterKeys = IntegratedAdapters(ctx.Profile),
            HdrCalibrationInstalled = monitors.Any(m => m.Color?.HdrEnabled == true) ? HdrCalibrationInstalled() : null,
        };
        return BuildCards(monitors, env);
    }, ct);

    // ───────────────────────────── Cards (pure over MonitorInfo + DeviceWriters) ─────────────────────────────

    internal static IReadOnlyList<DeviceCard> BuildCards(IReadOnlyList<MonitorInfo> monitors, DisplayEnvironment env)
    {
        var cards = new List<DeviceCard>();
        var primaryShouldBe = DisplayAdvice.FastestShouldBePrimary(monitors
            .Select(m => (m.MonitorPath, m.IsPrimary, m.IsInternal, NativeMaxHz(m))).ToList());
        // Global tweak rows appear once: Auto HDR on the first monitor with HDR on, VRR on the first VRR monitor (primary first).
        var autoHdrOn = monitors.FirstOrDefault(m => m.Color is { HdrSupported: true, HdrEnabled: true })?.MonitorPath;
        var vrrOn = monitors.OrderByDescending(m => m.IsPrimary).FirstOrDefault(m => m.Edid?.VrrRange is not null)?.MonitorPath;

        for (var i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            var card = new DeviceCard
            {
                Key = "display:" + m.MonitorPath,
                Kind = DeviceKind.Monitor,
                Title = m.Name,
                Subtitle = Subtitle(m),
                Order = 30 + i,
                Hardware = [new DeviceHardwareRef(null, null, null, InstanceId(m.MonitorPath) is { } id ? [id] : [])],
                Facts = Facts(m),
            };

            if (ModeSetting(m, env) is { } mode) card.Settings.Add(mode);
            if (HdrSetting(m) is { } hdr) card.Settings.Add(hdr);
            if (DdcSetting(m, MonitorVcp.Brightness) is { } brightness) card.Settings.Add(brightness);
            if (DdcSetting(m, MonitorVcp.Contrast) is { } contrast) card.Settings.Add(contrast);
            if (m.MonitorPath == autoHdrOn)
                card.Settings.Add(DeviceSetting.ForTweak(AutoHdrTweak, "Auto HDR для игр",
                    "HDR для игр DirectX 11/12 без своего HDR; яркость по играм — в Game Bar (Win+G)", order: 40));
            if (VrrInfo(m) is { } vrr) card.Settings.Add(vrr);
            if (m.MonitorPath == vrrOn)
                // Shown, never planned: its risk (flicker on OLED and VA panels) cannot be ruled out from Windows, and a row the
                // user may rightly leave alone must not keep the plan from ever being empty. It stays an Optimize item.
                card.Settings.Add(DeviceSetting.ForTweak(VrrTweak, "VRR в играх DirectX 11",
                    "Windows включает VRR в играх без своей поддержки G-SYNC/FreeSync; на OLED и VA бывает мерцание", order: 51,
                    policy: RecommendPolicy.None));

            card.Notes = Notes(m, env, primaryShouldBe == m.MonitorPath);
            cards.Add(card);
        }
        return cards;
    }

    private static int NativeMaxHz(MonitorInfo m) =>
        DisplayAdvice.NativeSize(m.Modes, m.PreferredSize, m.EdidFirstSize) is { } n ? m.MaxHzAt(n.Size) : 0;

    private static string? Subtitle(MonitorInfo m)
    {
        var parts = new List<string>();
        if (m.IsPrimary) parts.Add("основной");
        if (m.OutputName.Length > 0) parts.Add(m.OutputName);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>«2560×1440 · 239,97 Гц · 8 бит · HDR: поддерживается, выкл · масштаб 100 %».</summary>
    internal static IReadOnlyList<string> Facts(MonitorInfo m)
    {
        var facts = new List<string>();
        if (m.Current is { } c)
        {
            facts.Add($"{c.Width.ToString(CultureInfo.InvariantCulture)}×{c.Height.ToString(CultureInfo.InvariantCulture)}");
            facts.Add(c.HzText());
            if (DisplayAdvice.NativeSize(m.Modes, m.PreferredSize, m.EdidFirstSize) is { } native && native.Size != new DisplaySize(c.Width, c.Height))
                facts.Add($"родное {native.Size}");
        }
        if (m.Color is { Bpc: > 0 } bpc) facts.Add($"{bpc.Bpc.ToString(CultureInfo.InvariantCulture)} бит");
        if (m.Color is { HdrSupported: true } color)
        {
            facts.Add(color.HdrEnabled ? "HDR: вкл" : "HDR: поддерживается, выкл");
            if (color.HdrEnabled && m.SdrWhiteNits is { } nits) facts.Add(DisplayAdvice.SdrWhiteText(nits));
        }
        if (m.ScalePercent is { } scale) facts.Add($"масштаб {scale.ToString(CultureInfo.InvariantCulture)} %");
        return facts;
    }

    /// <summary>DISPLAY\ACR1132\5&amp;30ca09c6&amp;0&amp;UID4355 — for matching only, never shown.</summary>
    private static string? InstanceId(string monitorPath) =>
        DisplayInventory.EdidKeyPath(monitorPath) is { } key ? key["SYSTEM\\CurrentControlSet\\Enum\\".Length..^"\\Device Parameters".Length] : null;

    private static bool CanWriteDisplay(MonitorInfo m) => DeviceWriters.Display is not null && DeviceTargets.IsMonitorPath(m.MonitorPath);

    // ───────────────────────────── Resolution and refresh ─────────────────────────────

    /// <summary>The value key of a mode: «2560x1440@240» (GDI integer rate, the same for the live value and the options).</summary>
    internal static DeviceValue ModeValue(int width, int height, int hz) =>
        DeviceValue.Dual(DeviceValue.Of(new DisplaySize(width, height).Key), DeviceValue.Of(hz));

    internal static (DisplaySize Size, int Hz)? ParseMode(DeviceValue v)
    {
        if (v.SplitDual() is not { } split || DisplaySize.FromKey(split.Primary) is not { } size) return null;
        return int.TryParse(split.Secondary, NumberStyles.None, CultureInfo.InvariantCulture, out var hz) && hz is >= 1 and <= 1000 ? (size, hz) : null;
    }

    internal static DeviceSetting? ModeSetting(MonitorInfo m, DisplayEnvironment env)
    {
        if (m.Current is not { } current) return null;
        var native = DisplayAdvice.NativeSize(m.Modes, m.PreferredSize, m.EdidFirstSize);
        var sizes = m.Modes.Select(x => x.Size).Distinct()
            .OrderByDescending(s => native is { } n && s == n.Size)
            .ThenByDescending(s => s.Pixels).ThenByDescending(s => s.Width).ToList();
        var options = sizes.Select(s => new DeviceOption(DeviceValue.Of(s.Key), s.ToString())).ToList();
        var modes = m.Modes;
        var currentSize = new DisplaySize(current.Width, current.Height);
        IReadOnlyList<DeviceOption> Rates(DeviceValue primary)
        {
            if (DisplaySize.FromKey(primary.Text) is not { } size) return [];
            var rates = modes.Where(x => x.Size == size).Select(x => x.Hz).ToHashSet();
            // The live rate is always listed for the live size (so the list never shows an empty box), even when the
            // driver does not offer it as a separate mode; choosing it changes nothing.
            if (size == currentSize && current.Frequency > 1) rates.Add(current.Frequency);
            return rates.OrderByDescending(h => h).Select(h => new DeviceOption(DeviceValue.Of(h), $"{h.ToString(CultureInfo.InvariantCulture)} Гц")).ToList();
        }

        // The integer rate, as in the list (239,97 Гц is the 240 Гц mode; the exact rate is in the facts line).
        static string Format(DeviceValue v) =>
            ParseMode(v) is { } mode ? $"{mode.Size}, {mode.Hz.ToString(CultureInfo.InvariantCulture)} Гц"
            : v.SplitDual() is { } split && DisplaySize.FromKey(split.Primary) is { } size ? $"{size}, частота по умолчанию"
            : v.Key;

        var path = m.MonitorPath;
        var name = m.Name;
        var writable = CanWriteDisplay(m) && options.Count > 0;
        var advice = DisplayAdvice.RecommendedMode(m.Modes, m.PreferredSize, m.EdidFirstSize, current,
            new ModeContext(m.IsInternal, env.OnBattery, DisplayAdvice.DrrLikely(env.IsWindows11, m.IsInternal, m.Edid)));

        var setting = new DeviceSetting
        {
            Id = SettingMode,
            Title = "Разрешение и частота",
            Hint = "Первым в списке — родное разрешение, самое чёткое. Новый режим нужно подтвердить за 15 с, иначе вернётся прежний",
            Control = writable ? SettingControl.DualChoice : SettingControl.Info,
            Options = options,
            SecondaryOptions = Rates,
            Format = Format,
            JournalTarget = DeviceTargets.DisplayMode(path),
            NeedsCountdown = writable,
            Order = 10,
            ReadCurrent = () => DeviceWriters.Display?.ReadCurrentMode(path) is { } live ? ModeValue(live.Width, live.Height, live.Frequency) : null,
            Apply = writable ? (s, v, ct) => ApplyModeAsync(s, path, name, modes, v, ct) : null,
            Current = ModeValue(current.Width, current.Height, current.Frequency),
            DisabledReason = DeviceWriters.Display is null ? NoDisplayWriter : null,
        };
        if (writable && advice is { } a)
            setting.Recommended = new Recommendation(ModeValue(a.Target.Width, a.Target.Height, a.Target.Hz), a.Reason, a.Policy);
        return setting;
    }

    private static async Task<ApplyOutcome> ApplyModeAsync(ChangeSession s, string path, string name, IReadOnlyList<OfferedMode> modes,
        DeviceValue value, CancellationToken ct)
    {
        var title = $"Разрешение и частота — {name}";
        if (ParseMode(value) is not { } wanted || !modes.Any(x => x.Size == wanted.Size && x.Hz == wanted.Hz))
        {
            s.Errors.Add($"{title}: такого режима нет в списке монитора");
            return ApplyOutcome.Failed;
        }
        var writer = DeviceWriters.Display;
        var live = writer is null ? null : await Task.Run(() => writer.ReadCurrentMode(path), CancellationToken.None);
        // Same depth, orientation and position as now: only size and rate change (the writer never sets position or rotation).
        var target = new DisplayModeSpec(wanted.Size.Width, wanted.Size.Height, wanted.Hz, 0, 0, 32,
            live?.Orientation ?? 0, live?.PositionX ?? 0, live?.PositionY ?? 0);
        var outcome = await s.SetDisplayModeAsync(path, name, target, DeviceTargets.Source, title, ct);
        return Map(s, title, outcome);
    }

    /// <summary>Kept → Applied, Unchanged → NoChange, Reverted → Declined, Blocked → Blocked (with its reason), Failed → Failed.</summary>
    internal static ApplyOutcome Map(ChangeSession s, string title, DisplayApplyOutcome outcome)
    {
        switch (outcome.Result)
        {
            case DisplayApplyResult.Kept: return ApplyOutcome.Applied;
            case DisplayApplyResult.Unchanged: return ApplyOutcome.NoChange;
            case DisplayApplyResult.Reverted: return ApplyOutcome.Declined;
            case DisplayApplyResult.Blocked:
                if (outcome.Message is { Length: > 0 } why) s.Errors.Add($"{title}: {why}");
                return ApplyOutcome.Blocked;
            default: return ApplyOutcome.Failed;
        }
    }

    // ───────────────────────────── HDR ─────────────────────────────

    internal static DeviceSetting? HdrSetting(MonitorInfo m)
    {
        if (m.Color is not { HdrSupported: true } color) return null;
        var path = m.MonitorPath;
        var name = m.Name;
        var writable = CanWriteDisplay(m) && !color.LimitedByPolicy;
        return new DeviceSetting
        {
            Id = SettingHdr,
            Title = "HDR",
            Hint = DisplayAdvice.HdrHint(m.Edid),
            Control = writable ? SettingControl.Toggle : SettingControl.Info,
            JournalTarget = DeviceTargets.DisplayHdr(path),
            NeedsCountdown = writable,
            Order = 20,
            Format = v => v.Number != 0 ? "вкл" : "выкл",
            Current = DeviceValue.Of(color.HdrEnabled),
            ReadCurrent = () => DeviceWriters.Display?.ReadHdrEnabled(path) is { } on ? DeviceValue.Of(on) : null,
            DisabledReason = color.LimitedByPolicy ? "HDR запрещён политикой или драйвером видеокарты — MONOLITH его не включает."
                : DeviceWriters.Display is null ? NoDisplayWriter : null,
            Apply = writable
                ? async (s, v, ct) =>
                {
                    var title = $"HDR — {name}";
                    return Map(s, title, await s.SetDisplayHdrAsync(path, name, v.Number != 0, DeviceTargets.Source, title, ct));
                }
                : null,
        };
    }

    // ───────────────────────────── DDC/CI ─────────────────────────────

    /// <summary>
    /// The lowest value the slider offers: very dark or flat values can make the only screen unreadable, so the bottom of
    /// the range is cut off (brightness 5 %, contrast 10 % of the monitor's range); values a bit above that still need the
    /// 15-second confirmation in ChangeSession (brightness under 10 %, contrast under 25 %).
    /// </summary>
    public static uint DdcFloor(MonitorVcp code, uint min, uint max)
    {
        if (max <= min) return min;
        var share = code == MonitorVcp.Contrast ? 0.10 : 0.05;
        return min + (uint)Math.Ceiling((max - min) * share);
    }

    internal static DeviceSetting? DdcSetting(MonitorInfo m, MonitorVcp code)
    {
        var reading = code == MonitorVcp.Contrast ? m.Contrast : m.Brightness;
        if (reading is not { } r || r.Max <= r.Min || r.Max > 10_000) return null;
        var path = m.MonitorPath;
        var name = m.Name;
        var writable = DeviceWriters.Monitor is not null && DeviceTargets.IsMonitorPath(path);
        var contrast = code == MonitorVcp.Contrast;
        var max = r.Max;
        return new DeviceSetting
        {
            Id = contrast ? SettingContrast : SettingBrightness,
            Title = contrast ? "Контраст" : "Яркость",
            Hint = contrast
                ? "Хранится в памяти монитора. Ниже 25 % — с подтверждением за 15 с; ниже 10 % недоступно"
                : "Хранится в памяти монитора. Ниже 10 % — с подтверждением за 15 с; самые тёмные значения недоступны",
            Control = writable ? SettingControl.Slider : SettingControl.Info,
            Min = DdcFloor(code, r.Min, r.Max),
            Max = r.Max,
            Step = 1,
            Format = v => $"{v.Number.ToString(CultureInfo.InvariantCulture)} из {max.ToString(CultureInfo.InvariantCulture)}",
            JournalTarget = DeviceTargets.Monitor(path, code),
            Order = contrast ? 31 : 30,
            Current = DeviceValue.Of(r.Value),
            ReadCurrent = () => DeviceWriters.Monitor?.Get(path, code) is { } now ? DeviceValue.Of(now.Value) : null,
            DisabledReason = writable ? null : "Эта версия MONOLITH не умеет менять яркость и контраст монитора.",
            Apply = writable
                ? async (s, v, ct) =>
                {
                    var title = $"{(contrast ? "Контраст" : "Яркость")} монитора — {name}";
                    if (v.Number < DdcFloor(code, r.Min, r.Max) || v.Number > r.Max)
                    {
                        s.Errors.Add($"{title}: значение вне безопасного диапазона");
                        return ApplyOutcome.Failed;
                    }
                    return Map(s, title, await s.SetMonitorSettingAsync(path, name, code, (uint)v.Number, DeviceTargets.Source, title, ct));
                }
                : null,
        };
    }

    // ───────────────────────────── VRR ─────────────────────────────

    internal static DeviceSetting? VrrInfo(MonitorInfo m)
    {
        if (m.Edid?.VrrRange is not { } range) return null;
        var max = NativeMaxHz(m) is > 0 and var hz ? Math.Min(hz, range.Max) : range.Max;
        return new DeviceSetting
        {
            Id = SettingVrr,
            Title = "Переменная частота (G-SYNC / FreeSync)",
            Hint = DisplayAdvice.VrrHint(max),
            Control = SettingControl.Info,
            Order = 50,
            Format = v => v.Text ?? "—",
            Current = DeviceValue.Of(DisplayAdvice.VrrRangeText(range)),
        };
    }

    // ───────────────────────────── Notes (only when actionable) ─────────────────────────────

    internal static IReadOnlyList<DeviceNote> Notes(MonitorInfo m, DisplayEnvironment env, bool shouldBePrimary)
    {
        var notes = new List<DeviceNote>();
        if (DisplayAdvice.NativeSize(m.Modes, m.PreferredSize, m.EdidFirstSize) is { } native
            && DisplayAdvice.Bottleneck(m.Edid, native.Size, m.MaxHzAt(native.Size), m.OutputTechnology,
                env.IntegratedAdapterKeys.Contains(m.AdapterKey)) is { } bottleneck)
            notes.Add(new DeviceNote(bottleneck.Text,
                bottleneck.OnMotherboard ? new DeviceLink(DeviceLinkKind.Navigate, "НА ПАНЕЛЬ", "dashboard") : null));

        if (shouldBePrimary)
            notes.Add(new DeviceNote($"Игры открываются на основном мониторе — сделайте основным этот, самый быстрый ({NativeMaxHz(m).ToString(CultureInfo.InvariantCulture)} Гц).",
                new DeviceLink(DeviceLinkKind.Settings, "ПАРАМЕТРЫ ЭКРАНА", "ms-settings:display")));

        if (m.Color is { HdrEnabled: true } && env.HdrCalibrationInstalled == false)
            notes.Add(new DeviceNote("HDR включён: откалибруйте его бесплатным приложением Microsoft «Калибровка HDR в Windows» и настройте яркость SDR-контента.",
                new DeviceLink(DeviceLinkKind.Settings, "ПАРАМЕТРЫ HDR", "ms-settings:display-hdr")));
        return notes;
    }

    // ───────────────────────────── Environment probes (read-only) ─────────────────────────────

    /// <summary>Integrated graphics adapters, only on a PC that also has a graphics card (moving the cable helps only then).</summary>
    private static IReadOnlySet<string> IntegratedAdapters(SystemProfile sp)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!sp.Gpus.Any(HealthRules.IsDiscreteCard)) return set;
        try
        {
            var adapters = D3dkmt.OpenAdapters();
            try
            {
                foreach (var a in adapters)
                    if (sp.Gpus.FirstOrDefault(g => MonitorMath.SameGpuName(g.Name, a.Name)) is { } gpu && HealthRules.IsOnboardGpu(gpu))
                        set.Add(MonitorMath.LuidKey(a.LuidHigh, a.LuidLow));
            }
            finally
            {
                D3dkmt.Close(adapters);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Мониторы: не удалось определить видеоадаптеры — {ex.Message}");
        }
        return set;
    }

    /// <summary>The HDR Calibration app is registered for this account (AppModel repository, read-only); null = cannot tell.</summary>
    private static bool? HdrCalibrationInstalled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return key?.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.Windows.HDRCalibration_", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
