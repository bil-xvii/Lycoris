using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Lycoris.Core.Security;

internal static class ExpertAccessPolicy
{
    // The owner's Windows account and installation, represented only by their SHA-256 fingerprint.
    internal const string OwnerFingerprint = "CD2F0719882DBAAAF132D4C7AB92C4EBE50D8BCC26E84373AEB27AA6AEECF530";
    private static readonly Lazy<string?> ProcessFingerprint = new(ReadProcessFingerprint);
    private static readonly AsyncLocal<Func<string?>?> TestFingerprintProvider = new();

    internal static bool CanUseExpertMode => MatchesFingerprint(TestFingerprintProvider.Value is { } provider
        ? provider()
        : ProcessFingerprint.Value);

    internal static bool MatchesFingerprint(string? fingerprint) =>
        string.Equals(OwnerFingerprint, fingerprint, StringComparison.Ordinal);

    internal static string? ComputeFingerprint(string? sid, string? machineGuid)
    {
        if (string.IsNullOrWhiteSpace(sid) || !Guid.TryParse(machineGuid, out var guid) || guid == Guid.Empty)
            return null;

        try
        {
            var account = new SecurityIdentifier(sid);
            var material = $"lycoris-expert-owner-v1\nsid={account.Value}\nmachine={guid:D}\n";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadProcessFingerprint()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cryptography = machine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
            return ComputeFingerprint(Elevation.OwnerSid(Environment.ProcessId),
                cryptography?.GetValue("MachineGuid", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
        }
        catch
        {
            // An unreadable identity denies access. Never include identity values in diagnostics.
            return null;
        }
    }

    /// <summary>Tests only: supplies a fingerprint in the current asynchronous flow.</summary>
    internal static IDisposable OverrideFingerprintForTests(Func<string?> provider)
    {
        var scope = new FingerprintScope(TestFingerprintProvider.Value);
        TestFingerprintProvider.Value = provider;
        return scope;
    }

    private sealed class FingerprintScope(Func<string?>? previous) : IDisposable
    {
        public void Dispose() => TestFingerprintProvider.Value = previous;
    }
}
