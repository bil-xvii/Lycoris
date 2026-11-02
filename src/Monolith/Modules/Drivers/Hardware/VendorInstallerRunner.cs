using System.Diagnostics;
using System.IO;
using Monolith.Core;

namespace Monolith.Modules.Drivers.Hardware;

/// <summary>
/// Starts an official driver installer that <see cref="DriverDownloads.DownloadAsync"/> has downloaded and whose vendor
/// signature it verified, and waits until the user finishes it. Only files from the admin-only download folder are
/// started (nobody without admin rights can replace them after the check). Cancelling stops the wait, never the
/// installer: killing a driver installer half-way could leave the device without a driver.
/// </summary>
internal static class VendorInstallerRunner
{
    /// <summary>True when the file is directly inside the verified-downloads folder.</summary>
    public static bool IsInDownloadsFolder(string path, string downloadsDir)
    {
        try
        {
            var dir = Path.GetFullPath(downloadsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Runs the installer interactively and returns its exit code, or null when the wait was cancelled.</summary>
    public static async Task<int?> RunAndWaitAsync(string path, CancellationToken ct)
    {
        SafetyLock.ThrowIfLocked(Loc.T("запуск установщика драйвера"));
        if (!Elevation.IsElevated) throw new InvalidOperationException(Loc.T("Для установки драйвера нужны права администратора."));
        if (!IsInDownloadsFolder(path, DriverDownloads.DownloadsDir) || !File.Exists(path))
            throw new InvalidOperationException(Loc.T("Установщик не найден в папке проверенных загрузок — запуск отменён."));

        using var process = Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        }) ?? throw new InvalidOperationException(Loc.T("Windows не запустила установщик."));
        Log.Info("Запущен официальный установщик: {0} — ожидание его завершения.", Path.GetFileName(path));
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log.Warn("Ожидание установщика {0} прекращено; сам установщик продолжает работу.", Path.GetFileName(path));
            return null;
        }
    }
}
