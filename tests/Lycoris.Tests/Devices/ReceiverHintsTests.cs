using Lycoris.Modules.Devices.Vendors;

namespace Lycoris.Tests.Devices;

/// <summary>Receiver names from the PID table and the honest cable-history hint (devices_vendors.md §2.5).</summary>
public sealed class ReceiverHintsTests
{
    [Theory]
    [InlineData("C539")]
    [InlineData("C53A")]
    [InlineData("C53D")]
    [InlineData("C53F")]
    [InlineData("C541")]
    [InlineData("C545")]
    [InlineData("C547")]
    [InlineData("C54D")]
    [InlineData("c54d")]
    public void Lightspeed_receivers_are_named(string pid)
    {
        Assert.Equal("LIGHTSPEED-приёмник Logitech", ReceiverHints.Name("046D", pid));
        Assert.True(ReceiverHints.IsLightspeed("046d", pid));
        Assert.True(ReceiverHints.IsLogitechGamingMouse("046D", pid));
    }

    [Fact]
    public void Bolt_and_unifying_receivers_are_named_but_are_not_gaming()
    {
        Assert.Equal("Приёмник Logi Bolt", ReceiverHints.Name("046D", "C548"));
        Assert.Equal("Приёмник Unifying", ReceiverHints.Name("046D", "C52B"));
        Assert.Equal("Приёмник Unifying", ReceiverHints.Name("046D", "C532"));
        Assert.False(ReceiverHints.IsLogitechGamingMouse("046D", "C548"));
        Assert.False(ReceiverHints.IsLogitechGamingMouse("046D", "C52B"));
    }

    [Theory]
    [InlineData("046D", "C339")] // PRO Gaming Keyboard
    [InlineData("046D", "C231")] // G HUB virtual mouse
    [InlineData("1532", "C54D")] // same PID, another vendor
    [InlineData(null, "C54D")]
    [InlineData("046D", null)]
    public void Anything_else_is_not_a_receiver(string? vid, string? pid)
    {
        Assert.Null(ReceiverHints.Name(vid, pid));
        Assert.False(ReceiverHints.IsReceiver(vid, pid));
    }

    [Theory]
    [InlineData("C0A8", true)]  // PRO X2 SUPERSTRIKE
    [InlineData("C08B", true)]  // G502 SE HERO
    [InlineData("C07D", true)]
    [InlineData("C339", false)] // keyboard
    [InlineData("AB39", false)] // bootloader
    [InlineData("C077", false)] // office mouse
    public void Logitech_gaming_mice_are_lightspeed_or_wired_hidpp20(string pid, bool gaming) =>
        Assert.Equal(gaming, ReceiverHints.IsLogitechGamingMouse("046D", pid));

    [Fact]
    public void History_text_is_a_hint_not_a_claim()
    {
        var text = ReceiverHints.HistoryText(" PRO X2 SUPERSTRIKE ");
        Assert.Equal("раньше по кабелю подключалась: PRO X2 SUPERSTRIKE", text);
        Assert.DoesNotContain("за приёмником", text);
    }

    [Theory]
    [InlineData("USB Receiver", true)]
    [InlineData(" usb receiver ", true)]
    [InlineData("Wireless Controller", true)]
    [InlineData("Gaming Keyboard", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    [InlineData("PRO Gaming Keyboard", false)]
    [InlineData("LIGHTSPEED Receiver", false)]
    [InlineData("Xbox Wireless Controller", false)]
    public void Generic_product_strings_are_exact_matches(string? name, bool generic) =>
        Assert.Equal(generic, ReceiverHints.IsGenericProductName(name));
}
