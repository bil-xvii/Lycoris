using System.Runtime.InteropServices;

namespace Lycoris.Core;

public sealed record PowerScheme(Guid Id, string Name, bool IsActive);

/// <summary>
/// The parts of SYSTEM_POWER_CAPABILITIES Lycoris reads (GetPwrCapabilities): hiberfil present, Modern Standby (AoAc),
/// the hiberfil type (1 = reduced, Fast Startup only), hybrid-sleep support (FastSystemS4) and the battery facts
/// (SystemBatteriesPresent / BatteriesAreShortTerm — short-term batteries are UPS batteries, not a laptop's).
/// </summary>
public readonly record struct PowerCaps(bool HiberFilePresent, bool ModernStandby, byte HiberFileType, bool FastSystemS4,
    bool SystemBatteriesPresent, bool BatteriesAreShortTerm);

/// <summary>Power plans through powrprof.dll (no parsing of localized powercfg output).</summary>
public static class PowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    /// <summary>Hidden template; must be duplicated before it can be activated.</summary>
    public static readonly Guid UltimateTemplate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public static Guid GetActive()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static void SetActive(Guid scheme)
    {
        SafetyLock.ThrowIfLocked(Loc.T("смена плана питания"));
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0) throw new InvalidOperationException(Loc.Tf("Не удалось включить план питания {0} (код {1}).", scheme, rc));
    }

    public static IReadOnlyList<PowerScheme> List()
    {
        var active = GetActive();
        var result = new List<PowerScheme>();
        uint index = 0;
        while (true)
        {
            uint size = 16;
            var buffer = Marshal.AllocHGlobal(16);
            try
            {
                var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size);
                if (rc != 0) break;
                var id = Marshal.PtrToStructure<Guid>(buffer);
                result.Add(new PowerScheme(id, GetName(id), id == active));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            index++;
        }
        return result;
    }

    public static bool Exists(Guid scheme) => List().Any(s => s.Id == scheme);

    /// <summary>
    /// Whether the scheme exists; null when the plans could not be listed at all, so a failed enumeration is never
    /// mistaken for "the scheme is gone".
    /// </summary>
    public static bool? TryExists(Guid scheme)
    {
        var list = List();
        return list.Count == 0 ? null : list.Any(s => s.Id == scheme);
    }

    /// <summary>Plans Windows ships (never deleted by Lycoris) and the hidden Ultimate template.</summary>
    public static bool IsBuiltIn(Guid scheme) =>
        scheme == Balanced || scheme == HighPerformance || scheme == PowerSaver || scheme == UltimateTemplate;

    public static string GetName(Guid scheme)
    {
        uint size = 0;
        PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size);
        if (size == 0) return scheme.ToString();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return scheme.ToString();
            return Marshal.PtrToStringUni(buffer) ?? scheme.ToString();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Copies a scheme (e.g. the hidden Ultimate Performance template). Returns the new scheme id.</summary>
    public static Guid Duplicate(Guid source)
    {
        SafetyLock.ThrowIfLocked(Loc.T("создание плана питания"));
        var dest = IntPtr.Zero;
        var rc = PowerDuplicateScheme(IntPtr.Zero, ref source, ref dest);
        if (rc != 0 || dest == IntPtr.Zero)
            throw new InvalidOperationException(Loc.Tf("Не удалось создать план питания (код {0}).", rc));
        try { return Marshal.PtrToStructure<Guid>(dest); }
        finally { LocalFree(dest); }
    }

    public static void Delete(Guid scheme)
    {
        if (IsBuiltIn(scheme) || scheme == Guid.Empty) return; // never delete built-ins (or the template)
        SafetyLock.ThrowIfLocked(Loc.T("удаление плана питания"));
        if (GetActive() == scheme) return;
        PowerDeleteScheme(IntPtr.Zero, ref scheme);
    }

    /// <summary>
    /// SYSTEM_POWER_CAPABILITIES by byte offset: HiberFilePresent 8, FastSystemS4 17, AoAc 20, HiberFileType 22,
    /// SystemBatteriesPresent 30, BatteriesAreShortTerm 31 (the structure is 76 bytes; the buffer is 128). Read-only;
    /// null when the call fails or is unavailable (not Windows).
    /// </summary>
    public static PowerCaps? ReadCapabilities()
    {
        var buffer = Marshal.AllocHGlobal(128);
        try
        {
            for (var i = 0; i < 128; i++) Marshal.WriteByte(buffer, i, 0);
            if (!GetPwrCapabilities(buffer)) return null;
            return new PowerCaps(
                HiberFilePresent: Marshal.ReadByte(buffer, 8) != 0,
                ModernStandby: Marshal.ReadByte(buffer, 20) != 0,
                HiberFileType: Marshal.ReadByte(buffer, 22),
                FastSystemS4: Marshal.ReadByte(buffer, 17) != 0,
                SystemBatteriesPresent: Marshal.ReadByte(buffer, 30) != 0,
                BatteriesAreShortTerm: Marshal.ReadByte(buffer, 31) != 0);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// GUID_POWERSCHEME_PERSONALITY («Power plan type», powercfg alias PERSONALITY): lives in the scheme-level pseudo-subgroup
    /// NO_SUBGROUP_GUID (SUB_NONE). Per scheme the store keeps an INDEX into the setting's possible values (powercfg /qh:
    /// «Possible Setting Index 000 Power saver / 001 High performance / 002 Balanced», «Current AC Power Setting Index:
    /// 0x00000002» on Balanced), not a GUID; the possible values themselves are the three personality GUIDs.
    /// </summary>
    private static readonly Guid PersonalityGuid = new("245d8541-3943-4422-b025-13a784f679b7");

    /// <summary>NO_SUBGROUP_GUID (WinNT.h): «settings in this subgroup are part of the default power scheme».</summary>
    private static readonly Guid NoSubgroup = new("fea3413e-7e05-4911-9a71-700331f1c294");

    /// <summary>The documented index of the Balanced personality among the possible values (0 saver, 1 high performance, 2 balanced).</summary>
    internal const uint BalancedPersonalityIndex = 2;

    /// <summary>
    /// True when the scheme is Balanced or derived from it (personality GUID_TYPICAL_POWER_SAVINGS = the Balanced GUID):
    /// Microsoft's power-mode slider exists on Balanced «or any plan derived from Balanced». Read-only; false on any error
    /// (the Balanced scheme itself always counts). Reads the scheme's personality INDEX (PowerReadACValueIndex, NO_SUBGROUP_GUID)
    /// and resolves it through the setting's possible values (PowerReadPossibleValue → the 16-byte personality GUID); when the
    /// possible value cannot be read the documented index mapping (2 = Balanced) is used.
    /// </summary>
    public static bool IsBalancedPersonality(Guid scheme)
    {
        if (scheme == Balanced) return true;
        if (scheme == Guid.Empty) return false;
        try
        {
            var subgroup = NoSubgroup;
            var personality = PersonalityGuid;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref subgroup, ref personality, out var index) != 0) return false;
            return PersonalityIndexIsBalanced(index, ReadPossiblePersonality(index));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The possible value (a personality GUID, REG_BINARY 16 bytes) behind an index; null when it cannot be read.</summary>
    private static byte[]? ReadPossiblePersonality(uint index)
    {
        var subgroup = NoSubgroup;
        var personality = PersonalityGuid;
        var buffer = new byte[16];
        uint size = 16;
        var rc = PowerReadPossibleValue(IntPtr.Zero, ref subgroup, ref personality, out var type, index, buffer, ref size);
        return rc == 0 && type == 3 /* REG_BINARY */ && size == 16 ? buffer : null;
    }

    /// <summary>
    /// Pure: a scheme's personality index resolved against the possible value Windows reports for it. The GUID decides when it
    /// is known (16 bytes); otherwise the documented index mapping (0 saver, 1 high performance, 2 balanced) is the fallback.
    /// </summary>
    public static bool PersonalityIndexIsBalanced(uint index, byte[]? possibleValue) =>
        possibleValue is { Length: 16 } ? PersonalityIsBalanced(possibleValue) : index == BalancedPersonalityIndex;

    /// <summary>Pure: 16 personality-GUID bytes (a possible value of PERSONALITY) compared with the Balanced GUID.</summary>
    public static bool PersonalityIsBalanced(byte[]? value) =>
        value is { Length: 16 } && new Guid(value) == Balanced;

    /// <summary>Finds an existing copy of Ultimate Performance (any scheme whose name matches the template's).</summary>
    public static Guid? FindUltimateCopy()
    {
        var templateName = GetName(UltimateTemplate);
        foreach (var s in List())
        {
            if (s.Id == UltimateTemplate) return s.Id;
            if (!string.IsNullOrEmpty(templateName) && templateName != UltimateTemplate.ToString() &&
                string.Equals(s.Name, templateName, StringComparison.OrdinalIgnoreCase))
                return s.Id;
        }
        return null;
    }

    #region P/Invoke
    private const uint ACCESS_SCHEME = 16;

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingGuid,
        uint accessFlags, uint index, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroupOfPowerSettingGuid,
        IntPtr powerSettingGuid, IntPtr buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupOfPowerSettingsGuid,
        ref Guid powerSettingGuid, out uint acValueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadPossibleValue(IntPtr rootPowerKey, ref Guid subGroupOfPowerSettingsGuid, ref Guid powerSettingGuid,
        out uint type, uint possibleSettingIndex, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetPwrCapabilities(IntPtr capabilities);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
    #endregion
}
