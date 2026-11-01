using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>Graphics, presentation, Game Mode and Game Bar (docs/research/gaming_tweaks.md §2 A–B).</summary>
public static partial class TweakCatalog
{
    private static IEnumerable<Tweak> GamingTweaks()
    {
        // SAFE-DEFAULT (ensure ON). Absent = ON, so nothing is written unless someone turned it off.
        yield return new CustomTweak
        {
            Id = "game-mode",
            Title = "Игровой режим Windows",
            Description = "Во время игры Windows не ставит драйверы через Центр обновления, не предлагает перезагрузку и отдаёт игре приоритет.",
            Details = "Штатная функция (Параметры → Игры → Игровой режим), по умолчанию включена — пункт проверяет, что её никто не отключил, " +
                      "и при необходимости включает обратно. Прирост среднего FPS в независимых тестах около нуля; польза — меньше фоновых помех во время игры. " +
                      "На Ryzen X3D с двумя CCD игровой режим нужен драйверу AMD, чтобы переводить игру на ядра с 3D-кэшем. " +
                      "Отмена — откатом в MONOLITH или тем же переключателем в Параметрах.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Recommended | ProfileSet.Streaming,
            DetectFunc = () =>
            {
                var auto = CuInt(@"Software\Microsoft\GameBar", "AutoGameModeEnabled");
                var legacy = CuInt(@"Software\Microsoft\GameBar", "AllowAutoGameMode");
                return auto is null or 1 && legacy is null or 1 ? TweakState.Applied : TweakState.NotApplied;
            },
            ApplyAction = ctx =>
            {
                if (CuInt(@"Software\Microsoft\GameBar", "AutoGameModeEnabled") is { } auto && auto != 1)
                    SetDword(ctx, RegHive.HKCU, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
                if (CuInt(@"Software\Microsoft\GameBar", "AllowAutoGameMode") is { } legacy && legacy != 1)
                    SetDword(ctx, RegHive.HKCU, @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
            },
        };

        // SAFE-DEFAULT only for RTX 40/50 when the driver reports HwSchSupported.
        yield return new CustomTweak
        {
            Id = "hags",
            Title = "Аппаратное планирование GPU",
            Description = "Видеокарта сама управляет очередью своих задач (HAGS). Без этого не работает генерация кадров DLSS на GeForce RTX 40/50.",
            Details = "Параметры → Дисплей → Графика → «Аппаратное ускорение планирования GPU». На средний FPS влияет в пределах погрешности (±5 %), " +
                      "но DLSS Frame Generation и Multi Frame Generation требуют включённого HAGS. Встречаются единичные жалобы на рывки, VR и звук — " +
                      "если заметите их, откатите пункт. Вступает в силу после перезагрузки.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming,
            Restart = RestartRequirement.Reboot,
            ExclusiveGroup = TweakGroups.Hags,
            AppliesTo = sp => HasRtx40Or50(sp) && GpuScheduling?.Supported != false,
            NotApplicableReason = "По умолчанию включается только на GeForce RTX 40/50 с поддержкой в драйвере. Для других видеокарт есть отдельный необязательный пункт.",
            DetectFunc = DetectHags,
            ApplyAction = ApplyHags,
        };

        // SAFE-OPTIONAL on every other GPU whose driver reports support.
        yield return new CustomTweak
        {
            Id = "hags-other-gpu",
            Title = "Аппаратное планирование GPU (другие видеокарты)",
            Description = "Включает HAGS на видеокарте, драйвер которой его поддерживает (кроме RTX 40/50 — для них основной пункт).",
            Details = "Эффект на FPS — в пределах погрешности. Некоторые технологии генерации кадров требуют HAGS. На старых видеокартах и в VR " +
                      "иногда появляются рывки — тогда откатите пункт. MONOLITH предлагает его только если драйвер сам сообщает о поддержке. " +
                      "Вступает в силу после перезагрузки.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Reboot,
            ExclusiveGroup = TweakGroups.Hags,
            AppliesTo = sp => !HasRtx40Or50(sp) && GpuScheduling?.Supported == true,
            NotApplicableReason = "Драйвер видеокарты не сообщает о поддержке аппаратного планирования, либо установлена RTX 40/50 — для неё основной пункт.",
            DetectFunc = DetectHags,
            ApplyAction = ApplyHags,
        };

        // SAFE-OPTIONAL (gaming_tweaks.md §2 A, hags-off): HwSchMode = 1 — the value Settings writes for «Выкл.». A fix for
        // capture/stutter problems (OBS KB), not a boost; never pre-checked. Fills the gap where the driver enabled HAGS by
        // default (no HwSchMode value) and MONOLITH had nothing of its own to revert: the journal records absent → delete.
        yield return new CustomTweak
        {
            Id = HagsOffId,
            Title = "Отключить аппаратное планирование GPU (при сбоях захвата)",
            Description = "Выключает HAGS: лечение рывков и сбоев захвата в OBS и аппаратных кодировщиках, а не ускорение.",
            Details = "База знаний OBS называет HAGS причиной проблем производительности и сбоев захвата и советует отключить его как шаг " +
                      "диагностики. Цена: DLSS Frame Generation / Multi Frame Generation на GeForce RTX 40/50 перестают работать. Если HAGS " +
                      "включил драйвер по умолчанию, раньше MONOLITH не мог его выключить с откатом — теперь может: откат удаляет значение " +
                      "(возвращается умолчание драйвера) или возвращает прежнее. Вступает в силу после перезагрузки.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Optional,
            Restart = RestartRequirement.Reboot,
            ExclusiveGroup = TweakGroups.Hags,
            AppliesTo = _ => GpuScheduling?.Supported == true,
            NotApplicableReason = "Драйвер не поддерживает аппаратное планирование — отключать нечего.",
            DetectFunc = DetectHagsOff,
            ApplyAction = ApplyHagsOff,
        };

        // SAFE-DEFAULT, Windows 11 only. Edits only its own token of DirectXUserGlobalSettings.
        yield return DxTokenTweak(
            id: "windowed-games-optimization",
            title: "Оптимизация оконных игр",
            description: "Игры на DirectX 10/11 в оконном и безрамочном режиме переходят на современную модель вывода кадров (flip model).",
            details: "Параметры → Дисплей → Графика → «Оптимизации для оконных игр». По данным Microsoft это снижает задержку вывода " +
                     "и позволяет работать VRR и Auto HDR в окне. MONOLITH меняет только флаг SwapEffectUpgradeEnable в строке настроек DirectX, " +
                     "остальные настройки не трогает. Изредка отдельная игра работает с разрывами кадров — её можно исключить в тех же Параметрах. " +
                     "Действует после перезапуска игры.",
            token: "SwapEffectUpgradeEnable",
            risk: TweakRisk.Safe,
            defaults: ProfileSet.Gaming | ProfileSet.Recommended | ProfileSet.Streaming,
            appliesTo: sp => sp.IsWindows11,
            notApplicableReason: "Только для Windows 11.");

        // SAFE-OPTIONAL.
        yield return DxTokenTweak(
            id: "vrr-optimization",
            title: "Переменная частота обновления для игр",
            description: "Windows включает VRR для игр DirectX 11, которые сами не поддерживают G-SYNC/FreeSync.",
            details: "Работает, только если монитор и драйвер поддерживают переменную частоту обновления (G-SYNC / G-SYNC Compatible / FreeSync); " +
                     "на других мониторах ничего не меняет. Не заменяет G-SYNC/FreeSync в панели драйвера. На некоторых VA- и OLED-мониторах " +
                     "возможно мерцание на экранах загрузки — тогда откатите пункт. Действует после перезапуска игры.",
            token: "VRROptimizeEnable",
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None);

        // SAFE-OPTIONAL, only while HDR is on for at least one monitor (devices_displays.md display-auto-hdr). The 24H2 Auto HDR
        // bug (safeguard 55382406) was fixed by KB5051987 = build 26100.3194.
        yield return DxTokenTweak(
            id: "auto-hdr",
            title: "Auto HDR для игр",
            description: "Windows добавляет HDR играм DirectX 11/12, которые сами его не поддерживают. Работает только на мониторе с включённым HDR.",
            details: "Параметры → Дисплей → Графика → «Auto HDR». Силу эффекта можно задать для каждой игры в Game Bar (Win+G → Параметры → " +
                     "Игровые функции → «Настроить интенсивность HDR»). Некоторые игры выглядят перенасыщенными — тогда отключите Auto HDR для них " +
                     "в Game Bar или откатите пункт. MONOLITH меняет только флаг AutoHDREnable в строке настроек DirectX, остальные настройки " +
                     "не трогает. Действует со следующего запуска игры.",
            token: "AutoHDREnable",
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            appliesTo: sp => AutoHdrBuildSupported(sp) && HdrEnabledProbe() != false,
            notApplicableReason: "Нужен включённый HDR хотя бы на одном мониторе («Устройства и сеть» → Мониторы) и Windows 11; " +
                                 "для 24H2 — с обновлением KB5051987 (февраль 2025) или новее.");

        // SAFE-DEFAULT (ensure OFF). Absent means OFF on Windows 11.
        yield return new CustomTweak
        {
            Id = "gamedvr-off",
            Title = "Фоновая запись игр выключена",
            Description = "Отключает «Записывать последние моменты» — постоянную фоновую запись видео во время игры.",
            Details = "Фоновая запись непрерывно кодирует видео (NVENC/AMF) и занимает видеопамять — это 1–5 % FPS. В Windows 11 она выключена " +
                      "по умолчанию, поэтому пункт чаще всего лишь подтверждает это. Ручная запись (Win+Alt+R) и скриншоты продолжают работать.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Recommended | ProfileSet.Streaming,
            DetectFunc = () => BackgroundRecordingOff() ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = ctx =>
            {
                if (!BackgroundRecordingOff()) SetDword(ctx, RegHive.HKCU, GameDvrKey, "HistoricalCaptureEnabled", 0);
            },
        };

        // SAFE-OPTIONAL; pre-checked only in «Стриминг и запись» (OBS KB: Game DVR off next to OBS — no second encoder, no Win+Alt+R conflicts).
        yield return new RegistryTweak
        {
            Id = "gamedvr-capture-off",
            Title = "Запись и скриншоты Game Bar",
            Description = "Отключает захват видео и скриншотов через Xbox Game Bar (Win+Alt+R, Win+Alt+PrtScn).",
            Details = "Оверлей Game Bar, игровой режим и распознавание игр продолжают работать. Если вы ничего не записываете, прироста нет — " +
                      "пункт для тех, кто пишет видео другой программой и не хочет случайных захватов. Отмена — откатом в MONOLITH " +
                      "или в Параметрах → Игры → Записи.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Streaming,
            Values =
            [
                Cu(GameDvrKey, "AppCaptureEnabled", 0),
                Cu(@"System\GameConfigStore", "GameDVR_Enabled", 0),
            ],
        };

        // ADVANCED: a fix, not a boost.
        yield return new RegistryTweak
        {
            Id = "mpo-disable",
            Title = "Отключить MPO (многоплоскостные оверлеи)",
            Description = "Исправляет мерцание, чёрные экраны и подтормаживание видео в браузере на системах с несколькими мониторами разной частоты.",
            Details = "Способ из справки NVIDIA (статья 5157). Это исправление, а не ускорение: без MPO растут нагрузка на GPU и задержка в оконном " +
                      "и безрамочном режимах. По многочисленным отчётам Windows 11 24H2 и новее игнорирует этот ключ. Применяйте только при " +
                      "описанных симптомах. Нужна перезагрузка.",
            Category = TweakCategory.Gaming,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Это исправление проблем с мерцанием, а не ускорение. Без MPO нагрузка на видеокарту и задержка в оконных играх могут вырасти. " +
                      "Включайте только если у вас мерцание или чёрные экраны на нескольких мониторах.",
            Values = [Lm(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5)],
        };
    }

    /// <summary>Auto HDR works from Windows 11; on 24H2 (26100) only with KB5051987 (UBR 3194) or newer.</summary>
    internal static bool AutoHdrBuildSupported(SystemProfile sp) => sp.IsWindows11 && !(sp.OsBuild == 26100 && sp.OsUbr < 3194);

    /// <summary>
    /// Is HDR on for at least one active monitor? A cheap CCD read (null = cannot tell, then the tweak stays offered).
    /// Tests replace it.
    /// </summary>
    internal static Func<bool?> HdrEnabledProbe { get; set; } = Monolith.Modules.Devices.Displays.DisplayInventory.AnyHdrEnabled;

    private static bool BackgroundRecordingOff()
    {
        var value = CuInt(GameDvrKey, "HistoricalCaptureEnabled");
        return value == 0 || value is null && SystemProfile.Current.IsWindows11;
    }

    private static TweakState DetectHags()
    {
        switch (LmInt(GraphicsDrivers, "HwSchMode"))
        {
            case 2: return TweakState.Applied;
            case 1: return TweakState.NotApplied;
        }
        return GpuScheduling?.Enabled == true ? TweakState.Applied : TweakState.NotApplied;
    }

    public const string HagsOffId = "hags-off";

    /// <summary>Off when HwSchMode = 1, or when no value is set and the driver reports HAGS disabled (its default).</summary>
    private static TweakState DetectHagsOff()
    {
        var mode = LmInt(GraphicsDrivers, "HwSchMode");
        if (mode == 1) return TweakState.Applied;
        if (mode is null && GpuScheduling?.Enabled == false) return TweakState.Applied;
        return TweakState.NotApplied;
    }

    private static void ApplyHagsOff(TweakContext ctx)
    {
        if (LmInt(GraphicsDrivers, "HwSchMode") == 1) return;
        SetDword(ctx, RegHive.HKLM, GraphicsDrivers, "HwSchMode", 1);
    }

    private static void ApplyHags(TweakContext ctx)
    {
        var mode = LmInt(GraphicsDrivers, "HwSchMode");
        if (mode == 2) return;
        if (mode is null && GpuScheduling?.Enabled == true) return; // already on by the driver's default
        if (GpuScheduling?.Supported == false)
            throw new InvalidOperationException("драйвер видеокарты не поддерживает аппаратное планирование GPU");
        SetDword(ctx, RegHive.HKLM, GraphicsDrivers, "HwSchMode", 2);
    }

    /// <summary>A tweak that owns one "Key=1;" token of DirectXUserGlobalSettings and edits only that token.</summary>
    private static CustomTweak DxTokenTweak(string id, string title, string description, string details, string token,
        TweakRisk risk, ProfileSet defaults, Func<SystemProfile, bool>? appliesTo = null, string? notApplicableReason = null) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Details = details,
        Category = TweakCategory.Gaming,
        Risk = risk,
        DefaultIn = defaults,
        AppliesTo = appliesTo,
        NotApplicableReason = notApplicableReason,
        DetectFunc = () =>
            GetDxToken(RegistryHelper.ReadString(RegHive.HKCU, DxPreferencesPath, DxGlobalSettings), token) == "1"
                ? TweakState.Applied
                : TweakState.NotApplied,
        ApplyAction = ctx =>
        {
            var current = GetDxToken(RegistryHelper.ReadString(RegHive.HKCU, DxPreferencesPath, DxGlobalSettings), token);
            if (current == "1") return;
            // The apply may create UserGpuPreferences (and Software\Microsoft\DirectX): the undo removes them again.
            var (createdKey, createdParent) = DxUndoOwnership(
                RegistryHelper.KeyExists(RegHive.HKCU, DxPreferencesPath),
                RegistryHelper.KeyExists(RegHive.HKCU, DxParentPath),
                ChangeJournal.Instance.ActiveRecords);
            RunJournaled(ctx, $"dx:{token}", PowerShellExe, PowerShellArgs(DxTokenScript(token, "1")),
                PowerShellExe, PowerShellArgs(DxTokenScript(token, current, createdKey: createdKey, createdParent: createdParent)),
                current is null ? $"{token}: не задан" : $"{token}={current}", $"{token}=1", OutputEncoding.Utf8);
        },
    };
}
