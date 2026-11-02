using Monolith.Modules.Devices;
using Xunit;

namespace Monolith.Tests;

/// <summary>Obvious UI for «Устройства и сеть»: measurements run in place, the admin-intent value is validated.</summary>
public sealed class DevicesUxTests
{
    private static DeviceTool Tool(string id) => new() { Id = id, Title = "T", Run = (_, _) => Task.CompletedTask };

    private static DeviceCard CardWith(params DeviceSetting[] settings) => new()
    {
        Key = "net:1", Kind = DeviceKind.Network, Title = "Сеть",
        Settings = new System.Collections.ObjectModel.ObservableCollection<DeviceSetting>(settings),
    };

    [Fact]
    public void A_measurement_row_finds_its_tool_on_the_card_through_the_link()
    {
        var ping = Tool("net-ping");
        var card = CardWith();
        card.Tools.Add(ping);
        var setting = new DeviceSetting { Id = "x", Title = "x" };
        var link = new DeviceLink(DeviceLinkKind.Tool, "ЗАМЕРИТЬ", "net:1", "net-ping");

        Assert.Same(ping, DevicePlan.ToolOf(card, setting, link));
        Assert.Null(DevicePlan.ToolOf(card, setting, null));
        Assert.Null(DevicePlan.ToolOf(card, setting, new DeviceLink(DeviceLinkKind.Tool, "x", "net:1", "missing")));
    }

    [Fact]
    public void A_setting_with_its_own_tool_uses_it()
    {
        var own = Tool("dpi");
        var setting = new DeviceSetting { Id = "mouse-dpi", Title = "DPI", Tool = own };
        Assert.Same(own, DevicePlan.ToolOf(CardWith(setting), setting, null));
    }

    [Fact]
    public void Intent_values_are_accepted_only_when_the_row_offers_them()
    {
        var choice = new DeviceSetting
        {
            Id = "c", Title = "c", Control = SettingControl.Choice,
            Options = [new(DeviceValue.Of(1), "один"), new(DeviceValue.Of("fast"), "быстро")],
        };
        Assert.True(choice.TryParseValue("fast", out var v) && v == DeviceValue.Of("fast"));
        Assert.True(choice.TryParseValue("1", out v) && v == DeviceValue.Of(1));
        Assert.False(choice.TryParseValue("2", out _));

        var slider = new DeviceSetting { Id = "s", Title = "s", Control = SettingControl.Slider, Min = 1, Max = 20, Step = 1 };
        Assert.True(slider.TryParseValue("10", out v) && v == DeviceValue.Of(10));
        Assert.False(slider.TryParseValue("21", out _));
        Assert.False(slider.TryParseValue("abc", out _));

        var toggle = new DeviceSetting { Id = "t", Title = "t", Control = SettingControl.Toggle };
        Assert.True(toggle.TryParseValue("0", out v) && v == DeviceValue.Of(0));
        Assert.False(toggle.TryParseValue("5", out _));

        var dual = new DeviceSetting
        {
            Id = "d", Title = "d", Control = SettingControl.DualChoice,
            Options = [new(DeviceValue.Of("2560x1440"), "1440p")],
            SecondaryOptions = _ => [new(DeviceValue.Of(144), "144"), new(DeviceValue.Of(240), "240")],
        };
        Assert.True(dual.TryParseValue("2560x1440@240", out v) && v.Text == "2560x1440@240");
        Assert.False(dual.TryParseValue("2560x1440@999", out _));
        Assert.False(dual.TryParseValue("1920x1080@60", out _));

        var info = new DeviceSetting { Id = "i", Title = "i" };
        Assert.False(info.TryParseValue("1", out _));
    }
}
