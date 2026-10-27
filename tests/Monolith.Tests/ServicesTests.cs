using System.ServiceProcess;
using Monolith.Core;
using Monolith.Modules.Services;
using Monolith.Tweaks;

namespace Monolith.Tests;

public sealed class ServicesTests
{
    private static readonly ProfileSet[] Profiles = [ProfileSet.Normal, ProfileSet.Recommended, ProfileSet.Gaming];

    // ───────────────────────────── Curated services ─────────────────────────────

    [Fact]
    public void Curated_list_contains_no_protected_or_hard_blocked_service()
    {
        foreach (var s in ServiceCatalog.Services)
        {
            Assert.False(ServiceControl.IsProtected(s.Name), $"{s.Name} is protected by Core");
            Assert.False(ServiceGuard.IsHardBlocked(s.Name), $"{s.Name} is on the research hard-block list");
            Assert.False(ServiceGuard.IsPerUser(s.Name, 0x10), $"{s.Name} looks like a per-user service");
        }
    }

    [Fact]
    public void Curated_names_are_unique()
    {
        var dupes = ServiceCatalog.Services.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void Curated_targets_are_manual_or_disabled()
    {
        foreach (var s in ServiceCatalog.Services)
            Assert.True(s.Target is ServiceStartMode.Manual or ServiceStartMode.Disabled, $"{s.Name}: {s.Target}");
    }

    [Fact]
    public void Every_curated_entry_is_explained()
    {
        foreach (var s in ServiceCatalog.Services)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Description), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Condition), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Impact), s.Name);
            Assert.True(Enum.IsDefined(s.Group), s.Name);
        }
    }

    [Theory]
    [InlineData("DiagTrack")]
    [InlineData("MapsBroker")]
    [InlineData("TrkWks")]
    [InlineData("PcaSvc")]
    [InlineData("CDPSvc")]
    [InlineData("LanmanServer")]
    public void Auto_start_services_go_to_manual_not_disabled(string name)
    {
        var s = ServiceCatalog.Find(name);
        Assert.NotNull(s);
        Assert.Equal(ServiceStartMode.Manual, s!.Target);
    }

    [Theory]
    [InlineData("SysMain")]
    [InlineData("iphlpsvc")]
    [InlineData("edgeupdate")]
    [InlineData("edgeupdatem")]
    [InlineData("WbioSrvc")]
    [InlineData("SCardSvr")]
    [InlineData("StorSvc")]
    [InlineData("WerSvc")]
    [InlineData("lfsvc")]
    [InlineData("SharedAccess")]
    [InlineData("TermService")]
    [InlineData("TextInputManagementService")]
    [InlineData("wuauserv")]
    [InlineData("WinDefend")]
    public void Never_offered_services_are_not_curated(string name) => Assert.False(ServiceCatalog.IsCurated(name));

    [Fact]
    public void Conditional_entries_are_never_preselected()
    {
        foreach (var s in ServiceCatalog.Services.Where(s => s.Conditional))
            foreach (var p in Profiles)
                Assert.False(s.IsPreselectedIn(p), $"{s.Name} pre-selected in {p}");
    }

    [Fact]
    public void Expert_entries_are_always_conditional()
    {
        foreach (var s in ServiceCatalog.Services.Where(s => s.Risk == TweakRisk.Advanced))
            Assert.True(s.Conditional, s.Name);
    }

    [Fact]
    public void Xbox_entries_are_opt_in_only()
    {
        var xbox = ServiceCatalog.Services.Where(s => s.Group == ServiceGroup.Xbox).ToList();
        Assert.NotEmpty(xbox);
        foreach (var s in xbox)
        {
            Assert.True(s.Conditional, s.Name);
            Assert.Equal(ProfileSet.None, s.Profiles);
        }
        Assert.NotNull(ServiceCatalog.GroupNote(ServiceGroup.Xbox));
    }

    [Theory]
    [InlineData("bthserv")]
    [InlineData("BTAGService")]
    [InlineData("BthAvctpSvc")]
    public void Bluetooth_entries_require_no_adapter(string name) =>
        Assert.Equal(ServiceGate.NoBluetoothAdapter, ServiceCatalog.Find(name)!.Gate);

    [Fact]
    public void Companions_point_to_curated_entries()
    {
        foreach (var s in ServiceCatalog.Services.Where(s => s.Companion is not null))
        {
            Assert.True(ServiceCatalog.IsCurated(s.Companion!), $"{s.Name} → {s.Companion}");
            Assert.True(s.Conditional, s.Name);
        }
    }

    [Fact]
    public void Profiles_are_nested_normal_recommended_gaming()
    {
        foreach (var s in ServiceCatalog.Services)
        {
            if (s.IsPreselectedIn(ProfileSet.Normal)) Assert.True(s.IsPreselectedIn(ProfileSet.Recommended), s.Name);
            if (s.IsPreselectedIn(ProfileSet.Recommended)) Assert.True(s.IsPreselectedIn(ProfileSet.Gaming), s.Name);
        }
        foreach (var t in ServiceCatalog.Tasks)
        {
            if (t.IsPreselectedIn(ProfileSet.Normal)) Assert.True(t.IsPreselectedIn(ProfileSet.Recommended), t.Path);
            if (t.IsPreselectedIn(ProfileSet.Recommended)) Assert.True(t.IsPreselectedIn(ProfileSet.Gaming), t.Path);
        }
    }

    [Fact]
    public void Every_group_has_a_title()
    {
        foreach (var g in Enum.GetValues<ServiceGroup>())
            Assert.False(string.IsNullOrWhiteSpace(ServiceCatalog.GroupTitle(g)));
    }

    // ───────────────────────────── Curated tasks ─────────────────────────────

    [Fact]
    public void Task_paths_are_unique_absolute_and_explained()
    {
        Assert.Equal(ServiceCatalog.Tasks.Count, ServiceCatalog.Tasks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var t in ServiceCatalog.Tasks)
        {
            Assert.StartsWith("\\", t.Path);
            Assert.False(t.Path.EndsWith('\\'), t.Path);
            Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Path);
            Assert.False(string.IsNullOrWhiteSpace(t.Description), t.Path);
            Assert.False(string.IsNullOrWhiteSpace(t.Impact), t.Path);
        }
    }

    [Theory]
    [InlineData("Microsoft Compatibility Appraiser")]
    [InlineData("StartupAppTask")]
    [InlineData("PcaPatchDbTask")]
    [InlineData("SdbinstMergeDbTask")]
    [InlineData("ScheduledDefrag")]
    [InlineData("Flighting")]
    [InlineData("Device Information")]
    [InlineData("MicrosoftEdgeUpdate")]
    [InlineData("UpdateOrchestrator")]
    public void Never_touch_tasks_are_not_included(string fragment) =>
        Assert.DoesNotContain(ServiceCatalog.Tasks, t => t.Path.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Conditional_tasks_are_never_preselected()
    {
        foreach (var t in ServiceCatalog.Tasks.Where(t => t.Conditional))
            foreach (var p in Profiles)
                Assert.False(t.IsPreselectedIn(p), t.Path);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Maps\MapsUpdateTask", @"\Microsoft\Windows\Maps", "MapsUpdateTask")]
    [InlineData(@"\NvTmRep_CrashReport", @"\", "NvTmRep_CrashReport")]
    [InlineData("Microsoft/Office/OfficeTelemetryAgentLogOn2016", @"\Microsoft\Office", "OfficeTelemetryAgentLogOn2016")]
    public void Task_paths_split_into_folder_and_name(string path, string folder, string name) =>
        Assert.Equal((folder, name), TaskScanner.Split(path));

    // ───────────────────────────── Guard ─────────────────────────────

    [Theory]
    [InlineData("RpcSs", 0x20, 2, 1, 0, @"C:\WINDOWS\system32\svchost.exe -k rpcss -p")]
    [InlineData("CDPUserSvc_9fa35", 0xE0, 2, 1, 0, @"C:\WINDOWS\system32\svchost.exe -k UnistackSvcGroup")]
    [InlineData("SomeUserTemplate", 0x60, 3, 1, 0, @"C:\WINDOWS\system32\svchost.exe")]
    [InlineData("vgc", 0x10, 3, 1, 0, @"C:\Program Files\Riot Vanguard\vgc.exe")]
    [InlineData("EasyAntiCheat_EOS", 0x10, 3, 1, 0, @"C:\Program Files (x86)\EasyAntiCheat_EOS\EasyAntiCheat_EOS.exe")]
    [InlineData("SomeDriver", 0x01, 3, 1, 0, @"\SystemRoot\System32\drivers\some.sys")]
    [InlineData("BootThing", 0x10, 1, 1, 0, @"C:\x.exe")]
    [InlineData("PplService", 0x10, 3, 1, 2, @"C:\WINDOWS\system32\x.exe")]
    [InlineData("CriticalOne", 0x10, 3, 3, 0, @"C:\WINDOWS\system32\x.exe")]
    [InlineData("VendorSvc", 0x10, 2, 1, 0, @"C:\WINDOWS\System32\DriverStore\FileRepository\x.inf_amd64_1\svc.exe")]
    [InlineData("SysMain", 0x20, 2, 1, 0, @"C:\WINDOWS\system32\svchost.exe -k LocalSystemNetworkRestricted -p")]
    [InlineData("SCardSvr", 0x20, 3, 1, 0, @"C:\WINDOWS\system32\svchost.exe -k LocalServiceAndNoImpersonation")]
    public void Dangerous_services_are_locked(string name, int type, int start, int errorControl, int launchProtected, string imagePath) =>
        Assert.NotNull(ServiceGuard.GetLockReason(name, type, start, errorControl, launchProtected, imagePath));

    [Theory]
    [InlineData("DiagTrack", 0x10, 2, @"C:\WINDOWS\System32\svchost.exe -k utcsvc -p")]
    [InlineData("Spooler", 0x110, 2, @"C:\WINDOWS\System32\spoolsv.exe")]
    [InlineData("Razer Elevation Service", 0x10, 2, @"""C:\Program Files\Razer\razer_elevation_service\razer_elevation_service.exe""")]
    [InlineData("MyApp_Updater", 0x10, 3, @"C:\Program Files\MyApp\updater.exe")]
    public void Ordinary_services_are_not_locked(string name, int type, int start, string imagePath) =>
        Assert.Null(ServiceGuard.GetLockReason(name, type, start, 1, 0, imagePath));

    [Fact]
    public void Remote_registry_must_stay_disabled()
    {
        Assert.True(ServiceGuard.MustStayDisabled("RemoteRegistry"));
        Assert.False(ServiceGuard.MustStayDisabled("Spooler"));
    }

    // ───────────────────────────── Start-type policy (ВСЕ СЛУЖБЫ) ─────────────────────────────

    private static StartOption Option(ServiceStartMode mode, bool delayed = false) => StartOption.For(mode, delayed)!;

    [Theory]
    [InlineData("W32Time")]
    [InlineData("Eaphost")]
    [InlineData("RmSvc")]
    [InlineData("dot3svc")]
    public void Windows_services_outside_the_list_are_never_disabled(string name)
    {
        var scope = ServiceStartPolicy.ScopeFor(name, isThirdParty: false, isLocked: false);
        Assert.Equal(StartChangeScope.NoDisable, scope);
        Assert.False(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Disabled)));
        Assert.True(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Manual)));
        Assert.True(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Automatic)));
        Assert.True(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Automatic, delayed: true)));
        Assert.DoesNotContain(ServiceStartPolicy.OptionsFor(scope, ServiceStartMode.Manual, false), o => o.Mode == ServiceStartMode.Disabled);
        Assert.NotNull(ServiceStartPolicy.Note(scope, isCurated: false));
    }

    [Fact]
    public void Already_disabled_windows_service_shows_its_state_but_cannot_be_set_to_it()
    {
        var options = ServiceStartPolicy.OptionsFor(StartChangeScope.NoDisable, ServiceStartMode.Disabled, false);
        Assert.Contains(Option(ServiceStartMode.Disabled), options);
        Assert.Contains(Option(ServiceStartMode.Manual), options);
        Assert.False(ServiceStartPolicy.Allows(StartChangeScope.NoDisable, Option(ServiceStartMode.Disabled)));
    }

    [Fact]
    public void Third_party_services_may_be_disabled()
    {
        var scope = ServiceStartPolicy.ScopeFor("Razer Elevation Service", isThirdParty: true, isLocked: false);
        Assert.Equal(StartChangeScope.Full, scope);
        Assert.Equal(StartOption.All.Count, ServiceStartPolicy.OptionsFor(scope, ServiceStartMode.Automatic, false).Count);
        Assert.True(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Disabled)));
        Assert.Null(ServiceStartPolicy.Note(scope, isCurated: false));
    }

    [Fact]
    public void Curated_entries_follow_their_researched_target()
    {
        foreach (var s in ServiceCatalog.Services.Where(s => !ServiceGuard.MustStayDisabled(s.Name)))
        {
            var scope = ServiceStartPolicy.ScopeFor(s.Name, isThirdParty: false, isLocked: false);
            var disableAllowed = ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Disabled));
            Assert.Equal(s.Target == ServiceStartMode.Disabled, disableAllowed);
        }
        Assert.Equal(StartChangeScope.NoDisable, ServiceStartPolicy.ScopeFor("DiagTrack", false, false));
        Assert.NotNull(ServiceStartPolicy.Note(StartChangeScope.NoDisable, isCurated: true));
    }

    [Fact]
    public void Remote_registry_can_only_be_disabled()
    {
        var scope = ServiceStartPolicy.ScopeFor("RemoteRegistry", isThirdParty: false, isLocked: false);
        Assert.Equal(StartChangeScope.DisabledOnly, scope);
        Assert.Equal(new[] { Option(ServiceStartMode.Manual), Option(ServiceStartMode.Disabled) },
            ServiceStartPolicy.OptionsFor(scope, ServiceStartMode.Manual, false));
        Assert.False(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Manual)));
        Assert.True(ServiceStartPolicy.Allows(scope, Option(ServiceStartMode.Disabled)));
    }

    [Fact]
    public void Locked_services_allow_nothing()
    {
        var scope = ServiceStartPolicy.ScopeFor("Razer Elevation Service", isThirdParty: true, isLocked: true);
        Assert.Equal(StartChangeScope.None, scope);
        Assert.All(StartOption.All, o => Assert.False(ServiceStartPolicy.Allows(scope, o)));
        Assert.False(ServiceStartPolicy.Allows(StartChangeScope.Full, null));
    }

    private static ServiceInfo Info(string name, ServiceStartMode mode, bool thirdParty, string? lockReason = null) => new()
    {
        Name = name, DisplayName = name, StartMode = mode, IsThirdParty = thirdParty, LockReason = lockReason,
        Status = ServiceControllerStatus.Stopped,
    };

    [Fact]
    public void Service_row_cannot_apply_a_start_type_the_policy_refuses()
    {
        var item = new ServiceItem(Info("W32Time", ServiceStartMode.Manual, thirdParty: false)) { CanModify = true };
        Assert.DoesNotContain(item.StartOptions, o => o.Mode == ServiceStartMode.Disabled);
        item.PendingStart = Option(ServiceStartMode.Disabled);
        Assert.False(item.CanApplyStart);
        item.PendingStart = Option(ServiceStartMode.Automatic);
        Assert.True(item.CanApplyStart);

        var vendor = new ServiceItem(Info("VendorSvc", ServiceStartMode.Automatic, thirdParty: true)) { CanModify = true };
        vendor.PendingStart = Option(ServiceStartMode.Disabled);
        Assert.True(vendor.CanApplyStart);
    }

    [Fact]
    public void Service_row_options_follow_the_current_start_type()
    {
        var item = new ServiceItem(Info("W32Time", ServiceStartMode.Manual, thirdParty: false));
        Assert.DoesNotContain(Option(ServiceStartMode.Disabled), item.StartOptions);
        item.Apply(new ServiceLiveState(ServiceStartMode.Disabled, false, false, ServiceControllerStatus.Stopped, null));
        Assert.Contains(Option(ServiceStartMode.Disabled), item.StartOptions);
        Assert.Equal(Option(ServiceStartMode.Disabled), item.PendingStart);
        Assert.False(item.CanApplyStart);
    }

    // ───────────────────────────── Honest texts ─────────────────────────────

    [Fact]
    public void DiagTrack_is_opt_in_like_in_optimize()
    {
        var s = ServiceCatalog.Find("DiagTrack")!;
        Assert.Equal(ServiceStartMode.Manual, s.Target);
        Assert.Equal(TweakRisk.Optional, s.Risk);
        Assert.True(s.Conditional);
        foreach (var p in Profiles) Assert.False(s.IsPreselectedIn(p), p.ToString());
        Assert.Contains("Insider", s.Impact);
        Assert.DoesNotContain("последствий нет", s.Impact, StringComparison.OrdinalIgnoreCase);
    }

    private static RecommendationItem Rec(string name, ServiceStartMode? current) => new(ServiceCatalog.Find(name)!)
    {
        State = new ServiceLiveState(current, false, false, ServiceControllerStatus.Stopped, null),
    };

    [Fact]
    public void Profiles_skip_services_that_already_start_on_demand()
    {
        var manual = Rec("RetailDemo", ServiceStartMode.Manual);
        Assert.True(manual.IsAlreadyManual);
        Assert.False(manual.RecommendedIn(ProfileSet.Recommended));
        Assert.False(manual.RecommendedIn(ProfileSet.Gaming));

        var automatic = Rec("RetailDemo", ServiceStartMode.Automatic);
        Assert.False(automatic.IsAlreadyManual);
        Assert.True(automatic.RecommendedIn(ProfileSet.Recommended));

        var mapsAuto = Rec("MapsBroker", ServiceStartMode.Automatic);
        Assert.True(mapsAuto.RecommendedIn(ProfileSet.Normal));
    }

    [Fact]
    public void Remote_registry_switched_on_is_still_recommended_back_to_disabled()
    {
        var rr = Rec("RemoteRegistry", ServiceStartMode.Manual);
        Assert.False(rr.IsAlreadyManual);
        Assert.True(rr.RecommendedIn(ProfileSet.Normal));
        Assert.True(rr.IsSelectable);
    }

    [Fact]
    public void Profiles_only_preselect_manual_targets_for_services_in_their_default_state()
    {
        // On a default Windows 11 the Disabled-target entries a profile lists are already Manual (or Disabled),
        // so every profile pre-selection is «Вручную» — matching the page text.
        foreach (var s in ServiceCatalog.Services.Where(s => s.Target == ServiceStartMode.Disabled))
        {
            var item = new RecommendationItem(s) { State = new ServiceLiveState(ServiceStartMode.Manual, false, false, null, null) };
            foreach (var p in Profiles)
                if (!ServiceGuard.MustStayDisabled(s.Name))
                    Assert.False(item.RecommendedIn(p), $"{s.Name} in {p}");
        }
    }

    [Fact]
    public void Restore_point_line_matches_settings_and_protection()
    {
        Assert.StartsWith("Перед изменением создаётся точка восстановления.", ServiceText.RestorePointLine(true, true, "Журнал."));
        Assert.Contains("спросит, продолжать ли без неё", ServiceText.RestorePointLine(true, false, "Журнал."));
        Assert.Contains("отключена в настройках", ServiceText.RestorePointLine(false, true, "Журнал."));
        Assert.DoesNotContain("создаётся точка", ServiceText.RestorePointLine(false, false, "Журнал."));
        Assert.EndsWith("Журнал.", ServiceText.RestorePointLine(true, false, "Журнал."));
    }

    [Fact]
    public void Profile_descriptions_do_not_promise_telemetry_changes()
    {
        foreach (var p in Profiles)
            Assert.DoesNotContain("телеметр", ServiceCatalog.ProfileDescription(p), StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── Filters ─────────────────────────────

    private static ServiceFacts Facts(string name, bool running = false, ServiceStartMode? mode = ServiceStartMode.Manual, bool thirdParty = false,
        string display = "Служба", string description = "", string path = @"C:\WINDOWS\system32\svchost.exe") =>
        new(name, display, description, path, running, mode, thirdParty);

    [Fact]
    public void Filter_all_matches_everything()
    {
        Assert.True(ServiceFilter.Matches(Facts("a"), ServiceFilterKind.All, null));
        Assert.True(ServiceFilter.Matches(Facts("b", mode: null), ServiceFilterKind.All, ""));
    }

    [Fact]
    public void Filter_running_uses_state()
    {
        Assert.True(ServiceFilter.Matches(Facts("a", running: true), ServiceFilterKind.Running, null));
        Assert.False(ServiceFilter.Matches(Facts("a", running: false), ServiceFilterKind.Running, null));
    }

    [Fact]
    public void Filter_start_modes()
    {
        Assert.True(ServiceFilter.Matches(Facts("a", mode: ServiceStartMode.Automatic), ServiceFilterKind.Automatic, null));
        Assert.False(ServiceFilter.Matches(Facts("a", mode: ServiceStartMode.Manual), ServiceFilterKind.Automatic, null));
        Assert.True(ServiceFilter.Matches(Facts("a", mode: ServiceStartMode.Disabled), ServiceFilterKind.Disabled, null));
        Assert.False(ServiceFilter.Matches(Facts("a", mode: null), ServiceFilterKind.Disabled, null));
    }

    [Fact]
    public void Filter_third_party()
    {
        Assert.True(ServiceFilter.Matches(Facts("a", thirdParty: true), ServiceFilterKind.ThirdParty, null));
        Assert.False(ServiceFilter.Matches(Facts("a", thirdParty: false), ServiceFilterKind.ThirdParty, null));
    }

    [Fact]
    public void Search_looks_at_name_display_description_and_path_case_insensitively()
    {
        var f = Facts("Spooler", display: "Диспетчер печати", description: "Очередь заданий печати", path: @"C:\WINDOWS\System32\spoolsv.exe");
        Assert.True(ServiceFilter.Matches(f, ServiceFilterKind.All, "spooler"));
        Assert.True(ServiceFilter.Matches(f, ServiceFilterKind.All, "ДИСПЕТЧЕР"));
        Assert.True(ServiceFilter.Matches(f, ServiceFilterKind.All, "очередь"));
        Assert.True(ServiceFilter.Matches(f, ServiceFilterKind.All, "spoolsv"));
        Assert.True(ServiceFilter.Matches(f, ServiceFilterKind.All, "  печати   spool  "));
        Assert.False(ServiceFilter.Matches(f, ServiceFilterKind.All, "печати bluetooth"));
        Assert.False(ServiceFilter.Matches(f, ServiceFilterKind.Running, "spooler"));
    }

    // ───────────────────────────── Parsing / classification ─────────────────────────────

    [Theory]
    [InlineData(@"""C:\Program Files\Razer\svc.exe"" --run", @"C:\Program Files\Razer\svc.exe")]
    [InlineData(@"C:\WINDOWS\system32\svchost.exe -k netsvcs -p", @"C:\WINDOWS\system32\svchost.exe")]
    [InlineData(@"C:\Program Files\Some App\service host.exe /service", @"C:\Program Files\Some App\service host.exe")]
    [InlineData(@"\SystemRoot\System32\drivers\x.sys", @"C:\WINDOWS\System32\drivers\x.sys")]
    [InlineData(@"system32\DRIVERS\y.sys", @"C:\WINDOWS\system32\DRIVERS\y.sys")]
    [InlineData(@"\??\C:\Tools\z.exe", @"C:\Tools\z.exe")]
    [InlineData("", "")]
    public void Executable_is_extracted_from_path_name(string pathName, string expected) =>
        Assert.Equal(expected, ServicePathParser.ExtractExecutable(pathName, @"C:\WINDOWS"));

    [Fact]
    public void Environment_variables_are_expanded()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var result = ServicePathParser.ExtractExecutable(@"%SystemRoot%\System32\diagtrack.dll", windows);
        Assert.Equal(System.IO.Path.Combine(windows, @"System32\diagtrack.dll"), result, ignoreCase: true);
    }

    [Theory]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("Корпорация Майкрософт", true)]
    [InlineData("Advanced Micro Devices, Inc.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Microsoft_publisher_is_recognized(string? company, bool expected) =>
        Assert.Equal(expected, ServiceClassifier.IsMicrosoftCompany(company));

    [Theory]
    [InlineData("Microsoft Corporation", @"C:\Program Files\x.exe", false)]
    [InlineData("Razer Inc", @"C:\Program Files\Razer\x.exe", true)]
    [InlineData("", @"C:\WINDOWS\System32\x.exe", false)]
    [InlineData("", @"C:\WINDOWS\System32\DriverStore\FileRepository\nv.inf_amd64_1\NVDisplay.Container.exe", true)]
    [InlineData("", @"C:\Program Files\WindowsApps\App\svc.exe", true)]
    public void Third_party_classification(string company, string binary, bool expected) =>
        Assert.Equal(expected, ServiceClassifier.IsThirdParty(company, binary, @"C:\WINDOWS"));

    [Theory]
    [InlineData(1, 6, null, true)]
    [InlineData(1, 13, "", true)]
    [InlineData(1, 0, "MS DM Server", true)]
    [InlineData(1, 1, null, false)]
    [InlineData(1, 30, "Deploy Authority", false)]
    [InlineData(1, 28, "Local Authority", false)]
    [InlineData(0, 6, "MS DM Server", false)]
    [InlineData(null, null, null, false)]
    public void Mdm_enrollment_detection(int? state, int? type, string? provider, bool expected) =>
        Assert.Equal(expected, ManagedDetection.IsMdmEnrollment(state, type, provider));

    [Fact]
    public void Start_options_map_start_configuration()
    {
        Assert.Equal("Автоматически (отложенно)", StartOption.For(ServiceStartMode.Automatic, true)!.Label);
        Assert.Equal("Автоматически", StartOption.For(ServiceStartMode.Automatic, false)!.Label);
        Assert.Equal("Вручную", StartOption.For(ServiceStartMode.Manual, true)!.Label);
        Assert.Equal("Отключена", StartOption.For(ServiceStartMode.Disabled, false)!.Label);
        Assert.Null(StartOption.For(ServiceStartMode.Boot, false));
        Assert.Equal(4, StartOption.All.Count);
    }

    [Theory]
    [InlineData("Auto", ServiceStartMode.Automatic)]
    [InlineData("Manual", ServiceStartMode.Manual)]
    [InlineData("Disabled", ServiceStartMode.Disabled)]
    [InlineData("Unknown", null)]
    public void Wmi_start_mode_is_parsed(string text, ServiceStartMode? expected) =>
        Assert.Equal(expected, ServiceText.ParseWmiStartMode(text));

    [Theory]
    [InlineData("Running", ServiceControllerStatus.Running)]
    [InlineData("Stopped", ServiceControllerStatus.Stopped)]
    [InlineData("Start Pending", ServiceControllerStatus.StartPending)]
    [InlineData("Weird", null)]
    public void Wmi_state_is_parsed(string text, ServiceControllerStatus? expected) =>
        Assert.Equal(expected, ServiceText.ParseWmiState(text));

    [Fact]
    public void Start_text_marks_triggers_and_delay()
    {
        Assert.Equal("Вручную (триггер)", ServiceText.Start(ServiceStartMode.Manual, false, true));
        Assert.Equal("Автоматически (отложенно)", ServiceText.Start(ServiceStartMode.Automatic, true));
        Assert.Equal("Отключена", ServiceText.Start(ServiceStartMode.Disabled, false, true));
        Assert.Equal("Нет данных", ServiceText.Start(null, false));
    }
}
