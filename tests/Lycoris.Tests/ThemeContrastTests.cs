using System.IO;
using System.Xml.Linq;

namespace Lycoris.Tests;

public sealed class ThemeContrastTests
{
    private static readonly string[] Surfaces = ["Base", "Surface", "Surface2", "Surface3", "Surface4"];
    private static readonly string[] Hues = ["Red", "Success", "Warning", "Info"];
    private static readonly Lazy<Dictionary<string, string>> BrushColors = new(ReadBrushColors);

    [Fact]
    public void Text_is_readable_on_all_dark_surfaces_and_tints()
    {
        foreach (var background in Surfaces.Concat(["Selected"]).Concat(Hues.Select(hue => hue + "Deep")))
            foreach (var foreground in new[] { "Text", "Text2", "Text3" })
                AssertContrast(foreground, background, 4.5);
    }

    [Fact]
    public void Active_control_boundaries_and_keyboard_focus_are_visible()
    {
        foreach (var background in Surfaces.Concat(["Selected"]))
        {
            AssertContrast("Outline", background, 3.0);
            AssertContrast("AccentHover", background, 3.0);
        }
    }

    [Theory]
    [InlineData("Accent")]
    [InlineData("AccentHover")]
    [InlineData("AccentPressed")]
    public void Primary_action_is_readable_in_every_pointer_state(string fill)
    {
        AssertContrast("OnAccent", fill, 4.5);
        foreach (var background in Surfaces.Concat(["Selected"]))
            AssertContrast(fill, background, 3.0);
    }

    [Fact]
    public void Selection_marker_and_outline_are_visible_on_the_tinted_plate()
    {
        // The underline, sidebar marker, filter outline and selected tile edge use this pair.
        AssertContrast("Accent", "Selected", 3.0);
    }

    [Theory]
    [InlineData("Red")]
    [InlineData("Success")]
    [InlineData("Warning")]
    [InlineData("Info")]
    public void Semantic_text_and_indicators_remain_visible(string hue)
    {
        var tint = hue + "Deep";
        foreach (var background in Surfaces.Concat(["Selected", tint]))
        {
            AssertContrast(hue + "Text", background, 4.5);
            AssertContrast(hue, background, 3.0);
        }

        Assert.True(Luminance(Brush(tint)) <= 0.02, $"B.{tint} must stay dark");
    }

    [Fact]
    public void Danger_button_hover_has_a_visible_edge()
    {
        foreach (var background in Surfaces.Concat(["Selected", "RedDeep"]))
            AssertContrast("RedHover", background, 3.0);
    }

    [Fact]
    public void Filled_badge_and_switch_have_readable_foregrounds()
    {
        AssertContrast("Ink", "Warning", 4.5);
        AssertContrast("Ink", "Success", 3.0);
    }

    [Fact]
    public void Slate_layers_get_lighter_and_keep_a_subtle_blue_tint()
    {
        for (int i = 1; i < Surfaces.Length; i++)
        {
            Assert.True(Luminance(Brush(Surfaces[i])) > Luminance(Brush(Surfaces[i - 1])),
                $"B.{Surfaces[i]} must be lighter than B.{Surfaces[i - 1]}");
        }

        foreach (var name in Surfaces.Concat(["Line", "Line2", "Line3", "Selected"]))
        {
            var (r, g, b) = Brush(name);
            Assert.True(r <= g && g <= b && b - r <= 42, $"B.{name} must stay close to neutral slate");
        }
    }

    [Fact]
    public void Semantic_hues_and_steel_accents_keep_their_color_families()
    {
        foreach (var hue in Hues)
        {
            foreach (var name in new[] { hue, hue + "Text", hue + "Deep" })
            {
                var (r, g, b) = Brush(name);
                bool correctFamily = hue switch
                {
                    "Red" => r > g && r > b,
                    "Success" => g > r && g > b,
                    "Warning" => r >= g && g > b,
                    "Info" => b >= g && g > r,
                    _ => false,
                };
                Assert.True(correctFamily, $"B.{name} must stay in the {hue} color family");
            }
        }

        foreach (var name in new[] { "Accent", "AccentHover", "AccentPressed" })
        {
            var (r, g, b) = Brush(name);
            Assert.True(r <= g && g <= b, $"B.{name} must stay steel blue");
        }
    }

    [Fact]
    public void Interactive_and_semantic_colors_stay_muted()
    {
        var names = Hues.SelectMany(hue => new[] { hue, hue + "Text" })
            .Concat(["RedHover", "Accent", "AccentHover", "AccentPressed"]);
        foreach (var name in names)
        {
            var (r, g, b) = Brush(name);
            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            double saturation = max == 0 ? 0 : (max - min) / (double)max;
            Assert.True(saturation <= 0.6, $"B.{name} is too saturated ({saturation:F2})");
        }
    }

    private static Dictionary<string, string> ReadBrushColors()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        var rel = Path.Combine("src", "Lycoris", "Theme", "Colors.xaml");
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, rel)))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var root = XDocument.Load(Path.Combine(dir!.FullName, rel)).Root!;
        XNamespace wpf = root.Name.Namespace;
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var colors = root.Elements(wpf + "Color").ToDictionary(element => element.Attribute(xaml + "Key")!.Value, element => element.Value);
        const string resource = "{StaticResource ";
        return root.Elements(wpf + "SolidColorBrush").ToDictionary(
            brush => brush.Attribute(xaml + "Key")!.Value,
            brush =>
            {
                var value = brush.Attribute("Color")!.Value;
                return value.StartsWith(resource, StringComparison.Ordinal) ? colors[value[resource.Length..^1]] : value;
            });
    }

    private static (byte R, byte G, byte B) Brush(string name)
    {
        string value = BrushColors.Value["B." + name];
        Assert.StartsWith("#FF", value, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, value.Length);
        return (Convert.ToByte(value[3..5], 16), Convert.ToByte(value[5..7], 16), Convert.ToByte(value[7..9], 16));
    }

    private static double Luminance((byte R, byte G, byte B) color)
    {
        static double Linearize(byte channel)
        {
            double s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
    }

    private static void AssertContrast(string foreground, string background, double minimum)
    {
        double fg = Luminance(Brush(foreground));
        double bg = Luminance(Brush(background));
        double ratio = (Math.Max(fg, bg) + 0.05) / (Math.Min(fg, bg) + 0.05);
        Assert.True(ratio >= minimum, $"B.{foreground} on B.{background}: {ratio:F2}:1, expected at least {minimum:F1}:1");
    }
}
