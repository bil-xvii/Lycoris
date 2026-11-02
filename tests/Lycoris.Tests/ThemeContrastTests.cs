using System.IO;
using System.Text.RegularExpressions;

namespace Lycoris.Tests;

/// <summary>
/// The palette is black and white, a red accent and three muted semantic hues (green / amber / steel blue).
/// Every hue's text colour must be readable on the dark surfaces, every fill must be visible, and the neutral tokens must stay grey.
/// </summary>
public sealed class ThemeContrastTests
{
    private static readonly string[] Hues = ["Red", "Success", "Warning", "Info"];

    private static Dictionary<string, (byte R, byte G, byte B)> Colors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var rel = Path.Combine("src", "Lycoris", "Theme", "Colors.xaml");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, rel))) dir = dir.Parent;
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!.FullName, rel));
        var map = new Dictionary<string, (byte, byte, byte)>();
        foreach (Match m in Regex.Matches(xaml, "<Color x:Key=\"C\\.(\\w+)\">#([0-9A-Fa-f]{8})</Color>"))
        {
            var h = m.Groups[2].Value;
            map[m.Groups[1].Value] = (Convert.ToByte(h[2..4], 16), Convert.ToByte(h[4..6], 16), Convert.ToByte(h[6..8], 16));
        }
        return map;
    }

    private static double Lum((byte R, byte G, byte B) c)
    {
        static double F(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double Ratio((byte R, byte G, byte B) a, (byte R, byte G, byte B) b)
    {
        double x = Lum(a), y = Lum(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    /// <summary>Token name of a hue's text colour: RedText, SuccessText, …</summary>
    private static string TextOf(string hue) => hue + "Text";

    public static IEnumerable<object[]> HueOnBackground()
    {
        foreach (var hue in Hues)
            foreach (var bg in new[] { "Base", "Surface", "Surface2", "Surface3", hue + "Deep" })
                yield return [hue, bg];
    }

    [Theory]
    [MemberData(nameof(HueOnBackground))]
    public void Hue_text_is_readable_on_the_dark_backgrounds(string hue, string background)
    {
        var c = Colors();
        var r = Ratio(c[TextOf(hue)], c[background]);
        Assert.True(r >= 4.5, $"{TextOf(hue)} on {background}: {r:F2}");
    }

    [Theory]
    [InlineData("Success")]
    [InlineData("Warning")]
    [InlineData("Info")]
    public void Hue_fill_is_visible_as_a_bar_dot_and_outline(string hue)
    {
        var c = Colors();
        // WCAG 1.4.11: graphical objects >= 3:1 against what is next to them (Surface); the label beside always carries the meaning too.
        Assert.True(Ratio(c[hue], c["Surface"]) >= 3.0, $"{hue} on Surface");
        Assert.True(Ratio(c[hue], c["Surface3"]) >= 3.0, $"{hue} on Surface3");
        Assert.True(Ratio(c[hue], c[hue + "Deep"]) >= 3.0, $"{hue} outline on {hue}Deep");
    }

    [Fact]
    public void Dark_text_on_the_amber_counter_and_white_on_the_green_switch_are_readable()
    {
        var c = Colors();
        Assert.True(Ratio(c["Ink"], c["Warning"]) >= 4.5, "TabBadge.Alert: ink on amber");
        Assert.True(Ratio(c["White"], c["Success"]) >= 3.0, "Toggle ON: the knob on the green track");
    }

    [Fact]
    public void White_on_the_red_badge_and_the_red_edge_are_visible()
    {
        var c = Colors();
        Assert.True(Ratio(c["White"], c["Red"]) >= 4.5, "white text on the red badge");
        Assert.True(Ratio(c["Red"], c["Surface"]) >= 2.0, "the red bar / dot / outline on a surface");
        Assert.True(Ratio(c["RedHover"], c["Surface"]) >= Ratio(c["Red"], c["Surface"]));
    }

    [Fact]
    public void Deep_tints_are_nearly_black_so_a_hue_never_becomes_a_large_fill()
    {
        var c = Colors();
        foreach (var hue in Hues)
            Assert.True(Lum(c[hue + "Deep"]) <= 0.02, $"{hue}Deep must stay very dark (luminance {Lum(c[hue + "Deep"]):F4})");
    }

    [Fact]
    public void Neutrals_are_pure_greys_and_every_hue_token_belongs_to_one_family()
    {
        foreach (var (name, (r, g, b)) in Colors())
        {
            var hue = Hues.FirstOrDefault(h => name.StartsWith(h));
            if (hue is null) { Assert.True(r == g && g == b, $"C.{name} must be a pure grey"); continue; }
            var dominant = hue switch
            {
                "Red" => r > g && r > b,
                "Success" => g > r && g > b,
                "Warning" => r > b && g > b && r >= g,          // amber: red >= green > blue
                "Info" => b > r && b >= g,                       // steel: blue dominates, red lowest
                _ => false,
            };
            Assert.True(dominant, $"C.{name} does not belong to the {hue} family");
        }
    }

    [Fact]
    public void The_hues_are_muted_not_neon()
    {
        var c = Colors();
        foreach (var hue in new[] { "Success", "Warning", "Info" })
            foreach (var name in new[] { hue, hue + "Text" })
            {
                var (r, g, b) = c[name];
                var max = Math.Max(r, Math.Max(g, b)) / 255.0;
                var min = Math.Min(r, Math.Min(g, b)) / 255.0;
                var sat = max == 0 ? 0 : (max - min) / max;
                Assert.True(sat <= 0.72, $"C.{name} is too saturated ({sat:F2})");
            }
    }
}
