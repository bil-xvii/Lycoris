using System.IO;
using Lycoris.Core.Security;

namespace Lycoris.Tests.Security;

/// <summary>The «no certificate table» shortcut must agree with WinVerifyTrust and never hide a signature.</summary>
public sealed class AuthenticodeFastPathTests
{
    [Fact]
    public void Unsigned_pe_is_detected_from_the_header_and_agrees_with_WinVerifyTrust()
    {
        var dll = typeof(Authenticode).Assembly.Location; // locally built, never signed
        Assert.True(Authenticode.HasNoCertificateTable(dll));
        Assert.Equal(SignatureState.Unsigned, Authenticode.Check(dll));
    }

    [Fact]
    public void Embedded_signature_is_never_skipped()
    {
        var host = Environment.ProcessPath!; // testhost.exe / dotnet.exe: Microsoft, embedded Authenticode
        Assert.False(Authenticode.HasNoCertificateTable(host));
        Assert.NotEqual(SignatureState.Unsigned, Authenticode.Check(host));
    }

    [Fact]
    public void Non_pe_or_missing_file_falls_back_to_the_full_check()
    {
        var f = Path.Combine(Path.GetTempPath(), "lycoris-not-pe-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(f, "MZ but not really a PE file");
        try { Assert.False(Authenticode.HasNoCertificateTable(f)); }
        finally { File.Delete(f); }
        Assert.False(Authenticode.HasNoCertificateTable(f + ".missing"));
    }
}
