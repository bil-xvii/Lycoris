using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lycoris.Modules.Tools;

public enum ToolStatus { Success, Warning, Failed, Cancelled }

/// <summary>How a finished tool run went, in plain Russian.</summary>
public sealed record ToolOutcome(ToolStatus Status, string Summary)
{
    public static ToolOutcome Ok(string summary) => new(ToolStatus.Success, summary);
    public static ToolOutcome Warn(string summary) => new(ToolStatus.Warning, summary);
    public static ToolOutcome Fail(string summary) => new(ToolStatus.Failed, summary);
    public static ToolOutcome Cancel(string summary) => new(ToolStatus.Cancelled, summary);
}

/// <summary>
/// Pure helpers that read the console output of Windows repair tools (sfc, DISM, chkdsk, chkntfs, defrag, netsh).
/// Output is localized (Russian or English Windows), so every check knows both wordings and falls back to the exit code.
/// </summary>
public static partial class ToolOutput
{
    [GeneratedRegex(@"(?<!\d)(\d{1,3}(?:[.,]\d+)?)\s?%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"^\s*\[[=\-\s]*(\d{1,3}(?:[.,]\d+)?)\s?%[=\-\s]*\]\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DismBarRegex();

    [GeneratedRegex(@"(?:Ошибка|Error)\s*:?\s*(0x[0-9A-Fa-f]{8}|\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)] // loc:ignore Windows output pattern
    private static partial Regex ErrorCodeRegex();

    /// <summary>
    /// Finds a progress percentage in a line. When a line has several (chkdsk: "Stage: 40%; Total: 12%"),
    /// the last one is the overall progress. Values outside 0–100 are rejected.
    /// </summary>
    public static bool TryParsePercent(string? line, out double percent)
    {
        percent = 0;
        if (string.IsNullOrEmpty(line) || line.IndexOf('%') < 0) return false;
        var matches = PercentRegex().Matches(line);
        if (matches.Count == 0) return false;
        var raw = matches[^1].Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return false;
        if (v < 0 || v > 100) return false;
        percent = v;
        return true;
    }

    private static readonly string[] ProgressWords =
        ["заверш", "выполн", "обработ", "ход выполнения", "complete", "progress", "done"]; // loc:ignore Windows output patterns

    /// <summary>
    /// True for lines that only report progress ("Проверка 45% завершена.", DISM "[===  10.0%  ]",
    /// chkdsk "Progress: … Total: 12%", defrag "Retrim: 45% complete"). The console replaces such a line with the next one
    /// instead of stacking hundreds of them. Report lines ("Фрагментировано = 0%") are kept.
    /// </summary>
    public static bool IsProgressLine(string? line)
    {
        if (line is null || line.Length > 160 || !TryParsePercent(line, out _)) return false;
        var t = line.TrimStart();
        if (t.StartsWith('[')) return true;
        if (t.Contains(" = ", StringComparison.Ordinal)) return false;
        return ProgressWords.Any(w => t.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Turns DISM's 60-character "[=====   10.0%   ]" bar into a compact one that fits the console.</summary>
    public static string NormalizeProgressLine(string line, int width = 24)
    {
        var m = DismBarRegex().Match(line);
        if (!m.Success) return line.Trim();
        var raw = m.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return line.Trim();
        v = Math.Clamp(v, 0, 100);
        var filled = (int)Math.Round(v / 100d * width);
        var sb = new StringBuilder(width + 12);
        sb.Append('[').Append('#', filled).Append('.', width - filled).Append("] ")
          .Append(v.ToString("0.0", CultureInfo.InvariantCulture)).Append('%');
        return sb.ToString();
    }

    /// <summary>Formats a percentage for the big readout: "45", "99.5" → "99".</summary>
    public static string FormatPercent(double percent) =>
        Math.Floor(Math.Clamp(percent, 0, 100)).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"0:07" / "12:41" / "1:02:03".</summary>
    public static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    private static bool Has(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts "0x800f081f" / "740" from a "Ошибка: …" / "Error: …" line.</summary>
    public static string? FindErrorCode(string output)
    {
        var m = ErrorCodeRegex().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }

    // ─────────────────────────── sfc /scannow ───────────────────────────

    public static ToolOutcome ClassifySfc(int exitCode, string output)
    {
        output ??= "";
        if (Has(output, "требуется перезагрузка", "requires reboot", "repair pending", "ожидает восстановления")) // loc:ignore Windows output patterns
            return ToolOutcome.Warn(Loc.T("Windows ждёт завершения предыдущего восстановления. Перезагрузите компьютер и запустите проверку снова."));
        if (Has(output, "не обнаружила нарушений", "не обнаружила нарушения", "did not find any integrity violations")) // loc:ignore Windows output patterns
            return ToolOutcome.Ok(Loc.T("Нарушений целостности не обнаружено. Системные файлы в порядке."));
        if (Has(output, "не может восстановить", "не удалось восстановить", "unable to fix")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.T("Часть повреждённых файлов восстановить не удалось. Выполните «Восстановление образа» (DISM /RestoreHealth), затем повторите проверку."));
        if (Has(output, "успешно их восстановила", "успешно восстановила", "successfully repaired")) // loc:ignore Windows output patterns
            return ToolOutcome.Ok(Loc.T("Найдены повреждённые файлы — все успешно восстановлены. Перезагрузите компьютер."));
        if (Has(output, "не удалось выполнить запрошенную операцию", "could not perform the requested operation")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.T("Проверка не выполнена. Запустите её ещё раз после перезагрузки или используйте DISM /RestoreHealth."));
        if (Has(output, "должны быть администратором", "must be an administrator")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.T("Для проверки нужны права администратора."));
        return exitCode == 0
            ? ToolOutcome.Ok(Loc.T("Проверка завершена."))
            : ToolOutcome.Fail(Loc.Tf("Проверка завершилась с кодом {0}. Подробности — в выводе выше и в %WINDIR%\\Logs\\CBS\\CBS.log.", exitCode));
    }

    // ─────────────────────────── DISM ───────────────────────────

    public enum DismMode { CheckHealth, ScanHealth, RestoreHealth }

    public static ToolOutcome ClassifyDism(DismMode mode, int exitCode, string output)
    {
        output ??= "";
        if (exitCode == 740 || Has(output, "Ошибка: 740", "Error: 740")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.T("DISM требует прав администратора (код 740)."));

        if (exitCode != 0)
        {
            var code = FindErrorCode(output) ?? exitCode.ToString(CultureInfo.InvariantCulture);
            if (code.Equals("0x800f081f", StringComparison.OrdinalIgnoreCase) || code.Equals("0x800f0906", StringComparison.OrdinalIgnoreCase))
                return ToolOutcome.Fail(Loc.Tf("Не найдены исходные файлы для восстановления (ошибка {0}). Проверьте подключение к интернету и Центр обновления Windows.", code));
            return ToolOutcome.Fail(Loc.Tf("DISM завершился с ошибкой {0}. Журнал: %WINDIR%\\Logs\\DISM\\dism.log.", code));
        }

        if (mode == DismMode.RestoreHealth)
            return ToolOutcome.Ok(Loc.T("Образ Windows восстановлен. Теперь запустите проверку системных файлов (sfc /scannow)."));

        if (Has(output, "не может быть восстановлено", "не подлежит восстановлению", "cannot be repaired")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.T("Хранилище компонентов не подлежит восстановлению средствами DISM. Поможет переустановка Windows поверх текущей с сохранением файлов."));
        if (Has(output, "подлежит восстановлению", "is repairable")) // loc:ignore Windows output patterns
            return ToolOutcome.Warn(Loc.T("Хранилище компонентов повреждено, но подлежит восстановлению. Запустите «ВОССТАНОВИТЬ» (/RestoreHealth)."));
        if (Has(output, "не обнаружено", "no component store corruption")) // loc:ignore Windows output patterns
            return ToolOutcome.Ok(Loc.T("Повреждений хранилища компонентов не обнаружено."));
        return mode == DismMode.CheckHealth
            ? ToolOutcome.Ok(Loc.T("Быстрая проверка завершена."))
            : ToolOutcome.Ok(Loc.T("Сканирование завершено."));
    }

    // ─────────────────────────── chkdsk /scan ───────────────────────────

    /// <summary>chkdsk exit codes: 0 no errors, 1 errors fixed, 2 cleanup, 3 errors found that need an offline fix.</summary>
    public static ToolOutcome ClassifyChkdsk(int exitCode, string output) => exitCode switch
    {
        0 => ToolOutcome.Ok(Loc.T("Проблем в файловой системе не найдено.")),
        1 => ToolOutcome.Ok(Loc.T("Найденные ошибки исправлены в фоновом режиме.")),
        2 => ToolOutcome.Ok(Loc.T("Проверка завершена, выполнена служебная очистка файловой системы.")),
        3 => ToolOutcome.Warn(Loc.T("Найдены ошибки, которые исправляются только при загрузке. Запланируйте полную проверку (см. инструкцию ниже).")),
        _ when Has(output ?? "", "отказано в доступе", "access denied") => ToolOutcome.Fail(Loc.T("Нет доступа к диску — нужны права администратора.")), // loc:ignore Windows output patterns
        _ => ToolOutcome.Fail(Loc.Tf("Проверка завершилась с кодом {0}.", exitCode)),
    };

    // ─────────────────────────── chkntfs <drive> ───────────────────────────

    /// <summary>Reads "C: не загрязнен." / "C: is not dirty." (the volume is not marked for a boot-time check).</summary>
    public static ToolOutcome ClassifyChkntfs(int exitCode, string output, string drive)
    {
        output ??= "";
        if (Has(output, "не загрязнен", "is not dirty")) // loc:ignore Windows output patterns
            return ToolOutcome.Ok(Loc.Tf("Диск {0} в порядке: проверка при загрузке не запланирована.", drive));
        if (Has(output, "загрязнен", "is dirty")) // loc:ignore Windows output patterns
            return ToolOutcome.Warn(Loc.Tf("Диск {0} помечен для проверки: Windows проверит его при следующей загрузке.", drive));
        if (Has(output, "cannot query", "не удается", "не удалось")) // loc:ignore Windows output patterns
            return ToolOutcome.Fail(Loc.Tf("Не удалось получить состояние диска {0} — нужны права администратора.", drive));
        return exitCode == 0 ? ToolOutcome.Ok(Loc.T("Состояние получено.")) : ToolOutcome.Fail(Loc.Tf("chkntfs завершился с кодом {0}.", exitCode));
    }

    /// <summary>True when the BootExecute value asks autochk to check something beyond the default "autocheck autochk *".</summary>
    public static bool IsBootCheckScheduled(IEnumerable<string>? bootExecute)
    {
        if (bootExecute is null) return false;
        foreach (var raw in bootExecute)
        {
            var e = raw?.Trim() ?? "";
            if (e.Length == 0) continue;
            if (!e.Contains("autochk", StringComparison.OrdinalIgnoreCase)) continue;
            var normalized = string.Join(' ', e.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (!normalized.Equals("autocheck autochk *", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // ─────────────────────────── generic ───────────────────────────

    public static ToolOutcome ClassifyExit(int exitCode, string success, string failurePrefix) =>
        exitCode == 0 ? ToolOutcome.Ok(success) : ToolOutcome.Fail(Loc.Tf("{0} (код {1}).", failurePrefix, exitCode));

    /// <summary>Status tag text for the console header (a Russian KEY: <see cref="ToolsViewModel.RunState"/> is compared with it; the view translates it).</summary>
    public static string StatusText(ToolStatus status) => status switch
    {
        ToolStatus.Success => Loc.Key("ГОТОВО"),
        ToolStatus.Warning => Loc.Key("ВНИМАНИЕ"),
        ToolStatus.Cancelled => Loc.Key("ОТМЕНЕНО"),
        _ => Loc.Key("ОШИБКА"),
    };
}
