using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Lycoris.Core;

namespace Lycoris.Modules.Games;

/// <summary>What a priority rule change has to do to reach the wanted state.</summary>
public enum PriorityChange
{
    /// <summary>Already in the wanted state.</summary>
    None,
    /// <summary>Write (or delete) CpuPriorityClass through ChangeSession (journal + .reg backup + restore point).</summary>
    Write,
    /// <summary>Revert Lycoris's own journal record: its original already is the wanted state, so it comes back exactly.</summary>
    Revert,
}

/// <summary>
/// Persistent per-program CPU priority («Приоритет программ»). Windows reads
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\&lt;file.exe&gt;\PerfOptions\CpuPriorityClass (DWORD)
/// when a process with that file name starts and gives it that priority class: 1 Idle, 2 Normal, 3 High, 5 BelowNormal,
/// 6 AboveNormal (4 = Realtime is never written or accepted). IFEO is also a classic hijack/persistence place, so Lycoris touches
/// ONLY the CpuPriorityClass value under PerfOptions — never Debugger or any other IFEO value — and never for Windows' own
/// programs, Lycoris itself or anti-cheat programs. A rule works for every program with that file name, from its next start.
/// Pure logic (unit-tested); the registry side is <see cref="PriorityRuleStore"/>; the journal accepts back only this shape
/// (<see cref="IsRuleValue"/>, <see cref="IsRuleKeyRoot"/>).
/// </summary>
public static class PriorityRules
{
    public const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    public const string PerfOptions = "PerfOptions";
    public const string ValueName = "CpuPriorityClass";
    /// <summary>The Games page owns these records (JournalTrust allows only the GPU choice and this value for it).</summary>
    public const string JournalSource = GpuPreference.JournalSource;

    public const int Idle = 1;
    public const int Normal = 2;
    public const int High = 3;
    public const int BelowNormal = 5;
    public const int AboveNormal = 6;

    /// <summary>Levels offered in the list, highest first. «Выше/Ниже среднего» = ПО ЖЕЛАНИЮ; «Высокий»/«Низкий» = ЭКСПЕРТ.</summary>
    public static readonly IReadOnlyList<int> OfferedLevels = [High, AboveNormal, BelowNormal, Idle];

    public static string KeyPath(string exe) => $@"{IfeoPath}\{exe}\{PerfOptions}";
    public static string JournalTarget(string exe) => $@"reg:{RegHive.HKLM}\{KeyPath(exe)}\{ValueName}";

    /// <summary>Values Windows documents for CpuPriorityClass that Lycoris may read back or put back (never 4 = Realtime).</summary>
    public static bool IsKnownValue(int v) => v is Idle or Normal or High or BelowNormal or AboveNormal;
    public static bool IsOffered(int v) => OfferedLevels.Contains(v);
    /// <summary>«Высокий» can starve other programs, «Низкий» the program itself: expert mode only.</summary>
    public static bool IsExpertLevel(int v) => v is High or Idle;

    /// <summary>The level as Task Manager calls it.</summary>
    public static string Describe(int? v) => v switch
    {
        High => Loc.T("Высокий"),
        AboveNormal => Loc.T("Выше среднего"),
        Normal => Loc.T("Обычный"),
        BelowNormal => Loc.T("Ниже среднего"),
        Idle => Loc.T("Низкий"),
        null => Loc.T("нет правила"),
        _ => Loc.Tf("неизвестное значение {0}", v),
    };

    /// <summary>What the level does, in plain words (shown under the choice and in the confirmation).</summary>
    public static string Explain(int v) => v switch
    {
        High => Loc.T("Программа получает процессор раньше всех обычных. Для игры — чуть ровнее кадры при фоновой нагрузке; другие программы могут подтормаживать."),
        AboveNormal => Loc.T("Небольшой перевес над обычными программами. Мягкий вариант для игры."),
        BelowNormal => Loc.T("Программа уступает процессор остальным. Для тяжёлых фоновых задач: торрент, архиватор, рендер."),
        Idle => Loc.T("Программа работает, только когда процессор свободен. Под нагрузкой она может почти остановиться."),
        _ => "",
    };

    // ───────────────────────────── Which file names may get a rule ─────────────────────────────

