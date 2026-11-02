using System.IO;
using System.Text;
using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Shell;

/// <summary>
/// "--e2e &lt;dir&gt;" (admin): the real-system proof that Lycoris never leaves changes behind.
/// 1. Creates a System Restore point.
/// 2. For every safe/optional tweak that applies to this PC, one at a time:
///    read state → apply through the journal → check it reports "applied" → revert → check that every
///    journaled value (registry / service / power plan / task) is exactly the original again and the tweak
///    reports the same state as before.
/// Everything goes through the normal journal (batch "Сквозной тест"), so even a crash mid-test is
/// recoverable from the Restore center. Writes report.md; exit code = number of failures.
/// </summary>
public static class EndToEndTest
{
    /// <summary>Skipped on purpose: visible desktop flicker or heavy disk work for a few-second test.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "hibernation-off",            // deletes/recreates hiberfil.sys (several GB of writes)
        "visual-effects-performance", // broadcasts UI changes: whole desktop repaints twice
        "dns-google",                 // same mechanism as dns-cloudflare, which is tested
        "recall-snapshots-off",       // the policy deletes existing Recall snapshots (user data) — never, not even for a test
    };

    private sealed record Row(string Id, string Title, string Before, string AfterApply, string AfterRevert, bool Ok, string Note);

    public static async Task<int> RunAsync(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sb = new StringBuilder();
        sb.AppendLine("# Lycoris — сквозной тест на реальной системе");
        sb.AppendLine();
        sb.AppendLine($"Время: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (!Elevation.IsElevated)
        {
            sb.AppendLine("Нет прав администратора — тест не запускался.");
            await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), sb.ToString());
            return 1;
        }

        var sp = await SystemProfile.LoadAsync();
        sb.AppendLine($"Система: {sp.OsText}, {sp.CpuName}, {sp.PrimaryGpu?.Name}");

        // 1. Restore point (also proves restore-point creation works)
        var rp = await RestorePointService.CreateAsync("Lycoris: перед сквозным тестом");
        sb.AppendLine($"Точка восстановления: {rp.Outcome} — {rp.Message}");
        sb.AppendLine();

        var rows = new List<Row>();
        var journal = ChangeJournal.Instance;
        var tweaks = TweakCatalog.All
            .Where(t => t.Risk != TweakRisk.Advanced && !Skip.Contains(t.Id) && TweakEngine.Applicability(t, sp) == true)
            .ToList();

        foreach (var t in tweaks)
        {
            var before = Safe(t.Detect);
            string afterApply = "—", afterRevert = "—", note = "";
            var ok = true;
            if (before is nameof(TweakState.NotApplicable) or nameof(TweakState.Unknown))
            {
                rows.Add(new Row(t.Id, t.Title, before, "—", "—", true, "не применимо на этом ПК — пропущено"));
                continue;
            }
            try
            {
                var session = await ChangeSession.BeginAsync($"Сквозной тест: {t.Title}", createRestorePoint: false, journal: journal);
                await Task.Run(() => session.Exclusive(() => t.Apply(new TweakContext { Session = session, System = sp, Tweak = t })));
                session.Complete();
                if (session.Errors.Count > 0) { ok = false; note += "ошибки применения: " + string.Join("; ", session.Errors) + " "; }

                afterApply = Safe(t.Detect);
                if (before != TweakState.Applied.ToString() && afterApply != TweakState.Applied.ToString())
                {
                    ok = false;
                    note += "после применения состояние не «Applied». ";
                }

                var records = journal.Records.Where(r => r.BatchId == session.Batch.Id && !r.Reverted).ToList();
                var revert = await journal.RevertAsync(records);
                if (!revert.Success) { ok = false; note += "ошибки отката: " + string.Join("; ", revert.Errors) + " "; }

                foreach (var r in records)
                {
                    var mismatch = VerifyRestored(r);
                    if (mismatch is not null) { ok = false; note += mismatch + " "; }
                }

                afterRevert = Safe(t.Detect);
                if (afterRevert != before)
                {
                    ok = false;
                    note += $"после отката состояние «{afterRevert}», а было «{before}». ";
                }
                if (records.Count == 0 && before != TweakState.Applied.ToString()) note += "(изменений не потребовалось) ";
            }
            catch (Exception ex)
            {
                ok = false;
                note += "исключение: " + ex.Message;
            }
            rows.Add(new Row(t.Id, t.Title, before, afterApply, afterRevert, ok, note.Trim()));
        }

        // Anything left active from this test (should be none) → revert now, and report.
        var leftovers = journal.ActiveRecords.Where(r => journal.Batches.Any(b => b.Id == r.BatchId && b.Title.StartsWith("Сквозной тест"))).ToList();
        if (leftovers.Count > 0)
        {
            var rr = await journal.RevertAsync(leftovers);
            sb.AppendLine($"ВНИМАНИЕ: после теста оставались активные записи ({leftovers.Count}); откат: {(rr.Success ? "успешно" : string.Join("; ", rr.Errors))}");
        }

        var failed = rows.Count(r => !r.Ok);
        sb.AppendLine($"Проверено твиков: {rows.Count}, без замечаний: {rows.Count - failed}, с ошибками: {failed}");
        sb.AppendLine();
        sb.AppendLine("| Твик | До | После применения | После отката | Итог | Примечание |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in rows)
            sb.AppendLine($"| {r.Id} | {r.Before} | {r.AfterApply} | {r.AfterRevert} | {(r.Ok ? "OK" : "FAIL")} | {r.Note.Replace("|", "/")} |");

        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), sb.ToString(), new UTF8Encoding(false));
        Log.Info($"Сквозной тест: {rows.Count - failed}/{rows.Count} без замечаний.");
        return failed;
    }

    private static string Safe(Func<TweakState> detect)
    {
        try { return detect().ToString(); }
        catch (Exception ex) { return "ошибка: " + ex.Message; }
    }

    /// <summary>Checks that the target of a reverted record holds exactly the original value again.</summary>
    private static string? VerifyRestored(ChangeRecord r)
    {
        switch (r.Kind)
        {
            case ChangeKind.Registry when r.Registry is { } s:
                var now = RegistryHelper.Snapshot(s.Hive, s.Path, s.Name);
                if (now.ValueExisted != s.ValueExisted || (s.ValueExisted && (now.Kind != s.Kind || now.Data != s.Data)))
                    return $"реестр {s.FullName}: сейчас «{now.Describe()}», было «{s.Describe()}».";
                return null;
            case ChangeKind.Service when r.Service is { } svc:
                var cur = ServiceControl.Query(svc.Name);
                if (cur is null || cur.StartMode != svc.StartMode || cur.DelayedAutoStart != svc.DelayedAutoStart)
                    return $"служба {svc.Name}: сейчас «{cur?.Describe()}», было «{svc.Describe()}».";
                return null;
            case ChangeKind.PowerScheme when r.PowerSchemeOriginal is { } orig:
                var active = PowerSchemes.GetActive();
                return active == orig ? null : $"план питания: сейчас {PowerSchemes.GetName(active)}, было {PowerSchemes.GetName(orig)}.";
            case ChangeKind.ScheduledTask when r.TaskPath is { } path && r.TaskWasEnabled is { } was:
                var info = ScheduledTasks.Query(path);
                return info.Enabled == was ? null : $"задача {path}: сейчас {(info.Enabled ? "вкл" : "выкл")}, было {(was ? "вкл" : "выкл")}.";
            default:
                return null; // commands: verified through Detect() before/after
        }
    }
}
