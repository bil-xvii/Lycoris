using System.Text;
using System.Text.RegularExpressions;
using Lycoris.Core.Diagnostics;
using Lycoris.Shell;

namespace Lycoris.Core.Assistant;

/// <summary>An answer ready for the screen: the text and the places in Lycoris it points to (each becomes an «ОТКРЫТЬ» button).</summary>
public sealed record AssistantReply(string Text, IReadOnlyList<PaletteEntry> Actions);

/// <summary>
/// The «Помощник» logic without UI or network (all pure, unit-tested).
/// Safety by design: the model never changes anything. It reads facts Lycoris already collected (anonymized) and may only
/// point to numbered places from the command-palette index — the same pages, settings and tools Ctrl+K opens. Every change
/// is then made by the user with the page's own button, confirmation and undo. A number that is not in the list is ignored.
/// </summary>
public static partial class AssistantBrain
{
    /// <summary>At most this many places are offered to the model, and at most <see cref="MaxButtons"/> come back as buttons.</summary>
    public const int MaxPlaces = 14;
    public const int MaxButtons = 4;

    // Families that answer well in Russian and English; embedding models cannot chat.
    private static readonly string[] PreferredFamilies = ["qwen", "llama", "gemma", "mistral", "phi", "deepseek"];
    private const long SweetSpotBytes = 5L * 1024 * 1024 * 1024; // ~7–8B at 4 bits: good answers, still quick on a gaming GPU

    /// <summary>The model to use: the saved one if it is still installed, else the best chat model by family and size.</summary>
    public static string? PickModel(IReadOnlyList<LocalModel> models, string? saved)
    {
        if (!string.IsNullOrWhiteSpace(saved) && models.Any(m => m.Name == saved)) return saved;
        var chat = models.Where(IsChatModel).ToList();
        if (chat.Count == 0) return null;
        return chat
            .OrderBy(m => FamilyRank(m.Name))
            .ThenBy(m => m.SizeBytes > 0 ? Math.Abs(m.SizeBytes - SweetSpotBytes) : long.MaxValue)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .First().Name;
    }

    public static bool IsChatModel(LocalModel m) =>
        !m.Name.Contains("embed", StringComparison.OrdinalIgnoreCase) && !m.Name.Contains("bge-", StringComparison.OrdinalIgnoreCase);

    private static int FamilyRank(string name)
    {
        for (var i = 0; i < PreferredFamilies.Length; i++)
            if (name.Contains(PreferredFamilies[i], StringComparison.OrdinalIgnoreCase)) return i;
        return PreferredFamilies.Length;
    }

    /// <summary>
    /// Places in Lycoris that fit the question: the palette's own search for the whole question, then for each longer word,
    /// then the pages (so the model can always say where to go). No sections twice, at most <see cref="MaxPlaces"/>.
    /// </summary>
    public static IReadOnlyList<PaletteEntry> SelectPlaces(IReadOnlyList<PaletteEntry> index, string question, IEnumerable<string>? extraQueries = null)
    {
        var picked = new List<PaletteEntry>();
        void Add(IEnumerable<PaletteEntry> entries, int limit)
        {
            foreach (var e in entries)
            {
                if (picked.Count >= MaxPlaces || limit-- <= 0) return;
                if (!picked.Any(p => p.PageKey == e.PageKey && p.FocusId == e.FocusId && p.Title == e.Title)) picked.Add(e);
            }
        }

        Add(CommandPaletteSearch.Search(index, question, 4), 4);
        foreach (var query in extraQueries ?? []) Add(CommandPaletteSearch.Search(index, query, 2), 2);
        // Word by word: a natural question rarely matches the palette as a whole («почему игры лагают» → «игры»).
        var words = CommandPaletteSearch.Tokenize(question).Where(w => w.Length >= 4).Distinct().Take(6);
        foreach (var word in words) Add(CommandPaletteSearch.Search(index, Stem(word), 2), 2);
        Add(index.Where(e => e.Kind == PaletteKind.Page && !e.IsSection), MaxPlaces);
        return picked;
    }

    // «тормозит» → «тормоз», «играх» → «игр»: Russian endings would otherwise hide the match.
    private static string Stem(string word) => word.Length > 5 ? word[..^2] : word.Length > 4 ? word[..^1] : word;

    /// <summary>The rules the model gets first, in the interface language. Short and strict: small local models follow short rules better.</summary>
    public static string SystemPrompt(bool english) => string.Join('\n', english ? PromptEn : PromptRu);

    // The model's instructions, not interface text: both languages live here (the English one is not a translation table entry).
    private static readonly string[] PromptRu =
    [
        "Ты — помощник внутри Lycoris, безопасного оптимизатора Windows. Отвечай по-русски, коротко (до 8 предложений), простыми словами, для неспециалиста.", // loc:ignore
        "Опирайся ТОЛЬКО на сведения о ПК ниже; если сведений нет — так и скажи, не выдумывай.", // loc:ignore
        "Сам ты ничего не меняешь. Чтобы показать место в Lycoris, пиши его номер в квадратных скобках, например [2].", // loc:ignore
        "Используй только номера из списка МЕСТА. Изменения пользователь делает кнопками Lycoris — с подтверждением и возможностью отката.", // loc:ignore
        "Никогда не советуй: отключать Защитник Windows, брандмауэр, UAC, SmartScreen или Центр обновления; чистить реестр; удалять системные файлы;", // loc:ignore
        "отключать файл подкачки; скачивать утилиты с неизвестных сайтов. Если об этом спросят — объясни, чем это опасно.", // loc:ignore
    ];

