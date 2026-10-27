using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Modules.Settings;
using Monolith.Modules.Tools;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>Pure logic of the Tools, Restore and Settings pages. Nothing here touches the real system.</summary>
public sealed class ToolsTests
{
    // ─────────────────────────── progress parsing ───────────────────────────

    [Theory]
    [InlineData("Проверка 45% завершена.", 45)]
    [InlineData("Verification 100% complete.", 100)]
    [InlineData("[=====                      10.0%                          ]", 10)]
    [InlineData("[==========================100.0%==========================]", 100)]
    [InlineData("[===========                62,5%                          ]", 62.5)]
    [InlineData("Progress: 1234 of 5678 done; Stage: 40%; Total: 12%; ETA: 0:01:05", 12)]
    [InlineData("Ход выполнения: обработано 1234 из 5678; этап: 21%; всего: 15%; ETA: 0:01:05", 15)]
    [InlineData("Retrim:  7% complete...", 7)]
    [InlineData("Проверка 0% завершена.", 0)]
    public void Percent_is_parsed_from_tool_output(string line, double expected)
    {
        Assert.True(ToolOutput.TryParsePercent(line, out var pct));
        Assert.Equal(expected, pct, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Начало этапа проверки при сканировании системы.")]
    [InlineData("Использовано 250% квоты")]
    [InlineData("Версия образа: 10.0.26300.9457")]
    public void Lines_without_a_valid_percent_are_rejected(string? line) =>
        Assert.False(ToolOutput.TryParsePercent(line, out _));

    [Theory]
    [InlineData("Проверка 45% завершена.")]
    [InlineData("Verification 45% complete.")]
    [InlineData("[=====                      10.0%                          ]")]
    [InlineData("Progress: 0 of 456 done; Stage:  0%; Total:  0%; ETA:   0:00:12")]
    [InlineData("Анализ: 100% выполнено.")]
    public void Progress_updates_are_transient(string line) => Assert.True(ToolOutput.IsProgressLine(line));

    [Theory]
    [InlineData("Фрагментированное пространство = 0%")]
    [InlineData("Total fragmented space = 3%")]
    [InlineData("Свободно 45% места на диске")]
    [InlineData("Программа защиты ресурсов Windows не обнаружила нарушений целостности.")]
    public void Report_lines_are_kept(string line) => Assert.False(ToolOutput.IsProgressLine(line));

    [Fact]
    public void Dism_bar_is_compacted()
    {
        Assert.Equal("[############............] 50.0%",
            ToolOutput.NormalizeProgressLine("[=============================50.0%                          ]"));
        Assert.Equal("[........................] 0.0%", ToolOutput.NormalizeProgressLine("[                           0.0%                           ]"));
        Assert.Equal("Проверка 45% завершена.", ToolOutput.NormalizeProgressLine("  Проверка 45% завершена.  "));
    }

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(761, "12:41")]
    [InlineData(3723, "1:02:03")]
    public void Elapsed_time_is_compact(int seconds, string expected) =>
        Assert.Equal(expected, ToolOutput.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(99.6, "99")]
    [InlineData(100, "100")]
    [InlineData(-3, "0")]
    public void Percent_readout_never_rounds_up_to_done(double value, string expected) =>
        Assert.Equal(expected, ToolOutput.FormatPercent(value));

    // ─────────────────────────── result classification ───────────────────────────

    [Theory]
    [InlineData("Защита ресурсов Windows не обнаружила нарушений целостности.", ToolStatus.Success)]
    [InlineData("Windows Resource Protection did not find any integrity violations.", ToolStatus.Success)]
    [InlineData("Программа защиты ресурсов Windows обнаружила поврежденные файлы и успешно их восстановила.", ToolStatus.Success)]
    [InlineData("Windows Resource Protection found corrupt files and successfully repaired them.", ToolStatus.Success)]
    [InlineData("Программа защиты ресурсов Windows обнаружила поврежденные файлы, но не может восстановить некоторые из них.", ToolStatus.Failed)]
    [InlineData("Windows Resource Protection found corrupt files but was unable to fix some of them.", ToolStatus.Failed)]
    [InlineData("There is a system repair pending which requires reboot to complete.", ToolStatus.Warning)]
    [InlineData("Windows Resource Protection could not perform the requested operation.", ToolStatus.Failed)]
    public void Sfc_results_are_classified(string output, ToolStatus expected) =>
        Assert.Equal(expected, ToolOutput.ClassifySfc(0, output).Status);

    [Fact]
    public void Sfc_falls_back_to_exit_code()
    {
        Assert.Equal(ToolStatus.Success, ToolOutput.ClassifySfc(0, "непонятный вывод").Status);
        Assert.Equal(ToolStatus.Failed, ToolOutput.ClassifySfc(2, "").Status);
    }

    [Theory]
    [InlineData(ToolOutput.DismMode.CheckHealth, 0, "Повреждение хранилища компонентов не обнаружено.", ToolStatus.Success)]
    [InlineData(ToolOutput.DismMode.ScanHealth, 0, "No component store corruption detected.", ToolStatus.Success)]
    [InlineData(ToolOutput.DismMode.CheckHealth, 0, "Хранилище компонентов подлежит восстановлению.", ToolStatus.Warning)]
    [InlineData(ToolOutput.DismMode.ScanHealth, 0, "The component store is repairable.", ToolStatus.Warning)]
    [InlineData(ToolOutput.DismMode.CheckHealth, 0, "The component store cannot be repaired.", ToolStatus.Failed)]
    [InlineData(ToolOutput.DismMode.RestoreHealth, 0, "The restore operation completed successfully.", ToolStatus.Success)]
    [InlineData(ToolOutput.DismMode.RestoreHealth, 740, "Ошибка: 740", ToolStatus.Failed)]
    public void Dism_results_are_classified(ToolOutput.DismMode mode, int exit, string output, ToolStatus expected) =>
        Assert.Equal(expected, ToolOutput.ClassifyDism(mode, exit, output).Status);

    [Fact]
    public void Dism_source_error_explains_internet()
    {
        var outcome = ToolOutput.ClassifyDism(ToolOutput.DismMode.RestoreHealth, unchecked((int)0x800f081f), "Error: 0x800f081f\r\nThe source files could not be found.");
        Assert.Equal(ToolStatus.Failed, outcome.Status);
        Assert.Contains("0x800f081f", outcome.Summary);
        Assert.Contains("интернет", outcome.Summary);
    }

    [Theory]
    [InlineData("Ошибка: 0x800f0906", "0x800f0906")]
    [InlineData("Error: 87", "87")]
    [InlineData("всё хорошо", null)]
    public void Error_code_is_extracted(string output, string? expected) => Assert.Equal(expected, ToolOutput.FindErrorCode(output));

    [Theory]
    [InlineData(0, ToolStatus.Success)]
    [InlineData(1, ToolStatus.Success)]
    [InlineData(2, ToolStatus.Success)]
    [InlineData(3, ToolStatus.Warning)]
    [InlineData(5, ToolStatus.Failed)]
    public void Chkdsk_exit_codes_are_classified(int exit, ToolStatus expected) =>
        Assert.Equal(expected, ToolOutput.ClassifyChkdsk(exit, "").Status);

    [Theory]
    [InlineData("Тип файловой системы: NTFS.\r\nC: не загрязнен.", ToolStatus.Success)]
    [InlineData("The type of the file system is NTFS.\r\nC: is not dirty.", ToolStatus.Success)]
    [InlineData("C: загрязнен.", ToolStatus.Warning)]
    [InlineData("C: is dirty.", ToolStatus.Warning)]
    [InlineData("Cannot query state of drive C:", ToolStatus.Failed)]
    public void Chkntfs_dirty_state_is_read(string output, ToolStatus expected) =>
        Assert.Equal(expected, ToolOutput.ClassifyChkntfs(0, output, "C:").Status);

    [Fact]
    public void Boot_check_is_detected_from_BootExecute()
    {
        Assert.False(ToolOutput.IsBootCheckScheduled(["autocheck autochk *"]));
        Assert.False(ToolOutput.IsBootCheckScheduled(["autocheck  autochk  *", ""]));
        Assert.False(ToolOutput.IsBootCheckScheduled(null));
        Assert.False(ToolOutput.IsBootCheckScheduled(["sdnclean64.exe"]));
        Assert.True(ToolOutput.IsBootCheckScheduled(["autocheck autochk /p \\??\\C:", "autocheck autochk *"]));
        Assert.True(ToolOutput.IsBootCheckScheduled(["autocheck autochk /r \\??\\D:"]));
    }

    // ─────────────────────────── undo center ───────────────────────────

    [Theory]
    [InlineData(@"reg:HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled", @"HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled")]
    [InlineData("svc:DiagTrack", "DiagTrack")]
    [InlineData(@"task:\Microsoft\Windows\Defrag\ScheduledDefrag", @"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData("power:active", "Активная схема питания")]
    [InlineData("cmd:fsutil-8dot3", "fsutil-8dot3")]
    [InlineData("", "—")]
    [InlineData("plain", "plain")]
    public void Journal_targets_are_readable(string target, string expected) =>
        Assert.Equal(expected, RestoreFormat.DescribeTarget(target));

    [Fact]
    public void Record_state_and_status()
    {
        var active = new ChangeRecord { Title = "A" };
        var failed = new ChangeRecord { Title = "B", RevertError = "нет доступа" };
        var reverted = new ChangeRecord { Title = "C", Reverted = true, RevertedUtc = new DateTime(2026, 10, 26, 17, 40, 0, DateTimeKind.Utc) };

        Assert.Equal(RecordState.Active, RestoreFormat.StateOf(active));
        Assert.Equal("АКТИВНО", RestoreFormat.StatusText(active));
        Assert.Equal(RecordState.Failed, RestoreFormat.StateOf(failed));
        Assert.Equal("ОШИБКА ОТКАТА", RestoreFormat.StatusText(failed));
        Assert.Equal(RecordState.Reverted, RestoreFormat.StateOf(reverted));
        Assert.StartsWith("ОТКАЧЕНО ", RestoreFormat.StatusText(reverted));
    }

    [Theory]
    [InlineData("Точка восстановления создана (26.10 20:31).", true)]
    [InlineData("Нет прав администратора — точка восстановления не создана.", false)]
    [InlineData("Windows не создала новую точку (недавно уже была создана).", false)]
    [InlineData(null, false)]
    public void Restore_point_messages_are_recognized(string? message, bool created) =>
        Assert.Equal(created, RestoreFormat.IsRestorePointCreated(message));

    [Fact]
    public void Journal_is_grouped_newest_batch_first()
    {
        var t0 = new DateTime(2026, 10, 25, 10, 0, 0, DateTimeKind.Utc);
        var old = new ChangeBatch { Title = "Старый", TimestampUtc = t0 };
        var recent = new ChangeBatch { Title = "Новый", TimestampUtc = t0.AddDays(1) };
        var empty = new ChangeBatch { Title = "Сброс сети", TimestampUtc = t0.AddDays(2) };
        var records = new List<ChangeRecord>
        {
            new() { BatchId = old.Id, Title = "2", TimestampUtc = t0.AddMinutes(2) },
            new() { BatchId = old.Id, Title = "1", TimestampUtc = t0.AddMinutes(1), Reverted = true },
            new() { BatchId = recent.Id, Title = "3", TimestampUtc = t0.AddDays(1) },
            new() { BatchId = Guid.NewGuid(), Title = "сирота", TimestampUtc = t0.AddDays(3) },
        };

        var all = RestoreFormat.Group([old, recent, empty], records, JournalFilter.All);
        Assert.Equal(["Изменения без пакета", "Сброс сети", "Новый", "Старый"], all.Select(g => g.Batch.Title));
        Assert.True(all[0].IsSynthetic);
        Assert.Empty(all[1].Records);
        Assert.Equal(["1", "2"], all[3].Records.Select(r => r.Title));
        Assert.Equal(1, all[3].ActiveCount);
        Assert.Equal(2, all[3].TotalCount);

        var active = RestoreFormat.Group([old, recent, empty], records, JournalFilter.Active);
        Assert.DoesNotContain(active, g => g.Batch.Title == "Сброс сети");
        Assert.Equal(["2"], active.Single(g => g.Batch.Title == "Старый").Records.Select(r => r.Title));

        var reverted = RestoreFormat.Group([old, recent, empty], records, JournalFilter.Reverted);
        var only = Assert.Single(reverted);
        Assert.Equal("Старый", only.Batch.Title);
        Assert.Equal(2, only.TotalCount);
    }

    [Theory]
    [InlineData(1, "изменение")]
    [InlineData(2, "изменения")]
    [InlineData(4, "изменения")]
    [InlineData(5, "изменений")]
    [InlineData(11, "изменений")]
    [InlineData(12, "изменений")]
    [InlineData(21, "изменение")]
    [InlineData(22, "изменения")]
    [InlineData(111, "изменений")]
    [InlineData(0, "изменений")]
    public void Russian_plurals(int n, string expected) =>
        Assert.Equal(expected, RestoreFormat.Plural(n, "изменение", "изменения", "изменений"));

    [Theory]
    [InlineData(12u, "ИЗМЕНЕНИЕ НАСТРОЕК")]
    [InlineData(0u, "УСТАНОВКА ПРОГРАММЫ")]
    [InlineData(99u, "ТИП 99")]
    public void Restore_point_types_have_labels(uint type, string expected) =>
        Assert.Equal(expected, RestoreFormat.RestorePointTypeLabel(type));

    [Fact]
    public void Monolith_points_are_marked()
    {
        Assert.True(RestoreFormat.IsMonolithPoint("MONOLITH: точка, созданная вручную"));
        Assert.False(RestoreFormat.IsMonolithPoint("Windows Update"));
        Assert.False(RestoreFormat.IsMonolithPoint(null));
    }

    // ─────────────────────────── undo center: live value / drift ───────────────────────────

    [Theory]
    [InlineData("DWord: 1", RegistryValueKind.DWord, 1, LiveState.Same)]
    [InlineData("DWord: 1", RegistryValueKind.DWord, 0, LiveState.Drifted)]
    [InlineData("DWord: -1", RegistryValueKind.DWord, -1, LiveState.Same)]
    [InlineData("DWord: 4294967295", RegistryValueKind.DWord, -1, LiveState.Same)]
    [InlineData("DWord: 2", RegistryValueKind.String, "2", LiveState.Drifted)]
    [InlineData("String: Deny", RegistryValueKind.String, "Deny", LiveState.Same)]
    [InlineData("String: Deny", RegistryValueKind.String, "Allow", LiveState.Drifted)]
    [InlineData("ExpandString: %SystemRoot%\\x", RegistryValueKind.ExpandString, "%SystemRoot%\\x", LiveState.Same)]
    [InlineData("Нечто непонятное", RegistryValueKind.DWord, 1, LiveState.Unknown)]
    public void Registry_drift_is_detected_by_value(string journal, RegistryValueKind kind, object current, LiveState expected) =>
        Assert.Equal(expected, RestoreLive.CompareRegistry(journal, kind, current).State);

    [Fact]
    public void Registry_drift_handles_deleted_binary_multi_string_and_qword_values()
    {
        Assert.Equal(LiveState.Same, RestoreLive.CompareRegistry("удалено", null, null).State);
        var recreated = RestoreLive.CompareRegistry("удалено", RegistryValueKind.DWord, 1);
        Assert.Equal(LiveState.Drifted, recreated.State);
        Assert.Equal("DWord: 1", recreated.Text);

        var gone = RestoreLive.CompareRegistry("DWord: 0", null, null);
        Assert.Equal(LiveState.Drifted, gone.State);
        Assert.Equal(RestoreLive.Absent, gone.Text);

        Assert.Equal(LiveState.Same, RestoreLive.CompareRegistry("Binary: 02 00 FF", RegistryValueKind.Binary, new byte[] { 2, 0, 0xFF }).State);
        Assert.Equal(LiveState.Drifted, RestoreLive.CompareRegistry("Binary: 02 00 FF", RegistryValueKind.Binary, new byte[] { 3, 0, 0xFF }).State);
        Assert.Equal(LiveState.Same, RestoreLive.CompareRegistry("MultiString: a; б", RegistryValueKind.MultiString, new[] { "a", "б" }).State);
        Assert.Equal(LiveState.Same, RestoreLive.CompareRegistry("QWord: 1234567890123", RegistryValueKind.QWord, 1234567890123L).State);
    }

    [Fact]
    public void Service_task_and_power_drift_compare_the_journal_text()
    {
        Assert.Equal(LiveState.Same, RestoreLive.CompareText("Вручную", "Вручную").State);
        var drift = RestoreLive.CompareText("Вручную", "Отключена");
        Assert.Equal(LiveState.Drifted, drift.State);
        Assert.Equal("Отключена", drift.Text);
    }

    [Fact]
    public void Revert_dialog_shows_the_live_value_not_the_journal_copy()
    {
        var r = new ChangeRecord
        {
            Title = "Телеметрия", Kind = ChangeKind.Service, Target = "svc:DiagTrack",
            OldValue = "Автоматически", NewValue = "Вручную",
            Service = new ServiceStartSnapshot("DiagTrack", System.ServiceProcess.ServiceStartMode.Automatic, false),
        };

        var same = RestoreFormat.RevertLines(r, new LiveValue(LiveState.Same, "Вручную"));
        Assert.Contains("Сейчас: Вручную — как установил MONOLITH", same);
        Assert.Contains("Станет: Автоматически", same);

        var drifted = RestoreFormat.RevertLines(r, new LiveValue(LiveState.Drifted, "Отключена"));
        Assert.Contains("MONOLITH установил: Вручную", drifted);
        Assert.Contains(drifted, l => l.StartsWith("Сейчас: Отключена — изменено после MONOLITH"));
        Assert.DoesNotContain(drifted, l => l.StartsWith("Сейчас: Вручную"));

        var line = RestoreFormat.BatchLine(new LiveRecord(r, new LiveValue(LiveState.Drifted, "Отключена")));
        Assert.Contains("сейчас «Отключена»", line);
        Assert.Contains("изменено после MONOLITH", line);
        Assert.Contains("«Автоматически»", line);

        var gone = RestoreFormat.BatchLine(new LiveRecord(r, new LiveValue(LiveState.Gone, "службы «DiagTrack» больше нет в Windows")));
        Assert.Contains("возвращать нечего", gone);
    }

    [Fact]
    public void Revert_dialog_names_the_real_target_and_command()
    {
        // Free-text Target / Title can be anything; the dialog shows what the revert data really touches.
        var reg = new ChangeRecord
        {
            Title = "Безобидно", Kind = ChangeKind.Registry, Target = "reg:HKCU\\Harmless",
            Registry = new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = @"SOFTWARE\Test", Name = "Value" },
        };
        Assert.Equal(@"HKLM\SOFTWARE\Test\Value", RestoreFormat.RevertTarget(reg));

        var script = "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;Set-Something -Value 1";
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        var cmd = new ChangeRecord
        {
            Title = "Команда", Kind = ChangeKind.Command, Target = "cmd:test", OldValue = "0", NewValue = "1",
            RevertFile = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
            RevertArgs = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
        };
        Assert.Equal("powershell.exe (скрипт): Set-Something -Value 1", RestoreFormat.DescribeCommand(cmd));
        Assert.Contains(RestoreFormat.RevertLines(cmd, new LiveValue(LiveState.Unknown, "—")), l => l.StartsWith("Команда отката: powershell.exe"));

        var netsh = new ChangeRecord { Kind = ChangeKind.Command, RevertFile = @"C:\Windows\System32\netsh.exe", RevertArgs = "int tcp set global autotuninglevel=normal" };
        Assert.Equal("netsh.exe int tcp set global autotuninglevel=normal", RestoreFormat.DescribeCommand(netsh));
    }

    [Fact]
    public void Merged_records_name_every_source()
    {
        var r = new ChangeRecord { Source = "tweak:power-plan-performance", AlsoSources = ["tweak:power-plan-ultimate"], Kind = ChangeKind.PowerScheme };
        Assert.Equal("tweak:power-plan-performance + tweak:power-plan-ultimate", new RecordItem(r).Source);
        Assert.Contains(RestoreFormat.RevertLines(r, new LiveValue(LiveState.Same, "x")), l => l.Contains("tweak:power-plan-ultimate"));
    }

    [Theory]
    [InlineData(1, "1 активное изменение MONOLITH вернётся")]
    [InlineData(3, "3 активных изменения MONOLITH вернутся")]
    [InlineData(5, "5 активных изменений MONOLITH вернутся")]
    [InlineData(21, "21 активное изменение MONOLITH вернётся")]
    public void Revert_all_message_agrees_with_the_count(int n, string expected) =>
        Assert.Equal(expected, RestoreFormat.ActiveChangesWillReturn(n));

    // ─────────────────────────── tools: running and opening ───────────────────────────

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\battery-report.html", true)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\sub\report.htm", true)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\report.exe", false)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\..\evil.html", false)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs-other\report.html", false)]
    [InlineData(@"C:\Windows\report.html", false)]
    public void Only_own_html_reports_are_opened_in_a_browser(string file, bool allowed) =>
        Assert.Equal(allowed, ShellLauncher.IsOwnHtmlReport(file, @"C:\Users\u\AppData\Local\MONOLITH\Logs"));

