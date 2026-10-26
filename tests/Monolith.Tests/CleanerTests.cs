using System.Diagnostics;
using System.IO;
using Monolith.Modules.Cleaner;

namespace Monolith.Tests;

/// <summary>
/// Cleaner safety tests. Everything that touches the disk works only inside a private folder under %TEMP%
/// created (and removed) by the test itself.
/// </summary>
public sealed class CleanerTests : IDisposable
{
    private readonly string _root;
    private readonly List<string> _links = [];

    public CleanerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "MONOLITH-CleanerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Remove junctions first (deleting a junction never touches its target), then the sandbox.
        foreach (var link in _links)
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
        }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ───────────────────────────── Helpers ─────────────────────────────

    private string Dir(params string[] parts)
    {
        var p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string OldFile(string path, int size = 100, double ageDays = 10)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        var t = DateTime.UtcNow.AddDays(-ageDays);
        File.SetCreationTimeUtc(path, t);
        File.SetLastWriteTimeUtc(path, t);
        return path;
    }

    private static void AgeDir(string path, double ageDays = 10)
    {
        var t = DateTime.UtcNow.AddDays(-ageDays);
        Directory.SetCreationTimeUtc(path, t);
        Directory.SetLastWriteTimeUtc(path, t);
    }

    private string Junction(string link, string target)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15000);
        Assert.True(Directory.Exists(link), "mklink /J failed: " + p.StandardError.ReadToEnd());
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
        _links.Add(link);
        return link;
    }

    private static TargetManifest Analyze(CleanTarget t) =>
        CleanEngine.Analyze(t, PathGuard.Default, DateTime.UtcNow, CancellationToken.None);

    private static CleanTally Clean(TargetManifest m) => CleanEngine.Clean(m, PathGuard.Default, CancellationToken.None);

    // ───────────────────────────── Path guard: pure checks ─────────────────────────────

    public static TheoryData<string> ProtectedPaths()
    {
        var data = new TheoryData<string>();
        foreach (var f in new[]
                 {
                     Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.ApplicationData,
                 })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) data.Add(p);
        }
        data.Add(Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!); // C:\Users
        data.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedPaths))]
    public void Guard_refuses_system_and_user_folders(string path) =>
        Assert.NotEqual(GuardVerdict.Ok, PathGuard.Default.CheckBase(path));

    [Fact]
    public void Guard_refuses_drive_roots_network_relative_and_empty()
    {
        Assert.Equal(GuardVerdict.DriveRoot, PathGuard.Default.CheckBase(@"C:\"));
        Assert.Equal(GuardVerdict.DriveRoot, PathGuard.Default.CheckBase(@"D:\"));
        Assert.Equal(GuardVerdict.DriveRoot, PathGuard.Default.CheckBase(@"C:\Temp\.."));
        Assert.Equal(GuardVerdict.Network, PathGuard.Default.CheckBase(@"\\server\share\cache"));
        Assert.Equal(GuardVerdict.Network, PathGuard.Default.CheckBase(@"\\?\UNC\server\share\cache"));
        Assert.Equal(GuardVerdict.NotAbsolute, PathGuard.Default.CheckBase(@"relative\cache"));
        Assert.Equal(GuardVerdict.NotAbsolute, PathGuard.Default.CheckBase(@"C:cache"));
        Assert.Equal(GuardVerdict.Empty, PathGuard.Default.CheckBase("  "));
    }

    [Fact]
    public void Guard_refuses_folders_inside_no_touch_areas()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(Path.Combine(windows, "WinSxS", "Temp")));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(Path.Combine(windows, "Installer", "x")));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(Path.Combine(windows, "Prefetch")));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(Path.Combine(windows, "System32", "LogFiles")));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(Path.Combine(docs, "SomeApp", "Cache")));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(@"D:\x\INetCache\Content.Outlook"));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckBase(@"D:\Profile\Default\Local Storage"));
    }

    [Fact]
    public void Guard_allows_real_cache_locations()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(GuardVerdict.Ok, PathGuard.Default.CheckBase(Path.GetTempPath()));
        Assert.Equal(GuardVerdict.Ok, PathGuard.Default.CheckBase(Path.Combine(windows, "Temp")));
        Assert.Equal(GuardVerdict.Ok, PathGuard.Default.CheckBase(Path.Combine(local, "D3DSCache")));
        Assert.Equal(GuardVerdict.Ok, PathGuard.Default.CheckBase(Path.Combine(local, @"Microsoft\Edge\User Data\Default\Cache\Cache_Data")));
    }

    [Fact]
    public void Single_file_base_may_be_a_protected_folder_but_never_a_root_or_no_touch_area()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.Equal(GuardVerdict.Ok, PathGuard.Default.CheckSingleFileBase(windows));
        Assert.NotEqual(GuardVerdict.Ok, PathGuard.Default.CheckBase(windows));
        Assert.Equal(GuardVerdict.DriveRoot, PathGuard.Default.CheckSingleFileBase(@"C:\"));
        Assert.Equal(GuardVerdict.NoTouchArea, PathGuard.Default.CheckSingleFileBase(Environment.SystemDirectory));
    }

    [Theory]
    [InlineData(@"C:\a\..\b\", @"C:\b")]
    [InlineData(@"C:/x/y", @"C:\x\y")]
    [InlineData(@"\\?\C:\x\", @"C:\x")]
    [InlineData(@"\??\C:\x\y.tmp", @"C:\x\y.tmp")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"""C:\Quoted\""", @"C:\Quoted")]
    public void Paths_are_normalized(string input, string expected) => Assert.Equal(expected, PathGuard.Normalize(input));

    [Theory]
    [InlineData(@"C:\Temp\a", @"C:\Temp", true)]
    [InlineData(@"C:\TEMP\a\b", @"C:\temp", true)]
    [InlineData(@"C:\Temp2\a", @"C:\Temp", false)]
    [InlineData(@"C:\Temp", @"C:\Temp", false)]
    [InlineData(@"C:\x", @"C:\", true)]
    [InlineData(@"D:\Temp\a", @"C:\Temp", false)]
    public void Strict_containment(string path, string root, bool expected) => Assert.Equal(expected, PathGuard.IsStrictlyUnder(path, root));

    [Fact]
    public void Files_outside_the_base_are_never_allowed()
    {
        var guard = new PathGuard([], []);
        Assert.True(guard.IsFileAllowed(@"C:\Base\sub\f.tmp", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"C:\Base", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"C:\Base2\f.tmp", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"C:\Base\..\Other\f.tmp", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"\\server\share\Base\f.tmp", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"relative\f.tmp", @"C:\Base"));
        Assert.False(guard.IsFileAllowed(@"C:\Base\IndexedDB\f", @"C:\Base"));
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint, true)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, true)]
    [InlineData((FileAttributes)0x00400000, true)] // RECALL_ON_DATA_ACCESS (cloud placeholder)
    [InlineData((FileAttributes)0x00040000, true)] // RECALL_ON_OPEN
    [InlineData(FileAttributes.Offline, true)]
    [InlineData(FileAttributes.Normal, false)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly, false)]
    [InlineData(FileAttributes.Directory, false)]
    public void Links_and_placeholders_are_skipped(FileAttributes attributes, bool skip) =>
        Assert.Equal(skip, PathGuard.ShouldSkip(attributes));

    // ───────────────────────────── Engine on a sandbox ─────────────────────────────

    [Fact]
    public void Analysis_honours_min_age_and_deletes_nothing()
    {
        var b = Dir("age");
        var old = OldFile(Path.Combine(b, "old.tmp"), 300, 3);
        var fresh = Path.Combine(b, "new.tmp");
        File.WriteAllBytes(fresh, new byte[50]);

        var m = Analyze(new CleanTarget { Base = b, MinAge = TimeSpan.FromDays(1) });

        Assert.Null(m.Refused);
        Assert.Single(m.Files);
        Assert.Equal(old, m.Files[0].Path, ignoreCase: true);
        Assert.Equal(300, m.Bytes);
        Assert.Equal(1, m.TooNew);
        Assert.True(File.Exists(old) && File.Exists(fresh), "analysis must never delete");
    }

    [Fact]
    public void Clean_deletes_manifest_files_removes_empty_subfolders_and_keeps_the_base()
    {
        var b = Dir("clean");
        var deep = OldFile(Path.Combine(b, "a", "b", "deep.bin"), 4096);
        var top = OldFile(Path.Combine(b, "top.bin"), 10);
        var keepDir = Path.Combine(b, "busy");
        Directory.CreateDirectory(keepDir);
        var fresh = Path.Combine(keepDir, "fresh.bin");
        File.WriteAllBytes(fresh, [1, 2, 3]);
        AgeDir(Path.Combine(b, "a", "b"));
        AgeDir(Path.Combine(b, "a"));
        AgeDir(keepDir);

        var m = Analyze(new CleanTarget { Base = b, MinAge = TimeSpan.FromDays(1) });
        Assert.Equal(2, m.Count);
        var t = Clean(m);

        Assert.Equal(2, t.Deleted);
        Assert.False(File.Exists(deep));
        Assert.False(File.Exists(top));
        Assert.False(Directory.Exists(Path.Combine(b, "a")), "empty sub-folders are removed");
        Assert.True(File.Exists(fresh), "files newer than the age filter stay");
        Assert.True(Directory.Exists(keepDir), "non-empty folders stay");
        Assert.True(Directory.Exists(b), "the base folder is never deleted");
        Assert.Equal(4096 + 10, t.DeletedLogicalBytes);
    }

    [Fact]
    public void Base_folder_stays_even_when_completely_emptied()
    {
        var b = Dir("emptied");
        OldFile(Path.Combine(b, "x.tmp"));
        AgeDir(b);
        var t = Clean(Analyze(new CleanTarget { Base = b }));
        Assert.Equal(1, t.Deleted);
        Assert.True(Directory.Exists(b));
        Assert.Empty(Directory.EnumerateFileSystemEntries(b));
    }

    [Fact]
    public void Locked_file_is_skipped_and_counted_as_in_use()
    {
        var b = Dir("locked");
        var locked = OldFile(Path.Combine(b, "locked.tmp"));
        var free = OldFile(Path.Combine(b, "free.tmp"));
        var m = Analyze(new CleanTarget { Base = b });

        CleanTally t;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
            t = Clean(m);

        Assert.Equal(1, t.InUse);
        Assert.Equal(1, t.Deleted);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
    }

    [Fact]
    public void Junction_inside_the_base_is_never_followed_or_deleted()
    {
        var outside = Dir("outside");
        var secret = OldFile(Path.Combine(outside, "secret.txt"));
        var b = Dir("withlink");
        var mine = OldFile(Path.Combine(b, "mine.tmp"));
        var link = Junction(Path.Combine(b, "link"), outside);

        var m = Analyze(new CleanTarget { Base = b });
        Assert.Single(m.Files);
        Assert.Equal(mine, m.Files[0].Path, ignoreCase: true);
        Assert.DoesNotContain(m.Dirs, d => d.Path.Equals(link, StringComparison.OrdinalIgnoreCase));

        Clean(m);
        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(secret), "a file behind a junction must survive");
        Assert.True(Directory.Exists(link), "the junction itself is not deleted");
    }

    [Fact]
    public void Junction_as_base_is_refused()
    {
        var real = Dir("real");
        var file = OldFile(Path.Combine(real, "data.bin"));
        var link = Junction(Path.Combine(_root, "baselink"), real);

        var m = Analyze(new CleanTarget { Base = link });
        Assert.Equal(GuardVerdict.ReparsePoint, m.Refused);
        Assert.Empty(m.Files);
        Clean(m);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Base_reached_through_a_junction_parent_is_refused()
    {
        var real = Dir("realparent", "sub");
        var file = OldFile(Path.Combine(real, "data.bin"));
        var link = Junction(Path.Combine(_root, "parentlink"), Path.GetDirectoryName(real)!);

        var m = Analyze(new CleanTarget { Base = Path.Combine(link, "sub") });
        Assert.Equal(GuardVerdict.ReparsePoint, m.Refused);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Folder_swapped_for_a_junction_after_analysis_is_detected()
    {
        var b = Dir("swap");
        var sub = Path.Combine(b, "sub");
        var victimDir = Dir("victim");
        OldFile(Path.Combine(sub, "file.dat"), 64);
        var m = Analyze(new CleanTarget { Base = b });
        Assert.Single(m.Files);

        // Attacker: replace the analysed folder with a junction to another folder holding an identical-looking file.
        var victim = OldFile(Path.Combine(victimDir, "file.dat"), 64);
        File.SetCreationTimeUtc(victim, DateTime.FromFileTimeUtc(m.Files[0].CreationFileTime));
        File.SetLastWriteTimeUtc(victim, DateTime.FromFileTimeUtc(m.Files[0].LastWriteFileTime));
        Directory.Delete(sub, recursive: true);
        Junction(sub, victimDir);

        var t = Clean(m);
        Assert.Equal(0, t.Deleted);
        Assert.Equal(1, t.Refused);
        Assert.True(File.Exists(victim), "the file behind the swapped-in junction must survive");
    }

    [Fact]
    public void File_changed_after_analysis_is_left_alone()
    {
        var b = Dir("changed");
        var f = OldFile(Path.Combine(b, "grow.log"), 10);
        var m = Analyze(new CleanTarget { Base = b });
        File.AppendAllText(f, "more");

        var t = Clean(m);
        Assert.Equal(1, t.Changed);
        Assert.True(File.Exists(f));
    }

    [Fact]
    public void Read_only_hidden_system_files_are_deleted()
    {
        var b = Dir("attrs");
        var f = OldFile(Path.Combine(b, "ro.tmp"));
        File.SetAttributes(f, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
        var t = Clean(Analyze(new CleanTarget { Base = b }));
        Assert.Equal(1, t.Deleted);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Patterns_and_keep_newest()
    {
        var b = Dir("dumps");
        for (var i = 0; i < 7; i++) OldFile(Path.Combine(b, $"dump{i}.dmp"), 10, 40 + i);
        var text = OldFile(Path.Combine(b, "readme.txt"), 10, 90);

        var m = Analyze(new CleanTarget { Base = b, Patterns = ["*.dmp"], Recurse = false, MinAge = TimeSpan.FromDays(30), KeepNewest = 5 });
        var names = m.Files.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n).ToList();
        Assert.Equal(["dump5.dmp", "dump6.dmp"], names); // the two oldest; the five newest are always kept
        Assert.True(File.Exists(text));
    }

    [Fact]
    public void Non_recursive_target_ignores_sub_folders()
    {
        var b = Dir("flat");
        OldFile(Path.Combine(b, "a.txt"));
        var nested = OldFile(Path.Combine(b, "nested", "b.txt"));
        var m = Analyze(new CleanTarget { Base = b, Recurse = false });
        Assert.Single(m.Files);
        Assert.Empty(m.Dirs);
        Clean(m);
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Only_top_dirs_limits_the_walk_and_an_empty_list_walks_nothing()
    {
        var b = Dir("shadercache");
        var orphan = OldFile(Path.Combine(b, "100", "x.bin"));
        var installed = OldFile(Path.Combine(b, "200", "y.bin"));
        var loose = OldFile(Path.Combine(b, "loose.bin"));
        AgeDir(Path.Combine(b, "100"));

        Assert.Empty(Analyze(new CleanTarget { Base = b, OnlyTopDirs = [] }).Files);

        var m = Analyze(new CleanTarget { Base = b, OnlyTopDirs = ["100"] });
        Assert.Single(m.Files);
        Clean(m);
        Assert.False(File.Exists(orphan));
        Assert.False(Directory.Exists(Path.Combine(b, "100")));
        Assert.True(File.Exists(installed));
        Assert.True(File.Exists(loose));
    }

    [Fact]
    public void Excluded_entries_are_skipped_with_their_content()
    {
        var b = Dir("excl");
        var keep = OldFile(Path.Combine(b, "Content.Outlook", "attachment.docx"));
        var keep2 = OldFile(Path.Combine(b, "{GUID}", "setup.exe"));
        var del = OldFile(Path.Combine(b, "other", "x.tmp"));
        var m = Analyze(new CleanTarget { Base = b, Exclude = ["{GUID}"] });
        Clean(m);
        Assert.True(File.Exists(keep), "Content.Outlook is a no-touch segment");
        Assert.True(File.Exists(keep2), "excluded entries stay");
        Assert.False(File.Exists(del));
    }

    [Fact]
    public void Single_file_target_takes_only_that_file()
    {
        var b = Dir("single");
        var dump = OldFile(Path.Combine(b, "MEMORY.DMP"), 1000, 20);
        var other = OldFile(Path.Combine(b, "other.dmp"), 1000, 20);
        var m = Analyze(new CleanTarget { Base = b, Patterns = ["MEMORY.DMP"], Recurse = false, SingleFile = true, RemoveEmptyDirs = false, MinAge = TimeSpan.FromDays(14) });
        Assert.Single(m.Files);
        Clean(m);
        Assert.False(File.Exists(dump));
        Assert.True(File.Exists(other));

        var wildcard = Analyze(new CleanTarget { Base = b, Patterns = ["*.dmp"], Recurse = false, SingleFile = true });
        Assert.NotNull(wildcard.Refused);
    }

    [Fact]
    public void Protected_and_missing_bases_touch_nothing()
    {
        var profile = Analyze(new CleanTarget { Base = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) });
        Assert.Equal(GuardVerdict.ProtectedFolder, profile.Refused);
        Assert.Empty(profile.Files);
        Assert.Equal(0, Clean(profile).Deleted);

        var root = Analyze(new CleanTarget { Base = @"C:\" });
        Assert.Equal(GuardVerdict.DriveRoot, root.Refused);

        var missing = Analyze(new CleanTarget { Base = Path.Combine(_root, "does-not-exist") });
        Assert.True(missing.Missing);
        Assert.Null(missing.Refused);
    }

    [Fact]
    public void Analysis_is_cancellable()
    {
        var b = Dir("cancel");
        for (var i = 0; i < 20; i++) OldFile(Path.Combine(b, $"d{i}", "f.tmp"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CleanEngine.Analyze(new CleanTarget { Base = b }, PathGuard.Default, DateTime.UtcNow, cts.Token));
    }

    [Fact]
    public void Lock_probe_detects_an_exclusively_held_lockfile()
    {
        var f = Path.Combine(Dir("lock"), "lockfile");
        File.WriteAllText(f, "");
        Assert.False(AppOwner.IsLockHeld(f));
        using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.True(AppOwner.IsLockHeld(f));
        Assert.False(AppOwner.IsLockHeld(f));
        Assert.False(AppOwner.IsLockHeld(Path.Combine(_root, "absent")));
    }

    // ───────────────────────────── Catalog ─────────────────────────────

    [Fact]
    public void Catalog_ids_are_unique_and_texts_complete()
    {
        var ids = CleanCatalog.All.Select(d => d.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(CleanCatalog.All, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            Assert.False(string.IsNullOrWhiteSpace(d.Effect));
        });
    }

    [Fact]
    public void Catalog_defaults_follow_research_tiers()
    {
        foreach (var d in CleanCatalog.All)
        {
            if (d.DefaultChecked) Assert.Equal(CleanTier.Safe, d.Tier);
            if (d.Tier == CleanTier.Expert) Assert.False(d.DefaultChecked);
        }
        string[] safeDefaults = ["temp.user", "temp.windows", "temp.systemtemp", "wer.user", "wer.machine", "shell.inetcache", "do.cache", "discord.cache", "browser.edge", "browser.chrome", "browser.firefox"];
        foreach (var id in safeDefaults) Assert.True(CleanCatalog.All.Single(d => d.Id == id).DefaultChecked, id);
        string[] optional = ["recyclebin", "gpu.d3d", "gpu.nvidia", "gpu.amd", "wu.download", "dump.memory", "dump.livekernel", "wu.cbs"];
        foreach (var id in optional) Assert.False(CleanCatalog.All.Single(d => d.Id == id).DefaultChecked, id);
    }

    [Fact]
    public void Do_not_include_items_are_absent()
    {
        string[] forbidden = ["prefetch", "wu.resetbase", "downloads", "steam.shader", "user.versions", "windows.old", "shell.iconcache", "shell.fontcache", "dev.nuget.global", "steam.downloading"];
        foreach (var id in forbidden) Assert.DoesNotContain(CleanCatalog.All, d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    private CleanEnvironment FakeEnvironment(string local, string roaming) => new()
    {
        LocalAppData = local, RoamingAppData = roaming, LocalLow = Dir("LocalLow"), Temp = Dir("Temp"), Windows = Dir("Windows"),
        ProgramData = Dir("ProgramData"), MemoryDumpFile = Path.Combine(_root, "Windows", "MEMORY.DMP"), MinidumpDir = Dir("Windows", "Minidump"),
        Servicing = ServicingState.None, Processes = ProcessSnapshot.Empty,
    };

    [Fact]
    public void Browser_targets_are_cache_folders_only_in_every_profile()
    {
        var local = Dir("Local");
        var userData = Dir("Local", "Google", "Chrome", "User Data");
        File.WriteAllText(Path.Combine(userData, "Local State"), """{"profile":{"info_cache":{"Default":{},"Profile 1":{}}}}""");
        foreach (var p in new[] { "Default", "Profile 1", "Profile 7" })
        {
            Dir("Local", "Google", "Chrome", "User Data", p, "Cache", "Cache_Data");
            Dir("Local", "Google", "Chrome", "User Data", p, "Code Cache", "js");
            Dir("Local", "Google", "Chrome", "User Data", p, "Local Storage");
            Dir("Local", "Google", "Chrome", "User Data", p, "IndexedDB");
            File.WriteAllText(Path.Combine(userData, p, "Cookies"), "");
            File.WriteAllText(Path.Combine(userData, p, "Preferences"), "{}");
        }
        Dir("Local", "Google", "Chrome", "User Data", "System Profile", "Cache", "Cache_Data");
        Dir("Local", "Google", "Chrome", "User Data", "ShaderCache");

        var env = FakeEnvironment(local, Dir("Roaming"));
        var targets = CleanCatalog.All.Single(d => d.Id == "browser.chrome").Targets(env);
        var bases = targets.Select(t => Path.GetRelativePath(userData, t.Base)).ToList();

        Assert.Contains(@"Default\Cache\Cache_Data", bases);
        Assert.Contains(@"Profile 1\Code Cache", bases);
        Assert.Contains(@"Profile 7\Cache\Cache_Data", bases); // found through its Preferences file
        Assert.Contains("ShaderCache", bases);
        Assert.DoesNotContain(bases, b => b.StartsWith("System Profile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bases, b => b.Contains("Local Storage") || b.Contains("IndexedDB") || b.Contains("Cookies"));
        Assert.All(targets, t => Assert.NotNull(t.Owner));
        Assert.All(targets, t => Assert.Contains(t.Owner!.LockFiles, l => l.EndsWith(@"User Data\lockfile", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Firefox_targets_use_the_local_cache_mirror_of_relative_profiles()
    {
        var local = Dir("FLocal");
        var roaming = Dir("FRoaming");
        Dir("FRoaming", "Mozilla", "Firefox", "Profiles", "abc.default-release");
        File.WriteAllText(Path.Combine(roaming, @"Mozilla\Firefox\profiles.ini"),
            "[Profile0]\r\nName=default-release\r\nIsRelative=1\r\nPath=Profiles/abc.default-release\r\n\r\n[Profile1]\r\nIsRelative=1\r\nPath=../../escape\r\n");
        Dir("FLocal", "Mozilla", "Firefox", "Profiles", "abc.default-release", "cache2", "entries");
        Dir("FLocal", "Mozilla", "Firefox", "Profiles", "abc.default-release", "startupCache");

        var targets = CleanCatalog.All.Single(d => d.Id == "browser.firefox").Targets(FakeEnvironment(local, roaming));
        Assert.Equal(2, targets.Count);
        Assert.All(targets, t => Assert.StartsWith(Path.Combine(local, "Mozilla"), t.Base, StringComparison.OrdinalIgnoreCase));
        Assert.All(targets, t => Assert.EndsWith("parent.lock", t.Owner!.LockFiles[0]));
    }

    [Fact]
    public void Setup_api_filter_matches_only_rotated_archives()
    {
        Assert.True(CleanCatalog.IsSetupApiArchive("setupapi.offline.20240401_072605.log"));
        Assert.True(CleanCatalog.IsSetupApiArchive("setupapi.dev.20250102_101010.log"));
        Assert.False(CleanCatalog.IsSetupApiArchive("setupapi.dev.log"));
        Assert.False(CleanCatalog.IsSetupApiArchive("setupapi.setup.log"));
        Assert.False(CleanCatalog.IsSetupApiArchive("setupapi.offline.log"));
        Assert.False(CleanCatalog.IsSetupApiArchive("setupapi.app.log"));
    }

    // ───────────────────────────── Parsers ─────────────────────────────

    [Fact]
    public void Local_state_profiles_are_parsed_and_unsafe_names_dropped()
    {
        var names = BrowserProfiles.ParseLocalStateProfiles(
            """{"profile":{"info_cache":{"Default":{"name":"A"},"Profile 3":{},"..\\evil":{},"C:\\x":{}}},"other":1}""");
        Assert.Equal(["Default", "Profile 3"], names);
        Assert.Empty(BrowserProfiles.ParseLocalStateProfiles("not json"));
        Assert.Empty(BrowserProfiles.ParseLocalStateProfiles("""{"profile":{}}"""));
    }

    [Fact]
    public void Firefox_profiles_ini_is_parsed()
    {
        var list = BrowserProfiles.ParseProfilesIni(
            "[Install308046B0AF4A39CB]\nDefault=Profiles/x.default-release\n\n[Profile1]\nName=work\nIsRelative=0\nPath=D:\\FF\\work\n\n[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/x.default-release\n[General]\nVersion=2\n");
        Assert.Equal(2, list.Count);
        Assert.Contains((@"D:\FF\work", false), list);
        Assert.Contains(("Profiles/x.default-release", true), list);
    }

    [Fact]
    public void Steam_library_folders_vdf_is_parsed()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                    "apps"
                    {
                        "228980"		"0"
                        "730"		"35000000000"
                    }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                    "apps"
                    {
                        "1086940"		"150000000000"
                    }
                }
            }
            """;
        var libs = SteamLibraries.ParseLibraryFolders(vdf);
        Assert.Equal(2, libs.Count);
        Assert.Equal(@"C:\Program Files (x86)\Steam", libs[0].Path);
        Assert.Contains("730", libs[0].Apps);
        Assert.Equal(@"D:\SteamLibrary", libs[1].Path);
        Assert.Contains("1086940", libs[1].Apps);
    }

    [Fact]
    public void Steam_orphans_are_only_uninstalled_numeric_app_ids()
    {
        var installed = new HashSet<string> { "730", "570" };
        var orphans = SteamLibraries.FindOrphans(["730", "440", "570", "temp", "0", "1086940"], installed);
        Assert.Equal(["1086940", "440"], orphans);
    }

    [Fact]
    public void Dism_component_store_report_is_parsed()
    {
        const string output = """
            Deployment Image Servicing and Management tool
            Version: 10.0.26100.1

            Image Version: 10.0.26100.4061

            [===========================100.0%==========================]

            Component Store (WinSxS) information:

            Windows Explorer Reported Size of Component Store : 9.12 GB

            Actual Size of Component Store : 8.85 GB

                Shared with Windows : 5.63 GB
                Backups and Disabled Features : 3.03 GB
                Cache and Temporary Data :  193.92 MB

            Date of Last Cleanup : 2025-05-01 10:12:42

            Number of Reclaimable Packages : 2
            Component Store Cleanup Recommended : Yes

            The operation completed successfully.
            """;
        var r = DismOutput.ParseAnalyze(output);
        Assert.True(r.CleanupRecommended);
        Assert.Equal("8.85 GB", r.ActualSize);
        Assert.Equal("2", r.Reclaimable);
        Assert.False(DismOutput.ParseAnalyze("Component Store Cleanup Recommended : No").CleanupRecommended);
        Assert.Null(DismOutput.ParseAnalyze("Error: 740").CleanupRecommended);
    }

    [Theory]
    [InlineData("[==========================100.0%==========================]", 100.0)]
    [InlineData("[=====                      23.4%                          ]", 23.4)]
    [InlineData("[===  7,5% ]", 7.5)]
    public void Dism_progress_is_parsed(string line, double expected) => Assert.Equal(expected, DismOutput.ParsePercent(line)!.Value, 1);

    [Fact]
    public void Dism_lines_without_percent_give_null() => Assert.Null(DismOutput.ParsePercent("Image Version: 10.0.26100.4061"));

    [Fact]
    public void Delivery_optimization_size_output_is_parsed()
    {
        Assert.Equal(new DeliveryOptimizationSize(true, 3051105143), SystemCleanup.ParseDeliveryOptimizationSize("SIZE=3051105143\r\n"));
        Assert.False(SystemCleanup.ParseDeliveryOptimizationSize("SIZE=NA").Available);
        Assert.Null(SystemCleanup.ParseDeliveryOptimizationSize("SIZE=ERR").Bytes);
        Assert.Null(SystemCleanup.ParseDeliveryOptimizationSize("garbage").Bytes);
    }

    [Fact]
    public void Temp_references_from_run_once_and_pending_renames_are_extracted()
    {
        const string temp = @"C:\Users\Test User\AppData\Local\Temp";
        var refs = TempReferenceScanner.TopLevelEntries(temp, @"C:\Users\TESTUS~1\AppData\Local\Temp",
        [
            "\"C:\\Users\\Test User\\AppData\\Local\\Temp\\{6F1A}\\setup.exe\" /resume",
            @"C:\Users\TESTUS~1\AppData\Local\Temp\inst42.exe /quiet",
            @"\??\C:\Users\Test User\AppData\Local\Temp\old.dll",
            @"C:\Windows\System32\cmd.exe /c echo unrelated",
        ]);
        Assert.Contains("{6F1A}", refs);
        Assert.Contains("inst42.exe", refs);
        Assert.Contains("old.dll", refs);
        Assert.DoesNotContain(refs, r => r.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(112, "112 файлов")]
    [InlineData(1004, "1 004 файла")]
    public void Russian_plurals(long n, string expected) =>
        Assert.Equal(expected, CleanFormat.Files(n).Replace('\u00A0', ' ').Replace('\u202F', ' '));

    [Fact]
    public void Byte_stat_is_split_into_value_and_unit()
    {
        var (value, unit) = CleanFormat.Split(5L * 1024 * 1024 * 1024);
        Assert.Equal("5", value);
        Assert.Equal("ГБ", unit);
    }
}
