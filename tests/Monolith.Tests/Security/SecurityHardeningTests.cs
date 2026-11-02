using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Monolith.Core;
using Monolith.Core.Security;
using Monolith.Modules.Cleaner;
using Monolith.Shell;

namespace Monolith.Tests.Security;

/// <summary>Hostile inputs and trust decisions (docs/security-audit.md). Offline, read-only on the real system.</summary>
public sealed class SecurityHardeningTests
{
    // ───────────────────────────── The corpus (same one the self-test runs) ─────────────────────────────

    [Fact]
    public void Hostile_input_corpus_is_fully_refused()
    {
        var result = SecurityCorpus.Run();
        Assert.True(result.Checks > 80, "the corpus shrank: " + result.Checks);
        Assert.True(result.Failures.Count == 0, string.Join(Environment.NewLine, result.Failures));
    }

    // ───────────────────────────── Admin intent replay ─────────────────────────────

    [Theory]
    [InlineData("Format")]
    [InlineData("Apply;calc")]
    [InlineData("Apply\n")]
    [InlineData("..\\Apply")]
    [InlineData("Run")]
    [InlineData("apply")]
    public void Replay_refuses_commands_outside_the_allow_list(string command) =>
        Assert.Null(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", command]));

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("tab\there")]
    public void Replay_refuses_parameters_with_control_characters(string parameter) =>
        Assert.Null(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "Apply", "--replay-param", parameter]));

    [Fact]
    public void Replay_refuses_an_overlong_parameter_and_accepts_the_limit()
    {
        Assert.Null(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "Apply", "--replay-param", new string('x', AdminIntent.MaxParameterLength + 1)]));
        Assert.NotNull(AdminIntent.Parse(["--replay-page", "optimize", "--replay-command", "Apply", "--replay-param", new string('x', AdminIntent.MaxParameterLength)]));
    }

    [Theory]
    [InlineData("optimize", true)]
    [InlineData("devices-manual", true)]
    [InlineData("Optimize", false)]
    [InlineData("..\\x", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    public void Page_keys_are_plain_words(string key, bool ok) => Assert.Equal(ok, AdminIntent.IsPageKey(key));

    [Fact]
    public void Every_page_that_requests_replay_has_a_valid_key()
    {
        // The real keys must pass the new check (otherwise a legitimate replay would silently stop working).
        foreach (var key in new[] { "dashboard", "optimize", "games", "services", "maintenance", "drivers", "devices", "apps", "restore", "settings", "startup", "cleaner", "tools" })
            Assert.True(AdminIntent.IsPageKey(key), key);
    }

    // ───────────────────────────── Who may write a path ─────────────────────────────

    private static DirectorySecurity Acl(params (WellKnownSidType Sid, FileSystemRights Rights, bool InheritOnly)[] rules)
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        acl.SetAccessRuleProtection(true, false);
        foreach (var (sid, rights, inheritOnly) in rules)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                inheritOnly ? PropagationFlags.InheritOnly : PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    [Fact]
    public void Acl_with_only_trusted_writers_is_protected()
    {
        var acl = Acl((WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl, false),
                      (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, false),
                      (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute, false));
        Assert.Null(PathTrust.FindUntrustedWriter(acl, PathTrust.FolderWriteRights));
        Assert.Null(PathTrust.FindUntrustedWriter(acl, PathTrust.FileWriteRights));
    }

    [Theory]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.WorldSid)]
    public void Acl_that_lets_ordinary_accounts_write_is_flagged(WellKnownSidType who)
    {
        var acl = Acl((WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, false), (who, FileSystemRights.Modify, false));
        Assert.NotNull(PathTrust.FindUntrustedWriter(acl, PathTrust.FolderWriteRights));
    }

    [Fact]
    public void Acl_rule_that_only_applies_to_children_does_not_make_the_folder_itself_writable()
    {
        var acl = Acl((WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl, false),
                      (WellKnownSidType.AuthenticatedUserSid, FileSystemRights.Modify, true)); // C:\ looks like this
        Assert.Null(PathTrust.FindUntrustedWriter(acl, PathTrust.AncestorRights));
    }

    [Fact]
    public void An_untrusted_owner_counts_as_a_writer()
    {
        var acl = new DirectorySecurity();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)); // the owner can always rewrite the ACL
        Assert.NotNull(PathTrust.FindUntrustedWriter(acl, PathTrust.FileWriteRights));
    }

    [Fact]
    public void System32_is_protected_and_a_temp_file_is_not()
    {
        Assert.Null(PathTrust.WhyUserWritable(ProcessRunner.Sys("cmd.exe")));
        var temp = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(temp, [1]);
        try
        {
            Assert.NotNull(PathTrust.WhyUserWritable(temp));
            Assert.True(PathTrust.IsHostileLocation(temp));
        }
        finally { File.Delete(temp); }
    }

    [Fact]
    public void Missing_or_unreadable_paths_fail_closed()
    {
        Assert.True(PathTrust.IsUserWritable(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".exe")));
        Assert.True(PathTrust.IsUserWritable(@"\\server\share\a.exe"));
    }

    [Theory]
    [InlineData(@"\\server\share\MONOLITH.exe", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    public void Hostile_locations(string path, bool hostile) => Assert.Equal(hostile, PathTrust.IsHostileLocation(path));

    // ───────────────────────────── Elevation gate ─────────────────────────────

    private static SelfFacts Facts(string? start = "aa", string? now = "aa", bool hostile = false, SignatureState sig = SignatureState.Unsigned,
        string[]? bad = null, string? writable = null) => new(start, now, hostile, sig, bad ?? [], writable);

    [Fact]
    public void Clean_exe_in_a_protected_folder_is_allowed_without_warning()
    {
        var v = SelfIntegrity.Evaluate(Facts());
        Assert.True(v.Allowed);
        Assert.Null(v.Warning);
    }

    [Fact]
    public void Exe_in_a_writable_folder_is_allowed_with_a_warning_that_names_the_fix()
    {
        var v = SelfIntegrity.Evaluate(Facts(writable: "BUILTIN\\Users"));
        Assert.True(v.Allowed);
        Assert.Contains("Program Files", v.Warning);
    }

    [Fact]
    public void A_swapped_exe_is_refused()
    {
        var v = SelfIntegrity.Evaluate(Facts(start: "aa", now: "bb"));
        Assert.False(v.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(v.Reason));
    }

    [Fact] public void A_hostile_location_is_refused() => Assert.False(SelfIntegrity.Evaluate(Facts(hostile: true)).Allowed);
    [Fact] public void A_tampered_signature_is_refused() => Assert.False(SelfIntegrity.Evaluate(Facts(sig: SignatureState.Tampered)).Allowed);
    [Fact] public void A_valid_or_self_signed_exe_is_allowed()
    {
        Assert.True(SelfIntegrity.Evaluate(Facts(sig: SignatureState.Valid)).Allowed);
        Assert.True(SelfIntegrity.Evaluate(Facts(sig: SignatureState.Untrusted)).Allowed);
    }

    [Fact]
    public void A_planted_native_library_is_refused()
    {
        var v = SelfIntegrity.Evaluate(Facts(bad: [@"C:\Users\x\AppData\Local\Temp\.net\MONOLITH\abc\wpfgfx_cor3.dll"]));
        Assert.False(v.Allowed);
        Assert.Contains("wpfgfx_cor3.dll", v.Reason);
    }

    [Fact]
    public void Unknown_current_hash_cannot_veto_but_a_known_mismatch_does()
    {
        Assert.True(SelfIntegrity.Evaluate(Facts(start: null, now: "bb")).Allowed);
        Assert.False(SelfIntegrity.Evaluate(Facts(start: "aa", now: "bb")).Allowed);
    }

    [Fact]
    public void Extraction_folders_are_recognised_and_checked_for_signatures()
    {
        Assert.True(BundleGuard.IsExtractionFolder(@"C:\Users\x\AppData\Local\Temp\.net\MONOLITH\1a2b3c"));
        Assert.False(BundleGuard.IsExtractionFolder(@"C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.0.0"));

        var dir = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "good.dll"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "evil.dll"), [2]);
            var bad = BundleGuard.FindUntrustedLibraries([dir], f => f.EndsWith("good.dll") ? SignatureState.Valid : SignatureState.Unsigned);
            Assert.Equal([Path.Combine(dir, "evil.dll")], bad);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ───────────────────────────── Authenticode ─────────────────────────────

    [Fact]
    public void Wintrust_codes_are_classified()
    {
        Assert.Equal(SignatureState.Valid, Authenticode.Classify(0));
        Assert.Equal(SignatureState.Unsigned, Authenticode.Classify(unchecked((int)0x800B0100)));
        Assert.Equal(SignatureState.Tampered, Authenticode.Classify(unchecked((int)0x80096010)));
        Assert.Equal(SignatureState.Untrusted, Authenticode.Classify(unchecked((int)0x800B0109)));
        Assert.Equal(SignatureState.Error, Authenticode.Classify(unchecked((int)0x80092003)));
    }

    [Fact]
    public void A_Microsoft_signed_runtime_file_is_valid_and_a_changed_copy_is_not()
    {
        var coreclr = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "coreclr.dll");
        Assert.True(File.Exists(coreclr));
        Assert.Equal(SignatureState.Valid, Authenticode.Check(coreclr));

        var dir = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var copy = Path.Combine(dir, "coreclr.dll");
            var bytes = File.ReadAllBytes(coreclr);
            bytes[bytes.Length / 3] ^= 0xFF; // one flipped byte inside the image
            File.WriteAllBytes(copy, bytes);
            Assert.Equal(SignatureState.Tampered, Authenticode.Check(copy));

            var plain = Path.Combine(dir, "plain.exe");
            File.WriteAllBytes(plain, [0x4D, 0x5A, 0, 0]);
            Assert.NotEqual(SignatureState.Valid, Authenticode.Check(plain));
            Assert.Equal(SignatureState.Error, Authenticode.Check(Path.Combine(dir, "missing.exe")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_exe_hash_is_stable_and_detects_a_change()
    {
        var f = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(f, [1, 2, 3]);
        try
        {
            var a = SelfIntegrity.Sha256OfFile(f);
            Assert.Equal(64, a!.Length);
            Assert.Equal(a, SelfIntegrity.Sha256OfFile(f));
            File.WriteAllBytes(f, [1, 2, 4]);
            Assert.NotEqual(a, SelfIntegrity.Sha256OfFile(f));
        }
        finally { File.Delete(f); }
        Assert.Null(SelfIntegrity.Sha256OfFile(f));
    }

    // ───────────────────────────── Hardening ─────────────────────────────

    [Fact]
    public void Process_hardening_is_applied_and_the_process_still_works()
    {
        var r = Hardening.Apply();
        Assert.True(r.DllDirectoriesRestricted, r.Details);
        Assert.True(r.ImageLoadPolicy, r.Details);
        // A plain-name system DLL still loads through the restricted search order (what every [DllImport] of the app does).
        Assert.NotEqual(IntPtr.Zero, LoadLibraryW("winmm.dll"));
        Assert.Same(r, Hardening.Current);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string name);

    // ───────────────────────────── Environment ─────────────────────────────

    [Fact]
    public void Injecting_variables_never_reach_a_child()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = "x", ["CORECLR_PROFILER"] = "x", ["COR_PROFILER_PATH_64"] = "x", ["NODE_OPTIONS"] = "--require x", ["PYTHONSTARTUP"] = "x",
            ["PATH"] = @"C:\Windows", ["TEMP"] = @"C:\t",
        };
        var removed = EnvironmentGuard.Clean(env);
        Assert.Equal(5, removed.Count);
        Assert.Equal(["PATH", "TEMP"], env.Keys.Order().ToArray());
    }

    [Fact]
    public async Task PowerShell_children_get_system_module_folders_only()
    {
        var userModules = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WindowsPowerShell", "Modules");
        var r = await ProcessRunner.RunPowerShellAsync("$env:PSModulePath", new ProcessRunOptions
        {
            // Even when the user's environment (this dictionary stands for HKCU\Environment) names a module folder first, it is replaced.
            Environment = new Dictionary<string, string> { ["PSModulePath"] = userModules + ";C:\\evil" },
            Timeout = TimeSpan.FromSeconds(60),
        });
        Assert.True(r.Success, r.StdErr);
        var path = r.StdOut.Trim();
        Assert.DoesNotContain("evil", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(userModules, path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("system32", path, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── PowerShell text ─────────────────────────────

    [Theory]
    [InlineData("a'; Write-Output PWNED; '")]
    [InlineData("a\u2019; Write-Output PWNED; \u2018")]
    [InlineData("C:\\Users\\O\u2019Brien\\x.exe")]
    [InlineData("$(Write-Output PWNED)")]
    [InlineData("`n; PWNED")]
    public async Task A_hostile_value_stays_one_string_inside_a_real_PowerShell(string value)
    {
        var r = await ProcessRunner.RunPowerShellAsync("Write-Output " + PsText.Literal(value), new ProcessRunOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(r.Success, r.StdErr);
        Assert.Equal(value, r.StdOut.Trim());
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    [InlineData("a\rb")]
    public void Control_characters_cannot_enter_a_script(string value) => Assert.Throws<ArgumentException>(() => PsText.Literal(value));

    [Fact]
    public async Task A_signature_check_of_a_hostile_path_runs_no_code_and_reports_a_failure()
    {
        var (ok, _) = await VendorDownloads.VerifySignatureAsync("C:\\nope\\x\u2019; Write-Output PWNED; \u2018.exe", ["Anyone"]);
        Assert.False(ok);
    }

    // ───────────────────────────── Files ─────────────────────────────

    [Fact]
    public void Bounded_reads_refuse_a_file_over_the_limit()
    {
        var f = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(f, new byte[2048]);
        try
        {
            Assert.Equal(2048, SafeIo.ReadBytes(f, 2048).Length);
            Assert.Throws<InvalidDataException>(() => SafeIo.ReadBytes(f, 2047));
            File.WriteAllText(f, "\uFEFF{\"a\":1}");
            Assert.Equal("{\"a\":1}", SafeIo.ReadText(f));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Plain_text_check()
    {
        Assert.True(SafeIo.IsPlainText("Discord.exe", 20));
        Assert.False(SafeIo.IsPlainText("a\nb", 20));
        Assert.False(SafeIo.IsPlainText(new string('a', 21), 20));
        Assert.False(SafeIo.IsPlainText(null, 20));
    }

    // ───────────────────────────── Settings ─────────────────────────────

    [Fact]
    public void Default_settings_are_clean()
    {
        var s = new AppSettings();
        Assert.Equal(0, SettingsValidator.Sanitize(s));
    }

    [Fact]
    public void A_hostile_settings_file_is_clamped()
    {
        var json = "{\"MonitorIntervalMs\": -5, \"Language\": \"xx\", \"GameBoostCloseMode\": \"Rm\", \"CustomGameFolders\": [\"\\\\\\\\evil\\\\share\", \"C:\\\\Games\"], \"GameBoostCloseList\": [\"a\\nb\", \"ok\"]}";
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, new System.Text.Json.JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        Assert.True(SettingsValidator.Sanitize(s) >= 4);
        Assert.Equal(250, s.MonitorIntervalMs);
        Assert.Equal("auto", s.Language);
        Assert.Equal(AppSettings.CloseModeClose, s.GameBoostCloseMode);
        Assert.Equal([@"C:\Games"], s.CustomGameFolders);
        Assert.Equal(["ok"], s.GameBoostCloseList);
    }

    [Fact]
    public void Lists_are_capped()
    {
        var s = new AppSettings { GameBoostCloseList = Enumerable.Range(0, 5000).Select(i => "app" + i).ToList() };
        SettingsValidator.Sanitize(s);
        Assert.Equal(SettingsValidator.MaxListItems, s.GameBoostCloseList.Count);
    }

    [Theory]
    [InlineData(@"C:\Games", true)]
    [InlineData(@"D:\Steam Library\steamapps", true)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\?\C:\Windows", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\a\..\b", false)]
    [InlineData(@"games", false)]
    [InlineData("", false)]
    public void Custom_game_folders(string path, bool ok) => Assert.Equal(ok, SettingsValidator.IsLocalAbsoluteFolder(path));

    // ───────────────────────────── winget, cleaner, journal ─────────────────────────────

    [Fact]
    public void An_elevated_process_runs_only_the_winget_alias_not_a_plain_file()
    {
        var f = Path.Combine(Path.GetTempPath(), "mn-sec-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllBytes(f, [1]);
        try
        {
            Assert.True(WingetClient.IsTrustedAlias(f, elevated: false));
            Assert.False(WingetClient.IsTrustedAlias(f, elevated: true));
            Assert.False(WingetClient.IsTrustedAlias(f + ".none", elevated: true));
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Elevated_cleaner_skips_tools_in_user_writable_folders()
    {
        var cmds = new[] { new ToolCommand(ProcessRunner.Sys("cmd.exe"), "/c x", "npm") { Tool = @"C:\Users\x\AppData\Roaming\npm\npm.cmd" } };
        Assert.Null(CleanRunner.UntrustedToolReason(cmds, elevated: false, _ => "user"));
        Assert.NotNull(CleanRunner.UntrustedToolReason(cmds, elevated: true, p => p.EndsWith("npm.cmd") ? "BUILTIN\\Users" : null));
        Assert.Null(CleanRunner.UntrustedToolReason(cmds, elevated: true, _ => null));
        Assert.Equal(@"C:\Users\x\AppData\Roaming\npm\npm.cmd", cmds[0].Program);
    }

    [Theory]
    [InlineData("Environment", "PSModulePath")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "windir")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager", "BootExecute")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs", "x")]
    [InlineData(@"Software\Microsoft\Command Processor", "AutoRun")]
    [InlineData(@"SOFTWARE\Classes\CLSID\{11111111-1111-1111-1111-111111111111}\InprocServer32", "")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Print\Monitors\evil", "Driver")]
    public void More_hijack_locations_are_refused_on_undo(string path, string name) =>
        Assert.True(ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot { Hive = RegHive.HKLM, Path = path, Name = name }));

    [Fact]
    public void The_classic_context_menu_tweak_can_still_be_undone()
    {
        Assert.False(ChangeJournal.IsForbiddenRestoreTarget(new RegistryValueSnapshot
        {
            Hive = RegHive.HKCU, Path = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", Name = "",
        }));
    }

    // ───────────────────────────── Update channel building blocks ─────────────────────────────

    [Fact]
    public void Release_signature_round_trip_and_tamper()
    {
        var (priv, pub) = ReleaseSignature.GenerateKeyPair();
        var statement = ReleaseSignature.SignedStatement("1.2.3", "MONOLITH.exe", new string('A', 64));
        var sig = ReleaseSignature.Sign(statement, priv);
        Assert.True(ReleaseSignature.Verify(statement, sig, pub));

        // Another version, file name or hash invalidates the signature.
        Assert.False(ReleaseSignature.Verify(ReleaseSignature.SignedStatement("1.2.4", "MONOLITH.exe", new string('A', 64)), sig, pub));
        Assert.False(ReleaseSignature.Verify(ReleaseSignature.SignedStatement("1.2.3", "OTHER.exe", new string('A', 64)), sig, pub));
        Assert.False(ReleaseSignature.Verify(ReleaseSignature.SignedStatement("1.2.3", "MONOLITH.exe", new string('B', 64)), sig, pub));

        // Someone else's key does not verify.
        var (_, otherPub) = ReleaseSignature.GenerateKeyPair();
        Assert.False(ReleaseSignature.Verify(statement, sig, otherPub));
        // A flipped signature byte does not verify.
        var raw = Convert.FromBase64String(sig);
        raw[10] ^= 1;
        Assert.False(ReleaseSignature.Verify(statement, Convert.ToBase64String(raw), pub));
    }

    [Fact]
    public void Verify_never_throws_on_garbage()
    {
        var (_, pub) = ReleaseSignature.GenerateKeyPair();
        var data = Encoding.UTF8.GetBytes("x");
        foreach (var sig in new[] { null, "", "   ", "not base64!!", "AAAA", new string('A', 200) })
            Assert.False(ReleaseSignature.Verify(data, sig, pub));
        Assert.False(ReleaseSignature.Verify(data, Convert.ToBase64String(new byte[64]), "garbage"));
        Assert.False(ReleaseSignature.Verify(data, Convert.ToBase64String(new byte[64]), null));
    }

    [Fact]
    public void The_pinned_key_is_empty_until_the_owner_sets_it()
    {
        // Documents the open item: while empty an updater must report the channel as unauthenticated.
        Assert.Equal(ReleaseSignature.PinnedPublicKey.Length > 0, ReleaseSignature.IsPinned);
    }

    [Theory]
    [InlineData("2.0", "1.9", null, false)]
    [InlineData("2.0", "2.0", null, false)]
    [InlineData("2.0", "2.1", null, true)]
    [InlineData("2.0", "2.1", "2.2", false)]
    [InlineData("2.0", "2.3", "2.2", true)]
    public void Rollback_protection(string current, string candidate, string? floor, bool ok) =>
        Assert.Equal(ok, ReleaseSignature.IsAcceptableVersion(Version.Parse(current), Version.Parse(candidate), floor is null ? null : Version.Parse(floor)));
}
