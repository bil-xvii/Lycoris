using Lycoris.Modules.Drivers;

namespace Lycoris.Tests;

/// <summary>Code 32 «driver service disabled»: what can be restored, and that Hyper-V services stay a per-device decision.</summary>
public sealed class DriverServiceRestoreTests
{
    private static ProblemDeviceItem Device(int code, string service) =>
        new(new ProblemDeviceInfo("Device", @"ROOT\X\0000", "System", [@"ROOT\X"], code, true, "Microsoft", service));

    [Fact]
    public void Only_code_32_with_a_service_can_be_restored()
    {
        Assert.True(Device(32, "Serial").CanRestoreService);
        Assert.False(Device(32, "").CanRestoreService);
        Assert.False(Device(28, "Serial").CanRestoreService);
    }

    [Theory]
    [InlineData("vid", true)]
    [InlineData("hvservice", true)]
    [InlineData("vmbus", true)]
    [InlineData("CompositeBus", false)]
    [InlineData("Serial", false)]
    public void Hyper_v_services_are_recognised(string service, bool hyperV) =>
        Assert.Equal(hyperV, Device(32, service).IsHyperVService);

    [Fact]
    public void Code_badge_shows_the_number()
    {
        Assert.Equal(32, Device(32, "Serial").Code); // the badge binds Code directly (the Sentence converter blanked the number)
    }
}
