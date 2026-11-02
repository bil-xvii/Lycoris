using System.Globalization;
using System.IO;
using System.Text.Json;
using Monolith.Core;

namespace Monolith.Modules.Cleaner;

/// <summary>Dry-run result for one catalog item.</summary>
public sealed class ItemAnalysis
{
    public DateTime AtUtc { get; } = DateTime.UtcNow;
    public List<TargetManifest> Manifests { get; } = [];
    /// <summary>Everything found (including folders of running apps).</summary>
    public long Bytes { get; set; }
    public long Files { get; set; }
    /// <summary>What a cleaning run would actually take now (owners closed, rights present).</summary>
    public long CleanableBytes { get; set; }
    public long CleanableFiles { get; set; }
    public int CleanableDirs { get; set; }
    public List<string> RunningApps { get; } = [];
    public string? Blocker { get; set; }
    /// <summary>Some folders could not be read (usually: needs administrator rights).</summary>
    public bool PartialAccess { get; set; }
    public int RefusedTargets { get; set; }
    public string? RefusedNote { get; set; }
    /// <summary>Recycle Bin item count.</summary>
    public long? Items { get; set; }
    /// <summary>The size is only known after the tool runs (DISM).</summary>
    public bool SizeUnknown { get; set; }
    /// <summary>The method is not available on this Windows (cmdlet missing, shell refused).</summary>
    public string? Unavailable { get; set; }

    public bool HasRunningApps => RunningApps.Count > 0;

    /// <summary>True when a cleaning run has something to do for this item.</summary>
    public bool HasWork(CleanMethod method) => Blocker is null && Unavailable is null && method switch
    {
        CleanMethod.Files => CleanableFiles > 0,
        CleanMethod.Tools => !HasRunningApps && Bytes > 0,
        CleanMethod.RecycleBin => Items > 0,
        CleanMethod.DeliveryOptimization => SizeUnknown || Bytes > 0,
        CleanMethod.ComponentStore => true,
        _ => false,
    };
}

/// <summary>What cleaning one item did.</summary>
public sealed class ItemCleanResult
{
    public CleanTally Tally { get; } = new();
    /// <summary>Bytes freed by a tool (DISM, Delivery Optimization, Recycle Bin, developer tools).</summary>
    public long ToolFreed { get; set; }
    public long ToolItems { get; set; }
    /// <summary>The tool's freed size is measured as a difference (approximate).</summary>
    public bool Approximate { get; set; }
    /// <summary>The whole item was skipped, with the reason.</summary>
    public string? Skipped { get; set; }
    public string? Error { get; set; }
    public List<string> Notes { get; } = [];
    public long Freed => Tally.FreedBytes + ToolFreed;
}

/// <summary>Runs analysis and cleaning for catalog items. Everything here runs off the UI thread.</summary>
public static class CleanRunner
{
    // ───────────────────────────── Analysis ─────────────────────────────

    /// <summary>Dry run for one item (blocking; call on a worker thread).</summary>
    public static ItemAnalysis Analyze(CleanDefinition def, CleanEnvironment env, PathGuard guard, CancellationToken ct,
        Action<int>? onSeen = null, Func<DeliveryOptimizationSize>? deliveryOptimizationSize = null)
    {
        var a = new ItemAnalysis { Blocker = def.Blocker?.Invoke(env) };
        switch (def.Method)
        {
            case CleanMethod.Files:
            case CleanMethod.Tools:
                AnalyzeTargets(def, env, guard, ct, onSeen, a);
                break;

            case CleanMethod.RecycleBin:
                var q = SystemCleanup.RunSta(NativeCleaner.QueryRecycleBin);
                if (q is null)
                {
                    a.Unavailable = Loc.T("Windows не сообщила размер корзины.");
                }
                else
                {
                    a.Bytes = a.CleanableBytes = q.Value.Bytes;
                    a.Items = q.Value.Items;
                }
                break;

            case CleanMethod.DeliveryOptimization:
                var size = deliveryOptimizationSize?.Invoke() ?? DeliveryOptimizationSize.Unknown;
                if (!size.Available) a.Unavailable = Loc.T("Служба оптимизации доставки недоступна в этой версии Windows.");
                else if (size.Bytes is { } b) a.Bytes = a.CleanableBytes = b;
                else a.SizeUnknown = true;
                break;

            case CleanMethod.ComponentStore:
                a.SizeUnknown = true;
                break;
        }
        return a;
    }

