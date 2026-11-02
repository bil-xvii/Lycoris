using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Monolith.UI;
using Xunit;

namespace Monolith.Tests;

/// <summary>Buttons built from icon + text must expose the visible words to UI Automation (Obvious UI: every button is discoverable).</summary>
public class UiA11yTests
{
    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("STA test failed", error);
    }

    [Fact]
    public void Button_with_glyph_and_text_gets_the_text_as_name_without_the_glyph()
    {
        Sta(() =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons") });
            panel.Children.Add(new TextBlock { Text = "ПРОВЕРИТЬ  ОБНОВЛЕНИЯ" });
            var button = new Button { Content = panel };
            var host = new Window { Content = button, ShowActivated = false, Width = 10, Height = 10, WindowStyle = WindowStyle.None, Left = -20000 };
            host.Show();
            try
            {
                Assert.Equal("ПРОВЕРИТЬ ОБНОВЛЕНИЯ", UiA11y.Derive(button));
            }
            finally { host.Close(); }
        });
    }

    [Fact]
    public void Explicit_name_is_never_replaced_and_string_content_is_used_as_is()
    {
        Sta(() =>
        {
            Assert.Equal("ОТМЕНА", UiA11y.Derive(new Button { Content = "ОТМЕНА" }));
            var named = new Button { Content = "X" };
            AutomationProperties.SetName(named, "Закрыть окно");
            Assert.Equal("Закрыть окно", AutomationProperties.GetName(named));
        });
    }
}
