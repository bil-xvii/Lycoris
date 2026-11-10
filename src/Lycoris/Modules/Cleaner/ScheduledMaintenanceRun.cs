using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Core.Maintenance;
using Lycoris.Core.Security;
using Lycoris.Modules.Dashboard.Checks;

namespace Lycoris.Modules.Cleaner;

/// <summary>What the last scheduled run did — written by «Lycoris.exe --maintain», shown by the app next time.</summary>
public sealed record MaintenanceRunResult
{
    public const int MaxWarnings = 5;
    public const int MaxText = 200;

    public string StartedUtc { get; init; } = "";
    public string FinishedUtc { get; init; } = "";
    public long FreedBytes { get; init; }
    public int FilesDeleted { get; init; }
    /// <summary>Items that had something to clean.</summary>
    public int ItemsCleaned { get; init; }
    /// <summary>Items skipped (their app was running, nothing to do, a blocker).</summary>
    public int ItemsSkipped { get; init; }
    /// <summary>Health score 0–100 of the read-only check; null when the check did not finish.</summary>
    public int? HealthScore { get; init; }
    public int HealthWarnings { get; init; }
    /// <summary>Titles of the warnings (at most <see cref="MaxWarnings"/>), in the language of the run.</summary>
    public List<string> Warnings { get; init; } = [];
    /// <summary>Why the run stopped early (Russian sentence in the language of the run), null = completed.</summary>
    public string? Error { get; init; }

    public DateTime? FinishedAt => DateTime.TryParse(FinishedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)
        ? t.ToUniversalTime() : null;

    /// <summary>
    /// The file is written by a program running as the user, so any program of the user can write it too: the values are
    /// clamped to what Lycoris could have written (no negative sizes, short printable texts, few warnings). Null = unusable.
    /// </summary>
    public MaintenanceRunResult? Sanitized()
    {
        if (FinishedAt is null) return null;
        static string? Text(string? s) => s is null ? null : SafeIo.IsPlainText(s, MaxText) ? s : null;
        return this with
        {
            FreedBytes = Math.Clamp(FreedBytes, 0, 1L << 50),
            FilesDeleted = Math.Max(0, FilesDeleted),
            ItemsCleaned = Math.Clamp(ItemsCleaned, 0, 1000),
            ItemsSkipped = Math.Clamp(ItemsSkipped, 0, 1000),
            HealthScore = HealthScore is { } h ? Math.Clamp(h, 0, 100) : null,
            HealthWarnings = Math.Clamp(HealthWarnings, 0, 1000),
            Warnings = (Warnings ?? []).Select(Text).OfType<string>().Take(MaxWarnings).ToList(),
            Error = Text(Error),
        };
    }

    /// <summary>One line for the card and the toast: «Освобождено 1,2 ГБ · здоровье 86/100, предупреждений: 2».</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (Error is { } error) parts.Add(error);
        parts.Add(FreedBytes > 0 ? Loc.Tf("освобождено {0}", CleanFormat.Bytes(FreedBytes)) : Loc.T("чистить было нечего"));
        if (HealthScore is { } score)
            parts.Add(HealthWarnings > 0 ? Loc.Tf("здоровье {0}/100, предупреждений: {1}", score, HealthWarnings) : Loc.Tf("здоровье {0}/100", score));
        var text = string.Join(" · ", parts);
        return char.ToUpper(text[0], Loc.Culture) + text[1..];
    }
}

/// <summary>The result file in %LocalAppData%\Lycoris (the user's own folder: the run has normal rights).</summary>
public static class MaintenanceResultStore
{
    public const int MaxBytes = 64 * 1024;

    public static string FilePath => Path.Combine(AppPaths.UserDir, "maintenance-last.json");

    public static void Write(MaintenanceRunResult result, string? path = null)
    {
        path ??= FilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Обслуживание по расписанию: итог не записан — {0}", ex.Message);
        }
    }

    /// <summary>The last result, validated; null when there is none or it is unusable.</summary>
    public static MaintenanceRunResult? Read(string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path)) return null;
            var raw = SafeIo.ReadText(path, MaxBytes);
            return JsonSerializer.Deserialize<MaintenanceRunResult>(raw)?.Sanitized();
        }
        catch (Exception ex)
        {
            Log.Warn("Обслуживание по расписанию: итог прошлого запуска не прочитан — {0}", ex.Message);
            return null;
        }
    }
}

