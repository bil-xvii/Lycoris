using Lycoris.Core.Measurements;
using System.IO;

namespace Lycoris.Tests.Core.Measurements;

public sealed class MeasurementTests
{
    [Fact]
    public void Store_RoundTripsRecords()
    {
        using var dir = new TemporaryDirectory();
        var store = new MeasurementStore(Path.Combine(dir.Path, "measurements.json"));
        var measurement = Sample();

        Assert.True(store.Save(measurement));
        Assert.Equal(measurement, Assert.Single(store.Read()));
    }

    [Fact]
    public void Store_TreatsCorruptFileAsEmptyAndCanReplaceItOnExplicitSave()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "measurements.json");
        File.WriteAllText(path, "{ broken");
        var store = new MeasurementStore(path);

        Assert.Empty(store.Read());
        var row = Sample();
        Assert.True(store.Save(row));
        Assert.Equal(row, Assert.Single(store.Read()));
    }

    [Fact]
    public void Store_KeepsOnlyNewestFiftyRecords()
    {
        using var dir = new TemporaryDirectory();
        var store = new MeasurementStore(Path.Combine(dir.Path, "measurements.json"));
        for (var i = 0; i < 55; i++)
            Assert.True(store.Save(Sample(DateTimeOffset.UnixEpoch.AddMinutes(i))));

        var records = store.Read();
        Assert.Equal(50, records.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(5), records[0].RecordedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddMinutes(54), records[^1].RecordedAt);
    }

    [Fact]
    public void Comparer_MarksDifferentConditionsIncomparable()
    {
        var before = Sample();
        var after = Sample() with { Conditions = Sample().Conditions with { WindowsBuild = "different", EnabledStartupItems = 7 } };

        var result = MeasurementComparer.Compare(before, after);

        Assert.True(result.Incomparable);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Equal(2, result.EnabledStartupItems.Delta);
    }

    [Fact]
    public void Comparer_ReturnsDeltasAndSpreadForAtLeastThreeRepeats()
    {
        var before = Sample();
        var after = Sample() with { Conditions = Sample().Conditions with { EnabledStartupItems = 8, FreeMemoryBytes = 120, BootTimeSeconds = 12 } };
        Measurement[] a = [Sample(values: (2, 100, 10)), Sample(values: (3, 110, 11)), Sample(values: (4, 120, 12))];
        Measurement[] b = [Sample(values: (5, 140, 14)), Sample(values: (6, 150, 15)), Sample(values: (7, 160, 16))];

        var result = MeasurementComparer.Compare(before, after, a, b);

        Assert.False(result.Incomparable);
        Assert.Equal(3, result.EnabledStartupItems.Delta);
        Assert.Equal(1, result.EnabledStartupItems.MinDelta);
        Assert.Equal(5, result.EnabledStartupItems.MaxDelta);
        Assert.Equal(20, result.FreeMemoryBytes.Delta);
        Assert.Equal(2, result.BootTimeSeconds.Delta);
    }

    private static Measurement Sample(DateTimeOffset? time = null, (int Startup, long Memory, double Boot)? values = null)
    {
        var v = values ?? (5, 100L, 10d);
        return new Measurement
        {
            RecordedAt = time ?? DateTimeOffset.UtcNow,
            Conditions = new MeasurementConditions
            {
                WindowsVersion = "Windows 11", WindowsBuild = "26100", LycorisVersion = "1.2.3",
                EnabledStartupItems = v.Startup, FreeMemoryBytes = v.Memory, BootTimeSeconds = v.Boot,
                BootTimeSource = "Windows"
            }
        };
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lycoris-measure-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
