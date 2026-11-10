using Lycoris.Core;
using Lycoris.Core.Localization;
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
        // The unreadable history is kept aside, byte for byte, not overwritten.
        Assert.NotNull(store.PreservedCorruptFile);
        Assert.Equal("{ broken", File.ReadAllText(store.PreservedCorruptFile!));
    }

    [Fact]
    public void Store_ParallelSavesKeepEveryRecord()
    {
        using var dir = new TemporaryDirectory();
        var store = new MeasurementStore(Path.Combine(dir.Path, "measurements.json"));
        Parallel.For(0, 20, i => Assert.True(store.Save(Sample(DateTimeOffset.UnixEpoch.AddMinutes(i)))));
        Assert.Equal(20, store.Read().Count);
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

    [Fact]
    public void Collector_UsesInjectedReadOnlySourcesAndDoesNotPersist()
    {
        var collector = new MeasurementCollector(
            new FakeBoot((12.5, "test log")), new FakeMemory(2048), new FakeStartup(4), new FakeEnvironment(("Windows 11", "26100", "1.2.3")));

        var result = collector.Collect();

        Assert.Equal("Windows 11", result.Conditions.WindowsVersion);
        Assert.Equal("26100", result.Conditions.WindowsBuild);
        Assert.Equal("1.2.3", result.Conditions.LycorisVersion);
        Assert.Equal(4, result.Conditions.EnabledStartupItems);
        Assert.Equal(2048, result.Conditions.FreeMemoryBytes);
        Assert.Equal(12.5, result.Conditions.BootTimeSeconds);
        Assert.Equal("test log", result.Conditions.BootTimeSource);
    }

    [Fact]
    public void Collector_PreservesUnavailableBootReason()
    {
        var collector = new MeasurementCollector(
            new FakeBoot((null, "недоступно")), new FakeMemory(null), new FakeStartup(0), new FakeEnvironment(("Windows", "build", "version")));

        var result = collector.Collect();

        Assert.Null(result.Conditions.BootTimeSeconds);
        Assert.Equal("недоступно", result.Conditions.BootTimeSource);
        Assert.Null(result.Conditions.FreeMemoryBytes);
    }

    [Fact]
    public void Store_ReadsFilesWrittenBeforeHistoryFieldsExisted()
    {
        using var dir = new TemporaryDirectory();
        var path = Path.Combine(dir.Path, "measurements.json");
        // Exact shape written by the previous build: no ChangesApplied / ChangesReverted.
        File.WriteAllText(path, """
            [
              {
                "Id": "6f9619ff-8b86-d011-b42d-00c04fc964ff",
                "RecordedAt": "2026-11-01T10:00:00+00:00",
                "Conditions": {
                  "WindowsVersion": "Windows 11 Pro", "WindowsBuild": "26100", "LycorisVersion": "1.0.22.0",
                  "EnabledStartupItems": 9, "FreeMemoryBytes": 8589934592, "BootTimeSeconds": 31.4,
                  "BootTimeSource": "Microsoft-Windows-Diagnostics-Performance/Operational / ID 100 BootDuration"
                }
              }
            ]
            """);
        var store = new MeasurementStore(path);

        var old = Assert.Single(store.Read());
        Assert.Equal(9, old.Conditions.EnabledStartupItems);
        Assert.Null(old.ChangesApplied);
        Assert.Null(old.ChangesReverted);

        // A new record is appended next to the old one; the old one stays readable and unchanged.
        var next = Sample(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero)) with { ChangesApplied = 3, ChangesReverted = 1 };
        Assert.True(store.Save(next));
        var rows = store.Read();
        Assert.Equal(2, rows.Count);
        Assert.Equal(old, rows[0]);
        Assert.Equal(next, rows[1]);

        // A row whose record predates the journal counts simply has no journal note.
        using var _ = Loc.UseLanguage("ru");
        var history = MeasurementHistory.Build([old, Sample(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero))]);
        Assert.DoesNotContain("между замерами", history[0].Change);
    }

    [Fact]
    public void Store_RejectsNegativeJournalCounts()
    {
        using var dir = new TemporaryDirectory();
        var store = new MeasurementStore(Path.Combine(dir.Path, "measurements.json"));
        Assert.Throws<ArgumentException>(() => store.Save(Sample() with { ChangesApplied = -1 }));
    }

    [Fact]
    public void History_IsNewestFirstAndBounded()
    {
        var rows = Enumerable.Range(0, 30).Select(i => Sample(DateTimeOffset.UnixEpoch.AddDays(i))).Reverse().ToArray();

        var history = MeasurementHistory.Build(rows);

        Assert.Equal(MeasurementHistory.DefaultRows, history.Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(29), history[0].RecordedAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddDays(10), history[^1].RecordedAt);
        Assert.True(history.Zip(history.Skip(1)).All(p => p.First.RecordedAt > p.Second.RecordedAt));
        Assert.Equal(3, MeasurementHistory.Build(rows, 3).Count);
    }

    [Fact]
    public void History_OldestRowSaysThereIsNothingToCompare()
    {
        using var _ = Loc.UseLanguage("ru");
        var row = Assert.Single(MeasurementHistory.Build([Sample()]));
        Assert.Contains("сравнивать пока не с чем", row.Change);
        Assert.False(row.WithinNoise);
    }

    [Fact]
    public void History_SmallDifferencesAreReportedAsNoiseNotImprovement()
    {
        using var _ = Loc.UseLanguage("ru");
        const long gb8 = 8L * 1024 * 1024 * 1024;
        var before = Sample(DateTimeOffset.UnixEpoch, (5, gb8, 30));
        // +100 MB free memory and a 1 s faster boot are inside the run-to-run spread.
        var after = Sample(DateTimeOffset.UnixEpoch.AddHours(1), (5, gb8 + 100L * 1024 * 1024, 29));

        var row = MeasurementHistory.Build([before, after])[0];

        Assert.True(row.WithinNoise);
        Assert.Contains("в пределах погрешности", row.Change);
        Assert.DoesNotContain("быстрее", row.Change);
        Assert.DoesNotContain("больше", row.Change);
    }

    [Fact]
    public void History_ReportsDifferencesBeyondNoiseWithJournalCount()
    {
        using var _ = Loc.UseLanguage("ru");
        const long gb8 = 8L * 1024 * 1024 * 1024;
        var before = Sample(DateTimeOffset.UnixEpoch, (9, gb8, 40));
        var after = Sample(DateTimeOffset.UnixEpoch.AddHours(1), (6, gb8 + 2L * 1024 * 1024 * 1024, 30)) with { ChangesApplied = 4, ChangesReverted = 0 };

        var row = MeasurementHistory.Build([before, after])[0];

        Assert.False(row.WithinNoise);
        Assert.Contains("применено 4 изменения", row.Change);
        Assert.Contains("автозапуск −3", row.Change);
        Assert.Contains("свободной памяти больше на 2", row.Change);
        Assert.Contains("загрузка быстрее на 10", row.Change);
        Assert.DoesNotContain("погрешности", row.Change);
    }

    [Fact]
    public void History_SameBootTimeIsNotCountedAsAChange()
    {
        using var _ = Loc.UseLanguage("ru");
        var before = Sample(DateTimeOffset.UnixEpoch, (5, 100, 30));
        var after = Sample(DateTimeOffset.UnixEpoch.AddHours(1), (5, 100, 30)) with { ChangesApplied = 0, ChangesReverted = 0 };

        var row = MeasurementHistory.Build([before, after])[0];

        Assert.True(row.WithinNoise);
        Assert.Contains("ничего не менял", row.Change);
        Assert.Contains("в пределах погрешности", row.Change);
        Assert.Contains("перезагрузки, вероятно, не было", row.Change);
    }

    [Fact]
    public void History_IncomparableRowsDoNotClaimADifference()
    {
        using var _ = Loc.UseLanguage("ru");
        var before = Sample(DateTimeOffset.UnixEpoch, (9, 100, 40));
        var after = Sample(DateTimeOffset.UnixEpoch.AddHours(1), (2, 100, 20));
        after = after with { Conditions = after.Conditions with { WindowsBuild = "26200" } };

        var row = MeasurementHistory.Build([before, after])[0];

        Assert.True(row.Incomparable);
        Assert.Contains("несопоставимо", row.Change);
        Assert.DoesNotContain("быстрее", row.Change);
        Assert.DoesNotContain("автозапуск −", row.Change);
    }

    [Fact]
    public void History_EnglishWordingForNoise()
    {
        using var _ = Loc.UseLanguage("en");
        var row = MeasurementHistory.Build([Sample(DateTimeOffset.UnixEpoch), Sample(DateTimeOffset.UnixEpoch.AddHours(1), (5, 120, 10.5))])[0];
        Assert.Contains("within measurement noise", row.Change);
        Assert.Contains("MB free", row.Values);
    }

    [Fact]
    public void JournalCount_CountsOnlyChangesBetweenTheTwoMeasurements()
    {
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var t1 = t0.AddHours(2);
        ChangeRecord[] records =
        [
            new() { TimestampUtc = t0.AddMinutes(-5).UtcDateTime },                                   // before: not counted
            new() { TimestampUtc = t0.AddMinutes(10).UtcDateTime },
            new() { TimestampUtc = t0.AddMinutes(20).UtcDateTime, RevertedUtc = t0.AddMinutes(30).UtcDateTime },
            new() { TimestampUtc = t0.AddMinutes(-60).UtcDateTime, RevertedUtc = t0.AddMinutes(40).UtcDateTime },
            new() { TimestampUtc = t1.AddMinutes(1).UtcDateTime },                                    // after: not counted
        ];

        var (applied, reverted) = MeasurementHistory.CountJournalChanges(records, t0, t1);

        Assert.Equal(2, applied);
        Assert.Equal(2, reverted);
    }

    [Fact]
    public void NoiseThresholds_HaveSensibleFloors()
    {
        Assert.Equal(256d * 1024 * 1024, MeasurementHistory.MemoryNoiseBytes(1024));
        Assert.Equal(16L * 1024 * 1024 * 1024 * 0.05, MeasurementHistory.MemoryNoiseBytes(16L * 1024 * 1024 * 1024));
        Assert.Equal(2d, MeasurementHistory.BootNoiseSeconds(5));
        Assert.Equal(6d, MeasurementHistory.BootNoiseSeconds(60), 6);
    }

    private sealed class FakeBoot((double? Seconds, string Source) result) : IMeasurementBootSource
    {
        public (double? Seconds, string Source) Read() => result;
    }

    private sealed class FakeMemory(long? value) : IMeasurementMemorySource
    {
        public long? ReadFreeBytes() => value;
    }

    private sealed class FakeStartup(int value) : IMeasurementStartupSource
    {
        public int ReadEnabledCount() => value;
    }

    private sealed class FakeEnvironment((string Version, string Build, string LycorisVersion) value) : IMeasurementEnvironmentSource
    {
        public (string Version, string Build, string LycorisVersion) Read() => value;
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
