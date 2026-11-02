namespace Monolith.Core.Security;

/// <summary>
/// Text that goes INTO a PowerShell script. PowerShell treats the typographic quotes U+2018/2019/201A/201B exactly like
/// the plain apostrophe, so «double the apostrophe» is not enough: a name that contains one of them would end the literal
/// and the rest would run as code. Every dynamic value is turned into a single-quoted literal here — or refused.
/// </summary>
public static class PsText
{
    private static readonly char[] SingleQuotes = ['\'', '‘', '’', '‚', '‛'];

    /// <summary>True when the value can be placed in a literal: no control characters (a line break ends a one-line command), no NUL.</summary>
    public static bool IsLiteralSafe(string? value) =>
        value is not null && value.Length <= 32_768 && !value.Any(char.IsControl);

    /// <summary>
    /// <c>'text'</c> with every kind of single quote doubled. Throws <see cref="ArgumentException"/> for a value with control
    /// characters. Use for paths and names; the result is one PowerShell string, whatever the input.
    /// </summary>
    public static string Literal(string value)
    {
        if (!IsLiteralSafe(value)) throw new ArgumentException("The value cannot be placed in a PowerShell script.", nameof(value));
        var sb = new System.Text.StringBuilder(value.Length + 8).Append('\'');
        foreach (var c in value)
        {
            sb.Append(c);
            if (Array.IndexOf(SingleQuotes, c) >= 0) sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}
