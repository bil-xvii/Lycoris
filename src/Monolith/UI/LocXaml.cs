using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Monolith.UI;

/// <summary>
/// XAML localization: <c>Text="{loc:T Применить}"</c>, <c>Content="{loc:T 'Открыть, закрыть'}"</c> (quote a text that has a comma),
/// <c>ToolTip="{loc:T …}"</c>, <c>StringFormat={loc:T 'СТРОК: {0}'}</c>. The Russian text is the key (see <see cref="Loc.T"/>).
/// Declare once per file: <c>xmlns:loc="clr-namespace:Monolith.UI"</c>.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Text);
}

/// <summary>
/// For bindings that produce Russian text (<c>Text="{Binding Title, Converter={x:Static loc:LocConverter.Instance}}"</c>):
/// translates the whole string if the table knows it (otherwise the text is shown as is, no missing-key noise).
/// </summary>
public sealed class LocConverter : IValueConverter
{
    public static readonly LocConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s ? Loc.Sink(s) : value ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// A bound value inside a translated format: <c>ConverterParameter='Выбрано {0}'</c> → Loc.Tf(parameter, value).
/// Use for counters when the format itself lives in XAML.
/// </summary>
public sealed class LocFormatConverter : IValueConverter
{
    public static readonly LocFormatConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        parameter is string format ? Loc.Tf(format, value) : value?.ToString() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
