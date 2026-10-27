using System.Management;
using System.Net;
using System.Text;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>
/// Delivery Optimization, Wi-Fi power, TCP auto-tuning repair, DNS, MMCSS network throttling, TCP ACK
/// (general_tweaks.md §8, gaming_tweaks.md §2 E–F).
/// </summary>
public static partial class TweakCatalog
{
    private static readonly Guid SubWireless = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid WirelessPowerMode = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");

    private const string DeliveryOptimizationSettings = @"S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings";
    private const string DeliveryOptimizationPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    private static IEnumerable<Tweak> NetworkTweaks()
    {
        // HTTP-only Delivery Optimization through the Settings-backed value (the policy route would lock the UI).
        yield return new RegistryTweak
        {
            Id = "delivery-optimization-local",
            Title = "Обновления без P2P-раздачи",
            Description = "Windows скачивает обновления и приложения Store только с серверов Microsoft и не раздаёт их другим компьютерам.",
            Details = "Соответствует выключенному переключателю Параметры → Центр обновления Windows → Дополнительные параметры → " +
                      "Оптимизация доставки. Отключает раздачу обновлений другим ПК. По умолчанию Windows раздаёт их только в локальной сети, " +
                      "поэтому на пинг это влияет, лишь если раньше была включена раздача в интернет; сами загрузки работают как прежде. " +
                      "Политика не используется, поэтому переключатель в Параметрах остаётся доступным. Полностью вступает в силу после " +
                      "перезагрузки.",
            Category = TweakCategory.Network,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            Restart = RestartRequirement.Reboot,
            AppliesTo = _ => LmInt(DeliveryOptimizationPolicy, "DODownloadMode") is null,
            NotApplicableReason = "Режим оптимизации доставки задан групповой политикой.",
            Values = [RegSet.Dword(RegHive.HKU, DeliveryOptimizationSettings, "DownloadMode", 0)],
        };

        // SAFE-DEFAULT for AC (Recommended); the battery value is never touched.
        yield return PowerSettingTweak(
            id: "wifi-power-saving-off",
            title: "Wi-Fi без энергосбережения",
            description: "При питании от сети адаптер Wi-Fi работает в режиме максимальной производительности.",
            details: "Убирает задержки и скачки пинга, когда адаптер «засыпает» между пакетами. Меняется только значение «от сети» текущей " +
                     "схемы питания; работа от батареи на ноутбуке не меняется.",
            category: TweakCategory.Network,
            risk: TweakRisk.Safe,
            defaults: ProfileSet.Gaming | ProfileSet.Recommended,
            subgroup: SubWireless,
            setting: WirelessPowerMode,
            target: 0,
            describe: v => v switch
            {
                0 => "максимальная производительность",
                1 => "низкое энергосбережение",
                2 => "среднее энергосбережение",
                3 => "максимальное энергосбережение",
                _ => v.ToString(),
            },
            appliesTo: _ => HasWifiAdapter(),
            notApplicableReason: "Адаптер Wi-Fi не найден.");

        // SAFE-DEFAULT repair (Recommended): writes only when the level is not Windows' default "normal".
        yield return new CustomTweak
        {
            Id = "tcp-autotuning-normal",
            Title = "Автонастройка окна TCP — стандартная",
            Description = "Если «ускоритель интернета» отключил или ограничил автонастройку окна приёма TCP, возвращает стандартный уровень Windows (normal).",
            Details = "Без автонастройки одно соединение упирается в окно 64 КБ: по расчёту Microsoft на канале 1 Гбит/с с задержкой 10 мс " +
                      "это всего около 51 Мбит/с, поэтому загрузки в Steam, браузере и лаунчерах идут заметно медленнее. Уровень normal — " +
                      "значение Windows по умолчанию во всех редакциях; на исправной системе пункт ничего не меняет. Иногда автонастройку " +
                      "выключают намеренно — из-за старого роутера или межсетевого экрана, который плохо работает с масштабированием окна " +
                      "TCP (RFC 1323). Если после применения соединения стали хуже, откатите пункт: прежний уровень вернётся точно. Уровень, " +
                      "заданный групповой политикой, MONOLITH не меняет. На пинг в играх не влияет. Действует сразу для новых соединений.",
            Category = TweakCategory.Network,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Recommended,
            // Policy-managed or mixed templates are reported as "not applicable", so profiles skip them instead of failing.
            NotApplicableReason = "Уровень автонастройки задан групповой политикой или шаблоны TCP настроены по-разному — " +
                                  "MONOLITH их не меняет, потому что не сможет вернуть каждый точно.",
            DetectFunc = () => ReadTcpAutotuning() is { } s
                ? PlanTcpAutotuning(s) switch
                {
                    TcpAutotuningPlan.AlreadyNormal => TweakState.Applied,
                    TcpAutotuningPlan.Repair => TweakState.NotApplied,
                    _ => TweakState.NotApplicable,
                }
                : TweakState.Unknown,
            ApplyAction = ctx =>
            {
                var s = ReadTcpAutotuning() ?? throw new InvalidOperationException("не удалось прочитать настройки TCP");
                switch (PlanTcpAutotuning(s))
                {
                    case TcpAutotuningPlan.AlreadyNormal:
                        return;
                    case TcpAutotuningPlan.ByPolicy:
                        throw new InvalidOperationException("уровень автонастройки TCP задан групповой политикой — MONOLITH его не меняет");
                    case TcpAutotuningPlan.Mixed:
                        throw new InvalidOperationException(
                            $"шаблоны TCP настроены по-разному ({string.Join(", ", s.Templates.Select(t => $"{t.Name}: {TcpAutotuningLevels[t.Level]}"))}); " +
                            "одна команда не вернёт каждый из них точно, поэтому MONOLITH их не меняет");
                }
                var original = TcpAutotuningLevels[s.Templates[0].Level];
                var netsh = ProcessRunner.Sys("netsh.exe");
                RunJournaled(ctx, "tcp-autotuning", netsh, TcpAutotuningArgs("normal"), netsh, TcpAutotuningArgs(original),
                    $"автонастройка TCP: {original}", "автонастройка TCP: normal (по умолчанию)");
            },
        };

        // SAFE-OPTIONAL, never default.
        yield return DnsTweak(
            id: "dns-cloudflare",
            provider: "Cloudflare",
            title: "DNS-серверы Cloudflare (1.1.1.1)",
            ipv4: ["1.1.1.1", "1.0.0.1"],
            ipv6: ["2606:4700:4700::1111", "2606:4700:4700::1001"]);

        yield return DnsTweak(
            id: "dns-google",
            provider: "Google",
            title: "DNS-серверы Google (8.8.8.8)",
            ipv4: ["8.8.8.8", "8.8.4.4"],
            ipv6: ["2001:4860:4860::8888", "2001:4860:4860::8844"]);

        // ADVANCED (placebo for ping).
        yield return new RegistryTweak
        {
            Id = "network-throttling-off",
            Title = "Отключить NetworkThrottlingIndex",
            Description = "Снимает ограничение обработки сетевых пакетов, которое Windows включает во время воспроизведения мультимедиа.",
            Details = "Механизм времён Windows Vista влияет на пропускную способность сети только пока играет мультимедиа (MMCSS) и не меняет " +
                      "задержку в играх: популярное «снижение пинга» здесь — миф. Нужна перезагрузка.",
            Category = TweakCategory.Network,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Пинг в играх этот параметр не уменьшает. Изменение не опасно, но пользы для большинства систем тоже нет — " +
                      "включайте только осознанно.",
            Values = [RegSet.Dword(RegHive.HKLM, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", 0xFFFFFFFFu)],
        };

        // ADVANCED (only legacy TCP MMOs). TCPNoDelay is not documented for the Windows TCP stack, so it is not written.
        yield return new CustomTweak
        {
            Id = "tcp-ack-frequency",
            Title = "Мгновенные TCP-подтверждения",
            Description = "Для активных сетевых адаптеров Windows подтверждает каждый TCP-пакет сразу, без задержки (TcpAckFrequency = 1).",
            Details = "Может снизить задержку только в старых MMO, которые используют TCP и не отключают алгоритм Нейгла сами. Большинство игр " +
                      "работают по UDP или сами включают TCP_NODELAY — для них разницы нет, а служебных пакетов становится больше. Microsoft " +
                      "(KB 328890) советует менять параметр только осознанно. Нужна перезагрузка или переподключение адаптера.",
            Category = TweakCategory.Network,
            Risk = TweakRisk.Advanced,
            Restart = RestartRequirement.Reboot,
            Warning = "Помогает лишь отдельным старым онлайн-играм на TCP и увеличивает объём служебного трафика. Microsoft не рекомендует " +
                      "менять параметр без необходимости.",
            AppliesTo = _ => PhysicalAdapters().Count > 0,
            NotApplicableReason = "Не найдено активных физических сетевых адаптеров.",
            DetectFunc = () =>
            {
                var adapters = PhysicalAdapters();
                return StateOf(adapters.Count(a => LmInt($@"{TcpipInterfaces}\{a.Guid}", "TcpAckFrequency") == 1), adapters.Count);
            },
            ApplyAction = ctx =>
            {
                foreach (var a in PhysicalAdapters())
                {
                    var key = $@"{TcpipInterfaces}\{a.Guid}";
                    if (LmInt(key, "TcpAckFrequency") != 1) SetDword(ctx, RegHive.HKLM, key, "TcpAckFrequency", 1);
                }
            },
        };
    }

    /// <summary>
    /// Static DNS servers on every active physical adapter, IPv4 and IPv6 separately (netsh, per address family).
    /// Each family of each adapter is its own journal entry with an undo command that restores exactly what was there:
    /// DHCP, or the previous static list. A failed apply rolls itself back before reporting the error.
    /// </summary>
    private static CustomTweak DnsTweak(string id, string provider, string title, string[] ipv4, string[] ipv6) => new()
    {
        Id = id,
        Title = title,
        Description = $"Активные сетевые адаптеры переключаются на DNS-серверы {provider} (IPv4 и IPv6).",
        Details = "Помогает, только если DNS провайдера медленный или ненадёжный: сайты и лаунчеры начинают открываться быстрее. На пинг " +
                  "в играх не влияет. Прежние настройки каждого адаптера (автоматически от роутера или ручные адреса) запоминаются и " +
                  "возвращаются при откате; регистрация адреса компьютера в DNS (свойства адаптера) не меняется ни при применении, ни при " +
                  "откате. Не используйте, если у вас корпоративная сеть или VPN со своим DNS, Pi-hole/AdGuard Home " +
                  "или родительский контроль на роутере.",
        Category = TweakCategory.Network,
        Risk = TweakRisk.Optional,
        AppliesTo = _ => PhysicalAdapters().Count > 0,
        NotApplicableReason = "Не найдено активных физических сетевых адаптеров.",
        DetectFunc = () =>
        {
            var adapters = PhysicalAdapters();
            var matches = adapters.Count(a => SameServers(StaticDns(a.Guid, false), ipv4) && (!a.Ipv6 || SameServers(StaticDns(a.Guid, true), ipv6)));
            return StateOf(matches, adapters.Count);
        },
        ApplyAction = ctx =>
        {
            var adapters = PhysicalAdapters();
            if (adapters.Count == 0) throw new InvalidOperationException("нет активных физических сетевых адаптеров");
            foreach (var a in adapters)
            {
                ApplyDnsFamily(ctx, a, false, ipv4);
                if (a.Ipv6) ApplyDnsFamily(ctx, a, true, ipv6);
            }
        },
    };

    private static void ApplyDnsFamily(TweakContext ctx, NetAdapter adapter, bool ipv6, string[] servers)
    {
        var current = StaticDns(adapter.Guid, ipv6);
        if (SameServers(current, servers)) return;

        var family = ipv6 ? "ipv6" : "ipv4";
        // The interface index is not persistent (it changes when the adapter is re-enabled or replugged), so both
        // the apply and the undo find the adapter by its GUID when they run.
        RunJournaled(ctx, $"dns{(ipv6 ? 6 : 4)}:{adapter.Guid}",
            PowerShellExe, PowerShellArgs(DnsScript(family, adapter.Guid, servers, rollbackTo: current)),
            PowerShellExe, PowerShellArgs(DnsScript(family, adapter.Guid, current)),
            $"{adapter.Name} ({family}): {DescribeDns(current)}", $"{adapter.Name} ({family}): {DescribeDns(servers)}",
            OutputEncoding.Utf8);
    }

    /// <summary>
    /// PowerShell that resolves the adapter by InterfaceGuid (fails if it no longer exists), puts exactly
    /// <paramref name="servers"/> on it for one address family with netsh (empty = back to DHCP) and flushes the DNS cache.
    /// With <paramref name="rollbackTo"/>, a failure part-way puts that list back before the error is reported.
    /// </summary>
    /// <remarks>
    /// netsh always sets the adapter's dynamic DNS registration («Зарегистрировать адреса этого подключения в DNS» /
    /// «Использовать DNS-суффикс подключения при регистрации») as a side effect. The script reads both flags first and
    /// puts them back after netsh — also in the rollback — so neither the apply nor the undo changes registration.
    /// <paramref name="keepRegistration"/> = false gives the text older builds wrote (for checking their journal records).
    /// </remarks>
    internal static string DnsScript(string family, string interfaceGuid, IReadOnlyList<string> servers,
        IReadOnlyList<string>? rollbackTo = null, bool keepRegistration = true)
    {
        if (family is not ("ipv4" or "ipv6")) throw new ArgumentException("неизвестное семейство адресов: " + family, nameof(family));
        if (!Guid.TryParse(interfaceGuid, out var guid))
            throw new InvalidOperationException($"некорректный идентификатор сетевого адаптера: {interfaceGuid}");
        var set = DnsNetshCalls(family, servers);
        var keep = keepRegistration ? DnsRegistrationRestore : "";
        var sb = new StringBuilder();
        sb.Append($"$a=@(Get-NetAdapter -IncludeHidden | Where-Object {{ $_.InterfaceGuid -eq {Ps(guid.ToString("B"))} }});");
        sb.Append("if($a.Count -eq 0){throw 'сетевой адаптер не найден (отключён или удалён)'};$i=$a[0].ifIndex;");
        if (keepRegistration) sb.Append(DnsRegistrationCapture);
        sb.Append($"$n={Ps(ProcessRunner.Sys("netsh.exe"))};");
        sb.Append("function Invoke-Netsh([string[]]$x){$o=(& $n $x | Out-String);if($LASTEXITCODE -ne 0){throw ('netsh: '+$o.Trim())}};");
        if (rollbackTo is null)
            sb.Append(set).Append(keep);
        else if (keepRegistration)
            sb.Append("try{").Append(set).Append(keep).Append("}catch{$e=$_;try{").Append(DnsNetshCalls(family, rollbackTo))
              .Append("}catch{};try{").Append(keep).Append("}catch{};throw $e};");
        else
            sb.Append("try{").Append(set).Append("}catch{$e=$_;try{").Append(DnsNetshCalls(family, rollbackTo)).Append("}catch{};throw $e};");
        sb.Append($"& {Ps(ProcessRunner.Sys("ipconfig.exe"))} /flushdns | Out-Null;exit 0");
        return sb.ToString();
    }

    /// <summary>Reads the adapter's DNS registration flags before netsh runs (per interface, not per address family).</summary>
    internal const string DnsRegistrationCapture =
        "$r=@(Get-DnsClient -InterfaceIndex $i -ErrorAction Stop)[0];" +
        "$re=[bool]$r.RegisterThisConnectionsAddress;$us=[bool]$r.UseSuffixWhenRegistering;";

    /// <summary>Puts the registration flags back exactly as they were read.</summary>
    internal const string DnsRegistrationRestore =
        "Set-DnsClient -InterfaceIndex $i -RegisterThisConnectionsAddress:$re -UseSuffixWhenRegistering:$us -ErrorAction Stop;";

    /// <summary>The netsh calls of <see cref="DnsScript"/> ($i = interface index resolved at run time).</summary>
    private static string DnsNetshCalls(string family, IReadOnlyList<string> servers)
    {
        if (servers.Count == 0) return $"Invoke-Netsh @('interface','{family}','set','dnsservers',\"name=$i\",'source=dhcp');";
        // Only real IP literals ever reach the command line (values come from the registry).
        var addresses = servers.Select(s => IPAddress.TryParse(s, out var ip)
            ? ip.ToString()
            : throw new InvalidOperationException($"некорректный адрес DNS-сервера: {s}")).ToList();
        return string.Concat(addresses.Select((s, i) => i == 0
            ? $"Invoke-Netsh @('interface','{family}','set','dnsservers',\"name=$i\",'source=static','address={s}','register=primary','validate=no');"
            : $"Invoke-Netsh @('interface','{family}','add','dnsservers',\"name=$i\",'address={s}','index={i + 1}','validate=no');"));
    }

    private static string DescribeDns(IReadOnlyList<string> servers) =>
        servers.Count == 0 ? "автоматически (DHCP)" : string.Join(", ", servers);

    // ───────────────────────────── TCP receive window auto-tuning ─────────────────────────────

    /// <summary>netsh level names, indexed by MSFT_NetTCPSetting.AutoTuningLevelLocal (0 Disabled … 4 Experimental).</summary>
    public static readonly IReadOnlyList<string> TcpAutotuningLevels = ["disabled", "highlyrestricted", "restricted", "normal", "experimental"];

    private const int TcpAutotuningNormal = 3;

    /// <summary>
    /// The netsh arguments that set the auto-tuning level (System32 netsh.exe, no shell). Also the only undo line
    /// JournalTrust accepts for the "tcp-autotuning" target, for one of <see cref="TcpAutotuningLevels"/>.
    /// </summary>
    public static string TcpAutotuningArgs(string level) =>
        TcpAutotuningLevels.Contains(level)
            ? "interface tcp set global autotuninglevel=" + level
            : throw new ArgumentException("неизвестный уровень автонастройки TCP: " + level, nameof(level));

    internal sealed record TcpAutotuningState(IReadOnlyList<(string Name, int Level)> Templates, bool ByPolicy);

    internal enum TcpAutotuningPlan { AlreadyNormal, Repair, Mixed, ByPolicy }

    /// <summary>
    /// What the repair may do. netsh "global" sets every template at once, so it runs only when all templates hold the
    /// same non-default level — then one undo line restores each of them exactly.
    /// </summary>
    internal static TcpAutotuningPlan PlanTcpAutotuning(TcpAutotuningState s)
    {
        if (s.ByPolicy) return TcpAutotuningPlan.ByPolicy;
        if (s.Templates.All(t => t.Level == TcpAutotuningNormal)) return TcpAutotuningPlan.AlreadyNormal;
        return s.Templates.Select(t => t.Level).Distinct().Count() == 1 ? TcpAutotuningPlan.Repair : TcpAutotuningPlan.Mixed;
    }

    /// <summary>
    /// The local auto-tuning level of every TCP template (MSFT_NetTCPSetting, locale-independent numbers; the Automatic
    /// template has none) and whether Group Policy decides it. Null when it cannot be read.
    /// </summary>
    internal static TcpAutotuningState? ReadTcpAutotuning()
    {
        try
        {
            var templates = new List<(string, int)>();
            var byPolicy = false;
            using var searcher = SystemProfile.Searcher(@"\\.\root\StandardCimv2",
                "SELECT SettingName, AutoTuningLevelLocal, AutoTuningLevelEffective FROM MSFT_NetTCPSetting");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["AutoTuningLevelEffective"] is { } effective && Convert.ToInt32(effective) == 1) byPolicy = true;
                    if (mo["AutoTuningLevelLocal"] is not { } local) continue;
                    var level = Convert.ToInt32(local);
                    if (level < 0 || level >= TcpAutotuningLevels.Count) return null; // unknown level: never judge or change it
                    templates.Add((mo["SettingName"]?.ToString() ?? "?", level));
                }
            }
            return templates.Count == 0 ? null : new TcpAutotuningState(templates, byPolicy);
        }
        catch
        {
            return null;
        }
    }
}
