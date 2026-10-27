using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>
/// Power plans and power settings (gaming_tweaks.md §1, §2 C; general_tweaks.md §6, §11).
/// Ryzen X3D rules: a dual-CCD X3D always stays on Balanced (the 3D V-Cache driver needs it to steer games);
/// PROCTHROTTLEMIN is never touched on any X3D; core parking is never touched at all.
/// </summary>
public static partial class TweakCatalog
{
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid PcieAspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");

    private static IEnumerable<Tweak> PowerTweaks()
    {
        // SAFE-OPTIONAL: desktop, not dual-CCD X3D (DO-NOT-INCLUDE there). Pre-checked only in the Gaming profile.
        yield return new CustomTweak
        {
            Id = "power-plan-performance",
            Title = "Схема «Высокая производительность»",
            Description = "Включает стандартную схему питания «Высокая производительность»: процессор не уходит в глубокое энергосбережение в простое.",
            Details = "На современных процессорах прирост среднего FPS — 0–2 %, зато растёт потребление и нагрев в простое. На Ryzen X3D " +
                      "с одним CCD (5800X3D, 7800X3D, 9800X3D) разницы в FPS нет, и AMD рекомендует сбалансированную схему ради меньшего " +
                      "потребления — пункт можно снять. Прежняя схема запоминается и возвращается при откате.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Optional,
            DefaultIn = ProfileSet.Gaming,
            AppliesTo = sp => !sp.IsLaptop && !IsDualCcdX3D(sp),
            NotApplicableReason = "Не для ноутбуков и Ryzen X3D с двумя CCD: им нужна сбалансированная схема.",
            DetectFunc = () => SchemeState(PowerSchemes.HighPerformance),
            ApplyAction = ctx => ActivateScheme(ctx, PowerSchemes.HighPerformance,
                "схема «Высокая производительность» недоступна на этом ПК (Modern Standby оставляет только сбалансированную)"),
        };

        // ADVANCED: desktop only, not dual-CCD X3D, not Modern Standby.
        yield return new CustomTweak
        {
            Id = "power-plan-ultimate",
            Title = "Схема «Максимальная производительность»",
            Description = "Создаёт копию скрытой схемы Ultimate Performance (если её ещё нет) и включает её.",
            Details = "Для игр результат практически такой же, как у «Высокой производительности», но выше нагрев и потребление в простое. " +
                      "Если копия схемы уже есть, MONOLITH использует её. Созданную MONOLITH копию откат удалит и вернёт прежнюю схему.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Warning = "Прироста в играх по сравнению с «Высокой производительностью» почти нет, а потребление энергии и нагрев в простое " +
                      "заметно выше. Только для настольных ПК с хорошим охлаждением.",
            AppliesTo = sp => !sp.IsLaptop && !IsDualCcdX3D(sp) && !IsModernStandby(),
            NotApplicableReason = "Не для ноутбуков, систем с Modern Standby и Ryzen X3D с двумя CCD.",
            DetectFunc = () =>
            {
                var copy = PowerSchemes.FindUltimateCopy();
                return copy is { } id && !SchemeChangeNeeded(PowerSchemes.GetActive(), id, SessionHoldsPlan())
                    ? TweakState.Applied : TweakState.NotApplied;
            },
            ApplyAction = ctx =>
            {
                Guid scheme;
                Guid? created = null;
                if (PowerSchemes.FindUltimateCopy() is { } existing)
                {
                    if (!SchemeChangeNeeded(PowerSchemes.GetActive(), existing, SessionHoldsPlan())) return;
                    scheme = existing;
                }
                else
                {
                    // Only duplicate the hidden template when no copy exists; the journal deletes this copy on undo.
                    scheme = PowerSchemes.Duplicate(PowerSchemes.UltimateTemplate);
                    created = scheme;
                }
                if (!ctx.SetPowerScheme(scheme, created) && created is { } orphan)
                    PowerSchemes.Delete(orphan); // activation failed: remove the unused copy MONOLITH just made
            },
        };

        // SAFE-DEFAULT in Recommended everywhere (dual-CCD X3D systems use the dedicated item below).
        yield return new CustomTweak
        {
            Id = "power-plan-balanced",
            Title = "Сбалансированная схема питания",
            Description = "Включает стандартную схему «Сбалансированная», рекомендованную Microsoft и производителями процессоров.",
            Details = "Современные процессоры (CPPC/EPP) сами мгновенно повышают частоту под нагрузкой, поэтому в играх разница с " +
                      "«Высокой производительностью» почти незаметна, а в простое компьютер тише и экономичнее. Для Ryzen X3D с одним CCD " +
                      "это рекомендация AMD. Прежняя схема возвращается при откате.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            AppliesTo = sp => !IsDualCcdX3D(sp),
            NotApplicableReason = "Для Ryzen X3D с двумя CCD используется пункт «Схема питания для Ryzen X3D (2 CCD)».",
            DetectFunc = () => SchemeState(PowerSchemes.Balanced),
            ApplyAction = ctx => ActivateScheme(ctx, PowerSchemes.Balanced, "сбалансированная схема питания не найдена"),
        };

        // SAFE-OPTIONAL (Windows 11, Balanced plan, AC only; not on dual-CCD X3D: its interplay with the AMD driver is unverified).
        yield return new CustomTweak
        {
            Id = "power-mode-best-performance",
            Title = "Режим питания «Максимальная производительность»",
            Description = "На сбалансированной схеме выбирает режим питания «Максимальная производительность» (Параметры → Система → Питание) для работы от сети.",
            Details = "Штатный режим Windows 11 — «облегчённая схема» поверх сбалансированной: Windows смещает настройки управления процессором " +
                      "и ограничения фоновых процессов в сторону производительности, а сама схема «Сбалансированная» остаётся активной. " +
                      "Заметнее всего на ноутбуках и ПК с настройками производителя; на настольных ПК эффект обычно невелик, а потребление " +
                      "в простое немного растёт. Меняется только режим при питании от сети, режим от батареи не трогается. Применяется сразу " +
                      "через документированную функцию Windows (PowerSetUserConfiguredACPowerMode); откат возвращает прежний режим. На Ryzen " +
                      "X3D с двумя CCD не предлагается: совместимость режима с драйвером AMD 3D V-Cache не проверена.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => sp.IsWindows11 && !IsDualCcdX3D(sp),
            NotApplicableReason = "Только для Windows 11 на сбалансированной схеме питания и не для Ryzen X3D с двумя CCD.",
            DetectFunc = () =>
            {
                var scheme = PowerSchemes.GetActive();
                if (scheme == Guid.Empty) return TweakState.Unknown;
                if (scheme != PowerSchemes.Balanced) return TweakState.NotApplicable; // the mode exists only on Balanced
                return ReadAcPowerMode() is { } mode
                    ? mode == PowerModeBestPerformance ? TweakState.Applied : TweakState.NotApplied
                    : TweakState.NotApplicable;
            },
            ApplyAction = ctx =>
            {
                if (PowerSchemes.GetActive() != PowerSchemes.Balanced)
                    throw new InvalidOperationException("режим питания действует только на сбалансированной схеме — сначала включите её");
                var current = ReadAcPowerMode() ?? throw new InvalidOperationException("не удалось прочитать текущий режим питания");
                if (current == PowerModeBestPerformance) return;
                // Only the three documented modes can be journaled (and accepted by the journal check on undo).
                if (!IsDocumentedPowerMode(current))
                    throw new InvalidOperationException($"Windows сообщила неизвестный режим питания {current:D} — MONOLITH его не меняет");
                RunJournaled(ctx, "powermode:ac", PowerShellExe, PowerShellArgs(PowerModeScript(PowerModeBestPerformance)),
                    PowerShellExe, PowerShellArgs(PowerModeScript(current)),
                    $"от сети: {DescribePowerMode(current)}", $"от сети: {DescribePowerMode(PowerModeBestPerformance)}", OutputEncoding.Utf8);
            },
        };

        // SAFE-DEFAULT on dual-CCD X3D: the 3D V-Cache driver steers games to the cache CCD only on Balanced.
        yield return new CustomTweak
        {
            Id = "power-plan-balanced-x3d",
            Title = "Схема питания для Ryzen X3D (2 CCD)",
            Description = "Включает сбалансированную схему — только на ней драйвер AMD 3D V-Cache переводит игры на ядра с 3D-кэшем.",
            Details = "На Ryzen 9 X3D с двумя CCD (7900X3D, 7950X3D, 9900X3D, 9950X3D) драйвер AMD «паркует» частотный CCD во время игры " +
                      "только при сбалансированной схеме и включённом игровом режиме Windows; «Высокая» и «Максимальная производительность» это " +
                      "отключают, и игра теряет преимущество 3D-кэша. Проверьте также в BIOS: CPPC Dynamic Preferred Cores = Auto/Driver; " +
                      "если игра не распознана, отметьте её в Game Bar (Win+G) как игру. Прежняя схема возвращается при откате.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Recommended,
            AppliesTo = IsDualCcdX3D,
            NotApplicableReason = "Только для AMD Ryzen X3D с двумя CCD (7900X3D, 7950X3D, 9900X3D, 9950X3D).",
            DetectFunc = () => SchemeState(PowerSchemes.Balanced),
            ApplyAction = ctx => ActivateScheme(ctx, PowerSchemes.Balanced, "сбалансированная схема питания не найдена"),
        };

        // ADVANCED, not on X3D.
        yield return PowerSettingTweak(
            id: "cpu-min-state-100",
            title: "Минимальное состояние процессора 100 %",
            description: "В текущей схеме питания (при работе от сети) минимальное состояние процессора поднимается до 100 %.",
            details: "C-состояния и CPPC продолжают работать, поэтому частота не «закрепляется» — измеримый эффект близок к нулю, " +
                     "а потребление в простое растёт. Меняется только значение «от сети» текущей схемы; прежнее значение восстанавливается при откате.",
            category: TweakCategory.Power,
            risk: TweakRisk.Advanced,
            defaults: ProfileSet.None,
            subgroup: SubProcessor,
            setting: ProcessorMinState,
            target: 100,
            describe: v => $"{v} %",
            appliesTo: sp => !sp.CpuIsX3D,
            notApplicableReason: "Не для процессоров Ryzen X3D: параметр мешает работе драйвера AMD 3D V-Cache.",
            warning: "Эффект на производительность близок к нулю, а потребление энергии и нагрев в простое растут.");

        // SAFE-OPTIONAL (AC only).
        yield return PowerSettingTweak(
            id: "usb-selective-suspend-off",
            title: "Без отключения USB-устройств",
            description: "При питании от сети Windows перестаёт усыплять простаивающие USB-устройства.",
            details: "FPS не меняет. Помогает при пропаданиях и задержке пробуждения USB-ЦАП, беспроводных донглов мышей и геймпадов, " +
                     "VR-шлемов. Немного растёт потребление в простое. Меняется только значение «от сети» текущей схемы; режим от батареи не трогается.",
            category: TweakCategory.Power,
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            subgroup: SubUsb,
            setting: UsbSelectiveSuspend,
            target: 0,
            describe: v => v == 0 ? "запрещено" : "разрешено");

        // SAFE-OPTIONAL (AC, desktop).
        yield return PowerSettingTweak(
            id: "pcie-aspm-off",
            title: "Без энергосбережения PCI Express",
            description: "При питании от сети отключает управление питанием линий PCI Express (Link State Power Management).",
            details: "Последовательных измерений FPS нет; возможная польза — меньше микрозадержек при «пробуждении» линии видеокарты или NVMe. " +
                     "Если BIOS управляет ASPM сам, Windows этот параметр игнорирует. Немного растёт потребление в простое.",
            category: TweakCategory.Power,
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            subgroup: SubPciExpress,
            setting: PcieAspm,
            target: 0,
            describe: v => v switch { 0 => "откл.", 1 => "умеренное", 2 => "максимальное", _ => v.ToString() },
            appliesTo: sp => !sp.IsLaptop,
            notApplicableReason: "Только для настольных ПК.");

        // Desktops: SAFE-DEFAULT (Recommended). Laptops: SAFE-OPTIONAL (separate item below).
        yield return new RegistryTweak
        {
            Id = "fast-startup-off",
            Title = "Отключить быстрый запуск",
            Description = "Выключение становится полным: ядро и драйверы при включении стартуют заново, а не из файла гибернации.",
            Details = FastStartupDetails + " На настольном ПК с NVMe-диском включение дольше лишь на несколько секунд.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            AppliesTo = sp => !sp.IsLaptop,
            NotApplicableReason = "На ноутбуках используется необязательный пункт «Отключить быстрый запуск (ноутбук)».",
            Values = [Lm(SessionPower, "HiberbootEnabled", 0)],
        };

        yield return new RegistryTweak
        {
            Id = "fast-startup-off-laptop",
            Title = "Отключить быстрый запуск (ноутбук)",
            Description = "Выключение становится полным: ядро и драйверы при включении стартуют заново, а не из файла гибернации.",
            Details = FastStartupDetails + " На ноутбуке включение станет на несколько секунд дольше, поэтому по умолчанию пункт не выбран.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Optional,
            AppliesTo = sp => sp.IsLaptop,
            NotApplicableReason = "Только для ноутбуков — на настольных ПК используется основной пункт.",
            Values = [Lm(SessionPower, "HiberbootEnabled", 0)],
        };

        // Desktops: ADVANCED. Laptops: DO-NOT-INCLUDE.
        yield return new CustomTweak
        {
            Id = "hibernation-off",
            Title = "Отключить гибернацию",
            Description = "Выполняет powercfg /hibernate off: удаляется файл hiberfil.sys (около 40 % объёма ОЗУ), быстрый запуск тоже выключается.",
            Details = "Освобождает место на системном диске. Обычный сон продолжает работать. Откат выполняет powercfg /hibernate on " +
                      "и возвращает прежнее значение быстрого запуска.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Warning = "Гибернация и быстрый запуск станут недоступны. На ноутбуках пункт не предлагается: при критическом заряде Windows " +
                      "сохраняет работу через гибернацию, без неё несохранённые данные будут потеряны.",
            AppliesTo = sp => !sp.IsLaptop,
            NotApplicableReason = "На ноутбуках не предлагается: при разряде батареи Windows сохраняет работу через гибернацию.",
            DetectFunc = () => ReadPowerCaps() is { } caps
                ? caps.HiberFilePresent ? TweakState.NotApplied : TweakState.Applied
                : TweakState.Unknown,
            ApplyAction = ctx =>
            {
                var caps = ReadPowerCaps() ?? throw new InvalidOperationException("не удалось прочитать состояние гибернации");
                if (!caps.HiberFilePresent) return;
                // Fast Startup depends on hibernation: journal it explicitly so the undo restores it exactly.
                if (LmInt(SessionPower, "HiberbootEnabled") != 0) SetDword(ctx, RegHive.HKLM, SessionPower, "HiberbootEnabled", 0);
                var powercfg = SysTool("powercfg.exe");
                var revert = caps.HiberFileType == 1 // HIBERFILE_TYPE_REDUCED: the file only served Fast Startup
                    ? $"{powercfg} /hibernate on && {powercfg} /hibernate /type reduced"
                    : $"{powercfg} /hibernate on";
                RunJournaled(ctx, "hibernate", CmdExe, CmdArgs($"{powercfg} /hibernate off"), CmdExe, CmdArgs(revert),
                    "гибернация включена", "гибернация отключена");
            },
        };
    }