    [Fact]
    public void Network_reset_confirmation_tells_the_truth_about_the_restore_point()
    {
        Assert.StartsWith("Перед сбросом будет создана точка восстановления", ToolOutput.NetworkResetRestoreNote(true, true));
        Assert.Contains("создана не будет", ToolOutput.NetworkResetRestoreNote(true, false));
        Assert.Contains("выключено в настройках", ToolOutput.NetworkResetRestoreNote(false, true));
    }

    [Fact]
    public void Slow_servicing_note_says_monolith_will_not_stop_it()
    {
        var note = ToolOutput.SlowRunNote(TimeSpan.FromMinutes(45));
        Assert.Contains("45 мин", note);
        Assert.Contains("не прервёт", note);
    }

    // ─────────────────────────── settings ───────────────────────────

    [Fact]
    public void Reset_restores_preferences_and_keeps_user_lists()
    {
        var s = new AppSettings
        {
            CreateRestorePoints = false,
            ConfirmBeforeApply = false,
            ShowConsole = false,
            MonitorIntervalMs = 5000,
            LastProfile = "Gaming",
            GameBoostPowerPlan = false,
            GameBoostPriority = false,
            GameBoostPurgeStandby = true,
            GameBoostCloseList = ["discord"],
            CustomGameFolders = [@"D:\Games"],
            FirstRunCompleted = true,
        };

        SettingsDefaults.Apply(s);

        var d = new AppSettings();
        Assert.Equal(d.CreateRestorePoints, s.CreateRestorePoints);
        Assert.Equal(d.ConfirmBeforeApply, s.ConfirmBeforeApply);
        Assert.Equal(d.ShowConsole, s.ShowConsole);
        Assert.Equal(d.MonitorIntervalMs, s.MonitorIntervalMs);
        Assert.Equal(d.LastProfile, s.LastProfile);
        Assert.Equal(d.GameBoostPowerPlan, s.GameBoostPowerPlan);
        Assert.Equal(d.GameBoostPriority, s.GameBoostPriority);
        Assert.Equal(d.GameBoostPurgeStandby, s.GameBoostPurgeStandby);
        Assert.Equal(["discord"], s.GameBoostCloseList);
        Assert.Equal([@"D:\Games"], s.CustomGameFolders);
        Assert.True(s.FirstRunCompleted);
    }

