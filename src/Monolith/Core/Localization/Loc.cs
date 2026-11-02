using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Monolith.Core.Localization;

/// <summary>
/// One translation file (<c>Core/Localization/en/&lt;Area&gt;.json</c>): the English table of one area of the app.
/// </summary>
/// <param name="Name">Area name = file name without extension ("Shell", "Settings", "Optimize"…).</param>
/// <param name="Translated">The owner declares the area finished: the coverage test then demands that every Cyrillic literal
/// in <paramref name="Sources"/> is a key of this table (or is ignored on purpose).</param>
/// <param name="Sources">Source files of the area, relative to <c>src/Monolith</c>; <c>*</c> and <c>**</c> globs allowed.</param>
/// <param name="Ignore">Cyrillic literals that stay Russian on purpose (Windows output patterns, file formats…).</param>
/// <param name="Entries">Russian text (the key) → English text.</param>
public sealed record LocArea(string Name, bool Translated, IReadOnlyList<string> Sources, IReadOnlyList<string> Ignore,
    IReadOnlyDictionary<string, string> Entries);

/// <summary>
/// Localization. The RUSSIAN TEXT ITSELF is the key: <c>Loc.T("Применить")</c> shows «Применить» when the language is ru
/// and «Apply» when it is en. A text without an English entry falls back to the Russian original and is remembered in
/// <see cref="MissingKeys"/>. Tables are plain dictionaries filled once at startup from the embedded files
/// <c>Core/Localization/en/*.json</c> (one file per area, so parallel work never touches the same file). See docs/localization.md.
/// </summary>
public static class Loc
{
    public const string Ru = "ru";
    public const string En = "en";
    public const string ModeAuto = "auto";

    public static readonly CultureInfo RuCulture = CultureInfo.GetCultureInfo("ru-RU");
    public static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");

