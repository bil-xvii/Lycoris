using System.Globalization;
using System.IO;
using Lycoris.Core.Measurements;
using Lycoris.Modules.Dashboard;
using Lycoris.Modules.Home;
using Microsoft.VisualBasic.FileIO;

namespace Lycoris.Tests.Core.Measurements;

[Collection("HomeLifecycle")]
public sealed class MeasurementExportTests
{
    private static Measurement Sample(int i = 0) => new()
    {
        RecordedAt = new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.FromHours(2)).AddMinutes(i),
        Conditions = new MeasurementConditions
        {
            WindowsVersion = "Windows 11", WindowsBuild = "26100", LycorisVersion = "1.0",
            EnabledStartupItems = i, FreeMemoryBytes = 1234567890, BootTimeSeconds = 12.25, BootTimeSource = "Event log",
        },
        ChangesApplied = 0, ChangesReverted = 0,
    };

    private static List<string[]> Parse(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv))
        {
            TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        Assert.All(rows, row => Assert.Equal(11, row.Length));
        return rows;
    }

    [Fact]
    public void All_fifty_records_export_newest_first_including_equal_dates()
    {
        var all = Enumerable.Range(0, 50).Select(Sample).ToArray();
        all[49] = all[49] with { RecordedAt = all[48].RecordedAt };
        var rows = Parse(MeasurementExport.BuildCsv(all));
        Assert.Equal(51, rows.Count);
        Assert.Equal("RecordedAtIso8601", rows[0][0]);
        Assert.Equal("49", rows[1][4]);
        Assert.Equal("48", rows[2][4]);
        Assert.Equal(all[49].RecordedAt.ToString("O", CultureInfo.InvariantCulture), rows[1][0]);
        var explanations = MeasurementHistory.Build(all, 50);
        Assert.Equal(explanations.Select(x => x.Change), rows.Skip(1).Select(x => x[10]));
        Assert.Equal("0", rows[^1][4]);
    }

    [Theory]
    [InlineData("ru-RU")]
    [InlineData("en-US")]
    public void Numeric_and_date_cells_are_invariant_and_null_is_distinct_from_zero(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        using var language = Loc.UseLanguage(culture.StartsWith("ru") ? "ru" : "en");
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var known = Sample() with { Conditions = Sample().Conditions with { FreeMemoryBytes = 0, BootTimeSeconds = 0 } };
            var missing = Sample(1) with
            {
                Conditions = Sample(1).Conditions with { FreeMemoryBytes = null, BootTimeSeconds = null },
                ChangesApplied = null, ChangesReverted = null,
            };
            var rows = Parse(MeasurementExport.BuildCsv([known, missing, Sample(2)]));
            Assert.Equal("12.25", rows[1][6]);
            Assert.Equal("1234567890", rows[1][5]);
            Assert.Equal("", rows[2][5]);
            Assert.Equal("", rows[2][6]);
            Assert.Equal("", rows[2][8]);
            Assert.Equal("", rows[2][9]);
            Assert.Equal("0", rows[3][5]);
            Assert.Equal("0", rows[3][6]);
            Assert.Equal("0", rows[3][8]);
            Assert.Equal("0", rows[3][9]);
            Assert.EndsWith("+02:00", rows[1][0]);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Theory]
    [InlineData("=SUM(1,2)")]
    [InlineData(" +1")]
    [InlineData("\t-1")]
    [InlineData("\r\n@formula")]
    [InlineData("＝1")]
    [InlineData(" ＋1")]
    [InlineData("－1")]
    [InlineData("＠formula")]
    [InlineData("\ttext")]
    [InlineData("\rtext")]
    [InlineData("\ntext")]
    public void Untrusted_string_cells_are_protected_against_spreadsheet_formulas(string payload)
    {
        var sample = Sample() with { Conditions = Sample().Conditions with
        {
            WindowsVersion = payload, WindowsBuild = payload, LycorisVersion = payload, BootTimeSource = payload,
        } };
        var row = Parse(MeasurementExport.BuildCsv([sample]))[1];
        foreach (var column in new[] { 1, 2, 3, 7 }) Assert.Equal("'" + payload, row[column]);
    }

    [Fact]
    public void Csv_roundtrips_commas_quotes_and_multiline_text()
    {
        const string text = "Windows, \"quoted\"\r\nsecond line";
        var sample = Sample() with { Conditions = Sample().Conditions with { WindowsVersion = text } };
        var csv = MeasurementExport.BuildCsv([sample]);
        Assert.Contains("\"Windows, \"\"quoted\"\"\r\nsecond line\"", csv);
        Assert.EndsWith("\r\n", csv);
        Assert.Equal(text, Parse(csv)[1][1]);
    }

    [Fact]
    public void Export_reuses_cautious_noise_and_incomparable_explanations()
    {
        using var language = Loc.UseLanguage("ru");
        var baseline = Sample();
        var noise = Sample(1) with { Conditions = baseline.Conditions };
        var different = Sample(2) with { Conditions = baseline.Conditions with { WindowsBuild = "99999" } };
        var rows = Parse(MeasurementExport.BuildCsv([baseline, noise, different]));
        Assert.Contains("несопоставимо", rows[1][10]);
        Assert.Contains("в пределах погрешности", rows[2][10]);
        Assert.Contains("сравнивать пока не с чем", rows[3][10]);
        Assert.Single(Parse(MeasurementExport.BuildCsv([])));
    }

    [Fact]
    public void Export_is_disabled_without_measurements_and_for_snapshot_demo_history()
    {
        var home = new HomeViewModel(new DashboardViewModel());
        Assert.False(home.ExportMeasurementsCommand.CanExecute(null));
        var canExecuteChanges = 0;
        home.ExportMeasurementsCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;
        home.HasMeasurements = true;
        Assert.Equal(1, canExecuteChanges);
        Assert.True(home.ExportMeasurementsCommand.CanExecute(null));
        var previous = HomeViewModel.DemoMeasurements;
        try
        {
            HomeViewModel.DemoMeasurements = [Sample()];
            Assert.False(home.ExportMeasurementsCommand.CanExecute(null));
        }
        finally { HomeViewModel.DemoMeasurements = previous; }
        home.HasMeasurements = false;
        Assert.Equal(2, canExecuteChanges);
        Assert.False(home.ExportMeasurementsCommand.CanExecute(null));
    }
}
