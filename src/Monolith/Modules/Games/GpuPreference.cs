using System.Globalization;
using System.IO;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>What a per-game GPU switch has to do to reach the wanted state.</summary>
public enum GpuChange
{
    /// <summary>Already in the wanted state.</summary>
    None,
    /// <summary>Write the merged preference string (journaled through ChangeSession).</summary>
    Write,
    /// <summary>Revert MONOLITH's own journal record: its original already is the wanted state, so it comes back exactly.</summary>
    Revert,
}

/// <summary>
/// Windows' own per-app GPU preference (Settings → System → Display → Graphics → app → High performance) for
/// hybrid PCs (iGPU + dGPU): HKCU\Software\Microsoft\DirectX\UserGpuPreferences, value = full exe path,
/// REG_SZ "GpuPreference=N;" (0 = let Windows decide, 1 = power saving, 2 = high performance), other tokens of the
/// same value (SwapEffectUpgradeEnable, AutoHDREnable…) kept as they are. docs/research/gaming_tweaks.md
/// §gpu-preference-per-app: SAFE-DEFAULT on hybrid systems, only for exes detected as games.
/// Pure logic (unit-tested); the registry access lives in <see cref="GpuPreferenceStore"/>.
/// </summary>
public static class GpuPreference
{
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public const string ParentPath = @"Software\Microsoft\DirectX";
    public const string Token = "GpuPreference";
    public const int Auto = 0;
    public const int PowerSaving = 1;
    public const int HighPerformance = 2;
    /// <summary>Windows 10 1803: the first build with the per-app GPU preference.</summary>
    public const int MinBuild = 17134;
    /// <summary>Journal source of these records (JournalTrust allows only this key for it).</summary>
    public const string JournalSource = "games";

    /// <summary>The journal target ChangeSession.SetRegistry uses for this exe.</summary>
    public static string JournalTarget(string exe) => $@"reg:{RegHive.HKCU}\{KeyPath}\{exe}";

    /// <summary>
    /// The switch is shown only where the preference matters: two or more real GPUs (integrated + discrete) and
    /// Windows 10 1803+. Virtual and remote display adapters (vendor Microsoft/other) do not count.
    /// </summary>
    public static bool IsHybrid(IEnumerable<GpuInfo> gpus, int osBuild) =>
        osBuild >= MinBuild && gpus.Count(g => g.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel) >= 2;

