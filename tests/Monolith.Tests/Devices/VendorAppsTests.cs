using System.IO;
using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Vendors;
using Monolith.Modules.Drivers;
using Monolith.Modules.Drivers.Hardware;

namespace Monolith.Tests.Devices;

/// <summary>Vendor programs: detection by the shared rule, and the launch guard (fixed exe, Program Files, signature, unelevated).</summary>
public sealed class VendorAppsTests
{
    // ───────────────────────────── Detection ─────────────────────────────

    private static readonly UninstallEntry[] DevPc =
    [
        new("Logitech G HUB", "2026.7.992920", null, @"C:\Program Files\LGHUB\lghub.exe", "Logitech"),
        new("INZONE Hub", "1.0.20.0", @"C:\Program Files\Sony\INZONE Hub\", null, "Sony Corporation"),
        new("OpenRGB", "1.1.0", null, null, "OpenRGB"),
        new("Razer Synapse", "4.0.503", null, null, "Razer Inc."), // read from HKLM\SOFTWARE\WOW6432Node
    ];

    [Fact]
    public void Detection_needs_the_display_name_and_the_publisher()
    {
        var s = VendorApps.Detect([new UninstallEntry("OpenInzone Hub", "0.9", null, null, "someone"), new UninstallEntry("G-Helper", "1", null, null, "seerge")],
            _ => true, _ => false);
        Assert.False(s.IsInstalled(VendorApps.InzoneHub.Key));
        Assert.False(s.IsInstalled(VendorApps.GHub.Key));
        Assert.Empty(s.Installed);
    }

    [Fact]
    public void Razer_synapse_from_the_32_bit_uninstall_view_is_found()
    {
        var s = VendorApps.Detect(DevPc, _ => true, _ => false);
        var razer = s.Get(VendorApps.Synapse.Key)!;
        Assert.True(razer.Installed);
        Assert.Equal("4.0.503", razer.Version);
        Assert.True(s.IsInstalled(VendorApps.GHub.Key));
        Assert.True(s.IsInstalled(VendorApps.InzoneHub.Key));
        Assert.True(s.IsInstalled(VendorApps.OpenRgb.Key));
        Assert.False(s.IsInstalled(VendorApps.MsiCenter.Key));
    }

    [Fact]
    public void Launchable_needs_the_exe_and_openrgb_never_is()
    {
        var s = VendorApps.Detect(DevPc, app => app.Key != VendorApps.Synapse.Key, name => name == "lghub_agent");
        Assert.True(s.Get(VendorApps.GHub.Key)!.Launchable);
        Assert.True(s.Get(VendorApps.GHub.Key)!.Running);
        Assert.False(s.Get(VendorApps.Synapse.Key)!.Launchable); // installed, exe missing (e.g. Synapse 3)
        Assert.False(s.Get(VendorApps.OpenRgb.Key)!.Launchable); // unsigned: never
        Assert.False(VendorApps.OpenRgb.IsLaunchable);
        Assert.False((VendorApps.OpenRgb with { ExeUnderProgramFiles = @"OpenRGB\OpenRGB.exe", Signers = ["OpenRGB"] }).IsLaunchable);
    }

    [Fact]
    public void Drivers_and_devices_use_the_same_install_rule()
    {
        foreach (var app in VendorApps.All.Where(a => a.Install is not null))
        {
            Assert.Contains(app.DisplayNameContains, app.Install!.InstalledNames);
            Assert.Equal(app.PublisherContains.Length == 0 ? null : app.PublisherContains, app.Install.InstalledPublisher);
            foreach (var entry in DevPc.Append(new UninstallEntry("OpenInzone Hub", "1", null, null, "x")))
                Assert.Equal(app.Install.IsInstalled([entry]), app.Matches(entry));
        }
    }

    [Fact]
    public void Launch_table_is_fixed_and_signed()
    {
        Assert.All(VendorApps.All.Where(a => a.IsLaunchable), a =>
        {
            Assert.False(Path.IsPathRooted(a.ExeUnderProgramFiles));
            Assert.DoesNotContain("..", a.ExeUnderProgramFiles);
            Assert.EndsWith(".exe", a.ExeUnderProgramFiles, StringComparison.OrdinalIgnoreCase);
            Assert.NotEmpty(a.Signers);
            Assert.NotEmpty(a.PublisherContains);
            Assert.DoesNotContain(":", a.Args); // no URI handlers (lghubapp:, RazerAppEngine.synapse:)
        });
        Assert.Equal(["ghub", "synapse", "inzone"], VendorApps.All.Where(a => a.IsLaunchable).Select(a => a.Key));
        Assert.Equal("--url-params=apps=synapse", VendorApps.Synapse.Args);
    }

    // ───────────────────────────── Launch guard ─────────────────────────────

    private sealed class FakeDisk
    {
        public string Root { get; } = $@"X:\PF-{Guid.NewGuid():N}";
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DateTime> Stamps { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Reparse { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (bool Ok, string Signer) Signature { get; set; } = (true, "подписан: Razer USA Ltd.");
        public int SignatureChecks { get; private set; }
        public List<(string Exe, string Args, string? Dir)> Started { get; } = [];
        public bool StartResult { get; set; } = true;

        public string Add(string rel)
        {
            var full = Path.Combine(Root, rel);
            Files.Add(full);
            Stamps[full] = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            return full;
        }

        public VendorApps.LaunchEnvironment Env => new()
        {
            ProgramFiles = Root,
            FileExists = Files.Contains,
            LastWriteUtc = p => Stamps.TryGetValue(p, out var t) ? t : null,
            IsReparsePoint = Reparse.Contains,
            VerifySignature = (_, _) =>
            {
                SignatureChecks++;
                return Task.FromResult(Signature);
            },
            Start = (exe, args, dir) =>
            {
                Started.Add((exe, args, dir));
                return Task.FromResult(StartResult);
            },
        };
    }

    [Fact]
    public async Task A_valid_signed_exe_starts_with_the_fixed_arguments()
    {
        var disk = new FakeDisk();
        var exe = disk.Add(@"Razer\RazerAppEngine\RazerAppEngine.exe");
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.Synapse, disk.Env);
        Assert.True(ok, problem);
        var started = Assert.Single(disk.Started);
        Assert.Equal(exe, started.Exe);
        Assert.Equal("--url-params=apps=synapse", started.Args);
        Assert.Equal(Path.GetDirectoryName(exe), started.Dir);
    }

    [Theory]
    [InlineData(@"..\Windows\System32\cmd.exe")]
    [InlineData(@"..\..\Users\Public\evil.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    public async Task A_path_outside_program_files_is_refused(string rel)
    {
        var disk = new FakeDisk();
        disk.Files.Add(Path.GetFullPath(Path.Combine(disk.Root, rel)));
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.GHub with { ExeUnderProgramFiles = rel }, disk.Env);
        Assert.False(ok);
        Assert.NotNull(problem);
        Assert.Empty(disk.Started);
        Assert.Equal(0, disk.SignatureChecks);
    }

    [Fact]
    public async Task A_missing_file_is_refused()
    {
        var disk = new FakeDisk();
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.GHub, disk.Env);
        Assert.False(ok);
        Assert.Contains("не найдена", problem);
        Assert.Empty(disk.Started);
    }

    [Fact]
    public async Task A_reparse_point_on_the_way_is_refused()
    {
        var disk = new FakeDisk();
        var exe = disk.Add(@"LGHUB\system_tray\lghub_system_tray.exe");
        disk.Reparse.Add(Path.Combine(disk.Root, "LGHUB")); // a junction pointing the fixed path elsewhere
        var (ok, _) = await VendorApps.TryLaunchAsync(VendorApps.GHub, disk.Env);
        Assert.False(ok);
        Assert.Empty(disk.Started);

        disk.Reparse.Clear();
        disk.Reparse.Add(exe);
        Assert.False((await VendorApps.TryLaunchAsync(VendorApps.GHub, disk.Env)).Ok);
        Assert.Empty(disk.Started);
    }

    [Theory]
    [InlineData("статус «HashMismatch», подписант «Logitech Inc»")]
    [InlineData("подписант «Evil Corp», а ожидался «Logitech Inc»")]
    public async Task An_invalid_signature_or_the_wrong_signer_is_refused(string reason)
    {
        var disk = new FakeDisk { Signature = (false, reason) };
        disk.Add(@"LGHUB\system_tray\lghub_system_tray.exe");
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.GHub, disk.Env);
        Assert.False(ok);
        Assert.Contains(reason, problem);
        Assert.Empty(disk.Started);
    }

    [Fact]
    public async Task Openrgb_is_never_launched()
    {
        var disk = new FakeDisk();
        disk.Add(@"OpenRGB\OpenRGB.exe");
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.OpenRgb with { ExeUnderProgramFiles = @"OpenRGB\OpenRGB.exe", Signers = ["OpenRGB"] }, disk.Env);
        Assert.False(ok);
        Assert.Contains("без цифровой подписи", problem);
        Assert.Empty(disk.Started);
        Assert.Equal(0, disk.SignatureChecks);
    }