/// <summary>The work of one scheduled run (real: the cleaner catalog and the Dashboard checks; tests: fakes).</summary>
public interface IMaintenanceSteps
{
    /// <summary>The safe items present on this PC.</summary>
    IReadOnlyList<CleanDefinition> Items();
    /// <summary>Dry run, then deletion of what the dry run found (skips running apps, PathGuard, age filters).</summary>
    ItemCleanResult Clean(CleanDefinition item, CancellationToken ct);
    /// <summary>The read-only health check: score and warning titles; null when it could not run.</summary>
    Task<(int Score, IReadOnlyList<string> Warnings)?> CheckHealthAsync(CancellationToken ct);
}

/// <summary>
/// «Lycoris.exe --maintain» — started by the weekly task with normal rights, no window. Cleans only the per-user items of
/// the cleaner's БЕЗОПАСНО tier that are checked by default (temp files, error reports, WinINet and browser/Discord caches —
/// never Downloads, Documents, cookies, passwords or history; PathGuard and every age filter apply exactly as on the page),
/// then runs the Dashboard's read-only checks and writes a short result the app shows next time.
/// Exit codes: 0 done, 3 refused (admin rights), 4 off in the settings, 5 another run is in progress.
/// </summary>
public static class ScheduledMaintenanceRunner
{
    public const int ExitOk = 0, ExitElevated = 3, ExitDisabled = 4, ExitBusy = 5;

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The items a scheduled run cleans: safe tier, checked by default, no admin rights, deleted by the own engine.
    /// Everything else (Recycle Bin, Windows Update downloads, shader caches, developer tools, DISM) stays a manual choice.
    /// </summary>
    public static IReadOnlyList<CleanDefinition> SafeItems(IEnumerable<CleanDefinition> catalog) =>
        catalog.Where(d => d.Tier == CleanTier.Safe && d.DefaultChecked && !d.RequiresAdmin && d.Method == CleanMethod.Files).ToList();

    /// <summary>The headless entry point (App.OnStartup). Blocking work runs on the thread pool.</summary>
    public static async Task<int> RunAsync(bool elevated, bool enabled, IMaintenanceSteps steps, Action<MaintenanceRunResult> write,
        CancellationToken ct = default)
    {
        // The task never has admin rights (ScheduledMaintenanceTask builds it with LeastPrivilege). A copy started with them
        // anyway (by hand, or a task someone changed) does nothing: unattended deletion with an admin token is not offered.
        if (elevated)
        {
            Log.Warn("Обслуживание по расписанию: запуск с правами администратора отклонён — задача должна работать с обычными правами.");
            return ExitElevated;
        }
        if (!enabled)
        {
            Log.Info("Обслуживание по расписанию выключено в настройках — ничего не сделано.");
            return ExitDisabled;
        }

        var started = DateTime.UtcNow;
        Log.Info("Обслуживание по расписанию: начало.");
        long freed = 0;
        int files = 0, cleaned = 0, skipped = 0;
        string? error = null;
        try
        {
            var items = await Task.Run(steps.Items, ct).ConfigureAwait(false);
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await Task.Run(() => steps.Clean(item, ct), ct).ConfigureAwait(false);
                    if (r.Skipped is not null || r.Error is not null || r.Tally.Deleted == 0) skipped++;
                    else cleaned++;
                    freed += r.Freed;
                    files += r.Tally.Deleted;
                    foreach (var note in r.Notes) Log.Info("Обслуживание по расписанию: {0}", note);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    skipped++;
                    Log.Warn("Обслуживание по расписанию: «{0}» пропущено — {1}", item.Name, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            error = Loc.T("Прервано: время задачи истекло или ПК перешёл на батарею.");
        }

        (int Score, IReadOnlyList<string> Warnings)? health = null;
        if (error is null)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(HealthTimeout);
                health = await steps.CheckHealthAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("Обслуживание по расписанию: проверка состояния не уложилась в {0} мин.", HealthTimeout.TotalMinutes);
            }
            catch (Exception ex)
            {
                Log.Warn("Обслуживание по расписанию: проверка состояния не выполнена — {0}", ex.Message);
            }
        }

