using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Lycoris.Core;

namespace Lycoris.Modules.Games.Sources;

/// <summary>One "Programs and Features" entry (HKLM 64/32-bit and HKCU Uninstall keys).</summary>
public sealed record UninstallEntry(
    string KeyName,
    string DisplayName,
    string? Publisher,
    string? InstallLocation,
    string? UninstallString,
    string? DisplayIcon,
    long EstimatedSizeKb,
    bool SystemComponent);

internal enum UninstallScope { Machine64, Machine32, DesktopUser }
internal enum UninstallReadState { Complete, Partial, Failed }

/// <summary>Read-only identity and installer metadata. Command text, paths and icons are not retained.</summary>
internal sealed record UninstallRegistration(
    UninstallScope Scope, string KeyName, string DisplayName, string? Publisher, string? Version,
    bool? NoRemove, bool? SystemComponent, bool ParentOrUpdate, bool HasUninstallRegistration, string? UninstallFingerprint = null);

internal sealed record UninstallInventory(
    IReadOnlyList<UninstallRegistration> Entries, UninstallReadState State, DateTime CheckedAt);

/// <summary>Reads uninstall entries (read-only).</summary>
public static class UninstallEntries
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private static readonly string[] FingerprintValues =
        ["DisplayName", "Publisher", "DisplayVersion", "UninstallString", "QuietUninstallString", "WindowsInstaller", "WinGetInstallerType"];

    public static IReadOnlyList<UninstallEntry> ReadAll()
    {
        var list = new List<UninstallEntry>();
        Read(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath, list);
        Read(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPath, list);
        // Per-user installs (Riot, some EA titles) of the logged-in user, even when elevated as another admin.
        var (hive, prefix) = InteractiveUser.Hive;
        if (hive == RegHive.HKU) Read(RegistryHive.Users, RegistryView.Registry64, prefix + UninstallPath, list);
        else Read(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallPath, list);
        return list;
    }

    internal static UninstallInventory ReadInventory()
    {
        var entries = new List<UninstallRegistration>();
        var machine64 = Read(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallPath, null, entries, UninstallScope.Machine64);
        var machine32 = Read(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallPath, null, entries, UninstallScope.Machine32);
        var desktop = ReadDesktopUser(entries);
        return new UninstallInventory(entries, InventoryState(machine64, machine32, desktop), DateTime.Now);
    }

    internal static UninstallReadState InventoryState(UninstallReadState machine64, UninstallReadState machine32, UninstallReadState desktop) =>
        machine64 == desktop && machine32 == desktop ? desktop : UninstallReadState.Partial;

    private static UninstallReadState ReadDesktopUser(List<UninstallRegistration> entries)
    {
        var desktopAccount = Elevation.InteractiveUserState;
        if (desktopAccount == true)
            return Read(RegistryHive.CurrentUser, RegistryView.Registry64, UninstallPath, null, entries, UninstallScope.DesktopUser);

        // The legacy games scanner can fall back to its own HKCU. The removal panel needs verified desktop-user scope.
        if (desktopAccount == false && InteractiveUser.Hive is (RegHive.HKU, var prefix))
            return Read(RegistryHive.Users, RegistryView.Registry64, UninstallPath, null, entries, UninstallScope.DesktopUser, prefix.TrimEnd('\\'));

        return UninstallReadState.Failed;
    }

    private static UninstallReadState Read(RegistryHive hive, RegistryView view, string path, List<UninstallEntry>? into,
        List<UninstallRegistration>? inventory = null, UninstallScope scope = default, string? userSubKey = null)
    {
        var state = UninstallReadState.Complete;
        try
        {
            using var b = RegistryKey.OpenBaseKey(hive, view);
            using var user = userSubKey is null ? null : b.OpenSubKey(userSubKey, writable: false);
            if (userSubKey is not null && user is null) return UninstallReadState.Failed;
            using var root = (user ?? b).OpenSubKey(path, writable: false);
            if (root is null) return UninstallReadState.Complete;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var k = root.OpenSubKey(name);
                    if (k is null)
                    {
                        state = UninstallReadState.Partial;
                        continue;
                    }
                    if (inventory is not null)
                    {
                        if (ReadRegistration(k, name, scope) is { } registration) inventory.Add(registration);
                        continue;
                    }
                    var display = k.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(display)) continue;
                    long size = k.GetValue("EstimatedSize") switch { int i => (uint)i, long l => l, _ => 0 };
                    into!.Add(new UninstallEntry(
                        name, display.Trim(),
                        k.GetValue("Publisher")?.ToString(),
                        k.GetValue("InstallLocation")?.ToString(),
                        k.GetValue("UninstallString")?.ToString(),
                        k.GetValue("DisplayIcon")?.ToString(),
                        size,
                        k.GetValue("SystemComponent") is int sc && sc == 1));
                }
                catch { state = UninstallReadState.Partial; }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Список программ ({0}): {1}", hive, inventory is null ? ex.Message : ex.GetType().Name);
            return UninstallReadState.Failed;
        }
        return state;
    }

    private static UninstallRegistration? ReadRegistration(RegistryKey key, string name, UninstallScope scope)
    {
        object? ReadValue(string value) => key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames);

        if (ReadValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) return null;
        var hasHook = ReadValue("UninstallString") is string hook && !string.IsNullOrWhiteSpace(hook);
        return new UninstallRegistration(scope, name, InventoryText(display) ?? "—",
            InventoryText(ReadValue("Publisher")), InventoryText(ReadValue("DisplayVersion")),
            InstallerFlag(ReadValue("NoRemove")), InstallerFlag(ReadValue("SystemComponent")),
            ReadValue("ParentKeyName") is not null || ReadValue("ReleaseType") is not null,
            hasHook, hasHook ? ReadUninstallFingerprint(key) : null);
    }

    /// <summary>Captures exact kinds and original values without retaining path or command text in the inventory.</summary>
    internal static string? ReadUninstallFingerprint(RegistryKey key)
    {
        try
        {
            using var bytes = new MemoryStream();
            using var data = new BinaryWriter(bytes, Encoding.Unicode, leaveOpen: true);
            foreach (var name in FingerprintValues)
            {
                var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                data.Write(value is not null);
                if (value is null)
                {
                    if (name is "DisplayName" or "DisplayVersion" or "Publisher" or "UninstallString") return null;
                    continue;
                }
                var kind = key.GetValueKind(name);
                data.Write((int)kind);
                if (name == "WindowsInstaller")
                {
                    if (kind != RegistryValueKind.DWord || value is not int flag || flag is not (0 or 1)) return null;
                    data.Write(flag);
                }
                else
                {
                    if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString) || value is not string text ||
                        text.Length > 32_767 || name == "WinGetInstallerType" && text.Equals("portable", StringComparison.OrdinalIgnoreCase))
                        return null;
                    data.Write(text);
                }
            }
            return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool? InstallerFlag(object? value) => value switch { null or 0 => false, 1 => true, _ => null };

    internal static string? InventoryText(object? value)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw)) return null;
        return string.Concat(raw.Trim().Take(300).Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format
            ? ' ' : c));
    }
}

