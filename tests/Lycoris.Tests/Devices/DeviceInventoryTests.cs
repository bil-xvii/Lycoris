using Lycoris.Core.Devices;
using Lycoris.Modules.Devices.Vendors;

namespace Lycoris.Tests.Devices;

/// <summary>
/// The inventory grouping over synthetic device nodes modelled on the dev PC (devices_vendors.md §0.1): one group per physical
/// device, the right names and kinds, virtual / LampArray / hub / storage / radio nodes dropped, and no serial anywhere visible.
/// </summary>
public sealed class DeviceInventoryTests
{
    private const string KeyboardSerial = "088D34643436";
    private const string ReceiverSerial = "375C32603335";
    private const string MsiSerial = "7E4724092002";
    private const string Local = DeviceInventory.LocalMachineContainer;
    private const string KeyboardContainer = "{8E8EB054-4AD6-5D06-8200-85DAB151D7F6}";
    private const string ReceiverContainer = "{B7F3C0B6-30BE-52DE-AF39-C1A56D223E64}";
    private const string RootHub = @"USB\ROOT_HUB30\4&2A1B3C4D&0&0";
    private const string Pci = @"PCI\VEN_1022&DEV_43FC&SUBSYS_11421B21&REV_01\4&1";
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private static PnpNode N(string id, string? parent, string? container, string? cls = null, string? bus = null, string? friendly = null,
        string? service = null, bool present = true, DateTime? arrival = null, bool local = false, params string[] hwids) =>
        new(id, parent, container, local, bus, friendly, null, cls, service, present, arrival, hwids);