    private static readonly object Gate = new();
    private static volatile string _language = Ru;
    private static volatile string _mode = ModeAuto;
    private static readonly AsyncLocal<string?> Scoped = new();

    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);
    private static readonly List<LocArea> _areas = [];
    private static readonly ConcurrentDictionary<string, byte> _missing = new(StringComparer.Ordinal);
    private static readonly List<string> _conflicts = [];
    private static bool _loaded;

    /// <summary>Effective language of the UI: "ru" or "en". Russian until <see cref="Initialize"/> says otherwise.</summary>
    public static string Language => Scoped.Value ?? _language;

    public static bool IsEnglish => Language == En;

    /// <summary>The user's choice: "auto", "ru" or "en" (what the Settings page shows).</summary>
    public static string Mode => _mode;

    /// <summary>
    /// Culture for numbers and dates that are shown to the user: ru-RU or en-US. Use it instead of a private
    /// <c>CultureInfo.GetCultureInfo("ru-RU")</c>. Never for numbers that are parsed or stored (those stay Invariant).
    /// </summary>
    public static CultureInfo Culture => IsEnglish ? EnCulture : RuCulture;

    // ───────────────────────────── Setup ─────────────────────────────

    /// <summary>
    /// "auto" → the Windows display language (ru* → ru, anything else → en); "ru"; "en". Unknown values count as "auto".
    /// Pure: <paramref name="uiCultureName"/> is passed in so tests do not depend on the machine.
    /// </summary>
    public static string ResolveLanguage(string? mode, string? uiCultureName)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case Ru: return Ru;
            case En: return En;
        }
        var name = (uiCultureName ?? "").Trim();
        return name.StartsWith("ru", StringComparison.OrdinalIgnoreCase) && (name.Length == 2 || name[2] == '-' || name[2] == '_') ? Ru : En;
    }

    /// <summary>The mode written to settings: only "auto", "ru", "en" (anything else → "auto").</summary>
    public static string NormalizeMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        Ru => Ru,
        En => En,
        _ => ModeAuto,
    };

    /// <summary>
    /// Called once at startup, before any window or view-model exists. Chooses the language, loads the English tables
    /// (only for English: Russian needs none) and, for English, sets the process culture to en-US so numbers and dates
    /// look natural. Russian leaves the culture untouched (no change of behaviour).
    /// </summary>
    public static void Initialize(string? mode, string? uiCultureName = null)
    {
        _mode = NormalizeMode(mode);
        _language = ResolveLanguage(_mode, uiCultureName ?? CultureInfo.CurrentUICulture.Name);
        if (_language != En) return;
        LoadEmbeddedAreas();
        CultureInfo.DefaultThreadCurrentCulture = EnCulture;
        CultureInfo.DefaultThreadCurrentUICulture = EnCulture;
        CultureInfo.CurrentCulture = EnCulture;
        CultureInfo.CurrentUICulture = EnCulture;
    }

    /// <summary>TESTS / TOOLS: the language for the current async flow only (parallel tests do not disturb each other).</summary>
    public static IDisposable UseLanguage(string language)
    {
        if (language == En) LoadEmbeddedAreas();
        var previous = Scoped.Value;
        Scoped.Value = language == En ? En : Ru;
        return new Restore(() => Scoped.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // ───────────────────────────── Tables ─────────────────────────────

    /// <summary>All registered areas (embedded files first, then <see cref="RegisterArea"/> ones), in registration order.</summary>
    public static IReadOnlyList<LocArea> Areas
    {
        get { lock (Gate) return _areas.ToList(); }
    }

    /// <summary>Texts asked for in English that have no entry (Russian was shown instead). Cleared by <see cref="ClearMissing"/>.</summary>
    public static IReadOnlyCollection<string> MissingKeys => _missing.Keys.ToList();

    public static void ClearMissing() => _missing.Clear();

    /// <summary>Keys defined twice with different English texts («first wins»). Must stay empty: one term, one translation.</summary>
    public static IReadOnlyList<string> Conflicts
    {
        get { lock (Gate) return _conflicts.ToList(); }
    }

    /// <summary>Loads every embedded <c>Monolith.Loc.en.*.json</c> once ("Common" first, then alphabetically).</summary>
    public static void LoadEmbeddedAreas()
    {
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;
            var assembly = typeof(Loc).Assembly;
            const string prefix = "Monolith.Loc.en.";
            var names = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
                .OrderBy(n => n.Equals(prefix + "Common.json", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(n => n, StringComparer.Ordinal);
            foreach (var resource in names)
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null) continue;
                var fallbackName = resource[prefix.Length..^".json".Length];
                try { AddArea(ParseArea(stream, fallbackName)); }
                catch (Exception ex) { _conflicts.Add($"{fallbackName}: файл перевода не прочитан — {ex.Message}"); }
            }
        }
    }

    /// <summary>Registers a table from code (tests, tools). The same rules as for a file: the first definition of a key wins.</summary>
    public static void RegisterArea(string name, IReadOnlyDictionary<string, string> table, bool translated = false,
        IReadOnlyList<string>? sources = null, IReadOnlyList<string>? ignore = null)
    {
        lock (Gate) AddArea(new LocArea(name, translated, sources ?? [], ignore ?? [], table));
    }

    private static void AddArea(LocArea area)
    {
        // Caller holds Gate. Copy-on-write: readers use the dictionary without a lock.
        var next = new Dictionary<string, string>(_table, StringComparer.Ordinal);
        foreach (var (ru, en) in area.Entries)
        {
            if (next.TryGetValue(ru, out var existing))
            {
                if (!string.Equals(existing, en, StringComparison.Ordinal))
                    _conflicts.Add($"{area.Name}: «{Shorten(ru)}» уже переведён иначе: «{Shorten(existing)}» ≠ «{Shorten(en)}»");
                continue;
            }
            next[ru] = en;
        }
        _areas.Add(area);
        _table = next;
    }

    private static string Shorten(string s) => s.Length > 60 ? s[..60] + "…" : s;

    /// <summary>Reads one area file (see docs/localization.md for the format).</summary>
    public static LocArea ParseArea(Stream json, string fallbackName)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var name = root.TryGetProperty("area", out var a) && a.GetString() is { Length: > 0 } n ? n : fallbackName;
        var translated = root.TryGetProperty("translated", out var t) && t.ValueKind == JsonValueKind.True;
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("en", out var en) && en.ValueKind == JsonValueKind.Object)
            foreach (var p in en.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) entries[p.Name] = p.Value.GetString() ?? "";
        return new LocArea(name, translated, Strings(root, "sources"), Strings(root, "ignore"), entries);

        static List<string> Strings(JsonElement parent, string property) =>
            parent.TryGetProperty(property, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
                : [];
    }

    // ───────────────────────────── Lookup ─────────────────────────────

    /// <summary>
    /// Translates a text whose Russian original is the key. Russian: returned as is. English: the table entry; if there is
    /// none, the Russian text (and the key is remembered in <see cref="MissingKeys"/>). Text without Cyrillic is returned as is.
    /// </summary>
    public static string T(string ru)
    {
        if (Language != En || string.IsNullOrEmpty(ru)) return ru;
        if (_table.TryGetValue(ru, out var en)) return en;
        if (HasCyrillic(ru)) _missing.TryAdd(ru, 0);
        return ru;
    }

    /// <summary>
    /// Marks a Russian literal as a translation KEY that is translated later, where it is shown (static tables, catalogs,
    /// data classes): <c>new Item(Loc.Key("Игровой режим"))</c> … <c>Text = Loc.T(item.Title)</c>. Returns the text unchanged.
    /// </summary>
    public static string Key(string ru) => ru;

    /// <summary>
    /// Like <see cref="T"/> but for text that may legitimately be absent from the tables: a text that arrives from outside or
    /// from a not yet translated area (the safety net of dialogs, toasts, the log). Does not record a missing key.
    /// </summary>
    public static string Sink(string? text)
    {
        if (string.IsNullOrEmpty(text) || Language != En) return text ?? "";
        return _table.TryGetValue(text, out var en) ? en : text;
    }

    /// <summary>
    /// A formatted text: the RUSSIAN FORMAT is the key, <c>{0}</c> placeholders keep their numbers in both languages
    /// (an English text may reorder them). <c>Loc.Tf("Выбрано {0} из {1}", n, total)</c>.
    /// </summary>
    public static string Tf(string ruFormat, params object?[] args)
    {
        var format = T(ruFormat);
        try { return string.Format(IsEnglish ? EnCulture : CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return format; }
    }

    /// <summary>Russian plural category: 0 = one (1, 21, 101…), 1 = few (2–4, 22…), 2 = many (0, 5–20, 25…).</summary>
    public static int RuForm(long n)
    {
        n = Math.Abs(n);
        var m10 = n % 10;
        var m100 = n % 100;
        if (m10 == 1 && m100 != 11) return 0;
        if (m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14)) return 1;
        return 2;
    }

    /// <summary>
    /// The right word form for a number, WITHOUT the number: <c>Loc.Plural(n, "файл", "файла", "файлов")</c> →
    /// «файл/файла/файлов» in Russian, «file/files» in English. The table key is the first (singular) Russian form and the
    /// English value is <c>"one|other"</c> (<c>"file|files"</c>).
    /// </summary>
    public static string Plural(long n, string one, string few, string many)
    {
        if (Language == En)
        {
            if (_table.TryGetValue(one, out var forms))
            {
                var bar = forms.IndexOf('|');
                return bar < 0 ? forms : Math.Abs(n) == 1 ? forms[..bar] : forms[(bar + 1)..];
            }
            if (HasCyrillic(one)) _missing.TryAdd(one, 0);
        }
        return RuForm(n) switch { 0 => one, 1 => few, _ => many };
    }

    /// <summary>
    /// A counted phrase: <c>Loc.N(n, "{0} файл", "{0} файла", "{0} файлов")</c> → «5 файлов» / «1 file» / «5 files».
    /// English table: key "{0} файл" → "{0} file|{0} files". <c>{0}</c> is the number.
    /// </summary>
    public static string N(long n, string one, string few, string many) =>
        string.Format(IsEnglish ? EnCulture : CultureInfo.CurrentCulture, Plural(n, one, few, many), n);

    /// <summary>Marks a Russian literal that must stay Russian in every language (a Windows output pattern, a bilingual label). Returns it unchanged.</summary>
    public static string Keep(string text) => text;

    // ───────────── Dates and times: the same facts, in the format of the language ─────────────

    /// <summary>"02.11.2026" / "Nov 2, 2026".</summary>
    public static string DateText(DateTime t) => IsEnglish ? t.ToString("MMM d, yyyy", EnCulture) : t.ToString("dd.MM.yyyy", RuCulture);

    /// <summary>"02.11.2026 15:40" / "Nov 2, 2026 15:40".</summary>
    public static string DateTimeText(DateTime t) => IsEnglish ? t.ToString("MMM d, yyyy HH:mm", EnCulture) : t.ToString("dd.MM.yyyy HH:mm", RuCulture);

    /// <summary>"15:40" in both languages.</summary>
    public static string TimeText(DateTime t) => t.ToString("HH:mm", Culture);

    /// <summary>A short duration in seconds: "0.5 с" / "0.5 s" (the decimal separator of the language).</summary>
    public static string SecondsText(double seconds, string format = "0.#") => Tf("{0} с", seconds.ToString(format, Culture));

    public static bool HasCyrillic(string s)
    {
        foreach (var c in s)
            if (c is >= 'Ѐ' and <= 'ӿ') return true;
        return false;
    }
}

/// <summary>The list of areas the owner declared finished (those with <c>"translated": true</c> in their file).</summary>
public static class LocAreas
{
    public static IReadOnlyList<LocArea> Translated
    {
        get
        {
            Loc.LoadEmbeddedAreas();
            return Loc.Areas.Where(a => a.Translated).ToList();
        }
    }
}