    [Fact]
    public async Task The_signature_check_is_cached_until_the_file_changes()
    {
        var disk = new FakeDisk();
        var exe = disk.Add(@"Sony\INZONE Hub\INZONEHub.exe");
        Assert.True((await VendorApps.TryLaunchAsync(VendorApps.InzoneHub, disk.Env)).Ok);
        Assert.True((await VendorApps.TryLaunchAsync(VendorApps.InzoneHub, disk.Env)).Ok);
        Assert.Equal(1, disk.SignatureChecks);

        disk.Stamps[exe] = disk.Stamps[exe].AddMinutes(5); // updated (or replaced) since
        disk.Signature = (false, "подписант «Someone»");
        Assert.False((await VendorApps.TryLaunchAsync(VendorApps.InzoneHub, disk.Env)).Ok);
        Assert.Equal(2, disk.SignatureChecks);
        Assert.Equal(2, disk.Started.Count);
    }

    [Fact]
    public async Task Windows_refusing_to_start_it_is_reported()
    {
        var disk = new FakeDisk { StartResult = false };
        disk.Add(@"LGHUB\system_tray\lghub_system_tray.exe");
        var (ok, problem) = await VendorApps.TryLaunchAsync(VendorApps.GHub, disk.Env);
        Assert.False(ok);
        Assert.Contains("без прав администратора", problem);
    }
}

