using System.Runtime.InteropServices;
using Monolith.Core;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>
/// Read-only boot-security facts (tech.md §7): firmware type (kernel32 GetFirmwareType), UEFI Secure Boot
/// (registry; Confirm-SecureBootUEFI only as an admin fallback) and the TPM version (tbs.dll Tbsi_GetDeviceInfo).
/// Nothing here writes anything or touches firmware; every failure reads as "unknown".
/// </summary>
internal static class FirmwareProbe
{
    private const string SecureBootKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";
    private const uint TBS_SUCCESS = 0;
    private const uint TBS_E_TPM_NOT_FOUND = 0x8028400F;

    public static async Task<(FirmwareKind Firmware, bool? SecureBoot, TpmState Tpm)> ReadAsync(bool elevated)
    {
        var firmware = Safe(ReadFirmware, FirmwareKind.Unknown);
        var tpm = await Probe.RunAsync("TPM", ReadTpm, TimeSpan.FromSeconds(10), TpmState.Unknown).ConfigureAwait(false);
        // Secure Boot exists only on UEFI: on a Legacy BIOS boot the rule reports the boot mode instead.
        if (firmware == FirmwareKind.LegacyBios) return (firmware, null, tpm);
        var secureBoot = Safe(ReadSecureBootRegistry, null);
        if (secureBoot is null && firmware == FirmwareKind.Uefi && elevated)
            secureBoot = await ReadSecureBootPowerShellAsync().ConfigureAwait(false);
        return (firmware, secureBoot, tpm);
    }

    public static FirmwareKind ReadFirmware() =>
        GetFirmwareType(out var type) ? type switch { 1 => FirmwareKind.LegacyBios, 2 => FirmwareKind.Uefi, _ => FirmwareKind.Unknown } : FirmwareKind.Unknown;

    private static bool? ReadSecureBootRegistry() =>
        RegistryHelper.ReadInt(RegHive.HKLM, SecureBootKey, "UEFISecureBootEnabled") switch { 1 => true, 0 => false, _ => null };

    /// <summary>Confirm-SecureBootUEFI (admin only): "True"/"False"; "Cmdlet not supported" or any error = unknown.</summary>
    private static async Task<bool?> ReadSecureBootPowerShellAsync()
    {
        try
        {
            var r = await ProcessRunner.RunPowerShellAsync(
                "try { if (Confirm-SecureBootUEFI -ErrorAction Stop) { 'SB=ON' } else { 'SB=OFF' } } catch { 'SB=UNKNOWN' }",
                new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(15), LogCommand = false }).ConfigureAwait(false);
            return HealthRules.ParseSecureBootOutput(r.StdOut);
        }
        catch (Exception ex)
        {
            Log.Warn($"Secure Boot: {ex.Message}");
            return null;
        }
    }

    /// <summary>TPM_DEVICE_INFO.tpmVersion: 1 = TPM 1.2, 2 = TPM 2.0. TBS_E_TPM_NOT_FOUND = no TPM or fTPM/PTT off in BIOS.</summary>
    public static TpmState ReadTpm()
    {
        try
        {
            var info = new TPM_DEVICE_INFO();
            var rc = Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TPM_DEVICE_INFO>(), ref info);
            if (rc == TBS_E_TPM_NOT_FOUND) return TpmState.NotFound;
            if (rc != TBS_SUCCESS) return TpmState.Unknown;
            return info.tpmVersion switch { 1 => TpmState.V12, 2 => TpmState.V20, _ => TpmState.Unknown };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return TpmState.Unknown;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception ex)
        {
            Log.Warn($"Проверка загрузки: {ex.Message}");
            return fallback;
        }
    }

    #region P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    private struct TPM_DEVICE_INFO
    {
        public uint structVersion;
        public uint tpmVersion;
        public uint tpmInterfaceType;
        public uint tpmImpRevision;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    [DllImport("tbs.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Tbsi_GetDeviceInfo(uint size, ref TPM_DEVICE_INFO info);
    #endregion
}
