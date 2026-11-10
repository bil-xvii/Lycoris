using System.IO;
using System.ServiceProcess;
using Lycoris.Core;
using Lycoris.Modules.Tools;

namespace Lycoris.Tests;

/// <summary>
/// «Сброс Центра обновления»: plan, checks and the step sequence against a fake system. No real service is stopped and no
/// real folder is renamed here.
/// </summary>
public sealed class WuResetTests
{
    private const string Win = @"C:\Windows";
    private static readonly DateTime Now = new(2026, 11, 10, 14, 5, 9);

    private sealed class FakeSystem : IWuResetSystem
    {
        public readonly List<string> Calls = [];
        public readonly HashSet<string> Dirs = new(StringComparer.OrdinalIgnoreCase) { $@"{Win}\SoftwareDistribution", $@"{Win}\System32\catroot2" };
        public readonly Dictionary<string, string> StopErrors = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> StartErrors = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> RenameErrors = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, WuServiceState> States = new(StringComparer.OrdinalIgnoreCase);
        public bool Servicing;

        public WuServiceState Query(string name) =>
            States.TryGetValue(name, out var s) ? s : new WuServiceState(name, true, ServiceStartMode.Manual, ServiceControllerStatus.Running);
        public bool IsRunning(string name) => name == WuReset.ServicingService && Servicing;

        public Task<string?> StopAsync(string name)
        {
            Calls.Add("stop " + name);
            return Task.FromResult(StopErrors.TryGetValue(name, out var e) ? e : null);
        }

        public Task<string?> StartAsync(string name)
        {
            Calls.Add("start " + name);
            return Task.FromResult(StartErrors.TryGetValue(name, out var e) ? e : null);
        }

        public bool DirectoryExists(string path) => Dirs.Contains(path);

        public string? Rename(string source, string target)
        {
            Calls.Add($"ren {Path.GetFileName(source)} {Path.GetFileName(target)}");
            if (RenameErrors.TryGetValue(Path.GetFileName(source), out var e)) return e;
            Dirs.Remove(source);
            Dirs.Add(target);
            return null;
        }
    }

    private static IReadOnlyList<WuFolder> Plan(FakeSystem sys) => WuReset.PlanFolders(Win, Now, sys.DirectoryExists);

    [Fact]
    public void Plan_renames_both_documented_folders_with_a_timestamp_in_the_same_parent()
    {
        var plan = WuReset.PlanFolders(Win, Now, _ => false);
        Assert.Equal(2, plan.Count);
        Assert.Equal($@"{Win}\SoftwareDistribution", plan[0].Source);
        Assert.Equal($@"{Win}\SoftwareDistribution.bak-20261110-140509", plan[0].Target);
        Assert.Equal($@"{Win}\System32\catroot2", plan[1].Source);
        Assert.Equal($@"{Win}\System32\catroot2.bak-20261110-140509", plan[1].Target);
        Assert.All(plan, f => Assert.Equal(Path.GetDirectoryName(f.Source), Path.GetDirectoryName(f.Target)));
    }