/// <summary>The enrichment over hand-made cards, inventory and program snapshots (the owner's PC).</summary>
public sealed class VendorEnricherTests
{
    private const string KeyboardSerial = "088D34643436";
    private const string MsiSerial = "7E4724092002";

    private static InventoryGroup Group(string vid, string pid, string name, InventoryKind kind, bool lamp = false, string? container = null,
        params string[] ids) =>
        new(container ?? $@"USB\VID_{vid}&PID_{pid}\{Guid.NewGuid():N}", vid, pid, name, kind,
            ids.Length > 0 ? ids : [$@"USB\VID_{vid}&PID_{pid}\{Guid.NewGuid():N}"], container, lamp, HardwareCatalog.PeripheralByVid(vid));

    private static readonly InventoryGroup Receiver = Group("046D", "C54D", "LIGHTSPEED-приёмник Logitech", InventoryKind.Mouse, lamp: true,
        container: "{B7F3C0B6-30BE-52DE-AF39-C1A56D223E64}");
    private static readonly InventoryGroup Keyboard = Group("046D", "C339", "PRO Gaming Keyboard", InventoryKind.Keyboard, lamp: true,
        container: "{8E8EB054-4AD6-5D06-8200-85DAB151D7F6}", ids: [$@"USB\VID_046D&PID_C339\{KeyboardSerial}"]);
    private static readonly InventoryGroup Inzone = Group("054C", "0F80", "INZONE E9", InventoryKind.Audio, container: "{74FD64AE-D150-11F1-96E5-806E6F6E6963}");
    private static readonly InventoryGroup Seiren = Group("1532", "056A", "Razer Seiren V3 Mini", InventoryKind.Audio, container: "{74FD64AF-D150-11F1-96E5-806E6F6E6963}");
    private static readonly InventoryGroup MysticLight = new($@"USB\VID_0DB0&PID_0076\{MsiSerial}", "0DB0", "0076", "MYSTIC LIGHT",
        InventoryKind.Lighting, [$@"USB\VID_0DB0&PID_0076\{MsiSerial}", @"HID\VID_0DB0&PID_0076\8&1"], null, false, HardwareCatalog.PeripheralByVid("0DB0"));

