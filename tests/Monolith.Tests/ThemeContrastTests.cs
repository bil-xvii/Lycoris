using System.IO;
using System.Text.RegularExpressions;

namespace Monolith.Tests;

/// <summary>The palette is black and white plus one red accent: red text must be readable and no other hue may appear.</summary>
public sealed class ThemeContrastTests
{
    private static Dictionary<string, (byte R, byte G, byte B)> Colors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var rel = Path.Combine("src", "Monolith", "Theme", "Colors.xaml");
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

    [Theory]
    [InlineData("Base")]
    [InlineData("Surface")]
    [InlineData("Surface2")]
    [InlineData("Surface3")]
    [InlineData("RedDeep")]
    public void Red_text_is_readable_on_the_dark_backgrounds(string background)
        => Assert.True(Ratio(Colors()["RedText"], Colors()[background]) >= 4.5, $"RedText on {background}: {Ratio(Colors()["RedText"], Colors()[background]):F2}");

    [Fact]
    public void White_on_the_red_badge_and_the_red_edge_are_visible()
    {
        var c = Colors();
        Assert.True(Ratio(c["White"], c["Red"]) >= 4.5, "white text on the red badge");
        Assert.True(Ratio(c["Red"], c["Surface"]) >= 2.0, "the red bar / dot / outline on a surface");
        Assert.True(Ratio(c["RedHover"], c["Surface"]) >= Ratio(c["Red"], c["Surface"]));
    }

    [Fact]
    public void There_is_no_other_hue_than_the_red_family()
    {
        foreach (var (name, (r, g, b)) in Colors())
        {
            if (name.StartsWith("Red")) { Assert.True(r > g && r > b, name); continue; }
            Assert.True(r == g && g == b, $"C.{name} must be a pure grey");
        }
    }
}
