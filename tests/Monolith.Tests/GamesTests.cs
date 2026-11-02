using System.IO;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.Modules.Games;
using Monolith.Modules.Games.Sources;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>Pure logic of the Games module: parsers, classification, path guards, boost rules. Nothing here touches the system.</summary>
public sealed class GamesTests
{
    // ───────────────────────────── VDF ─────────────────────────────

    private const string LibraryFoldersNew = """
        "libraryfolders"
        {
        	"0"
        	{
        		"path"		"C:\\Program Files (x86)\\Steam"
        		"label"		""
        		"contentid"		"5617325235486120434"
        		"totalsize"		"0"
        		"apps"
        		{
        			"228980"		"312436390"
        			"730"		"38123478124"
        		}
        	}
        	"1"
        	{
        		"path"		"D:\\SteamLibrary"
        		"label"		"Games"
        		"apps"
        		{
        			"570"		"61000000000"
        		}
        	}
        }
        """;

    private const string LibraryFoldersOld = """
        "LibraryFolders"
        {
        	"TimeNextStatsReport"		"1597862148"
        	"ContentStatsID"		"-5839210385014553386"
        	"1"		"E:\\Games\\Steam"
        }
        """;

    [Fact]
    public void Vdf_parses_nested_objects_and_escapes()
    {
        var root = VdfParser.Parse(LibraryFoldersNew);
        var lib = root["libraryfolders"];
        Assert.NotNull(lib);
        Assert.Equal(@"C:\Program Files (x86)\Steam", lib!["0"]!.Get("path"));
        Assert.Equal("Games", lib["1"]!.Get("label"));
        Assert.Equal("38123478124", lib["0"]!["apps"]!.Get("730"));
        Assert.Equal(2, lib.Children.Count);
    }

    [Fact]
    public void Vdf_keys_are_case_insensitive()
    {
        var root = VdfParser.Parse(LibraryFoldersNew);
        Assert.NotNull(root["LIBRARYFOLDERS"]);
        Assert.Equal(@"D:\SteamLibrary", root["libraryfolders"]!["1"]!.Get("PATH"));
    }

    [Fact]
    public void Vdf_skips_comments_and_conditionals()
    {
        const string text = """
            // comment at top
            "root"
            {
                "a"  "1"  [$WIN32]
                // inline comment
                "b"  "two words"
                "c"  { "d" "x" }
            }
            """;
        var root = VdfParser.Parse(text)["root"]!;
        Assert.Equal("1", root.Get("a"));
        Assert.Equal("two words", root.Get("b"));
        Assert.Equal("x", root["c"]!.Get("d"));
    }

    [Fact]
    public void Vdf_tolerates_malformed_input()
    {
        Assert.Empty(VdfParser.Parse(null).Children);
        Assert.Empty(VdfParser.Parse("").Children);
        var root = VdfParser.Parse("\"AppState\" { \"appid\" \"10\" \"name\" \"unterminated");
        Assert.Equal("10", root["AppState"]!.Get("appid"));
        Assert.Equal("unterminated", root["AppState"]!.Get("name"));
    }

    [Fact]
    public void Vdf_keeps_unknown_escapes_literally()
    {
        var root = VdfParser.Parse("\"k\" \"C:\\Games\\x\"");
        Assert.Equal(@"C:\Games\x", root.Get("k"));
    }

    // ───────────────────────────── Steam ─────────────────────────────

