using System.Globalization;
using System.Text.Json;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Restore;

/// <summary>How the value MONOLITH set compares with what is in the system right now.</summary>
public enum LiveState
{
    /// <summary>Still exactly what MONOLITH set.</summary>
    Same,
    /// <summary>Changed after MONOLITH — by the user, Windows Update, a policy or another program.</summary>
    Drifted,
    /// <summary>The service / task no longer exists in Windows.</summary>
    Gone,
    /// <summary>Could not be read (commands, access denied, unparseable journal text).</summary>
    Unknown,
}

/// <summary>The current value of a journaled setting, read live just before a revert.</summary>
public sealed record LiveValue(LiveState State, string Text)
{
    public bool IsDrifted => State == LiveState.Drifted;
}

/// <summary>A journal record together with its live value.</summary>
public sealed record LiveRecord(ChangeRecord Record, LiveValue Live);

/// <summary>
/// Reads the current value of whatever a journal record changed (registry, service, task, power plan), read-only,
/// and compares it with the value MONOLITH set (<see cref="ChangeRecord.NewValue"/>, in the format ChangeSession writes).
/// services.md §3.5: before a revert the user must see the real current value and be asked when it drifted.
/// </summary>
public static class RestoreLive
{
    public const string Absent = "отсутствует";

    /// <summary>Live read for one record. Never throws; call off the UI thread (registry, SCM, Task Scheduler COM).</summary>
    public static LiveValue Read(ChangeRecord r)
    {
        try
        {
            switch (r.Kind)
            {
                case ChangeKind.Registry when r.Registry is { } s:
                {
                    var now = RegistryHelper.Snapshot(s.Hive, s.Path, s.Name);
                    var value = now.ValueExisted ? RegistryHelper.Deserialize(now.Kind, now.Data) : null;
                    return CompareRegistry(r.NewValue, now.ValueExisted ? now.Kind : null, value);
                }
                case ChangeKind.Service when r.Service is { } svc:
                {
                    var now = ServiceControl.Query(svc.Name);
                    return now is null
                        ? new LiveValue(LiveState.Gone, $"службы «{svc.Name}» больше нет в Windows")
                        : CompareText(r.NewValue, now.Describe());
                }
                case ChangeKind.ScheduledTask when r.TaskPath is { } path:
                {
                    var now = ScheduledTasks.Query(path);
                    return !now.Exists
                        ? new LiveValue(LiveState.Gone, "задачи больше нет в планировщике Windows")
                        : CompareText(r.NewValue, now.Enabled ? "Включена" : "Отключена");
                }
                case ChangeKind.PowerScheme:
                {
                    var active = PowerSchemes.GetActive();
                    return active == Guid.Empty
                        ? new LiveValue(LiveState.Unknown, "активную схему питания прочитать не удалось")
                        : CompareText(r.NewValue, PowerSchemes.GetName(active));
                }
                case ChangeKind.Command:
                    return new LiveValue(LiveState.Unknown, "результат команды MONOLITH проверить не может");
                default:
                    return new LiveValue(LiveState.Unknown, "в записи нет данных для проверки");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Восстановление: не удалось прочитать текущее значение «{r.Title}»: {ex.Message}");
            return new LiveValue(LiveState.Unknown, "прочитать не удалось");
        }
    }

    /// <summary>Services, tasks, power plans: the journal stores the same text the live read produces.</summary>
    public static LiveValue CompareText(string journalNewValue, string current) =>
        new(string.Equals(journalNewValue.Trim(), current.Trim(), StringComparison.OrdinalIgnoreCase) ? LiveState.Same : LiveState.Drifted, current);

    /// <summary>
    /// Compares a registry value with the journal's NewValue ("удалено" or "DWord: 1", "String: …", "Binary: 0A FF",
    /// "MultiString: a; b" — the format ChangeSession.SetRegistry writes). Numbers are compared as numbers, so
    /// "DWord: -1" and 0xFFFFFFFF are the same value. Unparseable journal text gives <see cref="LiveState.Unknown"/>.
    /// </summary>
    public static LiveValue CompareRegistry(string journalNewValue, RegistryValueKind? currentKind, object? currentValue)
    {
        var text = currentValue is null || currentKind is null ? Absent : $"{currentKind}: {Format(currentKind.Value, currentValue)}";
        var expected = journalNewValue.Trim();

        if (expected.Equals("удалено", StringComparison.OrdinalIgnoreCase))
            return new(currentValue is null ? LiveState.Same : LiveState.Drifted, text);

        var colon = expected.IndexOf(':');
        if (colon <= 0 || !Enum.TryParse<RegistryValueKind>(expected[..colon].Trim(), ignoreCase: true, out var kind))
            return new(LiveState.Unknown, text);
        var data = expected[(colon + 1)..].Trim();

        if (currentValue is null || currentKind is null) return new(LiveState.Drifted, text);
        if (currentKind != kind) return new(LiveState.Drifted, text);

        bool? same = kind switch
        {
            RegistryValueKind.DWord => TryLong(data, out var e) && TryLong(currentValue, out var c) ? unchecked((uint)e == (uint)c) : null,
            RegistryValueKind.QWord => TryLong(data, out var e) && TryLong(currentValue, out var c) ? e == c : null,
            RegistryValueKind.Binary => string.Equals(Normalize(data), Normalize(Format(kind, currentValue)), StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(data, Format(kind, currentValue).Trim(), StringComparison.Ordinal),
        };
        return new(same switch { true => LiveState.Same, false => LiveState.Drifted, _ => LiveState.Unknown }, text);

        static string Normalize(string hex) => string.Concat(hex.Where(Uri.IsHexDigit));
    }

    /// <summary>Same text ChangeSession uses for a new value: hex bytes for binary, "; " between strings.</summary>
    public static string Format(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.Binary when value is byte[] b => BitConverter.ToString(b).Replace("-", " "),
        RegistryValueKind.MultiString when value is string[] a => string.Join("; ", a),
        RegistryValueKind.MultiString when value is string s && s.StartsWith('[') => TryJoinJson(s),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static string TryJoinJson(string json)
    {
        try { return string.Join("; ", JsonSerializer.Deserialize<string[]>(json) ?? []); }
        catch { return json; }
    }

    private static bool TryLong(object? o, out long value)
    {
        switch (o)
        {
            case int i: value = i; return true;
            case uint u: value = u; return true;
            case long l: value = l; return true;
            case ulong ul: value = unchecked((long)ul); return true;
            case string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p): value = p; return true;
            case string s when ulong.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var up): value = unchecked((long)up); return true;
            case string s when s.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                               && ulong.TryParse(s.Trim()[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx):
                value = unchecked((long)hx); return true;
            default: value = 0; return false;
        }
    }
}
