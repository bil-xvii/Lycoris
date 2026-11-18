using System.Reflection;
using Lycoris.Core;
using Lycoris.Modules.Apps;

namespace Lycoris.Tests;

public sealed class AppsDetectionReliabilityTests
{
    private static AppItemViewModel Item(string id = "Valve.Steam") => new(AppCatalog.Find(id)!) { WingetReady = true };
    private static InstalledSnapshot Snapshot(bool export = false, bool store = false, bool cache = false,
        Dictionary<string, string>? packages = null) => new(packages ?? new(StringComparer.OrdinalIgnoreCase), new DateTime(2026, 11, 18, 10, 30, 0), cache)
        { ExportComplete = export, StoreComplete = store };

    [Fact]
    public void Cached_presence_is_dated_and_never_authorizes_an_update_or_recommendation()
    {
        var item = Item();
        var cache = Snapshot(cache: true, packages: new() { [item.PackageId] = "1.0" });
        AppsViewModel.ApplyInstalledState(item, cache);
        Assert.True(item.IsInstalled);
        Assert.False(item.IsKnown);
        Assert.False(item.CanUpgrade);
        Assert.False(item.CanRunAction);
        Assert.False(item.CanSelect);
        Assert.Equal(cache.CheckedAt, item.LastSeenAt);
        Assert.Contains("18.11 10:30", item.DisplayTags[0].ToolTip);
        Assert.Equal("РАНЕЕ НАЙДЕНО", item.DisplayTags[0].Text);
    }

    [Fact]
    public void Cache_missing_entry_does_not_prove_absence_or_enable_a_suggestion()
    {
        var item = Item();
        item.Advice = new(AdviceLevel.Required, "Test reason");
        AppsViewModel.ApplyInstalledState(item, Snapshot(cache: true));
        Assert.False(item.IsKnown);
        Assert.False(item.CanRunAction);
        Assert.False(item.IsSuggested);
        Assert.Null(AppsViewModel.BadgeFor([item]));
    }

    [Fact]
    public void Failed_export_retains_presence_but_revokes_selection_and_update_permission()
    {
        var item = Item();
        AppsViewModel.ApplyInstalledState(item, Snapshot(export: true, packages: new() { [item.PackageId] = "1.0" }));
        Assert.True(item.CanUpgrade);
        item.IsSelected = true;
        AppsViewModel.ApplyInstalledState(item, Snapshot());
        Assert.Equal("1.0", item.InstalledVersion);
        Assert.False(item.IsKnown);
        Assert.False(item.CanUpgrade);
        Assert.False(item.IsSelected);
    }

    [Fact]
    public void Successful_fresh_empty_export_can_replace_old_non_store_presence()
    {
        var item = Item();
        item.InstalledVersion = "1.0";
        AppsViewModel.ApplyInstalledState(item, Snapshot(export: true));
        Assert.True(item.IsKnown);
        Assert.False(item.IsInstalled);
        Assert.True(item.CanRunAction);
    }

    [Fact]
    public void Export_alone_and_failed_store_probe_cannot_erase_store_presence()
    {
        var item = Item(AppAdvisor.XboxAppId);
        item.InstalledVersion = "1.0";
        AppsViewModel.ApplyInstalledState(item, Snapshot(export: true));
        Assert.Equal("1.0", item.InstalledVersion);
        Assert.False(item.IsKnown);
        Assert.False(item.CanRunAction);
        AppsViewModel.ApplyInstalledState(item, Snapshot(export: true) with { VerifiedIds = new HashSet<string> { item.PackageId } });
        Assert.True(item.IsKnown);
        Assert.False(item.IsInstalled);
    }

    [Fact]
    public void A_fresh_positive_store_check_is_known_even_when_another_check_failed()
    {
        var item = Item(AppAdvisor.XboxAppId);
        AppsViewModel.ApplyInstalledState(item, Snapshot(packages: new() { [item.PackageId] = "" }));
        Assert.True(item.IsKnown);
        Assert.True(item.IsInstalled);
    }

