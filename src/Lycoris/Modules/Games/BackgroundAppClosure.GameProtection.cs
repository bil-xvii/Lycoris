using System.IO;
using System.Text.Json;
using Lycoris.Core;
using Lycoris.Core.Security;

namespace Lycoris.Modules.Games;

/// <summary>Known game paths are deny-only inputs. Launcher arguments and URLs are never used to open or start an executable.</summary>
internal sealed class BackgroundCloseGameProtection
{
    private const int MaxCachedGames = 20_000;
    private readonly HashSet<string> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    internal static BackgroundCloseGameProtection Empty { get; } = new();

    public bool Protects(string imagePath) =>
        _executables.Contains(imagePath) || _directories.Any(dir => GamePaths.IsUnder(imagePath, dir));

    public static BackgroundCloseGameProtection? ReadCurrent()
    {
        // AppPaths initializes a migration. Resolve the same per-user locations here without initializing it.
#if DEBUG
        var testRoot = Environment.GetEnvironmentVariable("LYCORIS_TEST_ROOT");
        if (!string.IsNullOrWhiteSpace(testRoot))
            return SettingsValidator.IsLocalAbsoluteFolder(testRoot) ? ReadUserData(Path.Combine(testRoot, "user")) : null;
#endif
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return SettingsValidator.IsLocalAbsoluteFolder(local)
            ? ReadUserData(Path.Combine(local, AppPaths.AppName), Path.Combine(local, "MONOLITH"))
            : null;
    }

    internal static BackgroundCloseGameProtection? ReadUserData(string current, string? legacy = null)
    {
        try
        {
            var currentExists = Exists(current, directory: true);
            var cache = currentExists ? FindCache(current) : null;
            var settings = currentExists ? FindSettings(current) : null;
            if (legacy is not null && (cache is null || settings is null) &&
                (!currentExists || !Exists(Path.Combine(current, ".migrated-from-monolith"), directory: false)) &&
                Exists(legacy, directory: true))
            {
                // Before migration, the main app merges each missing file. Apply that precedence without writing.
                cache ??= FindCache(legacy);
                settings ??= FindSettings(legacy);
            }

            var protection = new BackgroundCloseGameProtection();
            if (cache is not null)
            {
                var library = GameLibrary.LoadCache(cache, logError: false);
                if (library is null || library.Games.Count > MaxCachedGames) return null;
                foreach (var game in library.Games)
                {
                    if (game is null || !Enum.IsDefined(game.Platform)) return null;
                    if (game.ExePath is not null)
                    {
                        if (NormalizeExecutable(game.ExePath) is not { } exe) return null;
                        protection._executables.Add(exe);
                    }
                    if (game.Platform != GamePlatform.Custom && !string.IsNullOrWhiteSpace(game.InstallDir))
                    {
                        if (!SettingsValidator.IsLocalAbsoluteFolder(game.InstallDir)) return null;
                        var directory = GamePaths.NormalizeDir(game.InstallDir);
                        if (GamePaths.IsSpecificDir(directory)) protection._directories.Add(directory);
                    }
                }
            }

            if (settings is null) return protection;
            var custom = ParseCustomSettings(SafeIo.ReadText(settings));
            if (custom is null) return null;
            protection._executables.UnionWith(custom);
            return protection;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<string>? ParseCustomSettings(string json)
    {
        try
        {
            using var input = JsonDocument.Parse(json);
            if (input.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!input.RootElement.TryGetProperty(nameof(AppSettings.CustomGameFolders), out var custom)) return [];
            if (custom.ValueKind != JsonValueKind.Array || custom.GetArrayLength() > SettingsValidator.MaxListItems) return null;
            var paths = new List<string>();
            foreach (var item in custom.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || NormalizeExecutable(item.GetString()) is not { } exe)
                    return null;
                paths.Add(exe);
            }
            return paths;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? NormalizeExecutable(string? path) =>
        path is not null && SettingsValidator.IsLocalAbsoluteFolder(path) && !path.Any(char.IsControl) &&
        !path.AsSpan(2).Contains(':') && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(path)
            : null;

    private static string? FindCache(string root)
    {
        var directory = Path.Combine(root, "Cache");
        var path = Path.Combine(directory, "games.json");
        return Exists(directory, directory: true) && Exists(path, directory: false) ? path : null;
    }

    private static string? FindSettings(string root)
    {
        var path = Path.Combine(root, "settings.json");
        return Exists(path, directory: false) ? path : null;
    }

    private static bool Exists(string path, bool directory)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new InvalidDataException("Unexpected type of known game settings or cache.");
        return true;
    }
}
