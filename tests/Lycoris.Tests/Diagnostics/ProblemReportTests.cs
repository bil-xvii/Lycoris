using System.Net.Http;
using System.Net.Sockets;
using Lycoris.Core.Diagnostics;

namespace Lycoris.Tests.Diagnostics;

public sealed class ProblemReportTests
{
    [Fact]
    public void AnonymizeRedactsIdentityNetworkAndSecretsAndKeepsUnicode()
    {
        var input = "alice DESKTOP-ABC 192.0.2.1 fe80::1 aa:bb:cc:dd:ee:ff alice@example.test token=secret Привет";
        var result = ProblemReport.Anonymize(input, "alice", "DESKTOP-ABC");
        Assert.DoesNotContain("alice", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DESKTOP-ABC", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.1", result);
        Assert.DoesNotContain("fe80::1", result);
        Assert.DoesNotContain("aa:bb:cc:dd:ee:ff", result);
        Assert.DoesNotContain("alice@example.test", result);
        Assert.DoesNotContain("secret", result);
        Assert.Contains("Привет", result);
    }

    [Fact]
    public void ArchiveIsBoundedAndImplementationHasNoNetworkClientTypes()
    {
        Assert.Equal(5 * 1024 * 1024, ProblemReport.MaxArchiveBytes);
        var methods = typeof(ProblemReport).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.DoesNotContain(methods.SelectMany(m => m.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []),
            t => t == typeof(HttpClient) || t == typeof(Socket) || t.Name == "WebClient");
        Assert.DoesNotContain(typeof(ProblemReport).Assembly.GetReferencedAssemblies(), a => a.Name == "System.Net.Http");
    }
}
