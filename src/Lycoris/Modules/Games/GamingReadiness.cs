using Lycoris.Core;

namespace Lycoris.Modules.Games;

public enum ReadinessState { Ok, Attention, Info }

/// <summary>
/// One read-only gaming readiness check (checklist, never a scary score — docs/research/competitors.md).
/// <paramref name="Hint"/> explains what it means / how to fix it; <paramref name="Detail"/> is an always-visible fact line.
/// </summary>
public sealed record ReadinessCheck(string Title, string Value, ReadinessState State, string Hint, string? Detail = null)
{
    public bool IsOk => State == ReadinessState.Ok;
    public bool IsAttention => State == ReadinessState.Attention;
    public bool IsInfo => State == ReadinessState.Info;
}

/// <summary>
/// "ГОТОВНОСТЬ К ИГРАМ": Game Mode, HAGS, background recording, power plan, primary GPU and driver age.
/// Reads only (registry, powrprof, D3DKMT, WMI via SystemProfile). Blocking — call from a background thread.
/// </summary>
public static class GamingReadiness
{
    public const int DriverAgeAttentionDays = 180;

    public static IReadOnlyList<ReadinessCheck> Collect(SystemProfile sp) =>
    [
        GameMode(),
        Hags(sp),
        BackgroundRecording(),
        PowerPlan(sp),
        Gpu(sp, DateTime.Now),
    ];

    /// <summary>HKCU\Software\Microsoft\GameBar AutoGameModeEnabled: absent or 1 = on (gaming_tweaks.md `game-mode`).</summary>
    public static ReadinessCheck GameMode()
    {
        const string key = @"Software\Microsoft\GameBar";
        var v = InteractiveUser.ReadInt(key, "AutoGameModeEnabled") ?? InteractiveUser.ReadInt(key, "AllowAutoGameMode");
        return GameModeFromValue(v);
    }

    public static ReadinessCheck GameModeFromValue(int? value)
    {
        var on = (value ?? 1) != 0;
        return on
            ? new ReadinessCheck(Loc.T("ИГРОВОЙ РЕЖИМ WINDOWS"), Loc.T("Включён"), ReadinessState.Ok,
                Loc.T("Windows отдаёт приоритет запущенной игре и не ставит драйверы во время игры."))
            : new ReadinessCheck(Loc.T("ИГРОВОЙ РЕЖИМ WINDOWS"), Loc.T("Выключен"), ReadinessState.Attention,
                Loc.T("Включите в разделе «Оптимизация»: это безопасно и сразу работает при следующем запуске игры."));
    }

    /// <summary>D3DKMT WDDM_2_7_CAPS first; the HwSchMode registry value (2 on, 1 off) only as a fallback.</summary>
    public static ReadinessCheck Hags(SystemProfile sp)
    {
        var title = Loc.T("АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU");
        var state = GamesNative.QueryHags();
        if (state == GamesNative.HagsState.Unknown)
        {
            var reg = RegistryHelper.ReadInt(RegHive.HKLM, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
            state = reg switch { 2 => GamesNative.HagsState.On, 1 => GamesNative.HagsState.Off, _ => GamesNative.HagsState.Unknown };
        }
        var gpu = sp.PrimaryGpu?.Name ?? "";
        var frameGen = gpu.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) || gpu.Contains("RTX 50", StringComparison.OrdinalIgnoreCase);
        return state switch
        {
            GamesNative.HagsState.On => new ReadinessCheck(title, Loc.T("Включено"), ReadinessState.Ok,
                Loc.T("Нужно для генерации кадров DLSS (Frame Generation). На средний FPS почти не влияет.")),
            GamesNative.HagsState.Off when frameGen => new ReadinessCheck(title, Loc.T("Выключено"), ReadinessState.Attention,
                Loc.T("Для RTX 40/50 нужно для генерации кадров DLSS. Включается в «Оптимизации», нужна перезагрузка.")),
            GamesNative.HagsState.Off => new ReadinessCheck(title, Loc.T("Выключено"), ReadinessState.Info,
                Loc.T("Прирост FPS в пределах погрешности — включать по желанию.")),
            GamesNative.HagsState.NotSupported => new ReadinessCheck(title, Loc.T("Не поддерживается"), ReadinessState.Info,
                Loc.T("Видеокарта или драйвер не поддерживают эту функцию.")),
            _ => new ReadinessCheck(title, Loc.T("Не удалось определить"), ReadinessState.Info,
                Loc.T("Драйвер не сообщил состояние; значение реестра не задано (значит, по умолчанию).")),
        };
    }