        var result = new MaintenanceRunResult
        {
            StartedUtc = started.ToString("o"),
            FinishedUtc = DateTime.UtcNow.ToString("o"),
            FreedBytes = freed,
            FilesDeleted = files,
            ItemsCleaned = cleaned,
            ItemsSkipped = skipped,
            HealthScore = health?.Score,
            HealthWarnings = health?.Warnings.Count ?? 0,
            Warnings = health?.Warnings.Take(MaintenanceRunResult.MaxWarnings).ToList() ?? [],
            Error = error,
        };
        write(result);
        Log.Success("Обслуживание по расписанию: готово — освобождено {0}, файлов {1}.", CleanFormat.Bytes(freed), files);
        return ExitOk;
    }

    /// <summary>Process side of the headless mode: one run at a time, lower priority, the real steps.</summary>
    public static async Task<int> RunProcessAsync()
    {
        using var mutex = new Mutex(false, @"Local\Lycoris.ScheduledMaintenance");
        bool owns;
        try { owns = mutex.WaitOne(TimeSpan.Zero); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) return ExitBusy;
        try
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch { /* best effort */ }
            // No ConfigureAwait(false): the mutex is released on the thread that took it.
            return await RunAsync(Elevation.IsElevated, AppSettings.Current.ScheduledMaintenance, new RealSteps(),
                r => MaintenanceResultStore.Write(r));
        }
        finally
        {
            try { mutex.ReleaseMutex(); }
            catch (ApplicationException) { /* the process ends right after */ }
        }
    }

    /// <summary>The cleaner catalog and the Dashboard checks, exactly as the pages use them.</summary>
    private sealed class RealSteps : IMaintenanceSteps
    {
        private readonly Lazy<CleanEnvironment> _env = new(CleanEnvironment.Capture);

        public IReadOnlyList<CleanDefinition> Items() => SafeItems(CleanCatalog.All).Where(d => d.IsPresent(_env.Value)).ToList();

        public ItemCleanResult Clean(CleanDefinition item, CancellationToken ct)
        {
            var env = _env.Value;
            var analysis = CleanRunner.Analyze(item, env, PathGuard.Default, ct);
            if (!analysis.HasWork(item.Method)) return new ItemCleanResult { Skipped = analysis.Blocker ?? Loc.T("Нечего чистить.") };
            return CleanRunner.CleanFiles(item, analysis, env, PathGuard.Default, ct);
        }

        public async Task<(int Score, IReadOnlyList<string> Warnings)?> CheckHealthAsync(CancellationToken ct)
        {
            var snapshot = await SystemInspector.CollectAsync().WaitAsync(ct).ConfigureAwait(false);
            var results = HealthRules.Evaluate(snapshot);
            var warnings = HealthScore.Ordered(results).Where(r => r.Severity == CheckSeverity.Warning).Select(r => r.Title).ToList();
            return (HealthScore.Calculate(results), warnings);
        }
    }
}

/// <summary>What the app tells the user at startup about scheduled maintenance (one toast, never a dialog).</summary>
public static class ScheduledMaintenanceNotice
{
    /// <summary>
    /// A new result the user has not seen yet (once), or a task that needs fixing. Marks the result as seen and, for a run that
    /// completed, counts it as the last maintenance (the «ПОРА ОБСЛУЖИТЬ ПК» reminder starts over). Null = nothing to say.
    /// </summary>
    public static (string Text, Lycoris.UI.ToastKind Kind)? Take(AppSettings settings, MaintenanceRunResult? last, MaintenanceTaskInfo info)
    {
        if (last is { FinishedAt: { } at } && (Parse(settings.ScheduledMaintenanceSeenUtc) is not { } seen || at > seen))
        {
            settings.ScheduledMaintenanceSeenUtc = at.ToString("o");
            if (last.Error is null && (Parse(settings.LastMaintenanceUtc) is not { } lastClean || at > lastClean))
                settings.LastMaintenanceUtc = at.ToString("o");
            return (Loc.Tf("Обслуживание по расписанию ({0}): {1}", Loc.DateTimeText(at.ToLocalTime()), last.Summary()),
                last.Error is null ? Lycoris.UI.ToastKind.Success : Lycoris.UI.ToastKind.Warning);
        }
        if (settings.ScheduledMaintenance && info.NeedsFix)
            return (Loc.T("Задача обслуживания по расписанию не может запустить Lycoris — откройте «Обслуживание», чтобы исправить или выключить её."),
                Lycoris.UI.ToastKind.Warning);
        return null;
    }

    private static DateTime? Parse(string? utc) =>
        DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToUniversalTime() : null;
}
