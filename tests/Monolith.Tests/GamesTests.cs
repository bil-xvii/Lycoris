using System.IO;
using Monolith.Modules.Games;
using Monolith.Modules.Games.Sources;

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

    [Fact]
    public void User_folders_full_of_unrelated_programs_are_not_used_for_process_matching()
    {
        // A launched game has no PID: matching every process under Downloads or AppData would take other apps for the game.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Path.Combine(profile, "Downloads"))));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))));
        Assert.False(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Path.GetTempPath())));
        Assert.True(GamePaths.IsSpecificDir(GamePaths.NormalizeDir(Path.Combine(profile, "Downloads", "SomeGame"))));
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

    [Theory]
    [InlineData("steam://rungameid/570", "steam")]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", "com.epicgames.launcher")]
    [InlineData("uplay://launch/635/0", "uplay")]
    [InlineData("no-scheme", null)]
    [InlineData("", null)]
    public void Launch_url_scheme_is_extracted_for_the_installed_check(string uri, string? scheme) =>
        Assert.Equal(scheme, GameLauncher.SchemeOf(uri));

    // ───────────────────────────── Game process detection (no PID from the shell launch) ─────────────────────────────

    [Fact]
    public void Exact_exe_is_the_game_own_exe_never_an_outside_launcher()
    {
        var dir = GamePaths.NormalizeDir(@"C:\Riot Games\VALORANT\live");
        var riot = new GameEntry
        {
            Id = "riot:x", Name = "VALORANT", Platform = GamePlatform.Riot, InstallDir = dir,
            ExePath = @"C:\Riot Games\Riot Client\RiotClientServices.exe",
        };
        Assert.Null(GameProcessFinder.ExactExe(riot, dir));

        var gogDir = GamePaths.NormalizeDir(@"C:\GOG Games\Witcher 3");
        var gog = Game("gog:1", "Witcher 3", GamePlatform.Gog, gogDir, @"C:\GOG Games\Witcher 3\bin\x64\witcher3.exe");
        Assert.Equal(@"C:\GOG Games\Witcher 3\bin\x64\witcher3.exe", GameProcessFinder.ExactExe(gog, gogDir));

        // User-added exe counts even when its folder is too broad for folder matching.
        var desktop = GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        var custom = Game("custom:x", "Game", GamePlatform.Custom, desktop, Path.Combine(desktop, "game.exe"));
        Assert.Equal(Path.Combine(desktop, "game.exe"), GameProcessFinder.ExactExe(custom, desktop));
    }

    [Fact]
    public void Game_own_exe_ranks_first_then_memory()
    {
        var ranked = GameProcessFinder.Rank(
        [
            new GameProcess(10, "helper", @"D:\G\helper.exe", 900_000_000, false),
            new GameProcess(20, "game", @"D:\G\game.exe", 100_000_000, true),
            new GameProcess(30, "other", @"D:\G\other.exe", 500_000_000, false),
        ]);
        Assert.Equal([20, 10, 30], ranked.Select(p => p.Pid));
    }

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
    [InlineData("MONOLITH")]
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

    // ───────────────────────────── Boost rules ─────────────────────────────

    [Theory]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", 16, true)]
    [InlineData("AMD Ryzen 9 9900X3D 12-Core Processor", 12, true)]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", 8, false)]
    [InlineData("AMD Ryzen 9 5900X 12-Core Processor", 12, false)]
    [InlineData("Intel(R) Core(TM) i9-14900K", 24, false)]
    [InlineData("", 16, false)]
    public void Dual_ccd_x3d_detection(string cpu, int cores, bool dual) => Assert.Equal(dual, BoostPolicy.IsDualCcdX3D(cpu, cores));

    /// <summary>A desktop where every rule allows the switch; each test changes one fact.</summary>
    private static readonly PowerFacts Desktop = new(
        Enabled: true, DualCcdX3D: false, Laptop: false, OnBattery: false, ModernStandby: false,
        ManagedElsewhere: false, HighPerformanceExists: true, AlreadyFast: false);

    [Fact]
    public void Power_plan_switches_only_when_every_rule_allows_it()
    {
        Assert.Equal(PowerPlanAction.Switch, BoostPolicy.DecidePowerPlan(Desktop));
        Assert.Equal(PowerPlanAction.SkipDisabled, BoostPolicy.DecidePowerPlan(Desktop with { Enabled = false }));
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, BoostPolicy.DecidePowerPlan(Desktop with { DualCcdX3D = true }));
        Assert.Equal(PowerPlanAction.SkipLaptop, BoostPolicy.DecidePowerPlan(Desktop with { Laptop = true }));
        Assert.Equal(PowerPlanAction.SkipOnBattery, BoostPolicy.DecidePowerPlan(Desktop with { OnBattery = true }));
        Assert.Equal(PowerPlanAction.SkipModernStandby, BoostPolicy.DecidePowerPlan(Desktop with { ModernStandby = true }));
        Assert.Equal(PowerPlanAction.SkipAlreadyFast, BoostPolicy.DecidePowerPlan(Desktop with { AlreadyFast = true }));
        Assert.Equal(PowerPlanAction.SkipManagedElsewhere, BoostPolicy.DecidePowerPlan(Desktop with { ManagedElsewhere = true }));
        Assert.Equal(PowerPlanAction.SkipUnavailable, BoostPolicy.DecidePowerPlan(Desktop with { HighPerformanceExists = false }));
    }

    [Fact]
    public void Dual_ccd_x3d_wins_over_every_other_reason()
    {
        // Even on a laptop on battery with the setting on, the X3D rule is the one reported.
        var everything = new PowerFacts(true, true, true, true, true, true, false, true);
        Assert.Equal(PowerPlanAction.SkipDualCcdX3D, BoostPolicy.DecidePowerPlan(everything));
    }

    [Theory]
    [InlineData(false)] // plugged in
    [InlineData(true)]  // on battery
    public void Laptops_never_get_high_performance_plugged_in_or_not(bool onBattery)
    {
        // gaming_tweaks.md power-plan-high: SAFE-OPTIONAL for desktops only.
        Assert.Equal(PowerPlanAction.SkipLaptop, BoostPolicy.DecidePowerPlan(Desktop with { Laptop = true, OnBattery = onBattery }));
    }

    [Theory]
    [InlineData(true, true, true)]    // the session's plan is active and the PC unplugged → give it back now
    [InlineData(true, false, false)]  // still on mains → keep
    [InlineData(false, true, false)]  // the session did not switch (or already released) → nothing to do
    [InlineData(false, false, false)]
    public void Session_plan_is_released_when_the_pc_goes_on_battery(bool switched, bool onBattery, bool release) =>
        Assert.Equal(release, BoostPolicy.ShouldReleasePlanOnBattery(switched, onBattery));

    [Fact]
    public void Power_descriptions_are_specific()
    {
        Assert.Contains("«Сбалансированная» → «Высокая производительность»", BoostPolicy.Describe(PowerPlanAction.Switch, "Сбалансированная", ""));
        Assert.Contains("переходе на батарею", BoostPolicy.Describe(PowerPlanAction.Switch, "Сбалансированная", ""));
        Assert.Contains("Ryzen 9 7950X3D 16-Core", BoostPolicy.Describe(PowerPlanAction.SkipDualCcdX3D, "x", "AMD Ryzen 9 7950X3D 16-Core Processor"));
        Assert.Contains("с двумя CCD", BoostPolicy.Describe(PowerPlanAction.SkipDualCcdX3D, "x", "AMD Ryzen 9 7950X3D 16-Core Processor"));
        Assert.Contains("батареи", BoostPolicy.Describe(PowerPlanAction.SkipOnBattery, "x", ""));
        var laptop = BoostPolicy.Describe(PowerPlanAction.SkipLaptop, "x", "");
        Assert.Contains("ноутбук", laptop);
        Assert.Contains("0–2 %", laptop);
        Assert.Contains("Питание и батарея", laptop);
    }

    [Fact]
    public void Boost_texts_never_call_the_session_windows_game_mode()
    {
        // «Игровой режим» is the Windows feature; MONOLITH's session is «Ускорение игры».
        foreach (var action in Enum.GetValues<PowerPlanAction>())
        {
            var text = BoostPolicy.Describe(action, "Сбалансированная", "AMD Ryzen 9 7950X3D 16-Core Processor");
            var at = text.IndexOf("игровой режим", StringComparison.OrdinalIgnoreCase);
            Assert.True(at < 0 || text[at..].StartsWith("игровой режим Windows", StringComparison.OrdinalIgnoreCase), text);
        }
    }

    [Fact]
    public void Boost_plan_lists_exactly_what_will_happen()
    {
        var plan = new BoostPlan(PowerPlanAction.Switch, "План питания: A → B.",
            [new RunningApp("Discord", "", 3, 0, true), new RunningApp("chrome", "", 1, 0, true)], [], true, false);
        var game = Game("steam:730", "Counter-Strike 2", GamePlatform.Steam, @"D:\cs2");
        var lines = plan.Describe(game);
        Assert.Equal("План питания: A → B.", lines[0]);
        Assert.Contains(lines, l => l.Contains("Discord (3), chrome") && l.Contains("принудительное завершение — только после отдельного вопроса"));
        Assert.Contains(lines, l => l.Contains("«Высокий»") && l.Contains("никогда «Реального времени»"));
        Assert.DoesNotContain(lines, l => l.Contains("standby"));
        Assert.Contains(lines, l => l.Contains("«Counter-Strike 2»") && l.Contains("правами пользователя"));
    }

    [Fact]
    public void Boost_plan_is_honest_that_closed_apps_are_not_reopened()
    {
        var plan = new BoostPlan(PowerPlanAction.Switch, "План питания: A → B.", [new RunningApp("Discord", "", 1, 0, true)], [], true, false);
        var lines = plan.Describe(Game("steam:730", "Counter-Strike 2", GamePlatform.Steam, @"D:\cs2"));
        Assert.Contains(lines, l => l.Contains("Discord") && l.Contains("не открывает"));
        Assert.DoesNotContain(lines, l => l.Contains("всё вернётся", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void End_message_says_what_came_back_and_what_did_not()
    {
        var msg = BoostPolicy.EndMessage("«CS2» закрыта — ускорение выключено.", 1, 0, 1, ["Discord", "chrome"]);
        Assert.StartsWith("«CS2» закрыта — ускорение выключено.", msg);
        Assert.Contains("План питания возвращён.", msg);
        Assert.Contains("Приоритет игры возвращён.", msg);
        Assert.Contains("Закрытые программы (Discord, chrome) MONOLITH заново не открывает", msg);

        // Nothing was changed: the message claims nothing.
        Assert.Equal("Ускорение остановлено вручную.", BoostPolicy.EndMessage("Ускорение остановлено вручную.", 0, 0, 0, []));

        var failed = BoostPolicy.EndMessage("Стоп.", 0, 2, 0, []);
        Assert.Contains("2 изменения", failed);
        Assert.Contains("«Восстановление»", failed);
    }

    [Fact]
    public void Manual_session_plan_without_game_mentions_stop_button()
    {
        var plan = new BoostPlan(PowerPlanAction.SkipDisabled, "План питания не меняется (выключено).", [], [], false, true);
        var lines = plan.Describe(null);
        Assert.Contains(lines, l => l.Contains("список закрытия пуст"));
        Assert.Contains(lines, l => l.Contains("standby"));
        Assert.Contains(lines, l => l.Contains("«СТОП»"));
        Assert.DoesNotContain(lines, l => l.Contains("Приоритет"));
    }

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
        Assert.Contains("«Железо и драйверы»", old.Hint);

        Assert.Equal(ReadinessState.Info, GamingReadiness.GpuFromInfo("GPU", "", null, now).State);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070 Ti", "GeForce RTX 5070 Ti")]
    [InlineData("AMD Radeon RX 7800 XT", "Radeon RX 7800 XT")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Arc A770 Graphics")]
    [InlineData("Microsoft Basic Display Adapter", "Microsoft Basic Display Adapter")]
    public void Gpu_names_are_shortened(string name, string expected) => Assert.Equal(expected, GamingReadiness.ShortGpuName(name));

    [Fact]
    public void Readiness_power_plan_on_a_laptop_does_not_push_high_performance()
    {
        var balanced = Monolith.Core.PowerSchemes.Balanced;
        var laptop = GamingReadiness.PowerPlanFromFacts(balanced, "Сбалансированная", dualCcdX3D: false, laptop: true, onBattery: false);
        Assert.Equal(ReadinessState.Info, laptop.State);
        Assert.Contains("схему не меняет", laptop.Hint);

        var desktop = GamingReadiness.PowerPlanFromFacts(balanced, "Сбалансированная", dualCcdX3D: false, laptop: false, onBattery: false);
        Assert.Contains("Ускорение игры", desktop.Hint);
        Assert.DoesNotContain("Игровой режим", desktop.Hint);

        var battery = GamingReadiness.PowerPlanFromFacts(balanced, "Сбалансированная", dualCcdX3D: false, laptop: true, onBattery: true);
        Assert.Contains("батареи", battery.Hint);
    }

    [Fact]
    public void Readiness_power_plan_on_dual_ccd_x3d_wants_balanced()
    {
        var high = GamingReadiness.PowerPlanFromFacts(Monolith.Core.PowerSchemes.HighPerformance, "Высокая производительность", true, false, false);
        Assert.Equal(ReadinessState.Attention, high.State);
        Assert.Contains("с двумя CCD", high.Hint);
        Assert.Equal(ReadinessState.Ok,
            GamingReadiness.PowerPlanFromFacts(Monolith.Core.PowerSchemes.Balanced, "Сбалансированная", true, true, true).State);
    }

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
}