    // ───────────────────────────── Windows 11 power mode (powrprof, documented) ─────────────────────────────

    /// <summary>GUID_POWER_MODE_NONE: «Сбалансированный» (no overlay).</summary>
    public static readonly Guid PowerModeBalanced = Guid.Empty;
    /// <summary>GUID_POWER_MODE_BEST_EFFICIENCY.</summary>
    public static readonly Guid PowerModeBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    /// <summary>GUID_POWER_MODE_BEST_PERFORMANCE.</summary>
    public static readonly Guid PowerModeBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    /// <summary>The only values PowerSetUserConfiguredACPowerMode supports (Microsoft Learn).</summary>
    public static bool IsDocumentedPowerMode(Guid mode) =>
        mode == PowerModeBalanced || mode == PowerModeBestEfficiency || mode == PowerModeBestPerformance;

    internal static string DescribePowerMode(Guid mode) =>
        mode == PowerModeBestPerformance ? "максимальная производительность"
        : mode == PowerModeBestEfficiency ? "максимальная эффективность"
        : mode == PowerModeBalanced ? "сбалансированный"
        : mode.ToString("D");

    /// <summary>The Add-Type block that exposes PowerSetUserConfiguredACPowerMode to <see cref="PowerModeScript"/>.</summary>
    internal const string PowerModeTypeDefinition =
        "if(-not ('Monolith.PowerMode' -as [type])){Add-Type -Namespace Monolith -Name PowerMode -MemberDefinition " +
        "'[DllImport(\"powrprof.dll\", EntryPoint=\"PowerSetUserConfiguredACPowerMode\")] public static extern uint SetAc(ref Guid mode);'};";

