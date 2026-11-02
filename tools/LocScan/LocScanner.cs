using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Monolith.LocScan;

/// <summary>What the scanner found in a source file.</summary>
/// <param name="Key">A Cyrillic literal that goes through Loc / a sink: its text is a translation key.</param>
/// <param name="Violation">A Cyrillic literal that is not wrapped (it would stay Russian in English).</param>
public enum FindingKind { Key, Violation }

public sealed record Finding(string File, int Line, FindingKind Kind, string Text, string Why);

/// <summary>
/// Finds the Cyrillic text of a source file and tells which of it goes through the localization API
/// (docs/localization.md, «Scanner rules»). Shared by the xUnit coverage test and the tools/LocScan console tool.
/// </summary>
public static class Scanner
{
    // Calls whose string arguments are translation keys. Loc.* are the API; the rest are sinks that translate by themselves.
    private static readonly Dictionary<string, int[]> KeyArgs = new(StringComparer.Ordinal)
    {
        ["Loc.T"] = [0], ["Loc.Tf"] = [0], ["Loc.Key"] = [0], ["Loc.Keep"] = [0], ["Loc.Plural"] = [1], ["Loc.N"] = [1],
        ["Log.Info"] = [0], ["Log.Success"] = [0], ["Log.Warn"] = [0], ["Log.Error"] = [0], ["Log.Command"] = [0],
        ["Ui.Toast"] = [-1], ["Ui.ConfirmAsync"] = [-1], ["Ui.AlertAsync"] = [-1], ["Ui.ChooseAsync"] = [-1], ["Ui.ConfirmOptionAsync"] = [-1],
    };

    // The other word forms of a plural call: literals, but not separate keys (they live in the "one|other" value of the first form).
    private static readonly Dictionary<string, int[]> FormArgs = new(StringComparer.Ordinal)
    {
        ["Loc.Plural"] = [2, 3], ["Loc.N"] = [2, 3],
    };

    public static bool HasCyrillic(string s)
    {
        foreach (var c in s)
            if (c is >= 'Ѐ' and <= 'ӿ') return true;
        return false;
    }

    // ───────────────────────────── C# ─────────────────────────────

    private sealed class Tok
    {
        public char Kind;            // 's' string, 'i' identifier, 'p' punctuation
        public string Value = "";
        public int Line;
        public bool Interp;
        public bool CyrText;         // string: has Cyrillic outside interpolation holes
        public bool Covered;
    }

    public static IReadOnlyList<Finding> ScanCs(string file, string text, ISet<string>? ignore = null)
    {
        var toks = new List<Tok>();
        var ignoredLines = new HashSet<int>();
        var line = 1;
        var i = 0;
        Lex(text, ref i, toks, ignoredLines, ref line, inHole: false);

        var findings = new List<Finding>();
        Analyse(toks, findings, file, ignoredLines, ignore);
        return findings;
    }

