using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Monolith.Core;

public enum RegHive { HKLM, HKCU, HKCR, HKU }

/// <summary>
/// The exact state of one registry value before MONOLITH touched it.
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

    public string FullName => $"{Hive}\\{Path}\\{(Name.Length == 0 ? "(По умолчанию)" : Name)}";

    public string Describe() => !ValueExisted ? "отсутствовало" : $"{Kind}: {Data}";
}

/// <summary>Registry access helpers (always the 64-bit view).</summary>
public static class RegistryHelper
{
    public static RegistryKey OpenBase(RegHive hive) => hive switch
    {
        RegHive.HKLM => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        RegHive.HKCU => RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        RegHive.HKCR => RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        RegHive.HKU => RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
        _ => throw new ArgumentOutOfRangeException(nameof(hive)),
    };

    public static string HiveName(RegHive hive) => hive switch
    {
        RegHive.HKLM => "HKEY_LOCAL_MACHINE",
        RegHive.HKCU => "HKEY_CURRENT_USER",
        RegHive.HKCR => "HKEY_CLASSES_ROOT",
        _ => "HKEY_USERS",
    };

    public static bool KeyExists(RegHive hive, string path)
    {
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
        try
        {
            using var b = OpenBase(hive);
            using var k = b.OpenSubKey(path, false);
            return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public static int? ReadInt(RegHive hive, string path, string name) => Read(hive, path, name) switch
    {
        int i => i,
        long l => unchecked((int)l),
        string s when int.TryParse(s, out var v) => v,
        _ => null,
    };

    public static string? ReadString(RegHive hive, string path, string name) => Read(hive, path, name)?.ToString();

    public static RegistryValueSnapshot Snapshot(RegHive hive, string path, string name)
    {
        using var b = OpenBase(hive);
        using var k = b.OpenSubKey(path, false);
        if (k is null)
            return new RegistryValueSnapshot { Hive = hive, Path = path, Name = name, KeyExisted = false, ValueExisted = false };

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
        using var b = OpenBase(hive);
        using var k = b.CreateSubKey(path, true) ?? throw new IOException($"Не удалось открыть {hive}\\{path}");
        k.SetValue(name, Normalize(kind, value), kind);
    }

    public static void DeleteValue(RegHive hive, string path, string name)
    {
        using var b = OpenBase(hive);
        using var k = b.OpenSubKey(path, true);
        k?.DeleteValue(name, false);
    }

    /// <summary>Puts the value back exactly as captured. Removes keys MONOLITH created if they are empty again.</summary>
    public static void Restore(RegistryValueSnapshot s)
    {
        if (s.ValueExisted)
        {
            Write(s.Hive, s.Path, s.Name, s.Kind, Deserialize(s.Kind, s.Data));
            return;
        }

        DeleteValue(s.Hive, s.Path, s.Name);
        if (!s.KeyExisted) TryDeleteEmptyKeyChain(s.Hive, s.Path);
    }

    /// <summary>True when the current value equals the expected one (null expected = value must be absent).</summary>
    public static bool ValueEquals(RegHive hive, string path, string name, RegistryValueKind kind, object? expected)
    {
        var current = Read(hive, path, name);
        if (expected is null) return current is null;
        if (current is null) return false;
        return kind switch
        {
            RegistryValueKind.DWord => ToLong(current) == ToLong(expected) || unchecked((uint)ToLong(current)) == unchecked((uint)ToLong(expected)),
            RegistryValueKind.QWord => ToLong(current) == ToLong(expected),
            RegistryValueKind.Binary => current is byte[] a && expected is byte[] e && a.AsSpan().SequenceEqual(e),
            RegistryValueKind.MultiString => current is string[] ca && expected is string[] ce && ca.SequenceEqual(ce),
            _ => string.Equals(current.ToString(), expected.ToString(), StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>Exports a key into a .reg file (extra safety copy the user can double-click). Best-effort.</summary>
    public static async Task<bool> ExportKeyAsync(RegHive hive, string path, string file)
    {
        try
        {
            if (!KeyExists(hive, path)) return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("reg.exe"),
                ["export", $"{HiveName(hive)}\\{path}", file, "/y"],
                new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(20) }).ConfigureAwait(false);
            return r.Success;
        }
        catch { return false; }
    }

    private static void TryDeleteEmptyKeyChain(RegHive hive, string path)
    {
        try
        {
            using var b = OpenBase(hive);
            var current = path;
            // Only walk up a few levels and never past the first three path segments (e.g. SOFTWARE\Policies\Microsoft).
            for (var i = 0; i < 3 && current.Count(c => c == '\\') >= 3; i++)
            {
                using (var k = b.OpenSubKey(current, false))
                {
                    if (k is null) { current = Parent(current); continue; }
                    if (k.SubKeyCount > 0 || k.ValueCount > 0) return;
                }
                b.DeleteSubKey(current, false);
                current = Parent(current);
            }
        }
        catch { }

        static string Parent(string p) => p.Contains('\\') ? p[..p.LastIndexOf('\\')] : p;
    }

    private static long ToLong(object o) => o switch
    {
        int i => i,
        uint u => u,
        long l => l,
        ulong ul => unchecked((long)ul),
        string s when long.TryParse(s, out var v) => v,
        _ => Convert.ToInt64(o),
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