    /// <summary>
    /// PowerShell that sets the user-configured AC power mode (one of the three documented GUIDs) and fails on any error
    /// code. Also the only undo script JournalTrust accepts for the "powermode:ac" target.
    /// </summary>
    public static string PowerModeScript(Guid mode)
    {
        if (!IsDocumentedPowerMode(mode)) throw new ArgumentException($"недокументированный режим питания: {mode:D}", nameof(mode));
        return PowerModeTypeDefinition +
               $"$g=[Guid]'{mode:D}';$r=[Monolith.PowerMode]::SetAc([ref]$g);" +
               "if($r -ne 0){throw ('Windows отклонила смену режима питания, код '+$r)}";
    }

    /// <summary>The user-configured AC power mode (Windows 11), or null when the function is missing or fails. Read-only.</summary>
    internal static Guid? ReadAcPowerMode()
    {
        try
        {
            return PowerGetUserConfiguredACPowerMode(out var mode) == 0 ? mode : null;
        }
        catch
        {
            return null; // older builds without the export, or not Windows at all
        }
    }

    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);

    private const string FastStartupDetails =
        "Быстрый запуск сохраняет состояние ядра при выключении. Без него пропадают «странности» видеокарты и USB после включения, " +
        "корректно работают dual-boot и доступ к дискам из других систем, а время работы без перезагрузки считается честно. " +
        "Действует со следующего выключения.";
}
