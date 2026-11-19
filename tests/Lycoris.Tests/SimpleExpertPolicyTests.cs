using System.Reflection;
using System.ServiceProcess;
using Lycoris.Core;
using Lycoris.Core.Security;
using Lycoris.Modules.Cleaner;
using Lycoris.Modules.Devices;
using Lycoris.Modules.Services;
using Lycoris.Modules.Tools;
using Lycoris.Tweaks;
using Lycoris.UI;

namespace Lycoris.Tests;

[CollectionDefinition("SimpleExpertPolicy", DisableParallelization = true)]
public sealed class SimpleExpertPolicyCollection { }

[Collection("SimpleExpertPolicy")]
public sealed class SimpleExpertPolicyTests : IDisposable
{
    private readonly AppSettings _old = AppSettings.Current;
    private readonly bool? _answer = Ui.AutoAnswer;
    private static readonly PropertyInfo Settings = typeof(AppSettings).GetProperty(nameof(AppSettings.Current))!;
    private readonly AppSettings _settings = new();

    public SimpleExpertPolicyTests()
    {
        Settings.SetValue(null, _settings); // No Load/autosave; no real user preferences are written.
        Ui.AutoAnswer = false;
        Ui.AutoAnswered.Clear();
    }

    public void Dispose()
    {
        Settings.SetValue(null, _old);
        Ui.AutoAnswer = _answer;
        Ui.AutoAnswered.Clear();
    }