    private static void AnalyzeTargets(CleanDefinition def, CleanEnvironment env, PathGuard guard, CancellationToken ct,
        Action<int>? onSeen, ItemAnalysis a)
    {
        var now = DateTime.UtcNow;
        var owners = new Dictionary<AppOwner, bool>(ReferenceEqualityComparer.Instance);
        bool Running(AppOwner o)
        {
            if (!owners.TryGetValue(o, out var r)) owners[o] = r = o.IsRunning(env.Processes);
            return r;
        }

        IReadOnlyList<CleanTarget> targets;
        try { targets = def.Targets(env); }
        catch (Exception ex)
        {
            Log.Warn("Очистка: не удалось определить папки «{0}»: {1}", def.Name, ex.Message);
            targets = [];
        }

        foreach (var t in targets)
        {
            ct.ThrowIfCancellationRequested();
            var m = CleanEngine.Analyze(t, guard, now, ct, onSeen);
            if (m.Missing) continue;
            a.Manifests.Add(m);
            if (m.AccessDenied) a.PartialAccess = true;
            if (m.Refused is { } verdict)
            {
                a.RefusedTargets++;
                a.RefusedNote ??= Loc.Tf("{0}: {1} — пропущено.", t.Label, PathGuard.Describe(verdict));
                // Expected and safe (e.g. a cache folder moved to another drive via a junction): informational.
                Log.Info("Очистка: «{0}» пропущено защитой путей — {1}.", t.Label, PathGuard.Describe(verdict));
                continue;
            }
            a.Bytes += m.Bytes;
            a.Files += m.Count;

            var running = t.Owner is { } o && Running(o);
            if (running)
            {
                if (!a.RunningApps.Contains(t.Owner!.Name)) a.RunningApps.Add(t.Owner!.Name);
                continue;
            }
            if (t.RequiresAdmin && !env.IsElevated) continue;
            a.CleanableBytes += m.Bytes;
            a.CleanableFiles += m.Count;
            a.CleanableDirs += m.Dirs.Count;
        }

        if (def.Method == CleanMethod.Tools)
        {
            foreach (var o in def.ToolOwners)
                if (Running(o) && !a.RunningApps.Contains(o.Name))
                    a.RunningApps.Add(o.Name);
            a.CleanableBytes = a.HasRunningApps ? 0 : a.Bytes;
            a.CleanableFiles = a.HasRunningApps ? 0 : a.Files;
        }
    }

    // ───────────────────────────── Cleaning ─────────────────────────────

    /// <summary>Common checks before an item is cleaned. Returns the reason to skip, or null.</summary>
    public static string? PreCheck(CleanDefinition def, CleanEnvironment env)
    {
        if (def.RequiresAdmin && !env.IsElevated) return Loc.T("Нужны права администратора.");
        if (def.IsUserScope && env.UserScopeBlocked)
            return Loc.Tf("Lycoris запущен от имени другой учётной записи ({0}) — пользовательские данные не трогаются.", env.ForeignSessionUser);
        return def.Blocker?.Invoke(env);
    }

