using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Lycoris.Core.Diagnostics;

/// <summary>
/// Removes identity data from diagnostic text, line by line. Every pattern is a static, compiled-once <see cref="Regex"/> with a match timeout
/// (the Regex cache holds only 15 patterns), and the whole pass has a deadline.
/// </summary>
public static partial class ProblemReport
{
    private const int MaxTextChars = 2_000_000;
    private const int MaxLineChars = 8192;
    private const string ExcludedLine = "<line removed>";
    private const string TruncatedMark = "<truncated>";
    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);
    private const RegexOptions Rx = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static Regex R(string pattern, RegexOptions options = Rx) => new(pattern, options, RxTimeout);

    private const string SecretWords = @"(?:token|secret|passw(?:or)?d|pwd|bearer|парол\w*|ключ\w*|токен\w*|(?<![A-Za-z])key|(?-i:(?<=[a-z])Key))";

    private static readonly Regex ProfilePathRx = R(@"(?:[A-Z]:)?[\\/]+(?:Users|home)[\\/]+[^\\/\s""'<>|:*?]+");
    private static readonly Regex MacRx = R(@"(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])|(?<![0-9a-f])(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}(?![0-9a-f])");
    private static readonly Regex Ipv6Rx = R(@"(?<![0-9a-f:.])(?:[0-9a-f]{0,4}:){2,}[0-9a-f.:]*(?:%\w+)?");
    private static readonly Regex Ipv4Rx = R(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d])");
    private static readonly Regex EmailRx = R(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}");
    private static readonly Regex SecretWordRx = R(SecretWords);
    private static readonly Regex LongTokenRx = R(@"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_-]{24,}={0,2}(?![A-Za-z0-9+/_=-])", RegexOptions.CultureInvariant);
    private static readonly Regex SecretValueRx = R(@"(" + SecretWords + @"\w*(?:\s*[:=]\s*|\s+(?=bearer\s|\S{24,})))(?:""[^""]*""|'[^']*'|\S+)");
    private static readonly Regex SerialRx = R(@"\b(?:serial(?:\s*(?:number|no\.?))?|s/n|серийный\s+номер)\s*[:=#]?\s*[\w-]+");
    private static readonly Regex UsersPathLeftRx = R(@"[\\/]+(?:Users|home)[\\/]+");
    private static readonly Regex LongRunRx = R(@"[A-Za-z0-9+/_-]{24,}", RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes identity data line by line. Text is first normalized (Unicode FormKC, no invisible format characters, '/' as '\'
    /// inside paths). Anything that cannot be cleaned reliably (regex timeout, a leftover that still looks personal, a very long line)
    /// removes the WHOLE line.
    /// </summary>
    public static string Anonymize(string? value, string? userName = null, string? computerName = null)
    {
        var names = new List<(string Needle, string Replacement)>
        {
            (userName ?? Environment.UserName, "<user>"), (computerName ?? Environment.MachineName, "<pc>"),
        };
        return Anonymize(value, names, DefaultBudget);
    }

    internal static string Anonymize(string? value, IEnumerable<(string Needle, string Replacement)> identities, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        var text = value ?? string.Empty;
        if (text.Length > MaxTextChars) text = text[..MaxTextChars];
        text = Normalize(text);
        var names = identities.Select(x => (Needle: Normalize(x.Needle ?? string.Empty), x.Replacement)).Where(x => x.Needle.Length > 0)
            .DistinctBy(x => x.Needle, StringComparer.OrdinalIgnoreCase).OrderByDescending(x => x.Needle.Length).ToArray();
        var map = names.ToDictionary(x => x.Needle, x => x.Replacement, StringComparer.OrdinalIgnoreCase);
        var namesRx = names.Length == 0 ? null : R(string.Join("|", names.Select(x => Regex.Escape(x.Needle))));
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (clock.Elapsed > budget)
            {
                lines = [.. lines.Take(i), TruncatedMark];
                break;
            }
            string line;
            try { line = CleanLine(lines[i], namesRx, map); }
            catch (RegexMatchTimeoutException) { line = ExcludedLine; }
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }

    private static string Normalize(string text)
    {
        string n;
        try { n = text.Normalize(NormalizationForm.FormKC); } catch (ArgumentException) { n = new string(text.Where(c => !char.IsSurrogate(c)).ToArray()).Normalize(NormalizationForm.FormKC); }
        var sb = new StringBuilder(n.Length);
        foreach (var c in n)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.Format) continue; // zero-width and bidi characters split names invisibly
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CleanLine(string line, Regex? namesRx, Dictionary<string, string> map)
    {
        if (line.Length > MaxLineChars) return ExcludedLine;
        if (namesRx is not null) line = namesRx.Replace(line, m => map.TryGetValue(m.Value, out var r) ? r : "<user>");
        line = ProfilePathRx.Replace(line, "%USERPROFILE%");
        line = MacRx.Replace(line, "<mac>");
        line = Ipv6Rx.Replace(line, m => ReplaceIpv6(m.Value));
        line = Ipv4Rx.Replace(line, m => System.Net.IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "<ip>" : m.Value);
        line = EmailRx.Replace(line, "<email>");
        if (SecretWordRx.IsMatch(line))
        {
            line = LongTokenRx.Replace(line, "<secret>");
            line = SecretValueRx.Replace(line, "$1<redacted>");
        }
        line = SerialRx.Replace(line, "serial: <redacted>");
        return StillLooksPersonal(line) ? ExcludedLine : line;
    }

    private static string ReplaceIpv6(string candidate)
    {
        var core = candidate.TrimEnd(':', '.');
        var tail = candidate[core.Length..];
        if (core.Length > 0 && System.Net.IPAddress.TryParse(core, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return "<ip>" + tail;
        // A trailing «:»/«.» can also belong to the address («fe80::1:»), try the whole text too.
        return System.Net.IPAddress.TryParse(candidate, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "<ip>" : candidate;
    }

    /// <summary>Second pass over a finished line: anything that would still identify the user means the line is dropped.</summary>
    private static bool StillLooksPersonal(string line)
    {
        if (line.Contains('@') && EmailRx.IsMatch(line)) return true;
        if (UsersPathLeftRx.IsMatch(line)) return true;
        if (SecretWordRx.IsMatch(line) && LongRunRx.IsMatch(line)) return true;
        return false;
    }
}