    private static void Lex(string s, ref int i, List<Tok> toks, HashSet<int> ignoredLines, ref int line, bool inHole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                var end = s.IndexOf('\n', i);
                if (end < 0) end = s.Length;
                if (s.AsSpan(i, end - i).Contains("loc:ignore", StringComparison.Ordinal)) ignoredLines.Add(line);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) end = s.Length - 2;
                var body = s.Substring(i, end + 2 - i);
                if (body.Contains("loc:ignore", StringComparison.Ordinal)) ignoredLines.Add(line);
                line += body.Count(ch => ch == '\n');
                i = end + 2;
                continue;
            }
            if (c == '#' && AtLineStart(s, i))
            {
                var end = s.IndexOf('\n', i);
                i = end < 0 ? s.Length : end;
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < s.Length && s[i] != '\'' && s[i] != '\n') { if (s[i] == '\\') i++; i++; }
                i++;
                toks.Add(new Tok { Kind = 'p', Value = "'", Line = line });
                continue;
            }
            // string forms: " $" @" $@" @$" """
            if (c == '"' || ((c == '$' || c == '@') && IsStringStart(s, i)))
            {
                toks.Add(LexString(s, ref i, toks, ignoredLines, ref line));
                continue;
            }
            if (char.IsLetter(c) || c == '_' || c == '@')
            {
                var st = i;
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(new Tok { Kind = 'i', Value = s.Substring(st, i - st), Line = line });
                continue;
            }
            if (inHole)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) return; // end of the interpolation hole (the caller consumes the brace)
                    depth--;
                }
            }
            toks.Add(new Tok { Kind = 'p', Value = c.ToString(), Line = line });
            i++;
        }
    }

    private static bool AtLineStart(string s, int i)
    {
        for (var k = i - 1; k >= 0 && s[k] != '\n'; k--)
            if (!char.IsWhiteSpace(s[k])) return false;
        return true;
    }

    private static bool IsStringStart(string s, int i)
    {
        // $" $@" @" @$" $$" (raw handled by the quote count)
        var k = i;
        while (k < s.Length && (s[k] == '$' || s[k] == '@')) k++;
        return k < s.Length && s[k] == '"';
    }

    private static Tok LexString(string s, ref int i, List<Tok> toks, HashSet<int> ignoredLines, ref int line)
    {
        var startLine = line;
        var interp = false;
        var verbatim = false;
        var dollars = 0;
        while (s[i] != '"')
        {
            if (s[i] == '$') { interp = true; dollars++; }
            else verbatim = true;
            i++;
        }
        // raw string: three or more quotes
        var quotes = 0;
        while (i + quotes < s.Length && s[i + quotes] == '"') quotes++;
        var tok = new Tok { Kind = 's', Line = startLine, Interp = interp };
        var sb = new StringBuilder();
        var cyr = false;
        if (quotes >= 3)
        {
            i += quotes;
            var close = new string('"', quotes);
            while (i < s.Length && string.CompareOrdinal(s, i, close, 0, quotes) != 0)
            {
                if (interp && s[i] == '{' && (dollars == 1 ? (i + 1 >= s.Length || s[i + 1] != '{') : true))
                {
                    i++;
                    LexHole(s, ref i, toks, ignoredLines, ref line);
                    continue;
                }
                if (s[i] == '\n') line++;
                if (c(s[i])) cyr = true;
                sb.Append(s[i]);
                i++;
            }
            i += quotes;
        }
        else
        {
            i++; // opening quote
            while (i < s.Length)
            {
                var ch = s[i];
                if (ch == '\n') line++;
                if (verbatim)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        break;
                    }
                }
                else
                {
                    if (ch == '"') break;
                    if (ch == '\\' && i + 1 < s.Length)
                    {
                        i = Unescape(s, i, sb);
                        continue;
                    }
                }
                if (interp && ch == '{')
                {
                    if (i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                    i++;
                    LexHole(s, ref i, toks, ignoredLines, ref line);
                    continue;
                }
                if (interp && ch == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
                if (c(ch)) cyr = true;
                sb.Append(ch);
                i++;
            }
            i++; // closing quote
        }
        tok.Value = sb.ToString();
        tok.CyrText = cyr;
        return tok;

        static bool c(char ch) => ch is >= 'Ѐ' and <= 'ӿ';
    }

    private static void LexHole(string s, ref int i, List<Tok> toks, HashSet<int> ignoredLines, ref int line)
    {
        // Tokens of the hole are lexed into the same list so Cyrillic strings inside it are seen; the format part
        // after ':' or ',' lexes harmlessly. The matching brace ends it.
        Lex(s, ref i, toks, ignoredLines, ref line, inHole: true);
        i++; // the closing brace
    }

    private static int Unescape(string s, int i, StringBuilder sb)
    {
        var n = s[i + 1];
        switch (n)
        {
            case 'n': sb.Append('\n'); return i + 2;
            case 'r': sb.Append('\r'); return i + 2;
            case 't': sb.Append('\t'); return i + 2;
            case '0': sb.Append('\0'); return i + 2;
            case 'a': sb.Append('\a'); return i + 2;
            case 'b': sb.Append('\b'); return i + 2;
            case 'f': sb.Append('\f'); return i + 2;
            case 'v': sb.Append('\v'); return i + 2;
            case 'u' when i + 5 < s.Length + 0 && int.TryParse(s.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out var u):
                sb.Append((char)u); return i + 6;
            case 'x':
            {
                var k = i + 2;
                while (k < s.Length && k < i + 6 && Uri.IsHexDigit(s[k])) k++;
                if (k > i + 2 && int.TryParse(s.AsSpan(i + 2, k - i - 2), System.Globalization.NumberStyles.HexNumber, null, out var x)) { sb.Append((char)x); return k; }
                sb.Append(n); return i + 2;
            }
            default: sb.Append(n); return i + 2;
        }
    }

    private sealed class Frame
    {
        public string Callee = "";
        public char Open;
        public List<List<Tok>> Args = [];
        public List<Tok> Cur = [];
    }

    private static void Analyse(List<Tok> toks, List<Finding> findings, string file, HashSet<int> ignoredLines, ISet<string>? ignore)
    {
        var stack = new Stack<Frame>();
        for (var t = 0; t < toks.Count; t++)
        {
            var tok = toks[t];
            if (tok.Kind == 'p' && tok.Value is "(" or "[" or "{")
            {
                var frame = new Frame { Open = tok.Value[0] };
                if (tok.Value == "(") frame.Callee = CalleeBefore(toks, t);
                stack.Push(frame);
                continue;
            }
            if (tok.Kind == 'p' && tok.Value is ")" or "]" or "}")
            {
                if (stack.Count == 0) continue;
                var frame = stack.Pop();
                frame.Args.Add(frame.Cur);
                Evaluate(frame, findings, file);
                continue;
            }
            if (stack.Count == 0) continue;
            if (tok.Kind == 'p' && tok.Value == ",") { stack.Peek().Args.Add(stack.Peek().Cur); stack.Peek().Cur = []; continue; }
            stack.Peek().Cur.Add(tok);
        }

        foreach (var tok in toks)
        {
            if (tok.Kind != 's' || tok.Covered) continue;
            if (tok.Interp)
            {
                if (tok.CyrText && !ignoredLines.Contains(tok.Line) && !(ignore?.Contains(tok.Value) ?? false))
                    findings.Add(new Finding(file, tok.Line, FindingKind.Violation, tok.Value, "interpolated string: use Loc.Tf(\"… {0}\", x) / Log.Info(\"… {0}\", x)"));
                continue;
            }
            if (!HasCyrillic(tok.Value)) continue;
            if (ignoredLines.Contains(tok.Line) || (ignore?.Contains(tok.Value) ?? false)) continue;
            findings.Add(new Finding(file, tok.Line, FindingKind.Violation, tok.Value, "not wrapped: Loc.T(…), Loc.Key(…) or // loc:ignore"));
        }
        findings.Sort((a, b) => a.Line.CompareTo(b.Line));
    }

    private static string CalleeBefore(List<Tok> toks, int open)
    {
        // Loc . T (  → "Loc.T";  Monolith . Core . Log . Warn ( → "Log.Warn" (last two names)
        var names = new List<string>();
        var k = open - 1;
        while (k >= 0 && toks[k].Kind == 'i')
        {
            names.Insert(0, toks[k].Value);
            if (k >= 2 && toks[k - 1].Kind == 'p' && toks[k - 1].Value == "." && toks[k - 2].Kind == 'i') k -= 2;
            else break;
        }
        if (names.Count == 0) return "";
        return names.Count == 1 ? names[0] : names[^2] + "." + names[^1];
    }

    private static void Evaluate(Frame frame, List<Finding> findings, string file)
    {
        if (frame.Callee.Length == 0 || !KeyArgs.TryGetValue(frame.Callee, out var keyIdx)) return;
        FormArgs.TryGetValue(frame.Callee, out var formIdx);
        for (var a = 0; a < frame.Args.Count; a++)
        {
            var isKey = keyIdx.Contains(a) || keyIdx.Contains(-1);
            var isForm = formIdx is not null && formIdx.Contains(a);
            if (!isKey && !isForm) continue;
            var arg = frame.Args[a];
            // strip a named-argument prefix: confirmText: "…"
            var from = arg.Count >= 2 && arg[0].Kind == 'i' && arg[1].Kind == 'p' && arg[1].Value == ":" ? 2 : 0;
            var parts = new List<Tok>();
            var ok = from < arg.Count;
            for (var q = from; q < arg.Count; q++)
            {
                if (q % 2 == from % 2)
                {
                    if (arg[q].Kind != 's' || arg[q].Interp) { ok = false; break; }
                    parts.Add(arg[q]);
                }
                else if (!(arg[q].Kind == 'p' && arg[q].Value == "+")) { ok = false; break; }
            }
            if (!ok || parts.Count == 0) continue;
            var key = string.Concat(parts.Select(p => p.Value));
            foreach (var p in parts) p.Covered = true;
            if (HasCyrillic(key) && isKey && frame.Callee != "Loc.Keep") findings.Add(new Finding(file, parts[0].Line, FindingKind.Key, key, frame.Callee));
        }
    }

    // ───────────────────────────── XAML ─────────────────────────────

    private static readonly Regex XamlComment = new(@"<!--[\s\S]*?-->", RegexOptions.Compiled);
    private static readonly Regex LocT = new(@"\{loc:T\s+(?:'(?<q>[^']*)'|(?<p>[^}']*))\}", RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"(?<name>[\w:.]+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TextNode = new(@">(?<t>[^<>]*)<", RegexOptions.Compiled);

    public static IReadOnlyList<Finding> ScanXaml(string file, string text, ISet<string>? ignore = null)
    {
        var findings = new List<Finding>();
        // Comments are blanked (newlines kept) so line numbers stay right; a comment with loc:ignore protects its own line.
        var ignoredLines = new HashSet<int>();
        text = XamlComment.Replace(text, m =>
        {
            if (m.Value.Contains("loc:ignore", StringComparison.Ordinal))
            {
                var ln = LineOf(text, m.Index);
                ignoredLines.Add(ln);
            }
            return Regex.Replace(m.Value, @"[^\r\n]", " ");
        });

        foreach (Match m in Attr.Matches(text))
        {
            var raw = m.Groups["v"].Value;
            if (!HasCyrillic(raw)) continue;
            var line = LineOf(text, m.Index);
            var rest = raw;
            foreach (Match k in LocT.Matches(raw))
            {
                var key = WebUtility.HtmlDecode(k.Groups["q"].Success ? k.Groups["q"].Value : k.Groups["p"].Value.Trim());
                if (HasCyrillic(key)) findings.Add(new Finding(file, line, FindingKind.Key, key, "loc:T"));
                rest = rest.Replace(k.Value, "");
            }
            if (!HasCyrillic(rest)) continue;
            var decoded = WebUtility.HtmlDecode(raw);
            if (ignoredLines.Contains(line) || (ignore?.Contains(decoded) ?? false)) continue;
            findings.Add(new Finding(file, line, FindingKind.Violation, decoded, "attribute text: use {loc:T …}"));
        }
        foreach (Match m in TextNode.Matches(text))
        {
            var t = m.Groups["t"].Value;
            if (!HasCyrillic(t)) continue;
            var line = LineOf(text, m.Groups["t"].Index);
            var decoded = WebUtility.HtmlDecode(t.Trim());
            if (ignoredLines.Contains(line) || (ignore?.Contains(decoded) ?? false)) continue;
            findings.Add(new Finding(file, line, FindingKind.Violation, decoded, "element text: use an attribute with {loc:T …}"));
        }
        findings.Sort((a, b) => a.Line.CompareTo(b.Line));
        return findings;
    }

    private static int LineOf(string text, int index)
    {
        var n = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    public static IReadOnlyList<Finding> ScanFile(string path, string relative, ISet<string>? ignore = null)
    {
        var text = File.ReadAllText(path);
        return relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ? ScanXaml(relative, text, ignore)
            : relative.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? ScanCs(relative, text, ignore)
            : [];
    }

    // ───────────────────────────── Area sources ─────────────────────────────

    /// <summary>Source files (relative to <paramref name="srcRoot"/>, '/' separators) matching a list of globs ("Shell/*", "Modules/Settings/**").</summary>
    public static IReadOnlyList<string> ExpandSources(string srcRoot, IEnumerable<string> globs)
    {
        var all = Directory.EnumerateFiles(srcRoot, "*.*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(srcRoot, f).Replace('\\', '/'))
            .Where(f => (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        && !f.StartsWith("obj/", StringComparison.Ordinal) && !f.StartsWith("bin/", StringComparison.Ordinal))
            .ToList();
        var regexes = globs.Select(GlobToRegex).ToList();
        return all.Where(f => regexes.Any(r => r.IsMatch(f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            if (glob[i] == '*')
            {
                if (i + 1 < glob.Length && glob[i + 1] == '*') { sb.Append(".*"); i++; if (i + 1 < glob.Length && glob[i + 1] == '/') i++; }
                else sb.Append("[^/]*");
            }
            else sb.Append(Regex.Escape(glob[i].ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }
}
