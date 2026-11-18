using System.Collections.ObjectModel;
using System.IO;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Drivers;
using Lycoris.Modules.Drivers.Hardware;

namespace Lycoris.Tests;

public sealed class HardwareTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0);

    private const string NvidiaPnp = @"PCI\VEN_10DE&DEV_2C05&SUBSYS_51701458&REV_A1\4&1A2B3C4D&0&0008";
    private const string AmdIgpuPnp = @"PCI\VEN_1002&DEV_13C0&SUBSYS_7E471462&REV_C5\4&2B3C4D5E&0&0041";
    private const string IntelIgpuPnp = @"PCI\VEN_8086&DEV_A780&SUBSYS_7D251462&REV_04\3&11583659&0&10";
    private const string RealtekNicPnp = @"PCI\VEN_10EC&DEV_8126&SUBSYS_7E471462&REV_01\01000000684CE00000";
    private const string IntelWifiPnp = @"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\6&1A2B3C&0&00E0";

    // ───────────────────────────── Fixtures ─────────────────────────────

    private static GpuInfo Nvidia(string driver = "32.0.16.2101") =>
        new("NVIDIA GeForce RTX 5070 Ti", GpuVendor.Nvidia, driver, new DateTime(2026, 10, 19), 16303UL << 20, NvidiaPnp);

    private static GpuInfo AmdIgpu() =>
        new("AMD Radeon(TM) Graphics", GpuVendor.Amd, "32.0.11024.2", new DateTime(2024, 6, 25), 512UL << 20, AmdIgpuPnp);

    private static NvidiaRelease Release(string version) =>
        new(version, new DateTime(2026, 10, 21), true, false, "https://www.nvidia.com/en-us/drivers/details/282412/", "Game Ready", "1.2 GB")
        {
            DownloadUrl = $"https://us.download.nvidia.com/Windows/{version}/{version}-desktop-win10-win11-64bit-international-dch-whql.exe",
        };

    private static RuntimeInfo AllRuntimes() =>
        new("14.51.36247.0", "14.42.34438.0", true, new Dictionary<int, string> { [10] = "10.0.14" });

    private static HardwareSnapshot Snapshot(
        IReadOnlyList<GpuInfo>? gpus = null,
        RuntimeInfo? runtimes = null,
        IReadOnlyList<RamModule>? ram = null,
        BoardInfo? board = null,
        IReadOnlyList<DisplayInfo>? displays = null,
        IReadOnlyList<PnpDevice>? devices = null,
        IReadOnlyList<NetAdapter>? network = null,
        IReadOnlyList<DiskDetail>? disks = null,
        IReadOnlyList<UninstallEntry>? apps = null,
        bool laptop = false,
        string cpu = "AMD Ryzen 7 9800X3D 8-Core Processor",
        ComputerIdentity? identity = null) => new()
    {
        When = Now,
        Cpu = new CpuInfo(cpu, cpu.Contains("Intel") ? "GenuineIntel" : "AuthenticAMD", 8, 16, 4700),
        IsLaptop = laptop,
        Identity = identity ?? ComputerIdentity.FromFacts("Micro-Star International Co., Ltd.", "MS-7E47",
            (ushort)(laptop ? 2 : 1), [(ushort)(laptop ? 10 : 3)]),
        Board = board ?? new BoardInfo("Micro-Star International Co., Ltd.", "PRO X870-P WIFI (MS-7E47)",
            "American Megatrends International, LLC.", "1.A92", new DateTime(2026, 6, 29), "Micro-Star International Co., Ltd.", "MS-7E47"),
        Gpus = gpus ?? [Nvidia(), AmdIgpu()],
        NvidiaPackageVersion = null,
        Ram = ram ?? [new RamModule("F5-6000J3636F16G", 6000, 6000, 34, 16UL << 30), new RamModule("F5-6000J3636F16G", 6000, 6000, 34, 16UL << 30)],
        Disks = disks ?? [],
        Network = network ?? [],
        Displays = displays ?? [new DisplayInfo("X27U W3", 2560, 1440, 240, 240)],
        Devices = devices ?? [],
        Runtimes = runtimes ?? AllRuntimes(),
        Apps = apps ?? [],
        WingetAvailable = true,
    };

    private static HardwareContext Context(
        WuView? wu = null,
        Dictionary<string, NvidiaRelease?>? nvidia = null,
        IReadOnlyList<SignedDriverInfo>? drivers = null) =>
        new(drivers ?? [], wu ?? new WuView(WuViewState.Ready, []),
            new OnlineInfo(false, nvidia ?? new Dictionary<string, NvidiaRelease?>(), false, null), Now);

    private static WuDriverUpdate Wu(string id, string hwid, string cls, string title = "Realtek - Net - 10.77.1215.2025") =>
        new(id, title, "10.77", cls, "Realtek", "Realtek", "Realtek PCIe 5GbE", hwid, new DateTime(2025, 12, 15), 0, false, 2_000_000, true, null, 0, null, null);

    private static PnpDevice Device(string id, string cls, string name = "Устройство", int error = 0, params string[] hwids) =>
        new(name, id, cls, "", "", error, true, hwids);

    private static HwComponent Component(HwReport r, string keyPrefix) =>
        r.Components.First(c => c.Key.StartsWith(keyPrefix, StringComparison.Ordinal));

    // ───────────────────────────── Vendor ids ─────────────────────────────

    [Theory]
    [InlineData(@"USB\VID_046D&PID_C339&MI_01\7&153553EF&0&0001", "046D", "C339")]
    [InlineData(@"HID\VID_1532&PID_056A&MI_02\9&DFD92C&0&0000", "1532", "056A")]
    [InlineData(@"usb\vid_31e3&pid_1310\6&abc", "31E3", "1310")]
    [InlineData(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002046D_PID&B023\8&1A", "046D", "B023")]
    [InlineData(@"HID\{00001812-0000-1000-8000-00805F9B34FB}_Dev_VID&021038_PID&2210_REV&0001\9&2", "1038", "2210")]
    public void UsbId_parses_usb_hid_and_bluetooth_ids(string id, string vid, string pid)
    {
        Assert.Equal((vid, pid), HardwareRules.UsbId(id));
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2C05\4&1")]
    [InlineData(@"ROOT\UNNAMED_DEVICE\0000")]
    [InlineData("")]
    [InlineData(null)]
    public void UsbId_is_null_for_non_usb_ids(string? id) => Assert.Null(HardwareRules.UsbId(id));

    [Theory]
    [InlineData("046D", "Logitech", HardwareCatalog.LogitechGHub)]
    [InlineData("1532", "Razer", HardwareCatalog.RazerSynapse4)]
    [InlineData("1038", "SteelSeries", HardwareCatalog.SteelSeriesGg)]
    [InlineData("1b1c", "Corsair", HardwareCatalog.CorsairIcue5)]
    public void Peripheral_vendors_map_to_verified_winget_packages(string vid, string name, string wingetId)
    {
        var v = HardwareCatalog.PeripheralByVid(vid);
        Assert.NotNull(v);
        Assert.Equal(name, v!.Name);
        Assert.Equal(wingetId, v.Software[0].WingetId);
    }

    [Theory]
    [InlineData("03F0")]
    [InlineData("0951")]
    [InlineData("0B05")]
    [InlineData("31E3")]
    [InlineData("054C")]
    public void Peripheral_vendors_without_a_verified_package_get_an_official_page_only(string vid)
    {
        var v = HardwareCatalog.PeripheralByVid(vid)!;
        Assert.All(v.Software, s =>
        {
            Assert.Null(s.WingetId);
            Assert.StartsWith("https://", s.Url);
        });
    }

    [Theory]
    [InlineData("258A")] // Sinowealth: generic chip vendor (Glorious and many others) — not identifiable
    [InlineData("05E3")] // Genesys hub
    [InlineData(null)]
    public void Unknown_usb_vendors_are_not_guessed(string? vid) => Assert.Null(HardwareCatalog.PeripheralByVid(vid));

    [Theory]
    [InlineData("Составное USB устройство", true)]
    [InlineData("USB Composite Device", true)]
    [InlineData("HID-совместимая мышь", true)]
    [InlineData("USB-устройство ввода", true)]
    [InlineData("Клавиатура HID", true)]
    [InlineData("Универсальный USB-концентратор", true)]
    [InlineData("", true)]
    [InlineData("PRO", false)]
    [InlineData("LIGHTSPEED Receiver", false)]
    [InlineData("Razer Seiren V3 Mini", false)]
    [InlineData("INZONE E9", false)]
    public void Generic_windows_device_names_are_recognized(string name, bool generic) =>
        Assert.Equal(generic, HardwareRules.IsGenericDeviceName(name));

    [Theory]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_14629E47&REV_1004\5&1", "Realtek ALC897")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10EC&DEV_1220&SUBSYS_1043874F&REV_1000\5&1", "Realtek ALC1220")]
    [InlineData(@"HDAUDIO\FUNC_01&VEN_10DE&DEV_00AB&SUBSYS_10DE0000&REV_1001\5&1", null)]
    [InlineData(@"USB\VID_0BDA&PID_4014\1", null)]
    public void Realtek_codec_names_come_from_the_hd_audio_id(string id, string? expected) =>
        Assert.Equal(expected, HardwareRules.RealtekCodecName(id));

    [Theory]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("Gigabyte Technology Co., Ltd.", "GIGABYTE")]
    [InlineData("ASRock", "ASRock")]
    public void Board_makers_map_to_their_support_pages(string maker, string shortName)
    {
        var b = HardwareCatalog.Board(maker);
        Assert.NotNull(b);
        Assert.Equal(shortName, b!.ShortName);
        Assert.StartsWith("https://", b.SupportUrl);
    }

    [Fact]
    public void Unknown_board_maker_has_no_support_page() => Assert.Null(HardwareCatalog.Board("To Be Filled By O.E.M."));

    // ───────────────────────────── Windows Update matching ─────────────────────────────

    [Theory]
    [InlineData(@"PCI\VEN_10EC&DEV_8126", true)]
    [InlineData(@"pci\ven_10ec&dev_8126&subsys_7e471462", true)]
    [InlineData(RealtekNicPnp, true)]
    [InlineData(@"PCI\VEN_10EC&DEV_812", false)]    // no boundary after the id
    [InlineData(@"PCI\VEN_10EC&DEV_8125", false)]
    [InlineData("", false)]
    public void Wu_hardware_id_matches_the_device_instance(string wuId, bool expected) =>
        Assert.Equal(expected, HardwareRules.WuMatches(RealtekNicPnp, [], wuId));

    [Fact]
    public void Wu_hardware_id_matches_one_of_the_device_hardware_ids() =>
        Assert.True(HardwareRules.WuMatches(@"ACPI\AMDI0101\0", [@"ACPI\VEN_AMDI&DEV_0101", @"ACPI\AMDI0101"], @"acpi\amdi0101"));

    [Theory]
    [InlineData("Display", HwGroupKind.Gpu)]
    [InlineData("NET", HwGroupKind.Network)]
    [InlineData("Bluetooth", HwGroupKind.Network)]
    [InlineData("MEDIA", HwGroupKind.Audio)]
    [InlineData("Monitor", HwGroupKind.Monitors)]
    [InlineData("System", HwGroupKind.Platform)]
    [InlineData("HIDClass", HwGroupKind.Peripherals)]
    [InlineData("SCSIAdapter", HwGroupKind.Storage)]
    public void Wu_driver_classes_map_to_groups(string cls, HwGroupKind group) => Assert.Equal(group, HardwareRules.WuGroup(cls));

    [Fact]
    public void Wu_firmware_and_unknown_classes_belong_to_no_group()
    {
        Assert.Null(HardwareRules.WuGroup("Firmware"));
        Assert.Null(HardwareRules.WuGroup(null));
    }

    // ───────────────────────────── Versions and dates ─────────────────────────────

    [Theory]
    [InlineData("26.9.1", "24.10.24", 1)]
    [InlineData("24.10.24", "26.9.1", -1)]
    [InlineData("1.0", "1.0.0.0", 0)]
    [InlineData("v14.40.33810", "14.40.33810.0", 0)]
    public void Versions_compare_numerically(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(HardwareRules.CompareVersions(a, b)!.Value));

    [Theory]
    [InlineData("—", "24.10.24")]
    [InlineData(null, "1.0")]
    [InlineData("abc", "def")]
    public void Unparsable_versions_do_not_compare(string? a, string? b) => Assert.Null(HardwareRules.CompareVersions(a, b));

    [Fact]
    public void Vc_redist_is_parsed_from_apps_and_features()
    {
        UninstallEntry[] apps =
        [
            new("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "14.40.33810.0", null, null),
            new("Microsoft Visual C++ 2022 X64 Minimum Runtime - 14.44.35211", "14.44.35211", null, null),
            new("Microsoft Visual C++ v14 Redistributable (x86) - 14.50.35719", "14.50.35719.0", null, null),
            new("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664", "12.0.40664.0", null, null),
            new("Microsoft Visual C++ 2022 X64 Debug Runtime - 14.99.1", "14.99.1", null, null),
            new("Steam", "2.10.91.91", null, null),
        ];
        Assert.Equal("14.44.35211", HardwareRules.ParseVcRedist(apps, "x64"));
        Assert.Equal("14.50.35719.0", HardwareRules.ParseVcRedist(apps, "x86"));
        Assert.Null(HardwareRules.ParseVcRedist([apps[3], apps[5]], "x64"));
    }

    [Fact]
    public void Vc_redist_version_falls_back_to_the_display_name()
    {
        UninstallEntry[] apps = [new("Microsoft Visual C++ 2015-2019 Redistributable (x86) - 14.29.30139", null, null, null)];
        Assert.Equal("14.29.30139", HardwareRules.ParseVcRedist(apps, "x86"));
    }

    [Theory]
    [InlineData("14.29.30139.0", true)]
    [InlineData("14.0.24215", true)]
    [InlineData("14.40.33810.0", false)]
    [InlineData("14.51.36247.0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Old_vc_runtimes_are_flagged(string? version, bool outdated) => Assert.Equal(outdated, HardwareRules.VcIsOutdated(version));

    [Fact]
    public void Bios_age_verdict_uses_two_years()
    {
        Assert.True(HardwareRules.BiosIsOld(new DateTime(2024, 9, 1), Now));
        Assert.False(HardwareRules.BiosIsOld(new DateTime(2025, 1, 1), Now));
        Assert.False(HardwareRules.BiosIsOld(null, Now));
        Assert.False(HardwareRules.BiosIsOld(new DateTime(1980, 1, 1), Now)); // bogus SMBIOS date = unknown
    }

    // ───────────────────────────── Storage tools ─────────────────────────────

    [Theory]
    [InlineData("Samsung SSD 970 EVO Plus 1TB", "SSD", "NVMe", "Samsung Magician")]
    [InlineData("KINGSTON SFYRDK2000G", "SSD", "NVMe", "Kingston SSD Manager")]
    [InlineData("CT1000P3PSSD8", "SSD", "NVMe", "Crucial Storage Executive")]
    [InlineData("WD_BLACK SN850X 2000GB", "SSD", "NVMe", "SanDisk Dashboard")]
    [InlineData("SanDisk SSD PLUS 1000GB", "SSD", "SATA", "SanDisk Dashboard")]
    [InlineData("ST2000DM008-2FR102", "HDD", "SATA", "Seagate SeaTools")]
    public void Storage_makers_map_to_their_tools(string model, string media, string bus, string tool) =>
        Assert.Equal(tool, HardwareRules.StorageSoftware(new DiskDetail(model, media, bus, 1UL << 40, "1.0"))?.Name);

    [Theory]
    [InlineData("WDC WD10EZEX-08WN4A0", "HDD", "SATA")]  // WD hard drives: Dashboard supports SSDs only
    [InlineData("USB SanDisk 3.2Gen1", "—", "USB")]       // external drives need nothing
    [InlineData("Micron_2450_MTFDKBA512TFK", "SSD", "NVMe")]
    public void Drives_without_a_matching_tool_get_none(string model, string media, string bus) =>
        Assert.Null(HardwareRules.StorageSoftware(new DiskDetail(model, media, bus, 1UL << 40, "1.0")));

    [Fact]
    public void Samsung_magician_is_offered_through_the_store_page_not_winget()
    {
        var sw = HardwareCatalog.SamsungMagician;
        Assert.Null(sw.WingetId);
        Assert.Equal(HardwareCatalog.SamsungMagicianStoreId, sw.StoreId);
    }

    // ───────────────────────────── GPU rules ─────────────────────────────

    [Fact]
    public void Newer_nvidia_driver_is_a_preselected_official_download()
    {
        var r = HardwareAdvisor.Build(Snapshot(gpus: [Nvidia("32.0.16.1088")]),
            Context(nvidia: new() { [NvidiaPnp] = Release("621.01") }));
        var gpu = Component(r, "gpu:");
        Assert.Equal(HwStatus.UpdateAvailable, gpu.Status);
        var a = Assert.Single(gpu.Actions, x => x.Kind == HwActionKind.DriverPackage);
        Assert.Equal(HwSource.VendorServer, a.Source);
        Assert.True(a.IsInstallable);
        Assert.True(a.PreSelect);
        Assert.Equal("621.01", a.Package!.Version);
        Assert.Equal("NVIDIA Corporation", a.Package.Signer);
        Assert.Equal(NvidiaPnp, a.GpuPnpId);
    }

    [Fact]
    public void Same_nvidia_version_is_current_without_install_actions()
    {
        var r = HardwareAdvisor.Build(Snapshot(), Context(nvidia: new() { [NvidiaPnp] = Release("621.01") }));
        var gpu = Component(r, "gpu:" + NvidiaPnp);
        Assert.Equal(HwStatus.Current, gpu.Status);
        Assert.DoesNotContain(gpu.Actions, a => a.IsInstallable);
        Assert.Contains("621.01", gpu.Details);
    }

    [Fact]
    public void Unanswered_nvidia_lookup_is_never_called_current()
    {
        var r = HardwareAdvisor.Build(Snapshot(), Context(nvidia: new() { [NvidiaPnp] = null }));
        var gpu = Component(r, "gpu:" + NvidiaPnp);
        Assert.Equal(HwStatus.Unchecked, gpu.Status);
        Assert.DoesNotContain(gpu.Actions, a => a.IsInstallable);
        Assert.Contains(gpu.Actions, a => a.Kind == HwActionKind.OpenPage && a.Url == DriverSources.NvidiaDriversPage);
    }

    [Fact]
    public void Pending_lookup_shows_checking_state()
    {
        var ctx = Context() with { Online = OnlineInfo.NotStarted };
        var gpu = Component(HardwareAdvisor.Build(Snapshot(), ctx), "gpu:" + NvidiaPnp);
        Assert.Equal(HwStatus.Unchecked, gpu.Status);
        Assert.Equal("ПРОВЕРКА ВЕРСИИ", gpu.StatusText);
    }

    [Fact]
    public void Secondary_integrated_gpu_is_informational_only()
    {
        var r = HardwareAdvisor.Build(Snapshot(), Context(nvidia: new() { [NvidiaPnp] = Release("621.01") }));
        var igpu = Component(r, "gpu:" + AmdIgpuPnp);
        Assert.Equal(HwStatus.Info, igpu.Status);
        Assert.Empty(igpu.Actions);
        Assert.Equal("ОСНОВНАЯ ВИДЕОКАРТА", r.Groups.First(g => g.Kind == HwGroupKind.Gpu).Components[0].Kind);
    }

    [Fact]
    public void Gpu_on_basic_display_driver_is_not_installed()
    {
        var basic = new GpuInfo("Microsoft Basic Display Adapter", GpuVendor.Nvidia, "10.0.26100.1", null, 0, NvidiaPnp);
        var gpu = Component(HardwareAdvisor.Build(Snapshot(gpus: [basic]), Context()), "gpu:");
        Assert.Equal(HwStatus.NotInstalled, gpu.Status);
        Assert.Contains(gpu.Actions, a => a.Kind == HwActionKind.OpenPage && a.Url == DriverSources.NvidiaDriversPage);
    }

    [Fact]
    public void Newer_amd_installer_is_offered_for_a_primary_radeon()
    {
        var radeon = new GpuInfo("AMD Radeon RX 9070 XT", GpuVendor.Amd, "32.0.21001.1", new DateTime(2025, 6, 1), 16UL << 30,
            @"PCI\VEN_1002&DEV_7550&SUBSYS_1\4&1");
        var pkg = DriverDownloads.ParseAmdPage(
            "<a href=\"https://drivers.amd.com/drivers/installer/26.10/whql/amd-software-adrenalin-edition-26.9.1-minimalsetup-260903_web.exe\">x</a>")!;
        var s = Snapshot(gpus: [radeon]) with
        {
            GpuRegistry = new Dictionary<string, GpuRegistryInfo?> { [radeon.PnpDeviceId] = new("oem12.inf", "Advanced Micro Devices, Inc.", "25.5.1", "Adrenalin") },
        };
        var ctx = Context() with { Online = new OnlineInfo(false, new Dictionary<string, NvidiaRelease?>(), true, pkg) };
        var gpu = Component(HardwareAdvisor.Build(s, ctx), "gpu:");
        Assert.Equal(HwStatus.UpdateAvailable, gpu.Status);
        var a = Assert.Single(gpu.Actions, x => x.Kind == HwActionKind.DriverPackage);
        Assert.True(a.PreSelect);
        Assert.Equal("Advanced Micro Devices", a.Package!.Signer);
    }

    // ───────────────────────────── Platform rules ─────────────────────────────

    [Fact]
    public void Old_bios_is_a_manual_step_with_the_board_support_page()
    {
        var board = new BoardInfo("Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK (MS-7C91)", "AMI", "A.60", new DateTime(2022, 3, 1), "", "");
        var identity = ComputerIdentity.FromFacts("Micro-Star International Co., Ltd.", "MS-7C91", 1, [3]);
        var c = Component(HardwareAdvisor.Build(Snapshot(board: board, identity: identity), Context()), "board");
        Assert.Equal(HwStatus.ManualOnly, c.Status);
        var a = Assert.Single(c.Actions);
        Assert.Equal(HwActionKind.OpenPage, a.Kind);
        Assert.Equal(HwSource.Manual, a.Source);
        Assert.False(a.IsInstallable);
        Assert.Contains("только вручную", a.Reason);
        Assert.Equal("https://www.msi.com/support", a.Url);
        Assert.Equal("MSI MAG B550 TOMAHAWK (MS-7C91)", c.Name);
    }

    [Fact]
    public void Fresh_bios_is_information_only()
    {
        var c = Component(HardwareAdvisor.Build(Snapshot(), Context()), "board");
        Assert.Equal(HwStatus.Info, c.Status);
        Assert.Equal("MSI PRO X870-P WIFI (MS-7E47)", c.Name);
        Assert.DoesNotContain(c.Actions, a => a.IsInstallable);
    }

    [Theory]
    [InlineData("Dell Inc.", "Latitude 7450", 2, 10, "https://www.dell.com/support/home")]
    [InlineData("HP", "EliteBook 840 G11", 2, 10, "https://support.hp.com/us-en/drivers")]
    [InlineData("LENOVO", "ThinkPad T14 Gen 5", 2, 10, "https://pcsupport.lenovo.com/")]
    [InlineData("Acer", "Aspire C27", 1, 13, "https://www.acer.com/support")]
    [InlineData("ASUSTeK COMPUTER INC.", "Zenbook Flip", 2, 31, "https://www.asus.com/support/download-center/")]
    [InlineData("Micro-Star International Co., Ltd.", "Prestige 14", 2, 10, "https://www.msi.com/support")]
    [InlineData("Microsoft Corporation", "Surface Pro 11", 2, 30, "https://support.microsoft.com/surface")]
    [InlineData("Microsoft Corporation", "Surface Pro 11", 2, 32, "https://support.microsoft.com/surface")]
    [InlineData("Dell Inc.", "OptiPlex 7020", 1, 3, "https://www.dell.com/support/home")]
    [InlineData("Micro-Star International Co., Ltd.", "Aegis RS 10th", 1, 3, "https://www.msi.com/support")]
    [InlineData("MSI", "MEG Trident 3 12th", 1, 3, "https://www.msi.com/support")]
    [InlineData("ASUSTeK COMPUTER INC.", "ROG STRIX G15DK", 1, 3, "https://www.asus.com/support/download-center/")]
    [InlineData("ASUSTeK COMPUTER INC.", "G15DK_G15DK", 1, 3, "https://www.asus.com/support/download-center/")]
    public void Oem_bios_uses_system_support_even_with_an_msi_board(string manufacturer, string model,
        int pcType, int chassis, string supportUrl)
    {
        var identity = ComputerIdentity.FromFacts(manufacturer, model, (ushort)pcType, [(ushort)chassis]);
        var board = new BoardInfo("Micro-Star International Co., Ltd.", "MS-7E47", "AMI", "1.0",
            new DateTime(2022, 3, 1), manufacturer, model);
        var report = HardwareAdvisor.Build(Snapshot(board: board, identity: identity), Context());
        var c = Component(report, "board");
        Assert.Equal(HwStatus.ManualOnly, c.Status);
        Assert.Contains(model, c.Name);
        Assert.DoesNotContain("MS-7E47", c.Name);
        Assert.Equal("КОМПЬЮТЕР И BIOS", c.Kind);
        var action = Assert.Single(c.Actions);
        Assert.Equal(supportUrl, action.Url);
        Assert.Equal(HwActionKind.OpenPage, action.Kind);
        Assert.False(action.IsInstallable);
        var computer = Component(report, "computer");
        Assert.Equal(HwStatus.Info, computer.Status);
        Assert.Equal(identity.Summary, computer.Name);
        Assert.Equal(identity.EvidenceText, computer.Details);
        Assert.Equal(identity.Reason, computer.Note);
    }

    [Theory]
    [InlineData("MSI", "MS-7E47", "MSI", "PRO X870-P WIFI (MS-7E47)", true)]
    [InlineData("MSI", "ms-7e47", "MSI", "PRO X870-P WIFI (MS-7E47)", true)]
    [InlineData("MSI", "PRO X870-P WIFI (MS-7E47)", "MSI", "PRO X870-P WIFI (MS-7E47)", true)]
    [InlineData("ASUS", "ROG STRIX B650E-F GAMING WIFI", "ASUS", "ROG STRIX B650E-F GAMING WIFI", true)]
    [InlineData("MSI", "Custom Desktop", "MSI", "PRO X870-P WIFI (MS-7E47)", false)]
    [InlineData("ASUS", "ROG Desktop", "ASUS", "ROG STRIX B650E-F GAMING WIFI", false)]
    [InlineData("MSI", "MS-7E4", "MSI", "PRO X870-P WIFI (MS-7E47)", false)]
    [InlineData("MSI", "7E47", "MSI", "PRO X870-P WIFI (MS-7E47)", false)]
    [InlineData("MSI", "MS-7E47", "MSI", "MAG B550 TOMAHAWK (MS-7C91)", false)]
    [InlineData("MSI", "MS-7E47", "ASUS", "Board (MS-7E47)", false)]
    [InlineData("Gigabyte Technology Co., Ltd.", "X870 AORUS ELITE WIFI7", "Gigabyte Technology Co., Ltd.", "X870 AORUS ELITE WIFI7", true)]
    [InlineData("ASRock", "X870 Steel Legend WiFi", "ASRock", "X870 Steel Legend WiFi", true)]
    [InlineData("Gigabyte Technology Co., Ltd.", "Desktop 1", "Gigabyte Technology Co., Ltd.", "X870 AORUS ELITE WIFI7", false)]
    [InlineData("ASRock", "Desktop 1", "ASRock", "X870 Steel Legend WiFi", false)]
    [InlineData("Gigabyte Technology Co., Ltd.", "X870 AORUS ELITE WIFI7", "MSI", "X870 AORUS ELITE WIFI7", false)]
    [InlineData("ASRock", "X870 Steel Legend WiFi", "ASUS", "X870 Steel Legend WiFi", false)]
    public void Retail_board_support_requires_correlated_system_and_board_identity(string systemMaker, string systemModel,
        string boardMaker, string boardModel, bool matches)
    {
        var identity = ComputerIdentity.FromFacts(systemMaker, systemModel, 1, [3]);
        var board = new BoardInfo(boardMaker, boardModel, "AMI", "1.0", new DateTime(2022, 3, 1), systemMaker, systemModel);
        var snapshot = Snapshot(board: board, identity: identity);
        var support = HardwareCatalog.Support(snapshot);
        var component = Component(HardwareAdvisor.Build(snapshot, Context()), "board");
        if (matches)
        {
            Assert.NotNull(support);
            Assert.False(support.IsSystem);
            Assert.EndsWith(boardModel, component.Name);
            Assert.Equal("ПЛАТА И BIOS", component.Kind);
            Assert.Equal(HwStatus.ManualOnly, component.Status);
            Assert.Equal(HwActionKind.OpenPage, Assert.Single(component.Actions).Kind);
        }
        else
        {
            Assert.Null(support);
            Assert.Equal(HwStatus.Info, component.Status);
            Assert.Empty(component.Actions);
        }
    }

    [Fact]
    public void Uncertain_or_placeholder_identity_never_guesses_a_bios_page()
    {
        ComputerIdentity[] identities =
        [
            ComputerIdentity.Unknown,
            ComputerIdentity.FromFacts("Dell Inc.", "Latitude 7450", 1, [10]),
            ComputerIdentity.FromFacts("Dell Inc.", "To Be Filled By O.E.M.", 2, [10]),
            ComputerIdentity.FromFacts("System Manufacturer", "Latitude 7450", 2, [10]),
            ComputerIdentity.FromFacts("Other vendor", "Notebook 1", 2, [10]),
            ComputerIdentity.FromFacts("Other vendor", "OEM Desktop 1", 1, [3]),
        ];
        var oldBoard = new BoardInfo("Micro-Star International Co., Ltd.", "MAG B550 TOMAHAWK", "AMI", "1.0",
            new DateTime(2022, 3, 1), "Dell Inc.", "Latitude 7450");
        foreach (var identity in identities)
        {
            var snapshot = Snapshot(board: oldBoard, identity: identity);
            Assert.Null(HardwareCatalog.Support(snapshot));
            var c = Component(HardwareAdvisor.Build(snapshot, Context()), "board");
            Assert.Equal(HwStatus.Info, c.Status);
            Assert.Empty(c.Actions);
            Assert.Contains("не подтверждены", c.Note);
        }
    }

    [Fact]
    public void Virtual_machine_has_no_physical_board_or_chipset_advice()
    {
        var identity = ComputerIdentity.FromFacts("Microsoft Corporation", "Virtual Machine", 1, [3]);
        var report = HardwareAdvisor.Build(Snapshot(identity: identity), Context());
        Assert.Equal(ComputerKind.VirtualMachine, identity.Kind);
        Assert.DoesNotContain(report.Components, c => c.Key is "board" or "chipset");
        Assert.Empty(Component(report, "computer").Actions);
    }

    [Fact]
    public void Default_hardware_snapshot_has_unknown_identity_without_a_bios_guess()
    {
        var snapshot = new HardwareSnapshot();
        Assert.Same(ComputerIdentity.Unknown, snapshot.Identity);
        var report = HardwareAdvisor.Build(snapshot, Context());
        Assert.Equal(HwStatus.Info, Component(report, "computer").Status);
        Assert.Empty(Component(report, "board").Actions);
        Assert.Equal(0, report.RecommendationCount);
    }

    [Fact]
    public void Placeholder_retail_board_model_has_no_bios_support_action()
    {
        var board = new BoardInfo("Micro-Star International Co., Ltd.", "To Be Filled By O.E.M.", "AMI", "1.0",
            new DateTime(2022, 3, 1), "Micro-Star International Co., Ltd.", "MS-7E47");
        var snapshot = Snapshot(board: board);
        Assert.Null(HardwareCatalog.Support(snapshot));
        Assert.Empty(Component(HardwareAdvisor.Build(snapshot, Context()), "board").Actions);
    }

    [Theory]
    [InlineData("Dell Inc.", "Latitude 7450", 2, 10, "https://www.dell.com/support/home")]
    [InlineData("MSI", "Aegis RS 10th", 1, 3, "https://www.msi.com/support")]
    [InlineData("ASUS", "ROG STRIX G15DK", 1, 3, "https://www.asus.com/support/download-center/")]
    public void Oem_realtek_network_and_audio_use_the_same_system_support(string manufacturer, string model,
        int pcType, int chassis, string supportUrl)
    {
        const string audioId = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897\1";
        var identity = ComputerIdentity.FromFacts(manufacturer, model, (ushort)pcType, [(ushort)chassis]);
        var snapshot = Snapshot(identity: identity,
            network: [new NetAdapter("Realtek Ethernet", "Realtek", RealtekNicPnp, false)]) with
        {
            Audio = [new SoundDevice("Realtek Audio", "Realtek", audioId)],
        };
        SignedDriverInfo[] drivers =
        [
            new("Realtek Ethernet", RealtekNicPnp, "Net", "1.0", Now, "Microsoft", "Microsoft", "net.inf", false, "Microsoft", ""),
            new("Realtek Audio", audioId, "MEDIA", "1.0", Now, "Microsoft", "Microsoft", "audio.inf", false, "Microsoft", ""),
        ];
        var report = HardwareAdvisor.Build(snapshot, Context(drivers: drivers));
        foreach (var key in new[] { "net:", "audio:" })
        {
            var action = Assert.Single(Component(report, key).Actions);
            Assert.Equal(supportUrl, action.Url);
            Assert.True(action.Optional);
            Assert.False(action.IsInstallable);
        }
    }

    [Fact]
    public void Missing_amd_chipset_parts_point_to_amd_com_only()
    {
        var devices = new[]
        {
            Device(@"ACPI\AMDI0052\0", "System", "AMD PPM Provisioning File", 28, @"ACPI\VEN_AMDI&DEV_0052", @"ACPI\AMDI0052"),
        };
        var drivers = new[]
        {
            new SignedDriverInfo("AMD 3D V-Cache Performance Optimizer", @"ACPI\AMDI0101\0", "System", "1.0.0.9", new DateTime(2024, 4, 15),
                "Advanced Micro Devices, Inc", "AMD", "oem4.inf", true, "Microsoft Windows Hardware Compatibility Publisher", @"ACPI\VEN_AMDI&DEV_0101"),
        };
        var c = Component(HardwareAdvisor.Build(Snapshot(devices: devices), Context(drivers: drivers)), "chipset");
        Assert.Equal(HwStatus.NotInstalled, c.Status);
        var a = Assert.Single(c.Actions);
        Assert.Equal(DriverSources.AmdDriversPage, a.Url);
        Assert.False(a.IsInstallable);
        Assert.False(a.Optional);
    }

    [Fact]
    public void Chipset_waits_for_the_driver_list()
    {
        var c = Component(HardwareAdvisor.Build(Snapshot(), Context(drivers: [])), "chipset");
        Assert.Equal(HwStatus.Unchecked, c.Status);
    }

    // ───────────────────────────── Memory / monitors ─────────────────────────────

    [Fact]
    public void Memory_below_rated_speed_offers_a_conditional_manual_profile_check()
    {
        RamModule[] ram = [new("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30), new("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30)];
        var c = Component(HardwareAdvisor.Build(Snapshot(ram: ram), Context()), "ram");
        Assert.Equal(HwStatus.ManualOnly, c.Status);
        var a = Assert.Single(c.Actions);
        Assert.Equal(HwActionKind.Note, a.Kind);
        Assert.Equal(HwSource.Manual, a.Source);
        Assert.False(a.IsInstallable);
        Assert.False(a.PreSelect);
        Assert.Contains("EXPO", a.Reason);
        Assert.Contains("Если", a.Reason);
        Assert.DoesNotContain("скорее всего", a.Reason);
        Assert.Contains("6000", a.Reason);
        Assert.Contains("4800", a.Reason);
    }

    [Fact]
    public void Memory_below_rated_on_a_laptop_is_information_only()
    {
        RamModule[] ram = [new("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30)];
        var c = Component(HardwareAdvisor.Build(Snapshot(ram: ram, laptop: true), Context()), "ram");
        Assert.Equal(HwStatus.Info, c.Status);
        Assert.Empty(c.Actions);
    }

    [Fact]
    public void Memory_profile_advice_requires_a_confirmed_desktop_and_ignores_the_legacy_battery_flag()
    {
        RamModule[] ram = [new("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30)];
        ComputerIdentity[] uncertainOrNonDesktop =
        [
            ComputerIdentity.Unknown,
            ComputerIdentity.FromFacts("Dell Inc.", "Latitude 7450", 1, [10]),
            ComputerIdentity.FromFacts("Acer", "Aspire C27", 1, [13]),
            ComputerIdentity.FromFacts("Microsoft Corporation", "Virtual Machine", 1, [3]),
        ];
        foreach (var identity in uncertainOrNonDesktop)
        {
            var c = Component(HardwareAdvisor.Build(Snapshot(ram: ram, identity: identity), Context()), "ram");
            Assert.Equal(HwStatus.Info, c.Status);
            Assert.Empty(c.Actions);
            Assert.Contains("4800", c.Details);
            Assert.DoesNotContain("Включите", c.Note);
        }

        // A UPS can set the legacy battery/laptop flag on a physical desktop.
        var desktop = ComputerIdentity.FromFacts("Micro-Star International Co., Ltd.", "MS-7E47", 1, [3]);
        var realDesktop = Component(HardwareAdvisor.Build(Snapshot(ram: ram, laptop: true, identity: desktop), Context()), "ram");
        Assert.Equal(HwStatus.ManualOnly, realDesktop.Status);
        Assert.Equal(HwActionKind.Note, Assert.Single(realDesktop.Actions).Kind);
    }

    [Fact]
    public void Memory_at_rated_speed_is_current()
    {
        var c = Component(HardwareAdvisor.Build(Snapshot(), Context()), "ram");
        Assert.Equal(HwStatus.Current, c.Status);
        Assert.Equal("32 ГБ DDR5", c.Name);
    }

    [Fact]
    public void Drivers_keeps_no_refresh_verdict_for_monitors()
    {
        // The refresh rate is set and judged on «Устройства и сеть» (the Dashboard check links there); Drivers shows the driver only.
        var r = HardwareAdvisor.Build(Snapshot(displays: [new DisplayInfo("Odyssey G5", 2560, 1440, 60, 165)]), Context());
        var c = Component(r, "display:");
        Assert.Equal(HwStatus.Info, c.Status);
        Assert.Equal("ДРАЙВЕР WINDOWS", c.StatusText);
        Assert.Empty(c.Actions);
        Assert.DoesNotContain("Гц", c.Details);
        Assert.Null(c.Note);
        Assert.DoesNotContain(r.Components.SelectMany(x => x.Actions), a => a.Key.StartsWith("refresh:", StringComparison.Ordinal)
                                                                           || a.Url == "ms-settings:display-advanced");
    }

    [Fact]
    public void Monitor_driver_from_windows_update_is_matched_to_its_monitor()
    {
        PnpDevice[] devices = [Device(@"DISPLAY\SAM7377\5&2B1A3C&0&UID4352", "Monitor", "Generic Monitor (Odyssey G5)", 0, @"MONITOR\SAM7377")];
        var wu = new WuView(WuViewState.Ready, [Wu("m1", @"MONITOR\SAM7377", "Monitor", "Samsung - Monitor - 1.0.0.0")]);
        var r = HardwareAdvisor.Build(Snapshot(displays: [new DisplayInfo("Odyssey G5", 1440, 2560, 180, 180)], devices: devices), Context(wu: wu));
        var c = Component(r, "display:");
        Assert.Equal(HwStatus.UpdateAvailable, c.Status);
        var a = Assert.Single(c.Actions);
        Assert.Equal(nameof(DriversTab.Updates), a.Url);
        Assert.Equal(0, r.Groups.First(g => g.Kind == HwGroupKind.Monitors).WuExtra);
    }

    // ───────────────────────────── Network / Windows Update ─────────────────────────────

    private static HardwareSnapshot NetSnapshot(params NetAdapter[] adapters) => Snapshot(network: adapters,
        devices: adapters.Select(a => Device(a.PnpDeviceId, a.IsBluetooth ? "Bluetooth" : "Net", a.Name)).ToArray());

    [Fact]
    public void Matching_windows_update_driver_marks_the_adapter_and_links_the_updates_tab()
    {
        var s = NetSnapshot(new NetAdapter("Realtek(R) PCI(e) Ethernet Controller", "Realtek", RealtekNicPnp, false));
        var wu = new WuView(WuViewState.Ready, [Wu("u1", @"PCI\VEN_10EC&DEV_8126", "Net")]);
        var r = HardwareAdvisor.Build(s, Context(wu: wu));
        var c = Component(r, "net:");
        Assert.Equal(HwStatus.UpdateAvailable, c.Status);
        var a = Assert.Single(c.Actions, x => x.Source == HwSource.WindowsUpdate);
        Assert.Equal(HwActionKind.GoToTab, a.Kind);
        Assert.Equal(nameof(DriversTab.Updates), a.Url);
        Assert.Equal(1, a.WuCount);
        Assert.Equal(0, r.Groups.First(g => g.Kind == HwGroupKind.Network).WuExtra);
    }

    [Fact]
    public void Unmatched_windows_update_drivers_are_counted_per_group()
    {
        var s = NetSnapshot(new NetAdapter("Realtek(R) PCI(e) Ethernet Controller", "Realtek", RealtekNicPnp, false));
        var wu = new WuView(WuViewState.Ready, [Wu("u2", @"USB\VID_0BDA&PID_8156", "Net"), Wu("u3", @"MONITOR\SAM7377", "Monitor")]);
        var r = HardwareAdvisor.Build(s, Context(wu: wu));
        Assert.Equal(1, r.Groups.First(g => g.Kind == HwGroupKind.Network).WuExtra);
        Assert.Equal(1, r.Groups.First(g => g.Kind == HwGroupKind.Monitors).WuExtra);
        Assert.Equal(HwStatus.Current, Component(r, "net:").Status);
    }

    [Fact]
    public void Device_status_waits_for_the_windows_update_search()
    {
        var s = NetSnapshot(new NetAdapter("Realtek(R) PCI(e) Ethernet Controller", "Realtek", RealtekNicPnp, false));
        var c = Component(HardwareAdvisor.Build(s, Context(wu: new WuView(WuViewState.Searching, []))), "net:");
        Assert.Equal(HwStatus.Unchecked, c.Status);
        Assert.Equal("ИДЁТ ПОИСК", c.StatusText);
    }

    [Fact]
    public void Device_without_driver_is_not_installed()
    {
        var a = new NetAdapter("Ethernet Controller", "", RealtekNicPnp, false);
        var s = Snapshot(network: [a], devices: [Device(a.PnpDeviceId, "Net", a.Name, 28)]);
        var c = Component(HardwareAdvisor.Build(s, Context()), "net:");
        Assert.Equal(HwStatus.NotInstalled, c.Status);
        Assert.Contains(c.Actions, x => x.Url == nameof(DriversTab.Problems));
    }

    [Fact]
    public void Intel_dsa_is_offered_once_even_for_several_intel_devices()
    {
        var intelGpu = new GpuInfo("Intel(R) UHD Graphics 770", GpuVendor.Intel, "31.0.101.5186", new DateTime(2024, 1, 1), 128UL << 20, IntelIgpuPnp);
        var s = NetSnapshot(new NetAdapter("Intel(R) Wi-Fi 6E AX211 160MHz", "Intel Corporation", IntelWifiPnp, false)) with
        {
            Gpus = [intelGpu],
            Cpu = new CpuInfo("Intel(R) Core(TM) i5-14400F", "GenuineIntel", 10, 16, 2500),
        };
        var r = HardwareAdvisor.Build(s, Context());
        var dsa = r.Components.SelectMany(c => c.Actions).Where(a => a.Winget?.Id == DriverSources.IntelDsaWingetId).ToList();
        Assert.Single(dsa);
        Assert.False(dsa[0].Optional); // the Intel GPU needs it, even though the chipset only suggests it
        Assert.Contains(r.Components.SelectMany(c => c.Actions), a => a.Key.StartsWith("winget:" + DriverSources.IntelDsaWingetId + "@", StringComparison.Ordinal)
                                                                         && a.Kind == HwActionKind.Note);
    }

    // ───────────────────────────── Peripherals ─────────────────────────────

    [Fact]
    public void Logitech_devices_get_optional_unselected_g_hub()
    {
        PnpDevice[] devices =
        [
            Device(@"USB\VID_046D&PID_C339\088D34643436", "USB", "PRO"),
            Device(@"HID\VID_046D&PID_C339&MI_00\8&70AD0B1&0&0000", "Keyboard", "Клавиатура HID"),
            Device(@"USB\VID_05E3&PID_0608\6&2EB7ECB&0&1", "USB", "Универсальный USB-концентратор"),
        ];
        var c = Component(HardwareAdvisor.Build(Snapshot(devices: devices), Context()), "periph:046D");
        Assert.Equal("PRO", c.Name);
        Assert.Equal(HwStatus.Info, c.Status);
        var ghub = Assert.Single(c.Actions, a => a.Winget?.Id == HardwareCatalog.LogitechGHub);
        Assert.True(ghub.Optional);
        Assert.False(ghub.PreSelect);
        Assert.True(ghub.IsInstallable);
    }

    [Fact]
    public void Installed_vendor_software_is_recognized()
    {
        PnpDevice[] devices = [Device(@"USB\VID_1532&PID_056A&MI_00\8&1", "MEDIA", "Razer Seiren V3 Mini")];
        var c = Component(HardwareAdvisor.Build(Snapshot(devices: devices, apps: [new("Razer Synapse", "4.0", null, null, "Razer Inc.")]), Context()), "periph:1532");
        Assert.Equal(HwStatus.Installed, c.Status);
        Assert.DoesNotContain(c.Actions, a => a.IsInstallable);
    }

    [Fact]
    public void Vendor_software_needs_the_real_publisher_when_it_is_known()
    {
        // One rule with «Устройства и сеть»: display name AND publisher, so look-alikes never count as the vendor's app.
        Assert.False(HardwareCatalog.InzoneHub.IsInstalled([new UninstallEntry("OpenInzone Hub", "1.0", null, null, "someone")]));
        Assert.False(HardwareCatalog.GHub.IsInstalled([new UninstallEntry("Logitech G HUB", "1.0", null, null, null)]));
        Assert.True(HardwareCatalog.InzoneHub.IsInstalled([new UninstallEntry("INZONE Hub", "1.0.20.0", null, null, "Sony Corporation")]));
        Assert.True(HardwareCatalog.GHub.IsInstalled([new UninstallEntry("Logitech G HUB", "2026.7", null, null, "Logitech")]));
        // Publisher not verified for this program: the display name decides, as before.
        Assert.True(HardwareCatalog.Icue.IsInstalled([new UninstallEntry("iCUE", "5.0", null, null, null)]));
    }

    [Fact]
    public void Virtual_g_hub_devices_are_not_peripherals()
    {
        PnpDevice[] devices =
        [
            Device(@"HID\VID_046D&PID_C231\9&12345678&0&0000", "Mouse", "HID-совместимая мышь") with { Virtual = true },
            new("HID-совместимая клавиатура", @"HID\VID_046D&PID_C232\9&87654321&0&0000", "Keyboard", "", "logi_joy_vir_hid", 0, true, []),
            Device(@"LGHUBDEVICE\VID_046D&PID_C231\1", "HIDClass", "Logitech G HUB Virtual Mouse"),
        ];
        var r = HardwareAdvisor.Build(Snapshot(devices: devices), Context());
        Assert.DoesNotContain(r.Groups, g => g.Kind == HwGroupKind.Peripherals);

        // A real Logitech keyboard next to them is listed alone.
        var withKeyboard = HardwareAdvisor.Build(Snapshot(devices: [.. devices, Device(@"HID\VID_046D&PID_C339&MI_00\8&1", "Keyboard", "PRO")]), Context());
        var c = Component(withKeyboard, "periph:046D");
        Assert.Contains("046D:C339", c.Details);
        Assert.DoesNotContain("C231", c.Details);
        Assert.DoesNotContain("C232", c.Details);
    }

    [Fact]
    public void Msi_mystic_light_is_listed_with_msi_center_from_the_official_site()
    {
        PnpDevice[] devices = [Device(@"HID\VID_0DB0&PID_0076\7&2F7C0A1&0&0000", "HIDClass", "USB-устройство ввода")];
        var c = Component(HardwareAdvisor.Build(Snapshot(devices: devices), Context()), "periph:0DB0");
        Assert.Equal("MSI", c.Kind);
        Assert.Equal("Mystic Light (подсветка платы)", c.Name); // Windows calls it only «USB-устройство ввода»
        var a = Assert.Single(c.Actions);
        Assert.Equal("MSI Center", a.Title);
        Assert.Equal(HwActionKind.OpenPage, a.Kind);
        Assert.StartsWith("https://www.msi.com/", a.Url);
        Assert.False(a.IsInstallable); // link-first: no unversioned zip through winget
        Assert.True(a.Optional);
    }

    [Fact]
    public void Go_to_page_actions_are_never_installable()
    {
        var a = new HwAction("go", HwActionKind.GoToPage, HwSource.Manual, "Открыть", "…", "Lycoris") { Url = "devices", FocusId = "kind:monitor" };
        Assert.False(a.IsInstallable);
        var item = new HwActionItem(a, _ => { });
        item.IsSelected = true;
        Assert.False(item.IsSelected);
    }

    [Fact]
    public void Hubs_and_storage_are_not_peripherals()
    {
        PnpDevice[] devices =
        [
            Device(@"USB\VID_0951&PID_1666\1", "DiskDrive", "Kingston DataTraveler 3.0 USB Device"),
            Device(@"USB\VID_05E3&PID_0608\1", "USB", "Универсальный USB-концентратор"),
        ];
        var r = HardwareAdvisor.Build(Snapshot(devices: devices), Context());
        Assert.DoesNotContain(r.Groups, g => g.Kind == HwGroupKind.Peripherals);
    }

    // ───────────────────────────── Game runtimes ─────────────────────────────

    [Fact]
    public void Missing_runtimes_without_a_consumer_are_optional_with_catalog_packages()
    {
        var s = Snapshot(runtimes: new RuntimeInfo(null, null, false, new Dictionary<int, string>()));
        var r = HardwareAdvisor.Build(s, Context());
        var group = r.Groups.First(g => g.Kind == HwGroupKind.Runtimes);
        Assert.All(group.Components, c =>
        {
            Assert.Equal(HwStatus.Info, c.Status);
            Assert.Equal("ПО ЖЕЛАНИЮ", c.StatusText);
        });
        Assert.Equal(0, r.RecommendationCount);

        var actions = group.Components.SelectMany(c => c.Actions).ToList();
        Assert.All(actions, a =>
        {
            Assert.True(a.Optional);
            Assert.False(a.PreSelect);
            Assert.True(a.IsInstallable);
        });
        Assert.Equal([HardwareCatalog.VcX64, HardwareCatalog.VcX86, HardwareCatalog.DirectX,
            HardwareCatalog.DesktopRuntimePrefix + HardwareCatalog.DesktopRuntimeLts], actions.Select(a => a.Winget!.Id).ToList());

        var dx = group.Components.SelectMany(c => c.Actions).Single(a => a.Winget?.Id == HardwareCatalog.DirectX);
        Assert.Equal("exe", dx.Winget!.InstallerType); // the MSIX "DirectXRuntime" framework lacks d3dx9 / XAudio 2.7

        var net = group.Components.Single(c => c.Key == "rt:net").Actions.Single();
        Assert.False(net.PreSelect);
        Assert.Equal(HardwareCatalog.DesktopRuntimePrefix + HardwareCatalog.DesktopRuntimeLts, net.Winget!.Id);
    }

    [Fact]
    public void Outdated_vc_runtime_without_a_consumer_offers_an_optional_update()
    {
        var s = Snapshot(runtimes: AllRuntimes() with { VcX86 = "14.29.30139.0" });
        var report = HardwareAdvisor.Build(s, Context());
        var c = Component(report, "rt:vcx86");
        Assert.Equal(HwStatus.Info, c.Status);
        Assert.Equal("ПО ЖЕЛАНИЮ", c.StatusText);
        Assert.Contains("14.29.30139.0", c.Details);
        Assert.Equal(0, report.RecommendationCount);
        var a = Assert.Single(c.Actions);
        Assert.True(a.Optional);
        Assert.False(a.PreSelect);
        Assert.True(a.IsInstallable);
        Assert.Equal(HardwareCatalog.VcX86, a.Winget!.Id);
    }

    [Fact]
    public void Installed_runtimes_need_nothing()
    {
        var group = HardwareAdvisor.Build(Snapshot(), Context()).Groups.First(g => g.Kind == HwGroupKind.Runtimes);
        Assert.DoesNotContain(group.Components, c => HardwareRules.NeedsAttention(c.Status));
        Assert.Equal(HwStatus.Current, group.Components.Single(c => c.Key == "rt:dx").Status);
        Assert.Equal(HwStatus.Installed, group.Components.Single(c => c.Key == "rt:net").Status);
        Assert.Equal(HwStatus.Installed, group.Components.Single(c => c.Key == "rt:vcx64").Status);
        Assert.Contains("14.51.36247.0", group.Components.Single(c => c.Key == "rt:vcx64").Details);
        Assert.All(group.Components, c => Assert.Empty(c.Actions));
    }

    [Fact]
    public void Unknown_vc_versions_are_installed_without_an_update_offer()
    {
        var report = HardwareAdvisor.Build(Snapshot(runtimes: AllRuntimes() with { VcX64 = "", VcX86 = "" }), Context());
        foreach (var key in new[] { "rt:vcx64", "rt:vcx86" })
        {
            var c = Component(report, key);
            Assert.Equal(HwStatus.Installed, c.Status);
            Assert.Equal("установлен, версия неизвестна", c.Details);
            Assert.Empty(c.Actions);
        }
    }

    [Fact]
    public void Another_dotnet_major_does_not_require_the_catalog_runtime()
    {
        var report = HardwareAdvisor.Build(Snapshot(runtimes: AllRuntimes() with
        {
            DesktopRuntimes = new Dictionary<int, string> { [8] = "8.0.22" },
        }), Context());
        var c = Component(report, "rt:net");
        Assert.Equal(HwStatus.Installed, c.Status);
        Assert.Contains("8.0.22", c.Details);
        Assert.Equal(0, report.RecommendationCount);
        var action = Assert.Single(c.Actions);
        Assert.True(action.Optional);
        Assert.False(action.PreSelect);
        Assert.Equal(HardwareCatalog.DesktopRuntimePrefix + HardwareCatalog.DesktopRuntimeLts, action.Winget!.Id);
    }

    [Fact]
    public void Without_winget_runtimes_fall_back_to_the_microsoft_page()
    {
        var s = Snapshot(runtimes: new RuntimeInfo(null, "14.42.0.0", true, new Dictionary<int, string> { [10] = "10.0.1" })) with { WingetAvailable = false };
        var c = Component(HardwareAdvisor.Build(s, Context()), "rt:vcx64");
        Assert.Equal(HwStatus.Info, c.Status);
        var a = Assert.Single(c.Actions);
        Assert.Equal(HwActionKind.OpenPage, a.Kind);
        Assert.True(a.Optional);
        Assert.False(a.PreSelect);
        Assert.False(a.IsInstallable);
        Assert.StartsWith("https://", a.Url);
    }

    // ───────────────────────────── Safety invariants ─────────────────────────────

    private static HwReport BusyReport()
    {
        PnpDevice[] devices =
        [
            Device(@"USB\VID_046D&PID_C54D\1", "Mouse", "LIGHTSPEED Receiver"),
            Device(@"USB\VID_1532&PID_056A&MI_00\1", "MEDIA", "Razer Seiren V3 Mini"),
            Device(@"USB\VID_1B1C&PID_1B8B\1", "HIDClass", "CORSAIR SCIMITAR"),
            Device(@"USB\VID_1038&PID_12AA\1", "MEDIA", "Arctis Nova 7"),
            Device(@"USB\VID_31E3&PID_1310\1", "Keyboard", "Wooting 60HE"),
        ];
        DiskDetail[] disks =
        [
            new("Samsung SSD 990 PRO 2TB", "SSD", "NVMe", 2UL << 40, "4B2QJXD7"),
            new("CT1000P3PSSD8", "SSD", "NVMe", 1UL << 40, "P9CR40A"),
            new("ST2000DM008-2FR102", "HDD", "SATA", 2UL << 40, "0001"),
        ];
        var s = Snapshot(gpus: [Nvidia("32.0.16.1088"), AmdIgpu()], devices: devices, disks: disks,
            runtimes: new RuntimeInfo(null, "14.29.0.0", false, new Dictionary<int, string>()),
            ram: [new RamModule("F5-6000J3636F16G", 4800, 4800, 34, 16UL << 30)],
            displays: [new DisplayInfo("Odyssey G5", 2560, 1440, 60, 165)]);
        return HardwareAdvisor.Build(s, Context(nvidia: new() { [NvidiaPnp] = Release("621.01") }));
    }

    private static readonly HashSet<string> VerifiedWingetIds = new(StringComparer.OrdinalIgnoreCase)
    {
        HardwareCatalog.LogitechGHub, HardwareCatalog.LogiOptionsPlus, HardwareCatalog.RazerSynapse4, HardwareCatalog.SteelSeriesGg,
        HardwareCatalog.CorsairIcue5, HardwareCatalog.KingstonSsdManager, HardwareCatalog.CrucialStorageExecutive,
        HardwareCatalog.SanDiskDashboard, HardwareCatalog.SeagateSeaTools, DriverSources.IntelDsaWingetId,
    };

    [Fact]
    public void Every_winget_package_is_verified_or_from_the_apps_catalog()
    {
        var ids = BusyReport().Components.SelectMany(c => c.Actions).Select(a => a.Winget?.Id).OfType<string>().ToList();
        Assert.NotEmpty(ids);
        Assert.All(ids, id =>
        {
            Assert.True(VerifiedWingetIds.Contains(id) || AppCatalog.All.Any(e => e.Id == id), $"unverified winget id {id}");
            Assert.False(DriverSources.IsBlocked(id));
            Assert.True(WingetClient.IsValidPackageId(id));
        });
    }

    [Fact]
    public void Optional_software_is_never_preselected_and_only_installable_items_are()
    {
        var actions = BusyReport().Components.SelectMany(c => c.Actions).ToList();
        Assert.All(actions.Where(a => a.Optional), a => Assert.False(a.PreSelect));
        Assert.All(actions.Where(a => a.PreSelect), a => Assert.True(a.IsInstallable));
        // Only the confirmed GPU update is pre-selected; absent runtimes have no known consumer.
        var pre = actions.Where(a => a.PreSelect).Select(a => a.Kind == HwActionKind.DriverPackage ? "gpu" : a.Winget!.Id).ToList();
        Assert.Equal(["gpu"], pre);
    }

    [Fact]
    public void Manual_steps_are_never_installable()
    {
        var actions = BusyReport().Components.SelectMany(c => c.Actions).ToList();
        Assert.All(actions.Where(a => a.Source is HwSource.Manual or HwSource.WindowsUpdate), a => Assert.False(a.IsInstallable));
        Assert.All(actions.Where(a => a.Kind == HwActionKind.OpenPage), a => Assert.StartsWith("https://", a.Url));
        Assert.All(actions.Where(a => a.Kind == HwActionKind.OpenStore), a => Assert.StartsWith("ms-windows-store://", a.Url));
    }

    [Fact]
    public void Driver_packages_only_come_from_vendor_download_servers()
    {
        var packages = BusyReport().Components.SelectMany(c => c.Actions).Select(a => a.Package).OfType<DriverPackage>().ToList();
        Assert.NotEmpty(packages);
        Assert.All(packages, p => Assert.True(DriverDownloads.IsAllowedDownload(p.Url)));
    }

    [Fact]
    public void Report_counts_match_the_component_statuses()
    {
        var r = BusyReport();
        Assert.Equal(r.Groups.Sum(g => g.Components.Count), r.ComponentCount);
        Assert.Equal(r.Components.Count(c => c.Status is HwStatus.UpdateAvailable or HwStatus.NotInstalled or HwStatus.ManualOnly), r.RecommendationCount);
        Assert.Equal(2, r.RecommendationCount); // GPU update and RAM profile; runtimes need a known consumer.
        Assert.Equal(["ПРОЦЕССОР И ПЛАТФОРМА", "ВИДЕОКАРТЫ", "ПАМЯТЬ", "НАКОПИТЕЛИ", "МОНИТОРЫ", "ПЕРИФЕРИЯ", "БИБЛИОТЕКИ ДЛЯ ИГР"],
            r.Groups.Select(g => g.Title).ToList());
    }

    [Fact]
    public void Status_texts_are_russian_uppercase()
    {
        foreach (var s in Enum.GetValues<HwStatus>())
        {
            var t = HardwareRules.StatusText(s);
            Assert.Equal(t.ToUpperInvariant(), t);
            Assert.Matches("[А-Я]", t);
        }
        Assert.Equal("АКТУАЛЬНО", HardwareRules.StatusText(HwStatus.Current));
        Assert.Equal("ЕСТЬ ОБНОВЛЕНИЕ", HardwareRules.StatusText(HwStatus.UpdateAvailable));
        Assert.Equal("НЕ УСТАНОВЛЕНО", HardwareRules.StatusText(HwStatus.NotInstalled));
        Assert.Equal("ТОЛЬКО ВРУЧНУЮ", HardwareRules.StatusText(HwStatus.ManualOnly));
        Assert.Equal("ОФИЦ. СЕРВЕР", HardwareRules.SourceText(HwSource.VendorServer));
    }

    // ───────────────────────────── Installer launch guard ─────────────────────────────

    [Fact]
    public void Only_files_directly_in_the_verified_downloads_folder_may_be_started()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lycoris-dl-test");
        Assert.True(VendorInstallerRunner.IsInDownloadsFolder(Path.Combine(dir, "621.01-dch.exe"), dir));
        Assert.True(VendorInstallerRunner.IsInDownloadsFolder(Path.Combine(dir, "621.01-dch.exe"), dir + "\\"));
        Assert.False(VendorInstallerRunner.IsInDownloadsFolder(Path.Combine(dir, "sub", "x.exe"), dir));
        Assert.False(VendorInstallerRunner.IsInDownloadsFolder(Path.Combine(dir, "..", "x.exe"), dir));
        Assert.False(VendorInstallerRunner.IsInDownloadsFolder(dir + "-evil\\x.exe", dir));
        Assert.False(VendorInstallerRunner.IsInDownloadsFolder(@"C:\Windows\System32\cmd.exe", dir));
    }

    // ───────────────────────────── UI items ─────────────────────────────

    [Fact]
    public void Keyed_sync_keeps_existing_items_and_their_selection()
    {
        var calls = 0;
        var report1 = BusyReport();
        var group = report1.Groups.First(g => g.Kind == HwGroupKind.Runtimes);
        var item = new HwGroupItem(group, 9, _ => calls++);
        var vc = item.Components.Single(c => c.Kind == "VISUAL C++ X64").Actions.Single();
        Assert.False(vc.IsSelected);
        vc.IsSelected = true;
        Assert.Equal(1, calls);

        item.Update(group, 9);
        Assert.Same(vc, item.Components.Single(c => c.Kind == "VISUAL C++ X64").Actions.Single());
        Assert.True(vc.IsSelected);

        // The runtime got installed: its action disappears, the other components stay.
        var installed = group with
        {
            Components = group.Components.Select(c => c.Key == "rt:vcx64" ? c with { Status = HwStatus.Installed, Actions = [] } : c).ToList(),
        };
        item.Update(installed, 9);
        Assert.Empty(item.Components.Single(c => c.Kind == "VISUAL C++ X64").Actions);
        Assert.Equal(group.Components.Count, item.Components.Count);
    }

    [Fact]
    public void Non_installable_actions_cannot_be_selected()
    {
        var note = new HwAction("xmp", HwActionKind.Note, HwSource.Manual, "Включить EXPO", "…", "BIOS");
        var item = new HwActionItem(note, _ => { });
        item.IsSelected = true;
        Assert.False(item.IsSelected);
        Assert.False(item.CanToggle);
    }

    [Fact]
    public void Sync_moves_inserts_and_removes()
    {
        var target = new ObservableCollection<string> { "a", "b", "c" };
        HwItemSync.Sync(target, ["c", "x", "a"], s => s, s => s, s => s, (_, _) => { });
        Assert.Equal(["c", "x", "a"], target);
    }
}