    /// <summary>The GpuPreference token of a value ("GpuPreference=2;SwapEffectUpgradeEnable=1;" → 2); null when absent.</summary>
    public static int? Parse(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        foreach (var raw in data.Split(';'))
        {
            if (!TryToken(raw, out var value)) continue;
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        return null;
    }

    /// <summary>
    /// The value with GpuPreference set to <paramref name="preference"/>: an existing token is replaced in place
    /// (a duplicate dropped), otherwise one is appended; every other token stays exactly as it was.
    /// </summary>
    public static string Merge(string? existing, int preference)
    {
        var tokens = new List<string>();
        var replaced = false;
        foreach (var raw in (existing ?? "").Split(';'))
        {
            if (raw.Trim().Length == 0) continue;
            if (TryToken(raw, out _))
            {
                if (!replaced) tokens.Add($"{Token}={preference.ToString(CultureInfo.InvariantCulture)}");
                replaced = true;
                continue;
            }
            tokens.Add(raw);
        }
        if (!replaced) tokens.Add($"{Token}={preference.ToString(CultureInfo.InvariantCulture)}");
        return string.Join(";", tokens) + ";";
    }

    private static bool TryToken(string raw, out string value)
    {
        value = "";
        var eq = raw.IndexOf('=');
        if (eq <= 0 || !raw[..eq].Trim().Equals(Token, StringComparison.OrdinalIgnoreCase)) return false;
        value = raw[(eq + 1)..].Trim();
        return true;
    }

    /// <summary>
    /// How to reach the wanted state. When MONOLITH's own active record already holds the wanted state as its
    /// original, the record is reverted — the exact previous string comes back (or the value is deleted when it was
    /// absent) instead of stacking a second change on top. Otherwise the merged string is written and journaled.
    /// </summary>
    public static GpuChange Decide(bool enableHigh, int? currentPreference, RegistryValueSnapshot? journaledOriginal)
    {
        if ((currentPreference == HighPerformance) == enableHigh) return GpuChange.None;
        if (journaledOriginal is { } original)
        {
            var originalHigh = original.ValueExisted && Parse(original.Data) == HighPerformance;
            if (originalHigh == enableHigh) return GpuChange.Revert;
        }
        return GpuChange.Write;
    }

    /// <summary>
    /// An absolute local exe path that can safely name a preference value (what the journal accepts back):
    /// "X:\…\name.exe", no control characters, quotes, wildcards or stream suffixes. UNC paths are not used.
    /// </summary>
    public static bool IsValidExePath(string? path) =>
        path is { Length: > 7 and < 1024 }
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\'
        && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && !path.Any(char.IsControl)
        && path.IndexOfAny(['"', '*', '?', '<', '>', '|', '/']) < 0
        && path.IndexOf(':', 2) < 0
        && !path.Contains(@"\\", StringComparison.Ordinal)
        && !path.Contains(@"\.\", StringComparison.Ordinal) && !path.Contains(@"\..\", StringComparison.Ordinal);

    /// <summary>
    /// True when a preference value (named by an exe path) belongs to a game: its own exe, or any exe inside its
    /// install folder when that folder identifies exactly one game (<see cref="GamePaths.IsSpecificDir"/>).
    /// </summary>
    public static bool BelongsTo(string valueName, string? exactExe, string normalizedDir, bool dirIsSpecific)
    {
        if (!valueName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (exactExe is not null && string.Equals(valueName, exactExe, StringComparison.OrdinalIgnoreCase)) return true;
        return dirIsSpecific && GamePaths.IsUnder(valueName, normalizedDir);
    }

    /// <summary>
    /// The game's own exe when MONOLITH knows it reliably: a user-added game (the user picked it) or GOG's primary
    /// play task inside the game folder. Steam, Epic and Ubisoft start games by URL, EA/Battle.net entries are often
    /// launcher stubs and Riot's exe is the Riot Client — setting the preference on those would do nothing for the game.
    /// </summary>
    public static string? KnownExe(GameEntry g, string normalizedDir)
    {
        if (g.ExePath is not { } exe) return null;
        return g.Platform switch
        {
            GamePlatform.Custom => exe,
            GamePlatform.Gog when GamePaths.IsUnder(exe, normalizedDir) => exe,
            _ => null,
        };
    }

    /// <summary>File name of a Windows path (also on other platforms, for tests).</summary>
    public static string FileName(string path)
    {
        var i = path.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static readonly string[] NotTheGame =
    [
        "launcher", "setup", "install", "unins", "redist", "vcredist", "vc_redist", "dxsetup", "directx", "dotnet",
        "prereq", "crash", "report", "update", "patch", "config", "settings", "server", "editor", "benchmark", "helper",
        "service", "easyanticheat", "battleye", "beservice", "cefprocess", "webhelper", "uploader", "touchup", "cleanup",
    ];

    /// <summary>
    /// Pre-selection for the "which exe is the game" file dialog: the largest exe that is not an installer, launcher,
    /// crash reporter or anti-cheat. Only a suggestion — the user confirms the file and then the exact change.
    /// </summary>
    public static string? GuessExe(IEnumerable<(string Path, long Size)> exes) =>
        exes.Where(e => e.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(e => !NotTheGame.Any(f => FileName(e.Path).Contains(f, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Size)
            .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Path)
            .FirstOrDefault();

    /// <summary>"GpuPreference=2;" → «высокая производительность» (what Settings calls it).</summary>
    public static string Describe(int? preference) => preference switch
    {
        HighPerformance => Loc.T("высокая производительность"),
        PowerSaving => Loc.T("энергосбережение"),
        Auto or null => Loc.T("Windows решает сама"),
        _ => Loc.Tf("неизвестное значение {0}", preference),
    };

    /// <summary>"дискретной видеокарте" / "дискретной видеокарте (RTX 4070)" — the target of the high-performance choice, in the dative.</summary>
    public static string DiscreteCardPhrase(string? dgpuName) =>
        string.IsNullOrWhiteSpace(dgpuName) ? Loc.T("дискретной видеокарте") : Loc.Tf("дискретной видеокарте ({0})", dgpuName);

    /// <summary>A registry string for the confirm dialog: «параметра нет» when the value is absent.</summary>
    public static string ShowValue(string? data) => data is null ? Loc.T("параметра нет") : $"«{data}»";

    /// <summary>Tooltip of a card's switch: the current state in plain words, then what the switch does (or why it is read-only).</summary>
    public static string CardTooltip(IReadOnlyList<GpuPreferenceValue> values, string? dgpuName, bool elevated, bool writable)
    {
        var gpu = DiscreteCardPhrase(dgpuName);
        var high = values.Where(v => v.IsHigh).Select(v => FileName(v.Exe)).ToList();
        var saving = values.Where(v => v.Preference == PowerSaving).Select(v => FileName(v.Exe)).ToList();
        var state = high.Count > 0
            ? Loc.Tf("Включено: Windows запускает {0} на {1}.", string.Join(", ", high), gpu)
            : saving.Count > 0
                ? Loc.Tf("Сейчас в параметрах Windows для {0} выбрано энергосбережение — игра идёт на встроенной видеокарте.", string.Join(", ", saving))
                : Loc.T("Сейчас видеокарту выбирает Windows — на ноутбуке игра может запуститься на встроенной и работать медленнее.");
        var action = !elevated
            ? Loc.T("Только просмотр: менять можно с правами администратора.")
            : !writable
                ? Loc.T("MONOLITH запущен от другой учётной записи администратора, а эта настройка принадлежит вашей — измените её в «Параметры → Система → Дисплей → Графика».")
                : high.Count > 0
                    ? Loc.T("Выключите, чтобы вернуть прежний выбор: если включал MONOLITH, вернётся ровно то значение, что было до него.")
                    : Loc.Tf("Включите, чтобы игра всегда запускалась на {0} — как «Высокая производительность» в параметрах графики Windows. " +
                      "Подействует со следующего запуска игры; от батареи ноутбук разрядится быстрее. Отменяется этим же переключателем.", gpu);
        return state + " " + action;
    }
}

/// <summary>One preference value that belongs to a game card.</summary>
public sealed record GpuPreferenceValue(string Exe, string Data)
{
    public int? Preference => GpuPreference.Parse(Data);
    public bool IsHigh => Preference == GpuPreference.HighPerformance;
}

/// <summary>Result of switching the GPU preference of one game.</summary>
public sealed record GpuChangeOutcome(int Applied, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Registry side of <see cref="GpuPreference"/>. Reading is allowed without admin rights; every change goes through
/// ChangeSession (journal + .reg backup) under source "games", and turning a switch back reverts MONOLITH's own
/// record so the exact previous string returns.
/// </summary>
public static class GpuPreferenceStore
{
    /// <summary>
    /// Where the interactive user's preferences live, and whether MONOLITH may change them. When UAC elevated
    /// MONOLITH with another admin account, HKCU is that admin's hive: the player's own key (HKU\&lt;SID&gt;) is shown
    /// read-only and the switch is left to Windows Settings.
    /// </summary>
    public static (RegHive Hive, string Path, bool Writable) Location()
    {
        var (hive, prefix) = InteractiveUser.Hive;
        return (hive, prefix + GpuPreference.KeyPath, hive == RegHive.HKCU && prefix.Length == 0);
    }

    /// <summary>Every per-app value (exe path → data) of the interactive user. Blocking, read-only.</summary>
    public static Dictionary<string, string> ReadAll()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (hive, path, _) = Location();
            using var b = RegistryHelper.OpenBase(hive);
            using var k = b.OpenSubKey(path, false);
            if (k is null) return result;
            foreach (var name in k.GetValueNames())
            {
                // DirectXUserGlobalSettings and anything that is not an exe path is not a per-game value.
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.GetValueKind(name) != RegistryValueKind.String) continue;
                result[name] = k.GetValue(name)?.ToString() ?? "";
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Настройки видеокарты для игр не прочитаны: {0}", ex.Message);
        }
        return result;
    }

    /// <summary>The values that belong to a game (see <see cref="GpuPreference.BelongsTo"/>).</summary>
    public static IReadOnlyList<GpuPreferenceValue> ValuesFor(GameEntry g, IReadOnlyDictionary<string, string> all)
    {
        if (all.Count == 0) return [];
        var dir = GamePaths.NormalizeDir(g.InstallDir);
        var specific = GamePaths.IsSpecificDir(dir);
        var exact = GpuPreference.KnownExe(g, dir);
        return all.Where(kv => GpuPreference.BelongsTo(kv.Key, exact, dir, specific))
                  .Select(kv => new GpuPreferenceValue(kv.Key, kv.Value))
                  .OrderBy(v => v.Exe, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    /// <summary>Exe candidates inside the game folder for the file dialog's pre-selection. Blocking, bounded, read-only.</summary>
    public static string? GuessExe(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return null;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Offline,
            };
            var found = new List<(string, long)>();
            foreach (var f in Directory.EnumerateFiles(installDir, "*.exe", options))
            {
                try { found.Add((f, new FileInfo(f).Length)); } catch { }
                if (found.Count >= 400) break;
            }
            return GpuPreference.GuessExe(found);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Sets (<paramref name="enableHigh"/>) or clears the high-performance preference of these exes. Admin only
    /// (the journal is admin-only), HKCU of the same account only. Run off the UI thread.
    /// <paramref name="journal"/> is for tests (a temp journal); the app always uses the shared one.
    /// </summary>
    public static async Task<GpuChangeOutcome> SetAsync(IReadOnlyList<string> exes, bool enableHigh, string gameName,
        ChangeJournal? journal = null)
    {
        try
        {
            return await OperationCoordinator.Instance.RunAsync(
                new OperationRequest(OperationClass.JournalChange, Loc.Tf("Видеокарта для игры «{0}»", gameName)) { Owner = "games" },
                _ => SetCoreAsync(exes, enableHigh, gameName, journal));
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            return new GpuChangeOutcome(0, [ex.Message]);
        }
    }

    private static async Task<GpuChangeOutcome> SetCoreAsync(IReadOnlyList<string> exes, bool enableHigh, string gameName,
        ChangeJournal? journal)
    {
        journal ??= ChangeJournal.Instance;
        var errors = new List<string>();
        var applied = 0;
        if (!Location().Writable)
            return new GpuChangeOutcome(0, [Loc.T("MONOLITH запущен от другой учётной записи — измените настройку в параметрах Windows.")]);

        var writes = new List<(string Exe, string Data)>();
        var reverts = new List<ChangeRecord>();
        foreach (var exe in exes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!GpuPreference.IsValidExePath(exe))
            {
                errors.Add(Loc.Tf("{0}: путь не подходит для настройки видеокарты", exe));
                continue;
            }
            var snapshot = RegistryHelper.Snapshot(RegHive.HKCU, GpuPreference.KeyPath, exe);
            if (snapshot.ValueExisted && snapshot.Kind != RegistryValueKind.String)
            {
                errors.Add(Loc.Tf("{0}: в реестре значение необычного типа ({1}) — MONOLITH его не меняет", GpuPreference.FileName(exe), snapshot.Kind));
                continue;
            }
            // No change without an undo the journal will accept later: check the record shape before writing anything.
            var probe = new ChangeRecord
            {
                Source = GpuPreference.JournalSource, Kind = ChangeKind.Registry, Target = GpuPreference.JournalTarget(exe), Registry = snapshot,
            };
            if (JournalTrust.Check(probe) is { } refusal)
            {
                errors.Add(Loc.Tf("{0}: журнал не принял бы отмену такого изменения ({1}) — ничего не изменено", GpuPreference.FileName(exe), refusal));
                continue;
            }
            var current = snapshot.ValueExisted ? snapshot.Data : null;
            var record = journal.FindActive(GpuPreference.JournalTarget(exe));
            switch (GpuPreference.Decide(enableHigh, GpuPreference.Parse(current), record?.Registry))
            {
                case GpuChange.Revert when record is not null:
                    reverts.Add(record);
                    break;
                case GpuChange.Write:
                    if (!snapshot.KeyExisted && !RegistryHelper.KeyExists(RegHive.HKCU, GpuPreference.ParentPath))
                    {
                        // The undo removes a key MONOLITH created only one level deep: never leave an empty DirectX key behind.
                        errors.Add(Loc.T("в реестре нет раздела DirectX — выберите видеокарту в «Параметры → Система → Дисплей → Графика»"));
                        continue;
                    }
                    writes.Add((exe, GpuPreference.Merge(current, enableHigh ? GpuPreference.HighPerformance : GpuPreference.Auto)));
                    break;
            }
        }

        if (writes.Count > 0)
        {
            // Journal titles are stored in Russian (the on-disk schema is language-neutral).
            var title = string.Format(enableHigh ? Loc.Key("Игры: высокопроизводительный GPU для «{0}»") : Loc.Key("Игры: выбор GPU Windows для «{0}»"), gameName);
            // One HKCU value per game: the journal record and the .reg export are the undo (as for single startup toggles).
            var session = await ChangeSession.BeginAsync(title, createRestorePoint: false, journal: journal).ConfigureAwait(false);
            foreach (var (exe, _) in writes)
            {
                // Re-read and merge inside the journal gate: no revert can change the value between the read and the write.
                session.Exclusive(() =>
                {
                    var now = RegistryHelper.Read(RegHive.HKCU, GpuPreference.KeyPath, exe) as string;
                    var data = GpuPreference.Merge(now, enableHigh ? GpuPreference.HighPerformance : GpuPreference.Auto);
                    session.SetRegistry(RegHive.HKCU, GpuPreference.KeyPath, exe, RegistryValueKind.String, data,
                        GpuPreference.JournalSource, string.Format(Loc.Key("Видеокарта для игры «{0}» ({1})"), gameName, GpuPreference.FileName(exe)));
                });
            }
            session.Complete();
            applied += session.Applied;
            errors.AddRange(session.Errors);
        }

        if (reverts.Count > 0)
        {
            var r = await journal.RevertAsync(reverts).ConfigureAwait(false);
            applied += r.Reverted;
            errors.AddRange(r.Errors);
        }
        return new GpuChangeOutcome(applied, errors);
    }
}
