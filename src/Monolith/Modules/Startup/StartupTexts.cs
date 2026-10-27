namespace Monolith.Modules.Startup;

/// <summary>
/// Russian texts of the АВТОЗАПУСК page that depend on a count or on settings, with correct number agreement
/// («1 запись перестанет», «3 записи перестанут», «21 запись перестанет»). Pure, unit-tested.
/// </summary>
internal static class StartupTexts
{
    /// <summary>Noun form for a number: Plural(3, "запись", "записи", "записей") = "записи".</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        n = Math.Abs(n);
        var n10 = n % 10;
        var n100 = n % 100;
        if (n10 == 1 && n100 != 11) return one;
        if (n10 is >= 2 and <= 4 && n100 is < 12 or > 14) return few;
        return many;
    }

    /// <summary>
    /// Verb form agreeing with «N записей»: singular after 1, 21, 31… (but not 11) — «21 запись перестанет»,
    /// plural otherwise — «3 записи перестанут», «5 записей перестанут».
    /// </summary>
    public static string Agree(int n, string singular, string plural)
    {
        n = Math.Abs(n);
        return n % 10 == 1 && n % 100 != 11 ? singular : plural;
    }

    /// <summary>"3 записи".</summary>
    public static string Entries(int n) => $"{n} {Plural(n, "запись", "записи", "записей")}";

    /// <summary>The name in quotes for one row («Steam»), the count for several («3 записи»).</summary>
    public static string Subject(IReadOnlyList<string> names) => names.Count == 1 ? $"«{names[0]}»" : Entries(names.Count);

    private static string Will(int n) => Agree(n, "будет", "будут");

    public static string ConfirmTitle(int count, bool enable)
    {
        var verb = enable ? "ВКЛЮЧИТЬ" : "ОТКЛЮЧИТЬ";
        return count == 1 ? $"{verb} АВТОЗАПУСК?" : $"{verb} {count} {Plural(count, "ЗАПИСЬ", "ЗАПИСИ", "ЗАПИСЕЙ")}?";
    }

    public static string ConfirmMessage(IReadOnlyList<string> names, bool enable)
    {
        var n = names.Count;
        var subject = Subject(names);
        return enable
            ? $"{subject} снова {Will(n)} запускаться при входе в Windows."
            : $"{subject} {Agree(n, "перестанет", "перестанут")} запускаться при входе в Windows. Ничего не удаляется: " +
              "MONOLITH ставит отметку «отключено» так же, как Диспетчер задач. Уже запущенные программы продолжат работать до перезагрузки.";
    }

    /// <summary>Toast after Run / Startup-folder entries were switched.</summary>
    public static string EntriesResult(IReadOnlyList<string> names, bool enable)
    {
        var n = names.Count;
        var subject = Subject(names);
        return enable
            ? $"{subject} снова {Will(n)} запускаться вместе с Windows. Изменение вступит в силу при следующем входе в Windows."
            : $"{subject} больше не {Will(n)} запускаться при входе в Windows. Вернуть можно здесь же или в разделе «Восстановление».";
    }

    /// <summary>Toast after a logon / boot task was switched (the scheduler applies it at once).</summary>
    public static string TaskResult(string name, bool enable) => enable
        ? $"Задача «{name}» снова включена: планировщик будет запускать её, как раньше. Изменение действует сразу."
        : $"Задача «{name}» отключена: планировщик больше не будет её запускать. Вернуть можно здесь же или в разделе «Восстановление».";

    /// <summary>Genitive after «исходное состояние»: «1 записи», «3 записей», «21 записи».</summary>
    public static string RevertAllMessage(int n) =>
        $"MONOLITH вернёт исходное состояние {n} {Plural(n, "записи", "записей", "записей")} журнала: отметки StartupApproved " +
        "и состояние задач планировщика станут такими, какими были до первого изменения.";

    public static string RevertDone(int reverted) => $"Исходное состояние возвращено: {Entries(reverted)}.";

    public static string RevertPartial(int reverted, int failed, string? firstError) =>
        $"Возвращено: {Entries(reverted)}, ошибок: {failed}." + (string.IsNullOrWhiteSpace(firstError) ? "" : $" {firstError}");

    /// <summary>
    /// What happens with the restore point before a change of several entries (single toggles never make one).
    /// Shown in the selection bar and in the confirmation.
    /// </summary>
    public static string RestorePointNote(bool settingOn, bool protectionOn) =>
        !settingOn ? "Точка восстановления не создаётся — отключено в настройках MONOLITH. Откат — через журнал."
        : !protectionOn ? "Защита системы Windows выключена — точки восстановления не будет. Откат — через журнал MONOLITH."
        : "Перед изменением нескольких записей MONOLITH создаёт точку восстановления.";
}
