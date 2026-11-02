using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Lycoris.Core;

namespace Lycoris.Modules.Games;

/// <summary>Starts a game the way its launcher expects: protocol URL or the exe itself.</summary>
public static class GameLauncher
{
    /// <summary>Only URLs built by our own parsers are ever opened.</summary>
    private static readonly string[] AllowedSchemes = ["steam://rungameid/", "com.epicgames.launcher://apps/", "uplay://launch/"];

    public static bool IsAllowedUri(string? uri) =>
        !string.IsNullOrEmpty(uri) && AllowedSchemes.Any(s => uri.StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Launches the game as the signed-in user, never with Lycoris's administrator token (tech.md §3): when Lycoris is
    /// elevated the start goes through the desktop shell (<see cref="UnelevatedShell"/>), and if that is impossible the
    /// game is not started at all. Returns the PID only for a direct (non-elevated) exe start; otherwise null, and
    /// <see cref="GameProcessFinder"/> finds the game by its exe / install folder.
    /// </summary>
    public static async Task<int?> LaunchAsync(GameEntry game)
    {
        if (!string.IsNullOrEmpty(game.LaunchUri))
        {
            if (!IsAllowedUri(game.LaunchUri))
                throw new InvalidOperationException(Loc.T("Неизвестный способ запуска игры."));
            if (Elevation.IsElevated)
            {
                if (!await UnelevatedShell.ShellExecuteAsync(game.LaunchUri, "", "", UnelevatedShell.SwShowNormal).ConfigureAwait(false))
                    throw new InvalidOperationException(NoUnelevatedShell(GameText.SourceName(game.Platform)));
            }
            else
            {
                try
                {
                    using var _ = Process.Start(new ProcessStartInfo(game.LaunchUri) { UseShellExecute = true });
                }
                catch (Win32Exception ex)
                {
                    throw new InvalidOperationException(
                        Loc.Tf("Не удалось открыть {0}: лаунчер не установлен или не отвечает ({1}).", GameText.SourceName(game.Platform), ex.Message), ex);
                }
            }
            Log.Info("Запуск «{0}» через {1}", game.Name, GameText.SourceName(game.Platform));
            return null;
        }

        if (string.IsNullOrEmpty(game.ExePath) || !File.Exists(game.ExePath))
            throw new FileNotFoundException(Loc.Tf("Файл игры не найден: {0}", game.ExePath));

        var workingDir = !string.IsNullOrEmpty(game.WorkingDir) && Directory.Exists(game.WorkingDir)
            ? game.WorkingDir
            : Path.GetDirectoryName(game.ExePath) ?? "";

        if (Elevation.IsElevated)
        {
            // Never fall back to an elevated Process.Start: the exe path and the game folder are user-writable.
            if (!await UnelevatedShell.ShellExecuteAsync(game.ExePath, game.Arguments ?? "", workingDir, UnelevatedShell.SwShowNormal).ConfigureAwait(false))
                throw new InvalidOperationException(NoUnelevatedShell($"«{game.Name}»"));
            Log.Info("Запуск «{0}» без прав администратора: {1}", game.Name, game.ExePath);
            return null;
        }

        var psi = new ProcessStartInfo(game.ExePath) { UseShellExecute = true, WorkingDirectory = workingDir };
        if (!string.IsNullOrWhiteSpace(game.Arguments)) psi.Arguments = game.Arguments;
        try
        {
            using var p = Process.Start(psi);
            Log.Info("Запуск «{0}»: {1}", game.Name, game.ExePath);
            return p?.Id;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException(Loc.T("Запуск отменён в окне контроля учётных записей."), ex);
        }
    }

    private static string NoUnelevatedShell(string what) =>
        Loc.Tf("Не удалось запустить {0} без прав администратора: рабочий стол Windows (Проводник) не отвечает. " +
        "С правами Lycoris игры не запускаются — запустите игру обычным способом или повторите, когда Проводник перезапустится.", what);

    /// <summary>Opens the install folder in Explorer.</summary>
    public static void OpenFolder(GameEntry game)
    {
        var dir = game.InstallDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException(Loc.Tf("Папка игры не найдена: {0}", dir));
        using var _ = Process.Start(new ProcessStartInfo(dir.TrimEnd('\\') + "\\") { UseShellExecute = true });
    }
}
