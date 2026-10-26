using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Restore;

/// <summary>
/// What a journaled target looks like right now. <see cref="Drifted"/>: true when the live value differs from the
/// value MONOLITH applied (changed later by Windows Update, a policy or the user — research services.md §3.5),
/// false when it matches, null when it cannot be compared (commands, a vanished service or task, read errors).
/// </summary>
public sealed record LiveValue(string Text, bool? Drifted)
{
    public bool IsDrifted => Drifted == true;
}

/// <summary>
/// Read-only live reads for the undo center: the current value of a journal record's target, in the same
/// wording ChangeSession uses for <see cref="ChangeRecord.NewValue"/>. Blocking (registry, SCM, COM, power API):
/// call off the UI thread. Never changes anything.
/// </summary>
public static class LiveState
{
    public const string Unknown = "—";

    public static LiveValue Read(ChangeRecord r)
    {
        try
        {
            return r.Kind switch
            {
                ChangeKind.Service => ReadService(r),
                ChangeKind.Registry => ReadRegistry(r),
                ChangeKind.PowerScheme => PowerSchemes.GetActive() is var active && active != Guid.Empty
                    ? Compare(r, PowerSchemes.GetName(active))
                    : new LiveValue(Unknown, null),
                ChangeKind.ScheduledTask => ReadTask(r),
                _ => new LiveValue(Unknown, null),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось прочитать текущее значение «{r.Title}»: {ex.Message}");
            return new LiveValue(Unknown, null);
        }
    }

    private static LiveValue ReadService(ChangeRecord r)
    {
        var name = r.Service?.Name ?? StripPrefix(r.Target, "svc:");
        if (string.IsNullOrEmpty(name)) return new LiveValue(Unknown, null);
        return ServiceControl.Query(name) is { } live
            ? Compare(r, live.Describe())
            : new LiveValue("служба не найдена", null);
    }

    private static LiveValue ReadRegistry(ChangeRecord r)
    {
        // The record's snapshot holds the location (and the original value); the live read uses the same location.
        if (r.Registry is not { } loc) return new LiveValue(Unknown, null);
        return Compare(r, FormatRegistry(RegistryHelper.Snapshot(loc.Hive, loc.Path, loc.Name)));
    }

    private static LiveValue ReadTask(ChangeRecord r)
    {
        var path = r.TaskPath ?? StripPrefix(r.Target, "task:");
        if (string.IsNullOrEmpty(path)) return new LiveValue(Unknown, null);
        var info = ScheduledTasks.Query(path);
        return info.Exists
            ? Compare(r, info.Enabled ? "Включена" : "Отключена")
            : new LiveValue("задача не найдена", null);
    }

    private static LiveValue Compare(ChangeRecord r, string live) => new(live, IsDrift(r.Kind, r.NewValue, live));

    /// <summary>
    /// A registry snapshot in ChangeSession's NewValue wording: "удалено" for a missing value,
    /// otherwise "Kind: value" (DWORD as a signed number, binary as hex bytes, multi-string joined with "; ").
    /// </summary>
    public static string FormatRegistry(RegistryValueSnapshot s)
    {
        if (!s.ValueExisted) return "удалено";
        var text = s.Kind switch
        {
            RegistryValueKind.DWord when uint.TryParse(s.Data, out var u) => unchecked((int)u).ToString(),
            RegistryValueKind.Binary or RegistryValueKind.MultiString => FormatDeserialized(s),
            _ => s.Data ?? "",
        };
        return $"{s.Kind}: {text}";
    }

    private static string FormatDeserialized(RegistryValueSnapshot s)
    {
        try
        {
            return RegistryHelper.Deserialize(s.Kind, s.Data) switch
            {
                byte[] b => BitConverter.ToString(b).Replace("-", " "),
                string[] a => string.Join("; ", a),
                var o => o.ToString() ?? "",
            };
        }
        catch (FormatException)
        {
            return s.Data ?? "";
        }
    }

    /// <summary>
    /// Pure comparison of the applied value with the live one. Registry DWORDs compare numerically, so a value
    /// written as -1 and read back as 4294967295 is not reported as drift.
    /// </summary>
    public static bool? IsDrift(ChangeKind kind, string? applied, string? live)
    {
        if (string.IsNullOrWhiteSpace(applied) || string.IsNullOrWhiteSpace(live) || live == Unknown) return null;
        if (string.Equals(applied.Trim(), live.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (kind == ChangeKind.Registry && DWordOf(applied) is { } a && DWordOf(live) is { } b) return a != b;
        return true;
    }

    private static uint? DWordOf(string text)
    {
        const string prefix = "DWord: ";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var n = text[prefix.Length..].Trim();
        if (uint.TryParse(n, out var u)) return u;
        if (int.TryParse(n, out var i)) return unchecked((uint)i);
        return null;
    }

    private static string StripPrefix(string target, string prefix) =>
        target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? target[prefix.Length..] : "";
}
