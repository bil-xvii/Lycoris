using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Tools;

/// <summary>
/// Opens Windows tools, settings pages, folders and files through the shell.
/// Executables are always resolved to full System32 / Windows paths, never through the working directory.
/// Folders and documents (HTML reports) are opened as the signed-in user through <see cref="UnelevatedShell"/>:
/// an Explorer window or a browser must never inherit MONOLITH's administrator rights.
/// Used by the Tools, Restore and Settings pages.
/// </summary>
public static class ShellLauncher
{
    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string Explorer => Path.Combine(WindowsDir, "explorer.exe");

    /// <summary>
    /// Starts a Windows administration tool or settings page (msc, cpl, System32 exe, ms-settings:…).
    /// These run with MONOLITH's rights on purpose (Device Manager, Disk Management need them). Not for web pages,
    /// documents or folders — see <see cref="OpenDocumentAsUserAsync"/> / <see cref="OpenFolderAsync"/>.
    /// Returns false and toasts on failure.
    /// </summary>
    public static bool Open(string target, string? arguments = null, string? displayName = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
            using var _ = Process.Start(psi);
            Log.Info($"Открыто: {displayName ?? target}{(string.IsNullOrEmpty(arguments) ? "" : " " + arguments)}");
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined a UAC prompt of the opened tool — not an error.
            Log.Info($"Запуск отменён пользователем: {displayName ?? target}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"Не удалось открыть «{displayName ?? target}»", ex);
            Ui.Toast($"Не удалось открыть «{displayName ?? target}»: {ex.Message}", ToastKind.Error);
            return false;
        }
    }

    /// <summary>True for a local HTML report MONOLITH wrote into its own per-user logs folder.</summary>
    public static bool IsOwnHtmlReport(string file, string logsDir)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var dir = Path.GetFullPath(logsDir).TrimEnd('\\') + "\\";
            var ext = Path.GetExtension(full);
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                   && (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens an HTML report from MONOLITH's logs folder in the default browser as the signed-in user (never elevated).
    /// Returns false when it could not be opened without administrator rights; the caller then shows the path.
    /// </summary>
    public static async Task<bool> OpenDocumentAsUserAsync(string file, string displayName)
    {
        if (!IsOwnHtmlReport(file, AppPaths.LogsDir) || !File.Exists(file))
        {
            Log.Warn($"Отклонено открытие файла: {file}");
            return false;
        }
        var ok = await UnelevatedShell.StartAsUserAsync(file, "", Path.GetDirectoryName(file));
        if (ok) Log.Info($"Открыто: {displayName}");
        else Log.Warn($"«{displayName}» не открыт без прав администратора: {file}");
        return ok;
    }

    /// <summary>Opens a folder in Explorer as the signed-in user (creates it first when possible).</summary>
    public static async Task<bool> OpenFolderAsync(string folder, string? displayName = null)
    {
        if (!Directory.Exists(folder) && !AppPaths.TryEnsureDir(folder))
        {
            Ui.Toast($"Папка «{displayName ?? folder}» ещё не создана.", ToastKind.Warning);
            return false;
        }
        return await StartExplorerAsync(Quote(folder), displayName ?? folder, folder);
    }

    /// <summary>Opens Explorer with the file selected; falls back to its folder when the file does not exist yet.</summary>
    public static async Task<bool> RevealFileAsync(string file, string? displayName = null)
    {
        if (File.Exists(file)) return await StartExplorerAsync("/select," + Quote(file), displayName ?? file, file);
        var dir = Path.GetDirectoryName(file);
        if (dir is not null && Directory.Exists(dir)) return await StartExplorerAsync(Quote(dir), displayName ?? dir, dir);
        Ui.Toast($"Файл «{displayName ?? Path.GetFileName(file)}» ещё не создан.", ToastKind.Warning);
        return false;
    }

    private static async Task<bool> StartExplorerAsync(string arguments, string displayName, string path)
    {
        if (await UnelevatedShell.StartAsUserAsync(Explorer, arguments))
        {
            Log.Info($"Открыто: {displayName}");
            return true;
        }
        // No elevated fallback: an Explorer window with administrator rights would hand them to everything opened from it.
        Ui.Toast($"Не удалось открыть Проводник без прав администратора. Путь: {path}", ToastKind.Warning, displayName);
        return false;
    }

    private static string Quote(string path) => "\"" + path.TrimEnd('\\') + "\"";
}
