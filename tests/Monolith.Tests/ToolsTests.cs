using Monolith.Core;
using Monolith.Modules.Restore;
using Monolith.Modules.Settings;
using Monolith.Modules.Tools;

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
