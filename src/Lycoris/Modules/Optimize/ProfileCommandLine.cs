using System.IO;
using System.Text;
using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Modules.Optimize;

/// <summary>Parsed «--apply-profile» arguments. Without <see cref="Yes"/> (or with <see cref="DryRun"/>) only the plan is printed.</summary>
public sealed record ProfileCliOptions(OptimizeProfile Profile, string ProfileName, bool DryRun, bool Yes)
{
    /// <summary>The plan is applied only with --yes and without --dry-run (--dry-run always wins).</summary>
    public bool Apply => Yes && !DryRun;
}

/// <summary>A row the profile would change, with its state detected now.</summary>
public sealed record ProfileCliTarget(Tweak Tweak, TweakState State);

/// <summary>
/// The scenario selection on this PC: the rows to change (the same rows the Optimize page's «ПРИМЕНИТЬ» would apply),
/// how many of the profile's defaults are already applied and how many do not suit this PC.
/// </summary>
public sealed record ProfileCliSelection(IReadOnlyList<ProfileCliTarget> Targets, int AlreadyApplied, int NotApplicable, bool Laptop);

/// <summary>One «сейчас → станет» line of the plan.</summary>
public sealed record ProfileCliLine(string Target, string Current, string New);

/// <summary>Everything the command line needs from the system — the real one (<see cref="ProfileCommandLineHost"/>) or a test fake.</summary>
public interface IProfileCliHost
{
    bool IsElevated { get; }

    /// <summary>Reads the current state of every tweak (read-only) and selects the profile like the Optimize page does.</summary>
    Task<ProfileCliSelection> SelectAsync(OptimizeProfile profile);

    /// <summary>What applying the tweak changes, with the current value read now (read-only).</summary>
    IReadOnlyList<ProfileCliLine> ReadChanges(Tweak tweak);

    /// <summary>The restore-point line shown before an apply (same text as the page's confirmation).</summary>
    Task<string> RestorePointNoteAsync();

    /// <summary>Takes Lycoris's single-instance lock: false = Lycoris is already open (two copies must not write one journal).</summary>
    bool TryLockInstance();

    /// <summary>Applies through the one engine of the page (<see cref="TweakEngine.ApplyAsync"/>: journal, restore point, undo).</summary>
    Task<TweakRunResult> ApplyAsync(IReadOnlyList<Tweak> tweaks, string batchTitle, IProgress<string> progress);

    /// <summary>Reads the states back after the apply.</summary>
    Task<IReadOnlyDictionary<string, TweakState>> DetectAsync(IReadOnlyList<Tweak> tweaks);
}

/// <summary>
/// «Lycoris.exe --apply-profile normal|gaming|recommended [--dry-run] [--yes]»: prints the exact plan of an Optimize scenario
/// (every change, current → new) and, only with --yes in an administrator console, applies it through the same engine as the
/// page's button. Nothing here writes to the system itself: the write path is <see cref="IProfileCliHost.ApplyAsync"/>.
/// Launch arguments are attacker input (CLAUDE.md «Security» 5): an allow-list of switches and profile names, nothing echoed raw.
/// </summary>
public static class ProfileCommandLine
{
    public const string Switch = "--apply-profile";

    /// <summary>Exit codes (documented in README «Командная строка»).</summary>
    public static class Exit
    {
        /// <summary>Plan printed (dry run), nothing to change, or every change applied and confirmed.</summary>
        public const int Ok = 0;
        /// <summary>The apply reported errors (each one printed; the rest stays applied and can be undone).</summary>
        public const int ApplyErrors = 1;
        /// <summary>Wrong arguments (usage printed).</summary>
        public const int Usage = 2;
        /// <summary>--yes without administrator rights: nothing was changed.</summary>
        public const int NeedsAdmin = 3;
        /// <summary>Lycoris is already open: nothing was changed.</summary>
        public const int Busy = 4;
        /// <summary>Applied without errors, but not every item could be confirmed (skipped or not verified).</summary>
        public const int NotConfirmed = 5;
        /// <summary>Unexpected failure.</summary>
        public const int Failed = 6;
    }

    /// <summary>Exactly the three scenarios of the command line (exact, lower-case names).</summary>
    private static readonly Dictionary<string, OptimizeProfile> Profiles = new(StringComparer.Ordinal)
    {
        ["normal"] = OptimizeProfile.Normal,
        ["gaming"] = OptimizeProfile.Gaming,
        ["recommended"] = OptimizeProfile.Recommended,
    };

    private const int MaxArgs = 8;