    /// <summary>Deletes the approved manifests of a Files item (blocking; call on a worker thread).</summary>
    public static ItemCleanResult CleanFiles(CleanDefinition def, ItemAnalysis a, CleanEnvironment env, PathGuard guard,
        CancellationToken ct, Action<int>? onProgress = null)
    {
        var r = new ItemCleanResult();
        if (PreCheck(def, env) is { } reason)
        {
            r.Skipped = reason;
            return r;
        }

        // Fresh process list: the owner may have been started since the analysis.
        var snapshot = ProcessSnapshot.Capture();
        var snapshotAge = System.Diagnostics.Stopwatch.StartNew();
        var owners = new Dictionary<AppOwner, bool>(ReferenceEqualityComparer.Instance);
        var done = 0;
        foreach (var m in a.Manifests)
        {
            if (!m.HasWork || m.Refused is not null) continue;
            if (ct.IsCancellationRequested)
            {
                r.Tally.Cancelled = true;
                break;
            }
            if (m.Target.RequiresAdmin && !env.IsElevated)
            {
                r.Notes.Add(Loc.Tf("{0}: нужны права администратора — пропущено.", m.Target.Label));
                continue;
            }
            if (m.Target.Owner is { } owner)
            {
                // A long run (dozens of targets): a browser opened meanwhile must not have its cache cleaned under it.
                if (snapshotAge.Elapsed > TimeSpan.FromSeconds(3))
                {
                    snapshot = ProcessSnapshot.Capture();
                    owners.Clear();
                    snapshotAge.Restart();
                }
                if (!owners.TryGetValue(owner, out var running)) owners[owner] = running = owner.IsRunning(snapshot);
                if (running)
                {
                    var note = Loc.Tf("{0} запущен — его кэш пропущен.", owner.Name);
                    if (!r.Notes.Contains(note)) r.Notes.Add(note);
                    continue;
                }
            }

            var offset = done;
            var t = CleanEngine.Clean(m, guard, ct, n => onProgress?.Invoke(offset + n));
            done += m.Files.Count;
            if (t.BaseProblem is { } problem) r.Notes.Add(Loc.Tf("{0}: {1} — пропущено.", m.Target.Label, problem));
            r.Tally.Add(t);
            if (t.Cancelled) break;
        }
        return r;
    }

    /// <summary>Runs the tool's own cache command and measures the folders again.</summary>
    /// <summary>
    /// The reason an elevated run must skip these tool commands (a program lies where ordinary programs can write), or null.
    /// <paramref name="whyWritable"/> gives the reason for a path or null when the path is protected (pure for tests).
    /// </summary>
    internal static string? UntrustedToolReason(IEnumerable<ToolCommand> commands, bool elevated, Func<string, string?> whyWritable)
    {
        if (!elevated) return null;
        foreach (var c in commands)
        {
            var why = whyWritable(c.Program);
            if (why is not null)
                return Loc.Tf("{0} лежит в папке, куда могут писать обычные программы, — с правами администратора он не запускается. Очистите этот кэш командой самого инструмента в обычном окне терминала.",
                    Path.GetFileName(c.Program));
        }
        return null;
    }

