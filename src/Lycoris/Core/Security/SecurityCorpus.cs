using System.Text;
using Lycoris.Modules.Games;
using Lycoris.Modules.Optimize;
using Lycoris.Shell;
using Lycoris.Core.Diagnostics;

namespace Lycoris.Core.Security;

/// <summary>
/// The hostile-input corpus: crafted launch arguments, profile files, settings, journal records, package ids, links and
/// parser bombs that every untrusted-input entry point must refuse (or survive). Read-only and offline — it only calls pure
/// parsers/validators. Used by the self-test stage «БЕЗОПАСНОСТЬ» and by the unit tests, so a new entry point is one line here.
/// </summary>
public static class SecurityCorpus
{
    public sealed record Result(int Checks, IReadOnlyList<string> Failures);

    public static Result Run()
    {
        var checks = 0;
        var failures = new List<string>();

        void Expect(string name, Func<bool> ok)
        {
            checks++;
            try
            {
                if (!ok()) failures.Add(name);
            }
            catch (Exception ex)
            {
                failures.Add($"{name} — exception {ex.GetType().Name}: {ex.Message}");
            }
        }

        Expect("diagnostics redacts hostile mixed identity and secret", () =>
        {
            var value = ProblemReport.Anonymize("alice 2001:db8::1 aa:bb:cc:dd:ee:ff alice@host.test api_token=do-not-leak \u041F\u0440\u0438\u0432\u0435\u0442", "alice", "pc"); // loc:ignore
            return !value.Contains("alice", StringComparison.OrdinalIgnoreCase) && !value.Contains("2001:db8::1", StringComparison.Ordinal)
                && !value.Contains("aa:bb:cc:dd:ee:ff", StringComparison.Ordinal) && !value.Contains("alice@host.test", StringComparison.Ordinal)
                && !value.Contains("do-not-leak", StringComparison.Ordinal) && ProblemReport.Anonymize("\u041F\u0440\u0438\u0432\u0435\u0442", "alice", "pc").Contains("\u041F\u0440\u0438\u0432\u0435\u0442", StringComparison.Ordinal); // loc:ignore
        });

        Expect("diagnostics redacts camelCase/JSON secrets, spaced profile, UNC, zoned IPv6, unicode e-mail, serial", () =>
        {
            var value = ProblemReport.Anonymize("{\"accessToken\":\"hunter2\"} D:\\Users\\\u041C\u0430\u0440\u0438\u044F \u0418\u0432\u0430\u043D\u043E\u0432\u0430\\Docs \\\\SRV\\profiles$\\carol\\Desktop fe80::1%eth0 \u043C\u0430\u0440\u0438\u044F@\u043F\u043E\u0447\u0442\u0430.\u0440\u0444 DiskSerial: WD-X1 2001:db8:aa:bb:cc:dd:ee:ff", "alice", "pc"); // loc:ignore
            string[] leaks = ["hunter2", "\u041C\u0430\u0440\u0438\u044F", "\u0418\u0432\u0430\u043D\u043E\u0432\u0430", "SRV", "carol", "eth0", "fe80", "\u043F\u043E\u0447\u0442\u0430", "WD-X1", "2001:db8", "ee:ff"]; // loc:ignore
            return leaks.All(l => !value.Contains(l, StringComparison.OrdinalIgnoreCase));
        });

        // ── Replay arguments of the elevated copy (--replay-page / --replay-command / --replay-param) ──
        string[] badCommands = ["Format", "Delete", "Apply;calc", "Apply calc", "Apply\n", "..\\Apply", "Apply()", "Apply.Execute", "041D04350442", "", new string('A', 65), "ApplyCommand", "apply"];
        foreach (var c in badCommands)
            Expect($"replay command refused: «{c}»", () => AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", c]) is null);
        string[] badPages = ["..\\x", "Optimize", "opt imize", "opti\nmize", "", "a/b", "-x", new string('a', 33), "043E043F04420438043C04380437043004460438044F"];
        foreach (var p in badPages)
            Expect($"replay page refused: «{p}»", () => AdminIntent.Parse(["--replay-page", p, "--replay-command", "Apply"]) is null);
        string[] badParams = ["a\nb", "a\0b", "a\rb", "\u001b[31m", new string('x', AdminIntent.MaxParameterLength + 1)];
        foreach (var p in badParams)
            Expect($"replay parameter refused (len {p.Length})", () => AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "Apply", "--replay-param", p]) is null);
        Expect("replay: a valid intent still parses", () => AdminIntent.Parse(["--replay-page", "restore", "--replay-command", "RevertBatch", "--replay-param", "3f2504e0-4f89-11d3-9a0c-0305e82c3301"]) is not null);

