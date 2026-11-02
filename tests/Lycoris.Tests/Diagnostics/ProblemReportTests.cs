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

    [Fact]
    public void LogScanLooksAtMostFiftyFilesAndHonoursReadBudget()
    {
        var dir = TempDir();
        try
        {
            for (var i = 0; i < 80; i++) File.WriteAllText(Path.Combine(dir, $"f{i:000}.log"), "x");
            // The newest file is far beyond the first 50 enumerated: it must not be reached when the scan is capped.
            var newest = Path.Combine(dir, "zzz-newest.log");
            File.WriteAllText(newest, "NEWEST-MARKER");
            File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(1));
            var logs = ProblemReport.CollectLogs(dir);
            Assert.Equal(3, logs.Count);
            Assert.DoesNotContain(logs, l => System.Text.Encoding.UTF8.GetString(l.Data).Contains("NEWEST-MARKER"));

            // Read budget: three 400 KB files with a 700 KB budget give only one log.
            var big = TempDir();
            try
            {
                for (var i = 0; i < 3; i++) File.WriteAllText(Path.Combine(big, $"b{i}.log"), new string('a', 400 * 1024));
                Assert.Single(ProblemReport.CollectLogs(big, 50, 700 * 1024));
            }
            finally { try { Directory.Delete(big, true); } catch { } }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void JournalSummaryHasNoTitlesTargetsOrValues()
    {
        var records = new[]
        {
            new Lycoris.Core.ChangeRecord { Title = "Заголовок alice SECRET-TITLE", Target = @"reg:HKCU\Software\SECRET-TARGET", OldValue = "OLD-SECRET", NewValue = "NEW-SECRET", Source = "tweak:SECRET-SRC", Kind = Lycoris.Core.ChangeKind.Registry },
            new Lycoris.Core.ChangeRecord { Title = "Другой", Kind = Lycoris.Core.ChangeKind.Registry },
            new Lycoris.Core.ChangeRecord { Title = "Служба", Kind = Lycoris.Core.ChangeKind.Service },
        };
        var json = ProblemReport.SummarizeJournal(records);
        Assert.DoesNotContain("SECRET", json);
        Assert.DoesNotContain("Заголовок", json);
        Assert.DoesNotContain("Другой", json);
        Assert.Contains("\"Registry\"", json);
        Assert.Contains("\"Changes\": 2", json);
    }
}
