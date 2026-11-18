using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Lycoris.Core.Services;

internal static class ServiceRemovalAudit
{
    internal const int Access = 0x01000000;
    private const uint ImpersonateWithPrivileges = 0x002C;
    private const uint PrivilegeEnabled = 2;

    // Duplicate the process identity for a narrow impersonated open. The process token is never changed.
    // Windows only requires SeSecurityPrivilege while opening an ACCESS_SYSTEM_SECURITY handle.
    internal static T Open<T>(Func<T> open)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        if (!DuplicateTokenEx(identity.AccessToken, ImpersonateWithPrivileges, 0, 2, 2, out var token))
        {
            var error = Marshal.GetLastPInvokeError();
            token?.Dispose();
            throw new Win32Exception(error);
        }

        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeSecurityPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            var privilege = new TokenPrivileges { Count = 1, Id = luid, Attributes = PrivilegeEnabled };
            var adjusted = AdjustTokenPrivileges(token, false, in privilege, 0, 0, 0);
            var error = Marshal.GetLastPInvokeError();
            if (!adjusted || error != 0) throw new Win32Exception(error);
            return WindowsIdentity.RunImpersonated(token, open);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint Low;
        public int High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Id;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeAccessTokenHandle source, uint rights, nint securityAttributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        in TokenPrivileges newState, uint bufferLength, nint previousState, nint returnLength);
}

