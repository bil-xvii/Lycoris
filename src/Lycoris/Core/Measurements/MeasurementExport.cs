using System.Globalization;
using System.Text;

namespace Lycoris.Core.Measurements;

/// <summary>Exports saved measurements without collecting or changing their values.</summary>
public static class MeasurementExport
{
    public static string BuildCsv(IReadOnlyList<Measurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);
        // Match the history builder's stable ascending sort followed by reverse traversal, including equal dates.
        var ordered = measurements.OrderBy(m => m.RecordedAt).ToArray();
        var explanations = MeasurementHistory.Build(ordered, MeasurementStore.MaxRecords);
        var newest = ordered.Reverse().Take(MeasurementStore.MaxRecords).ToArray();
        var csv = new StringBuilder("RecordedAtIso8601,WindowsVersion,WindowsBuild,LycorisVersion,EnabledStartupItems,FreeMemoryBytes,BootTimeSeconds,BootTimeSource,ChangesApplied,ChangesReverted,Comparison\r\n");
        for (var i = 0; i < newest.Length; i++)
        {
            var m = newest[i];
            var c = m.Conditions;
            string[] cells = [m.RecordedAt.ToString("O", CultureInfo.InvariantCulture),
                SafeText(c.WindowsVersion), SafeText(c.WindowsBuild), SafeText(c.LycorisVersion),
                Number(c.EnabledStartupItems), Number(c.FreeMemoryBytes), Number(c.BootTimeSeconds),
                SafeText(c.BootTimeSource), Number(m.ChangesApplied), Number(m.ChangesReverted), SafeText(explanations[i].Change)];
            csv.AppendJoin(',', cells.Select(Quote)).Append("\r\n");
        }
        return csv.ToString();
    }

    private static string Number<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? "";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string SafeText(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        return (value.Length > 0 && value[0] is '\t' or '\r' or '\n')
            || (!trimmed.IsEmpty && trimmed[0] is '=' or '+' or '-' or '@' or '＝' or '＋' or '－' or '＠')
            ? "'" + value : value;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
