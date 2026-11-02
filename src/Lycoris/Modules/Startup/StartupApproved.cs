using System.Buffers.Binary;

namespace Lycoris.Modules.Startup;

/// <summary>
/// The 12-byte REG_BINARY state that Task Manager and Settings › Apps › Startup keep under
/// <c>…\CurrentVersion\Explorer\StartupApproved\{Run, Run32, StartupFolder}</c> (docs/research/general_tweaks.md §1):
/// bytes 0–3 are a little-endian flag DWORD, bytes 4–11 a UTC FILETIME of the moment the entry was disabled (or zero).
/// Enabled = <c>02 00 00 00 00…</c>, disabled = <c>03 00 00 00</c> + FILETIME.
/// </summary>
public static class StartupApproved
{
    public const int Length = 12;
    public const byte EnabledFlag = 0x02;
    public const byte DisabledFlag = 0x03;

    /// <summary>
    /// Reading rule from the research: a missing (or empty) value means enabled; otherwise the entry is disabled
    /// when the low bit of the first byte is set. Other values seen in the wild (04, 06…) count as enabled.
    /// </summary>
    public static bool IsEnabled(byte[]? data) => data is not { Length: > 0 } || (data[0] & 1) == 0;

    /// <summary>When the entry was disabled (UTC), if the value is a disabled record carrying a valid FILETIME.</summary>
    public static DateTime? DisabledAtUtc(byte[]? data)
    {
        if (IsEnabled(data) || data!.Length < Length) return null;
        var fileTime = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4, 8));
        if (fileTime <= 0) return null;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Builds the exact bytes Task Manager writes: 02 + zeros, or 03 + the FILETIME of <paramref name="utcNow"/>.</summary>
    public static byte[] Build(bool enabled, DateTime utcNow)
    {
        var data = new byte[Length];
        data[0] = enabled ? EnabledFlag : DisabledFlag;
        if (!enabled)
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4, 8), utcNow.ToFileTimeUtc());
        return data;
    }

    public static byte[] Build(bool enabled) => Build(enabled, DateTime.UtcNow);

    /// <summary>Human-readable hex dump ("03 00 00 00 …") for dialogs and the log.</summary>
    public static string ToHex(byte[] data) => string.Join(' ', Convert.ToHexString(data).Chunk(2).Select(c => new string(c)));
}