    [Fact]
    public void Steam_library_folders_new_format()
    {
        var libs = SteamSource.ParseLibraryFolders(LibraryFoldersNew, @"C:\Program Files (x86)\Steam");
        Assert.Equal([@"C:\Program Files (x86)\Steam\", @"D:\SteamLibrary\"], libs);
    }

    [Fact]
    public void Steam_library_folders_old_format_and_steam_path_always_included()
    {
        var libs = SteamSource.ParseLibraryFolders(LibraryFoldersOld, "C:/Program Files (x86)/Steam");
        Assert.Equal([@"C:\Program Files (x86)\Steam\", @"E:\Games\Steam\"], libs);
    }

    [Fact]
    public void Steam_library_folders_missing_file_returns_steam_path()
    {
        var libs = SteamSource.ParseLibraryFolders(null, @"C:\Steam\");
        Assert.Equal([@"C:\Steam\"], libs);
    }

    [Fact]
    public void Steam_app_manifest_is_parsed()
    {
        const string acf = """
            "AppState"
            {
            	"appid"		"570"
            	"Universe"		"1"
            	"name"		"Dota 2"
            	"StateFlags"		"4"
            	"installdir"		"dota 2 beta"
            	"LastUpdated"		"1729000000"
            	"SizeOnDisk"		"61203857408"
            }
            """;
        var m = SteamSource.ParseAppManifest(acf);
        Assert.NotNull(m);
        Assert.Equal(570, m!.AppId);
        Assert.Equal("Dota 2", m.Name);
        Assert.Equal("dota 2 beta", m.InstallDir);
        Assert.Equal(61203857408, m.SizeOnDisk);
        Assert.True(m.IsInstalled);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(6, true)]      // installed + update required
    [InlineData(1030, true)]   // updating
    [InlineData(2, false)]     // update required, not installed
    [InlineData(1026, false)]
    public void Steam_state_flags_bit_4_means_installed(int flags, bool installed)
    {
        var m = SteamSource.ParseAppManifest($"\"AppState\" {{ \"appid\" \"10\" \"name\" \"X\" \"installdir\" \"x\" \"StateFlags\" \"{flags}\" }}");
        Assert.Equal(installed, m!.IsInstalled);
    }

    [Fact]
    public void Steam_manifest_without_appid_or_installdir_is_rejected()
    {
        Assert.Null(SteamSource.ParseAppManifest("\"AppState\" { \"name\" \"X\" \"installdir\" \"x\" }"));
        Assert.Null(SteamSource.ParseAppManifest("\"AppState\" { \"appid\" \"5\" \"name\" \"X\" }"));
        Assert.Null(SteamSource.ParseAppManifest("garbage"));
    }

    [Fact]
    public void Steam_manifest_without_name_falls_back_to_installdir()
    {
        var m = SteamSource.ParseAppManifest("\"AppState\" { \"appid\" \"5\" \"installdir\" \"Some Game\" \"StateFlags\" \"4\" }");
        Assert.Equal("Some Game", m!.Name);
    }

    [Theory]
    [InlineData(228980, "Steamworks Common Redistributables", true)]
    [InlineData(1, "Proton 8.0", true)]
    [InlineData(1, "Proton Experimental", true)]
    [InlineData(1, "Proton EasyAntiCheat Runtime", true)]
    [InlineData(1628350, "Steam Linux Runtime 3.0 (sniper)", true)]
    [InlineData(250820, "SteamVR", true)]
    [InlineData(1, "Source SDK Base 2013 Multiplayer", true)]
    [InlineData(1, "Counter-Strike 2 Dedicated Server", true)]
    [InlineData(730, "Counter-Strike 2", false)]
    [InlineData(620, "Portal 2", false)]
    [InlineData(1, "Protonaut", false)]
    public void Steam_tools_and_redistributables_are_skipped(int appId, string name, bool tool) =>
        Assert.Equal(tool, SteamSource.IsToolOrRedistributable(appId, name));

    // ───────────────────────────── Epic ─────────────────────────────

    private const string EpicItem = """
        {
            "FormatVersion": 0,
            "bIsIncompleteInstall": false,
            "LaunchCommand": "",
            "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
            "ManifestLocation": "C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests",
            "bIsApplication": true,
            "bIsExecutable": true,
            "DisplayName": "Fortnite",
            "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
            "InstallSize": 31243589632,
            "CatalogNamespace": "fn",
            "CatalogItemId": "4fe75bbc5a674f4f9b356b5c90567da5",
            "AppName": "Fortnite",
            "MainGameAppName": "Fortnite",
        }
        """;

    [Fact]
    public void Epic_manifest_is_parsed()
    {
        var m = EpicSource.ParseManifest(EpicItem);
        Assert.NotNull(m);
        Assert.Equal("Fortnite", m!.DisplayName);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite", m.InstallLocation);
        Assert.Equal("FortniteGame/Binaries/Win64/FortniteLauncher.exe", m.LaunchExecutable);
        Assert.Equal(31243589632, m.InstallSize);
        Assert.False(m.IsIncompleteInstall);
        Assert.False(m.IsDlc);
    }

    [Fact]
    public void Epic_launch_uri_uses_namespace_item_and_app_name()
    {
        var m = EpicSource.ParseManifest(EpicItem)!;
        Assert.Equal("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true", m.LaunchUri);
    }

    [Fact]
    public void Epic_launch_uri_falls_back_to_app_name()
    {
        var m = EpicSource.ParseManifest("""{ "AppName": "Sugar", "DisplayName": "Rocket League", "InstallLocation": "D:\\RL", "LaunchExecutable": "rl.exe" }""")!;
        Assert.Equal("com.epicgames.launcher://apps/Sugar?action=launch&silent=true", m.LaunchUri);
    }

    [Fact]
    public void Epic_dlc_and_broken_manifests_are_recognised()
    {
        var dlc = EpicSource.ParseManifest("""{ "AppName": "DlcX", "MainGameAppName": "Base", "DisplayName": "DLC", "InstallLocation": "D:\\G" }""");
        Assert.True(dlc!.IsDlc);
        Assert.Null(EpicSource.ParseManifest("{ not json"));
        Assert.Null(EpicSource.ParseManifest("""{ "DisplayName": "No app name", "InstallLocation": "D:\\G" }"""));
        Assert.Null(EpicSource.ParseManifest("[]"));
        Assert.True(EpicSource.ParseManifest("""{ "AppName": "A", "InstallLocation": "D:\\G", "bIsIncompleteInstall": true }""")!.IsIncompleteInstall);
    }

    // ───────────────────────────── GOG ─────────────────────────────

    [Fact]
    public void Gog_primary_play_task_is_chosen()
    {
        const string info = """
            {
              "gameId": "1207664663",
              "name": "The Witcher 3: Wild Hunt",
              "playTasks": [
                { "category": "document", "name": "Manual", "path": "manual.pdf", "type": "FileTask" },
                { "category": "game", "isPrimary": true, "name": "The Witcher 3", "path": "bin/x64/witcher3.exe", "arguments": "-debugscripts", "workingDir": "bin/x64", "type": "FileTask" },
                { "link": "https://forums.gog.com", "name": "Forum", "type": "URLTask" }
              ]
            }
            """;
        var t = GogSource.ParsePrimaryPlayTask(info);
        Assert.NotNull(t);
        Assert.Equal(@"bin\x64\witcher3.exe", t!.Path);
        Assert.Equal("-debugscripts", t.Arguments);
        Assert.Equal(@"bin\x64", t.WorkingDir);
    }

    [Fact]
    public void Gog_without_primary_takes_first_file_task_and_ignores_urls()
    {
        const string info = """{ "playTasks": [ { "type": "URLTask", "link": "https://x" }, { "type": "FileTask", "path": "game.exe" } ] }""";
        Assert.Equal("game.exe", GogSource.ParsePrimaryPlayTask(info)!.Path);
        Assert.Null(GogSource.ParsePrimaryPlayTask("""{ "playTasks": [ { "type": "URLTask", "link": "https://x" } ] }"""));
        Assert.Null(GogSource.ParsePrimaryPlayTask("""{ "name": "no tasks" }"""));
        Assert.Null(GogSource.ParsePrimaryPlayTask("broken"));
    }

    // ───────────────────────────── EA / Battle.net / Riot ─────────────────────────────

    private static UninstallEntry Entry(string key, string name, string? publisher, string? uninstall = null, string? icon = null, bool system = false) =>
        new(key, name, publisher, @"C:\Games\X", uninstall, icon, 0, system);

    [Fact]
    public void Uninstall_entries_are_classified_by_launcher()
    {
        Assert.Equal(GamePlatform.Riot, LauncherSources.Classify(Entry("Riot Game valorant.live", "VALORANT", "Riot Games, Inc",
            "\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=valorant --uninstall-patchline=live")));
        Assert.Equal(GamePlatform.BattleNet, LauncherSources.Classify(Entry("Overwatch", "Overwatch", "Blizzard Entertainment")));
        Assert.Equal(GamePlatform.Ea, LauncherSources.Classify(Entry("{GUID}", "Apex Legends", "Electronic Arts")));
    }

    [Fact]
    public void Launchers_anticheat_steam_copies_and_others_are_not_games()
    {
        Assert.Null(LauncherSources.Classify(Entry("Battle.net", "Battle.net", "Blizzard Entertainment")));
        Assert.Null(LauncherSources.Classify(Entry("EA", "EA app", "Electronic Arts")));
        Assert.Null(LauncherSources.Classify(Entry("Riot Vanguard", "Riot Vanguard", "Riot Games, Inc", "C:\\Program Files\\Riot Vanguard\\installer.exe --quiet")));
        Assert.Null(LauncherSources.Classify(Entry("Steam App 1172470", "Apex Legends", "Electronic Arts", "\"C:\\Steam\\steam.exe\" steam://uninstall/1172470")));
        Assert.Null(LauncherSources.Classify(Entry("x", "Some Tool", "Electronic Arts", system: true)));
        Assert.Null(LauncherSources.Classify(Entry("x", "7-Zip", "Igor Pavlov")));
        Assert.Null(LauncherSources.Classify(Entry("x", "Riot thing", "Riot Games, Inc", "\"C:\\x\\uninstall.exe\"")));
    }

    [Fact]
    public void Riot_launch_command_is_derived_from_uninstall_string()
    {
        var r = LauncherSources.RiotLaunch("\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=league_of_legends --uninstall-patchline=live");
        Assert.NotNull(r);
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", r!.Value.Exe);
        Assert.Equal("--launch-product=league_of_legends --launch-patchline=live", r.Value.Arguments);
    }

    [Fact]
    public void Riot_launch_rejects_foreign_exe_client_itself_and_injection()
    {
        Assert.Null(LauncherSources.RiotLaunch("\"C:\\Evil\\other.exe\" --uninstall-product=valorant"));
        Assert.Null(LauncherSources.RiotLaunch("\"C:\\Riot Games\\Riot Client\\RiotClientServices.exe\" --uninstall-product=riot_client"));
        Assert.Null(LauncherSources.RiotLaunch(null));
        var r = LauncherSources.RiotLaunch("\"C:\\Riot\\RiotClientServices.exe\" --uninstall-product=valorant&calc.exe");
        Assert.Equal("--launch-product=valorant --launch-patchline=live", r!.Value.Arguments); // only the safe token survives
    }

    [Fact]
    public void Game_exe_from_display_icon_must_be_inside_install_dir_and_not_an_uninstaller()
    {
        var dir = GamePaths.NormalizeDir(@"C:\Games\Overwatch");
        Assert.Equal(@"C:\Games\Overwatch\Overwatch Launcher.exe",
            LauncherSources.GameExeFromEntry(Entry("k", "Overwatch", "Blizzard", icon: "\"C:\\Games\\Overwatch\\Overwatch Launcher.exe\""), dir));
        Assert.Null(LauncherSources.GameExeFromEntry(Entry("k", "Overwatch", "Blizzard", icon: @"C:\ProgramData\Battle.net\Agent\Blizzard Uninstaller.exe,0"), dir));
        Assert.Null(LauncherSources.GameExeFromEntry(Entry("k", "Game", "EA", icon: @"C:\Games\Overwatch\__Installer\Cleanup.exe"), dir));
        Assert.Null(LauncherSources.GameExeFromEntry(Entry("k", "Game", "EA", icon: @"C:\Games\Overwatch\unins000.exe"), dir));
        Assert.Null(LauncherSources.GameExeFromEntry(Entry("k", "Game", "EA", icon: @"C:\Games\Overwatch\icon.ico"), dir));
    }

    // ───────────────────────────── Paths ─────────────────────────────

    [Theory]
    [InlineData("C:/Games/Foo", @"C:\Games\Foo\")]
    [InlineData(@"C:\Games\Foo\\", @"C:\Games\Foo\")]
    [InlineData("\"D:\\SteamLibrary\"", @"D:\SteamLibrary\")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Paths_are_normalized(string? input, string expected) => Assert.Equal(expected, GamePaths.NormalizeDir(input));

    [Fact]
    public void Path_prefix_match_respects_folder_boundaries()
    {
        var dir = GamePaths.NormalizeDir(@"C:\Games\Foo");
        Assert.True(GamePaths.IsUnder(@"c:\games\foo\bin\game.exe", dir));
        Assert.False(GamePaths.IsUnder(@"C:\Games\FooBar\game.exe", dir));
        Assert.False(GamePaths.IsUnder(null, dir));
        Assert.False(GamePaths.IsUnder(@"C:\Games\Foo\x.exe", ""));
    }

    [Fact]
    public void Broad_folders_are_not_used_for_process_matching()
    {
        Assert.False(GamePaths.IsSpecificDir(@"C:\"));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows))));
        Assert.False(GamePaths.IsSpecificDir(""));
        Assert.True(GamePaths.IsSpecificDir(@"D:\SteamLibrary\steamapps\common\Portal 2\"));
    }

    [Theory]
    [InlineData("\"C:\\Games\\X\\game.exe\"", @"C:\Games\X\game.exe")]
    [InlineData(@"C:\Games\X\game.exe,0", @"C:\Games\X\game.exe")]
    [InlineData(@"C:\Games\X\game.exe,-101", @"C:\Games\X\game.exe")]
    [InlineData("C:/Games/X/game.exe", @"C:\Games\X\game.exe")]
    [InlineData(@"C:\Games\X\game.ico", null)]
    [InlineData("", null)]
    public void Exe_is_extracted_from_icon_values(string value, string? expected) =>
        Assert.Equal(expected, GamePaths.ExeFromIconValue(value));

    // ───────────────────────────── Text ─────────────────────────────

    [Theory]
    [InlineData("Counter-Strike 2", "CS")]
    [InlineData("Dota 2", "D2")]
    [InlineData("The Witcher 3: Wild Hunt", "W3")]
    [InlineData("Fortnite", "FO")]
    [InlineData("VALORANT", "VA")]
    [InlineData("Ведьмак 3", "В3")]
    [InlineData("X", "X")]
    [InlineData("  ", "?")]
    [InlineData("!!!", "?")]
    [InlineData("The", "TH")]
    public void Monogram_initials(string name, string expected) => Assert.Equal(expected, GameText.Initials(name));

    [Theory]
    [InlineData(1, "игра")]
    [InlineData(2, "игры")]
    [InlineData(5, "игр")]
    [InlineData(11, "игр")]
    [InlineData(21, "игра")]
    [InlineData(104, "игры")]
    [InlineData(0, "игр")]
    public void Russian_plural(int n, string expected) => Assert.Equal(expected, GameText.Plural(n, "игра", "игры", "игр"));

    // ───────────────────────────── Library ─────────────────────────────

    private static GameEntry Game(string id, string name, GamePlatform p, string dir, string? exe = null) =>
        new() { Id = id, Name = name, Platform = p, InstallDir = dir, ExePath = exe };

    [Fact]
    public void Same_folder_from_two_launchers_keeps_the_higher_priority_one()
    {
        var list = GameLibrary.Deduplicate(
        [
            Game("ea:apex", "Apex Legends (EA)", GamePlatform.Ea, @"D:\SteamLibrary\steamapps\common\Apex Legends"),
            Game("steam:1172470", "Apex Legends", GamePlatform.Steam, @"D:/SteamLibrary/steamapps/common/Apex Legends/"),
            Game("gog:1", "Witcher 3", GamePlatform.Gog, @"C:\GOG Games\Witcher 3"),
            Game("gog:1", "Witcher 3 duplicate id", GamePlatform.Gog, @"C:\GOG Games\Other"),
        ]);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, g => g.Id == "steam:1172470");
        Assert.DoesNotContain(list, g => g.Id == "ea:apex");
        Assert.Equal(["Apex Legends", "Witcher 3"], list.Select(g => g.Name));
    }

    [Fact]
    public void User_added_games_are_kept_even_inside_a_store_folder()
    {
        var list = GameLibrary.Deduplicate(
        [
            Game("steam:1", "Game", GamePlatform.Steam, @"D:\G"),
            Game(@"custom:d:\g\tool.exe", "Mod tool", GamePlatform.Custom, @"D:\G", @"D:\G\tool.exe"),
        ]);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void Missing_custom_exe_is_marked_unavailable()
    {
        var e = GameLibrary.CustomEntry(@"Z:\definitely\missing\game.exe");
        Assert.False(e.IsAvailable);
        Assert.Equal("game", e.Name);
        Assert.Equal(GamePlatform.Custom, e.Platform);
        Assert.Equal(@"Z:\definitely\missing\", e.InstallDir);
    }

    [Theory]
    [InlineData("steam://rungameid/570", true)]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", true)]
    [InlineData("uplay://launch/635/0", true)]
    [InlineData("steam://uninstall/570", false)]
    [InlineData("https://example.com", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData(null, false)]
    public void Only_known_launch_urls_are_opened(string? uri, bool allowed) => Assert.Equal(allowed, GameLauncher.IsAllowedUri(uri));

    // ───────────────────────────── Processes ─────────────────────────────

    [Theory]
    [InlineData("Discord", "Discord")]
    [InlineData("discord.EXE", "discord")]
    [InlineData(@"C:\Users\x\AppData\Local\Discord\app-1.0\Discord.exe", "Discord")]
    [InlineData("  \"chrome.exe\" ", "chrome")]
    [InlineData("", "")]
    public void Process_names_are_normalized(string input, string expected) => Assert.Equal(expected, UserProcesses.NormalizeName(input));

    [Theory]
    [InlineData("svchost.exe")]
    [InlineData("explorer")]
    [InlineData("csrss")]
    [InlineData("lsass")]
    [InlineData("dwm")]
    [InlineData("audiodg")]
    [InlineData("MsMpEng")]
    [InlineData("EasyAntiCheat_EOS")]
    [InlineData("BEService_x64")]
    [InlineData("vgc")]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("RiotClientServices")]
    [InlineData("GameBar")]
    [InlineData("Lycoris")]
    [InlineData("")]
    public void System_security_anticheat_and_launcher_processes_are_never_closable(string name) =>
        Assert.True(UserProcesses.IsProtectedName(name));

    [Theory]
    [InlineData("Discord")]
    [InlineData("chrome")]
    [InlineData("Telegram")]
    [InlineData("OneDrive")]
    [InlineData("Spotify")]
    public void Ordinary_apps_may_be_closed(string name) => Assert.False(UserProcesses.IsProtectedName(name));

    [Theory]
    [InlineData("UnityCrashHandler64", true)]
    [InlineData("CrashReportClient", true)]
    [InlineData("EasyAntiCheat", true)]
    [InlineData("cs2", false)]
    [InlineData("VALORANT-Win64-Shipping", false)]
    public void Game_helpers_are_not_taken_for_the_game(string name, bool helper) => Assert.Equal(helper, GameProcessFinder.IsHelper(name));

    // Boost power rules (plan / Windows 11 power mode decisions and their texts) live in BoostPolicyTests.cs.

    // ───────────────────────────── Readiness ─────────────────────────────

    [Fact]
    public void Game_mode_absent_means_on()
    {
        Assert.Equal(ReadinessState.Ok, GamingReadiness.GameModeFromValue(null).State);
        Assert.Equal(ReadinessState.Ok, GamingReadiness.GameModeFromValue(1).State);
        Assert.Equal(ReadinessState.Attention, GamingReadiness.GameModeFromValue(0).State);
    }

    [Fact]
    public void Background_recording_only_flagged_when_on()
    {
        Assert.Equal(ReadinessState.Ok, GamingReadiness.BackgroundRecordingFromValue(null).State);
        Assert.Equal(ReadinessState.Ok, GamingReadiness.BackgroundRecordingFromValue(0).State);
        Assert.Equal(ReadinessState.Attention, GamingReadiness.BackgroundRecordingFromValue(1).State);
    }

    [Fact]
    public void Gpu_driver_age_check()
    {
        var now = new DateTime(2026, 10, 26);
        var fresh = GamingReadiness.GpuFromInfo("NVIDIA GeForce RTX 5070 Ti", "32.0.16.1088", new DateTime(2026, 7, 22), now);
        Assert.Equal(ReadinessState.Ok, fresh.State);
        Assert.Equal("GeForce RTX 5070 Ti", fresh.Value);
        Assert.Equal("Драйвер 32.0.16.1088 · 22.07.2026", fresh.Detail);

        var old = GamingReadiness.GpuFromInfo("AMD Radeon RX 6600", "31.0.1", new DateTime(2025, 1, 10), now);
        Assert.Equal(ReadinessState.Attention, old.State);
        Assert.Contains("«Драйверы»", old.Hint);

        Assert.Equal(ReadinessState.Info, GamingReadiness.GpuFromInfo("GPU", "", null, now).State);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "GeForce RTX 5070 Ti")]
    [InlineData("AMD Radeon RX 7800 XT", "Radeon RX 7800 XT")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Arc A770 Graphics")]
    [InlineData("Microsoft Basic Display Adapter", "Microsoft Basic Display Adapter")]
    public void Gpu_names_are_shortened(string name, string expected) => Assert.Equal(expected, GamingReadiness.ShortGpuName(name));

    [Fact]
    public void Readiness_score_ignores_info_checks()
    {
        var (passed, total) = GamingReadiness.Score(
        [
            new ReadinessCheck("A", "x", ReadinessState.Ok, ""),
            new ReadinessCheck("B", "x", ReadinessState.Attention, ""),
            new ReadinessCheck("C", "x", ReadinessState.Info, ""),
            new ReadinessCheck("D", "x", ReadinessState.Ok, ""),
        ]);
        Assert.Equal(2, passed);
        Assert.Equal(3, total);
    }

    [Fact]
    public void Readiness_summary_is_one_line_pointing_to_the_dashboard() =>
        Assert.Equal("Готовность к играм: 2/3 — на Панели", GamingReadiness.SummaryLine(2, 3));

    // ───────────────────────────── Per-game GPU preference ─────────────────────────────

    private static GpuInfo Gpu(string name, GpuVendor vendor) => new(name, vendor, "1.0", null, 0, "");

    [Fact]
    public void Gpu_switch_is_shown_only_on_hybrid_pcs_with_windows_10_1803_or_later()
    {
        var hybrid = new[] { Gpu("AMD Radeon(TM) Graphics", GpuVendor.Amd), Gpu("NVIDIA GeForce RTX 4060 Laptop GPU", GpuVendor.Nvidia) };
        Assert.True(GpuPreference.IsHybrid(hybrid, 22631));
        Assert.True(GpuPreference.IsHybrid(hybrid, GpuPreference.MinBuild));
        Assert.False(GpuPreference.IsHybrid(hybrid, GpuPreference.MinBuild - 1));
        Assert.True(GpuPreference.IsHybrid([Gpu("Intel(R) UHD Graphics", GpuVendor.Intel), Gpu("Intel(R) Arc(TM) A370M", GpuVendor.Intel)], 22631));
        // One real GPU: nothing to choose between.
        Assert.False(GpuPreference.IsHybrid([Gpu("NVIDIA GeForce RTX 4070", GpuVendor.Nvidia)], 22631));
        Assert.False(GpuPreference.IsHybrid([], 22631));
        // Virtual / remote display adapters are not a second GPU.
        Assert.False(GpuPreference.IsHybrid([Gpu("NVIDIA GeForce RTX 4070", GpuVendor.Nvidia), Gpu("Microsoft Remote Display Adapter", GpuVendor.Microsoft)], 22631));
        Assert.False(GpuPreference.IsHybrid([Gpu("Intel(R) UHD Graphics 770", GpuVendor.Intel), Gpu("Parsec Virtual Display Adapter", GpuVendor.Other)], 22631));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("GpuPreference=2;", 2)]
    [InlineData("SwapEffectUpgradeEnable=1;GpuPreference=1;", 1)]
    [InlineData(" gpupreference = 0 ;AutoHDREnable=1;", 0)]
    [InlineData("AutoHDREnable=2097;", null)]
    [InlineData("GpuPreference=x;", null)]
    [InlineData("GpuPreference=2", 2)]
    public void Gpu_preference_token_is_parsed(string? data, int? expected) => Assert.Equal(expected, GpuPreference.Parse(data));

    [Theory]
    [InlineData(null, 2, "GpuPreference=2;")]
    [InlineData("", 2, "GpuPreference=2;")]
    [InlineData("GpuPreference=0;", 2, "GpuPreference=2;")]
    // Other tokens are kept exactly, in their place; the preference token is replaced in place or appended.
    [InlineData("SwapEffectUpgradeEnable=1;", 2, "SwapEffectUpgradeEnable=1;GpuPreference=2;")]
    [InlineData("GpuPreference=1;SwapEffectUpgradeEnable=1;", 2, "GpuPreference=2;SwapEffectUpgradeEnable=1;")]
    [InlineData("AutoHDREnable=2097;GpuPreference=2;SwapEffectUpgradeEnable=1;", 0, "AutoHDREnable=2097;GpuPreference=0;SwapEffectUpgradeEnable=1;")]
    [InlineData("AutoHDREnable=2097", 2, "AutoHDREnable=2097;GpuPreference=2;")]
    // A duplicate token is dropped (Windows reads one), empty segments are not kept.
    [InlineData("GpuPreference=0;;AutoHDREnable=1;GpuPreference=1;", 2, "GpuPreference=2;AutoHDREnable=1;")]
    public void Gpu_preference_merge_changes_only_the_preference_token(string? existing, int preference, string expected) =>
        Assert.Equal(expected, GpuPreference.Merge(existing, preference));

    [Fact]
    public void Gpu_preference_merge_round_trips()
    {
        var merged = GpuPreference.Merge("SwapEffectUpgradeEnable=1;AutoHDREnable=1;", GpuPreference.HighPerformance);
        Assert.Equal(GpuPreference.HighPerformance, GpuPreference.Parse(merged));
        Assert.Contains("SwapEffectUpgradeEnable=1;", merged);
        Assert.Contains("AutoHDREnable=1;", merged);
        Assert.Equal(merged, GpuPreference.Merge(merged, GpuPreference.HighPerformance)); // idempotent
    }

    private static RegistryValueSnapshot Original(string? data) => new()
    {
        Hive = RegHive.HKCU, Path = GpuPreference.KeyPath, Name = @"D:\Games\X\x.exe",
        KeyExisted = data is not null, ValueExisted = data is not null, Kind = Microsoft.Win32.RegistryValueKind.String, Data = data,
    };

    [Fact]
    public void Gpu_switch_reverts_monolith_records_to_the_exact_original_and_writes_otherwise()
    {
        // Already in the wanted state: nothing to do.
        Assert.Equal(GpuChange.None, GpuPreference.Decide(true, 2, null));
        Assert.Equal(GpuChange.None, GpuPreference.Decide(false, 1, null));
        Assert.Equal(GpuChange.None, GpuPreference.Decide(false, null, null));
        // Nothing journaled: write (and journal) the merged value.
        Assert.Equal(GpuChange.Write, GpuPreference.Decide(true, null, null));
        Assert.Equal(GpuChange.Write, GpuPreference.Decide(false, 2, null)); // chosen in Windows Settings → «let Windows decide»
        // Turning Lycoris's own change back = revert its record: the previous string (or absence) comes back exactly.
        Assert.Equal(GpuChange.Revert, GpuPreference.Decide(false, 2, Original(null)));
        Assert.Equal(GpuChange.Revert, GpuPreference.Decide(false, 2, Original("GpuPreference=1;SwapEffectUpgradeEnable=1;")));
        Assert.Equal(GpuChange.Revert, GpuPreference.Decide(true, 0, Original("GpuPreference=2;")));
        // Changed outside Lycoris after its record: writing again keeps the record's true original (merge by target).
        Assert.Equal(GpuChange.Write, GpuPreference.Decide(true, 0, Original(null)));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Game\Game.exe", true)]
    [InlineData(@"D:\Games\[GOG] Witcher\bin\x64\witcher3.EXE", true)]
    [InlineData(@"game.exe", false)]
    [InlineData(@"\\server\share\game.exe", false)]
    [InlineData(@"C:\Games\game.exe:stream.exe", false)]
    [InlineData(@"C:\Games\..\Windows\notepad.exe", false)]
    [InlineData(@"C:\Games\\game.exe", false)]
    [InlineData(@"C:/Games/game.exe", false)]
    [InlineData(@"C:\Games\game.txt", false)]
    [InlineData("C:\\Games\\ga\nme.exe", false)]
    [InlineData(@"C:\Games\"" game.exe", false)]
    [InlineData(null, false)]
    public void Gpu_preference_names_only_absolute_local_exe_paths(string? path, bool valid) =>
        Assert.Equal(valid, GpuPreference.IsValidExePath(path));

    [Fact]
    public void Gpu_values_belong_to_a_game_by_its_exe_or_its_own_folder()
    {
        const string dir = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\";
        Assert.True(GpuPreference.BelongsTo(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe", null, dir, true));
        Assert.True(GpuPreference.BelongsTo(@"d:\steamlibrary\STEAMAPPS\common\cyberpunk 2077\bin\x64\cyberpunk2077.exe", null, dir, true));
        Assert.False(GpuPreference.BelongsTo(@"D:\SteamLibrary\steamapps\common\Cyberpunk 2077 Tools\x.exe", null, dir, true));
        Assert.False(GpuPreference.BelongsTo("DirectXUserGlobalSettings", null, dir, true));
        // A shared folder ("D:\Games\") identifies no single game: only the exact exe counts.
        Assert.False(GpuPreference.BelongsTo(@"D:\Games\other.exe", null, @"D:\Games\", false));
        Assert.True(GpuPreference.BelongsTo(@"D:\Games\mine.exe", @"D:\Games\mine.exe", @"D:\Games\", false));
    }

    [Fact]
    public void Gpu_preference_targets_only_exes_known_to_be_the_game()
    {
        const string dir = @"D:\Games\X\";
        Assert.Null(GpuPreference.KnownExe(Game("steam:1", "X", GamePlatform.Steam, dir), dir));
        Assert.Null(GpuPreference.KnownExe(Game("epic:x", "X", GamePlatform.Epic, dir), dir));
        // Riot's exe is the Riot Client; EA / Battle.net entries are often launcher stubs.
        Assert.Null(GpuPreference.KnownExe(Game("riot:x", "X", GamePlatform.Riot, dir, @"C:\Riot Games\Riot Client\RiotClientServices.exe"), dir));
        Assert.Null(GpuPreference.KnownExe(Game("ea:x", "X", GamePlatform.Ea, dir, @"D:\Games\X\X Launcher.exe"), dir));
        Assert.Equal(@"D:\Games\X\bin\x.exe", GpuPreference.KnownExe(Game("gog:1", "X", GamePlatform.Gog, dir, @"D:\Games\X\bin\x.exe"), dir));
        Assert.Null(GpuPreference.KnownExe(Game("gog:2", "X", GamePlatform.Gog, dir, @"C:\Other\x.exe"), dir));
        Assert.Equal(@"D:\Games\X\x.exe", GpuPreference.KnownExe(Game("custom:x", "X", GamePlatform.Custom, dir, @"D:\Games\X\x.exe"), dir));
    }

    [Fact]
    public void Gpu_exe_guess_prefers_the_largest_real_game_binary()
    {
        var guess = GpuPreference.GuessExe(
        [
            (@"D:\G\X\UnityCrashHandler64.exe", 1_500_000),
            (@"D:\G\X\X Launcher.exe", 90_000_000),
            (@"D:\G\X\_CommonRedist\vcredist_x64.exe", 25_000_000),
            (@"D:\G\X\Engine\Binaries\Win64\X-Win64-Shipping.exe", 80_000_000),
            (@"D:\G\X\X.exe", 300_000),
            (@"D:\G\X\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", 40_000_000),
        ]);
        Assert.Equal(@"D:\G\X\Engine\Binaries\Win64\X-Win64-Shipping.exe", guess);
        Assert.Null(GpuPreference.GuessExe([]));
        Assert.Null(GpuPreference.GuessExe([(@"D:\G\X\unins000.exe", 1000)]));
    }

    [Fact]
    public void Gpu_journal_target_is_the_one_change_session_writes() =>
        Assert.Equal(@"reg:HKCU\Software\Microsoft\DirectX\UserGpuPreferences\D:\G\x.exe", GpuPreference.JournalTarget(@"D:\G\x.exe"));

    [Fact]
    public void Gpu_card_tooltip_states_the_current_choice_and_why_it_is_read_only()
    {
        var high = new[] { new GpuPreferenceValue(@"D:\G\X\x.exe", "GpuPreference=2;") };
        var saving = new[] { new GpuPreferenceValue(@"D:\G\X\x.exe", "GpuPreference=1;") };
        Assert.StartsWith("Включено: Windows запускает x.exe на дискретной видеокарте (GeForce RTX 4060)", GpuPreference.CardTooltip(high, "GeForce RTX 4060", true, true));
        Assert.Contains("вернётся ровно то значение", GpuPreference.CardTooltip(high, null, true, true));
        Assert.Contains("энергосбережение", GpuPreference.CardTooltip(saving, null, true, true));
        Assert.Contains("Сейчас видеокарту выбирает Windows", GpuPreference.CardTooltip([], null, true, true));
        Assert.Contains("Только просмотр", GpuPreference.CardTooltip([], null, false, true));
        Assert.Contains("от другой учётной записи", GpuPreference.CardTooltip([], null, true, false));
    }

    // ───────────────────────────── Leftovers of a crashed session ─────────────────────────────

    private static ChangeRecord Plan(string source, string title, string from, string to) => new()
    {
        Source = source, Title = title, Kind = ChangeKind.PowerScheme, Target = "power:active", OldValue = from, NewValue = to,
    };

    private static ChangeRecord Session(string from = "Сбалансированная", string to = "Высокая производительность") =>
        Plan(GameBoostService.Source, "Игровой сеанс: план питания", from, to);

    private static ChangeRecord LaterTweak(string from = "Высокая производительность") =>
        Plan("tweak:power-plan-balanced", "Сбалансированная схема питания", from, "Сбалансированная");

    /// <summary>The newer-layer lookup of the real journal, over a fixed record list (journal order).</summary>
    private static Func<ChangeRecord, ChangeRecord?> Layers(params ChangeRecord[] journal) =>
        r => ChangeJournal.NewerLayer(journal, r);

    [Fact]
    public void Stale_session_texts_promise_the_original_only_when_nothing_newer_holds_the_plan()
    {
        var session = Session();
        var alone = Layers(session);
        Assert.Equal(["Игровой сеанс: план питания: Высокая производительность → Сбалансированная"], GameBoostService.StaleLines([session], alone));
        Assert.Contains("Всё ещё действует: план питания — «Высокая производительность» вместо «Сбалансированная».",
            GameBoostService.StaleBanner([session], alone));
        Assert.EndsWith("Вернуть исходные значения?", GameBoostService.StalePrompt([session], alone));
        Assert.False(GameBoostService.AllKept([session], alone));
        Assert.Equal("", GameBoostService.StaleBanner([], alone));
    }

    [Fact]
    public void Stale_session_texts_say_that_a_plan_chosen_later_stays()
    {
        // ПОЗЖЕ after the crash, then «Сбалансированная схема питания» in Optimize: a newer layer holds the plan.
        var session = Session();
        var tweak = LaterTweak();
        var layers = Layers(session, tweak);

        Assert.Equal(["Игровой сеанс: план питания: останется «Сбалансированная» — его позже выбрал «Сбалансированная схема питания»"],
            GameBoostService.StaleLines([session], layers));
        var banner = GameBoostService.StaleBanner([session], layers);
        Assert.DoesNotContain("Всё ещё действует", banner);
        Assert.DoesNotContain("вместо", banner);
        Assert.Contains("План питания: останется «Сбалансированная» — его позже выбрал «Сбалансированная схема питания».", banner);
        Assert.EndsWith("Возврат только закроет запись сеанса, система не изменится.", banner);
        Assert.EndsWith("Закрыть запись сеанса?", GameBoostService.StalePrompt([session], layers));
        Assert.True(GameBoostService.AllKept([session], layers));
    }

    [Fact]
    public void Stale_session_records_undone_together_do_not_keep_each_other()
    {
        // Two leftover sessions: the newer one is undone too, so the older one's original comes back.
        var first = Session();
        var second = Session(from: "Экономия энергии");
        Assert.Null(GameBoostService.Keeper(first, [first, second], Layers(first, second)));
        Assert.False(GameBoostService.AllKept([first, second], Layers(first, second)));

        // A tweak between them stays: the older session hands its original over to it, the newer one comes back.
        var tweak = LaterTweak();
        var layers = Layers(first, tweak, second);
        Assert.Same(tweak, GameBoostService.Keeper(first, [first, second], layers));
        Assert.Null(GameBoostService.Keeper(second, [first, second], layers));
        Assert.Contains("Часть временных изменений всё ещё действует", GameBoostService.StalePrompt([first, second], layers));
        Assert.Contains("Всё ещё действует: план питания — «Высокая производительность» вместо «Экономия энергии».",
            GameBoostService.StaleBanner([first, second], layers));
    }

    [Fact]
    public void Stale_session_toast_counts_only_what_really_came_back()
    {
        Assert.Equal("Исходные настройки возвращены (изменений: 1).", GameBoostService.StaleRevertedText(new RevertResult(1, 0, [])));
        Assert.Equal("Запись игрового сеанса закрыта, система не изменена: настройку питания задаёт более позднее изменение, оно остаётся.",
            GameBoostService.StaleRevertedText(new RevertResult(1, 0, []) { HandedOver = 1 }));
        Assert.StartsWith("Исходные настройки возвращены (изменений: 1); настройку питания",
            GameBoostService.StaleRevertedText(new RevertResult(2, 0, []) { HandedOver = 1 }));
    }

    [Fact]
    public void Session_end_names_the_plan_chosen_during_the_session()
    {
        var tweak = LaterTweak();
        Assert.Null(GameBoostService.KeptPlanNote(new RevertResult(1, 0, []), tweak));
        Assert.Null(GameBoostService.KeptPlanNote(new RevertResult(1, 0, []) { HandedOver = 1 }, null));
        Assert.Equal("План питания остаётся «Сбалансированная» — его выбрали во время сеанса («Сбалансированная схема питания»).",
            GameBoostService.KeptPlanNote(new RevertResult(1, 0, []) { HandedOver = 1 }, tweak));

        // The same note for a power mode chosen in Optimize while the session held the mode.
        var modeTweak = new ChangeRecord
        {
            Source = "tweak:power-mode-best-efficiency-ac", Title = "Режим питания «Максимальная эффективность» от сети", Kind = ChangeKind.Command,
            Target = "cmd:" + GameBoostService.PowerModeTarget, OldValue = "от сети: максимальная производительность", NewValue = "от сети: максимальная эффективность",
        };
        Assert.Equal("Режим питания остаётся «от сети: максимальная эффективность» — его выбрали во время сеанса («Режим питания «Максимальная эффективность» от сети»).",
            GameBoostService.KeptPlanNote(new RevertResult(1, 0, []) { HandedOver = 1 }, modeTweak));
    }

    // ───────────────────────────── Power mode record (Windows 11, AC only) ─────────────────────────────

    /// <summary>The service's own JournalTrust probe: the record exactly as ChangeSession.RunCommandAsync stores it.</summary>
    private static ChangeRecord PowerModeRecord(Guid current) => GameBoostService.PowerModeRecord(current);

    [Fact]
    public void Game_session_power_mode_probe_is_the_record_the_session_writes()
    {
        var c = GameBoostService.PowerModeCommand(TweakCatalog.PowerModeBalanced);
        var r = PowerModeRecord(TweakCatalog.PowerModeBalanced);
        Assert.Equal(GameBoostService.Source, r.Source);
        Assert.Equal(ChangeKind.Command, r.Kind);
        Assert.Equal("cmd:" + c.Target, r.Target);
        Assert.Equal(c.Title, r.Title);
        Assert.Equal((c.OldValue, c.NewValue, c.RevertFile, c.RevertArgs), (r.OldValue, r.NewValue, r.RevertFile, r.RevertArgs));
        // Machine-wide: no account on the record (the journal refuses a SID on a system-wide change).
        Assert.Null(r.UserSid);
        Assert.False(r.IsPerUser);
    }

    [Fact]
    public void Game_session_power_mode_command_is_the_catalog_command_with_an_exact_undo()
    {
        foreach (var current in new[] { TweakCatalog.PowerModeBalanced, TweakCatalog.PowerModeBestEfficiency })
        {
            var c = GameBoostService.PowerModeCommand(current);
            Assert.Equal("powermode:ac", c.Target);
            Assert.Equal(TweakCatalog.PowerShellExe, c.File);
            Assert.Equal(TweakCatalog.PowerShellExe, c.RevertFile);
            // Apply sets Best performance; undo is the very script the Optimize power-mode items record for the original mode.
            Assert.Equal(TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBestPerformance)), c.Args);
            Assert.Equal(TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(current)), c.RevertArgs);
            Assert.Contains($"[Guid]'{current:D}'", TweakCatalog.DecodePowerShellArgs(c.RevertArgs));
            Assert.Equal($"от сети: {TweakCatalog.DescribePowerMode(current)}", c.OldValue);
            Assert.Equal("от сети: максимальная производительность", c.NewValue);
            Assert.Equal("Игровой сеанс: режим питания", c.Title);
        }
        // Only the three documented modes can be the original: anything else is refused before any change.
        Assert.Throws<ArgumentException>(() => GameBoostService.PowerModeCommand(Guid.NewGuid()));
        // The record is the same shape as the Optimize items', so the stale-session texts work on it.
        var r = PowerModeRecord(TweakCatalog.PowerModeBalanced);
        Assert.Equal(["Игровой сеанс: режим питания: от сети: максимальная производительность → от сети: сбалансированный"],
            GameBoostService.StaleLines([r], _ => null));
    }

    /// <summary>
    /// JournalTrust rule (c): a Command record from source "gameboost" is accepted for the target powermode:ac only, with the
    /// undo rebuilt from one of the three documented GUIDs — the record built here through the real catalog code. The same
    /// check is the probe DecidePower and SwitchPowerModeAsync run before promising or writing the mode.
    /// </summary>
    [Fact]
    public void Game_session_power_mode_record_is_trusted_by_the_journal()
    {
        foreach (var current in new[] { TweakCatalog.PowerModeBalanced, TweakCatalog.PowerModeBestEfficiency, TweakCatalog.PowerModeBestPerformance })
        {
            Assert.Null(JournalTrust.Check(PowerModeRecord(current)));
            Assert.Null(GameBoostService.PowerModeUntrusted(current));
        }
        // The probe itself never builds a record for an undocumented original (the decision skips it earlier).
        Assert.Throws<ArgumentException>(() => GameBoostService.PowerModeUntrusted(Guid.NewGuid()));

        // Tampered undo (an extra command) and any other command target from this source stay refused.
        var tampered = PowerModeRecord(TweakCatalog.PowerModeBalanced);
        tampered.RevertArgs = TweakCatalog.PowerShellArgs(TweakCatalog.PowerModeScript(TweakCatalog.PowerModeBalanced) + ";Start-Process calc");
        Assert.NotNull(JournalTrust.Check(tampered));
        var other = new ChangeRecord
        {
            Source = GameBoostService.Source, Title = "t", Kind = ChangeKind.Command, Target = "cmd:tcp-autotuning",
            RevertFile = ProcessRunner.Sys("netsh.exe"), RevertArgs = TweakCatalog.TcpAutotuningArgs(TweakCatalog.TcpAutotuningLevels[0]),
        };
        Assert.NotNull(JournalTrust.Check(other));
    }
}

/// <summary>
/// The per-game GPU switch against a redirected registry (HKCU\Software\MONOLITH-Tests\Redirect-*), never the real
/// settings: on → off must put back exactly what was there, and one game's undo must not touch another game.
/// </summary>
[Collection("Registry")]
public sealed class GpuPreferenceRegistryTests : IDisposable
{
    private const string GameA = @"D:\Games\Hybrid Test A\bin\GameA.exe";
    private const string GameB = @"D:\Games\Hybrid Test B\GameB.exe";
    private readonly string _root = $@"Software\MONOLITH-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"monolith-gputest-{Guid.NewGuid():N}.json");

    public GpuPreferenceRegistryTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        // The DirectX key exists on every PC that ran a game; UserGpuPreferences itself may not.
        RegistryHelper.Write(RegHive.HKCU, GpuPreference.ParentPath, "Sentinel", RegistryValueKind.String, "keep");
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    private static string? Read(string exe) => RegistryHelper.ReadString(RegHive.HKCU, GpuPreference.KeyPath, exe);

    private async Task Set(string exe, bool high)
    {
        var outcome = await GpuPreferenceStore.SetAsync([exe], high, "Test", new ChangeJournal(_journalPath));
        Assert.True(outcome.Success, string.Join("; ", outcome.Errors));
    }

    [Fact]
    public async Task On_then_off_removes_the_value_and_the_key_monolith_created()
    {
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, GpuPreference.KeyPath));
        await Set(GameA, true);
        Assert.Equal("GpuPreference=2;", Read(GameA));

        await Set(GameA, false);
        Assert.Null(Read(GameA));
        Assert.False(RegistryHelper.KeyExists(RegHive.HKCU, GpuPreference.KeyPath));
        Assert.Equal("keep", RegistryHelper.ReadString(RegHive.HKCU, GpuPreference.ParentPath, "Sentinel"));
        Assert.Empty(new ChangeJournal(_journalPath).ActiveRecords);
    }

    [Fact]
    public async Task On_then_off_restores_the_exact_previous_string_with_its_other_tokens()
    {
        const string before = "SwapEffectUpgradeEnable=1;GpuPreference=1;AutoHDREnable=2097;";
        RegistryHelper.Write(RegHive.HKCU, GpuPreference.KeyPath, GameA, RegistryValueKind.String, before);

        await Set(GameA, true);
        Assert.Equal("SwapEffectUpgradeEnable=1;GpuPreference=2;AutoHDREnable=2097;", Read(GameA));

        await Set(GameA, false);
        Assert.Equal(before, Read(GameA));
    }

    [Fact]
    public async Task Undoing_one_game_leaves_the_other_game_alone()
    {
        await Set(GameA, true);
        await Set(GameB, true);

        await Set(GameA, false);
        Assert.Null(Read(GameA));
        Assert.Equal("GpuPreference=2;", Read(GameB));
        var active = new ChangeJournal(_journalPath).ActiveRecords;
        Assert.Single(active);
        Assert.Equal(GpuPreference.JournalTarget(GameB), active[0].Target);
    }

    [Fact]
    public async Task A_choice_made_in_windows_is_changed_through_the_journal_and_comes_back_on_revert()
    {
        RegistryHelper.Write(RegHive.HKCU, GpuPreference.KeyPath, GameA, RegistryValueKind.String, "GpuPreference=2;");

        await Set(GameA, false);
        Assert.Equal("GpuPreference=0;", Read(GameA));

        var result = await new ChangeJournal(_journalPath).RevertAllAsync();
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal("GpuPreference=2;", Read(GameA));
    }
}