    /// <summary>Windows' own programs and shells: a rule there could slow down or hang the whole system.</summary>
    private static readonly HashSet<string> SystemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso", "lsm", "svchost", "dwm", "explorer",
        "fontdrvhost", "sihost", "ctfmon", "conhost", "taskhostw", "taskhost", "runtimebroker", "audiodg", "searchindexer", "searchhost",
        "searchapp", "searchui", "startmenuexperiencehost", "shellexperiencehost", "textinputhost", "applicationframehost", "msmpeng",
        "nissrv", "mpdefendercoreservice", "securityhealthservice", "securityhealthsystray", "smartscreen", "spoolsv", "wudfhost",
        "dllhost", "rundll32", "regsvr32", "mmc", "cmd", "powershell", "powershell_ise", "pwsh", "wscript", "cscript", "mshta", "taskmgr",
        "sppsvc", "trustedinstaller", "tiworker", "consent", "logonui", "userinit", "dashost", "wmiprvse", "msiexec", "werfault",
        "wermgr", "wlanext", "lsaiso", "secure system", "memcompression", "vmmem", "vmcompute", "wsl", "wslhost", "wslservice",
        "lycoris",
    };

    /// <summary>Anti-cheat programs (a changed priority can be flagged) — matched as fragments of the name.</summary>
    private static readonly string[] AntiCheatFragments =
    [
        "anticheat", "anti-cheat", "easyanticheat", "battleye", "beservice", "faceit", "vanguard", "gameguard", "xigncode", "nprotect",
        "punkbuster", "pnkbstr", "sguard", "mhyprot", "eaac",
    ];

    /// <summary>Anti-cheat programs with short names (exact match only: «vgc» would also be inside unrelated names).</summary>
    private static readonly HashSet<string> AntiCheatNames = new(StringComparer.OrdinalIgnoreCase) { "vgc", "vgk", "vgtray", "ace-base", "acesvc" };

    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>
    /// A plain file name that can name an IFEO key: «name.exe», 5–128 characters, letters/digits and « ._-()+[]» only, no path
    /// separators, no «..», no leading/trailing space or dot before «.exe», not a device name.
    /// </summary>
    public static bool IsValidExeName(string? name)
    {
        if (name is not { Length: >= 5 and <= 128 }) return false;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        var stem = name[..^4];
        if (stem.Length == 0 || stem[0] is ' ' or '.' || stem[^1] is ' ' or '.') return false;
        if (stem.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var c in name)
            if (!(char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '(' or ')' or '+' or '[' or ']')) return false;
        var device = stem.Split('.')[0];
        return !DeviceNames.Contains(device);
    }

    public static bool IsAntiCheat(string exeName)
    {
        var stem = Stem(exeName);
        return AntiCheatNames.Contains(stem) || AntiCheatFragments.Any(f => stem.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static string Stem(string exeName) =>
        exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName[..^4] : exeName;

    /// <summary>A program of Windows itself: in the fixed list, or a file of that name in the Windows folder, System32 or SysWOW64.</summary>
    public static bool IsSystemName(string exeName, Func<string, bool>? windowsFileExists = null)
    {
        if (SystemNames.Contains(Stem(exeName))) return true;
        windowsFileExists ??= WindowsFileExists;
        try { return windowsFileExists(exeName); }
        catch { return true; } // unknown = refused
    }

    private static bool WindowsFileExists(string exeName)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) return false;
        return File.Exists(Path.Combine(windows, exeName)) || File.Exists(Path.Combine(windows, "System32", exeName))
               || File.Exists(Path.Combine(windows, "SysWOW64", exeName));
    }

    /// <summary>Null when a rule may be set for this file name; otherwise the reason in plain words.</summary>
    public static string? WhyNot(string? exeName, Func<string, bool>? windowsFileExists = null)
    {
        if (!IsValidExeName(exeName)) return Loc.T("имя файла не подходит для правила (нужно обычное имя .exe без пути и спецсимволов)");
        if (string.Equals(exeName, Path.GetFileName(AppPaths.ExecutablePath), StringComparison.OrdinalIgnoreCase) || Stem(exeName!).Equals("lycoris", StringComparison.OrdinalIgnoreCase))
            return Loc.T("это сам Lycoris");
        if (IsSystemName(exeName!, windowsFileExists)) return Loc.T("так называется программа Windows — её приоритет Lycoris не меняет");
        if (IsAntiCheat(exeName!)) return Loc.T("это античит — изменённый приоритет может помешать игре или вызвать подозрение");
        return null;
    }

    // ───────────────────────────── Journal shape (untrusted input) ─────────────────────────────

    /// <summary>
    /// A journal snapshot of exactly this rule: HKLM …\Image File Execution Options\&lt;valid non-system exe&gt;\PerfOptions,
    /// value CpuPriorityClass, original absent or a known DWORD level. Anything else under IFEO stays forbidden.
    /// </summary>
    public static bool IsRuleValue(RegistryValueSnapshot s)
    {
        if (s.Hive != RegHive.HKLM || !string.Equals(s.Name, ValueName, StringComparison.OrdinalIgnoreCase)) return false;
        if (ExeOfKeyPath(s.Path) is not { } exe || exe.Sub != PerfOptions.ToLowerInvariant()) return false;
        if (WhyNot(exe.Name) is not null) return false;
        if (!s.ValueExisted) return true;
        return s.Kind == RegistryValueKind.DWord
               && int.TryParse(s.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && IsKnownValue(v);
    }

    /// <summary>A key Lycoris may have created for a rule (and removes again when empty): IFEO\&lt;exe&gt; or IFEO\&lt;exe&gt;\PerfOptions.</summary>
    public static bool IsRuleKeyRoot(RegHive hive, string root) =>
        hive == RegHive.HKLM && ExeOfKeyPath(root) is { } exe && exe.Sub is "" or "perfoptions" && WhyNot(exe.Name) is null;

    /// <summary>«…\Image File Execution Options\name.exe[\Sub]» → (name.exe, sub in lower case); null for anything else.</summary>
    private static (string Name, string Sub)? ExeOfKeyPath(string path)
    {
        var p = path.Replace('/', '\\').Trim('\\');
        if (!p.StartsWith(IfeoPath + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = p[(IfeoPath.Length + 1)..].Split('\\');
        return rest.Length switch
        {
            1 => (rest[0], ""),
            2 => (rest[0], rest[1].ToLowerInvariant()),
            _ => null,
        };
    }

    // ───────────────────────────── Decisions ─────────────────────────────

    /// <summary>
    /// How to reach <paramref name="wanted"/> (null = no rule). When Lycoris's own active record already holds the wanted state
    /// as its original, the record is reverted (exact previous value, created keys removed); otherwise the value is written or deleted.
    /// </summary>
    public static PriorityChange Decide(int? wanted, int? current, RegistryValueSnapshot? journaledOriginal)
    {
        if (current == wanted) return PriorityChange.None;
        if (journaledOriginal is { } original)
        {
            int? before = original.ValueExisted && int.TryParse(original.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            if (original.ValueExisted == (before is not null) && before == wanted) return PriorityChange.Revert;
        }
        return PriorityChange.Write;
    }
}

/// <summary>One rule found in the registry.</summary>
public sealed record PriorityRule(string Exe, int Value, bool ByLycoris)
{
    public bool IsKnown => PriorityRules.IsKnownValue(Value);
}

/// <summary>Result of a rule change.</summary>
public sealed record PriorityRuleOutcome(int Applied, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Registry side of <see cref="PriorityRules"/>. Reading works without admin rights; every change goes through ChangeSession
/// (journal + .reg backup + restore point) under source "games", and removing Lycoris's own rule reverts its record.
/// </summary>
public static class PriorityRuleStore
{
    /// <summary>Every IFEO entry with PerfOptions\CpuPriorityClass (DWORD). Blocking, read-only; at most 2000 keys are looked at.</summary>
    public static IReadOnlyList<PriorityRule> ReadAll(ChangeJournal? journal = null)
    {
        journal ??= ChangeJournal.Instance;
        var result = new List<PriorityRule>();
        try
        {
            foreach (var exe in RegistryHelper.SubKeyNames(RegHive.HKLM, PriorityRules.IfeoPath).Take(2000))
            {
                if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (RegistryHelper.Read(RegHive.HKLM, PriorityRules.KeyPath(exe), PriorityRules.ValueName) is not int value) continue;
                result.Add(new PriorityRule(exe, value, journal.FindActive(PriorityRules.JournalTarget(exe)) is not null));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Приоритет программ: правила не прочитаны — {0}", ex.Message);
        }
        return result.OrderBy(r => r.Exe, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Sets the rule of <paramref name="exe"/> to <paramref name="level"/> (null = removes it). Admin only (the journal is
    /// admin-only). Run off the UI thread. <paramref name="journal"/>, <paramref name="windowsFileExists"/> and <paramref name="expertMode"/>
    /// are for tests (a temp journal, no real Windows folder, no settings change); the app passes none of them.
    /// </summary>
    public static async Task<PriorityRuleOutcome> SetAsync(string exe, int? level, ChangeJournal? journal = null, Func<string, bool>? windowsFileExists = null,
        bool? expertMode = null)
    {
        try
        {
            return await OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.JournalChange, Loc.Tf("Приоритет программы «{0}»", exe)) { Owner = "games" },
                _ => SetCoreAsync(exe, level, journal, windowsFileExists, expertMode ?? AppSettings.Current.ExpertMode));
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            return new PriorityRuleOutcome(0, [ex.Message]);
        }
    }

    private static async Task<PriorityRuleOutcome> SetCoreAsync(string exe, int? level, ChangeJournal? journal, Func<string, bool>? windowsFileExists,
        bool expertMode)
    {
        journal ??= ChangeJournal.Instance;
        if (PriorityRules.WhyNot(exe, windowsFileExists) is { } why) return new PriorityRuleOutcome(0, [Loc.Tf("{0}: {1}", exe, why)]);
        if (level is { } l && !PriorityRules.IsOffered(l)) return new PriorityRuleOutcome(0, [Loc.Tf("{0}: уровень {1} Lycoris не задаёт", exe, l)]);
        if (level is { } e && PriorityRules.IsExpertLevel(e) && !expertMode)
            return new PriorityRuleOutcome(0, [Loc.Tf("{0}: уровень «{1}» доступен в режиме «Эксперт»", exe, PriorityRules.Describe(e))]);
        if (!RegistryHelper.KeyExists(RegHive.HKLM, PriorityRules.IfeoPath))
            return new PriorityRuleOutcome(0, [Loc.T("в реестре нет раздела Image File Execution Options — правило не создано")]);

        var path = PriorityRules.KeyPath(exe);
        var snapshot = RegistryHelper.Snapshot(RegHive.HKLM, path, PriorityRules.ValueName);
        int? current = null;
        if (snapshot.ValueExisted)
        {
            if (snapshot.Kind != RegistryValueKind.DWord || !int.TryParse(snapshot.Data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                || !PriorityRules.IsKnownValue(v))
                return new PriorityRuleOutcome(0, [Loc.Tf("{0}: в реестре необычное значение ({1}) — Lycoris его не меняет", exe, snapshot.Describe())]);
            current = v;
        }
        // No change without an undo the journal will accept later: check the record shape before writing anything.
        var probe = new ChangeRecord { Source = PriorityRules.JournalSource, Kind = ChangeKind.Registry, Target = PriorityRules.JournalTarget(exe), Registry = snapshot };
        if (JournalTrust.Check(probe) is { } refusal)
            return new PriorityRuleOutcome(0, [Loc.Tf("{0}: журнал не принял бы отмену такого изменения ({1}) — ничего не изменено", exe, refusal)]);

        var record = journal.FindActive(PriorityRules.JournalTarget(exe));
        switch (PriorityRules.Decide(level, current, record?.Registry))
        {
            case PriorityChange.None:
                return new PriorityRuleOutcome(0, []);
            case PriorityChange.Revert when record is not null:
            {
                var r = await journal.RevertAsync([record]).ConfigureAwait(false);
                return new PriorityRuleOutcome(r.Reverted, r.Errors);
            }
        }

        // Journal titles are stored in Russian (the on-disk schema is language-neutral).
        var title = level is { } lv
            ? string.Format(Loc.Key("Игры: приоритет «{0}» для {1}"), PriorityRules.Describe(lv), exe)
            : string.Format(Loc.Key("Игры: правило приоритета для {0} удалено"), exe);
        var session = await ChangeSession.BeginAsync(title, createRestorePoint: true, journal: journal).ConfigureAwait(false);
        session.SetRegistry(RegHive.HKLM, path, PriorityRules.ValueName, RegistryValueKind.DWord, level,
            PriorityRules.JournalSource, string.Format(Loc.Key("Приоритет программы {0}"), exe));
        session.Complete();
        return new PriorityRuleOutcome(session.Applied, session.Errors);
    }
}