    private static VendorAppSnapshot Apps(string[] installed, string[]? launchable = null) =>
        new(VendorApps.All.Select(a => new VendorAppState(a, installed.Contains(a.Key), (launchable ?? installed).Contains(a.Key) && a.IsLaunchable, false, null)).ToList());

    private static readonly VendorAppSnapshot OwnerApps = Apps(["ghub", "synapse", "inzone", "openrgb"]);

    private static DeviceCard Card(string key, DeviceKind kind, string title, params InventoryGroup[] refs) => new()
    {
        Key = key, Kind = kind, Title = title,
        Hardware = refs.Select(g => new DeviceHardwareRef(g.Vid, g.Pid, null, [])).ToList(),
    };

    private static readonly IReadOnlyList<HistoryHint> ProX2 = [new("046D", "C0A8", "PRO X2 SUPERSTRIKE", DateTime.UtcNow.AddDays(-1))];

    private static (List<DeviceCard> Cards, List<VendorApp> Launched) Run(List<DeviceCard> cards, IReadOnlyList<InventoryGroup> groups,
        VendorAppSnapshot apps, IReadOnlyList<HistoryHint>? history = null)
    {
        var launched = new List<VendorApp>();
        VendorEnricher.Enrich(cards, groups, apps, _ => history ?? ProX2, app =>
        {
            launched.Add(app);
            return Task.FromResult(true);
        });
        return (cards, launched);
    }

    private static List<DeviceCard> OwnerCards() =>
    [
        Card("mouse", DeviceKind.Mouse, "Мышь", Receiver),
        Card("keyboard", DeviceKind.Keyboard, "Клавиатура", Keyboard),
        Card("audio:{0.0.0.00000000}.{11111111-1111-1111-1111-111111111111}", DeviceKind.AudioOutput, "Наушники (INZONE E9)", Inzone),
        Card("audio:{0.0.1.00000000}.{22222222-2222-2222-2222-222222222222}", DeviceKind.AudioInput, "Микрофон (Razer Seiren V3 Mini)", Seiren),
    ];

