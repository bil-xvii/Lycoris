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
    public void Profile_titles_follow_the_optimize_scenario_names()
    {
        // Review #40: one wording across pages — the Optimize tiles say «Игры» and «Работа и учёба», so does this picker.
        Assert.Equal("РАБОТА И УЧЁБА", ServiceCatalog.ProfileTitle(ProfileSet.Normal));
        Assert.Equal("ИГРЫ", ServiceCatalog.ProfileTitle(ProfileSet.Gaming));
        Assert.Equal("РЕКОМЕНДУЕМЫЙ", ServiceCatalog.ProfileTitle(ProfileSet.Recommended));
        Assert.Equal("РЕКОМЕНДУЕМЫЙ", ServiceCatalog.ProfileTitle(ProfileSet.Streaming)); // only the three services sets have a title
        foreach (var p in new[] { ProfileSet.Normal, ProfileSet.Gaming, ProfileSet.Recommended })
            Assert.Matches("[А-Я]", ServiceCatalog.ProfileDescription(p));
    }

    [Fact]
    public void Services_profile_follows_the_saved_scenario()
    {
        // Windows only (ServicesViewModel is WPF-bound). Streaming → the gaming services set; efficiency / custom → recommended.
        Assert.Equal(ProfileSet.Normal, ServicesViewModel.ProfileFromScenario("Normal"));
        Assert.Equal(ProfileSet.Gaming, ServicesViewModel.ProfileFromScenario("Streaming"));
        Assert.Equal(ProfileSet.Gaming, ServicesViewModel.ProfileFromScenario("gaming"));
        Assert.Equal(ProfileSet.Recommended, ServicesViewModel.ProfileFromScenario("Efficiency"));
        Assert.Equal(ProfileSet.Recommended, ServicesViewModel.ProfileFromScenario("Custom"));
        Assert.Equal(ProfileSet.Recommended, ServicesViewModel.ProfileFromScenario(null));
        Assert.Equal(ProfileSet.Recommended, ServicesViewModel.ProfileFromScenario("2"));

        Assert.Equal("Следует сценарию «Стриминг и запись» из раздела «Оптимизация».",
            ServicesViewModel.ProfileCaptionFor(ProfileSet.Gaming, "Streaming", laptop: false));
        Assert.Equal("Следует сценарию «Ноутбук: автономность» из раздела «Оптимизация».",
            ServicesViewModel.ProfileCaptionFor(ProfileSet.Recommended, "Efficiency", laptop: true));
        Assert.Equal("Выбран вручную; сценарию «Игры» из раздела «Оптимизация» соответствует профиль «Игры».",
            ServicesViewModel.ProfileCaptionFor(ProfileSet.Normal, "Gaming", laptop: false));
        Assert.StartsWith("Свой набор", ServicesViewModel.ProfileCaptionFor(ProfileSet.Gaming, "Custom", laptop: false));
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

    [Theory]
    [InlineData("Eaphost")]
    [InlineData("dot3svc")]
    [InlineData("NcaSvc")]
    [InlineData("RmSvc")]
    [InlineData("W32Time")]
    [InlineData("tzautoupdate")]
    public void Auth_radio_and_time_services_are_hard_blocked(string name) =>
        Assert.True(ServiceGuard.IsHardBlocked(name));

    [Theory]
    [InlineData("Razer Elevation Service", true)]
    [InlineData("SomeWindowsService", false)]
    public void Non_curated_services_are_read_only_outside_expert_mode(string name, bool thirdParty)
    {
        var reason = ServiceGuard.NotCuratedLockReason(name, thirdParty, expertMode: false);
        Assert.NotNull(reason);
        Assert.Contains("Экспертного режима служб", reason);
        Assert.Null(ServiceGuard.NotCuratedLockReason(name, thirdParty, expertMode: true));
    }

    [Fact]
    public void Curated_services_are_never_expert_locked()
    {
        foreach (var s in ServiceCatalog.Services)
            Assert.Null(ServiceGuard.NotCuratedLockReason(s.Name, false, expertMode: false));
    }

    [Fact]
    public void Services_expert_mode_is_off_by_default() => Assert.False(new AppSettings().ServicesExpertMode);

    [Fact]
    public void DiagTrack_has_one_place_optimize()
    {
        // DiagTrack is changed only by Optimize's ADVANCED diagtrack-manual tweak, never from Services.
        Assert.False(ServiceCatalog.IsCurated("DiagTrack"));
        Assert.Equal("diagtrack-manual", ServiceGuard.OptimizeTweakFor("DiagTrack"));
        var tweak = TweakCatalog.All.Single(t => t.Id == "diagtrack-manual");
        Assert.Equal(TweakRisk.Advanced, tweak.Risk);
        // Locked on ВСЕ СЛУЖБЫ even in expert mode.
        Assert.NotNull(ServiceGuard.OptimizeOwnedLockReason("diagtrack"));
        Assert.Null(ServiceGuard.OptimizeOwnedLockReason("Spooler"));
    }

    [Fact]
    public void Curated_services_are_not_owned_by_optimize()
    {
        foreach (var s in ServiceCatalog.Services)
            Assert.Null(ServiceGuard.OptimizeTweakFor(s.Name));
    }

    [Theory]
    [InlineData("tab:startup", ServicesViewModel.TabStartup)]
    [InlineData("tab:services", ServicesViewModel.TabRecommendations)]
    [InlineData("tab:all", ServicesViewModel.TabAll)]
    [InlineData("tab:tasks", ServicesViewModel.TabTasks)]
    public void Focus_ids_select_tabs(string id, int tab) => Assert.Equal(tab, ServicesViewModel.FocusTab(id));

    [Theory]
    [InlineData("Spooler")]
    [InlineData("tab:unknown")]
    public void Other_focus_ids_are_service_names(string id) => Assert.Null(ServicesViewModel.FocusTab(id));

    [Fact]
    public void Remote_registry_must_stay_disabled()
    {
        Assert.True(ServiceGuard.MustStayDisabled("RemoteRegistry"));
        Assert.False(ServiceGuard.MustStayDisabled("Spooler"));
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