    [Fact]
    public void Empty_recommendations_stay_selected_and_zero_component_kit_has_no_install_action()
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        var all = AppCatalog.All.SelectMany(e => e.AllIds).ToDictionary(id => id, _ => "1.0", StringComparer.OrdinalIgnoreCase);
        vm.ApplySnapshot(Snapshot(export: true, store: true, packages: all), partial: false);
        Assert.True(vm.SelectedCategory!.IsRecommended);
        Assert.Equal(0, vm.VisibleCount);
        Assert.Equal("Подтверждённых рекомендаций нет", vm.NoResultsTitle);
        var unknown = new AppsViewModel { WingetState = WingetState.Available };
        Assert.Equal(0, unknown.KitTotal);
        Assert.False(unknown.HasKit);
        Assert.Equal("Подтверждённых компонентов нет", unknown.KitStatusText);
        Assert.False(unknown.InstallKitCommand.CanExecute(null));
    }

    [Fact]
    public void Cache_and_partial_results_never_claim_complete_advice_inputs()
    {
        var vm = new AppsViewModel();
        vm.ApplySnapshot(Snapshot(cache: true, packages: new() { ["Valve.Steam"] = "1.0" }), partial: false);
        var context = Context(vm);
        Assert.False(context.InstalledKnown);
        Assert.False(context.StoreKnown);
        Assert.Empty(context.InstalledIds);
        Assert.False(vm.HasDetection);
        vm.ApplySnapshot(Snapshot(export: true), partial: true);
        Assert.True(Context(vm).InstalledKnown);
        Assert.False(Context(vm).StoreKnown);
        Assert.False(vm.HasDetection);
        vm.ApplySnapshot(Snapshot(), partial: false);
        Assert.Equal("Проверка неполная — рекомендации ограничены", vm.DetectionText);
        Assert.Equal("Проверка не завершена", vm.NoResultsTitle);
        Assert.False(Context(vm).InstalledKnown);
    }

    [Fact]
    public void Recheck_blocks_every_modifying_button_and_selection_but_keeps_website_links_available()
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        vm.ApplySnapshot(Snapshot(export: true, store: true, packages: new() { ["Valve.Steam"] = "1.0" }), partial: false);
        var installed = VmItem(vm, "Valve.Steam");
        installed.IsSelected = true;
        Assert.True(vm.UpdateAllCommand.CanExecute(null));
        vm.SetDetecting(true);
        Assert.Equal("1.0", installed.InstalledVersion);
        Assert.False(installed.CanRunAction);
        Assert.False(installed.CanSelect);
        Assert.False(installed.IsSelected);
        Assert.False(vm.UpdateAllCommand.CanExecute(null));
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
        Assert.False(vm.InstallKitCommand.CanExecute(null));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.All(vm.Groups.SelectMany(g => g.Items).Where(i => i.Action is AppAction.Install or AppAction.Upgrade or AppAction.VendorDownload),
            i => Assert.False(i.CanRunAction));
        var website = vm.Groups.SelectMany(g => g.Items).First(i => i.Action == AppAction.Website);
        Assert.True(website.CanRunAction);
        var vendor = vm.Groups.SelectMany(g => g.Items).First(i => i.Entry.VendorInstaller is not null);
        var fallback = new QueueItemViewModel(vendor, QueueOperation.Install)
            { State = QueueState.Failed, Fallback = QueueFallback.VendorDownload };
        Assert.False(vm.RunFallbackCommand.CanExecute(fallback));
        fallback.Fallback = QueueFallback.Website;
        Assert.True(vm.RunFallbackCommand.CanExecute(fallback));
        vm.SetDetecting(false);
        Assert.True(installed.CanRunAction);
        Assert.True(vm.UpdateAllCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("version")]
    [InlineData("removed")]
    [InlineData("winget")]
    [InlineData("checking")]
    [InlineData("rechecked")]
    public async Task Pending_confirmation_cannot_start_any_part_of_a_stale_update_plan(string transition)
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        var packages = new Dictionary<string, string> { ["Valve.Steam"] = "1.0", ["7zip.7zip"] = "1.0" };
        vm.ApplySnapshot(Snapshot(export: true, store: true, packages: packages), partial: false);
        var first = VmItem(vm, "Valve.Steam");
        var second = VmItem(vm, "7zip.7zip");
        var dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var opened = false;
        var executions = 0;
        var pending = vm.StartQueueAsync([(first, QueueOperation.Upgrade), (second, QueueOperation.Upgrade)],
            AppsViewModel.QueueKind.UpdateAll,
            confirmation: () => { opened = true; return dialog.Task; },
            runConfirmed: () => { executions++; return Task.CompletedTask; });
        Assert.True(opened);
        Assert.False(pending.IsCompleted);

        switch (transition)
        {
            case "failed":
                vm.SetDetecting(true);
                vm.ApplySnapshot(Snapshot(), partial: false);
                vm.SetDetecting(false);
                break;
            case "version": second.InstalledVersion = "2.0"; break;
            case "removed": second.InstalledVersion = null; break;
            case "winget": vm.WingetState = WingetState.Missing; break;
            case "checking": vm.SetDetecting(true); break;
            case "rechecked":
                vm.SetDetecting(true);
                vm.ApplySnapshot(Snapshot(export: true, store: true, packages: packages), partial: false);
                vm.SetDetecting(false);
                break;
        }
        dialog.SetResult(true);
        await pending;
        Assert.Equal(0, executions);
        Assert.Empty(vm.Queue);
        Assert.False(vm.HasQueue);
        Assert.Null(first.Job);
        Assert.Null(second.Job);
    }

    [Fact]
    public async Task Unchanged_fresh_plan_can_continue_after_confirmation_without_real_installers_in_the_test()
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        vm.ApplySnapshot(Snapshot(export: true, store: true, packages: new() { ["Valve.Steam"] = "1.0" }), partial: false);
        var item = VmItem(vm, "Valve.Steam");
        var dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var pending = vm.StartQueueAsync([(item, QueueOperation.Upgrade)], AppsViewModel.QueueKind.UpdateAll,
            confirmation: () => dialog.Task,
            runConfirmed: () => { executions++; return Task.CompletedTask; });
        Assert.False(pending.IsCompleted);
        dialog.SetResult(true);
        await pending;
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task An_install_plan_does_not_turn_into_an_update_when_state_changes_in_the_dialog()
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        vm.ApplySnapshot(Snapshot(export: true, store: true), partial: false);
        var item = VmItem(vm, "Valve.Steam");
        var dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var pending = vm.StartQueueAsync([(item, QueueOperation.Install)], AppsViewModel.QueueKind.Single,
            confirmation: () => dialog.Task,
            runConfirmed: () => { executions++; return Task.CompletedTask; });
        item.InstalledVersion = "1.0";
        dialog.SetResult(true);
        await pending;
        Assert.Equal(0, executions);
        Assert.Empty(vm.Queue);
    }

    [Fact]
    public async Task A_package_variant_change_requires_a_new_confirmation()
    {
        var vm = new AppsViewModel { WingetState = WingetState.Available };
        vm.ApplySnapshot(Snapshot(export: true, store: true), partial: false);
        var item = VmItem(vm, "RiotGames.Valorant.EU");
        var dialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        var pending = vm.StartQueueAsync([(item, QueueOperation.Install)], AppsViewModel.QueueKind.Single,
            confirmation: () => dialog.Task,
            runConfirmed: () => { executions++; return Task.CompletedTask; });
        Assert.False(pending.IsCompleted);
        item.SelectedVariant = item.Variants.Last();
        dialog.SetResult(true);
        await pending;
        Assert.Equal(0, executions);
        Assert.Empty(vm.Queue);
    }

    private static AppItemViewModel VmItem(AppsViewModel vm, string id) =>
        vm.Groups.SelectMany(g => g.Items).Single(i => i.Entry.Id == id);

    private static AdviceContext Context(AppsViewModel vm) => (AdviceContext)typeof(AppsViewModel)
        .GetMethod("BuildAdviceContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
}
