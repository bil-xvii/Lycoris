using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Shell;

namespace Lycoris.Tests;

public sealed class LaunchpadTileTextTests
{
    [Fact]
    public void Game_tile_starts_idle_with_a_white_start_and_stops_when_active()
    {
        var idle = LaunchpadLogic.Game(false);
        Assert.Equal("Выключен", idle.Value);
        Assert.Equal("СТАРТ", idle.Button);
        Assert.True(idle.Primary);

        var active = LaunchpadLogic.Game(true);
        Assert.Equal("Идёт", active.Value);
        Assert.Equal("СТОП", active.Button);
        Assert.False(active.Primary); // white only for the action that begins something
    }

    [Fact]
    public void Optimize_tile_counts_changes_of_the_recommended_profile()
    {
        var some = LaunchpadLogic.Optimize(2, 26);
        Assert.Equal("2 к изменению", some.Value);
        Assert.Equal("ПРИМЕНИТЬ · 2", some.Button);
        Assert.Contains("26 выбрано", some.Detail);

        var none = LaunchpadLogic.Optimize(0, 26);
        Assert.Equal("Всё применено", none.Value);
        Assert.DoesNotContain("ПРИМЕНИТЬ", none.Button); // nothing to apply: the button opens the list instead

        var unknown = LaunchpadLogic.Optimize(null, 0);
        Assert.Equal("Проверка…", unknown.Value);
    }

    [Theory]
    [InlineData("654,8", 654.8)]
    [InlineData("4,7", 4.7)]
    [InlineData("0", 0.0)]
    public void Amounts_parse_with_the_russian_decimal_comma(string text, double expected) =>
        Assert.Equal(expected, LaunchpadLogic.ParseAmount(text)!.Value, 3);

    [Theory]
    [InlineData("—")]
    [InlineData("")]
    [InlineData(null)]
    public void No_amount_is_null(string? text) => Assert.Null(LaunchpadLogic.ParseAmount(text));

    [Fact]
    public void Clean_tile_shows_what_will_be_deleted_and_what_was_found()
    {
        var s = LaunchpadLogic.Clean(false, "654,8", "МБ", "4,7", "ГБ");
        Assert.Equal("654,8 МБ", s.Value);
        Assert.Equal("ОЧИСТИТЬ · 654,8 МБ", s.Button);
        Assert.Contains("4,7 ГБ", s.Detail);
        Assert.Equal("654,8 МБ", LaunchpadLogic.CleanSize("654,8", "МБ"));
    }

    [Fact]
    public void Clean_tile_runs_the_read_only_analysis_until_something_is_known()
    {
        var notYet = LaunchpadLogic.Clean(false, "—", "", "—", "");
        Assert.Equal("ПРОВЕРИТЬ", notYet.Button);
        Assert.Null(LaunchpadLogic.CleanSize("—", ""));

        var working = LaunchpadLogic.Clean(true, "—", "", "—", "");
        Assert.Equal("Считаем…", working.Value);
        Assert.Contains("ничего не удаляет", working.Detail);

        var empty = LaunchpadLogic.Clean(false, "0", "Б", "1,2", "МБ");
        Assert.Equal("Нечего чистить", empty.Value);
        Assert.DoesNotContain("ОЧИСТИТЬ", empty.Button);
        Assert.Null(LaunchpadLogic.CleanSize("0", "Б"));
    }

    [Fact]
    public void Gpu_tile_offers_the_check_then_the_download()
    {
        var idle = LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.Unchecked, "621.01", "RTX 5070 Ti", new DateTime(2026, 10, 19), null, null);
        Assert.Equal("621.01", idle.Value);
        Assert.Equal("ПРОВЕРИТЬ ВЕРСИЮ", idle.Button);
        Assert.Contains("19.10.2026", idle.Detail);

