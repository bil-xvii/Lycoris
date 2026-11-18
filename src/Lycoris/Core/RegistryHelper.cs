using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Lycoris.Core.Services;

namespace Lycoris.Core;

public enum RegHive { HKLM, HKCU, HKCR, HKU }

/// <summary>
/// The exact state of one registry value before Lycoris touched it.
/// Restoring the snapshot puts the value back byte-for-byte (or deletes it if it did not exist).
/// </summary>
public sealed class RegistryValueSnapshot
{
    public RegHive Hive { get; init; }
    public string Path { get; init; } = "";
    /// <summary>Value name; empty string = the key's (Default) value.</summary>
    public string Name { get; init; } = "";
    public bool KeyExisted { get; init; }
    public bool ValueExisted { get; init; }
    public RegistryValueKind Kind { get; init; }
    /// <summary>Serialized data: decimal for DWORD/QWORD, text for strings, JSON array for multi-string, base64 for binary.</summary>
    public string? Data { get; init; }

    /// <summary>
    /// The shallowest key of <see cref="Path"/> that Lycoris created (it did not exist before the first write, or an
    /// active journal record created it). On restore the empty keys from <see cref="Path"/> up to and including this
    /// one are removed, never anything above it. Null: Lycoris created no key (or the record is from an older build).
    /// </summary>
    public string? CreatedKeyRoot { get; init; }

    public string FullName => $"{Hive}\\{Path}\\{(Name.Length == 0 ? Loc.T("(По умолчанию)") : Name)}";

    public string Describe() => !ValueExisted ? Loc.Keep("отсутствовало") : $"{Kind}: {Data}";

    /// <summary>The same snapshot with another created-key root.</summary>
    public RegistryValueSnapshot WithCreatedKeyRoot(string? root) => new()
    {
        Hive = Hive, Path = Path, Name = Name, KeyExisted = KeyExisted, ValueExisted = ValueExisted, Kind = Kind, Data = Data,
        CreatedKeyRoot = root,
    };
}

/// <summary>Registry access helpers (always the 64-bit view).</summary>
public static class RegistryHelper
{
    public static RegistryKey OpenBase(RegHive hive) => RegistryKey.OpenBaseKey(WindowsHive(hive), RegistryView.Registry64);

