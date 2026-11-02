using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Monolith.Modules.Optimize;

/// <summary>
/// A parsed profile file: only ids and metadata, nothing that could be executed. <paramref name="Scenario"/> is the Optimize
/// scenario the set was exported from («Gaming», «Streaming», «Efficiency»…), null when absent or unknown — informational only:
/// an import always lands in «Свой набор».
/// </summary>
public sealed record ProfileFileData(string? Name, string? AppVersion, DateTimeOffset? Created, IReadOnlyList<string> Ids, int InvalidIds,
    OptimizeProfile? Scenario = null);

/// <summary>What the page knows about one catalog tweak when an import is classified.</summary>
public sealed record ProfileCatalogEntry(string Id, bool IsApplicable, bool NeedsManualPick);

/// <summary>Import outcome: ids to check, and the skipped ones by reason.</summary>
public sealed record ProfileImportPlan(
    IReadOnlyList<string> Selected,
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> NotApplicable,
    IReadOnlyList<string> Manual);

/// <summary>
/// Export/import of the Optimize selection as a small JSON file (machine-independent list of tweak ids).
/// The file is untrusted input: strict shape, size and id limits; anything unexpected rejects the whole file.
/// Pure logic — no file or system access (covered by OptimizeProfileFileTests).
/// </summary>
internal static partial class OptimizeProfileFile
{
    public const string FormatTag = "monolith-optimize-profile";
    public const int FormatVersion = 1;
    public const int MaxBytes = 256 * 1024;
    public const int MaxIds = 2000;
    public const int MaxNameLength = 80;
    public const string DefaultFileName = "monolith-profile.json";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// Writes the file. <paramref name="scenario"/> (the tile the set came from; null or Custom = none) is stored by its enum NAME
    /// («Streaming», «Efficiency»), so older builds, which read only the known properties, ignore it.
    /// </summary>
    public static string Serialize(IEnumerable<string> ids, string? name, string? appVersion, DateTimeOffset created, OptimizeProfile? scenario = null)
    {
        var list = ids.Where(IsValidId).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var doc = new Dictionary<string, object?>
        {
            ["format"] = FormatTag,
            ["version"] = FormatVersion,
            ["appVersion"] = Clean(appVersion, 32),
            ["created"] = created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["name"] = Clean(name, MaxNameLength),
            ["tweaks"] = list,
        };
        if (scenario is { } sc && sc != OptimizeProfile.Custom && Enum.IsDefined(sc)) doc["scenario"] = sc.ToString();
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>«scenario» as read from a file: a known OptimizeProfile name other than Custom, else null (never a number).</summary>
    internal static OptimizeProfile? ParseScenario(string? value) =>
        value is not null && !int.TryParse(value, out _) && Enum.TryParse<OptimizeProfile>(value.Trim(), true, out var p)
        && Enum.IsDefined(p) && p != OptimizeProfile.Custom
            ? p
            : null;

    /// <summary>Parses a profile file. Returns null and a Russian <paramref name="error"/> when the file is not a valid profile.</summary>
    public static ProfileFileData? Parse(byte[]? bytes, out string? error)
    {
        error = null;
        if (bytes is null || bytes.Length == 0) { error = Loc.T("Файл пуст."); return null; }
        if (bytes.Length > MaxBytes) { error = Loc.Tf("Файл больше {0} КБ — это не профиль MONOLITH.", MaxBytes / 1024); return null; }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            error = Loc.T("Файл не в кодировке UTF-8.");
            return null;
        }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4, CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Fail(Loc.T("Ожидался объект JSON."), out error);

            if (!root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != FormatTag)
                return Fail(Loc.T("Это не файл профиля MONOLITH."), out error);
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v < 1)
                return Fail(Loc.T("Не указана версия формата профиля."), out error);
            if (v > FormatVersion)
                return Fail(Loc.T("Профиль создан более новой версией MONOLITH. Обновите программу."), out error);

            if (!root.TryGetProperty("tweaks", out var tweaks) || tweaks.ValueKind != JsonValueKind.Array)
                return Fail(Loc.T("В профиле нет списка настроек."), out error);
            if (tweaks.GetArrayLength() > MaxIds)
                return Fail(Loc.T("В профиле слишком много пунктов."), out error);

            var ids = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var invalid = 0;
            foreach (var e in tweaks.EnumerateArray())
            {
                var id = e.ValueKind == JsonValueKind.String ? e.GetString()?.Trim() : null;
                if (!IsValidId(id)) { invalid++; continue; }
                if (seen.Add(id!)) ids.Add(id!);
            }

            var name = OptionalString(root, "name", MaxNameLength);
            var appVersion = OptionalString(root, "appVersion", 32);
            DateTimeOffset? created = null;
            if (OptionalString(root, "created", 40) is { } c
                && DateTimeOffset.TryParse(c, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                created = dt;

            var scenario = ParseScenario(OptionalString(root, "scenario", 32));

            return new ProfileFileData(name, appVersion, created, ids, invalid, scenario);
        }
        catch (JsonException)
        {
            return Fail(Loc.T("Файл повреждён или это не JSON."), out error);
        }
    }

    /// <summary>
    /// Splits imported ids: unknown to this catalog, not applicable on this PC, needing a manual pick
    /// (expert or irreversible data loss — they have their own warning), and the rest, which get selected.
    /// </summary>
    public static ProfileImportPlan Classify(IEnumerable<string> ids, int invalidIds, IEnumerable<ProfileCatalogEntry> catalog)
    {
        var map = new Dictionary<string, ProfileCatalogEntry>(StringComparer.Ordinal);
        foreach (var e in catalog) map.TryAdd(e.Id, e);

        var selected = new List<string>();
        var unknown = new List<string>();
        var notApplicable = new List<string>();
        var manual = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            if (!map.TryGetValue(id, out var entry)) unknown.Add(id);
            else if (!entry.IsApplicable) notApplicable.Add(id);
            else if (entry.NeedsManualPick) manual.Add(id);
            else selected.Add(id);
        }
        for (var i = 0; i < invalidIds; i++) unknown.Add(Loc.T("(некорректная запись)"));
        return new ProfileImportPlan(selected, unknown, notApplicable, manual);
    }

    public static string Summary(ProfileImportPlan plan) =>
        Loc.N(plan.Selected.Count, "Выбрана {0} настройка", "Выбраны {0} настройки", "Выбрано {0} настроек") + ". " +
        Loc.Tf("Пропущено: неизвестные — {0}, не подходят этому ПК — {1}, требуют ручного выбора — {2}.",
            plan.Unknown.Count, plan.NotApplicable.Count, plan.Manual.Count);

    private static ProfileFileData? Fail(string message, out string? error)
    {
        error = message;
        return null;
    }

    private static string? OptionalString(JsonElement root, string property, int max) =>
        root.TryGetProperty(property, out var e) && e.ValueKind == JsonValueKind.String ? Clean(e.GetString(), max) : null;

    /// <summary>Trims, drops control characters and caps the length; empty → null.</summary>
    internal static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s.Trim())
        {
            if (char.IsControl(ch) || char.IsSurrogate(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format) continue;
            if (sb.Length >= max) break;
            sb.Append(ch);
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? null : r;
    }
}