/// <summary>
/// Launchers without a public manifest format: Ubisoft Connect (its Installs key, docs/research/tech.md §6)
/// and EA app / Battle.net / Riot games, recognised through their uninstall entries.
/// </summary>
public static class LauncherSources
{
    private static readonly HashSet<string> LauncherNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "EA app", "EA Desktop", "Origin", "Battle.net", "Riot Client", "Ubisoft Connect", "Uplay",
    };

    private static readonly Regex RiotProduct = new(@"--uninstall-product=(?<v>[A-Za-z0-9_.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RiotPatchline = new(@"--uninstall-patchline=(?<v>[A-Za-z0-9_.\-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Which launcher installed this program (games only; launchers, anti-cheat and Steam copies return null).</summary>
    public static GamePlatform? Classify(UninstallEntry e)
    {
        if (e.SystemComponent || string.IsNullOrWhiteSpace(e.DisplayName)) return null;
        if (e.KeyName.StartsWith("Steam App", StringComparison.OrdinalIgnoreCase)) return null;
        if (e.UninstallString?.Contains("steam://", StringComparison.OrdinalIgnoreCase) == true) return null;
        var name = e.DisplayName.Trim();
        if (LauncherNames.Contains(name)) return null;
        var pub = e.Publisher ?? "";

        if (pub.StartsWith("Riot Games", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)) return null;
            return RiotLaunch(e.UninstallString) is not null ? GamePlatform.Riot : null;
        }
        if (pub.StartsWith("Blizzard", StringComparison.OrdinalIgnoreCase)) return GamePlatform.BattleNet;
        if (pub.StartsWith("Electronic Arts", StringComparison.OrdinalIgnoreCase)) return GamePlatform.Ea;
        return null;
    }

    /// <summary>
    /// Riot games uninstall through RiotClientServices.exe --uninstall-product=X --uninstall-patchline=Y;
    /// the same client launches them with --launch-product / --launch-patchline (what Riot's own shortcuts do).
    /// </summary>
    public static (string Exe, string Arguments)? RiotLaunch(string? uninstallString)
    {
        if (string.IsNullOrWhiteSpace(uninstallString)) return null;
        var s = uninstallString.Trim();
        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            if (end < 2) return null;
            exe = s[1..end];
        }
        else
        {
            var idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            exe = s[..(idx + 4)];
        }
        if (!Path.GetFileName(exe).Equals("RiotClientServices.exe", StringComparison.OrdinalIgnoreCase)) return null;
        var product = RiotProduct.Match(s);
        if (!product.Success) return null;
        if (product.Groups["v"].Value.Equals("riot_client", StringComparison.OrdinalIgnoreCase)) return null;
        var patch = RiotPatchline.Match(s);
        var patchline = patch.Success ? patch.Groups["v"].Value : "live";
        return (exe, $"--launch-product={product.Groups["v"].Value} --launch-patchline={patchline}");
    }

    /// <summary>The game exe advertised by an uninstall entry's DisplayIcon, if it is inside the install folder and is not an uninstaller.</summary>
    public static string? GameExeFromEntry(UninstallEntry e, string normalizedInstallDir)
    {
        var exe = GamePaths.ExeFromIconValue(e.DisplayIcon);
        if (exe is null || normalizedInstallDir.Length == 0) return null;
        if (!GamePaths.IsUnder(exe, normalizedInstallDir)) return null;
        var file = Path.GetFileNameWithoutExtension(exe);
        string[] notGames = ["unins", "uninst", "cleanup", "installer", "setup", "touchup", "crash"];
        if (notGames.Any(n => file.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;
        return exe;
    }

    public static IReadOnlyList<SourceResult> ScanUninstallLaunchers(IReadOnlyList<UninstallEntry> entries)
    {
        var ea = new List<GameEntry>();
        var bnet = new List<GameEntry>();
        var riot = new List<GameEntry>();

        foreach (var e in entries)
        {
            try
            {
                var platform = Classify(e);
                if (platform is null) continue;
                var dir = GamePaths.NormalizeDir(e.InstallLocation);
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                var size = e.EstimatedSizeKb * 1024;

                if (platform == GamePlatform.Riot)
                {
                    var launch = RiotLaunch(e.UninstallString)!.Value;
                    if (!File.Exists(launch.Exe)) continue;
                    riot.Add(new GameEntry
                    {
                        Id = $"riot:{e.KeyName}", Name = e.DisplayName, Platform = GamePlatform.Riot, InstallDir = dir,
                        ExePath = launch.Exe, Arguments = launch.Arguments, WorkingDir = Path.GetDirectoryName(launch.Exe), SizeBytes = size,
                    });
                    continue;
                }

                var exe = GameExeFromEntry(e, dir);
                if (exe is null || !File.Exists(exe)) continue;
                var entry = new GameEntry
                {
                    Id = $"{(platform == GamePlatform.Ea ? "ea" : "bnet")}:{e.KeyName}", Name = e.DisplayName, Platform = platform.Value,
                    InstallDir = dir, ExePath = exe, WorkingDir = Path.GetDirectoryName(exe), SizeBytes = size,
                };
                (platform == GamePlatform.Ea ? ea : bnet).Add(entry);
            }
            catch (Exception ex)
            {
                Log.Warn("Лаунчеры: пропущена запись «{0}» — {1}", e.DisplayName, ex.Message);
            }
        }

        bool Has(params string[] names) => entries.Any(x => names.Any(n => x.DisplayName.Equals(n, StringComparison.OrdinalIgnoreCase)));
        var riotInstalled = riot.Count > 0 || entries.Any(x => x.Publisher?.StartsWith("Riot Games", StringComparison.OrdinalIgnoreCase) == true);
        return
        [
            new SourceResult(GamePlatform.Ea, ea.Count > 0 || Has("EA app", "EA Desktop", "Origin"), ea),
            new SourceResult(GamePlatform.BattleNet, bnet.Count > 0 || Has("Battle.net"), bnet),
            new SourceResult(GamePlatform.Riot, riotInstalled, riot),
        ];
    }

    /// <summary>Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\&lt;id&gt;\InstallDir. Launch: uplay://launch/&lt;id&gt;/0.</summary>
    public static SourceResult ScanUbisoft(IReadOnlyList<UninstallEntry> entries)
    {
        RegistryKey? installs;
        bool launcher;
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using (var l = hklm32.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher")) launcher = l is not null;
            installs = hklm32.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
        }
        catch (Exception ex)
        {
            return new SourceResult(GamePlatform.Ubisoft, false, [], ex.Message);
        }
        if (installs is null) return new SourceResult(GamePlatform.Ubisoft, launcher, []);

        var games = new List<GameEntry>();
        using (installs)
        {
            foreach (var id in installs.GetSubKeyNames())
            {
                try
                {
                    if (!id.All(char.IsDigit)) continue;
                    using var k = installs.OpenSubKey(id);
                    var dir = GamePaths.NormalizeDir(k?.GetValue("InstallDir")?.ToString());
                    if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                    var un = entries.FirstOrDefault(x => x.KeyName.Equals($"Uplay Install {id}", StringComparison.OrdinalIgnoreCase));
                    var name = un?.DisplayName;
                    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(dir.TrimEnd('\\'));
                    games.Add(new GameEntry
                    {
                        Id = $"ubisoft:{id}", Name = name, Platform = GamePlatform.Ubisoft, InstallDir = dir,
                        LaunchUri = $"uplay://launch/{id}/0", SizeBytes = (un?.EstimatedSizeKb ?? 0) * 1024,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("Ubisoft Connect: пропущена запись {0} — {1}", id, ex.Message);
                }
            }
        }
        return new SourceResult(GamePlatform.Ubisoft, true, games);
    }
}
