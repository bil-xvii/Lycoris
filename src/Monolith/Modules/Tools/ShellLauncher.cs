using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Tools;

/// <summary>
/// Opens Windows tools, settings pages, folders and files through the shell.
/// Executables are always resolved to full System32 / Windows paths, never through the working directory.
/// Used by the Tools, Restore and Settings pages.
/// </summary>
public static class ShellLauncher
{
    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string Explorer => Path.Combine(WindowsDir, "explorer.exe");

    /// <summary>Starts a file / URI with the shell (msc, cpl, exe, ms-settings:…). Returns false and toasts on failure.</summary>
    public static bool Open(string target, string? arguments = null, string? displayName = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
            using var _ = Process.Start(psi);
            Log.Info("Открыто: {0}{1}", displayName ?? target, string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user declined a UAC prompt of the opened tool — not an error.
            Log.Info("Запуск отменён пользователем: {0}", displayName ?? target);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Не удалось открыть «{0}»", ex, displayName ?? target);
            Ui.Toast(Loc.Tf("Не удалось открыть «{0}»: {1}", displayName ?? target, ex.Message), ToastKind.Error);
            return false;
        }
    }

    /// <summary>Opens a folder in Explorer (creates it first when possible).</summary>
    public static bool OpenFolder(string folder, string? displayName = null)
    {
        if (!Directory.Exists(folder) && !AppPaths.TryEnsureDir(folder))
        {
            Ui.Toast(Loc.Tf("Папка «{0}» ещё не создана.", displayName ?? folder), ToastKind.Warning);
            return false;
        }
        return Open(Explorer, Quote(folder), displayName ?? folder);
    }

    /// <summary>Opens Explorer with the file selected; falls back to its folder when the file does not exist yet.</summary>
    public static bool RevealFile(string file, string? displayName = null)
    {
        if (File.Exists(file)) return Open(Explorer, "/select," + Quote(file), displayName ?? file);
        var dir = Path.GetDirectoryName(file);
        if (dir is not null && Directory.Exists(dir)) return Open(Explorer, Quote(dir), displayName ?? dir);
        Ui.Toast(Loc.Tf("Файл «{0}» ещё не создан.", displayName ?? Path.GetFileName(file)), ToastKind.Warning);
        return false;
    }

    private static string Quote(string path) => "\"" + path.TrimEnd('\\') + "\"";
}
