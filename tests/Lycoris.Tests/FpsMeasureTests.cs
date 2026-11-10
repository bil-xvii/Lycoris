using System.IO;
using System.Text;
using Lycoris.Core.Security;
using Lycoris.Modules.Games;
using Lycoris.Shell;
using Xunit;

namespace Lycoris.Tests;

public class FpsMeasureTests
{
    private static FpsRecord Rec(string kind, double avgMs, double p99Ms, string process = "game.exe", int seconds = 30) => new()
    {
        Kind = kind,
        Process = process,
        RequestedSeconds = seconds,
        RecordedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        Stats = new FpsStats(300, 3, 1000 / avgMs, 1000 / p99Ms, avgMs, p99Ms),
    };

    [Fact]
    public void Compute_SteadyFrames_GiveExactFps()
    {
        var s = FpsMeasure.Compute(Enumerable.Repeat(10.0, 100))!;
        Assert.Equal(100, s.Frames);
        Assert.Equal(100, s.AvgFps, 6);
        Assert.Equal(100, s.Low1Fps, 6);
        Assert.Equal(10, s.AvgFrameMs, 6);
        Assert.Equal(1.0, s.Seconds, 6);
    }

    [Fact]
    public void Compute_OnePercentLow_IsThe99thPercentileFrameTime()
    {
        // 98 fast frames + 2 slow ones of 100: the 99th percentile (nearest rank 99) is a slow frame → 20 FPS.
        var s = FpsMeasure.Compute(Enumerable.Repeat(10.0, 98).Concat([50.0, 50.0]))!;
        Assert.Equal(50, s.P99FrameMs, 6);
        Assert.Equal(20, s.Low1Fps, 6);
        Assert.Equal(100 * 1000.0 / (98 * 10 + 100), s.AvgFps, 6);
        // Only one slow frame of 100 stays outside the 1 % tail.
        Assert.Equal(100, FpsMeasure.Compute(Enumerable.Repeat(10.0, 99).Append(50.0))!.Low1Fps, 6);
    }

    [Fact]
    public void Compute_SkipsGlitches_AndRefusesTooFewFrames()
    {
        Assert.Null(FpsMeasure.Compute(Enumerable.Repeat(10.0, FpsMeasure.MinFrames - 1)));
        var s = FpsMeasure.Compute(Enumerable.Repeat(10.0, 40).Concat([0, -5, double.NaN, double.PositiveInfinity, 99_999]))!;
        Assert.Equal(40, s.Frames);
        Assert.Null(FpsMeasure.Compute(Enumerable.Repeat(0.0, 100)));
    }

    [Fact]
    public void ParseCsv_PicksTheBusiestSwapChain_SkipsNotesAndBadRows()
    {
        var sb = new StringBuilder("Application,ProcessID,SwapChainAddress,PresentRuntime,MsBetweenPresents,MsBetweenDisplayChange,FrameType\n");
        sb.Append("game.exe,100,0x1,DXGI,NA,NA,Application\n");
        for (var i = 0; i < 60; i++) sb.Append("game.exe,100,0x1,DXGI,8.000,8.0,Application\n");
        for (var i = 0; i < 40; i++) sb.Append("game.exe,100,0x2,DXGI,100.0,100.0,Application\n");
        sb.Append("// PresentMon: lost events\n");
        sb.Append("broken line\n\n");
        var s = FpsMeasure.ParseCsv(new StringReader(sb.ToString()))!;
        Assert.Equal(60, s.Frames);
        Assert.Equal(125, s.AvgFps, 6);
    }

    [Fact]
    public void ParseCsv_WithoutFrameTimeColumn_OrEmpty_IsNull()
    {
        Assert.Null(FpsMeasure.ParseCsv(new StringReader("Application,ProcessID\ngame.exe,1\n")));
        Assert.Null(FpsMeasure.ParseCsv(new StringReader("")));
        Assert.Null(FpsMeasure.ParseCsv(new StringReader("MsBetweenPresents\n")));
    }

    [Fact]
    public void ParseCsv_ReadsInvariantNumbers_RegardlessOfUiCulture()
    {
        using var _ = Loc.UseLanguage("ru");
        var csv = "MsBetweenPresents\n" + string.Concat(Enumerable.Repeat("16.5\n", 50));
        Assert.Equal(16.5, FpsMeasure.ParseCsv(new StringReader(csv))!.AvgFrameMs, 6);
    }

