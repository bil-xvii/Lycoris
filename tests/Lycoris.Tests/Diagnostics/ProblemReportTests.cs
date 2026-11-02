using System.IO;
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
        Assert.Contains("Привет", ProblemReport.Anonymize("Привет", "alice", "pc"));
    }

    [Fact]
    public void ArchiveIsBoundedAndImplementationHasNoNetworkClientTypes()
    {
        Assert.Equal(5 * 1024 * 1024, ProblemReport.MaxArchiveBytes);
        var methods = typeof(ProblemReport).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.DoesNotContain(methods.SelectMany(m => m.GetMethodBody()?.LocalVariables.Select(v => v.LocalType) ?? []),
            t => t == typeof(HttpClient) || t == typeof(Socket) || t.Name == "WebClient");
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "lyc-pr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void LogsUseFixedNamesNotSourceNames()
    {
        var dir = TempDir();
        try
        {
            foreach (var n in new[] { "alice_secret-host.log", "C-Users-bob.txt", "a@b.test.log", "4.log", "5.log" })
                File.WriteAllText(Path.Combine(dir, n), "line");
            var logs = ProblemReport.CollectLogs(dir);
            Assert.Equal(3, logs.Count);
            Assert.Equal(new[] { "logs/log-1.txt", "logs/log-2.txt", "logs/log-3.txt" }, logs.Select(l => l.Name).ToArray());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
