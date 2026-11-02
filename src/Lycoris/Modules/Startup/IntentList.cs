using System.Text;

namespace Lycoris.Modules.Startup;

/// <summary>
/// A list of ids (autostart keys, service names, task paths) as the single text parameter of an admin intent: the checked
/// rows survive the relaunch with admin rights, and the elevated copy re-checks exactly them before the confirmation
/// dialog lists what will change. Base64 of the UTF-8 text, so no character can break the command line.
/// </summary>
public static class IntentList
{
    private const char Separator = '\n';

    /// <summary>Packs ids into one text parameter; null for an empty list.</summary>
    public static string? Pack(IEnumerable<string> ids)
    {
        var list = ids.Where(i => !string.IsNullOrEmpty(i) && i.IndexOf(Separator) < 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return list.Count == 0 ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(Separator, list)));
    }

    /// <summary>The ids of a packed parameter; empty for null, empty or damaged text (never throws).</summary>
    public static IReadOnlyList<string> Unpack(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed)) return [];
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(packed)).Split(Separator, StringSplitOptions.RemoveEmptyEntries);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
