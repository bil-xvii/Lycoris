using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>A game whose anti-cheat refuses to start without Secure Boot and/or TPM 2.0.</summary>
public sealed record AntiCheatGame(string Title, string AntiCheat, bool NeedsSecureBoot, bool NeedsTpm20);

/// <summary>Active code-integrity policies read from "CiTool -lp -json". Blocklist null = could not be decided.</summary>
public sealed record CiPolicyState(bool? Blocklist, SmartAppControlState Sac);

/// <summary>
/// Boot security (Secure Boot, TPM, UEFI) and kernel protection (memory integrity, vulnerable driver blocklist,
/// Smart App Control). Information only: MONOLITH never changes firmware or these security controls; the only button
/// opens Windows Security. Sources: tech.md §7, gaming_tweaks.md §G, competitors.md §3 (readiness #6).
/// </summary>
public static partial class HealthRules
{
    public const string DriverBlocklistPolicyId = "d2bda982-ccf6-4344-ac5b-0b44427b6816";
    public const string SacEnforcePolicyId = "0283ac0f-fff1-49ae-ada1-8a933130cad6";
    public const string SacEvaluationPolicyId = "1283ac0f-fff1-49ae-ada1-8a933130cad6";
    public const string WindowsSecurityUri = "ms-settings:windowsdefender";

    // ───────────────────────────── Secure Boot / TPM (anti-cheat readiness) ─────────────────────────────

    /// <summary>
    /// Anti-cheats that require Secure Boot and TPM 2.0: Battlefield 6 (EA Javelin), Call of Duty: Black Ops 7 and
    /// Warzone (Ricochet; per support.activision.com/tpm no other current Call of Duty title needs them) and VALORANT on
    /// Windows 11 (Riot Vanguard, VAN9001/VAN9003). Matched against normalized library names: older Call of Duty games
    /// (Black Ops III, WWII, Modern Warfare…) must not match. "call of duty" / "call of duty hq" alone is the HQ app that
    /// hosts Black Ops 7 and Warzone.
    /// </summary>
    private static readonly (Func<string, bool> Matches, AntiCheatGame Game, bool Windows11Only)[] AntiCheatRules =
    [
        (n => Battlefield6Rx().IsMatch(n), new AntiCheatGame("Battlefield 6", "EA Javelin", true, true), false),
        (n => n is "call of duty" or "call of duty hq" || (n.StartsWith("call of duty", StringComparison.Ordinal) && RicochetTitleRx().IsMatch(n)),
            new AntiCheatGame("Call of Duty", "Ricochet", true, true), false),
        (n => n.Contains("valorant", StringComparison.Ordinal), new AntiCheatGame("VALORANT", "Riot Vanguard", true, true), true),
    ];

    [GeneratedRegex(@"[™®©]")] private static partial Regex TrademarkRx();
    [GeneratedRegex(@"\s+")] private static partial Regex SpacesRx();
    [GeneratedRegex(@"\bbattlefield 6\b")] private static partial Regex Battlefield6Rx();
    [GeneratedRegex(@"\b(black ops 7|warzone)\b")] private static partial Regex RicochetTitleRx();

    private static string NormalizeTitle(string name) =>
        SpacesRx().Replace(TrademarkRx().Replace(name, " "), " ").Trim().ToLowerInvariant();

    /// <summary>Games from the library whose anti-cheat needs Secure Boot / TPM 2.0 (one entry per game family).</summary>
    public static IReadOnlyList<AntiCheatGame> AntiCheatGames(IEnumerable<string> names, int osBuild)
    {
        var normalized = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(NormalizeTitle).ToList();
        return AntiCheatRules
            .Where(r => (!r.Windows11Only || osBuild >= 22000) && normalized.Any(r.Matches))
            .Select(r => r.Game)
            .ToList();
    }

