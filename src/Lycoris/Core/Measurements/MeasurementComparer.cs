namespace Lycoris.Core.Measurements;

public sealed record MeasurementDelta(double? Before, double? After, double? Delta, double? MinDelta, double? MaxDelta);

public sealed record MeasurementComparison(bool Incomparable, string? Reason,
    MeasurementDelta EnabledStartupItems, MeasurementDelta FreeMemoryBytes, MeasurementDelta BootTimeSeconds);

public static class MeasurementComparer
{
    public static MeasurementComparison Compare(Measurement before, Measurement after,
        IReadOnlyCollection<Measurement>? beforeRepeats = null, IReadOnlyCollection<Measurement>? afterRepeats = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = before.Conditions;
        var b = after.Conditions;
        string? reason = null;
        if (!string.Equals(a.WindowsVersion, b.WindowsVersion, StringComparison.Ordinal) || !string.Equals(a.WindowsBuild, b.WindowsBuild, StringComparison.Ordinal))
            reason = "Разная версия или сборка Windows";
        else if (!string.Equals(a.LycorisVersion, b.LycorisVersion, StringComparison.Ordinal))
            reason = "Разная версия Lycoris";
        else if (!string.Equals(a.BootTimeSource, b.BootTimeSource, StringComparison.Ordinal))
            reason = "Разные источники времени загрузки";

        return new MeasurementComparison(reason is not null, reason,
            Build(a.EnabledStartupItems, b.EnabledStartupItems,
                beforeRepeats?.Select(x => (double?)x.Conditions.EnabledStartupItems), afterRepeats?.Select(x => (double?)x.Conditions.EnabledStartupItems)),
            Build(a.FreeMemoryBytes, b.FreeMemoryBytes,
                beforeRepeats?.Select(x => (double?)x.Conditions.FreeMemoryBytes), afterRepeats?.Select(x => (double?)x.Conditions.FreeMemoryBytes)),
            Build(a.BootTimeSeconds, b.BootTimeSeconds,
                beforeRepeats?.Select(x => x.Conditions.BootTimeSeconds), afterRepeats?.Select(x => x.Conditions.BootTimeSeconds)));
    }

    private static MeasurementDelta Build(double? before, double? after, IEnumerable<double?>? beforeSamples, IEnumerable<double?>? afterSamples)
    {
        var delta = before.HasValue && after.HasValue ? after - before : null;
        var deltas = beforeSamples?.ToArray();
        var afterValues = afterSamples?.ToArray();
        if (deltas is null || afterValues is null || deltas.Length < 3 || afterValues.Length < 3)
            return new MeasurementDelta(before, after, delta, null, null);

        var pairDeltas = deltas.Where(x => x.HasValue).SelectMany(x => afterValues.Where(y => y.HasValue).Select(y => y!.Value - x!.Value)).ToArray();
        return pairDeltas.Length >= 3
            ? new MeasurementDelta(before, after, delta, pairDeltas.Min(), pairDeltas.Max())
            : new MeasurementDelta(before, after, delta, null, null);
    }
}
