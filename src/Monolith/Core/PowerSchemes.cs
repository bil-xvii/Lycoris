using System.Runtime.InteropServices;

namespace Monolith.Core;

public sealed record PowerScheme(Guid Id, string Name, bool IsActive);

/// <summary>Power plans through powrprof.dll (no parsing of localized powercfg output).</summary>
public static class PowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3bcb-4fb4-a5a6-92eeb7c4f6d2");
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
        SafetyLock.ThrowIfLocked("смена плана питания");
        var rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (rc != 0) throw new InvalidOperationException($"Не удалось включить план питания {scheme} (код {rc}).");
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
        SafetyLock.ThrowIfLocked("создание плана питания");
        var dest = IntPtr.Zero;
        var rc = PowerDuplicateScheme(IntPtr.Zero, ref source, ref dest);
        if (rc != 0 || dest == IntPtr.Zero)
            throw new InvalidOperationException($"Не удалось создать план питания (код {rc}).");
        try { return Marshal.PtrToStructure<Guid>(dest); }
        finally { LocalFree(dest); }
    }

    public static void Delete(Guid scheme)
    {
        if (scheme == Balanced || scheme == HighPerformance || scheme == PowerSaver) return; // never delete built-ins
        SafetyLock.ThrowIfLocked("удаление плана питания");
        if (GetActive() == scheme) return;
        PowerDeleteScheme(IntPtr.Zero, ref scheme);
    }

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

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
    #endregion
}