    public static async Task<ItemCleanResult> CleanToolsAsync(CleanDefinition def, ItemAnalysis a, CleanEnvironment env, PathGuard guard,
        CancellationToken ct)
    {
        var r = new ItemCleanResult { Approximate = true };
        if (PreCheck(def, env) is { } reason)
        {
            r.Skipped = reason;
            return r;
        }
        var snapshot = await Task.Run(ProcessSnapshot.Capture, ct).ConfigureAwait(false);
        var running = def.ToolOwners.FirstOrDefault(o => o.IsRunning(snapshot));
        if (running is not null)
        {
            r.Skipped = Loc.Tf("Запущен {0} — пункт пропущен, чтобы не помешать работе.", running.Name);
            return r;
        }

        var commands = def.Commands?.Invoke(env) ?? [];
        if (commands.Count == 0)
        {
            r.Skipped = Loc.T("Инструмент не найден.");
            return r;
        }
        // Developer tools are found on PATH, usually in the user's own folders. MONOLITH's administrator token must not run a
        // file that any program running as the user could have replaced.
        if (UntrustedToolReason(commands, Elevation.IsElevated, Monolith.Core.Security.PathTrust.WhyUserWritable) is { } untrusted)
        {
            r.Skipped = untrusted;
            return r;
        }
        foreach (var c in commands)
        {
            ct.ThrowIfCancellationRequested();
            var res = await ProcessRunner.RunAsync(c.File, c.Arguments,
                new ProcessRunOptions { Encoding = c.Encoding, Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
            if (res.Cancelled) throw new OperationCanceledException(ct);
            if (!res.Success)
            {
                var last = res.Combined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                var why = res.TimedOut ? Loc.T("превышено время ожидания") : Loc.Tf("код {0}", res.ExitCode);
                r.Error ??= string.IsNullOrEmpty(last) ? Loc.Tf("{0}: {1}", c.Display, why) : Loc.Tf("{0}: {1} — {2}", c.Display, why, last);
            }
            else
            {
                Log.Info("Очистка: {0} — выполнено.", c.Display);
            }
        }

        var after = await Task.Run(() =>
        {
            long bytes = 0;
            foreach (var t in def.Targets(env))
                bytes += CleanEngine.Analyze(t, guard, DateTime.UtcNow, CancellationToken.None).Bytes;
            return bytes;
        }, CancellationToken.None).ConfigureAwait(false);
        r.ToolFreed = Math.Max(0, a.Bytes - after);
        return r;
    }

    /// <summary>
    /// True when the bin now holds more items or more bytes than the analysis the user approved — then emptying
    /// it would also destroy things the confirmation did not list.
    /// </summary>
    public static bool RecycleBinGrew(long? approvedItems, long approvedBytes, long nowItems, long nowBytes) =>
        approvedItems is not { } items || nowItems > items || nowBytes > approvedBytes;

    public static async Task<ItemCleanResult> CleanRecycleBinAsync(CleanDefinition def, ItemAnalysis approved, CleanEnvironment env)
    {
        var r = new ItemCleanResult();
        if (PreCheck(def, env) is { } reason)
        {
            r.Skipped = reason;
            return r;
        }
        await Task.Run(() =>
        {
            var q = SystemCleanup.RunSta(NativeCleaner.QueryRecycleBin);
            // Some builds return E_UNEXPECTED for an empty bin, so the call is skipped when there is nothing.
            if (q is null || q.Value.Items == 0)
            {
                r.Notes.Add(Loc.T("Корзина уже пуста."));
                return;
            }
            // SHEmptyRecycleBinW empties everything: refuse if anything was added after the user confirmed.
            if (RecycleBinGrew(approved.Items, approved.CleanableBytes, q.Value.Items, q.Value.Bytes))
            {
                r.Skipped = Loc.T("Корзина изменилась после анализа — проверьте и подтвердите заново.");
                return;
            }
            var hr = SystemCleanup.RunSta(NativeCleaner.EmptyRecycleBin);
            if (hr != 0)
            {
                r.Error = Loc.Tf("Windows не очистила корзину (код 0x{0}).", hr.ToString("X8"));
                return;
            }
            r.ToolFreed = q.Value.Bytes;
            r.ToolItems = q.Value.Items;
        }).ConfigureAwait(false);
        return r;
    }

    public static async Task<ItemCleanResult> CleanDeliveryOptimizationAsync(CleanDefinition def, ItemAnalysis a, CleanEnvironment env, CancellationToken ct)
    {
        var r = new ItemCleanResult { Approximate = true };
        if (PreCheck(def, env) is { } reason)
        {
            r.Skipped = reason;
            return r;
        }
        var before = a.Bytes;
        var (ok, message) = await SystemCleanup.DeleteDeliveryOptimizationCacheAsync(ct).ConfigureAwait(false);
        if (!ok)
        {
            r.Error = message;
            return r;
        }
        var after = await SystemCleanup.GetDeliveryOptimizationCacheSizeAsync(CancellationToken.None).ConfigureAwait(false);
        r.ToolFreed = after.Bytes is { } b ? Math.Max(0, before - b) : before;
        return r;
    }

    public static async Task<ItemCleanResult> CleanComponentStoreAsync(CleanDefinition def, CleanEnvironment env,
        Action<string, double?> progress, CancellationToken ct)
    {
        var r = new ItemCleanResult { Approximate = true };
        if (PreCheck(def, env) is { } reason)
        {
            r.Skipped = reason;
            return r;
        }
        var outcome = await SystemCleanup.RunComponentCleanupAsync(progress, ct).ConfigureAwait(false);
        r.ToolFreed = outcome.FreedBytes;
        if (outcome.Error is not null) r.Error = outcome.Error;
        else if (outcome.Note is not null) r.Notes.Add(outcome.Note);
        return r;
    }
}

public readonly record struct DeliveryOptimizationSize(bool Available, long? Bytes)
{
    public static DeliveryOptimizationSize Unknown => new(true, null);
}

/// <summary>Windows' own cleanup tools: Delivery Optimization cmdlets, DISM, plus STA helper and the JSON report.</summary>
public static class SystemCleanup
{
    /// <summary>Shell calls (Recycle Bin) run on a short-lived STA thread.</summary>
    public static T RunSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "Lycoris cleaner shell" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result;
    }

    /// <summary>Get-DeliveryOptimizationPerfSnap.CacheSizeBytes (works without administrator rights).</summary>
    public static async Task<DeliveryOptimizationSize> GetDeliveryOptimizationCacheSizeAsync(CancellationToken ct)
    {
        const string script =
            "if (-not (Get-Command Get-DeliveryOptimizationPerfSnap -ErrorAction SilentlyContinue)) { Write-Output 'SIZE=NA'; exit 0 }; " +
            "try { $s = Get-DeliveryOptimizationPerfSnap -ErrorAction Stop; Write-Output ('SIZE=' + [string]$s.CacheSizeBytes) } " +
            "catch { Write-Output 'SIZE=ERR' }";
        var r = await ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(60) }, ct)
            .ConfigureAwait(false);
        return ParseDeliveryOptimizationSize(r.StdOut);
    }

    public static DeliveryOptimizationSize ParseDeliveryOptimizationSize(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("SIZE=", StringComparison.Ordinal)) continue;
            var v = line[5..].Trim();
            if (v == "NA") return new DeliveryOptimizationSize(false, null);
            if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) && bytes >= 0)
                return new DeliveryOptimizationSize(true, bytes);
            return DeliveryOptimizationSize.Unknown;
        }
        return DeliveryOptimizationSize.Unknown;
    }

    /// <summary>Delete-DeliveryOptimizationCache -Force (pinned files are kept: no -IncludePinnedFiles).</summary>
    public static async Task<(bool Ok, string Message)> DeleteDeliveryOptimizationCacheAsync(CancellationToken ct)
    {
        const string script =
            "if (-not (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue)) { Write-Output 'RESULT=NA'; exit 0 }; " +
            "try { Delete-DeliveryOptimizationCache -Force -ErrorAction Stop; Write-Output 'RESULT=OK' } " +
            "catch { Write-Output ('RESULT=ERR ' + $_.Exception.Message) }";
        var r = await ProcessRunner.RunPowerShellAsync(script, new ProcessRunOptions { Timeout = TimeSpan.FromMinutes(5) }, ct).ConfigureAwait(false);
        if (r.Cancelled) throw new OperationCanceledException(ct);
        var line = r.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("RESULT=", StringComparison.Ordinal));
        return line switch
        {
            "RESULT=OK" => (true, Loc.T("Кэш оптимизации доставки очищен.")),
            "RESULT=NA" => (false, Loc.T("Команда Delete-DeliveryOptimizationCache недоступна в этой версии Windows.")),
            null => (false, r.TimedOut ? Loc.T("Превышено время ожидания PowerShell.") : Loc.T("PowerShell не вернул результат.")),
            _ => (false, Loc.Tf("Ошибка: {0}", line["RESULT=ERR".Length..].Trim())),
        };
    }

    public sealed record ComponentCleanupOutcome(long FreedBytes, string? Note, string? Error);

    /// <summary>
    /// DISM /AnalyzeComponentStore, and only when it reports "Component Store Cleanup Recommended : Yes",
    /// DISM /StartComponentCleanup (never /ResetBase). The cleanup itself is not cancellable: interrupting
    /// servicing half-way is worse than waiting.
    /// </summary>
    public static async Task<ComponentCleanupOutcome> RunComponentCleanupAsync(Action<string, double?> progress, CancellationToken ct)
    {
        var dism = ProcessRunner.Sys("Dism.exe");
        if (!File.Exists(dism)) return new(0, null, Loc.T("DISM не найден в System32."));

        progress(Loc.T("DISM проверяет хранилище компонентов…"), null);
        var analyze = await ProcessRunner.RunAsync(dism, "/Online /English /Cleanup-Image /AnalyzeComponentStore",
            new ProcessRunOptions
            {
                Encoding = OutputEncoding.Oem, Timeout = TimeSpan.FromMinutes(40),
                OnOutput = l => { if (DismOutput.ParsePercent(l) is { } p) progress(Loc.T("DISM проверяет хранилище компонентов…"), p); },
            }, ct).ConfigureAwait(false);
        if (analyze.Cancelled) throw new OperationCanceledException(ct);
        if (!analyze.Success)
            return new(0, null, analyze.TimedOut ? Loc.T("DISM: превышено время анализа.") : Loc.Tf("DISM /AnalyzeComponentStore завершился с кодом {0}.", analyze.ExitCode));

        var report = DismOutput.ParseAnalyze(analyze.StdOut);
        Log.Info("DISM: размер хранилища {0}, очистка рекомендована: {1}.", report.ActualSize ?? "—", report.CleanupRecommended is true ? Loc.T("да") : Loc.T("нет"));
        if (report.CleanupRecommended is not true)
            return new(0, Loc.T("DISM сообщил, что очистка хранилища компонентов сейчас не нужна."), null);

        var drive = SystemProfile.SystemDrive;
        var before = FreeSpace(drive);
        progress(Loc.T("DISM очищает хранилище компонентов — не выключайте ПК…"), null);
        // Servicing is never killed: after the wait limit Lycoris stops waiting and leaves DISM to finish.
        var run = await ProcessRunner.RunAsync(dism, "/Online /English /Cleanup-Image /StartComponentCleanup",
            new ProcessRunOptions
            {
                Encoding = OutputEncoding.Oem, Timeout = TimeSpan.FromHours(3), KillOnTimeout = false,
                OnOutput = l => { if (DismOutput.ParsePercent(l) is { } p) progress(Loc.T("DISM очищает хранилище компонентов — не выключайте ПК…"), p); },
            }, CancellationToken.None).ConfigureAwait(false);
        var after = FreeSpace(drive);
        if (run.StillRunning)
            return new(0, Loc.T("DISM работает дольше 3 часов. Lycoris перестал ждать, но не прерывал очистку — не выключайте и не перезагружайте ПК, пока процесс Dism.exe не завершится (видно в диспетчере задач)."), null);
        if (!run.Success)
            return new(0, null, run.TimedOut ? Loc.T("DISM: превышено время очистки.") : Loc.Tf("DISM /StartComponentCleanup завершился с кодом {0}.", run.ExitCode));
        return new(Math.Max(0, after - before), Loc.T("Хранилище компонентов очищено."), null);
    }

    private static long FreeSpace(string drive)
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        catch { return 0; }
    }

    // ───────────────────────────── Report ─────────────────────────────

    public sealed record ReportItem(string Id, string Name, long FreedBytes, int Deleted, int InUse, int Denied, int Changed,
        int Errors, int FoldersRemoved, string? Skipped, string? Error, IReadOnlyList<string> Notes, IReadOnlyList<ReportFile> DeletedFiles);

    public sealed record ReportFile(string Path, long Size);

    /// <summary>
    /// Writes clean-&lt;utc&gt;.json (paths and sizes only, never contents) to %ProgramData%\MONOLITH\Logs, or the user log
    /// folder without admin rights. Keeps the newest 30 reports. Returns the file path or null.
    /// </summary>
    public static string? WriteReport(DateTime startedUtc, IReadOnlyList<ReportItem> items, long freeBefore, long freeAfter)
    {
        try
        {
            var dir = Elevation.IsElevated ? Path.Combine(AppPaths.MachineDir, "Logs") : AppPaths.LogsDir;
            if (!AppPaths.TryEnsureDir(dir)) return null;
            var file = Path.Combine(dir, $"clean-{startedUtc:yyyyMMdd-HHmmss}Z.json");
            var payload = new
            {
                StartedUtc = startedUtc,
                FinishedUtc = DateTime.UtcNow,
                Elevated = Elevation.IsElevated,
                SystemDriveFreeBefore = freeBefore,
                SystemDriveFreeAfter = freeAfter,
                FreedBytes = items.Sum(i => i.FreedBytes),
                Items = items,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var old in new DirectoryInfo(dir).EnumerateFiles("clean-*.json").OrderByDescending(f => f.Name).Skip(30))
            {
                try { old.Delete(); } catch { }
            }
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось записать отчёт об очистке: {0}", ex.Message);
            return null;
        }
    }

    public static long SystemDriveFree() => FreeSpace(SystemProfile.SystemDrive);
}
