using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Monolith.Core;

namespace Monolith.Modules.Games;

/// <summary>Starts a game the way its launcher expects: protocol URL or the exe itself.</summary>
public static class GameLauncher
{
    /// <summary>Only URLs built by our own parsers are ever opened.</summary>
    private static readonly string[] AllowedSchemes = ["steam://rungameid/", "com.epicgames.launcher://apps/", "uplay://launch/"];

    public static bool IsAllowedUri(string? uri) =>
        !string.IsNullOrEmpty(uri) && AllowedSchemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>Launches the game. Returns the PID when an exe was started directly, null for launcher URLs.</summary>
    public static int? Launch(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.LaunchUri))
        {
            if (!IsAllowedUri(game.LaunchUri))
                throw new InvalidOperationException("Неизвестный способ запуска игры.");
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"Не удалось открыть {GameText.SourceName(game.Platform)}: лаунчер не установлен или не отвечает ({ex.Message}).", ex);
            }
            Log.Info($"Запуск «{game.Name}» через {GameText.SourceName(game.Platform)}");
            return null;
        }

        if (string.IsNullOrEmpty(game.ExePath) || !File.Exists(game.ExePath))
            throw new FileNotFoundException($"Файл игры не найден: {game.ExePath}");

        var psi = new ProcessStartInfo(game.ExePath)
        {
            UseShellExecute = true,
            WorkingDirectory = !string.IsNullOrEmpty(game.WorkingDir) && Directory.Exists(game.WorkingDir)
                ? game.WorkingDir
                : Path.GetDirectoryName(game.ExePath) ?? "",
        };
        if (!string.IsNullOrWhiteSpace(game.Arguments)) psi.Arguments = game.Arguments;
        try
        {
            using var p = Process.Start(psi);
            Log.Info($"Запуск «{game.Name}»: {game.ExePath}");
            return p?.Id;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Запуск отменён в окне контроля учётных записей.", ex);
        }
    }

    /// <summary>Opens the install folder in Explorer.</summary>
    public static void OpenFolder(GameEntry game)
    {
        var dir = game.InstallDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Папка игры не найдена: {dir}");
        using var _ = Process.Start(new ProcessStartInfo(dir.TrimEnd('\\') + "\\") { UseShellExecute = true });
    }
}
