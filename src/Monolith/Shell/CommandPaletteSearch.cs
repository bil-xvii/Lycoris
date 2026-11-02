using System.Text;
using Monolith.Tweaks;

namespace Monolith.Shell;

/// <summary>What a command-palette result points to.</summary>
/// <remarks><see cref="Myth"/> = a rejected tweak («НЕ ДЕЛАЕМ»): opens its explanation in Settings → О программе.</remarks>
public enum PaletteKind { Page, Tweak, Tool, Service, Myth, App, Clean, Startup, Setting }

/// <summary>
/// One searchable entry of the command palette. Opening it only navigates (and may ask the page to focus an item):
/// the palette never applies, runs or changes anything itself.
/// </summary>
public sealed class PaletteEntry
{
    private string? _nTitle, _nKeywords, _nDescription;

    public required PaletteKind Kind { get; init; }
    /// <summary>The title in the interface language (a Russian original the tables know is translated; the original stays searchable).</summary>
    public required string Title { get => _title; init { _ruTitle = value; _title = Loc.Sink(value); } }
    private string _title = "", _ruTitle = "";
    /// <summary>Plain-language description, shown as one muted line for the selected result.</summary>
    public string Description { get => _description; init => _description = Loc.Sink(value); }
    private string _description = "";
    /// <summary>Extra searchable text that is not displayed (id, category, command, service name). Russian aliases are kept in every language.</summary>
    public string Keywords { get; init; } = "";
    /// <summary>Set for tweaks (and curated services): drives the risk tag.</summary>
    public TweakRisk? Risk { get; init; }
    /// <summary>Page to navigate to.</summary>
    public required string PageKey { get; init; }
    /// <summary>Item the target page should scroll to and highlight (tweak id, service name), or null.</summary>
    public string? FocusId { get; init; }
    /// <summary>A part of a page (a tab), found by search but not listed with the pages for an empty query.</summary>
    public bool IsSection { get; init; }

    public string KindText => Kind switch
    {
        PaletteKind.Page => Loc.T("РАЗДЕЛ"),
        PaletteKind.Tweak => Loc.T("НАСТРОЙКА"),
        PaletteKind.Tool => Loc.T("ИНСТРУМЕНТ"),
        PaletteKind.Myth => Loc.T("НЕ ДЕЛАЕМ"),
        PaletteKind.App => Loc.T("ПРОГРАММА"),
        PaletteKind.Clean => Loc.T("ОЧИСТКА"),
        PaletteKind.Startup => Loc.T("АВТОЗАПУСК"),
        PaletteKind.Setting => Loc.T("ПАРАМЕТР"),
        _ => Loc.T("СЛУЖБА"),
    };

    /// <summary>Risk tag text, only for tweaks (the owner's rule: no tags on other rows).</summary>
    public string? RiskText => Kind == PaletteKind.Tweak && Risk is { } r ? CommandPaletteSearch.RiskText(r) : null;
    public bool HasRisk => RiskText is not null;

    internal string NTitle => _nTitle ??= CommandPaletteSearch.Normalize(Title);
    // In English the Russian title and keywords stay searchable too (someone types «диск» into the English UI).
    internal string NKeywords => _nKeywords ??= CommandPaletteSearch.Normalize(Keywords + " " + Loc.Sink(Keywords) + (_ruTitle != _title ? " " + _ruTitle : ""));
    internal string NDescription => _nDescription ??= CommandPaletteSearch.Normalize(Description);
}

/// <summary>Pure search / ranking for the command palette (unit-tested, no UI, no system access).</summary>
public static class CommandPaletteSearch
{
    public const int DefaultMax = 12;

    public static string RiskText(TweakRisk risk) => risk switch
    {
        TweakRisk.Safe => Loc.T("БЕЗОПАСНО"),
        TweakRisk.Optional => Loc.T("ПО ЖЕЛАНИЮ"),
        _ => Loc.T("ЭКСПЕРТ"),
    };

    /// <summary>Lower case, ё → е, every non-letter/digit becomes one space, trimmed.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var space = true;
        foreach (var raw in text)
        {
            var c = char.ToLowerInvariant(raw);
            if (c == 'ё') c = 'е';
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static string[] Tokenize(string? query) =>
        Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Every query word must match the title, keywords or description. Title matches rank above keyword matches,
    /// which rank above description matches; a word start beats a match inside a word. Empty query = the pages (no sections).
    /// </summary>
    public static IReadOnlyList<PaletteEntry> Search(IReadOnlyList<PaletteEntry> index, string? query, int max = DefaultMax)
    {
        var tokens = Tokenize(query);
        if (tokens.Length == 0)
            return index.Where(e => e.Kind == PaletteKind.Page && !e.IsSection).Take(max).ToList();

        var phrase = string.Join(' ', tokens);
        var scored = new List<(PaletteEntry Entry, int Score, int Order)>();
        for (var i = 0; i < index.Count; i++)
        {
            var score = Score(index[i], tokens, phrase);
            if (score > 0) scored.Add((index[i], score, i));
        }
        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Entry.Kind)
            .ThenBy(s => s.Order)
            .Take(max)
            .Select(s => s.Entry)
            .ToList();
    }

    /// <summary>0 = no match.</summary>
    public static int Score(PaletteEntry e, string[] tokens, string phrase)
    {
        var total = 0;
        foreach (var t in tokens)
        {
            int s;
            var at = IndexOfWordStart(e.NTitle, t);
            if (at == 0) s = 130;
            else if (at > 0) s = 115;
            else if (e.NTitle.Contains(t, StringComparison.Ordinal)) s = 100;
            else if (e.NKeywords.Contains(t, StringComparison.Ordinal)) s = 40;
            else if (e.NDescription.Contains(t, StringComparison.Ordinal)) s = 20;
            else return 0;
            total += s;
        }
        if (e.NTitle == phrase) total += 60;
        else if (e.NTitle.StartsWith(phrase, StringComparison.Ordinal)) total += 30;
        if (e.Kind == PaletteKind.Page) total += 5;
        return total;
    }

    /// <summary>Position of the first occurrence of <paramref name="token"/> that starts a word, or -1.</summary>
    private static int IndexOfWordStart(string text, string token)
    {
        var from = 0;
        while (from <= text.Length - token.Length)
        {
            var i = text.IndexOf(token, from, StringComparison.Ordinal);
            if (i < 0) return -1;
            if (i == 0 || text[i - 1] == ' ') return i;
            from = i + 1;
        }
        return -1;
    }
}