        var update = LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.UpdateReady, "621.01", "RTX 5070 Ti", null, "622.05", DateTime.Now);
        Assert.Equal("СКАЧАТЬ И УСТАНОВИТЬ 622.05", update.Button);
        Assert.Equal("622.05", update.Value);
        Assert.Contains("621.01", update.Detail);
        Assert.True(update.Primary);

        var current = LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.UpToDate, "621.01", null, null, null, new DateTime(2026, 11, 2, 15, 40, 0));
        Assert.Contains("последняя", current.Detail);
        Assert.Contains("15:40", current.Detail);
        Assert.Equal("ПРОВЕРИТЬ СНОВА", current.Button);

        // «Update ready» without a version never shows a download button that names nothing.
        Assert.Equal("ПРОВЕРИТЬ ВЕРСИЮ", LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.UpdateReady, "1", null, null, null, null).Button);
    }

    [Fact]
    public void Undo_tile_names_the_last_batch_and_opens_restore_when_there_is_none()
    {
        var some = LaunchpadLogic.Undo(71, "Оптимизация: профиль «Рекомендуемый»", new DateTime(2026, 11, 1, 13, 12, 0));
        Assert.Equal("01.11 13:12", some.Value);
        Assert.Contains("Рекомендуемый", some.Detail);
        Assert.Contains("71", some.Detail);
        Assert.Equal("ОТКАТИТЬ ПОСЛЕДНЕЕ", some.Button);

        var none = LaunchpadLogic.Undo(0, null, null);
        Assert.Equal("ОТКРЫТЬ ВОССТАНОВЛЕНИЕ", none.Button);
        // Changes exist but none is bulk-revertible: still no «revert» button that would find nothing.
        Assert.Equal("ОТКРЫТЬ ВОССТАНОВЛЕНИЕ", LaunchpadLogic.Undo(3, null, null).Button);
    }

    [Fact]
    public void Ping_tile_measures_and_remembers_the_last_result()
    {
        Assert.Equal("не измерен", LaunchpadLogic.Ping(null).Value);
        Assert.Equal("ЗАМЕРИТЬ", LaunchpadLogic.Ping("  ").Button);
        var done = LaunchpadLogic.Ping("12 мс");
        Assert.Equal("12 мс", done.Value);
        Assert.Equal("ЗАМЕРИТЬ СНОВА", done.Button);
    }

    [Theory]
    [InlineData(2, 0, CheckFilter.Warning)]
    [InlineData(1, 5, CheckFilter.Warning)]
    [InlineData(0, 2, CheckFilter.Info)]
    [InlineData(0, 0, CheckFilter.Ok)]
    public void The_list_opens_on_the_worst_category_that_has_items(int warnings, int infos, CheckFilter expected) =>
        Assert.Equal(expected, LaunchpadLogic.DefaultFilter(warnings, infos));

    [Fact]
    public void Chip_counts_badge_and_fix_all_text()
    {
        Assert.Equal("ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ · 3", LaunchpadLogic.FixSafeText(3));
        Assert.Equal("2", LaunchpadLogic.Badge(0, 2));
        Assert.Equal("3", LaunchpadLogic.Badge(1, 2));
        Assert.Null(LaunchpadLogic.Badge(0, 0));
        foreach (var f in Enum.GetValues<CheckFilter>()) Assert.NotEmpty(LaunchpadLogic.EmptyText(f));
    }

    [Fact]
    public void Every_tile_names_its_action_in_words_and_never_ends_with_a_bare_icon()
    {
        var states = new[]
        {
            LaunchpadLogic.Game(false), LaunchpadLogic.Game(true), LaunchpadLogic.Optimize(null, 0), LaunchpadLogic.Optimize(0, 5), LaunchpadLogic.Optimize(3, 5),
            LaunchpadLogic.Clean(false, "—", "", "—", ""), LaunchpadLogic.Clean(true, "—", "", "—", ""), LaunchpadLogic.Clean(false, "1", "МБ", "2", "МБ"),
            LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.Unchecked, "", null, null, null, null), LaunchpadLogic.Gpu(LaunchpadLogic.GpuState.Checking, "1", null, null, null, null),
            LaunchpadLogic.Undo(0, null, null), LaunchpadLogic.Undo(1, "x", DateTime.Now), LaunchpadLogic.Ping(null),
        };
        foreach (var s in states)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Button));
            Assert.Equal(s.Button, s.Button.ToUpperInvariant()); // buttons are UPPER CASE labels
            Assert.False(string.IsNullOrWhiteSpace(s.Value));
        }
    }
}

public sealed class DashboardLaunchpadViewModelTests
{
    [Fact]
    public void Six_tiles_in_the_approved_order_each_with_a_command()
    {
        var vm = new DashboardViewModel();
        Assert.Equal(["game", "optimize", "clean", "gpu", "undo", "ping"], vm.Tiles.Select(t => t.Key));
        Assert.All(vm.Tiles, t =>
        {
            Assert.NotNull(t.Command);
            Assert.False(string.IsNullOrWhiteSpace(t.Label));
        });
    }

