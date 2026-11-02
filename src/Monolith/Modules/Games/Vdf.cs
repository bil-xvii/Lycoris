using System.Text;

namespace Monolith.Modules.Games;

/// <summary>A node of Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf).</summary>
public sealed class VdfNode
{
    private readonly Dictionary<string, VdfNode> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, VdfNode>> _children = [];

    public VdfNode(string? value = null) => Value = value;

    /// <summary>String value of a leaf; null for an object node.</summary>
    public string? Value { get; }

    /// <summary>Children in file order (duplicate keys are kept here; lookups return the first).</summary>
    public IReadOnlyList<KeyValuePair<string, VdfNode>> Children => _children;

    public VdfNode? this[string key] => _byKey.GetValueOrDefault(key);

    /// <summary>Value of a direct child leaf.</summary>
    public string? Get(string key) => this[key]?.Value;

    internal void Add(string key, VdfNode node)
    {
        _children.Add(new(key, node));
        _byKey.TryAdd(key, node);
    }
}

/// <summary>
/// Tolerant parser for Valve's text KeyValues: quoted/unquoted tokens, escapes (\\ \" \n \t),
/// // comments, [$PLATFORM] conditionals. Never throws on malformed input — returns what it could read.
/// </summary>
public static class VdfParser
{
    /// <summary>Real files nest 4-5 levels; a deeper file is hostile.</summary>
    public const int MaxDepth = 64;

    private enum Kind { Text, Open, Close, Conditional }

    private readonly record struct Token(Kind Kind, string Text);

    public static VdfNode Parse(string? text)
    {
        var root = new VdfNode();
        if (string.IsNullOrEmpty(text)) return root;
        var pos = 0;
        ParseObject(text, ref pos, root, topLevel: true, depth: 0);
        return root;
    }

    private static void ParseObject(string s, ref int pos, VdfNode target, bool topLevel, int depth)
    {
        if (depth > MaxDepth) { pos = s.Length; return; } // hostile nesting: stop (a stack overflow cannot be caught)
        while (true)
        {
            var tok = Next(s, ref pos);
            if (tok is null) return;
            switch (tok.Value.Kind)
            {
                case Kind.Close:
                    if (topLevel) continue; // stray brace at top level
                    return;
                case Kind.Open:
                    ParseObject(s, ref pos, new VdfNode(), topLevel: false, depth + 1); // anonymous block: skip
                    continue;
                case Kind.Conditional:
                    continue;
            }

            var key = tok.Value.Text;
            var next = Next(s, ref pos);
            while (next is { Kind: Kind.Conditional }) next = Next(s, ref pos);
            if (next is null)
            {
                target.Add(key, new VdfNode(""));
                return;
            }
            switch (next.Value.Kind)
            {
                case Kind.Open:
                    var child = new VdfNode();
                    ParseObject(s, ref pos, child, topLevel: false, depth + 1);
                    target.Add(key, child);
                    break;
                case Kind.Text:
                    target.Add(key, new VdfNode(next.Value.Text));
                    break;
                case Kind.Close:
                    target.Add(key, new VdfNode(""));
                    if (!topLevel) return;
                    break;
            }
        }
    }

    private static Token? Next(string s, ref int pos)
    {
        while (pos < s.Length)
        {
            var c = s[pos];
            if (char.IsWhiteSpace(c) || c == '﻿') { pos++; continue; }
            if (c == '/' && pos + 1 < s.Length && s[pos + 1] == '/')
            {
                while (pos < s.Length && s[pos] != '\n') pos++;
                continue;
            }
            if (c == '{') { pos++; return new Token(Kind.Open, "{"); }
            if (c == '}') { pos++; return new Token(Kind.Close, "}"); }
            if (c == '[')
            {
                var end = s.IndexOf(']', pos);
                var text = end < 0 ? s[pos..] : s[pos..(end + 1)];
                pos = end < 0 ? s.Length : end + 1;
                return new Token(Kind.Conditional, text);
            }
            if (c == '"') return new Token(Kind.Text, ReadQuoted(s, ref pos));
            return new Token(Kind.Text, ReadBare(s, ref pos));
        }
        return null;
    }

    private static string ReadQuoted(string s, ref int pos)
    {
        pos++; // opening quote
        var sb = new StringBuilder();
        while (pos < s.Length)
        {
            var c = s[pos];
            if (c == '"') { pos++; return sb.ToString(); }
            if (c == '\\' && pos + 1 < s.Length)
            {
                var n = s[pos + 1];
                switch (n)
                {
                    case '\\': sb.Append('\\'); pos += 2; continue;
                    case '"': sb.Append('"'); pos += 2; continue;
                    case 'n': sb.Append('\n'); pos += 2; continue;
                    case 't': sb.Append('\t'); pos += 2; continue;
                    default: sb.Append(c); pos++; continue; // unknown escape: keep the backslash literally
                }
            }
            sb.Append(c);
            pos++;
        }
        return sb.ToString(); // unterminated string: take the rest
    }

    private static string ReadBare(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length && !char.IsWhiteSpace(s[pos]) && s[pos] is not ('{' or '}' or '"')) pos++;
        return s[start..pos];
    }
}