    /// <summary>HKCU\…\GameDVR HistoricalCaptureEnabled: 1 = background recording on; absent = off (gaming_tweaks.md B).</summary>
    public static ReadinessCheck BackgroundRecording() =>
        BackgroundRecordingFromValue(InteractiveUser.ReadInt(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"));

    public static ReadinessCheck BackgroundRecordingFromValue(int? value) => value == 1
        ? new ReadinessCheck(Loc.T("ФОНОВАЯ ЗАПИСЬ ИГР"), Loc.T("Включена"), ReadinessState.Attention,
            Loc.T("Постоянная запись последних минут игры отнимает 1–5 % FPS и видеопамять. Выключается в «Оптимизации»."))
        : new ReadinessCheck(Loc.T("ФОНОВАЯ ЗАПИСЬ ИГР"), Loc.T("Выключена"), ReadinessState.Ok,
            Loc.T("Видеокарта не тратит ресурсы на постоянную запись."));

    public static ReadinessCheck PowerPlan(SystemProfile sp)
    {
        var title = Loc.T("ПЛАН ПИТАНИЯ");
        var active = PowerSchemes.GetActive();
        var name = active == Guid.Empty ? Loc.T("Не удалось определить") : PowerSchemes.GetName(active);
        if (BoostPolicy.IsDualCcdX3D(sp.CpuName, sp.CpuCores))
        {
            return active == PowerSchemes.Balanced
                ? new ReadinessCheck(title, name, ReadinessState.Ok,
                    Loc.T("Для Ryzen X3D с двумя CCD AMD рекомендует именно эту схему."))
                : new ReadinessCheck(title, name, ReadinessState.Attention,
                    Loc.T("Для Ryzen X3D с двумя CCD AMD рекомендует «Сбалансированную»: иначе игра может попасть не на тот CCD."));
        }
        if (active == PowerSchemes.PowerSaver)
            return new ReadinessCheck(title, name, ReadinessState.Attention,
                Loc.T("Экономия энергии ограничивает частоты процессора — для игр не подходит."));
        if (GamesNative.IsOnBattery())
            return new ReadinessCheck(title, name, ReadinessState.Info, Loc.T("Ноутбук работает от батареи — для игр подключите зарядку."));
        if (active == PowerSchemes.Balanced)
            return new ReadinessCheck(title, name, ReadinessState.Info,
                BalancedHint(sp.IsLaptop, sp.IsWindows11, sp.IsModernStandby || GamesNative.IsModernStandby(), PowerSchemes.Exists(PowerSchemes.HighPerformance)));
        return new ReadinessCheck(title, name, ReadinessState.Info,
            Loc.T("Максимальные частоты процессора; в простое компьютер потребляет больше энергии."));
    }

    /// <summary>
    /// The Balanced-plan hint: what the game session does with power here (mirrors <see cref="BoostPolicy"/>: the plan on a
    /// desktop that has «Высокая производительность»; the Windows 11 AC power mode on a laptop, a Modern Standby PC or a PC
    /// without that plan). States a typical difference, never a promised gain. Pure, unit-tested.
    /// </summary>
    public static string BalancedHint(bool laptop, bool windows11, bool modernStandby, bool highPerformanceExists)
    {
        var head = Loc.T("Подходит для игр: разница с «Высокой производительностью» обычно 0–2 % FPS.") + " ";
        if (!laptop && !modernStandby && highPerformanceExists)
            return head + Loc.T("Игровой сеанс Lycoris переключает схему на время игры.");
        if (laptop)
            return head + (windows11
                ? Loc.T("На ноутбуке игровой сеанс Lycoris включает режим питания «Максимальная производительность» (от сети) на время игры.")
                : Loc.T("На ноутбуке игровой сеанс Lycoris схему не меняет: режим питания «Максимальная производительность» есть только в Windows 11."));
        return head + (windows11
            ? Loc.T("Игровой сеанс Lycoris включает режим питания «Максимальная производительность» (от сети) на время игры.")
            : Loc.T("Игровой сеанс Lycoris схему не меняет: режим питания «Максимальная производительность» есть только в Windows 11."));
    }

    public static ReadinessCheck Gpu(SystemProfile sp, DateTime now)
    {
        var gpu = sp.PrimaryGpu;
        if (gpu is null)
            return new ReadinessCheck(Loc.T("ВИДЕОКАРТА"), Loc.T("Не определена"), ReadinessState.Info, Loc.T("Сведения о видеокарте ещё собираются или недоступны."));
        return GpuFromInfo(gpu.Name, gpu.DriverVersion, gpu.DriverDate, now);
    }

    public static ReadinessCheck GpuFromInfo(string name, string version, DateTime? driverDate, DateTime now)
    {
        var title = Loc.T("ВИДЕОКАРТА");
        var shortName = ShortGpuName(name);
        var ver = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
        if (driverDate is not { } date || date.Year < 2000)
            return new ReadinessCheck(title, shortName, ReadinessState.Info,
                Loc.Tf("{0}. Дата драйвера не указана.", name), ver.Length > 0 ? Loc.Tf("Драйвер {0}", ver) : null);
        var days = Math.Max(0, (int)(now.Date - date.Date).TotalDays);
        var detail = ver.Length > 0 ? Loc.Tf("Драйвер {0} · {1}", ver, Loc.DateText(date)) : Loc.Tf("Драйвер {0}", Loc.DateText(date));
        var age = Loc.N(days, "{0} день", "{0} дня", "{0} дней");
        return days > DriverAgeAttentionDays
            ? new ReadinessCheck(title, shortName, ReadinessState.Attention,
                Loc.Tf("Драйверу {0} — проверьте обновление в разделе «Драйверы».", age), detail)
            : new ReadinessCheck(title, shortName, ReadinessState.Ok,
                Loc.Tf("{0}: драйвер установлен {1} назад.", name, age), detail);
    }

    /// <summary>"NVIDIA GeForce RTX 5070 Ti" → "GeForce RTX 5070 Ti"; "AMD Radeon RX 7800 XT" → "Radeon RX 7800 XT".</summary>
    public static string ShortGpuName(string name)
    {
        var n = (name ?? "").Trim();
        foreach (var prefix in new[] { "NVIDIA ", "AMD ", "Intel(R) ", "Intel " })
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && n.Length > prefix.Length + 3)
                return n[prefix.Length..].Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Trim();
        return n;
    }

    /// <summary>The one muted line on the Games page; the full checklist (with fix buttons) is on the Dashboard.</summary>
    public static string SummaryLine(int passed, int total) => Loc.Tf("Готовность к играм: {0}/{1} — на Панели", passed, total);

    /// <summary>(passed, counted): Info checks are not counted.</summary>
    public static (int Passed, int Total) Score(IEnumerable<ReadinessCheck> checks)
    {
        var list = checks.ToList();
        var ok = list.Count(c => c.State == ReadinessState.Ok);
        var att = list.Count(c => c.State == ReadinessState.Attention);
        return (ok, ok + att);
    }
}
