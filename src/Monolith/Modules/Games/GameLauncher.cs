using System.IO;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>
/// Starts a game the way its launcher expects (protocol URL or the exe itself) and opens game folders —
/// always as the signed-in user, never with MONOLITH's administrator token (CLAUDE.md rule 8, tech.md §3):
/// <see cref="UnelevatedShell.StartAsUserAsync"/> asks the desktop Explorer to start it. There is no elevated fallback.
/// No PID comes back, so Game Boost finds the game by its exe / install folder (<see cref="GameProcessFinder"/>).
/// </summary>
public static class GameLauncher
{
    /// <summary>Only URLs built by our own parsers are ever opened.</summary>
    private static readonly string[] AllowedSchemes = ["steam://rungameid/", "com.epicgames.launcher://apps/", "uplay://launch/"];

    public static bool IsAllowedUri(string? uri) =>
        !string.IsNullOrEmpty(uri) && AllowedSchemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>"steam://rungameid/570" → "steam"; null when there is no scheme.</summary>
    public static string? SchemeOf(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        var i = uri.IndexOf("://", StringComparison.Ordinal);
        return i > 0 ? uri[..i] : null;
    }

    /// <summary>
    /// Starts the game as the signed-in user. Throws with a user-facing Russian message when it cannot be started
    /// (file missing, launcher not installed, or the desktop shell did not respond).
    /// </summary>
    public static async Task LaunchAsync(GameEntry game)
    {
        var (file, args, dir, what) = await Task.Run(() => Prepare(game));
        var ok = await Task.Run(() => UnelevatedShell.StartAsUserAsync(file, args, dir));
        if (!ok) throw new InvalidOperationException(NotStartedMessage(what));
        Log.Info(string.IsNullOrEmpty(game.LaunchUri)
            ? $"Запуск «{game.Name}» с правами пользователя: {file}"
            : $"Запуск «{game.Name}» через {GameText.SourceName(game.Platform)} с правами пользователя");
    }

    /// <summary>Opens the install folder in the user's Explorer.</summary>
    public static async Task OpenFolderAsync(GameEntry game)
    {
        var dir = game.InstallDir;
        var exists = !string.IsNullOrEmpty(dir) && await Task.Run(() => Directory.Exists(dir));
        if (!exists) throw new DirectoryNotFoundException($"Папка игры не найдена: {dir}");
        var path = dir.TrimEnd('\\') + "\\";
        if (!await Task.Run(() => UnelevatedShell.StartAsUserAsync(path)))
            throw new InvalidOperationException(Elevation.IsElevated
                ? "Проводник Windows не ответил — откройте папку вручную: " + path
                : "Не удалось открыть папку: " + path);
    }

    /// <summary>Validates the entry and returns exactly what will be started. Blocking (file system, registry).</summary>
    private static (string File, string Args, string? Dir, string What) Prepare(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.LaunchUri))
        {
            if (!IsAllowedUri(game.LaunchUri))
                throw new InvalidOperationException("Неизвестный способ запуска игры.");
            var launcher = GameText.SourceName(game.Platform);
            if (!IsProtocolRegistered(SchemeOf(game.LaunchUri)))
                throw new InvalidOperationException($"Не удалось открыть {launcher}: лаунчер не установлен (Windows не знает, чем открыть ссылку запуска).");
            return (game.LaunchUri, "", null, launcher);
        }

        if (string.IsNullOrEmpty(game.ExePath) || !File.Exists(game.ExePath))
            throw new FileNotFoundException($"Файл игры не найден: {game.ExePath}");
        var dir = !string.IsNullOrEmpty(game.WorkingDir) && Directory.Exists(game.WorkingDir)
            ? game.WorkingDir
            : Path.GetDirectoryName(game.ExePath);
        return (game.ExePath, game.Arguments?.Trim() ?? "", dir, $"«{game.Name}»");
    }

    /// <summary>A launcher URL works only when its scheme has an open command (machine-wide or for the signed-in user).</summary>
    private static bool IsProtocolRegistered(string? scheme)
    {
        if (string.IsNullOrEmpty(scheme)) return false;
        var command = scheme + @"\shell\open\command";
        try
        {
            return RegistryHelper.KeyExists(RegHive.HKCR, command) ||
                   InteractiveUser.KeyExists(@"Software\Classes\" + command);
        }
        catch
        {
            return true; // cannot tell — let the shell decide and show its own message
        }
    }

    private static string NotStartedMessage(string what) => Elevation.IsElevated
        ? $"Не удалось запустить {what} с правами пользователя: Проводник Windows не ответил. " +
          "С правами администратора MONOLITH игры не запускает — откройте игру ярлыком или из лаунчера."
        : $"Не удалось запустить {what}: Windows отклонила запуск или он был отменён в окне контроля учётных записей.";
}