    private static readonly string[] PromptEn =
    [
        "You are the assistant inside Lycoris, a safe Windows PC optimizer. Answer in English, briefly (up to 8 sentences), plainly, for a non-expert.",
        "Use ONLY the PC facts below; if a fact is missing, say so instead of guessing.",
        "You cannot change anything yourself. To point the user to a place in Lycoris, write its number in square brackets, e.g. [2].",
        "Use only numbers from the PLACES list. The user then changes things with Lycoris buttons, which ask for confirmation and can be undone.",
        "Never advise: turning off Windows Defender, the firewall, UAC, SmartScreen or Windows Update; registry cleaners; deleting system files;",
        "turning off the page file; downloading tools from unknown sites. If asked, explain why it is risky.",
    ];

    /// <summary>
    /// The facts and places for one question. System data is anonymized (user and computer names, profile paths), and every
    /// line is cut, so a long or hostile text (an autostart entry's name) cannot flood or steer the prompt.
    /// </summary>
    public static string Context(HealthReportData? pc, IReadOnlyList<PaletteEntry> places, IEnumerable<string>? recentChanges)
    {
        // System data goes through the report anonymizer; Lycoris's own texts (journal titles, place names) are only cut and
        // flattened — the anonymizer treats «ключ» in «Включён» as a secret word and would garble them.
        static string Flat(string? s, int max)
        {
            var text = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Replace("[", "(").Replace("]", ")").Trim();
            return text.Length > max ? text[..max] + "…" : text;
        }
        static string Clean(string? s, int max = 200) => Flat(ProblemReport.Anonymize(Flat(s, 2000)), max);
        var sb = new StringBuilder();
        sb.AppendLine(Loc.T("СВЕДЕНИЯ О ПК (собраны Lycoris, только чтение):"));
        if (pc is null)
            sb.AppendLine(Loc.T("- Проверки ещё не запускались."));
        else
        {
            sb.Append("- ").AppendLine(Loc.Tf("Оценка ПК: {0} / 100 — {1}", pc.Score, Clean(pc.Verdict)));
            foreach (var row in pc.System.Take(14)) sb.Append("- ").Append(Clean(row.Label, 40)).Append(": ").AppendLine(Clean(row.Value + (string.IsNullOrWhiteSpace(row.Detail) ? "" : " (" + row.Detail + ")")));
            var problems = pc.Checks.Where(c => c.Status != "ok").Take(10).ToList();
            sb.AppendLine(Loc.T("Проблемы, найденные проверками:"));
            if (problems.Count == 0) sb.AppendLine(Loc.T("- нет"));
            foreach (var c in problems) sb.Append("- ").Append(Clean(c.Title, 80)).Append(": ").AppendLine(Clean(c.Detail));
            if (!string.IsNullOrWhiteSpace(pc.Measurement)) sb.Append("- ").AppendLine(Clean(pc.Measurement, 300));
        }
        var changes = (recentChanges ?? []).Take(6).ToList();
        if (changes.Count > 0)
        {
            sb.AppendLine(Loc.T("Последние изменения Lycoris (всё можно откатить в «Восстановлении»):"));
            foreach (var c in changes) sb.Append("- ").AppendLine(Flat(c, 120));
        }
        sb.AppendLine(Loc.T("МЕСТА (ссылайся только на эти номера):"));
        for (var i = 0; i < places.Count; i++)
            sb.Append('[').Append(i + 1).Append("] ").Append(Flat(places[i].Title, 80)).Append(" — ").AppendLine(Flat(places[i].Description, 140));
        return sb.ToString();
    }

    /// <summary>The model's text for the screen: reasoning blocks removed, «[n]» replaced by the place's name, the places as buttons.</summary>
    public static AssistantReply ParseReply(string raw, IReadOnlyList<PaletteEntry> places)
    {
        var text = ThinkBlock().Replace(raw ?? "", "");
        // A reasoning block that never closed (the answer was stopped) hides everything after it.
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0) text = text[..open];
        var actions = new List<PaletteEntry>();
        text = PlaceList().Replace(text, m => string.Concat(m.Groups[1].Value.Split(',').Select(n => "[" + n.Trim() + "] ")).TrimEnd());
        text = PlaceRef().Replace(text, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var n) || n < 1 || n > places.Count) return "";
            var place = places[n - 1];
            if (!actions.Contains(place) && actions.Count < MaxButtons) actions.Add(place);
            return "«" + place.Title + "»";
        });
        text = ExtraSpaces().Replace(text, " ").Replace(" .", ".").Replace(" ,", ",").Trim();
        return new AssistantReply(text, actions);
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    // «[3, 5]» → «[3] [5]» first, then each [n] is one reference.
    [GeneratedRegex(@"\[\s*(\d{1,2}(?:\s*,\s*\d{1,2})+)\s*\]")]
    private static partial Regex PlaceList();

    [GeneratedRegex(@"\[\s*(\d{1,2})\s*\]")]
    private static partial Regex PlaceRef();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex ExtraSpaces();
}
