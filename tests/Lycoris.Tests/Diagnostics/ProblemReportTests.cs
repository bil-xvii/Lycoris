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

    [Theory]
    [InlineData(@"open C:\Users\bob\Desktop\a.txt", "bob")]
    [InlineData(@"open D:\Users\Мария Иванова\Documents", "Мария")]
    [InlineData(@"{""p"":""C:\\Users\\carol\\x""}", "carol")]
    [InlineData("open C:/Users/dave/x", "dave")]
    [InlineData("open /home/erin/.cfg", "erin")]
    [InlineData("open /Users/frank/Library", "frank")]
    [InlineData("open C\uFF1A\uFF3CUsers\uFF3Cgrace\uFF3Cx", "grace")]
    public void AnyProfilePathIsReplaced(string input, string name)
    {
        var r = ProblemReport.Anonymize(input, "someoneelse", "pc");
        Assert.DoesNotContain(name, r);
    }

    [Theory]
    [InlineData(@"open D:\Users\Мария Иванова\Documents", "Мария,Иванова")]
    [InlineData(@"open D:\Users\Ann Marie Lee\x", "Ann,Marie,Lee")]
    [InlineData(@"{""p"":""C:\\Users\\Иван Петров\\x""}", "Иван,Петров")]
    [InlineData(@"C:\Documents and Settings\Bob Smith\Desktop", "Bob,Smith")]
    [InlineData(@"D:\OneDrive - Контосо Групп\Docs", "Контосо,Групп")]
    [InlineData(@"C:\Users\bob\OneDrive - Contoso Ltd\Docs", "Contoso,Ltd,bob")]
    [InlineData(@"\\SRV\profiles$\carol\Desktop", "SRV,profiles,carol")]
    [InlineData(@"\\fileserver01\Home Dirs\Dana Lee\Documents\a.txt", "fileserver01,Home,Dirs,Dana,Lee")]
    [InlineData(@"{""p"":""\\\\SRV\\profiles$\\carol\\Desktop""}", "SRV,profiles,carol")]
    public void EveryPartOfAProfileOrNetworkPathIsRemoved(string input, string parts)
    {
        var r = ProblemReport.Anonymize(input, "someoneelse", "pc");
        foreach (var part in parts.Split(','))
            Assert.DoesNotContain(part, r);
    }

    [Fact]
    public void OwnProfilePathKeepsTheRestOfTheLine()
    {
        var r = ProblemReport.Anonymize(@"load failed C:\Users\alice\AppData\Local\app\x.dll code 5", "alice", "pc");
        Assert.DoesNotContain("alice", r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("load failed", r);
        Assert.Contains("code 5", r);
        Assert.DoesNotContain("<line removed>", r);
    }

    [Fact]
    public void InvisibleCharactersAndFullwidthDoNotHideTheUserName()
    {
        var r = ProblemReport.Anonymize("al\u200Bice \uFF41\uFF4C\uFF49\uFF43\uFF45 Alice", "alice", "pc");
        Assert.DoesNotContain("ali", r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\uFF41", r);
    }

    [Fact]
    public void NetworkIdentifiersAreValidatedAndReplaced()
    {
        var r = ProblemReport.Anonymize("a 10.20.30.40 b 2001:db8:0:0:0:0:0:1 c ::1 d fe80::1%12 e 00:1A:2B:3C:4D:5E f 001A.2B3C.4D5E g 999.1.1.1 h 12:34:56 i 2001:db8::1.", "zzq", "qqpc");
        foreach (var leak in new[] { "10.20.30.40", "2001:db8:0:0:0:0:0:1", "fe80::1", "00:1A:2B:3C:4D:5E", "001A.2B3C.4D5E", "2001:db8::1" })
            Assert.DoesNotContain(leak, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("12:34:56", r);   // a time is not an address: kept
        Assert.Contains("999.1.1.1", r);  // not an IPv4: kept
    }

    [Theory]
    [InlineData("addr fe80::1%eth0 up", "fe80", "eth0")]
    [InlineData("addr fe80::abcd:1%Ethernet 2 up", "abcd", "Ethernet")]
    [InlineData("glued deadfe80::1234 x", "1234", "fe80")]
    [InlineData("bad 2001:db8::zz::1 x", "2001", "db8")]
    [InlineData("eui 00-11-22-33-44-55-66-77 x", "66-77", "00-11")]
    [InlineData("eui 00:11:22:33:44:55:66:77 x", "66:77", "00:11")]
    [InlineData("mac aabbccddeeff x", "aabbccddeeff", "aabbcc")]
    [InlineData("mac 001a2b3c4d5e x", "001a2b3c4d5e", "3c4d5e")]
    [InlineData("v6 2001:db8:aa:bb:cc:dd:ee:ff x", "2001", "ee:ff")]
    [InlineData("v6 2001:db8:aa:bb:cc:dd:ee:ff x", "aa:bb", "dd:ee")]
    [InlineData("zone fe80::1%12 x", "fe80", "%12")]
    public void ExoticNetworkIdentifiersLeaveNoFragment(string input, string a, string b)
    {
        var r = ProblemReport.Anonymize(input, "zzq", "qqpc");
        Assert.DoesNotContain(a, r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b, r, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("мария@почта.рф", "мария", "почта")]
    [InlineData("мария@mail.xn--p1ai", "мария", "xn--p1ai")]
    [InlineData("alice@corp.com", "alice", "corp")]
    [InlineData("o'neil+tag@host.example.org", "neil", "host.example")]
    [InlineData("a!b#c$d%e&f*g/h=i?j^k`l{m|n}o~p-q@dom.test", "a!b", "dom")]
    [InlineData("user@sub.домен.москва", "user", "домен")]
    public void EmailsOfAnyShapeAreRemovedWholeEvenWithTheUserName(string input, string a, string b)
    {
        var r = ProblemReport.Anonymize("mail " + input + " end", "alice", "qqpc");
        Assert.DoesNotContain(a, r, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(b, r, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<email>", r);
        Assert.Contains("end", r);
    }

    [Theory]
    [InlineData("\"SerialNumber\":\"WD-X1\"", "WD-X1")]
    [InlineData("DiskSerial: X9Q", "X9Q")]
    [InlineData("serial: 1234 5678", "5678")]
    [InlineData("serial: 1234 5678", "1234")]
    [InlineData("Serial Number : AB 12 CD 34", "CD 34")]
    [InlineData("SerialNumber=ZZ-77 tail", "ZZ-77")]
    [InlineData("serial QQ-9", "QQ-9")]
    [InlineData("S/N: 12 34", "34")]
    [InlineData("серийный номер: ЮЯ 55", "55")]
    public void SerialNumbersOfAnyShapeAreRemoved(string input, string leak)
    {
        Assert.DoesNotContain(leak, ProblemReport.Anonymize("a\n" + input + "\nb", "zzq", "qqpc"));
    }

    [Fact]
    public void GuidTailsAndTimestampsAreNotTakenForMacs()
    {
        var r = ProblemReport.Anonymize("id {6F9619FF-8B86-D011-B42D-00C04FC964FF} t 202611030000 time 12:34:56", "zzq", "qqpc");
        Assert.Contains("00C04FC964FF", r);
        Assert.Contains("202611030000", r);
        Assert.Contains("12:34:56", r);
    }

    [Theory]
    [InlineData("api_key=AbCdEf0123456789AbCdEf0123456789", "AbCdEf0123456789")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9abcdefghijkl", "eyJhbGci")]
    [InlineData("password: hunter2", "hunter2")]
    [InlineData("пароль=Секрет123", "Секрет123")]
    [InlineData("token \"deadbeefdeadbeefdeadbeefdeadbeef\"", "deadbeef")]
    [InlineData("mail bob@example.test now", "bob@")]
    [InlineData("Serial Number: ABC-123-XYZ", "ABC-123")]
    [InlineData("serial=SN99887766", "SN99887766")]
    public void SecretsEmailAndSerialsAreRemoved(string input, string leak)
    {
        Assert.DoesNotContain(leak, ProblemReport.Anonymize(input, "zzq", "qqpc"));
    }

    [Theory]
    [InlineData("ApiKey=LEAK1234")]
    [InlineData("accessToken: LEAK1234")]
    [InlineData("refreshToken=LEAK1234")]
    [InlineData("privateKey=LEAK1234")]
    [InlineData("clientSecret=LEAK1234")]
    [InlineData("authToken=LEAK1234")]
    [InlineData("licenseKey=LEAK1234")]
    [InlineData("Cookie: LEAK1234")]
    [InlineData("sessionId=LEAK1234")]
    [InlineData("UserSid=LEAK1234")]
    [InlineData("ConnectionString=LEAK1234")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==")]
    public void CamelCaseAndExtraSecretWordsAreRedacted(string input)
    {
        var r = ProblemReport.Anonymize(input, "zzq", "qqpc");
        Assert.DoesNotContain("LEAK1234", r);
        Assert.DoesNotContain("dXNlcjpw", r);
    }

    [Theory]
    [InlineData("\"password\":\"hunter2\"", "hunter2")]
    [InlineData("password hunter2", "hunter2")]
    [InlineData("Bearer abc123", "abc123")]
    [InlineData("Authorization: Bearer abc123", "abc123")]
    [InlineData("{\\\"token\\\":\\\"s3cr3t\\\"}", "s3cr3t")]
    [InlineData("pwd = 'p w d'", "p w d")]
    [InlineData("passwd:x1", "x1")]
    [InlineData("PASSWORD=\"a b\" next", "a b")]
    [InlineData("пароль Qwerty1", "Qwerty1")]
    public void ShortAndJsonSecretValuesAreRedactedWhateverTheirShape(string input, string leak)
    {
        Assert.DoesNotContain(leak, ProblemReport.Anonymize(input, "zzq", "qqpc"));
    }

    [Fact]
    public void SecretWordWithoutSeparatorBesideATokenDropsTheLine()
    {
        var r = ProblemReport.Anonymize("ok\nsecret deadbeef12345678deadbeef12\ntoken\nabc9def8ghi7jkl6mno5 token\nfine", "zzq", "qqpc");
        Assert.DoesNotContain("deadbeef", r);
        Assert.DoesNotContain("abc9def8", r);
        Assert.Contains("fine", r);
    }

    [Fact]
    public void OrdinaryWordsContainingKeyAreKept()
    {
        Assert.Contains("monkey business", ProblemReport.Anonymize("monkey business", "zzq", "qqpc"));
    }

    [Fact]
    public void UncleanableLinesAreDroppedAndOtherLinesKept()
    {
        var r = ProblemReport.Anonymize("keep me\n" + new string('a', 9000) + "\nsecond ok", "zzq", "qqpc");
        Assert.Contains("keep me", r);
        Assert.Contains("second ok", r);
        Assert.DoesNotContain(new string('a', 100), r);
    }

    [Fact]
    public void AnonymizeHasADeadlineAndTruncatesTheRest()
    {
        var text = string.Join("\n", Enumerable.Range(0, 50).Select(i => "line " + i));
        var r = ProblemReport.Anonymize(text, new[] { ("zzq", "<user>") }, TimeSpan.Zero);
        Assert.Contains("<truncated>", r);
        Assert.DoesNotContain("line 40", r);
    }

    [Fact]
    public void PathologicalInputFinishesQuickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = ProblemReport.Anonymize(string.Join("\n", Enumerable.Repeat(new string(':', 3000) + "a" + new string('1', 3000), 20)), "zzq", "qqpc");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20));
        Assert.NotNull(r);
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