    /// <summary>Parents of a node from the ParentId links of the fixture (plus the root hub and the PCI controller).</summary>
    private static Func<string, IReadOnlyList<string>> Ancestors(IEnumerable<PnpNode> nodes)
    {
        var all = nodes.Concat([N(RootHub, Pci, Local, "USB", service: "USBHUB3", local: true), N(Pci, null, Local, "USB")])
            .GroupBy(n => n.InstanceId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return id =>
        {
            var chain = new List<string>();
            var current = id;
            while (all.TryGetValue(current, out var n) && n.ParentId is { } parent && chain.Count < 16)
            {
                chain.Add(parent);
                current = parent;
            }
            return chain;
        };
    }

    private static IReadOnlyList<PnpNode> Keyboard() =>
    [
        N($@"USB\VID_046D&PID_C339\{KeyboardSerial}", RootHub, KeyboardContainer, "USB", "PRO Gaming Keyboard", "PRO", "usbccgp"),
        N(@"USB\VID_046D&PID_C339&MI_00\7&1A2B3C4D&0&0000", $@"USB\VID_046D&PID_C339\{KeyboardSerial}", KeyboardContainer, "HIDClass", "PRO Gaming Keyboard", service: "HidUsb"),
        N(@"USB\VID_046D&PID_C339&MI_01\7&1A2B3C4D&0&0001", $@"USB\VID_046D&PID_C339\{KeyboardSerial}", KeyboardContainer, "HIDClass", "PRO Gaming Keyboard", service: "HidUsb"),
        N(@"HID\VID_046D&PID_C339&MI_00\8&2B3C4D5E&0&0000", @"USB\VID_046D&PID_C339&MI_00\7&1A2B3C4D&0&0000", KeyboardContainer, "Keyboard", service: "kbdhid"),
        N(@"HID\VID_046D&PID_C339&MI_01&COL01\8&3C4D5E6F&0&0000", @"USB\VID_046D&PID_C339&MI_01\7&1A2B3C4D&0&0001", KeyboardContainer, "Keyboard", service: "kbdhid"),
        N(@"HID\VID_046D&PID_C339&MI_01&COL02\8&3C4D5E6F&0&0001", @"USB\VID_046D&PID_C339&MI_01\7&1A2B3C4D&0&0001", KeyboardContainer, "HIDClass"),
        N(@"HID\VID_046D&PID_C339&MI_01&COL03\8&3C4D5E6F&0&0002", @"USB\VID_046D&PID_C339&MI_01\7&1A2B3C4D&0&0001", KeyboardContainer, "HIDClass"),
        N(@"HID\VID_046D&PID_C339&MI_01&COL04\8&3C4D5E6F&0&0003", @"USB\VID_046D&PID_C339&MI_01\7&1A2B3C4D&0&0001", KeyboardContainer, "HIDClass"),
        // G HUB's Dynamic Lighting child: its own container, a flag on the keyboard — not a device.
        N(@"USB\VID_046D&PID_C339&LAMPARRAY\9&1", $@"USB\VID_046D&PID_C339\{KeyboardSerial}", "{8ACD6CA4-D172-11F1-96E8-849E56C83CE8}", bus: "PRO"),
    ];

    private static IReadOnlyList<PnpNode> Receiver() =>
    [
        N($@"USB\VID_046D&PID_C54D\{ReceiverSerial}", RootHub, ReceiverContainer, "USB", "USB Receiver", "LIGHTSPEED Receiver", "usbccgp"),
        N(@"USB\VID_046D&PID_C54D&MI_00\7&5&0&0000", $@"USB\VID_046D&PID_C54D\{ReceiverSerial}", ReceiverContainer, "HIDClass", "USB Receiver"),
        N(@"HID\VID_046D&PID_C54D&MI_00\8&6&0&0000", @"USB\VID_046D&PID_C54D&MI_00\7&5&0&0000", ReceiverContainer, "Mouse", service: "mouhid"),
        N(@"USB\VID_046D&PID_C54D&MI_01\7&5&0&0001", $@"USB\VID_046D&PID_C54D\{ReceiverSerial}", ReceiverContainer, "HIDClass", "USB Receiver"),
        N(@"HID\VID_046D&PID_C54D&MI_01&COL01\8&7&0&0000", @"USB\VID_046D&PID_C54D&MI_01\7&5&0&0001", ReceiverContainer, "Keyboard", service: "kbdhid"),
        N(@"USB\VID_046D&PID_C54D&LAMPARRAY\9&2", $@"USB\VID_046D&PID_C54D\{ReceiverSerial}", "{8ACD6CA9-D172-11F1-96E8-849E56C83CE8}", bus: "LIGHTSPEED Receiver"),
        N(@"USB\VID_046D&PID_C54D&LAMPARRAY\9&3", $@"USB\VID_046D&PID_C54D\{ReceiverSerial}", "{8ACD6CAA-D172-11F1-96E8-849E56C83CE8}", bus: "LIGHTSPEED Receiver"),
    ];

    private static IReadOnlyList<PnpNode> Msi() =>
    [
        N($@"USB\VID_0DB0&PID_0076\{MsiSerial}", RootHub, Local, "HIDClass", "MYSTIC LIGHT", "USB-устройство ввода", "HidUsb", local: true),
        N(@"HID\VID_0DB0&PID_0076\8&9&0&0000", $@"USB\VID_0DB0&PID_0076\{MsiSerial}", Local, "HIDClass", local: true),
    ];

    private static IReadOnlyList<PnpNode> GHubVirtual() =>
    [
        N(@"HID\VID_046D&PID_C231\9&10&0&0000", @"LGHUBDevice\VID_046D&PID_C231\1", "{8600B879-D22F-11F1-96EE-849E56C83CE8}", "Mouse", service: "mouhid"),
        N(@"HID\VID_046D&PID_C232\9&11&0&0000", @"LGHUBDevice\VID_046D&PID_C232\1", "{8600B878-D22F-11F1-96EE-849E56C83CE8}", "Keyboard", service: "kbdhid"),
        N(@"LGHUBDevice\VID_046D&PID_C231\1", @"ROOT\LGHUBVIRTUALBUS\0000", "{8600B879-D22F-11F1-96EE-849E56C83CE8}", "HIDClass", service: "logi_joy_vir_hid"),
    ];

    private static IReadOnlyList<PnpNode> Noise() =>
    [
        N(RootHub, Pci, Local, "USB", service: "USBHUB3", local: true),
        N(@"USB\VID_05E3&PID_0608\6&2EB7ECB&0&1", RootHub, Local, "USB", "USB2.0 Hub", service: "USBHUB3", local: true),
        N(@"USB\VID_0781&PID_55A3\04015F0F", RootHub, "{6F305D2D-0AE5-5E96-A5C4-404792D7A9D6}", "SCSIAdapter", " SanDisk 3.2Gen1", service: "UASPStor"),
        N(@"USB\VID_0489&PID_E10A\000000000", RootHub, "{74FD64AD-D150-11F1-96E5-806E6F6E6963}", "Bluetooth", service: "BTHUSB"),
    ];

    private static IReadOnlyList<PnpNode> Inzone() =>
    [
        N(@"USB\VID_054C&PID_0F80\5&1", RootHub, "{74FD64AE-D150-11F1-96E5-806E6F6E6963}", "USB", "INZONE E9", "Составное USB устройство", "usbccgp"),
        N(@"USB\VID_054C&PID_0F80&MI_00\6&1&0000", @"USB\VID_054C&PID_0F80\5&1", "{74FD64AE-D150-11F1-96E5-806E6F6E6963}", "MEDIA", "INZONE E9", "INZONE E9", "INZONEHS"),
        N(@"USB\VID_054C&PID_0F80&MI_02\6&1&0002", @"USB\VID_054C&PID_0F80\5&1", "{74FD64AE-D150-11F1-96E5-806E6F6E6963}", "HIDClass", "INZONE E9"),
    ];

    private static IReadOnlyList<InventoryGroup> GroupAll(params IReadOnlyList<PnpNode>[] sets)
    {
        var nodes = sets.SelectMany(s => s).ToList();
        return DeviceInventory.Group(nodes, Ancestors(nodes));
    }

    [Fact]
    public void The_keyboards_nodes_in_one_container_form_one_group()
    {
        var g = Assert.Single(GroupAll(Keyboard()));
        Assert.Equal(("046D", "C339"), (g.Vid, g.Pid));
        Assert.Equal(8, g.InstanceIds.Count); // the LampArray child is a flag, not a member
        Assert.Equal(KeyboardContainer, g.ContainerId);
        Assert.Equal(InventoryKind.Keyboard, g.Kind);
        Assert.Equal("PRO Gaming Keyboard", g.Name); // the device's own string, not G HUB's INF name «PRO»
        Assert.Equal("Logitech", g.Vendor?.Name);
    }

    [Fact]
    public void The_msi_controller_in_the_local_machine_container_is_grouped_by_its_usb_parent()
    {
        var g = Assert.Single(GroupAll(Msi()));
        Assert.Equal(2, g.InstanceIds.Count);
        Assert.Null(g.ContainerId); // the shared local-machine container never matches anything
        Assert.Equal("MYSTIC LIGHT", g.Name);
        Assert.Equal(InventoryKind.Lighting, g.Kind);
        Assert.Equal("MSI", g.Vendor?.Name);
        Assert.Equal("Mystic Light (MSI)", DeviceInventory.DisplayTitle(g));
    }

    [Fact]
    public void Card_titles_keep_model_codes_and_name_the_vendor_of_controllers()
    {
        var groups = GroupAll(Keyboard(), Inzone(), Msi());
        Assert.Equal("PRO Gaming Keyboard", DeviceInventory.DisplayTitle(groups.Single(g => g.Pid == "C339")));
        Assert.Equal("INZONE E9", DeviceInventory.DisplayTitle(groups.Single(g => g.Pid == "0F80")));
        Assert.Equal("Mystic Light (MSI)", DeviceInventory.DisplayTitle(groups.Single(g => g.Pid == "0076")));
    }

    [Fact]
    public void G_hub_virtual_devices_are_dropped()
    {
        Assert.Empty(GroupAll(GHubVirtual()));
        var groups = GroupAll(GHubVirtual(), Keyboard());
        Assert.DoesNotContain(groups, g => g.Pid is "C231" or "C232");
    }

    [Fact]
    public void Lamparray_children_are_dropped_and_flag_their_device()
    {
        var groups = GroupAll(Keyboard(), Receiver(), Msi());
        Assert.DoesNotContain(groups.SelectMany(g => g.InstanceIds), id => id.Contains("LAMPARRAY", StringComparison.OrdinalIgnoreCase));
        Assert.True(groups.Single(g => g.Pid == "C339").HasLampArray);
        Assert.True(groups.Single(g => g.Pid == "C54D").HasLampArray);
        Assert.False(groups.Single(g => g.Pid == "0076").HasLampArray);
    }

    [Fact]
    public void Hubs_storage_and_the_bluetooth_radio_are_dropped()
    {
        Assert.Empty(GroupAll(Noise()));
        var groups = GroupAll(Noise(), Inzone());
        Assert.Equal(["054C"], groups.Select(g => g.Vid));
    }

    [Fact]
    public void A_generic_receiver_string_falls_through_to_the_receiver_table()
    {
        // Bus name «USB Receiver» is generic; G HUB's INF name «LIGHTSPEED Receiver» is not the device's own — the PID table wins.
        var g = Assert.Single(GroupAll(Receiver()));
        Assert.Equal("LIGHTSPEED-приёмник Logitech", g.Name);
        Assert.Equal(InventoryKind.Mouse, g.Kind); // the keyboard collection of a receiver does not make it a keyboard
    }

    [Fact]
    public void Kinds_follow_the_device_classes()
    {
        PnpNode[] pad =
        [
            N(@"USB\VID_045E&PID_0B12\3032363030", RootHub, "{11111111-2222-3333-4444-555555555555}", "XnaComposite", "Controller"),
            N(@"HID\VID_045E&PID_0B12&IG_00\9&1", @"USB\VID_045E&PID_0B12\3032363030", "{11111111-2222-3333-4444-555555555555}", "HIDClass",
                hwids: [@"HID\VID_045E&PID_0B12&IG_00", "HID_DEVICE_SYSTEM_GAME"]),
        ];
        PnpNode[] webcam = [N(@"USB\VID_046D&PID_085E\ABCDEF01", RootHub, "{22222222-2222-3333-4444-555555555555}", "Camera", "Logitech BRIO")];
        var groups = GroupAll(Keyboard(), Receiver(), Msi(), Inzone(), pad, webcam);
        Assert.Equal(InventoryKind.Keyboard, groups.Single(g => g.Pid == "C339").Kind);
        Assert.Equal(InventoryKind.Mouse, groups.Single(g => g.Pid == "C54D").Kind);
        Assert.Equal(InventoryKind.Lighting, groups.Single(g => g.Pid == "0076").Kind);
        Assert.Equal(InventoryKind.Audio, groups.Single(g => g.Pid == "0F80").Kind);
        Assert.Equal(InventoryKind.Controller, groups.Single(g => g.Pid == "0B12").Kind);
        Assert.Equal(InventoryKind.Other, groups.Single(g => g.Pid == "085E").Kind); // a webcam: matched, never a card here
    }

    [Fact]
    public void No_serial_or_instance_tail_is_in_any_card_key_or_name()
    {
        var groups = GroupAll(Keyboard(), Receiver(), Msi(), Inzone(), Noise(), GHubVirtual());
        foreach (var g in groups)
        {
            foreach (var serial in new[] { KeyboardSerial, ReceiverSerial, MsiSerial })
            {
                Assert.DoesNotContain(serial, g.CardKey, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(serial, g.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(serial, DeviceInventory.DisplayTitle(g), StringComparison.OrdinalIgnoreCase);
            }
            Assert.Matches("^periph:[0-9a-f]{12}$", g.CardKey);
        }
        // Stable across scans, different per device.
        Assert.Equal(GroupAll(Msi())[0].CardKey, GroupAll(Msi())[0].CardKey);
        Assert.Equal(groups.Count, groups.Select(g => g.CardKey).Distinct().Count());
    }

    // ───────────────────────────── Cable history ─────────────────────────────

    private static IReadOnlyList<PnpNode> History() =>
    [
        N(@"USB\VID_046D&PID_C0A8\1A2B3C", RootHub, "{A4B6AFDB-826E-5581-A137-AB64F075420C}", "USB", "PRO X2 SUPERSTRIKE", present: false, arrival: Now.AddDays(-1)),
        N(@"HID\VID_046D&PID_C0A8&MI_00\8&1", @"USB\VID_046D&PID_C0A8&MI_00\7&1", "{A4B6AFDB-826E-5581-A137-AB64F075420C}", "Mouse", present: false, arrival: Now.AddDays(-1)),
        N(@"HID\VID_046D&PID_C0A8&MI_01&COL01\8&2", @"USB\VID_046D&PID_C0A8&MI_01\7&2", "{A4B6AFDB-826E-5581-A137-AB64F075420C}", "Keyboard", present: false, arrival: Now.AddDays(-1)),
        // Firmware-update mode of the same mouse: never a «device».
        N(@"USB\VID_046D&PID_AB39\4D5E6F", RootHub, "{A18349D2-D235-11F1-96EE-849E56C83CE8}", "HIDClass", "Bootloader for X2 SUPERSTRIKE", present: false, arrival: Now.AddDays(-1)),
        N(@"HID\VID_046D&PID_AB39&COL01\9&1", @"USB\VID_046D&PID_AB39\4D5E6F", "{A18349D2-D235-11F1-96EE-849E56C83CE8}", "Mouse", present: false, arrival: Now.AddDays(-1)),
        // Too old.
        N(@"USB\VID_046D&PID_C08B\7A8B9C", RootHub, "{33333333-2222-3333-4444-555555555555}", "USB", "G502 SE HERO Gaming Mouse", present: false, arrival: Now.AddDays(-200)),
        N(@"HID\VID_046D&PID_C08B&MI_00\9&2", @"USB\VID_046D&PID_C08B&MI_00\8&3", "{33333333-2222-3333-4444-555555555555}", "Mouse", present: false, arrival: Now.AddDays(-200)),
        // Another vendor.
        N(@"USB\VID_1532&PID_0084\1", RootHub, "{44444444-2222-3333-4444-555555555555}", "USB", "Razer DeathAdder V2", present: false, arrival: Now.AddDays(-3)),
        N(@"HID\VID_1532&PID_0084&MI_00\9&3", @"USB\VID_1532&PID_0084&MI_00\8&4", "{44444444-2222-3333-4444-555555555555}", "Mouse", present: false, arrival: Now.AddDays(-3)),
    ];

    [Fact]
    public void History_lists_recent_named_mice_of_the_vendor_only()
    {
        var hints = DeviceInventory.History(History().Concat(Receiver()), "046D", InventoryKind.Mouse, Now);
        var h = Assert.Single(hints);
        Assert.Equal("PRO X2 SUPERSTRIKE", h.Name);
        Assert.Equal("C0A8", h.Pid);
    }

    [Fact]
    public void History_skips_devices_that_are_present_now()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "046D:C0A8" };
        Assert.Empty(DeviceInventory.History(History(), "046D", InventoryKind.Mouse, Now, present));
    }

    [Fact]
    public void History_window_is_180_days()
    {
        var hints = DeviceInventory.History(History(), "046D", InventoryKind.Mouse, Now.AddDays(-30));
        // Seen from 30 days earlier, the G502 (200 days before «now») is within the window; the PRO X2 is in the future → skipped.
        Assert.Equal(["G502 SE HERO Gaming Mouse"], hints.Select(x => x.Name));
    }
}
