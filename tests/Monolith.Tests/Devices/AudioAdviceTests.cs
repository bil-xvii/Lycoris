using Monolith.Modules.Devices;
using Monolith.Modules.Devices.Audio;

namespace Monolith.Tests.Devices;

/// <summary>Audio recommendations (research devices_audio.md): default-device findings, format, effects, ducking choices.</summary>
public sealed class AudioAdviceTests
{
    internal const string Headset = "{0.0.0.00000000}.{13115c35-2308-4ab1-8fa0-2a1a69dff0a8}";
    internal const string DisplayOut = "{0.0.0.00000000}.{264c7e39-05c5-49e2-9ece-8b68055b6bad}";
    internal const string DisplayOut2 = "{0.0.0.00000000}.{285d25ab-8011-44b3-b184-736e54321c5e}";
    internal const string Mic = "{0.0.1.00000000}.{8baa6d9f-01c8-4bfe-b9e5-758c42eb3681}";
    internal const string Webcam = "{0.0.1.00000000}.{9caa6d9f-01c8-4bfe-b9e5-758c42eb3681}";

    internal static EndpointInfo Render(string id, string name, int ff, bool console = false, bool comms = false, string? instance = null) => new()
    {
        Id = id, Flow = AudioFlow.Render, Name = name, FormFactor = ff, IsDefaultConsole = console, IsDefaultMultimedia = console,
        IsDefaultCommunications = comms, InstanceId = instance, Format = new AudioFormat(48000, 16, 2, false, 16, 4),
    };

    internal static EndpointInfo Capture(string id, string name, bool console = false, bool comms = false) => new()
    {
        Id = id, Flow = AudioFlow.Capture, Name = name, FormFactor = 4, IsDefaultConsole = console, IsDefaultMultimedia = console,
        IsDefaultCommunications = comms, Format = new AudioFormat(48000, 24, 1, false, 24, 3),
    };

    [Fact]
    public void A_well_set_up_pc_has_no_findings() =>
        Assert.Empty(AudioAdvice.DefaultRolesIssues(
        [
            Render(Headset, "Наушники (INZONE E9)", 3, console: true, comms: true),
            Render(DisplayOut, "X27U W3 (NVIDIA High Definition Audio)", 9),
            Capture(Mic, "Микрофон (Razer Seiren V3 Mini)", console: true, comms: true),
        ]));

    [Fact]
    public void Split_render_roles_are_flagged_on_the_default_output()
    {
        var issues = AudioAdvice.DefaultRolesIssues(
        [
            Render(Headset, "Наушники (INZONE E9)", 3, console: true),
            Render(DisplayOut2, "Динамики", 1, comms: true),
        ]);
        var issue = Assert.Single(issues);
        Assert.Equal(Headset, issue.EndpointId);
        Assert.Equal(0, issue.SoundPanelTab);
        Assert.Contains("Динамики", issue.Text);
    }

    [Fact]
    public void A_monitor_as_default_while_headphones_are_connected_is_flagged_on_the_headphones()
    {
        var issues = AudioAdvice.DefaultRolesIssues(
        [
            Render(Headset, "Наушники (INZONE E9)", 3, comms: true),
            Render(DisplayOut, "X27U W3 (NVIDIA High Definition Audio)", 9, console: true),
        ]);
        var issue = Assert.Single(issues); // the more specific finding replaces «split roles»
        Assert.Equal(Headset, issue.EndpointId);
        Assert.Contains("«X27U W3»", issue.Text);
    }

    [Fact]
    public void A_bluetooth_hands_free_default_is_flagged()
    {
        var bt = Render(Headset, "Головной телефон (WH-1000XM5)", 5, console: true, comms: true, instance: @"BTHHFENUM\BthHFPAudio\8&1");
        var issue = Assert.Single(AudioAdvice.DefaultRolesIssues([bt]));
        Assert.Equal(Headset, issue.EndpointId);
        Assert.Contains("Bluetooth", issue.Text);
    }