    [Theory]
    [InlineData("game", "game.exe")]
    [InlineData("Game.EXE", "Game.EXE")]
    [InlineData(" cs2.exe ", "cs2.exe")]
    [InlineData("Ведьмак3", "Ведьмак3.exe")]
    [InlineData("Game (x64)", "Game (x64).exe")]
    public void NormalizeProcess_AcceptsPlainNames(string input, string expected) =>
        Assert.Equal(expected, FpsMeasure.NormalizeProcess(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..\\x")]
    [InlineData("c:\\games\\x.exe")]
    [InlineData("-x")]
    [InlineData("--timed")]
    [InlineData("a\"b")]
    [InlineData("a*b")]
    [InlineData("a\nb")]
    [InlineData(".hidden")]
    public void NormalizeProcess_RefusesPathsSwitchesAndQuotes(string? input) =>
        Assert.Null(FpsMeasure.NormalizeProcess(input));

    [Fact]
    public void BuildArguments_TimedCaptureThatEndsByItself()
    {
        var a = FpsMeasure.BuildArguments("game.exe", 30, @"C:\x\out.csv");
        string After(string flag) => a[a.ToList().IndexOf(flag) + 1];
        Assert.Equal("game.exe", After("--process_name"));
        Assert.Equal("30", After("--timed"));
        Assert.Equal(@"C:\x\out.csv", After("--output_file"));
        Assert.Contains("--terminate_after_timed", a);
        Assert.Contains("--stop_existing_session", a);
        Assert.Contains("--no_console_stats", a);
    }

    [Fact]
    public void Compare_PercentsNoiseAndDirection()
    {
        var cmp = FpsMeasure.Compare(Rec(FpsRecord.BeforeKind, 10, 20), Rec(FpsRecord.AfterKind, 8, 19.8));
        Assert.Null(cmp.Warning);
        var avg = cmp.Rows[0];
        Assert.Equal(25, avg.Percent, 6);                // 100 → 125 FPS
        Assert.True(avg.IsBetter);
        var low = cmp.Rows[1];
        Assert.True(low.IsNoise);                         // 50 → 50.5 FPS = +1 %
        var ft = cmp.Rows[2];
        Assert.Equal(-20, ft.Percent, 6);                 // frametime down = better
        Assert.True(ft.IsBetter);
        var worse = FpsMeasure.Compare(Rec(FpsRecord.BeforeKind, 10, 20), Rec(FpsRecord.AfterKind, 12, 25));
        Assert.True(worse.Rows[0].IsWorse);
        Assert.True(worse.Rows[2].IsWorse);
    }

    [Fact]
    public void Compare_WarnsAboutDifferentGameOrLength()
    {
        Assert.NotNull(FpsMeasure.Compare(Rec(FpsRecord.BeforeKind, 10, 20), Rec(FpsRecord.AfterKind, 10, 20, "other.exe")).Warning);
        Assert.NotNull(FpsMeasure.Compare(Rec(FpsRecord.BeforeKind, 10, 20), Rec(FpsRecord.AfterKind, 10, 20, seconds: 60)).Warning);
    }

    [Fact]
    public void Summary_NamesBothCaptures()
    {
        var text = FpsMeasure.Summary(Rec(FpsRecord.BeforeKind, 10, 20), Rec(FpsRecord.AfterKind, 8, 16));
        Assert.Contains("100", text);
        Assert.Contains("125", text);
        Assert.Contains("game.exe", text);
        Assert.Equal("", FpsMeasure.Summary(null, null));
    }

    [Fact]
    public void PickConsole_TakesTheNewestVersion_IgnoresOtherFiles()
    {
        Assert.Equal(@"d\PresentMon-2.10.0-x64.exe",
            PresentMon.PickConsole([@"d\PresentMon-2.3.1-x64.exe", @"d\PresentMon-2.10.0-x64.exe", @"d\evil.exe", @"d\PresentMon-x-x64.exe", @"d\PresentMon-9.9-x86.exe"]));
        Assert.Null(PresentMon.PickConsole(["PresentMon.exe", "presentmon-cli.bat"]));
    }

    [Fact]
    public void Refusal_OnlyAnIntelSignedFileInAProtectedFolderRuns()
    {
        Assert.Null(PresentMon.Refusal(null, SignatureState.Valid, "Intel Corporation"));
        Assert.NotNull(PresentMon.Refusal("Users: Write", SignatureState.Valid, "Intel Corporation"));
        Assert.NotNull(PresentMon.Refusal(null, SignatureState.Unsigned, null));
        Assert.NotNull(PresentMon.Refusal(null, SignatureState.Tampered, "Intel Corporation"));
        Assert.NotNull(PresentMon.Refusal(null, SignatureState.Untrusted, "Intel Corporation"));
        Assert.NotNull(PresentMon.Refusal(null, SignatureState.Valid, "Evil Ltd"));
        Assert.NotNull(PresentMon.Refusal(null, SignatureState.Valid, null));
    }

    [Fact]
    public void Store_SavesBeforeAndAfter_NewBeforeStartsAFreshComparison()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lycoris-fps-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FpsStore(Path.Combine(dir, "fps.json"));
            Assert.Null(store.Read().Before);
            store.Save(Rec(FpsRecord.BeforeKind, 10, 20));
            var pair = store.Save(Rec(FpsRecord.AfterKind, 8, 16));
            Assert.NotNull(pair.Before);
            Assert.NotNull(pair.After);
            var read = store.Read();
            Assert.Equal(125, read.After!.Stats.AvgFps, 6);
            Assert.Equal("game.exe", read.Before!.Process);
            store.Save(Rec(FpsRecord.BeforeKind, 9, 18));
            Assert.Null(store.Read().After);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Before\":{\"Kind\":\"after\",\"Process\":\"game.exe\",\"RequestedSeconds\":30,\"RecordedAt\":\"2026-11-01T00:00:00Z\",\"Stats\":{\"Frames\":300,\"Seconds\":3,\"AvgFps\":100,\"Low1Fps\":50,\"AvgFrameMs\":10,\"P99FrameMs\":20}}}")]
    [InlineData("{\"Before\":{\"Kind\":\"before\",\"Process\":\"..\\\\x.exe\",\"RequestedSeconds\":30,\"RecordedAt\":\"2026-11-01T00:00:00Z\",\"Stats\":{\"Frames\":300,\"Seconds\":3,\"AvgFps\":100,\"Low1Fps\":50,\"AvgFrameMs\":10,\"P99FrameMs\":20}}}")]
    [InlineData("{\"Before\":{\"Kind\":\"before\",\"Process\":\"game.exe\",\"RequestedSeconds\":30,\"RecordedAt\":\"2026-11-01T00:00:00Z\",\"Stats\":{\"Frames\":300,\"Seconds\":3,\"AvgFps\":-1,\"Low1Fps\":50,\"AvgFrameMs\":10,\"P99FrameMs\":20}}}")]
    [InlineData("{\"Before\":{\"Kind\":\"before\",\"Process\":\"game.exe\",\"RequestedSeconds\":99999,\"RecordedAt\":\"2026-11-01T00:00:00Z\",\"Stats\":{\"Frames\":300,\"Seconds\":3,\"AvgFps\":100,\"Low1Fps\":50,\"AvgFrameMs\":10,\"P99FrameMs\":20}}}")]
    [InlineData("{\"Before\":{\"Kind\":\"before\",\"Process\":\"game.exe\",\"RequestedSeconds\":30,\"RecordedAt\":\"2026-11-01T00:00:00Z\",\"Stats\":null}}")]
    [InlineData("[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]")]
    public void Store_Parse_DropsHostileRecords(string json)
    {
        var pair = FpsStore.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Null(pair.Before);
        Assert.Null(pair.After);
    }

    [Fact]
    public void Store_Parse_RefusesOversizeAndInvalidUtf8()
    {
        Assert.Null(FpsStore.Parse(new byte[FpsStore.MaxBytes + 1]).Before);
        Assert.Null(FpsStore.Parse([0xC3, 0x28, 0xFF]).Before);
    }

    [Fact]
    public void MeasureFps_IsReplayableAfterTheAdminRelaunch()
    {
        Assert.True(AdminIntent.IsReplayable("MeasureFps"));
        Assert.NotNull(AdminIntent.Parse(["--replay-page", "games", "--replay-command", "MeasureFps", "--replay-param", "after"]));
    }
}