    [Fact]
    public void Reset_description_lists_every_change()
    {
        var lines = SettingsDefaults.Describe();
        Assert.Contains(lines, l => l.StartsWith("Точка восстановления перед изменениями — включено"));
        Assert.Contains(lines, l => l.Contains("1 с"));
        Assert.Contains(lines, l => l.StartsWith("Сохраняются"));
    }

    [Theory]
    [InlineData(500, 500)]
    [InlineData(750, 500)]
    [InlineData(800, 1000)]
    [InlineData(1000, 1000)]
    [InlineData(3000, 2000)]
    [InlineData(60000, 5000)]
    [InlineData(0, 500)]
    public void Monitor_interval_snaps_to_an_offered_choice(int stored, int expected) =>
        Assert.Equal(expected, SettingsDefaults.NearestInterval(stored));

    [Theory]
    [InlineData(500, "0,5 с")]
    [InlineData(1000, "1 с")]
    [InlineData(5000, "5 с")]
    public void Monitor_interval_is_formatted(int ms, string expected) =>
        Assert.Equal(expected, SettingsDefaults.FormatInterval(ms));

    [Theory]
    [InlineData("8.4.2+5b4c1d2", "8.4.2")]
    [InlineData("8.4.2.1", "8.4.2")]
    [InlineData("10.0.14", "10.0.14")]
    [InlineData("1.0.0-preview.3+abc", "1.0.0-preview.3")]
    [InlineData(null, "2.1.0")]
    public void Component_versions_are_cleaned(string? informational, string expected) =>
        Assert.Equal(expected, SettingsDefaults.CleanVersion(informational, new Version(2, 1, 0, 7)));
}

