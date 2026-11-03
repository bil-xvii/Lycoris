using Lycoris.Modules.Devices.Network;

namespace Lycoris.Tests.Devices;

/// <summary>Drivers that restart the adapter mid-cmdlet fail the write although the value landed: the read-back decides.</summary>
public sealed class NicWriterConfirmTests
{
    [Fact]
    public void Value_that_appears_after_a_restart_counts_as_applied()
    {
        var reads = new Queue<string?>(new string?[] { null, null, "128", "96" });
        var slept = 0;
        Assert.True(NicWriter.WaitUntilApplied(() => reads.Dequeue(), "96", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), _ => slept++));
        Assert.Equal(3, slept);
    }

    [Fact]
    public void Value_that_never_changes_is_a_failure_after_the_timeout()
    {
        var slept = 0;
        Assert.False(NicWriter.WaitUntilApplied(() => "128", "96", TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), _ => slept++));
        Assert.Equal(3, slept);
    }
}