    [Fact]
    public void Plan_never_reuses_an_existing_backup_name()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $@"{Win}\SoftwareDistribution.bak-20261110-140509",
            $@"{Win}\SoftwareDistribution.bak-20261110-140509-2",
        };
        var plan = WuReset.PlanFolders(Win, Now, taken.Contains);
        Assert.Equal($@"{Win}\SoftwareDistribution.bak-20261110-140509-3", plan[0].Target);
        Assert.Equal($@"{Win}\System32\catroot2.bak-20261110-140509", plan[1].Target);
    }

    [Fact]
    public void Order_follows_Microsoft_and_touches_only_the_three_services()
    {
        Assert.Equal(["wuauserv", "BITS", "CryptSvc"], WuReset.StopOrder);
        Assert.Equal(["CryptSvc", "BITS", "wuauserv"], WuReset.StartOrder);
        Assert.True(WuReset.IsResetService("cryptsvc"));
        Assert.False(WuReset.IsResetService("msiserver"));
        Assert.False(WuReset.IsResetService("TrustedInstaller"));
        // These services stay protected: the reset stops/starts them, it never changes their start type.
        Assert.All(WuReset.StopOrder, s => Assert.True(ServiceControl.IsProtected(s)));
    }

    [Fact]
    public async Task Happy_path_stops_renames_starts_in_order()
    {
        var sys = new FakeSystem();
        var lines = new List<string>();
        var report = await WuReset.RunAsync(sys, Plan(sys), lines.Add);
        Assert.Equal(
        [
            "stop wuauserv", "stop BITS", "stop CryptSvc",
            "ren SoftwareDistribution SoftwareDistribution.bak-20261110-140509",
            "ren catroot2 catroot2.bak-20261110-140509",
            "start CryptSvc", "start BITS", "start wuauserv",
        ], sys.Calls);
        Assert.Equal(2, report.Renamed.Count);
        Assert.NotEmpty(lines);
        var outcome = WuReset.Classify(report);
        Assert.Equal(ToolStatus.Success, outcome.Status);
        Assert.Contains("SoftwareDistribution.bak-20261110-140509", outcome.Summary);
        Assert.Contains("catroot2.bak-20261110-140509", outcome.Summary);
        Assert.Contains("больше времени", outcome.Summary);
    }

    [Fact]
    public async Task A_service_that_does_not_stop_leaves_the_folders_and_restarts_the_services()
    {
        var sys = new FakeSystem();
        sys.StopErrors["BITS"] = "отказано в доступе";
        var report = await WuReset.RunAsync(sys, Plan(sys), _ => { });
        Assert.DoesNotContain(sys.Calls, c => c.StartsWith("ren "));
        Assert.DoesNotContain("stop CryptSvc", sys.Calls);
        Assert.Equal(["start CryptSvc", "start BITS", "start wuauserv"], sys.Calls.Where(c => c.StartsWith("start ")));
        Assert.Contains($@"{Win}\SoftwareDistribution", sys.Dirs);
        var outcome = WuReset.Classify(report);
        Assert.Equal(ToolStatus.Failed, outcome.Status);
        Assert.Contains("Папки не тронуты", outcome.Summary);
        Assert.Contains("BITS", outcome.Summary);
    }

    [Fact]
    public async Task A_locked_catroot2_is_a_partial_result_and_the_other_folder_is_still_renamed()
    {
        var sys = new FakeSystem();
        sys.RenameErrors["catroot2"] = "файл занят другим процессом";
        var report = await WuReset.RunAsync(sys, Plan(sys), _ => { });
        Assert.Single(report.Renamed);
        Assert.Single(report.RenameFailed);
        Assert.Equal(3, report.Started.Count);
        var outcome = WuReset.Classify(report);
        Assert.Equal(ToolStatus.Warning, outcome.Status);
        Assert.Contains("частично", outcome.Summary);
        Assert.Contains("SoftwareDistribution.bak-", outcome.Summary);
    }

    [Fact]
    public async Task Missing_folders_are_skipped_not_failed()
    {
        var sys = new FakeSystem();
        sys.Dirs.Clear();
        var report = await WuReset.RunAsync(sys, Plan(sys), _ => { });
        Assert.Equal(2, report.Missing.Count);
        Assert.DoesNotContain(sys.Calls, c => c.StartsWith("ren "));
        Assert.Equal(ToolStatus.Warning, WuReset.Classify(report).Status);
    }

    [Fact]
    public async Task A_service_that_does_not_start_again_is_reported()
    {
        var sys = new FakeSystem();
        sys.StartErrors["wuauserv"] = "не ответила за 60 секунд";
        var report = await WuReset.RunAsync(sys, Plan(sys), _ => { });
        var outcome = WuReset.Classify(report);
        Assert.Equal(ToolStatus.Warning, outcome.Status);
        Assert.Contains("перезагрузите", outcome.Summary);
        Assert.Contains("wuauserv", outcome.Summary);
    }

    [Fact]
    public void Disabled_service_blocks_the_reset_and_is_never_enabled()
    {
        var sys = new FakeSystem();
        sys.States["wuauserv"] = new WuServiceState("wuauserv", true, ServiceStartMode.Disabled, ServiceControllerStatus.Stopped);
        var states = WuReset.StopOrder.Select(sys.Query).ToList();
        var blocker = WuReset.Blocker(states, servicingRunning: false);
        Assert.NotNull(blocker);
        Assert.Contains("wuauserv", blocker);
        Assert.Contains("не включает", blocker);
        Assert.Contains("services.msc", blocker);
        Assert.Empty(sys.Calls);
    }

    [Fact]
    public void Missing_service_and_running_servicing_block_the_reset()
    {
        var ok = WuReset.StopOrder.Select(n => new WuServiceState(n, true, ServiceStartMode.Manual, ServiceControllerStatus.Stopped)).ToList();
        Assert.Null(WuReset.Blocker(ok, servicingRunning: false));
        var busy = new FakeSystem { Servicing = true };
        Assert.Contains("устанавливает обновления", WuReset.Blocker(ok, busy.IsRunning(WuReset.ServicingService)));

        var missing = ok.Select(s => s.Name == "BITS" ? s with { Exists = false, StartMode = null } : s).ToList();
        Assert.Contains("BITS", WuReset.Blocker(missing, servicingRunning: false));
    }

    [Fact]
    public void Card_sits_in_the_integrity_section_and_needs_admin()
    {
        var sections = ToolsViewModel.BuildSections([], "C:", hasBattery: false, modernStandby: false);
        var integrity = sections.Single(s => s.Title == "ЦЕЛОСТНОСТЬ WINDOWS");
        Assert.Equal(3, integrity.Cards.Count);
        var card = integrity.Cards.Single(c => c.Title == "СБРОС ЦЕНТРА ОБНОВЛЕНИЯ");
        Assert.True(card.RequiresAdmin);
        var action = Assert.Single(card.Actions);
        Assert.Equal(ToolOp.WuReset, action.Op);
        Assert.True(action.RequiresAdmin);
        Assert.Equal("WuReset", ToolsViewModel.ActionId(action));
    }

    [Fact]
    public void English_texts_exist()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("WINDOWS UPDATE RESET", Loc.T("СБРОС ЦЕНТРА ОБНОВЛЕНИЯ"));
        Assert.Equal("Windows Update (wuauserv)", WuReset.DisplayName("wuauserv"));
        Assert.Equal("Windows Update components reset", Loc.T(WuReset.JournalTitle));
        Assert.DoesNotMatch("[А-Яа-яЁё]", Loc.T(WuReset.JournalNote));
    }
}
