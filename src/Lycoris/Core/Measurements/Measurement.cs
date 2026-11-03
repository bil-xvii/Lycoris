namespace Lycoris.Core.Measurements;

/// <summary>One explicitly requested, local-only performance measurement.</summary>
public sealed record Measurement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
    public required MeasurementConditions Conditions { get; init; }
}

/// <summary>Environment and read-only values captured with a measurement.</summary>
public sealed record MeasurementConditions
{
    public required string WindowsVersion { get; init; }
    public required string WindowsBuild { get; init; }
    public required string LycorisVersion { get; init; }
    public required int EnabledStartupItems { get; init; }
    public long? FreeMemoryBytes { get; init; }
    public double? BootTimeSeconds { get; init; }
    /// <summary>Identifies where boot time came from; use "недоступно" when unavailable.</summary>
    public required string BootTimeSource { get; init; }
}