    /// <summary>Whether the launch asks for the command line (any position, any case — then it never opens a window).</summary>
    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the arguments; null with a ready-to-print <paramref name="error"/> on anything outside the allow-list:
    /// --apply-profile &lt;normal|gaming|recommended&gt;, --dry-run, --yes, --lang &lt;ru|en|auto&gt;, each at most once.
    /// </summary>
    public static ProfileCliOptions? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (args.Count > MaxArgs)
        {
            error = Loc.T("Слишком много параметров.");
            return null;
        }
        string? name = null;
        bool dry = false, yes = false, lang = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i] ?? "";
            switch (a)
            {
                case Switch when name is null:
                    if (i + 1 >= args.Count || !Profiles.ContainsKey(args[i + 1] ?? ""))
                    {
                        error = i + 1 >= args.Count
                            ? Loc.T("Не указан сценарий: normal, gaming или recommended.")
                            : Loc.Tf("Неизвестный сценарий «{0}»: допустимы normal, gaming, recommended.", Printable(args[i + 1]));
                        return null;
                    }
                    name = args[++i];
                    break;
                case "--dry-run" when !dry:
                    dry = true;
                    break;
                case "--yes" when !yes:
                    yes = true;
                    break;
                case "--lang" when !lang && i + 1 < args.Count && args[i + 1] is "ru" or "en" or "auto":
                    lang = true;
                    i++;
                    break;
                default:
                    error = Loc.Tf("Неизвестный или повторный параметр «{0}».", Printable(a));
                    return null;
            }
        }
        if (name is null)
        {
            error = Loc.T("Не указан сценарий: normal, gaming или recommended.");
            return null;
        }
        return new ProfileCliOptions(Profiles[name], name, dry, yes);
    }

    /// <summary>An argument as it may be echoed: printable characters only (no escape sequences), at most 40 of them.</summary>
    internal static string Printable(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (sb.Length >= 40) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) || char.IsSurrogate(c) || IsBidiOrSeparator(c) ? '?' : c);
        }
        return sb.ToString();
    }

    /// <summary>Line/paragraph separators and bidi controls (U+2028/2029, U+200E/200F, U+202A–U+202E, U+2066–U+2069): they reorder or break console text.</summary>
    private static bool IsBidiOrSeparator(char c) =>
        c is (>= (char)0x2028 and <= (char)0x202E) or (char)0x200E or (char)0x200F or (>= (char)0x2066 and <= (char)0x2069);

    public static string Usage() => string.Join(Environment.NewLine,
        Loc.T("Использование: Lycoris.exe --apply-profile <сценарий> [--dry-run] [--yes]"),
        Loc.T("  Сценарии: normal — «Работа и учёба», gaming — «Игры», recommended — «Рекомендуемый»."),
        Loc.T("  Без --yes (или с --dry-run) только показывает план: что изменится, сейчас → станет. Система не меняется."),
        Loc.T("  --yes применяет план в консоли администратора: журнал Lycoris, точка восстановления, откат в «Восстановлении»."),
        Loc.T("Коды выхода: 0 — готово, 1 — ошибки при применении, 2 — неверные параметры, 3 — нужны права администратора, 4 — Lycoris уже открыт, 5 — применено не всё, 6 — сбой."));

    /// <summary><see cref="RunAsync(IReadOnlyList{string}, IProfileCliHost, TextWriter)"/> that turns an unexpected exception into <see cref="Exit.Failed"/>.</summary>
    public static async Task<int> RunSafeAsync(IReadOnlyList<string> args, IProfileCliHost host, TextWriter output)
    {
        try
        {
            return await RunAsync(args, host, output).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error("Командная строка --apply-profile: сбой", ex);
            output.WriteLine(Loc.Tf("Сбой: {0} Подробности — в журнале Lycoris.", ex.Message));
            return Exit.Failed;
        }
    }

    /// <summary>Runs the command line and returns the exit code. Writes only to <paramref name="output"/>.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, IProfileCliHost host, TextWriter output)
    {
        var options = Parse(args, out var error);
        if (options is null)
        {
            output.WriteLine(error);
            output.WriteLine(Usage());
            return Exit.Usage;
        }
        return await RunAsync(options, host, output).ConfigureAwait(true);
    }

    public static async Task<int> RunAsync(ProfileCliOptions options, IProfileCliHost host, TextWriter output)
    {
        // Rights first: an apply that cannot run must not cost a detection pass or look like it is about to start.
        if (options.Apply && !host.IsElevated)
        {
            output.WriteLine(Loc.T("Нужны права администратора: запустите консоль от имени администратора и повторите команду. Ничего не изменено."));
            return Exit.NeedsAdmin;
        }

        var selection = await host.SelectAsync(options.Profile).ConfigureAwait(true);
        var scenario = OptimizeLogic.ScenarioName(options.Profile, selection.Laptop);
        output.WriteLine(options.Apply
            ? Loc.Tf("Lycoris · сценарий «{0}» ({1}) — применение.", scenario, options.ProfileName)
            : Loc.Tf("Lycoris · сценарий «{0}» ({1}) — пробный запуск, система не меняется.", scenario, options.ProfileName));

        // Irreversible data loss is confirmed item by item on the page — never through a blanket --yes.
        var lossy = selection.Targets.Where(t => OptimizeLogic.DataLossNote(t.Tweak.Id) is not null).ToList();
        var targets = selection.Targets.Except(lossy).ToList();

        WritePlan(output, host, targets);
        foreach (var t in lossy)
            output.WriteLine(Loc.Tf("Пропущено — {0}: {1} Отметьте этот пункт в окне Lycoris, если он нужен.", t.Tweak.Title, OptimizeLogic.DataLossNote(t.Tweak.Id)));
        output.WriteLine(Loc.Tf("Изменится: {0} · уже применено: {1} · не подходит для этого ПК: {2}.",
            targets.Count, selection.AlreadyApplied, selection.NotApplicable));

        if (targets.Count == 0)
        {
            output.WriteLine(Loc.T("Менять нечего: всё из сценария уже применено или не подходит для этого ПК."));
            return Exit.Ok;
        }
        if (!options.Apply)
        {
            output.WriteLine(Loc.Tf("Пробный запуск: ничего не изменено. Применить: Lycoris.exe --apply-profile {0} --yes (в консоли администратора).", options.ProfileName));
            return Exit.Ok;
        }

        if (!host.TryLockInstance())
        {
            output.WriteLine(Loc.T("Lycoris уже открыт: закройте его окно и повторите команду. Ничего не изменено."));
            return Exit.Busy;
        }

        output.WriteLine(await host.RestorePointNoteAsync().ConfigureAwait(true));
        output.WriteLine(Loc.T("Применение изменений…"));
        var tweaks = targets.Select(t => t.Tweak).ToList();
        var progress = new SyncProgress(p => output.WriteLine("  … " + p));
        var result = await host.ApplyAsync(tweaks, OptimizeLogic.BatchTitle(options.Profile, selection.Laptop), progress).ConfigureAwait(true);
        var after = await host.DetectAsync(tweaks).ConfigureAwait(true);

        var rows = tweaks.Select(t => new ApplyReportRow(t.Id, t.Title, after.TryGetValue(t.Id, out var s) ? s : TweakState.Unknown,
            OptimizeLogic.NotApplicableText(t))).ToList();
        var report = OptimizeLogic.BuildApplyReport(result, rows, options.Profile, selection.Laptop);
        foreach (var line in report.Lines) output.WriteLine("  " + line);
        output.WriteLine(report.Title + ". " + report.Message);
        if (result.Applied > 0 && result.Restart != RestartRequirement.None)
            output.WriteLine(Loc.T("Часть изменений вступит в силу после перезагрузки или повторного входа в Windows — Lycoris её не выполняет."));

        if (result.Errors.Count > 0) return Exit.ApplyErrors;
        return report.Verified == rows.Count ? Exit.Ok : Exit.NotConfirmed;
    }

    private static void WritePlan(TextWriter output, IProfileCliHost host, IReadOnlyList<ProfileCliTarget> targets)
    {
        if (targets.Count == 0) return;
        output.WriteLine(Loc.Tf("ИЗМЕНИТСЯ {0}:", targets.Count));
        var n = 0;
        foreach (var t in targets)
        {
            n++;
            var head = Loc.Tf("{0}. {1} · {2}", n, t.Tweak.Title, t.Tweak.RiskText);
            if (t.Tweak.Restart != RestartRequirement.None) head += " · " + t.Tweak.RestartText;
            output.WriteLine(head);
            var lines = host.ReadChanges(t.Tweak);
            if (lines.Count == 0)
            {
                output.WriteLine("   " + Loc.Tf("состояние: {0} → {1}", OptimizeLogic.StateText(t.State), OptimizeLogic.StateText(TweakState.Applied)));
                continue;
            }
            foreach (var l in lines) output.WriteLine($"   {l.Target}: {l.Current} → {l.New}");
        }
    }

    /// <summary>Reports in the caller's flow, in order (Progress&lt;T&gt; would post to the dispatcher and print late).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        private readonly object _lock = new();
        public void Report(string value)
        {
            lock (_lock) report(value);
        }
    }
}
