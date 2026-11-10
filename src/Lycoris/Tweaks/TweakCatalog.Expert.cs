using Lycoris.Core;
using Microsoft.Win32;

namespace Lycoris.Tweaks;

/// <summary>
/// Expert-only settings: changes Lycoris does not recommend (docs/research/rejected_tweaks.md explains why) but an expert may
/// still want. Each one is ADVANCED (closed in «Простой»), reversible through the journal and carries an honest warning.
/// Nothing here turns off Windows protection. Ids start with <see cref="ExpertPrefix"/>.
/// </summary>
public static partial class TweakCatalog
{
    public const string ExpertPrefix = "expert-";

    internal static readonly Guid IdleDisable = new("5d76a2ca-e8c0-402f-a133-2158492d58ad");
    internal static readonly Guid CpMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private const string SvcHost = @"SYSTEM\CurrentControlSet\Control";

    private static IEnumerable<Tweak> ExpertTweaks()
    {
        yield return PowerSettingTweak(
            id: ExpertPrefix + "cstates-idle-disable",
            title: Loc.Key("Отключить простой процессора (C-states)"),
            description: Loc.T("Запрещает процессору переходить в состояния простоя в активной схеме питания."),
            details: Loc.T("Ставит IDLEDISABLE=1 для питания от сети. Польза в замерах не обнаружена; отмена восстанавливает прежнее значение параметра активной схемы."),
            category: TweakCategory.Power,
            risk: TweakRisk.Advanced,
            defaults: ProfileSet.None,
            subgroup: SubProcessor,
            setting: IdleDisable,
            target: 1,
            describe: v => v == 0 ? Loc.T("простой разрешён") : Loc.T("простой запрещён"),
            warning: Loc.T("Lycoris это не рекомендует: в замерах пользы нет, растут нагрев и потребление, а буст одного ядра может снизиться."));

        yield return PowerSettingTweak(
            id: ExpertPrefix + "core-parking-off",
            title: Loc.Key("Отключить парковку ядер в активной схеме"),
            description: Loc.T("Ставит минимум незапаркованных логических процессоров на 100 % в активной схеме."),
            details: Loc.T("Ставит CPMINCORES=100 для питания от сети. Отмена восстанавливает прежнее значение параметра активной схемы."),
            category: TweakCategory.Power,
            risk: TweakRisk.Advanced,
            defaults: ProfileSet.None,
            subgroup: SubProcessor,
            setting: CpMinCores,
            target: 100,
            describe: v => Loc.Tf("минимум незапаркованных процессоров: {0} %", v),
            warning: Loc.T("Lycoris это не рекомендует: на Ryzen X3D и гибридных Intel принудительное отключение парковки может снизить FPS."));

        yield return new CustomTweak
        {
            Id = ExpertPrefix + "svchost-split-off",
            Title = Loc.Key("Отключить разделение служб svchost"),
            Description = Loc.T("Ставит порог разделения служб равным объёму установленной оперативной памяти."),
            Details = Loc.T("Записывает SvcHostSplitThresholdInKB в килобайтах ОЗУ. Нужна перезагрузка. Откат восстанавливает прежнее значение или его отсутствие."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: экономия составляет десятки МБ, а сбой одной службы может затронуть соседние."),
            DetectFunc = () => SystemProfile.Current.RamTotalBytes == 0
                ? TweakState.Unknown
                : LmInt(SvcHost, "SvcHostSplitThresholdInKB") == SvcHostThreshold(SystemProfile.Current)
                    ? TweakState.Applied : TweakState.NotApplied,
            ApplyAction = ctx =>
            {
                var threshold = SvcHostThreshold(ctx.System);
                if (LmInt(SvcHost, "SvcHostSplitThresholdInKB") == threshold) return;
                ctx.SetRegistry(RegHive.HKLM, SvcHost, "SvcHostSplitThresholdInKB", RegistryValueKind.DWord, threshold);
            },
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "do-background-limit",
            Title = Loc.Key("Ограничить фоновую загрузку Delivery Optimization"),
            Description = Loc.T("Ограничивает фоновую пропускную способность Delivery Optimization десятью процентами."),
            Details = Loc.T("Записывает групповую политику DOPercentageMaxBackgroundBandwidth=10. Скорость загрузки не увеличивается, а настройка в Параметрах блокируется; откат — через журнал."),
            Category = TweakCategory.Network,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: ускорения нет, а настройка оптимизации доставки в Параметрах будет заблокирована."),
            Values = [Lm(DeliveryOptimizationPolicy, "DOPercentageMaxBackgroundBandwidth", 10)],
        };

        yield return new CustomTweak
        {
            Id = ExpertPrefix + "memory-compression-off",
            Title = Loc.Key("Отключить сжатие памяти"),
            Description = Loc.T("Отключает сжатие памяти Windows командой Disable-MMAgent -MemoryCompression."),
            Details = Loc.T("Перед изменением Lycoris читает Get-MMAgent. Нужна перезагрузка. Откат включает сжатие, только если оно было включено до изменения."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: в замерах пользы нет, а при нехватке памяти чаще идут обращения к диску."),
            DetectFunc = () => ReadMemoryCompressionEnabled() ? TweakState.NotApplied : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (!ReadMemoryCompressionEnabled()) return;
                var (apply, revert) = BuildMemoryCompressionCommands(wasEnabled: true);
                var (oldValue, newValue) = Ru(() => (Loc.T("сжатие памяти включено"), Loc.T("сжатие памяти отключено")));
                RunJournaled(ctx, "memory-compression", SysTool("powershell.exe"), PowerShellArgs(apply),
                    SysTool("powershell.exe"), PowerShellArgs(revert), oldValue, newValue);
            },
        };

        yield return new CustomTweak
        {
            Id = ExpertPrefix + "reserved-storage-off",
            Title = Loc.Key("Отключить зарезервированное хранилище"),
            Description = Loc.T("Отключает зарезервированное хранилище Windows командой DISM."),
            Details = Loc.T("Перед изменением Lycoris читает Get-WindowsReservedStorageState. Откат включает хранилище, только если оно было включено до изменения."),
            Category = TweakCategory.System,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: освобождается около 7 ГБ, но крупное обновление может не установиться."),
            DetectFunc = () => ReadReservedStorageEnabled() ? TweakState.NotApplied : TweakState.Applied,
            ApplyAction = ctx =>
            {
                if (!ReadReservedStorageEnabled()) return;
                var (apply, revert) = BuildReservedStorageCommands(wasEnabled: true);
                var (oldValue, newValue) = Ru(() => (Loc.T("зарезервированное хранилище включено"), Loc.T("зарезервированное хранилище отключено")));
                RunJournaled(ctx, "reserved-storage", SysTool("powershell.exe"), PowerShellArgs(apply),
                    SysTool("powershell.exe"), PowerShellArgs(revert), oldValue, newValue);
            },
        };

        yield return new CustomTweak
        {
            Id = ExpertPrefix + "hibernation-off-laptop",
            Title = Loc.Key("Отключить гибернацию, в том числе на ноутбуке"),
            Description = Loc.T("Отключает гибернацию и быстрый запуск командой powercfg /hibernate off."),
            Details = Loc.T("Удаляется hiberfil.sys. На ноутбуке отключение питания при критическом разряде может привести к потере несохранённой работы. Откат возвращает прежний тип файла гибернации и Fast Startup."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: на ноутбуке при полном разряде батареи несохранённая работа пропадёт."),
            DetectFunc = () => ReadPowerCaps() is { } caps
                ? caps.HiberFilePresent ? TweakState.NotApplied : TweakState.Applied
                : TweakState.Unknown,
            ApplyAction = ctx =>
            {
                var caps = ReadPowerCaps() ?? throw new InvalidOperationException(Loc.T("не удалось прочитать состояние гибернации"));
                if (!caps.HiberFilePresent) return;
                if (LmInt(SessionPower, "HiberbootEnabled") != 0) SetDword(ctx, RegHive.HKLM, SessionPower, "HiberbootEnabled", 0);
                var (apply, revert) = BuildLaptopHibernationCommands(caps.HiberFileType);
                var active = PowerSchemes.GetActive();
                var hybrid = active == Guid.Empty ? null : ReadAcIndex(active,
                    new Guid("238c9fa8-0aad-41ed-83f4-97be242c8f20"), new Guid("94ac6d29-73ce-41a6-809f-6363ba21b47e"));
                RunJournaled(ctx, "hibernate", CmdExe, CmdArgs(apply), CmdExe, CmdArgs(revert),
                    Ru(() => Loc.Tf("гибернация включена, гибридный сон: {0}", HybridSleepFact(hybrid, caps.FastSystemS4))), Ru(() => Loc.T("гибернация отключена")));
            },
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "power-throttling-off",
            Title = Loc.Key("Отключить Power Throttling для всех программ"),
            Description = Loc.T("Windows перестаёт замедлять фоновые программы ради экономии энергии."),
            Details = Loc.T("Записывает PowerThrottlingOff=1. Игре в активном окне это не помогает: Windows не замедляет активную программу. " +
                      "Фоновые программы начинают потреблять больше. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: игре не помогает, на ноутбуке батарея садится быстрее."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "global-timer-resolution",
            Title = Loc.Key("Общий системный таймер, как в Windows 10"),
            Description = Loc.T("Запрос точного таймера одной программой снова действует на все программы."),
            Details = Loc.T("Записывает GlobalTimerResolutionRequests=1 (Windows 11). Игры сами запрашивают точный таймер для себя, поэтому им это " +
                      "не нужно. Пригодится только старым программам, которые рассчитывали на общий таймер. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Performance,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: FPS не растёт, компьютер тратит больше энергии."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "s3-override",
            Title = Loc.Key("Сон S3 вместо Modern Standby"),
            Description = Loc.T("Просит Windows использовать старый режим сна S3 вместо современного режима ожидания."),
            Details = Loc.T("Записывает PlatformAoAcOverride=0. Работает только если прошивка компьютера поддерживает S3; на многих новых ноутбуках " +
                      "S3 нет. Нужна перезагрузка. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Power,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = Loc.T("Lycoris это не рекомендует: ноутбук может перестать засыпать или просыпаться. Если так случилось — сделайте откат."),
            Values = [Lm(@"SYSTEM\CurrentControlSet\Control\Power", "PlatformAoAcOverride", 0)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "bg-apps-global-off",
            Title = Loc.Key("Запретить фоновую работу всем приложениям из Store"),
            Description = Loc.T("Политика Windows: приложения из Microsoft Store не работают в фоне."),
            Details = Loc.T("Записывает политику LetAppsRunInBackground=2. Переключатели фоновой работы в Параметрах станут недоступны. " +
                      "Отмена — откатом в Lycoris."),
            Category = TweakCategory.Privacy,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: перестанут приходить уведомления и срабатывать будильники, не будут работать «Связь с телефоном» и Xbox."),
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsRunInBackground", 2)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "qos-reserve-off",
            Title = Loc.Key("Снять «резерв 20 % полосы» (QoS)"),
            Description = Loc.T("Политика Windows: планировщик пакетов не резервирует полосу для приоритетного трафика."),
            Details = Loc.T("Записывает политику NonBestEffortLimit=0. Резерв используется только когда программа сама просит приоритет, " +
                      "в остальное время вся полоса и так доступна. Отмена — откатом в Lycoris."),
            Category = TweakCategory.Network,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: скорость и пинг не меняются."),
            Values = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", 0)],
        };

        yield return new RegistryTweak
        {
            Id = ExpertPrefix + "notifications-off",
            Title = Loc.Key("Выключить все уведомления Windows"),
            Description = Loc.T("Всплывающие уведомления всех программ перестают показываться."),
            Details = Loc.T("Записывает ToastEnabled=0 для текущего пользователя. Мягче — режим «Не беспокоить» в Windows: он сам включается в играх. " +
                      "Отмена — откатом в Lycoris."),
            Category = TweakCategory.Interface,
            Risk = TweakRisk.Advanced,
            Warning = Loc.T("Lycoris это не рекомендует: пропадут все уведомления, включая будильники и напоминания."),
            Values = [Cu(@"Software\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", 0)],
        };
    }

    internal static uint SvcHostThreshold(SystemProfile system) => SvcHostThreshold(system.RamTotalBytes);

    internal static uint SvcHostThreshold(ulong ramTotalBytes)
    {
        if (ramTotalBytes == 0) throw new InvalidOperationException(Loc.T("не удалось определить установленный объём оперативной памяти"));
        return checked((uint)(ramTotalBytes / 1024));
    }

    internal static (string Apply, string Revert) BuildMemoryCompressionCommands(bool wasEnabled) => wasEnabled
        ? ("Disable-MMAgent -MemoryCompression -ErrorAction Stop", "Enable-MMAgent -MemoryCompression -ErrorAction Stop")
        : ("Enable-MMAgent -MemoryCompression -ErrorAction Stop", "Disable-MMAgent -MemoryCompression -ErrorAction Stop");

    internal static (string Apply, string Revert) BuildReservedStorageCommands(bool wasEnabled) => wasEnabled
        ? (BuildDismReservedStorageCommand("Disabled"), BuildDismReservedStorageCommand("Enabled"))
        : (BuildDismReservedStorageCommand("Enabled"), BuildDismReservedStorageCommand("Disabled"));

    internal static (string Apply, string Revert) BuildLaptopHibernationCommands(byte hiberFileType)
    {
        var powercfg = SysTool("powercfg.exe");
        return ($"{powercfg} /hibernate off", hiberFileType == 1
            ? $"{powercfg} /hibernate on && {powercfg} /hibernate /type reduced"
            : $"{powercfg} /hibernate on");
    }

    private static string BuildDismReservedStorageCommand(string state) =>
        $"& $env:windir\\System32\\dism.exe /Online /Set-ReservedStorageState /State:{state}; if($LASTEXITCODE -ne 0){{throw 'DISM failed'}}";

    private static bool ReadMemoryCompressionEnabled() => ReadPowerShellBool("[bool](Get-MMAgent -ErrorAction Stop).MemoryCompression");

    private static bool ReadReservedStorageEnabled() => ReadPowerShellBool("([string](Get-WindowsReservedStorageState -ErrorAction Stop).ReservedStorageState) -eq 'Enabled'");

    private static bool ReadPowerShellBool(string script)
    {
        var result = ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(20), LogCommand = false })
            .GetAwaiter().GetResult();
        if (!result.Success) throw new InvalidOperationException(Loc.T("не удалось прочитать исходное состояние настройки"));
        var value = result.StdOut.Trim();
        if (bool.TryParse(value, out var enabled)) return enabled;
        throw new InvalidOperationException(Loc.T("Windows вернула неизвестное исходное состояние настройки"));
    }
}
