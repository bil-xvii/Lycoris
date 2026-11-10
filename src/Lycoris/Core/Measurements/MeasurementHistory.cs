namespace Lycoris.Core.Measurements;

using Lycoris.Core.Localization;

/// <summary>One line of the measurement history: date · values · change against the previous (older) measurement.</summary>
public sealed record MeasurementHistoryRow(DateTimeOffset RecordedAt, string When, string Values, string Change, bool WithinNoise, bool Incomparable);

/// <summary>
/// Turns the stored measurements into a newest-first history. The wording is deliberately cautious: a difference
/// smaller than the usual run-to-run spread is reported as noise, never as an improvement.
/// </summary>
public static class MeasurementHistory
{
    /// <summary>How many rows the page shows (the store itself keeps up to <see cref="MeasurementStore.MaxRecords"/>).</summary>
    public const int DefaultRows = 20;

    /// <summary>Free memory sampled at one moment moves by hundreds of MB on its own: at least 256 MB or 5 %.</summary>
    public static double MemoryNoiseBytes(long before) => Math.Max(256d * 1024 * 1024, Math.Abs(before) * 0.05);

    /// <summary>Boot duration varies from boot to boot: at least 2 s or 10 %.</summary>
    public static double BootNoiseSeconds(double before) => Math.Max(2d, Math.Abs(before) * 0.10);

    /// <summary>Counts journal changes applied and reverted after <paramref name="since"/> up to <paramref name="until"/>.</summary>
    public static (int Applied, int Reverted) CountJournalChanges(IEnumerable<ChangeRecord> records, DateTimeOffset since, DateTimeOffset until)
    {
        ArgumentNullException.ThrowIfNull(records);
        var from = since.UtcDateTime;
        var to = until.UtcDateTime;
        var applied = 0;
        var reverted = 0;
        foreach (var r in records)
        {
            if (r.TimestampUtc > from && r.TimestampUtc <= to) applied++;
            if (r.RevertedUtc is { } rv && rv > from && rv <= to) reverted++;
        }
        return (applied, reverted);
    }

    public static IReadOnlyList<MeasurementHistoryRow> Build(IReadOnlyList<Measurement> measurements, int maxRows = DefaultRows)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        var ordered = measurements.OrderBy(x => x.RecordedAt).ToArray();
        var rows = new List<MeasurementHistoryRow>();
        for (var i = ordered.Length - 1; i >= 0 && rows.Count < Math.Max(0, maxRows); i--)
            rows.Add(BuildRow(ordered[i], i > 0 ? ordered[i - 1] : null));
        return rows;
    }

    public static string ValuesText(MeasurementConditions c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return Loc.Tf("автозапуск {0}", c.EnabledStartupItems)
            + " · " + (c.FreeMemoryBytes is { } m ? Loc.Tf("свободно {0} МБ", Mb(m)) : Loc.T("память недоступна"))
            + " · " + (c.BootTimeSeconds is { } s ? Loc.Tf("загрузка {0} с", s.ToString("0.0", Loc.Culture)) : Loc.T("загрузка недоступна"));
    }

    private static MeasurementHistoryRow BuildRow(Measurement current, Measurement? previous)
    {
        var when = Loc.DateTimeText(current.RecordedAt.ToLocalTime().DateTime);
        var values = ValuesText(current.Conditions);
        if (previous is null)
            return new(current.RecordedAt, when, values, Loc.T("первый замер в истории — сравнивать пока не с чем"), false, false);

        var parts = new List<string>();
        if (current.ChangesApplied is { } applied)
        {
            var reverted = current.ChangesReverted ?? 0;
            parts.Add(applied == 0 && reverted == 0
                ? Loc.T("Lycoris между замерами ничего не менял")
                : reverted == 0
                    ? Loc.N(applied, "между замерами применено {0} изменение", "между замерами применено {0} изменения", "между замерами применено {0} изменений")
                    : Loc.Tf("между замерами применено {0}, откачено {1}", applied, reverted));
        }

        var comparison = MeasurementComparer.Compare(previous, current);
        if (comparison.Incomparable)
        {
            parts.Add(Loc.Tf("несопоставимо: {0}", comparison.Reason ?? ""));
            return new(current.RecordedAt, when, values, string.Join(" · ", parts), false, true);
        }

        var significant = new List<string>();
        if (comparison.EnabledStartupItems.Delta is { } ds && ds != 0)
            significant.Add(Loc.Tf("автозапуск {0}", Signed((int)ds)));
        if (previous.Conditions.FreeMemoryBytes is { } mb && comparison.FreeMemoryBytes.Delta is { } dm && Math.Abs(dm) > MemoryNoiseBytes(mb))
            significant.Add(dm > 0
                ? Loc.Tf("свободной памяти больше на {0} МБ", Mb((long)dm))
                : Loc.Tf("свободной памяти меньше на {0} МБ", Mb((long)-dm)));
        string? bootNote = null;
        if (previous.Conditions.BootTimeSeconds is { } bb && comparison.BootTimeSeconds.Delta is { } db)
        {
            if (db == 0)
                bootNote = Loc.T("время загрузки то же — перезагрузки, вероятно, не было");
            else if (Math.Abs(db) > BootNoiseSeconds(bb))
                significant.Add(db < 0
                    ? Loc.Tf("загрузка быстрее на {0} с", (-db).ToString("0.0", Loc.Culture))
                    : Loc.Tf("загрузка медленнее на {0} с", db.ToString("0.0", Loc.Culture)));
        }

        var withinNoise = significant.Count == 0;
        if (withinNoise) parts.Add(Loc.T("разница в пределах погрешности"));
        else parts.AddRange(significant);
        if (bootNote is not null) parts.Add(bootNote);
        return new(current.RecordedAt, when, values, string.Join(" · ", parts), withinNoise, false);
    }

    private static string Mb(long bytes) => (bytes / 1024 / 1024).ToString("N0", Loc.Culture);

    private static string Signed(int value) => value > 0 ? "+" + value.ToString(Loc.Culture) : "−" + (-value).ToString(Loc.Culture);
}