        // ── Relaunch arguments never carry a remembered action, a language override or the parent id of another run ──
        Expect("relaunch strips replay args", () =>
        {
            var args = Elevation.RelaunchArguments(["--replay-page", "x", "--replay-command", "Apply", "--page", "devices", "--elevated"], null, 1);
            return !args.Contains("--replay-command") && !args.Contains("x") && args.Count(a => a == "--elevated") == 1;
        });

        // ── Imported profile file ──
        Expect("profile: empty", () => OptimizeProfileFile.Parse([], out _) is null);
        Expect("profile: oversize", () => OptimizeProfileFile.Parse(new byte[OptimizeProfileFile.MaxBytes + 1], out _) is null);
        Expect("profile: not json", () => OptimizeProfileFile.Parse(Encoding.UTF8.GetBytes("MZ\u0090\0\0"), out _) is null);
        Expect("profile: invalid UTF-8", () => OptimizeProfileFile.Parse([0xC3, 0x28, 0xFF], out _) is null);
        Expect("profile: nesting bomb", () => OptimizeProfileFile.Parse(Encoding.UTF8.GetBytes(new string('[', 5000) + new string(']', 5000)), out _) is null);
        Expect("profile: wrong format tag", () => OptimizeProfileFile.Parse(Encoding.UTF8.GetBytes("{\"format\":\"evil\",\"version\":1,\"tweaks\":[]}"), out _) is null);
        Expect("profile: hostile ids are counted invalid, never kept", () =>
        {
            var json = "{\"format\":\"lycoris-optimize-profile\",\"version\":1,\"tweaks\":[\"../x\",\"a;calc\",\"a b\",\"A\",\"\",\"..\\\\..\\\\x\",\"ok-id\"]}";
            var data = OptimizeProfileFile.Parse(Encoding.UTF8.GetBytes(json), out _);
            return data is not null && data.Ids.SequenceEqual(["ok-id"]) && data.InvalidIds == 6;
        });
        Expect("profile: too many ids", () =>
            OptimizeProfileFile.Parse(Encoding.UTF8.GetBytes("{\"format\":\"lycoris-optimize-profile\",\"version\":1,\"tweaks\":[" + string.Join(',', Enumerable.Repeat("\"a\"", OptimizeProfileFile.MaxIds + 1)) + "]}"), out _) is null);

