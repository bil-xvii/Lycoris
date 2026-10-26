using Monolith.Core;

namespace Monolith.Modules.Games;

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
            ? new ReadinessCheck("ИГРОВОЙ РЕЖИМ WINDOWS", "Включён", ReadinessState.Ok,
                "Windows отдаёт приоритет запущенной игре и не ставит драйверы во время игры.")
            : new ReadinessCheck("ИГРОВОЙ РЕЖИМ WINDOWS", "Выключен", ReadinessState.Attention,
                "Включите в разделе «Оптимизация»: это безопасно и сразу работает при следующем запуске игры.");
    }

    /// <summary>D3DKMT WDDM_2_7_CAPS first; the HwSchMode registry value (2 on, 1 off) only as a fallback.</summary>
    public static ReadinessCheck Hags(SystemProfile sp)
    {
        const string title = "АППАРАТНОЕ ПЛАНИРОВАНИЕ GPU";
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
            GamesNative.HagsState.On => new ReadinessCheck(title, "Включено", ReadinessState.Ok,
                "Нужно для генерации кадров DLSS (Frame Generation). На средний FPS почти не влияет."),
            GamesNative.HagsState.Off when frameGen => new ReadinessCheck(title, "Выключено", ReadinessState.Attention,
                "Для RTX 40/50 нужно для генерации кадров DLSS. Включается в «Оптимизации», нужна перезагрузка."),
            GamesNative.HagsState.Off => new ReadinessCheck(title, "Выключено", ReadinessState.Info,
                "Прирост FPS в пределах погрешности — включать по желанию."),
            GamesNative.HagsState.NotSupported => new ReadinessCheck(title, "Не поддерживается", ReadinessState.Info,
                "Видеокарта или драйвер не поддерживают эту функцию."),
            _ => new ReadinessCheck(title, "Не удалось определить", ReadinessState.Info,
                "Драйвер не сообщил состояние; значение реестра не задано (значит, по умолчанию)."),
        };
    }

    /// <summary>HKCU\…\GameDVR HistoricalCaptureEnabled: 1 = background recording on; absent = off (gaming_tweaks.md B).</summary>
    public static ReadinessCheck BackgroundRecording() =>
        BackgroundRecordingFromValue(InteractiveUser.ReadInt(@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled"));

    public static ReadinessCheck BackgroundRecordingFromValue(int? value) => value == 1
        ? new ReadinessCheck("ФОНОВАЯ ЗАПИСЬ ИГР", "Включена", ReadinessState.Attention,
            "Постоянная запись последних минут игры отнимает 1–5 % FPS и видеопамять. Выключается в «Оптимизации».")
        : new ReadinessCheck("ФОНОВАЯ ЗАПИСЬ ИГР", "Выключена", ReadinessState.Ok,
            "Видеокарта не тратит ресурсы на постоянную запись.");

    public static ReadinessCheck PowerPlan(SystemProfile sp)
    {
        const string title = "ПЛАН ПИТАНИЯ";
        var active = PowerSchemes.GetActive();
        var name = active == Guid.Empty ? "Не удалось определить" : PowerSchemes.GetName(active);
        if (BoostPolicy.IsDualCcdX3D(sp.CpuName, sp.CpuCores))
        {
            return active == PowerSchemes.Balanced
                ? new ReadinessCheck(title, name, ReadinessState.Ok,
                    "Для Ryzen X3D с двумя CCD AMD рекомендует именно эту схему.")
                : new ReadinessCheck(title, name, ReadinessState.Attention,
                    "Для Ryzen X3D с двумя CCD AMD рекомендует «Сбалансированную»: иначе игра может попасть не на тот CCD.");
        }
        if (active == PowerSchemes.PowerSaver)
            return new ReadinessCheck(title, name, ReadinessState.Attention,
                "Экономия энергии ограничивает частоты процессора — для игр не подходит.");
        if (GamesNative.IsOnBattery())
            return new ReadinessCheck(title, name, ReadinessState.Info, "Ноутбук работает от батареи — для игр подключите зарядку.");
        if (active == PowerSchemes.Balanced)
            return new ReadinessCheck(title, name, ReadinessState.Info,
                sp.IsLaptop
                    ? "Подходит для игр: разница с «Высокой производительностью» обычно 0–2 % FPS. На ноутбуке игровой сеанс схему не меняет — выберите режим питания «Максимальная производительность» в параметрах Windows."
                    : "Подходит для игр: разница с «Высокой производительностью» обычно 0–2 % FPS. Игровой сеанс MONOLITH переключает схему на время игры.");
        return new ReadinessCheck(title, name, ReadinessState.Info,
            "Максимальные частоты процессора; в простое компьютер потребляет больше энергии.");
    }

    public static ReadinessCheck Gpu(SystemProfile sp, DateTime now)
    {
        var gpu = sp.PrimaryGpu;
        if (gpu is null)
            return new ReadinessCheck("ВИДЕОКАРТА", "Не определена", ReadinessState.Info, "Сведения о видеокарте ещё собираются или недоступны.");
        return GpuFromInfo(gpu.Name, gpu.DriverVersion, gpu.DriverDate, now);
    }

    public static ReadinessCheck GpuFromInfo(string name, string version, DateTime? driverDate, DateTime now)
    {
        const string title = "ВИДЕОКАРТА";
        var shortName = ShortGpuName(name);
        var ver = string.IsNullOrWhiteSpace(version) ? "" : version.Trim();
        if (driverDate is not { } date || date.Year < 2000)
            return new ReadinessCheck(title, shortName, ReadinessState.Info,
                $"{name}. Дата драйвера не указана.", ver.Length > 0 ? $"Драйвер {ver}" : null);
        var days = Math.Max(0, (int)(now.Date - date.Date).TotalDays);
        var detail = $"Драйвер {(ver.Length > 0 ? ver + " · " : "")}{date:dd.MM.yyyy}";
        return days > DriverAgeAttentionDays
            ? new ReadinessCheck(title, shortName, ReadinessState.Attention,
                $"Драйверу {days} {GameText.Plural(days, "день", "дня", "дней")} — проверьте обновление в разделе «Драйверы».", detail)
            : new ReadinessCheck(title, shortName, ReadinessState.Ok,
                $"{name}: драйвер установлен {days} {GameText.Plural(days, "день", "дня", "дней")} назад.", detail);
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
    public static string SummaryLine(int passed, int total) => $"Готовность к играм: {passed}/{total} — на Панели";

    /// <summary>(passed, counted): Info checks are not counted.</summary>
    public static (int Passed, int Total) Score(IEnumerable<ReadinessCheck> checks)
    {
        var list = checks.ToList();
        var ok = list.Count(c => c.State == ReadinessState.Ok);
        var att = list.Count(c => c.State == ReadinessState.Attention);
        return (ok, ok + att);
    }
}