/// <summary>
/// The undo center's live read against real journal records: values are written through ChangeSession into a
/// redirected registry (HKCU\Software\MONOLITH-Tests\Redirect-*) and a temp journal — never the real settings.
/// </summary>
[Collection("Registry")]
public sealed class RestoreLiveRegistryTests : IDisposable
{
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-restorelive-{Guid.NewGuid():N}.json");

    public RestoreLiveRegistryTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    [Fact]
    public async Task Values_set_through_the_journal_read_back_as_unchanged_until_something_else_changes_them()
    {
        const string key = @"SOFTWARE\MONOLITH\LiveRead";
        var journal = new ChangeJournal(_journalPath);
        var session = await ChangeSession.BeginAsync("live read", createRestorePoint: false, journal: journal);
        session.SetRegistry(RegHive.HKLM, key, "Dw", RegistryValueKind.DWord, 0xFFFFFFFFu, "test", "Dw");
        session.SetRegistry(RegHive.HKLM, key, "Qw", RegistryValueKind.QWord, 1234567890123L, "test", "Qw");
        session.SetRegistry(RegHive.HKLM, key, "Sz", RegistryValueKind.String, "текст", "test", "Sz");
        session.SetRegistry(RegHive.HKLM, key, "Ms", RegistryValueKind.MultiString, new[] { "a", "б" }, "test", "Ms");
        session.SetRegistry(RegHive.HKLM, key, "Bin", RegistryValueKind.Binary, new byte[] { 2, 0, 0xFF }, "test", "Bin");
        RegistryHelper.Write(RegHive.HKLM, key, "Del", RegistryValueKind.DWord, 7);
        session.SetRegistry(RegHive.HKLM, key, "Del", RegistryValueKind.DWord, null, "test", "Del");
        session.Complete();
        Assert.Empty(session.Errors);

        var records = journal.ActiveRecords;
        Assert.Equal(6, records.Count);
        Assert.All(records, r => Assert.Equal(LiveState.Same, RestoreLive.Read(r).State));

        // Someone else changes two of them afterwards.
        RegistryHelper.Write(RegHive.HKLM, key, "Dw", RegistryValueKind.DWord, 0);
        RegistryHelper.Write(RegHive.HKLM, key, "Del", RegistryValueKind.DWord, 9);
        var dw = RestoreLive.Read(records.Single(r => r.Title == "Dw"));
        Assert.Equal(LiveState.Drifted, dw.State);
        Assert.Equal("DWord: 0", dw.Text);
        Assert.Equal(LiveState.Drifted, RestoreLive.Read(records.Single(r => r.Title == "Del")).State);
        Assert.Equal(LiveState.Same, RestoreLive.Read(records.Single(r => r.Title == "Sz")).State);
    }

    public static IEnumerable<object[]> RegistryTweakIds() =>
        TweakCatalog.All.OfType<RegistryTweak>().Select(t => new object[] { t.Id });

    /// <summary>Every catalog registry tweak writes NewValue text that the live read understands (no false drift).</summary>
    [Theory]
    [MemberData(nameof(RegistryTweakIds))]
    public async Task Catalog_tweaks_are_not_reported_as_drifted_right_after_apply(string id)
    {
        var tweak = (RegistryTweak)TweakCatalog.Find(id)!;
        var journal = new ChangeJournal(_journalPath);
        var session = await ChangeSession.BeginAsync("live " + id, createRestorePoint: false, journal: journal);
        tweak.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = tweak });
        session.Complete();
        Assert.Empty(session.Errors);
        Assert.All(journal.ActiveRecords, r => Assert.Equal(LiveState.Same, RestoreLive.Read(r).State));
    }
}