        // ── settings.json ──
        Expect("settings: hostile values are clamped", () =>
        {
            var s = new AppSettings
            {
                MonitorIntervalMs = 1, Language = "../../x", DevicesTab = "<script>", GameBoostCloseMode = "Evil", LastProfile = "a\nb",
                GameBoostCloseList = ["ok", "bad\nname", new string('x', 500), "ok"],
                CustomGameFolders = [@"\\evil\share", @"C:\", @"C:\games\..\..\Windows", @"relative\dir", @"C:\Games", "\\\\?\\C:\\Windows", "C:\\a\0b"],
            };
            var fixes = SettingsValidator.Sanitize(s);
            return fixes > 0 && s.MonitorIntervalMs == 250 && s.Language == "auto" && s.DevicesTab == "auto" && s.GameBoostCloseMode == AppSettings.CloseModeClose
                   && s.LastProfile == "Recommended" && s.GameBoostCloseList.SequenceEqual(["ok"]) && s.CustomGameFolders.SequenceEqual([@"C:\Games"]);
        });
        Expect("settings: a null list does not crash", () => { var s = new AppSettings { GameBoostCloseList = null! }; SettingsValidator.Sanitize(s); return s.GameBoostCloseList is { Count: 0 }; });

        // ── Undo journal (forged records) ──
        Expect("journal: Run key is a forbidden restore target", () =>
            ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "x" }));
        Expect("journal: user Environment is a forbidden restore target", () =>
            ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKCU, Path = "Environment", Name = "PSModulePath" }));
        Expect("journal: IFEO is a forbidden restore target", () =>
            ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\x.exe", Name = "Debugger" }));
        Expect("journal: forged command from a user folder is refused", () =>
            JournalTrust.Check(new ChangeRecord { Source = "tweak:x", Kind = ChangeKind.Command, Target = "cmd:hibernate", RevertFile = @"C:\Users\Public\evil.exe", RevertArgs = "" }) is not null);
        Expect("journal: forged cmd line is refused", () =>
            JournalTrust.Check(new ChangeRecord { Source = "tweak:x", Kind = ChangeKind.Command, Target = "cmd:hibernate", RevertFile = ProcessRunner.Sys("cmd.exe"), RevertArgs = "/d /s /c \"calc.exe & whoami\"" }) is not null);
        Expect("journal: unknown source is refused", () =>
            JournalTrust.Check(new ChangeRecord { Source = "malware", Kind = ChangeKind.Command, Target = "cmd:x", RevertFile = ProcessRunner.Sys("cmd.exe") }) is not null);

        // ── winget ──
        string[] badIds = ["a b", "a;b", "a&calc", "a|b", "a\"b", "a'b", "-v", "--force", "../x", "a\nb", "", new string('a', 200), "id`whoami`", "$(calc)", "A\u2019b"];
        foreach (var id in badIds)
            Expect($"winget id refused: «{id.Replace("\n", "\\n")}»", () => !WingetClient.IsValidPackageId(id));
        Expect("winget: install args refuse a hostile id", () =>
        {
            try { WingetClient.BuildInstallArgs(new WingetPackageRequest("x --force")); return false; }
            catch (ArgumentException) { return true; }
        });
        string[] badLocations = [@"%ProgramFiles%\..\Windows", @"%ProgramFiles%\a\..\..\b", @"C:\Windows", @"%ProgramFiles%\a&b", @"%LOCALAPPDATA%\x\\y", @"%PATH%\x", @"%ProgramFiles%\a\"];
        foreach (var l in badLocations)
            Expect($"winget location refused: {l}", () => !WingetClient.IsValidLocationTemplate(l));
        Expect("winget export: a hostile JSON gives an empty map", () =>
            WingetClient.ParseExport("{\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":42},null,{\"PackageIdentifier\":\"ok\",\"Version\":{}}]}]}").Count <= 1
            && WingetClient.ParseExport(new string('[', 10_000)).Count == 0);

        // ── PowerShell text ──
        foreach (var hostile in new[] { "a'; calc; '", "a\u2019; calc; \u2018", "a\u201A\u201B", "a'b" })
            Expect($"PowerShell literal escapes quotes: {hostile}", () =>
            {
                var lit = PsText.Literal(hostile);
                // Between the outer quotes every single-quote variant must come doubled.
                var inner = lit[1..^1];
                for (var i = 0; i < inner.Length; i++)
                {
                    if (inner[i] is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B')
                    {
                        if (i + 1 >= inner.Length || inner[i + 1] != inner[i]) return false;
                        i++;
                    }
                }
                return true;
            });
        Expect("PowerShell literal refuses control characters", () =>
        {
            try { PsText.Literal("a\nb"); return false; }
            catch (ArgumentException) { return true; }
        });

        // ── Links, downloads, shell ──
        foreach (var url in new[] { "http://dl.example/x.exe", "https://user:pw@download.nvidia.com/x.exe", "https://download.nvidia.com:8443/x.exe", "https://evil.example/x.exe",
                     "https://download.nvidia.com.evil.example/x.exe", "https://download.nvidia.com/x.msi", "file:///C:/Windows/System32/calc.exe", "ftp://download.nvidia.com/x.exe" })
            Expect($"download URL refused: {url}", () =>
                !Uri.TryCreate(url, UriKind.Absolute, out var u) || !VendorDownloads.IsAllowedUrl(u, ["download.nvidia.com"]));
        foreach (var name in new[] { "..\\x.exe", "a/b.exe", "a b.exe", "con.exe.", ".exe", "x.exe;y", "x:evil.exe" })
            Expect($"download file name refused: {name}", () => !Uri.TryCreate("https://h.example/" + Uri.EscapeDataString(name), UriKind.Absolute, out var u) || VendorDownloads.SafeFileName(u) is null);
        foreach (var target in new[] { "file:///C:/Windows/System32/calc.exe", "http://example.com", "javascript:alert(1)", "ms-msdt:/id PCWDiagnostic /skip force /param \"IT_BrowseForFile=x\"", "\\\\evil\\share\\a.exe", "calc.exe", "" })
            Expect($"browser open refused: {target}", () => !UnelevatedShell.OpenAsync(target).GetAwaiter().GetResult());
        foreach (var path in new[] { @"C:\Users\Public\evil.exe", @"C:\ProgramData\Lycoris\Downloads\..\..\evil.exe", @"\\evil\share\x.exe", @"C:\ProgramData\Lycoris\Downloads\sub\x.exe" })
            Expect($"installer outside the downloads folder refused: {path}", () => !Lycoris.Modules.Drivers.Hardware.VendorInstallerRunner.IsInDownloadsFolder(path, VendorDownloads.RootDir));

        // ── Parser bombs ──
        Expect("VDF: nesting bomb", () => { VdfParser.Parse(new string('{', 200_000)); return true; });
        Expect("VDF: unterminated string", () => { VdfParser.Parse("\"" + new string('a', 1_000_000)); return true; });
        Expect("EcoQoS marker: hostile JSON", () => Lycoris.Modules.Games.EcoQos.ParseMarker("[{\"Pid\":-1},{\"Pid\":2147483647,\"StartTimeUtc\":\"x\"},null]").Count == 0
                                                  && Lycoris.Modules.Games.EcoQos.ParseMarker(new string('[', 50_000)).Count == 0);
        Expect("startup manifest: DTD / entity bomb", () =>
        {
            var bomb = "<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]><Package>&b;</Package>";
            return Lycoris.Modules.Startup.PackagedStartup.ParseManifest(bomb) is null;
        });

        Expect("XML from outside: a DTD / entity bomb is refused, plain XML is read", () =>
        {
            try { SafeXml.Parse("<?xml version=\"1.0\"?><!DOCTYPE l [<!ENTITY a \"aaaaaaaaaa\">]><r>&a;</r>"); return false; }
            catch (System.Xml.XmlException) { }
            return SafeXml.Parse("<r><a>1</a></r>").Root?.Element("a")?.Value == "1";
        });
        Expect("update: an ordinary release answer with hostile fields is refused or harmless", () =>
        {
            var json = "{\"tag_name\":\"v9.9.9\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":-5,\"browser_download_url\":\"http://evil.example/Lycoris.exe\"}]}";
            var release = Updates.ReleaseParser.TryParse(json, out _);
            // Whatever the parser keeps, the downloader's host check must refuse the link.
            return release is null || !Updates.UpdateVerifier.IsAssetUrl(release.ExeUrl, new Updates.UpdatePolicy(), Updates.ReleaseParser.ExeAssetName);
        });
        Expect("update: links outside the releases repository are refused", () =>
        {
            var policy = new Updates.UpdatePolicy();
            foreach (var u in new[] { "https://github.com/evil/Lycoris-releases/releases/download/v1/Lycoris.exe", "https://github.com/bil-xvii/Lycoris-releases/releases/download/../../x/Lycoris.exe",
                         "https://evil.github.com/bil-xvii/Lycoris-releases/releases/download/v1/Lycoris.exe", "https://github.com:444/bil-xvii/Lycoris-releases/releases/download/v1/Lycoris.exe",
                         "https://u:p@github.com/bil-xvii/Lycoris-releases/releases/download/v1/Lycoris.exe", "http://github.com/bil-xvii/Lycoris-releases/releases/download/v1/Lycoris.exe" })
                if (Updates.UpdateVerifier.IsAssetUrl(u, policy, "Lycoris.exe")) return false;
            return Updates.UpdateVerifier.IsAssetUrl("https://github.com/bil-xvii/Lycoris-releases/releases/download/v1.0.1/Lycoris.exe", policy, "Lycoris.exe");
        });
        Expect("update: the updater refuses a forged call", () =>
            Updates.UpdateApplyMode.Run(["--apply-update", @"C:\Windows\System32\cmd.exe", "1", new string('a', 64)], new Updates.SystemUpdateEnvironment(), @"C:\nope\Lycoris.exe.new", elevated: true) == 13);

        // ── Environment / signatures / release channel ──
        Expect("environment: injecting variables are removed from a child", () =>
        {
            var env = new Dictionary<string, string?> { ["DOTNET_STARTUP_HOOKS"] = "x.dll", ["COR_PROFILER"] = "{x}", ["PATH"] = "C:\\Windows", ["pythonstartup"] = "x.py" };
            var removed = EnvironmentGuard.Clean(env);
            return removed.Count == 3 && env.Count == 1 && env.ContainsKey("PATH");
        });
        Expect("environment: PowerShell gets system module folders only", () =>
        {
            var env = new Dictionary<string, string?> { ["PSModulePath"] = @"C:\Users\x\Documents\WindowsPowerShell\Modules" };
            EnvironmentGuard.ApplyForPowerShell(env);
            var path = env["PSModulePath"] ?? "";
            return path.Length > 0 && !path.Contains("Users", StringComparison.OrdinalIgnoreCase) && path.Split(';').All(f => f.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase)
                                                                         || f.StartsWith(Environment.SystemDirectory, StringComparison.OrdinalIgnoreCase));
        });
        Expect("signature: garbage never verifies", () =>
        {
            var (_, pub) = ReleaseSignature.GenerateKeyPair();
            var data = Encoding.UTF8.GetBytes("x");
            return !ReleaseSignature.Verify(data, "AAAA", pub) && !ReleaseSignature.Verify(data, null, pub) && !ReleaseSignature.Verify(data, new string('A', 86) + "==", "garbage")
                   && !ReleaseSignature.Verify([], "", pub);
        });
        Expect("signature: a signature of other data does not verify", () =>
        {
            var (priv, pub) = ReleaseSignature.GenerateKeyPair();
            var sig = ReleaseSignature.Sign(Encoding.UTF8.GetBytes("one"), priv);
            return ReleaseSignature.Verify(Encoding.UTF8.GetBytes("one"), sig, pub) && !ReleaseSignature.Verify(Encoding.UTF8.GetBytes("two"), sig, pub);
        });
        Expect("update: rollback is refused", () =>
            !ReleaseSignature.IsAcceptableVersion(new Version(2, 0), new Version(1, 9)) && !ReleaseSignature.IsAcceptableVersion(new Version(2, 0), new Version(2, 0))
            && ReleaseSignature.IsAcceptableVersion(new Version(2, 0), new Version(2, 1)) && !ReleaseSignature.IsAcceptableVersion(new Version(2, 0), new Version(2, 1), new Version(2, 2)));

        // ── The cleaner never leaves its sandbox ──
        foreach (var path in new[] { @"C:\", @"C:\Windows", @"C:\Windows\System32", @"\\evil\share\x", @"\\?\C:\Windows", @"C:\Users", "" })
            Expect($"cleaner base refused: {path}", () => Modules.Cleaner.PathGuard.Default.CheckBase(path) != Modules.Cleaner.GuardVerdict.Ok);

        return new Result(checks, failures);
    }
}