    [Fact]
    public async Task Advanced_device_single_command_and_execution_refuse_before_any_apply_or_admin_prompt()
    {
        using var language = Loc.UseLanguage("ru");
        var calls = 0;
        var row = new DeviceSetting
        {
            Id = "nic:*EEE", Title = "EEE", RequiresExpertMode = true, Control = SettingControl.Toggle,
            Current = DeviceValue.Of(1), Staged = DeviceValue.Of(0),
            Apply = (_, _, _) => { calls++; throw new InvalidOperationException("Must not execute"); },
        };
        var vm = new DevicesViewModel();
        await vm.ApplySettingCommand.ExecuteAsync(row);
        var result = await DeviceApply.ApplyOneAsync(row, DeviceValue.Of(0), CancellationToken.None);
        Assert.Equal(ApplyOutcome.Blocked, result.Outcome);
        Assert.Contains("Эксперт", Assert.Single(result.Errors));
        var run = (Task<ApplyOutcome>)typeof(DeviceApply).GetMethod("RunApplyAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [null, row, DeviceValue.Of(0), CancellationToken.None])!;
        Assert.Equal(ApplyOutcome.Blocked, await run);
        Assert.Equal(0, calls);
        Assert.Empty(Ui.AutoAnswered);
        var plan = new PlanItem { Card = new DeviceCard { Key = "test", Kind = DeviceKind.Network, Title = "Test" },
            Setting = row, Kind = PlanItemKind.Setting, Selectable = true, IsSelected = true };
        var planned = await DeviceApply.ApplyPlanAsync([plan], null, CancellationToken.None);
        Assert.Equal(1, planned!.Blocked);
        Assert.Equal(0, planned.Applied);
        Assert.Empty(Ui.AutoAnswered);
    }

    [Fact]
    public async Task Full_service_commands_and_network_reset_replay_refuse_without_prompts_in_simple()
    {
        var vm = new ServicesViewModel();
        await vm.ApplyStartTypeCommand.ExecuteAsync("Spooler|Disabled|0");
        await vm.StartServiceCommand.ExecuteAsync("Spooler");
        await vm.StopServiceCommand.ExecuteAsync("Spooler");
        var tools = new ToolsViewModel();
        await tools.RunToolCommand.ExecuteAsync("NetworkReset");
        Assert.False(ToolsViewModel.CanRunInMode(ToolOp.NetworkReset, false));
        Assert.True(ToolsViewModel.CanRunInMode(ToolOp.NetworkReset, true));
        Assert.True(ToolsViewModel.CanRunInMode(ToolOp.FlushDns, false));
        Assert.True(ToolsViewModel.CanRunInMode(ToolOp.Sfc, false));
        Assert.Empty(Ui.AutoAnswered);
    }

    [Fact]
    public void Mode_downgrade_clears_advanced_device_staging_and_service_selections()
    {
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        _settings.ExpertMode = true;
        var devices = new DevicesViewModel();
        var row = new DeviceSetting { Id = "nic:*EEE", Title = "EEE", RequiresExpertMode = true,
            Staged = DeviceValue.Of(0) };
        devices.Cards.Add(new DeviceCard { Key = "test", Kind = DeviceKind.Network, Title = "Test", Settings = { row } });
        var services = new ServicesViewModel();
        services.SelectedTab = ServicesViewModel.TabAll;
        var item = new RecommendationItem(ServiceCatalog.Services.First(s => s.Name == "Spooler")) { IsSelected = true };
        var map = (Dictionary<string, RecommendationItem>)typeof(ServicesViewModel)
            .GetField("_recByName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(services)!;
        map.Add(item.Name, item);
        _settings.ExpertMode = false;
        Assert.Null(row.Staged);
        Assert.False(row.ControlsEnabled);
        Assert.False(item.IsSelected);
        Assert.NotEqual(ServicesViewModel.TabAll, services.SelectedTab);
    }

    [Fact]
    public void Expert_cleaner_rows_cannot_be_selected_in_simple_and_downgrade_clears_them()
    {
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        _settings.ExpertMode = true;
        var vm = new CleanerViewModel();
        var expert = new CleanItem(CleanCatalog.All.Single(d => d.Tier == CleanTier.Expert)) { IsChecked = true };
        ((List<CleanItem>)typeof(CleanerViewModel).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Add(expert);
        _settings.ExpertMode = false;
        Assert.False(expert.IsChecked);
        Assert.False(expert.CanToggle);
        expert.ToggleCommand.Execute(null);
        Assert.False(expert.IsChecked);
        Assert.False(CleanerViewModel.CanCleanInMode(expert, false));
        Assert.True(CleanerViewModel.CanCleanInMode(expert, true));
    }

    [Fact]
    public async Task Component_cleanup_runner_refuses_simple_before_process_or_progress_callbacks()
    {
        using var language = Loc.UseLanguage("ru");
        var outcome = await SystemCleanup.RunComponentCleanupAsync(
            (_, _) => throw new InvalidOperationException("The DISM runner must not start"), CancellationToken.None);
        Assert.Equal(0, outcome.FreedBytes);
        Assert.Null(outcome.Error);
        Assert.Equal("Доступно в режиме «Эксперт».", outcome.Note);
    }

    [Fact]
    public async Task Advanced_catalog_recommendation_and_task_replays_refuse_before_admin_or_system_calls()
    {
        var vm = new ServicesViewModel();
        var service = new RecommendationItem(ServiceCatalog.Services.Single(s => s.Name == "Spooler"))
        {
            IsSelected = true, CanModify = true,
            State = new ServiceLiveState(ServiceStartMode.Automatic, false, false, ServiceControllerStatus.Running, "Fake"),
        };
        var map = (Dictionary<string, RecommendationItem>)typeof(ServicesViewModel)
            .GetField("_recByName", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;
        map.Add(service.Name, service);
        var task = new TaskItem(new CuratedTask { Path = @"\Lycoris-Fake\Advanced", Title = "Fake", Description = "Fake",
            Impact = "Fake", Risk = TweakRisk.Advanced }, @"\Lycoris-Fake\Advanced") { IsSelected = true, Enabled = true, CanModify = true };
        vm.TaskItems.Add(task);
        Assert.False(service.CanSelect);
        Assert.False(task.CanSelect);
        await vm.ApplyRecommendationsCommand.ExecuteAsync(service.Name);
        await vm.ApplyTasksCommand.ExecuteAsync(task.Path);
        Assert.Empty(Ui.AutoAnswered);
        using var owner = ExpertAccessPolicy.OverrideFingerprintForTests(() => ExpertAccessPolicy.OwnerFingerprint);
        _settings.ExpertMode = true;
        Assert.True(service.CanSelect);
        Assert.True(task.CanSelect);
        _settings.ExpertMode = false;
        Assert.False(service.IsSelected);
        Assert.False(task.IsSelected);
    }
}
