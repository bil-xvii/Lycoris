using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Modules.Settings;
using Monolith.Modules.Tools;
using Monolith.Shell;

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

    // ─────────────────────────── quick links & drives ───────────────────────────

    [Fact]
    public void Quick_links_do_not_duplicate_monolith_pages()
    {
        var commands = ToolsViewModel.BuildQuickLinks().Select(l => l.Command).ToList();
        Assert.Equal(["diskmgmt.msc", "eventvwr.msc", "resmon.exe", "taskmgr.exe", "msinfo32.exe"], commands);
    }

    [Fact]
    public void Per_drive_defrag_button_carries_its_volume()
    {
        var v = new VolumeInfo("D:", "Игры", "NTFS", 1_000_000_000_000, 250_000_000_000, "HDD", "SATA", false);
        var a = ToolsViewModel.DefragAction(v);
        Assert.Equal(ToolOp.DefragVolume, a.Op);
        Assert.Same(v, a.Volume);
        Assert.True(a.RequiresAdmin);
        Assert.Equal("D: · ДЕФРАГМЕНТАЦИЯ", a.Label);
        Assert.Contains("HDD · SATA", a.Hint);
    }

    [Fact]
    public void Volume_set_change_is_detected_by_letter_and_media()
    {
        VolumeInfo V(string letter, string media, ulong free = 1) => new(letter, "", "NTFS", 10, free, media, "NVMe", letter == "C:");
        Assert.True(ToolsViewModel.SameVolumes([V("C:", "SSD")], [V("C:", "SSD", free: 5)]));
        Assert.False(ToolsViewModel.SameVolumes([V("C:", "SSD")], [V("C:", "SSD"), V("D:", "HDD")]));
        Assert.False(ToolsViewModel.SameVolumes([V("C:", "SSD")], [V("C:", "HDD")]));
    }

    // ─────────────────────────── power & sleep diagnostics (read-only) ───────────────────────────

    private static IReadOnlyList<ToolCard> Diagnostics(bool hasBattery, bool modernStandby) =>
        ToolsViewModel.BuildSections([], "C:", hasBattery, modernStandby).Single(s => s.Title == "ДИАГНОСТИКА").Cards;

    [Fact]
    public void Diagnostics_section_has_sleep_wake_and_power_report_cards()
    {
        var cards = Diagnostics(hasBattery: false, modernStandby: false);
        Assert.Equal(["СОН И ПРОБУЖДЕНИЕ", "ОТЧЁТЫ О ПИТАНИИ И СНЕ", "ДИАГНОСТИКА ПАМЯТИ"], cards.Select(c => c.Title));

        var sleepWake = cards[0];
        var check = Assert.Single(sleepWake.Actions);
        Assert.Equal(ToolOp.SleepWakeDiagnostics, check.Op);
        Assert.Equal("ПРОВЕРИТЬ", check.Label);
        Assert.False(check.RequiresAdmin); // one button; without admin rights the card runs the three unprivileged queries
        Assert.False(sleepWake.RequiresAdmin);
        Assert.Equal(ToolsViewModel.SleepWakeCommandText, sleepWake.Command);
        Assert.Contains("Ничего не меняет", sleepWake.Note);

        var reports = cards[1];
        Assert.Equal([ToolOp.EnergyReport, ToolOp.SystemSleepDiagnostics], reports.Actions.Select(a => a.Op));
        var energy = reports.Actions.Single(a => a.Op == ToolOp.EnergyReport);
        Assert.True(energy.RequiresAdmin);
        Assert.True(energy.IsPrimary);
        Assert.Equal("ЭНЕРГИЯ, 60 С", energy.Label);
        Assert.True(reports.Actions.Single(a => a.Op == ToolOp.SystemSleepDiagnostics).RequiresAdmin);
        Assert.Equal("powercfg /energy · /systemsleepdiagnostics", reports.Command);
        Assert.Contains("во время игрового сеанса он не запускается", reports.Note);
    }

    [Fact]
    public void Battery_and_sleepstudy_reports_follow_the_hardware()
    {
        var laptop = Diagnostics(hasBattery: true, modernStandby: true)[1];
        Assert.Equal([ToolOp.BatteryReport, ToolOp.EnergyReport, ToolOp.SleepStudyReport, ToolOp.SystemSleepDiagnostics], laptop.Actions.Select(a => a.Op));
        Assert.Equal("powercfg /batteryreport · /energy · /sleepstudy · /systemsleepdiagnostics", laptop.Command);
        Assert.False(laptop.Actions.Single(a => a.Op == ToolOp.BatteryReport).RequiresAdmin);
        Assert.True(laptop.Actions.Single(a => a.Op == ToolOp.SleepStudyReport).RequiresAdmin); // powercfg /sleepstudy needs elevation

        var desktop = Diagnostics(hasBattery: false, modernStandby: false)[1];
        Assert.DoesNotContain(desktop.Actions, a => a.Op == ToolOp.BatteryReport);
        Assert.DoesNotContain(desktop.Actions, a => a.Op == ToolOp.SleepStudyReport);

        var modernDesktop = Diagnostics(hasBattery: false, modernStandby: true)[1];
        Assert.Contains(modernDesktop.Actions, a => a.Op == ToolOp.SleepStudyReport);
        Assert.DoesNotContain(modernDesktop.Actions, a => a.Op == ToolOp.BatteryReport);
    }

    [Fact]
    public void Sleep_wake_note_names_the_existing_wake_timers_tweak()
    {
        var tweak = Monolith.Tweaks.TweakCatalog.Find(ToolsViewModel.WakeTimersTweakId);
        Assert.NotNull(tweak);
        Assert.Equal("ПК не просыпается ночью по расписанию программ", tweak!.Title);

        var card = Diagnostics(hasBattery: false, modernStandby: false)[0];
        Assert.Equal("СОН И ПРОБУЖДЕНИЕ", card.Title);
        Assert.StartsWith("Ничего не меняет.", card.Note);
        Assert.Contains($"есть пункт «{tweak.Title}» в разделе «Оптимизация»", card.Note);
        Assert.Contains("/requests и /waketimers пропускаются", card.Note);
    }

    [Fact]
    public void Sleep_wake_card_runs_the_five_documented_powercfg_queries()
    {
        Assert.Equal(["/lastwake", "/waketimers", "/requests", "/devicequery wake_armed", "/availablesleepstates"],
            ToolsViewModel.SleepWakeQueries.Select(q => q.Arguments));
        Assert.Equal("powercfg /lastwake · /waketimers · /requests · /devicequery wake_armed · /availablesleepstates", ToolsViewModel.SleepWakeCommandText);
        Assert.Equal(["/waketimers", "/requests"], ToolsViewModel.SleepWakeQueries.Where(q => q.RequiresAdmin).Select(q => q.Arguments));
        Assert.All(ToolsViewModel.SleepWakeQueries, q => Assert.False(string.IsNullOrWhiteSpace(q.Title)));
    }

    [Fact]
    public void Power_reports_are_written_to_the_logs_folder_and_never_change_anything()
    {
        var file = @"C:\Users\u\AppData\Local\MONOLITH\Logs\r.html";
        Assert.Equal(["/batteryreport", "/output", file], ToolsViewModel.BatteryReport.Arguments(file));
        Assert.Equal(["/energy", "/output", file, "/duration", "60"], ToolsViewModel.EnergyReport.Arguments(file));
        Assert.Equal(["/sleepstudy", "/output", file, "/duration", "3"], ToolsViewModel.SleepStudyReport.Arguments(file));
        Assert.Equal(["/systemsleepdiagnostics", "/output", file], ToolsViewModel.SleepDiagnosticsReport.Arguments(file));
        Assert.True(ToolsViewModel.EnergyReport.NonZeroExitMeansProblemsFound);
        Assert.False(ToolsViewModel.BatteryReport.NonZeroExitMeansProblemsFound);
        Assert.True(ToolsViewModel.EnergyReport.Timeout >= TimeSpan.FromMinutes(2)); // 60 s of tracing + report generation
        foreach (var spec in new[] { ToolsViewModel.BatteryReport, ToolsViewModel.EnergyReport, ToolsViewModel.SleepStudyReport, ToolsViewModel.SleepDiagnosticsReport })
        {
            // Plain .html names inside the logs folder: exactly what UnelevatedShell.IsAllowedLocalReport lets a browser open.
            Assert.EndsWith(".html", spec.FileName);
            Assert.DoesNotContain('\\', spec.FileName);
            Assert.DoesNotContain('/', spec.FileName);
            Assert.StartsWith("powercfg ", spec.Display);
        }
    }

    [Fact]
    public void Energy_report_problems_are_a_warning_not_a_failure()
    {
        const string file = @"C:\Logs\energy-report.html";
        var energy = ToolsViewModel.EnergyReport;

        var found = ToolsViewModel.ClassifyPowerReport(energy, exitCode: 7, fileExists: true, opened: true, file, elevated: true);
        Assert.Equal(ToolStatus.Warning, found.Status);
        Assert.Contains("Найдены проблемы энергоэффективности", found.Summary);
        Assert.Contains("код 7", found.Summary);
        Assert.DoesNotContain("Найдено ошибок/предупреждений", found.Summary); // the exit code is not a count of problems
        Assert.Contains("см. отчёт", found.Summary);

        var clean = ToolsViewModel.ClassifyPowerReport(energy, 0, true, true, file, true);
        Assert.Equal(ToolStatus.Success, clean.Status);
        Assert.Equal("Отчёт об энергоэффективности создан и открыт в браузере.", clean.Summary);

        var notOpened = ToolsViewModel.ClassifyPowerReport(energy, 0, true, false, file, true);
        Assert.Equal(ToolStatus.Warning, notOpened.Status);
        Assert.Contains("Откройте файл вручную: " + file, notOpened.Summary);

        var missing = ToolsViewModel.ClassifyPowerReport(energy, 1, false, false, file, elevated: true);
        Assert.Equal(ToolStatus.Failed, missing.Status);
        Assert.Equal("Отчёт не создан (код 1).", missing.Summary);

        var unelevated = ToolsViewModel.ClassifyPowerReport(ToolsViewModel.SleepDiagnosticsReport, 1, false, false, file, elevated: false);
        Assert.Equal(ToolStatus.Failed, unelevated.Status);
        Assert.Contains("права администратора", unelevated.Summary);

        // Other reports: a created file with a non-zero code is kept, but flagged as possibly incomplete.
        var battery = ToolsViewModel.ClassifyPowerReport(ToolsViewModel.BatteryReport, 3, true, true, file, true);
        Assert.Equal(ToolStatus.Warning, battery.Status);
        Assert.Contains("код 3", battery.Summary);
    }

    [Fact]
    public void Maintenance_wake_state_prefers_policy_over_setting()
    {
        Assert.Equal("Пробуждение для обслуживания: запрещено (политика); задача «Maintenance Configurator» включена.",
            ToolsViewModel.DescribeMaintenanceWake(taskExists: true, taskEnabled: true, policy: 0, setting: 1));
        Assert.Equal("Пробуждение для обслуживания: разрешено (политика); задача «Maintenance Configurator» отключена.",
            ToolsViewModel.DescribeMaintenanceWake(true, false, policy: 1, setting: 0));
        Assert.Equal("Пробуждение для обслуживания: разрешено (настройка); задача «Maintenance Configurator» включена.",
            ToolsViewModel.DescribeMaintenanceWake(true, true, policy: null, setting: 1));
        Assert.Equal("Пробуждение для обслуживания: запрещено (настройка); задача «Maintenance Configurator» не найдена.",
            ToolsViewModel.DescribeMaintenanceWake(false, false, null, 0));
        Assert.StartsWith("Пробуждение для обслуживания: разрешено (значение не задано",
            ToolsViewModel.DescribeMaintenanceWake(true, true, null, null));
    }

    [Fact]
    public void Command_palette_indexes_the_new_diagnostics_cards()
    {
        var sections = ToolsViewModel.BuildSections([], "C:", hasBattery: false, modernStandby: false);
        var index = CommandPaletteIndex.Build([], [], sections, [], []);
        var tools = index.Where(e => e.Kind == PaletteKind.Tool).ToList();
        Assert.Contains(tools, e => e.Title == "СОН И ПРОБУЖДЕНИЕ" && e.Keywords.Contains("/lastwake") && e.Keywords.Contains("ДИАГНОСТИКА"));
        Assert.Contains(tools, e => e.Title == "ОТЧЁТЫ О ПИТАНИИ И СНЕ" && e.Keywords.Contains("/energy"));
        Assert.DoesNotContain(tools, e => e.Title == "ОТЧЁТ О БАТАРЕЕ");
        Assert.Equal("СОН И ПРОБУЖДЕНИЕ", CommandPaletteSearch.Search(index, "lastwake")[0].Title);
        Assert.Equal("ОТЧЁТЫ О ПИТАНИИ И СНЕ", CommandPaletteSearch.Search(index, "sleepstudy")[0].Title);
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
        Assert.Equal(d.ServicesExpertMode, s.ServicesExpertMode);
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

    // ─────────────────────────── local report opening ───────────────────────────

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\battery-report.html", true)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\sub\r.HTM", true)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\..\evil.html", false)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\LogsX\r.html", false)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\run.exe", false)]
    [InlineData(@"C:\Users\u\AppData\Local\MONOLITH\Logs\r.html.lnk", false)]
    [InlineData(@"Logs\r.html", false)]
    [InlineData(@"\\server\share\r.html", false)]
    [InlineData("", false)]
    public void Only_html_reports_inside_the_logs_folder_may_be_opened(string path, bool allowed) =>
        Assert.Equal(allowed, UnelevatedShell.IsAllowedLocalReport(path, @"C:\Users\u\AppData\Local\MONOLITH\Logs"));
}