    /// <summary>Output of the Confirm-SecureBootUEFI wrapper: "SB=ON" / "SB=OFF"; anything else = unknown.</summary>
    public static bool? ParseSecureBootOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        if (output.Contains("SB=ON", StringComparison.Ordinal)) return true;
        if (output.Contains("SB=OFF", StringComparison.Ordinal)) return false;
        return null;
    }

    public static HealthCheckResult? CheckBootSecurity(SystemSnapshot s)
    {
        var legacy = s.Firmware == FirmwareKind.LegacyBios;
        var secureBootMissing = legacy || s.SecureBootOn == false;
        var tpmMissing = s.Tpm is TpmState.NotFound or TpmState.V12;

        if (!secureBootMissing && !tpmMissing)
        {
            if (s.SecureBootOn != true || s.Tpm != TpmState.V20) return null; // something unknown: no verdict
            return new("boot-security", "SECURE BOOT И TPM 2.0 ВКЛЮЧЕНЫ",
                "Windows загружается в режиме UEFI с Secure Boot, TPM 2.0 доступен — требования античитов Battlefield 6, " +
                "Call of Duty и VALORANT выполнены.", CheckSeverity.Ok, 8);
        }

        var facts = new List<string>();
        if (legacy) facts.Add("Windows загружается в режиме Legacy BIOS (CSM), а Secure Boot работает только в режиме UEFI");
        else if (s.SecureBootOn == false) facts.Add("Secure Boot выключен");
        else if (s.SecureBootOn == true) facts.Add("Secure Boot включён");
        else facts.Add("состояние Secure Boot определить не удалось");
        facts.Add(s.Tpm switch
        {
            TpmState.V20 => "TPM 2.0 доступен",
            TpmState.V12 => "найден только TPM 1.2, а играм нужен TPM 2.0",
            TpmState.NotFound => "TPM не найден или выключен в BIOS (fTPM/PTT)",
            _ => "состояние TPM определить не удалось",
        });
        var detail = Capitalize(string.Join("; ", facts)) + ". ";

        var games = AntiCheatGames(s.InstalledGames, s.OsBuild)
            .Where(g => (g.NeedsSecureBoot && secureBootMissing) || (g.NeedsTpm20 && tpmMissing))
            .ToList();
        detail += games.Count > 0
            ? $"Без этого не запустятся игры из вашей библиотеки: {string.Join(", ", games.Select(g => $"{g.Title} ({g.AntiCheat})"))}. "
            : "Это требование античитов Battlefield 6 (EA Javelin), Call of Duty: Black Ops 7 и Warzone (Ricochet), а также " +
              "VALORANT на Windows 11 (Riot Vanguard). Если вы в такие игры не играете, менять ничего не нужно. ";

        var steps = new List<string>();
        if (legacy)
            steps.Add("Для Secure Boot нужен режим UEFI. Если системный диск размечен как MBR, после выключения CSM Windows не " +
                      "загрузится: сначала диск преобразуют в GPT средством Microsoft mbr2gpt (сначала проверка " +
                      "«mbr2gpt /validate /allowFullOS»), заранее сделав резервную копию");
        else if (s.SecureBootOn == false)
            steps.Add("Включите Secure Boot в BIOS/UEFI (раздел Boot или Security). Если плата спросит про ключи, выберите " +
                      "установку заводских ключей («Install default keys»), а не «Clear keys» и не Setup Mode");
        if (tpmMissing)
            steps.Add("Включите встроенный TPM в BIOS: на AMD — fTPM, на Intel — PTT (Platform Trust Technology). " +
                      "Не выбирайте «Clear TPM» или «Reset TPM»");
        steps.Add("Если включено шифрование BitLocker или шифрование устройства, сначала сохраните ключ восстановления " +
                  "(aka.ms/myrecoverykey): после изменений в BIOS Windows может его запросить");
        detail += string.Join(". ", steps) + ". MONOLITH прошивку и разметку дисков не меняет.";

        var title = (secureBootMissing, tpmMissing) switch
        {
            (true, true) => "SECURE BOOT И TPM 2.0 НЕДОСТУПНЫ",
            (true, false) => legacy ? "ЗАГРУЗКА В РЕЖИМЕ LEGACY BIOS" : "SECURE BOOT ВЫКЛЮЧЕН",
            _ => "TPM 2.0 НЕДОСТУПЕН",
        };
        // No game in the library needs it: information only, no score penalty (nothing is wrong with the PC).
        return games.Count > 0
            ? new("boot-security", title, detail, CheckSeverity.Warning, 10)
            : new("boot-security", title, detail, CheckSeverity.Info, 0);
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ───────────────────────────── Kernel protection (information only) ─────────────────────────────

    /// <summary>
    /// Parses "CiTool -lp -json": the blocklist is enforced when policy d2bda982-… is enforced; Smart App Control is On
    /// when 0283ac0f-… is enforced, Evaluation when 1283ac0f-… is. Null = empty, invalid or unexpected output.
    /// </summary>
    public static CiPolicyState? ParseCiPolicies(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(output[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || Property(doc.RootElement, "Policies") is not { ValueKind: JsonValueKind.Array } policies)
                return null;
            if (policies.GetArrayLength() == 0) return null;
            bool blocklist = false, sacOn = false, sacEvaluation = false;
            foreach (var p in policies.EnumerateArray())
            {
                if (p.ValueKind != JsonValueKind.Object) continue;
                var id = Property(p, "PolicyID") is { ValueKind: JsonValueKind.String } v ? v.GetString()?.Trim().Trim('{', '}') : null;
                if (string.IsNullOrEmpty(id)) continue;
                var enforced = Bool(Property(p, "IsEnforced")) == true && Bool(Property(p, "IsAuthorized")) != false;
                if (!enforced) continue;
                if (id.Equals(DriverBlocklistPolicyId, StringComparison.OrdinalIgnoreCase)) blocklist = true;
                else if (id.Equals(SacEnforcePolicyId, StringComparison.OrdinalIgnoreCase)) sacOn = true;
                else if (id.Equals(SacEvaluationPolicyId, StringComparison.OrdinalIgnoreCase)) sacEvaluation = true;
            }
            return new CiPolicyState(blocklist,
                sacOn ? SmartAppControlState.On : sacEvaluation ? SmartAppControlState.Evaluation : SmartAppControlState.Off);
        }
        catch (JsonException)
        {
            return null;
        }

        static JsonElement? Property(JsonElement obj, string name)
        {
            foreach (var prop in obj.EnumerateObject())
                if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return prop.Value;
            return null;
        }

        static bool? Bool(JsonElement? e) => e?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(e.Value.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static string SmartAppControlText(SmartAppControlState sac) => sac switch
    {
        SmartAppControlState.On => " Интеллектуальное управление приложениями включено: Windows блокирует неподписанные и " +
                                   "малоизвестные программы — иногда и утилиты или моды для игр.",
        SmartAppControlState.Evaluation => " Интеллектуальное управление приложениями работает в режиме оценки: Windows " +
                                           "наблюдает за программами и сама решит, включать ли его.",
        _ => "",
    };

    public static HealthCheckResult? CheckVbs(SystemSnapshot s)
    {
        if (s.Vbs == VbsState.Unknown) return null;
        var hvci = s.Vbs == VbsState.MemoryIntegrity;
        // Documented: with memory integrity or Smart App Control on, the vulnerable driver blocklist is always enforced.
        var blocklist = (hvci || s.SmartAppControl == SmartAppControlState.On) ? true : s.DriverBlocklistEnforced;
        var sac = SmartAppControlText(s.SmartAppControl);

        if (!hvci && blocklist == false)
            return new("vbs", "БЛОКИРОВКА УЯЗВИМЫХ ДРАЙВЕРОВ ВЫКЛЮЧЕНА",
                "Целостность памяти выключена, и Windows не применяет список блокировки уязвимых драйверов Microsoft: через " +
                "известные уязвимые драйверы вредоносные программы и читы получают доступ к ядру системы. Включите «Список " +
                "блокировки уязвимых драйверов» в «Безопасность Windows → Безопасность устройства → Изоляция ядра» и " +
                "перезагрузите ПК — на FPS это не влияет. Если после этого перестанет запускаться старая утилита (разгон, " +
                "подсветка), её драйвер как раз из этого списка." + sac + " MONOLITH эту защиту не меняет.",
                CheckSeverity.Warning, 8, new CheckAction(CheckActionKind.OpenUri, "БЕЗОПАСНОСТЬ WINDOWS", WindowsSecurityUri));

        switch (s.Vbs)
        {
            case VbsState.MemoryIntegrity:
                return new("vbs", "ЦЕЛОСТНОСТЬ ПАМЯТИ ВКЛЮЧЕНА",
                    "Изоляция ядра защищает Windows от вредоносных и уязвимых драйверов; список блокировки уязвимых драйверов " +
                    "при этом действует всегда. В части игр она стоит 0–10 % FPS (в среднем около 5 %), а античиты Riot Vanguard " +
                    "и FACEIT могут её требовать. MONOLITH не отключает её сам; отключение есть только как экспертный пункт в " +
                    "«Оптимизации» с отдельным предупреждением — это снижает безопасность и ломает часть античитов. Настройка — " +
                    "«Безопасность Windows → Безопасность устройства → Изоляция ядра»." + sac,
                    CheckSeverity.Info, 0);
            case VbsState.VbsOnly:
                return new("vbs", "ВИРТУАЛИЗАЦИЯ БЕЗОПАСНОСТИ АКТИВНА",
                    "Windows изолирует часть ядра в защищённой виртуальной среде (VBS). Это защита от атак на учётные данные и ядро; " +
                    "влияние на игры обычно небольшое. MONOLITH её не отключает." +
                    (blocklist == true ? " Список блокировки уязвимых драйверов Microsoft действует." : "") + sac,
                    CheckSeverity.Info, 0);
            default:
                return new("vbs", "ЦЕЛОСТНОСТЬ ПАМЯТИ ВЫКЛЮЧЕНА",
                    "Изоляция ядра не используется" +
                    (blocklist == true
                        ? ", но список блокировки уязвимых драйверов Microsoft действует — известные опасные драйверы не загрузятся. "
                        : ". ") +
                    "Целостность памяти дополнительно защищает ядро от вредоносных драйверов; античиты Riot Vanguard (режим по " +
                    "требованию) и FACEIT могут её требовать. Включается в «Безопасность Windows → Безопасность устройства → " +
                    "Изоляция ядра» (нужна перезагрузка; несовместимый старый драйвер может этому помешать)." + sac,
                    CheckSeverity.Info, 0);
        }
    }
}