    [Fact]
    public void Split_capture_roles_are_flagged_with_the_recording_tab()
    {
        var issue = Assert.Single(AudioAdvice.DefaultRolesIssues(
        [
            Capture(Mic, "Микрофон (Razer Seiren V3 Mini)", comms: true),
            Capture(Webcam, "Микрофон (Веб-камера)", console: true),
        ]));
        Assert.Equal(Mic, issue.EndpointId);
        Assert.Equal(1, issue.SoundPanelTab);
    }

    [Fact]
    public void Format_advice_is_48_khz_and_never_96_or_192()
    {
        Assert.Null(AudioAdvice.FormatAdvice(new AudioFormat(48000, 16, 2, false)));
        Assert.Null(AudioAdvice.FormatAdvice(null));
        var cd = AudioAdvice.FormatAdvice(new AudioFormat(44100, 16, 2, false));
        Assert.NotNull(cd);
        Assert.Contains("48 кГц", cd);
        var high = AudioAdvice.FormatAdvice(new AudioFormat(192000, 24, 2, false));
        Assert.NotNull(high);
        Assert.Contains("48 кГц", high);
        foreach (var text in new[] { cd!, high! })
        {
            Assert.DoesNotContain("96", text);
            Assert.DoesNotContain("192", text);
        }
    }

    [Fact]
    public void Effects_advice_only_for_an_output_with_vendor_processing_and_effects_on()
    {
        var on = Render(Headset, "Наушники (INZONE E9)", 3, console: true) with { Vid = "054C", EnhancementsOff = false };
        Assert.True(AudioAdvice.HasVendorProcessing(on));
        Assert.NotNull(AudioAdvice.EnhancementsAdvice(on, vendorDsp: true));
        Assert.Null(AudioAdvice.EnhancementsAdvice(on with { EnhancementsOff = true }, vendorDsp: true));
        Assert.Null(AudioAdvice.EnhancementsAdvice(on, vendorDsp: false));
        Assert.Null(AudioAdvice.EnhancementsAdvice(Capture(Mic, "Микрофон") with { EnhancementsOff = false, Vid = "1532" }, vendorDsp: true));
        Assert.False(AudioAdvice.HasVendorProcessing(on with { Vid = null }));
    }

    [Fact]
    public void Spatial_advice_only_when_windows_spatial_is_on_over_vendor_surround()
    {
        var e = Render(Headset, "Наушники (INZONE E9)", 3) with { Vid = "054C", SpatialSupported = true, Spatial = SpatialState.WindowsSonic };
        Assert.NotNull(AudioAdvice.SpatialAdvice(e, hasVendorSpatial: true));
        Assert.Null(AudioAdvice.SpatialAdvice(e with { Spatial = SpatialState.Off }, hasVendorSpatial: true));
        Assert.Null(AudioAdvice.SpatialAdvice(e, hasVendorSpatial: false));
    }

    [Fact]
    public void Ducking_choices_follow_the_communications_tab_and_recommend_do_nothing()
    {
        Assert.Equal([0L, 1L, 2L, 3L], AudioAdvice.DuckingOptions.Select(o => o.Value.Number));
        Assert.Equal(
            ["Отключать все другие звуки", "Уменьшать громкость на 80 %", "Уменьшать громкость на 50 %", "Ничего не делать"],
            AudioAdvice.DuckingOptions.Select(o => o.Label));
        Assert.Equal(3, AudioAdvice.DuckingRecommended);
        Assert.Equal(RecommendPolicy.Suggest, AudioAdvice.DuckingPolicy);
        Assert.Equal("игровой звук не будет приглушаться, когда открыт голосовой чат", AudioAdvice.DuckingReason);
        Assert.Equal("тише на 80 %", AudioAdvice.DuckingShort(null)); // absent = the Windows default
        Assert.Equal("ничего не делать", AudioAdvice.DuckingShort(3));
        Assert.Equal("нестандартное значение", AudioAdvice.DuckingShort(7));
    }
}
