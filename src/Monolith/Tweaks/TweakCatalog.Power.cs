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
    /// <summary>PERFBOOSTMODE (hidden, documented on Learn: options-for-perf-state-engine-perfboostmode): 0 = Disabled.</summary>
    internal static readonly Guid ProcessorBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    internal static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    internal static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid SubPciExpress = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid PcieAspm = new("ee12f906-d277-404b-b6da-e5fa1a576df5");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    /// <summary>HYBRIDSLEEP (Allow hybrid sleep): 1 = on. Read-only here.</summary>
    private static readonly Guid HybridSleep = new("94ac6d29-73ce-41a6-809f-6363ba21b47e");
    /// <summary>RTCWAKE (Allow wake timers): 0 = disabled, 1 = enabled, 2 = important wake timers only.</summary>
    internal static readonly Guid WakeTimers = new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d");

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

        // SAFE-OPTIONAL (Windows 11, Balanced-personality plan, AC only; not on dual-CCD X3D: its interplay with the AMD driver is unverified).
        yield return PowerModeTweak(
            id: "power-mode-best-performance",
            mode: PowerModeBestPerformance,
            title: "Режим питания «Максимальная производительность»",
            description: "На сбалансированной схеме выбирает режим питания «Максимальная производительность» (Параметры → Система → Питание) для работы от сети.",
            details: "Штатный режим Windows 11 — «облегчённая схема» поверх сбалансированной: Windows смещает настройки управления процессором " +
                     "и ограничения фоновых процессов в сторону производительности, а сама схема «Сбалансированная» остаётся активной. " +
                     "Заметнее всего на ноутбуках и ПК с настройками производителя; на настольных ПК эффект обычно невелик, а потребление " +
                     "в простое немного растёт. Меняется только режим при питании от сети, режим от батареи не трогается. Применяется сразу " +
                     "через документированную функцию Windows (PowerSetUserConfiguredACPowerMode); откат возвращает прежний режим. На Ryzen " +
                     "X3D с двумя CCD не предлагается: совместимость режима с драйвером AMD 3D V-Cache не проверена.");

        // SAFE-OPTIONAL (gaming_tweaks.md §2 C): the same documented mechanism towards efficiency; never pre-selected.
        yield return PowerModeTweak(
            id: "power-mode-best-efficiency-ac",
            mode: PowerModeBestEfficiency,
            title: "Режим питания «Максимальная эффективность» от сети",
            description: "На сбалансированной схеме выбирает режим питания «Максимальная эффективность» для работы от сети: ниже частоты " +
                         "и нагрев под лёгкой нагрузкой, тише вентиляторы.",
            details: "Штатный режим Windows 11 («облегчённая схема» поверх сбалансированной): Windows смещает управление процессором и " +
                     "ограничение фоновых процессов в сторону экономии. Пиковая производительность ниже — для игр и тяжёлой работы пункт " +
                     "не подходит. Это не аналог «тихих» режимов утилит производителя: кривые вентиляторов и лимиты мощности MONOLITH не " +
                     "трогает, эффект зависит от производителя — измерьте. Меняется только режим от сети. Применяется сразу " +
                     "(PowerSetUserConfiguredACPowerMode); откат возвращает прежний режим.");

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
                     "VR-шлемов. Немного растёт потребление в простое. Меняется только значение «от сети» текущей схемы; режим от батареи не трогается. " +
                     "Если включена опция игрового сеанса «Схема питания», значение записывается и в схему «Высокая производительность» " +
                     "(и в копию Ultimate, если она есть): иначе во время игры Windows снова усыпляла бы USB-устройства — у «Высокой " +
                     "производительности» этот параметр по умолчанию включён. Каждая схема откатывается отдельной записью.",
            category: TweakCategory.Power,
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            subgroup: SubUsb,
            setting: UsbSelectiveSuspend,
            target: 0,
            describe: v => v == 0 ? "запрещено" : "разрешено",
            alsoSchemes: ExtraSchemesForBoost);

        // SAFE-OPTIONAL (gaming_tweaks.md §2 C, AC only): 1 → 2; «отключены» (0) is stricter and is never loosened.
        yield return PowerSettingTweak(
            id: "wake-timers-important-only",
            title: "ПК не просыпается ночью по расписанию программ",
            description: "При питании от сети Windows разрешает будить компьютер только важным таймерам (например, перезагрузке после " +
                         "обновления), а не любым программам.",
            details: "Обычные таймеры пробуждения ставят лаунчеры, обновляторы, резервное копирование и задачи обслуживания — ПК включается " +
                     "ночью, шумит и светит. Ночные задачи с флагом «Пробуждать компьютер для выполнения» теперь выполнятся только когда ПК " +
                     "уже работает. Устройства (мышь, сетевая карта), которые будят ПК, — отдельная причина: см. «Обслуживание → Диагностика → " +
                     "Сон и пробуждение». Меняется только значение «от сети» текущей схемы.",
            category: TweakCategory.Power,
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            subgroup: SubSleep,
            setting: WakeTimers,
            target: 2,
            describe: v => v switch { 0 => "отключены", 1 => "включены", 2 => "только важные", _ => v.ToString() },
            appliesTo: sp => !sp.IsModernStandby,
            notApplicableReason: "На ПК с Modern Standby поведение таймеров не проверено.",
            alsoSatisfied: WakeTimersAlreadyStrict);

        // SAFE-OPTIONAL (gaming_tweaks.md §2 C, AC only), never pre-selected; not on any X3D.
        yield return PowerSettingTweak(
            id: "cpu-boost-off-ac",
            title: "Без турбобуста процессора от сети",
            description: "В текущей схеме питания (от сети) отключает Turbo Boost / Precision Boost: заметно ниже мощность и температура " +
                         "пакета, тише вентиляторы.",
            details: "Самый сильный штатный рычаг Windows для тихого ПК без записи кривых вентиляторов. Цена — большая потеря однопоточной " +
                     "производительности (десятки процентов); игры, упирающиеся в процессор, станут медленнее. Параметр хранится в схеме: на " +
                     "настольном ПК игровой сеанс MONOLITH переключает схему на «Высокую производительность», где буст остаётся включённым — " +
                     "только на время игры. Известное лечение «взвывающих» вентиляторов при коротких всплесках нагрузки.",
            category: TweakCategory.Power,
            risk: TweakRisk.Optional,
            defaults: ProfileSet.None,
            subgroup: SubProcessor,
            setting: ProcessorBoostMode,
            target: 0,
            describe: DescribeBoostMode,
            appliesTo: sp => !sp.CpuIsX3D,
            notApplicableReason: "Не для Ryzen X3D: управление частотами оставлено драйверу AMD.");

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
            Details = "Освобождает место на системном диске. Обычный сон продолжает работать, но гибридный сон (на настольных ПК включён " +
                      "по умолчанию) перестаёт работать: если во время сна пропадёт питание, несохранённая работа будет потеряна — об этом " +
                      "предупреждает сама Microsoft (KB 920730). Альтернатива вручную, если нужен только быстрый запуск: powercfg /h /type " +
                      "reduced — файл вдвое меньше, но гибернация и гибридный сон при этом тоже недоступны. Откат выполняет powercfg " +
                      "/hibernate on и возвращает прежнее значение быстрого запуска.",
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Warning = "Гибернация и быстрый запуск станут недоступны. Если гибридный сон включён в текущей схеме (на настольных ПК — по " +
                      "умолчанию), он перестанет работать: при пропадании питания во время сна несохранённая работа будет потеряна " +
                      "(Microsoft, KB 920730); текущее состояние гибридного сна записывается в журнал изменений. На ноутбуках пункт не " +
                      "предлагается: при критическом заряде Windows сохраняет работу через гибернацию, без неё несохранённые данные будут потеряны.",
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
                // Read-only fact for the record: whether hybrid sleep (which hibernation off breaks, KB 920730) is in use here.
                var active = PowerSchemes.GetActive();
                var hybrid = active == Guid.Empty ? null : ReadAcIndex(active, SubSleep, HybridSleep);
                RunJournaled(ctx, "hibernate", CmdExe, CmdArgs($"{powercfg} /hibernate off"), CmdExe, CmdArgs(revert),
                    $"гибернация включена, гибридный сон: {HybridSleepFact(hybrid, caps.FastSystemS4)}", "гибернация отключена");
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

    /// <summary>
    /// A Windows 11 power-mode item (one of the documented overlay GUIDs) for a Balanced-personality plan. Detect: Applied when
    /// the user-configured AC mode equals <paramref name="mode"/>. Apply refuses undocumented current modes (no exact undo).
    /// </summary>
    private static CustomTweak PowerModeTweak(string id, Guid mode, string title, string description, string details) => new()
    {
        Id = id,
        Title = title,
        Description = description,
        Details = details,
        Category = TweakCategory.Power,
        Risk = TweakRisk.Optional,
        AppliesTo = sp => sp.IsWindows11 && !IsDualCcdX3D(sp),
        NotApplicableReason = "Только для Windows 11 на сбалансированной схеме питания и не для Ryzen X3D с двумя CCD.",
        DetectFunc = () =>
        {
            var scheme = PowerSchemes.GetActive();
            if (scheme == Guid.Empty) return TweakState.Unknown;
            // The mode exists only on Balanced or a plan derived from it (Microsoft: customize-power-slider).
            if (!PowerSchemes.IsBalancedPersonality(scheme)) return TweakState.NotApplicable;
            return ReadAcPowerMode() is { } current
                ? current == mode ? TweakState.Applied : TweakState.NotApplied
                : TweakState.NotApplicable;
        },
        ApplyAction = ctx =>
        {
            if (!PowerSchemes.IsBalancedPersonality(PowerSchemes.GetActive()))
                throw new InvalidOperationException("режим питания действует только на сбалансированной схеме — сначала включите её");
            var current = ReadAcPowerMode() ?? throw new InvalidOperationException("не удалось прочитать текущий режим питания");
            if (current == mode) return;
            // Only the three documented modes can be journaled (and accepted by the journal check on undo).
            if (!IsDocumentedPowerMode(current))
                throw new InvalidOperationException($"Windows сообщила неизвестный режим питания {current:D} — MONOLITH его не меняет");
            RunJournaled(ctx, "powermode:ac", PowerShellExe, PowerShellArgs(PowerModeScript(mode)),
                PowerShellExe, PowerShellArgs(PowerModeScript(current)),
                $"от сети: {DescribePowerMode(current)}", $"от сети: {DescribePowerMode(mode)}", OutputEncoding.Utf8);
        },
    };

    /// <summary>
    /// Hybrid sleep as the journal record states it: it needs FastSystemS4 (SYSTEM_POWER_CAPABILITIES; absent e.g. with an
    /// active hypervisor) and the active plan's HYBRIDSLEEP AC value of 1.
    /// </summary>
    internal static string HybridSleepFact(uint? hybridSleepAc, bool fastSystemS4)
    {
        if (!fastSystemS4) return "недоступен (например, активен гипервизор)";
        return hybridSleepAc switch { 1 => "включён в текущей схеме", null => "неизвестно", _ => "выключен в текущей схеме" };
    }

    /// <summary>RTCWAKE 0 («отключены») already keeps every program timer from waking the PC: stricter than 2, never loosened.</summary>
    internal static bool WakeTimersAlreadyStrict(uint value) => value == 0;

    /// <summary>PERFBOOSTMODE values as powercfg names them (0–4 on Learn; 5 and 6 appear in powercfg /qh on newer builds).</summary>
    internal static string DescribeBoostMode(uint v) => v switch
    {
        0 => "отключён",
        1 => "включён",
        2 => "агрессивный",
        3 => "эффективный",
        4 => "эффективный агрессивный",
        5 => "агрессивный при гарантии",
        6 => "эффективный агрессивный при гарантии",
        _ => v.ToString(),
    };

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
