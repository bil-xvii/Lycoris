using Lycoris.Modules.Devices;
using Lycoris.Modules.Devices.Input;

namespace Lycoris.Tests.Devices;

/// <summary>Pointer-speed / DPI math and the Russian texts of the mouse and keyboard advice (pure).</summary>
public sealed class InputAdviceTests
{
    [Theory]
    [InlineData(1, 1.0 / 32)]
    [InlineData(2, 1.0 / 16)]
    [InlineData(3, 1.0 / 8)]
    [InlineData(4, 0.25)]
    [InlineData(6, 0.5)]
    [InlineData(10, 1.0)]
    [InlineData(11, 1.25)]
    [InlineData(14, 2.0)]
    [InlineData(20, 3.5)]
    public void Pointer_multiplier_table(int speed, double expected) =>
        Assert.Equal(expected, InputAdvice.PointerMultiplier(speed), 6);

    [Fact]
    public void Compensated_dpi_keeps_the_cursor_speed()
    {
        Assert.Equal(3200, InputAdvice.CompensatedDpi(1600, 14));
        Assert.Equal(400, InputAdvice.CompensatedDpi(800, 6));
        Assert.Equal(1600, InputAdvice.CompensatedDpi(1600, 10));
        Assert.Equal(50, InputAdvice.CompensatedDpi(1600, 1)); // 1600 / 32
    }

    [Fact]
    public void Desktop_travel()
    {
        Assert.Equal(4.064, InputAdvice.DesktopTravelCm(2560, 1600, 1), 3);
        Assert.Equal(0, InputAdvice.DesktopTravelCm(0, 1600, 1));
        Assert.Equal("через экран 2560 px ≈ 4,1 см", InputAdvice.TravelText(2560, 1600, 1));
        Assert.Null(InputAdvice.TravelText(0, 1600, 1));
    }

    [Theory]
    [InlineData(400, true)]
    [InlineData(800, false)]
    [InlineData(780, false)]  // within the ±5 % measurement tolerance
    [InlineData(1600, false)]
    [InlineData(1650, false)]
    [InlineData(3200, true)]
    public void Dpi_advice_range(double dpi, bool outside)
    {
        var (text, flagged) = InputAdvice.DpiAdvice(dpi, 2560);
        Assert.Equal(outside, flagged);
        Assert.Contains("через экран 2560 px", text);
        if (outside) Assert.Contains("800–1600 DPI", text);
    }

    [Fact]
    public void Dpi_advice_without_a_measurement_is_not_a_finding()
    {
        var (text, outside) = InputAdvice.DpiAdvice(null, 2560);
        Assert.False(outside);
        Assert.Contains("не измерено", text);
    }

    [Fact]
    public void Pointer_speed_reason_never_contradicts_the_dpi_range()
    {
        // 1600 DPI at speed 14 → 3200 DPI keeps the speed, but that is outside 800–1600: both ways are offered.
        var high = InputAdvice.PointerSpeedReason(14, 1600);
        Assert.Contains("3200 DPI", high);
        Assert.Contains("1600 DPI — курсор станет медленнее", high);
        // 800 DPI at speed 6 → 400 DPI; the other way is 800 DPI and a faster cursor.
        var low = InputAdvice.PointerSpeedReason(6, 800);
        Assert.Contains("400 DPI", low);
        Assert.Contains("800 DPI — курсор станет быстрее", low);
        // Inside the range: one number.
        var inside = InputAdvice.PointerSpeedReason(12, 1000);
        Assert.Contains("≈ 1500 DPI", inside);
        Assert.DoesNotContain("или", inside);
    }

    [Fact]
    public void Pointer_speed_recommendation_waits_for_the_dpi_test()
    {
        Assert.Null(InputAdvice.PointerSpeedRecommendation(10, null, null));
        Assert.Null(InputAdvice.PointerSpeedRecommendation(null, 1600, null));

        var waiting = InputAdvice.PointerSpeedRecommendation(14, null, InputProvider.DpiToolLink)!;
        Assert.True(waiting.NeedsMeasurement);
        Assert.Equal(DeviceLinkKind.Tool, waiting.Link!.Kind);
        Assert.Equal(10, waiting.Value.Number);

        var measured = InputAdvice.PointerSpeedRecommendation(14, 1600, InputProvider.DpiToolLink)!;
        Assert.False(measured.NeedsMeasurement);
        Assert.Equal(RecommendPolicy.Suggest, measured.Policy);
        Assert.Contains("3200", measured.Reason);
    }

    [Fact]
    public void Polling_advice()
    {
        Assert.Contains("1000 Гц", InputAdvice.PollingAdvice(500));
        Assert.Contains("на 1 мс", InputAdvice.PollingAdvice(500));
        Assert.Contains("на 7 мс", InputAdvice.PollingAdvice(125));
        Assert.Contains("оптимально", InputAdvice.PollingAdvice(1000));
        Assert.Contains("по желанию", InputAdvice.PollingAdvice(4000));
        Assert.Contains("проверили свои игры", InputAdvice.PollingAdvice(8000));
        Assert.Contains("1000 Гц", InputAdvice.PollingAdvice(null));
        Assert.True(InputAdvice.PollingNeedsAdvice(500));
        Assert.True(InputAdvice.PollingNeedsAdvice(8000));
        Assert.False(InputAdvice.PollingNeedsAdvice(1000));
        Assert.False(InputAdvice.PollingNeedsAdvice(2000));
        Assert.False(InputAdvice.PollingNeedsAdvice(null));
    }

    [Fact]
    public void Keyboard_texts()
    {
        Assert.Equal("≈ 250 мс", InputAdvice.KeyboardDelayText(0));
        Assert.Equal("≈ 500 мс", InputAdvice.KeyboardDelayText(1));
        Assert.Equal("≈ 1000 мс", InputAdvice.KeyboardDelayText(3));
        Assert.Equal("≈ 30,0 в секунду", InputAdvice.RepeatRateText(31));
        Assert.Equal("≈ 2,5 в секунду", InputAdvice.RepeatRateText(0));
        Assert.Equal("≈30/с", InputAdvice.RepeatSpeedText(31));
        Assert.Equal("≈20,2/с", InputAdvice.RepeatSpeedText(20));
    }

    [Fact]
    public void Russian_formatting()
    {
        Assert.Equal("1 строка", InputAdvice.WheelLinesText(1));
        Assert.Equal("3 строки", InputAdvice.WheelLinesText(3));
        Assert.Equal("5 строк", InputAdvice.WheelLinesText(5));
        Assert.Equal("11 строк", InputAdvice.WheelLinesText(11));
        Assert.Equal("21 строка", InputAdvice.WheelLinesText(21));
        Assert.Equal("экран целиком", InputAdvice.WheelLinesText(-1));
        Assert.Equal("14 (×2)", InputAdvice.SpeedText(14));
        Assert.Equal("1 (×1/32)", InputAdvice.SpeedText(1));
        Assert.Equal("6 (×0,5)", InputAdvice.SpeedText(6));
        Assert.Equal("выкл", InputAdvice.TrailsText(1));
        Assert.Equal("длина 5", InputAdvice.TrailsText(5));
        Assert.Equal("≈ 1600 DPI", InputAdvice.DpiText(1587));
    }
}