    [Fact]
    public async Task The_receiver_mouse_gets_its_names_g_hub_and_the_onboard_memory_note()
    {
        var (cards, launched) = Run(OwnerCards(), [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        var mouse = cards.Single(c => c.Key == "mouse");
        Assert.Equal("LIGHTSPEED-приёмник Logitech · раньше по кабелю подключалась: PRO X2 SUPERSTRIKE", mouse.Subtitle);
        var ghub = Assert.Single(mouse.VendorApps);
        Assert.Equal("ОТКРЫТЬ G HUB", ghub.ButtonText);
        Assert.True(ghub.CanLaunch);
        Assert.Contains("DPI", ghub.Tooltip);
        Assert.Contains(mouse.Notes, n => n.Text == VendorEnricher.OnboardMemoryNote);

        Assert.True(await ghub.Launch!());
        Assert.Equal([VendorApps.GHub], launched); // nothing starts by itself — only the click
    }

    [Fact]
    public void Nothing_is_launched_by_the_scan()
    {
        var (_, launched) = Run(OwnerCards(), [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        Assert.Empty(launched);
    }

    [Fact]
    public void The_keyboard_gets_its_model_the_polling_tooltip_and_the_only_dynamic_lighting_note()
    {
        var (cards, _) = Run(OwnerCards(), [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        var keyboard = cards.Single(c => c.Key == "keyboard");
        Assert.Equal("PRO Gaming Keyboard", keyboard.Subtitle);
        Assert.Contains("Частота опроса", Assert.Single(keyboard.VendorApps).Tooltip);
        Assert.DoesNotContain(keyboard.Notes, n => n.Text == VendorEnricher.OnboardMemoryNote);
        var lighting = Assert.Single(cards.SelectMany(c => c.Notes), n => n.Text.Contains("Динамическое освещение"));
        Assert.Contains(lighting, keyboard.Notes); // once per page, on the keyboard (the receiver has LampArray children too)
        Assert.Equal(DeviceLinkKind.Settings, lighting.Link!.Kind);
        Assert.Equal("ms-settings:personalization-lighting", lighting.Link.Target);
        Assert.Contains("G HUB", lighting.Text);
    }

    [Fact]
    public void A_keyboard_card_that_also_holds_the_receiver_gets_no_mouse_notes()
    {
        // An input provider that grouped the receiver's keyboard collection with the keyboard: still only the keyboard's name.
        var cards = new List<DeviceCard> { Card("keyboard", DeviceKind.Keyboard, "Клавиатура", Keyboard, Receiver) };
        Run(cards, [Receiver, Keyboard], OwnerApps);
        var keyboard = cards[0];
        Assert.Equal("PRO Gaming Keyboard", keyboard.Subtitle);
        Assert.DoesNotContain(keyboard.Notes, n => n.Text == VendorEnricher.OnboardMemoryNote);
        Assert.DoesNotContain("раньше по кабелю", keyboard.Subtitle);
    }

    [Fact]
    public void The_inzone_card_gets_inzone_hub_once_and_the_razer_mic_synapse()
    {
        var cards = OwnerCards();
        cards.Add(Card("audio:{0.0.1.00000000}.{33333333-3333-3333-3333-333333333333}", DeviceKind.AudioInput, "Микрофон (INZONE E9)", Inzone));
        Run(cards, [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        var headset = cards.Single(c => c.Title == "Наушники (INZONE E9)");
        Assert.Equal("ОТКРЫТЬ INZONE HUB", Assert.Single(headset.VendorApps).ButtonText);
        Assert.Null(headset.Subtitle); // the endpoint name already says INZONE E9
        Assert.Single(cards.SelectMany(c => c.Notes), n => n.Text == VendorEnricher.InzoneNote);
        Assert.Contains(headset.Notes, n => n.Text == VendorEnricher.InzoneNote);

        var mic = cards.Single(c => c.Title == "Микрофон (Razer Seiren V3 Mini)");
        Assert.Equal("ОТКРЫТЬ RAZER SYNAPSE", Assert.Single(mic.VendorApps).ButtonText);
        Assert.Contains(mic.Notes, n => n.Text == VendorEnricher.RazerMicNote);
    }

    [Fact]
    public void A_missing_vendor_program_is_offered_on_drivers_at_the_vendors_row()
    {
        var cards = new List<DeviceCard> { Card("mouse", DeviceKind.Mouse, "Мышь", Receiver) };
        Run(cards, [Receiver], Apps([]));
        var offer = Assert.Single(cards[0].VendorApps);
        Assert.False(offer.CanLaunch);
        Assert.True(offer.ShowInstall);
        Assert.Equal("УСТАНОВИТЬ В «ДРАЙВЕРАХ»", offer.ButtonText);
        Assert.Equal("periph:046D", offer.InstallFocusId);
        Assert.DoesNotContain(cards[0].Notes, n => n.Text == VendorEnricher.OnboardMemoryNote); // G HUB is not there to act on it
    }

    [Fact]
    public void An_installed_program_without_a_verified_launch_says_where_to_open_it()
    {
        var cards = new List<DeviceCard> { Card("mouse", DeviceKind.Mouse, "Мышь", Receiver) };
        Run(cards, [Receiver], Apps(["optionsplus"]));
        Assert.Empty(cards[0].VendorApps);
        Assert.Contains(cards[0].Notes, n => n.Text == "Откройте Logi Options+ из меню «Пуск»");
    }

    [Fact]
    public void The_unclaimed_msi_controller_with_openrgb_becomes_a_lighting_card_without_an_msi_center_offer()
    {
        var (cards, _) = Run(OwnerCards(), [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        var msi = cards.Single(c => c.Kind == DeviceKind.Lighting);
        Assert.Equal("Mystic Light (MSI)", msi.Title);
        Assert.Equal("подсветка материнской платы", msi.Subtitle);
        Assert.StartsWith("periph:", msi.Key);
        Assert.True(msi.Order >= 80);
        Assert.Empty(msi.Facts); // no «USB 0DB0:0076»
        Assert.Empty(msi.VendorApps); // OpenRGB is never launched; MSI Center (a third RGB program) is not recommended
        Assert.Contains(msi.Notes, n => n.Text == VendorEnricher.UnifierOwnsNote("OpenRGB", board: true));
        Assert.Contains(msi.Notes, n => n.Text.StartsWith("OpenRGB и программы производителей (G HUB, Razer Synapse)", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_providers_every_known_device_with_a_program_gets_its_own_card()
    {
        // A provider that found nothing (or failed): the inventory still shows the devices that have something to do here.
        var (cards, _) = Run([], [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        Assert.Equal(5, cards.Count);
        var mouse = cards.Single(c => c.Kind == DeviceKind.Mouse);
        Assert.Equal("LIGHTSPEED-приёмник Logitech", mouse.Title);
        Assert.Equal("раньше по кабелю подключалась: PRO X2 SUPERSTRIKE", mouse.Subtitle);
        Assert.Contains(mouse.Notes, n => n.Text == VendorEnricher.OnboardMemoryNote);
        Assert.Equal("PRO Gaming Keyboard", cards.Single(c => c.Kind == DeviceKind.Keyboard).Title);
        // One audio device does not hide the other.
        Assert.Equal(["INZONE E9", "Razer Seiren V3 Mini"], cards.Where(c => c.Kind == DeviceKind.AudioOutput).Select(c => c.Title).Order());
        Assert.All(cards, c => Assert.InRange(c.Order, 80, 99));
        Assert.Equal(cards.Count, cards.Select(c => c.Key).Distinct().Count());
    }

    [Fact]
    public void Inventory_cards_with_only_an_install_offer_are_not_created()
    {
        var (cards, _) = Run([], [Receiver, Keyboard, Inzone], Apps([]));
        Assert.Empty(cards);
    }

    [Fact]
    public void An_msi_controller_with_only_an_install_offer_stays_on_drivers()
    {
        var (cards, _) = Run([], [MysticLight], Apps([]));
        Assert.Empty(cards);
    }

    [Fact]
    public void An_installed_msi_center_gives_the_msi_card_a_way_to_open_it()
    {
        var (cards, _) = Run([], [MysticLight], Apps(["msicenter"]));
        var msi = Assert.Single(cards);
        Assert.Contains(msi.Notes, n => n.Text == "Откройте MSI Center из меню «Пуск»");
        Assert.DoesNotContain(msi.Notes, n => n.Text.Contains("OpenRGB"));
    }

    [Theory]
    [InlineData(new[] { "openrgb" }, false)]
    [InlineData(new[] { "ghub" }, false)]
    [InlineData(new[] { "ghub", "openrgb" }, true)]
    [InlineData(new[] { "icue", "signalrgb" }, true)]
    public void The_rgb_conflict_note_needs_a_unifier_and_a_vendor_lighting_program(string[] installed, bool shown)
    {
        var (cards, _) = Run(OwnerCards(), [Receiver, Keyboard], Apps(installed));
        var notes = cards.SelectMany(c => c.Notes).Where(n => n.Text.Contains("могут спорить за подсветку")).ToList();
        Assert.Equal(shown ? 1 : 0, notes.Count);
        if (shown) Assert.Contains(notes[0], cards.Single(c => c.Key == "keyboard").Notes); // no lighting card here → the keyboard
    }

    [Fact]
    public void The_unifier_note_for_one_vendor_program_reads_as_specified() =>
        Assert.Equal("OpenRGB и G HUB могут спорить за подсветку — пользуйтесь одной программой", VendorEnricher.UnifierNote(["OpenRGB"], ["G HUB"]));

    [Fact]
    public void No_serial_reaches_a_card()
    {
        var (cards, _) = Run(OwnerCards(), [Receiver, Keyboard, Inzone, Seiren, MysticLight], OwnerApps);
        foreach (var c in cards)
        {
            var visible = string.Join("|", new[] { c.Key, c.Title, c.Subtitle ?? "", c.FactsText }
                .Concat(c.Notes.Select(n => n.Text + n.Link?.Target)).Concat(c.VendorApps.Select(a => a.ButtonText + a.Tooltip + a.InstallFocusId)));
            Assert.DoesNotContain(KeyboardSerial, visible, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(MsiSerial, visible, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_card_keeps_its_collections_when_its_subtitle_is_added()
    {
        var setting = new DeviceSetting { Id = "mouse-speed", Title = "Скорость указателя" };
        var card = Card("mouse", DeviceKind.Mouse, "Мышь", Receiver);
        card.Settings.Add(setting);
        var cards = new List<DeviceCard> { card };
        Run(cards, [Receiver], OwnerApps);
        Assert.NotSame(card, cards[0]);
        Assert.Same(card.Settings, cards[0].Settings);
        Assert.Same(setting, Assert.Single(cards[0].Settings));
        Assert.Equal(card.Hardware, cards[0].Hardware);
    }

    // ───────────────────────────── HDR calibration ─────────────────────────────

    private static DeviceCard MonitorWithCalibrationNote() => new()
    {
        Key = @"display:\\?\DISPLAY#AOC2701#5&1&UID4352#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", Kind = DeviceKind.Monitor, Title = "X27U W3",
        Notes = [new DeviceNote("HDR включён — откалибруйте его приложением «Калибровка HDR»", new DeviceLink(DeviceLinkKind.Navigate, "ПРИЛОЖЕНИЯ", "apps", "9N7F2SM5D1LR"))],
    };

    [Fact]
    public void The_hdr_calibration_link_opens_the_store_page_when_the_apps_catalog_has_no_entry()
    {
        var cards = new List<DeviceCard> { MonitorWithCalibrationNote() };
        VendorEnricher.FixHdrCalibrationLinks(cards, inAppsCatalog: false);
        var note = Assert.Single(cards[0].Notes);
        Assert.Null(note.Link); // no dead end to «Приложения»
        Assert.Contains("откалибруйте", note.Text);
        var open = Assert.Single(cards[0].VendorApps);
        Assert.Equal("ОТКРЫТЬ КАЛИБРОВКУ HDR", open.ButtonText);
        Assert.True(open.CanLaunch);
        Assert.Equal("ms-windows-store://pdp/?productid=9N7F2SM5D1LR", VendorApps.HdrCalibrationStorePage);
    }

    [Fact]
    public void The_hdr_calibration_link_is_kept_when_the_apps_catalog_can_install_it()
    {
        var cards = new List<DeviceCard> { MonitorWithCalibrationNote() };
        VendorEnricher.FixHdrCalibrationLinks(cards, inAppsCatalog: true);
        Assert.NotNull(Assert.Single(cards[0].Notes).Link);
        Assert.Empty(cards[0].VendorApps);
    }
}

/// <summary>The build registers the vendor enrichment (DeviceProviders.Vendors.cs).</summary>
[Collection("Registry")] // DeviceProviders.OverrideForTests elsewhere is process-wide
public sealed class VendorRegistrationTests
{
    [Fact]
    public void The_vendor_enricher_runs_after_the_providers()
    {
        var e = Assert.Single(DeviceProviders.Enrichers, x => x is VendorEnricher);
        Assert.Equal("программы производителей", e.Name);
    }
}