    [Fact]
    public void Chips_select_one_list_and_a_click_is_remembered()
    {
        var vm = new DashboardViewModel();
        vm.ShowInfos = true;
        Assert.Equal(CheckFilter.Info, vm.Filter);
        Assert.True(vm.ShowInfos);
        Assert.False(vm.ShowWarnings);
        vm.ShowOk = true;
        Assert.Equal(CheckFilter.Ok, vm.Filter);
        vm.ShowWarnings = false; // unchecking another radio never changes the choice
        Assert.Equal(CheckFilter.Ok, vm.Filter);
    }

    [Theory]
    [InlineData("ToggleGame")]
    [InlineData("ApplyRecommended")]
    [InlineData("CleanJunk")]
    [InlineData("GpuAction")]
    [InlineData("GpuDownload")]
    [InlineData("RevertLast")]
    [InlineData("MeasurePing")]
    [InlineData("FixSafe")]
    [InlineData("RunCheck")]
    public void Admin_intents_of_the_dashboard_find_their_commands(string command) =>
        Assert.NotNull(new AdminIntent("dashboard", command).FindCommand(new DashboardViewModel()));

    [Fact]
    public void Fix_all_counts_only_buttons_that_apply_a_tweak()
    {
        var run = (CheckItemViewModel _) => Task.CompletedTask;
        CheckItemViewModel Item(CheckSeverity sev, CheckAction? action) => new(new HealthCheckResult("x", "T", "D", sev, 5, action), run);

        Assert.True(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Warning, new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", "game-mode"))));
        Assert.True(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Info, new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", "trim-on"))));
        Assert.False(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Ok, new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", "trim-on"))));
        Assert.False(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Warning, new CheckAction(CheckActionKind.Navigate, "ОЧИСТИТЬ ДИСК", "cleaner"))));
        Assert.False(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Warning, new CheckAction(CheckActionKind.FocusTweak, "ПОКАЗАТЬ", "x"))));
        Assert.False(DashboardViewModel.IsSafeFix(Item(CheckSeverity.Warning, null)));
    }

    [Fact]
    public void A_button_is_never_disabled_for_missing_admin_rights_and_says_why_in_text()
    {
        var item = new CheckItemViewModel(
            new HealthCheckResult("game-mode", "T", "D", CheckSeverity.Warning, 5, new CheckAction(CheckActionKind.ApplyTweak, "ВКЛЮЧИТЬ", "game-mode")),
            _ => Task.CompletedTask);
        Assert.True(item.ActionNeedsAdmin);
        Assert.True(item.ActionAvailable); // the click offers the relaunch with admin rights and continues by itself
        Assert.Equal("СОВЕТ", new CheckItemViewModel(new HealthCheckResult("i", "T", "D", CheckSeverity.Info, 1), _ => Task.CompletedTask).SeverityText);
    }

    [Fact]
    public void Cleanup_row_button_takes_the_live_size_text()
    {
        string? size = null;
        var result = new HealthCheckResult("disk-space", "T", "D", CheckSeverity.Warning, 15, new CheckAction(CheckActionKind.Navigate, "ОЧИСТИТЬ ДИСК", "cleaner"));
        var item = new CheckItemViewModel(result, _ => Task.CompletedTask, a => a.Target == "cleaner" && size is not null ? $"ОЧИСТИТЬ {size}" : null);
        Assert.Equal("ОЧИСТИТЬ ДИСК", item.ActionText);
        size = "654,8 МБ";
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.RefreshLabels();
        Assert.Equal("ОЧИСТИТЬ 654,8 МБ", item.ActionText);
        Assert.Contains("ActionText", changed);
    }

    [Fact]
    public void Problem_buttons_are_verbs_and_jump_to_the_exact_place()
    {
        var disk = HealthRules.CheckDiskSpace(new SystemSnapshot { SystemDriveTotal = 500UL << 30, SystemDriveFree = 20UL << 30 });
        Assert.Equal("ОЧИСТИТЬ ДИСК", disk!.Action!.Label);
        Assert.Equal("cleaner", disk.Action.Target);

        var devices = HealthRules.CheckDevices(new SystemSnapshot { ProblemDevices = [new ProblemDevice("Net", 28)] });
        Assert.Equal("drivers", devices!.Action!.Target);
        Assert.Equal("tab:problems", devices.Action.Focus);
        Assert.Equal("ИСПРАВИТЬ ДРАЙВЕРЫ", devices.Action.Label);

        Assert.Equal("ВКЛЮЧИТЬ ЗАЩИТУ", HealthRules.CheckRestore(new SystemSnapshot { RestoreEnabled = false })!.Action!.Label);
    }
}
