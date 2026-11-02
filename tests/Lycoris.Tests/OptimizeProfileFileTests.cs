using System.Text;
using System.Text.Json;
using Lycoris.Modules.Optimize;

namespace Lycoris.Tests;

/// <summary>Optimize profile export/import: the file is untrusted input. Pure logic only.</summary>
public sealed class OptimizeProfileFileTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static ProfileFileData Ok(string json)
    {
        var d = OptimizeProfileFile.Parse(Utf8(json), out var error);
        Assert.Null(error);
        return Assert.IsType<ProfileFileData>(d);
    }

    private static string Fails(string json)
    {
        Assert.Null(OptimizeProfileFile.Parse(Utf8(json), out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var created = new DateTimeOffset(2026, 10, 26, 12, 30, 0, TimeSpan.Zero);
        var json = OptimizeProfileFile.Serialize(["b-tweak", "a-tweak", "b-tweak"], "Игровой", "1.2.3", created);

        using (var doc = JsonDocument.Parse(json))
        {
            Assert.Equal(OptimizeProfileFile.FormatTag, doc.RootElement.GetProperty("format").GetString());
            Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            Assert.Equal("2026-10-26T12:30:00Z", doc.RootElement.GetProperty("created").GetString());
        }

        var d = Ok(json);
        Assert.Equal(["a-tweak", "b-tweak"], d.Ids);
        Assert.Equal("Игровой", d.Name);
        Assert.Equal("1.2.3", d.AppVersion);
        Assert.Equal(created, d.Created);
        Assert.Equal(0, d.InvalidIds);
    }

    [Fact]
    public void Serialize_DropsMalformedIds()
    {
        var d = Ok(OptimizeProfileFile.Serialize(["ok", "Bad Id", "", "../x"], null, null, DateTimeOffset.UtcNow));
        Assert.Equal(["ok"], d.Ids);
        Assert.Null(d.Name);
    }

    [Fact]
    public void Parse_AcceptsBomAndMinimalFile()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[]}""")).ToArray();
        var d = OptimizeProfileFile.Parse(bytes, out var error);
        Assert.Null(error);
        Assert.NotNull(d);
        Assert.Empty(d!.Ids);
    }

    [Fact]
    public void Parse_AcceptsPreRenameFormatTag()
    {
        var data = OptimizeProfileFile.Parse(Utf8("""{"format":"monolith-optimize-profile","version":1,"tweaks":["old-profile"]}"""), out var error);
        Assert.Null(error);
        Assert.Equal(["old-profile"], Assert.IsType<ProfileFileData>(data).Ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""["a","b"]""")]
    [InlineData("""{"version":1,"tweaks":[]}""")]
    [InlineData("""{"format":"other","version":1,"tweaks":[]}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","tweaks":[]}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":"1","tweaks":[]}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":0,"tweaks":[]}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":2,"tweaks":[]}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":1}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":1,"tweaks":"a"}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],}""")]
    [InlineData("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[[[[["a"]]]]]}""")]
    public void Parse_RejectsMalformed(string json) => Fails(json);

    [Fact]
    public void Parse_RejectsOversizedAndInvalidUtf8()
    {
        Assert.Null(OptimizeProfileFile.Parse(new byte[OptimizeProfileFile.MaxBytes + 1], out var e1));
        Assert.NotNull(e1);
        Assert.Null(OptimizeProfileFile.Parse([0x7B, 0xFF, 0xFE, 0x7D], out var e2));
        Assert.NotNull(e2);
        Assert.Null(OptimizeProfileFile.Parse(null, out _));
    }

    [Fact]
    public void Parse_RejectsTooManyIds()
    {
        var ids = string.Join(",", Enumerable.Range(0, OptimizeProfileFile.MaxIds + 1).Select(i => $"\"t{i}\""));
        Fails($$"""{"format":"lycoris-optimize-profile","version":1,"tweaks":[{{ids}}]}""");
    }

    [Fact]
    public void Parse_CountsInvalidEntriesAndDedupes()
    {
        var d = Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":["a"," a ","A","a b",5,null,{},"x\\y","b"]}""");
        Assert.Equal(["a", "b"], d.Ids);
        Assert.Equal(6, d.InvalidIds);
    }

    [Fact]
    public void Parse_SanitizesOptionalFields()
    {
        var longName = new string('я', 200);
        var d = Ok($$"""{"format":"lycoris-optimize-profile","version":1,"name":"{{longName}}\u0007‮","appVersion":42,"created":"garbage","tweaks":[]}""");
        Assert.Equal(OptimizeProfileFile.MaxNameLength, d.Name!.Length);
        Assert.Null(d.AppVersion);
        Assert.Null(d.Created);

        Assert.Equal("Мой", OptimizeProfileFile.Clean(" Мой‮\u0000 ", 80));
        Assert.Null(OptimizeProfileFile.Clean("\u0001\u0002", 80));
    }

    [Fact]
    public void Classify_SplitsByReason()
    {
        ProfileCatalogEntry[] catalog =
        [
            new("safe", IsApplicable: true, NeedsManualPick: false),
            new("na", IsApplicable: false, NeedsManualPick: false),
            new("expert", IsApplicable: true, NeedsManualPick: true),
            new("expert-na", IsApplicable: false, NeedsManualPick: true),
        ];
        var plan = OptimizeProfileFile.Classify(["safe", "na", "expert", "expert-na", "ghost", "safe"], 2, catalog);

        Assert.Equal(["safe"], plan.Selected);
        Assert.Equal(["na", "expert-na"], plan.NotApplicable);
        Assert.Equal(["expert"], plan.Manual);
        Assert.Equal(3, plan.Unknown.Count);
        Assert.Equal("ghost", plan.Unknown[0]);

        Assert.Equal("Выбрана 1 настройка. Пропущено: неизвестные — 3, не подходят этому ПК — 2, требуют ручного выбора — 1.",
            OptimizeProfileFile.Summary(plan));
    }

    [Fact]
    public void Summary_UsesPlural()
    {
        var plan = new ProfileImportPlan(["a", "b", "c", "d", "e"], [], [], []);
        Assert.StartsWith("Выбрано 5 настроек.", OptimizeProfileFile.Summary(plan));
    }

    [Theory]
    [InlineData("Streaming")]
    [InlineData("Efficiency")]
    [InlineData("Gaming")]
    public void Scenario_name_round_trips(string scenario)
    {
        var json = OptimizeProfileFile.Serialize(["a"], "Набор", "1.0.0", DateTimeOffset.UtcNow, Enum.Parse<OptimizeProfile>(scenario));
        using (var doc = JsonDocument.Parse(json))
            Assert.Equal(scenario, doc.RootElement.GetProperty("scenario").GetString());
        Assert.Equal(Enum.Parse<OptimizeProfile>(scenario), Ok(json).Scenario);
    }

    [Fact]
    public void Custom_set_and_no_scenario_write_no_field_and_parse_as_null()
    {
        using (var doc = JsonDocument.Parse(OptimizeProfileFile.Serialize(["a"], null, null, DateTimeOffset.UtcNow, OptimizeProfile.Custom)))
            Assert.False(doc.RootElement.TryGetProperty("scenario", out _));
        using (var doc = JsonDocument.Parse(OptimizeProfileFile.Serialize(["a"], null, null, DateTimeOffset.UtcNow)))
            Assert.False(doc.RootElement.TryGetProperty("scenario", out _));
        // Files of older builds have no field; a hand-edited one may hold anything — never a crash, never a selection.
        Assert.Null(Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[]}""").Scenario);
        Assert.Null(Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],"scenario":"Turbo"}""").Scenario);
        Assert.Null(Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],"scenario":"16"}""").Scenario);
        Assert.Null(Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],"scenario":"Custom"}""").Scenario);
        Assert.Null(Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],"scenario":7}""").Scenario);
        Assert.Equal(OptimizeProfile.Efficiency, Ok("""{"format":"lycoris-optimize-profile","version":1,"tweaks":[],"scenario":" efficiency "}""").Scenario);
    }
}