    private static RegistryHive WindowsHive(RegHive hive) => hive switch
    {
        RegHive.HKLM => RegistryHive.LocalMachine,
        RegHive.HKCU => RegistryHive.CurrentUser,
        RegHive.HKCR => RegistryHive.ClassesRoot,
        RegHive.HKU => RegistryHive.Users,
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    public static string HiveName(RegHive hive) => hive switch
    {
        RegHive.HKLM => "HKEY_LOCAL_MACHINE",
        RegHive.HKCU => "HKEY_CURRENT_USER",
        RegHive.HKCR => "HKEY_CLASSES_ROOT",
        _ => "HKEY_USERS",
    };

    /// <summary>
    /// TESTS ONLY: maps every registry location to another one (e.g. under HKCU\Software\Lycoris-Tests)
    /// so tweaks can be applied and reverted without touching real settings. Never set in the app.
    /// </summary>
    internal static Func<RegHive, string, (RegHive Hive, string Path)>? Redirect { get; set; }

    private static void Map(ref RegHive hive, ref string path)
    {
        if (Redirect is { } r) (hive, path) = r(hive, path);
    }

    public static bool KeyExists(RegHive hive, string path)
    {
        Map(ref hive, ref path);
        try
        {
            using var b = OpenBase(hive);
            using var k = b.OpenSubKey(path, false);
            return k is not null;
        }
        catch { return false; }
    }

    public static object? Read(RegHive hive, string path, string name)
    {
        Map(ref hive, ref path);
        try
        {
            using var b = OpenBase(hive);
            using var k = b.OpenSubKey(path, false);
            if (k is not null) ServiceRemovalRegistryProtection.RequirePublicHandle(k);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    /// <summary>Names of the subkeys of a key (empty when it is missing or unreadable). Read-only.</summary>
    public static IReadOnlyList<string> SubKeyNames(RegHive hive, string path)
    {
        Map(ref hive, ref path);
        try
        {
            using var b = OpenBase(hive);
            using var k = b.OpenSubKey(path, false);
            return k?.GetSubKeyNames() ?? [];
        }
        catch { return []; }
    }

    public static int? ReadInt(RegHive hive, string path, string name) => Read(hive, path, name) switch
    {
        int i => i,
        long l => unchecked((int)l),
        string s when int.TryParse(s, out var v) => v,
        _ => null,
    };

    /// <summary>Strict 64-bit DWORD read. Only a confirmed missing value or key returns null.</summary>
    internal static uint? ReadDwordOrMissing(RegHive hive, string path, string name)
    {
        const uint regDword = 0x00000010; // RRF_RT_REG_DWORD, excludes REG_BINARY
        const uint wow64Native = 0x00010000; // RRF_SUBKEY_WOW6464KEY
        const int fileNotFound = 2;
        const int invalidData = 13;

        Map(ref hive, ref path);
        // RegistryHive enum values are the signed predefined Win32 HKEY constants.
        var handle = (nint)(int)WindowsHive(hive);
        uint size = sizeof(uint);
        var error = RegGetValueW(handle, path, name, regDword | wow64Native, 0, out var value, ref size);
        return error switch
        {
            0 when size == sizeof(uint) => value,
            fileNotFound => null,
            _ => throw new Win32Exception(error == 0 ? invalidData : error),
        };
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegGetValueW(nint key, string subKey, string valueName, uint flags, nint valueType,
        out uint data, ref uint dataSize);

    public static string? ReadString(RegHive hive, string path, string name) => Read(hive, path, name)?.ToString();

    public static RegistryValueSnapshot Snapshot(RegHive hive, string path, string name)
    {
        // The snapshot keeps the logical (unmapped) location; Restore maps it again.
        ServiceRemovalRegistryProtection.RequirePublicName(hive, path);
        RegHive h = hive; string p = path;
        Map(ref h, ref p);
        using var b = OpenBase(h);
        using var k = b.OpenSubKey(p, false);
        if (k is null)
            return new RegistryValueSnapshot
            {
                Hive = hive, Path = path, Name = name, KeyExisted = false, ValueExisted = false,
                CreatedKeyRoot = ShallowestMissing(path, logical => KeyExists(hive, logical)),
            };

        ServiceRemovalRegistryProtection.RequirePublicHandle(k);
        var value = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null)
            return new RegistryValueSnapshot { Hive = hive, Path = path, Name = name, KeyExisted = true, ValueExisted = false };

        var kind = k.GetValueKind(name);
        return new RegistryValueSnapshot
        {
            Hive = hive, Path = path, Name = name, KeyExisted = true, ValueExisted = true,
            Kind = kind, Data = Serialize(kind, value),
        };
    }

    public static void Write(RegHive hive, string path, string name, RegistryValueKind kind, object value)
    {
        if (Redirect is null) SafetyLock.ThrowIfLocked(Loc.T("запись в реестр"));
        Map(ref hive, ref path);
        using var b = OpenBase(hive);
        using var k = ServiceRemovalRegistryProtection.OpenForMutation(b, hive, path, create: true) ?? throw new IOException(Loc.Tf("Не удалось открыть {0}\\{1}", hive, path));
        k.SetValue(name, Normalize(kind, value), kind);
    }

    public static void DeleteValue(RegHive hive, string path, string name)
    {
        if (Redirect is null) SafetyLock.ThrowIfLocked(Loc.T("удаление из реестра"));
        Map(ref hive, ref path);
        using var b = OpenBase(hive);
        using var k = ServiceRemovalRegistryProtection.OpenForMutation(b, hive, path, create: false);
        k?.DeleteValue(name, false);
    }

    /// <summary>
    /// Puts the value back exactly as captured. When the value did not exist, removes it and then the keys Lycoris
    /// created, while they are empty: from <see cref="RegistryValueSnapshot.Path"/> up to and including
    /// <see cref="RegistryValueSnapshot.CreatedKeyRoot"/>, never above it. Records from older builds (no root) only
    /// lose their own key, and only when it is empty.
    /// </summary>
    public static void Restore(RegistryValueSnapshot s)
    {
        if (s.ValueExisted)
        {
            Write(s.Hive, s.Path, s.Name, s.Kind, Deserialize(s.Kind, s.Data));
            return;
        }

        DeleteValue(s.Hive, s.Path, s.Name);
        if (s.CreatedKeyRoot is { } root) DeleteEmptyKeys(s.Hive, s.Path, root);
        else if (!s.KeyExisted) DeleteEmptyKeys(s.Hive, s.Path, s.Path);
    }

    /// <summary>True when the current value equals the expected one (null expected = value must be absent).</summary>
    public static bool ValueEquals(RegHive hive, string path, string name, RegistryValueKind kind, object? expected)
    {
        var current = Read(hive, path, name);
        if (expected is null) return current is null;
        if (current is null) return false;
        // A value of another kind (e.g. REG_SZ text where a DWORD is expected) simply does not match.
        return kind switch
        {
            RegistryValueKind.DWord => ToLong(current) is { } c && ToLong(expected) is { } e && (c == e || unchecked((uint)c) == unchecked((uint)e)),
            RegistryValueKind.QWord => ToLong(current) is { } cq && ToLong(expected) is { } eq && cq == eq,
            RegistryValueKind.Binary => current is byte[] a && expected is byte[] e && a.AsSpan().SequenceEqual(e),
            RegistryValueKind.MultiString => current is string[] ca && expected is string[] ce && ca.SequenceEqual(ce),
            _ => string.Equals(current.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Recursive registry export is disabled because it can expose protected recovery data. Undo uses journal snapshots.</summary>
    public static Task<bool> ExportKeyAsync(RegHive hive, string path, string file) => Task.FromResult(false);

    /// <summary>Deletes the empty keys from <paramref name="path"/> up to and including <paramref name="root"/> (logical paths).</summary>
    private static void DeleteEmptyKeys(RegHive hive, string path, string root)
    {
        if (Redirect is null) SafetyLock.ThrowIfLocked(Loc.T("удаление раздела реестра"));
        try
        {
            DeleteEmptyChain(path, root,
                logical =>
                {
                    RegHive h = hive; string p = logical;
                    Map(ref h, ref p);
                    using var b = OpenBase(h);
                    using var k = b.OpenSubKey(p, false);
                    return k is null ? null : k.SubKeyCount == 0 && k.ValueCount == 0;
                },
                logical =>
                {
                    RegHive h = hive; string p = logical;
                    Map(ref h, ref p);
                    using var b = OpenBase(h);
                    using var k = ServiceRemovalRegistryProtection.OpenForMutation(b, h, p, create: false, delete: true);
                    if (k is not null) ServiceRemovalRegistryProtection.DeleteEmpty(k);
                });
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось удалить пустой раздел реестра {0}\\{1}: {2}", hive, path, ex.Message);
        }
    }

    // ───────────────────────────── Created keys (pure path logic) ─────────────────────────────

    private static string TrimPath(string path) => path.Replace('/', '\\').Trim('\\');

    private static string[] Segments(string path) => TrimPath(path).Split('\\');

    /// <summary>True when <paramref name="ancestor"/> is <paramref name="path"/> itself or one of its parent keys (whole segments).</summary>
    internal static bool IsSameOrAncestor(string ancestor, string path)
    {
        var a = TrimPath(ancestor);
        var p = TrimPath(path);
        if (a.Length == 0) return false;
        return string.Equals(a, p, StringComparison.OrdinalIgnoreCase)
               || (p.Length > a.Length && p.StartsWith(a, StringComparison.OrdinalIgnoreCase) && p[a.Length] == '\\');
    }

    /// <summary>The shallower of two created-key roots of the same path (null-tolerant).</summary>
    internal static string? ShallowerRoot(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return Segments(a).Length <= Segments(b).Length ? a : b;
    }

    /// <summary>
    /// A created-key root a journal record may carry: <paramref name="path"/> itself or a parent of it, at least two
    /// segments deep (never a top-level key such as SOFTWARE), with no empty segments.
    /// </summary>
    internal static bool IsValidCreatedRoot(string path, string root)
    {
        var segments = Segments(root);
        return segments.Length >= 2 && segments.All(x => x.Length > 0) && IsSameOrAncestor(root, path);
    }

    /// <summary>
    /// The shallowest missing key of <paramref name="path"/> (the first segment that does not exist), or null when the
    /// key exists. Never shallower than two segments: a missing top-level key is reported as its first child.
    /// </summary>
    internal static string? ShallowestMissing(string path, Func<string, bool> exists)
    {
        var segments = Segments(path);
        if (segments.Length == 0 || segments.Any(x => x.Length == 0)) return null;
        for (var i = 1; i <= segments.Length; i++)
        {
            var prefix = string.Join('\\', segments.Take(i));
            if (exists(prefix)) continue;
            if (i == 1) return segments.Length >= 2 ? string.Join('\\', segments.Take(2)) : null;
            return prefix;
        }
        return null;
    }

    /// <summary>
    /// Walks from <paramref name="path"/> up to and including <paramref name="root"/>: a missing key is skipped, an
    /// empty one is deleted, the first key that still holds values or subkeys stops the walk. Never goes above
    /// <paramref name="root"/>, and does nothing when the root is not <paramref name="path"/> or one of its parents.
    /// <paramref name="isEmpty"/> returns null for a missing key.
    /// </summary>
    internal static void DeleteEmptyChain(string path, string root, Func<string, bool?> isEmpty, Action<string> delete)
    {
        if (!IsSameOrAncestor(root, path)) return;
        var stop = Segments(root).Length;
        var segments = Segments(path);
        for (var depth = segments.Length; depth >= stop; depth--)
        {
            var current = string.Join('\\', segments.Take(depth));
            switch (isEmpty(current))
            {
                case null: continue;   // already gone
                case false: return;    // something else lives here: keep it and everything above
                default: delete(current); break;
            }
        }
    }

    private static long? ToLong(object o) => o switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => unchecked((long)ul),
        short s => s,
        ushort us => us,
        byte b => b,
        string s when long.TryParse(s, out var v) => v,
        _ => null,
    };

    private static object Normalize(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.DWord => value switch
        {
            uint u => unchecked((int)u),
            long l => unchecked((int)l),
            _ => Convert.ToInt32(value),
        },
        RegistryValueKind.QWord => value switch
        {
            ulong ul => unchecked((long)ul),
            _ => Convert.ToInt64(value),
        },
        RegistryValueKind.MultiString => value as string[] ?? [value.ToString() ?? ""],
        RegistryValueKind.Binary => value as byte[] ?? Convert.FromBase64String(value.ToString() ?? ""),
        _ => value.ToString() ?? "",
    };

    public static string Serialize(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        RegistryValueKind.MultiString => JsonSerializer.Serialize((string[])value),
        RegistryValueKind.DWord => unchecked((uint)Convert.ToInt32(value)).ToString(),
        RegistryValueKind.QWord => Convert.ToInt64(value).ToString(),
        _ => value.ToString() ?? "",
    };

    public static object Deserialize(RegistryValueKind kind, string? data) => kind switch
    {
        RegistryValueKind.Binary => Convert.FromBase64String(data ?? ""),
        RegistryValueKind.MultiString => JsonSerializer.Deserialize<string[]>(data ?? "[]") ?? [],
        RegistryValueKind.DWord => unchecked((int)uint.Parse(data ?? "0")),
        RegistryValueKind.QWord => long.Parse(data ?? "0"),
        _ => data ?? "",
    };
}
