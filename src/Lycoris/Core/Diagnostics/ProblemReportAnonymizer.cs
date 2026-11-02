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

    // No left boundary for the long words (camelCase: accessToken, clientSecret); «key» and «sid» need one or a lower→Upper hump (monkey is not a key).
    private const string SecretWords = @"(?:token|secret|passw(?:or)?d|pwd|bearer|authorization|cookie|credential|session|signature|connectionstring|парол\w*|ключ\w*|токен\w*|куки|(?<![A-Za-z])key|(?-i:(?<=[a-z])Key)|(?<![A-Za-z])sid|(?-i:(?<=[a-z])Sid))";

    // A path segment may hold single spaces («Мария Иванова», «OneDrive - Компания»): it runs to the next slash, but stops before « C:\».
    private const string Seg = @"(?:[^\\/\s""'<>|:*?]| (?![A-Za-z]:))+";
    private static readonly Regex ProfilePathRx = R(@"(?:[A-Z]:)?[\\/]+(?:Users|home|Documents and Settings|Пользователи)[\\/]+" + Seg);
    private static readonly Regex OneDriveRx = R(@"\bOneDrive +- +" + Seg);
    // \\server\share\… (also JSON-escaped): the whole network path goes, the server, the share and any profile folder below them.
    private static readonly Regex UncPathRx = R(@"(?<![\w:.])[\\/]{2,4}(?![\\/?.])[^\\/\s""'<>|:*?]+(?:[\\/]+" + Seg + ")+");
    // EUI-64 (8 bytes) and EUI-48 (6 bytes) with «:» or «-», Cisco «aabb.ccdd.eeff», and 12 unseparated hex digits that hold a letter (not a timestamp, not a GUID tail).
    private static readonly Regex MacRx = R(@"(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){7}[0-9a-f]{2}(?![0-9a-f])|(?<![0-9a-f])(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}(?![0-9a-f])|(?<![0-9a-f])(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}(?![0-9a-f])|(?<![0-9a-f:-])(?=[0-9a-f]*[a-f])[0-9a-f]{12}(?![0-9a-f])(?!-[0-9a-f])");
    private static readonly Regex MacExactRx = R(@"^(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}$|^(?:[0-9a-f]{2}[:-]){7}[0-9a-f]{2}$");
    // No left boundary: an address glued to hex letters («deadfe80::1») is still found (the glued letters stay, the address does not).
    private static readonly Regex Ipv6Rx = R(@"(?:[0-9a-f]{0,4}:){2,}[0-9a-f.:]*(?:%[\w.-]+)?");
    private static readonly Regex Ipv4Rx = R(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d])");
    private static readonly Regex EmailRx = R(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}");
    private static readonly Regex BasicAuthRx = R(@"\bbasic\s+[A-Za-z0-9+/]{8,}={0,2}");
    private static readonly Regex SecretWordRx = R(SecretWords);
    private static readonly Regex LongTokenRx = R(@"(?<![A-Za-z0-9+/_=-])[A-Za-z0-9+/_-]{24,}={0,2}(?![A-Za-z0-9+/_=-])", RegexOptions.CultureInvariant);
    private static readonly Regex SecretValueRx = R(@"(" + SecretWords + @"\w*(?:\\?[""']?\s*[:=]\s*|\s+))((?:(?:bearer|basic|digest|negotiate|ntlm)\s+)?(?:\\?""[^""]*""|\\?'[^']*'|\S+))");
    private static readonly Regex SerialRx = R(@"\b(?:serial(?:\s*(?:number|no\.?))?|s/n|серийный\s+номер)\s*[:=#]?\s*[\w-]+");
    private static readonly Regex UsersPathLeftRx = R(@"[\\/]+(?:Users|home)[\\/]+");
    private static readonly Regex LongRunRx = R(@"(?=[A-Za-z0-9+/_-]*\d)(?=[A-Za-z0-9+/_-]*[A-Za-z])[A-Za-z0-9+/_-]{16,}", RegexOptions.CultureInvariant);

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
        // Paths first: the profile folder of ANY account is cut whole (also the user's own, before its name becomes «<user>»).
        line = UncPathRx.Replace(line, "<unc>");
        line = ProfilePathRx.Replace(line, "%USERPROFILE%");
        line = OneDriveRx.Replace(line, "OneDrive");
        if (namesRx is not null) line = namesRx.Replace(line, m => map.TryGetValue(m.Value, out var r) ? r : "<user>");
        line = Ipv6Rx.Replace(line, m => ReplaceIpv6(m.Value));
        line = MacRx.Replace(line, "<mac>");
        line = Ipv4Rx.Replace(line, m => System.Net.IPAddress.TryParse(m.Value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "<ip>" : m.Value);
        line = EmailRx.Replace(line, "<email>");
        line = BasicAuthRx.Replace(line, "Basic <redacted>");
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
        // The zone («%eth0», «%12») is cut before parsing and dropped: an adapter name is not diagnostics.
        var zone = candidate.IndexOf('%');
        var body = zone >= 0 ? candidate[..zone] : candidate;
        var core = body.TrimEnd(':', '.');
        var tail = zone >= 0 ? string.Empty : body[core.Length..];
        if (MacExactRx.IsMatch(core)) return "<mac>" + tail;           // a MAC written with colons (EUI-48 / EUI-64)
        if (core.Length > 0 && IsV6(core)) return "<ip>" + tail;
        // A trailing «:»/«.» can also belong to the address («fe80::1:»), try the whole text too.
        if (IsV6(body)) return "<ip>";
        // Looks like an address but does not parse (typo, glued text): the whole candidate goes.
        if (core.Contains("::") || core.Count(c => c == ':') >= 3) return "<ip>" + tail;
        return candidate;
    }

    private static bool IsV6(string text) => System.Net.IPAddress.TryParse(text, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    /// <summary>Second pass over a finished line: anything that would still identify the user means the line is dropped.</summary>
    private static bool StillLooksPersonal(string line)
    {
        if (line.Contains('@') && EmailRx.IsMatch(line)) return true;
        if (UsersPathLeftRx.IsMatch(line)) return true;
        if (SecretWordRx.IsMatch(line) && LongRunRx.IsMatch(line)) return true;
        return false;
    }
}
