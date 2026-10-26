using System.Net;
using Monolith.Core;

namespace Monolith.Tweaks;

/// <summary>Delivery Optimization, Wi-Fi power, DNS, MMCSS network throttling, TCP ACK (general_tweaks.md §8, gaming_tweaks.md §2 E–F).</summary>
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
                      "Оптимизация доставки. Убирает исходящий трафик раздачи, который может мешать онлайн-играм на слабом канале; сами загрузки " +
                      "работают как прежде. Политика не используется, поэтому переключатель в Параметрах остаётся доступным. Полностью вступает " +
                      "в силу после перезагрузки.",
            Category = TweakCategory.Network,
            Risk = TweakRisk.Safe,
            DefaultIn = ProfileSet.Gaming | ProfileSet.Recommended,
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
                  "возвращаются при откате. Не используйте, если у вас корпоративная сеть или VPN со своим DNS, Pi-hole/AdGuard Home " +
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
        var forward = DnsCommand(family, adapter.Index, servers);
        var restore = DnsCommand(family, adapter.Index, current);
        var flush = $"{SysTool("ipconfig.exe")} /flushdns";
        RunJournaled(ctx, $"dns{(ipv6 ? 6 : 4)}:{adapter.Guid}",
            CmdExe, CmdArgs($"({forward} && {flush}) || ({restore} & exit /b 1)"),
            CmdExe, CmdArgs($"{restore} && {flush}"),
            $"{adapter.Name} ({family}): {DescribeDns(current)}", $"{adapter.Name} ({family}): {DescribeDns(servers)}");
    }

    /// <summary>netsh command line that puts exactly <paramref name="servers"/> on the interface (empty = back to DHCP).</summary>
    internal static string DnsCommand(string family, int interfaceIndex, IReadOnlyList<string> servers)
    {
        var netsh = SysTool("netsh.exe");
        if (servers.Count == 0) return $"{netsh} interface {family} set dnsservers name={interfaceIndex} source=dhcp";
        // Only real IP literals ever reach the command line (values come from the registry).
        var addresses = servers.Select(s => IPAddress.TryParse(s, out var ip)
            ? ip.ToString()
            : throw new InvalidOperationException($"некорректный адрес DNS-сервера: {s}")).ToList();
        return string.Join(" && ", addresses.Select((s, i) => i == 0
            ? $"{netsh} interface {family} set dnsservers name={interfaceIndex} source=static address={s} register=primary validate=no"
            : $"{netsh} interface {family} add dnsservers name={interfaceIndex} address={s} index={i + 1} validate=no"));
    }

    private static string DescribeDns(IReadOnlyList<string> servers) =>
        servers.Count == 0 ? "автоматически (DHCP)" : string.Join(", ", servers);
}
