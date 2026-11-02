using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Lycoris.Core.Updates;

/// <summary>
/// The old (running) copy's part of an update: put the verified exe where the updater will run from, check it once more
/// through a handle that nobody can write through, start it as «--apply-update …» and let the caller close the app.
/// The running exe is NEVER touched here (CLAUDE.md: never overwrite the running single-file exe).
/// </summary>
public static class UpdateLauncher
{
    public readonly record struct LaunchResult(bool Started, string? Error);

    /// <summary>
    /// Opens <paramref name="path"/> for reading with FileShare.Read (nobody can write or delete while it is open), hashes it
    /// and compares with <paramref name="sha256"/>. The caller keeps the stream open until the process has started, so the
    /// bytes that were checked are the bytes that run.
    /// </summary>
    public static async Task<FileStream> OpenVerifiedAsync(string path, string sha256, CancellationToken ct = default)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        try
        {
            var actual = await UpdateVerifier.Sha256Async(fs, ct).ConfigureAwait(false);
            if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRefusedException(Loc.T("Файл обновления изменился после проверки — обновление отменено."));
            return fs;
        }
        catch
        {
            await fs.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static async Task<LaunchResult> StartAsync(UpdatePlan plan, StagedUpdate staged, int oldPid, CancellationToken ct = default)
    {
        if (plan.Route == UpdateRoute.Unsupported) return new LaunchResult(false, plan.Reason);
        var runFrom = staged.Path;
        try
        {
            if (plan.Route == UpdateRoute.InPlace)
            {
                runFrom = plan.StagePath;
                if (File.Exists(runFrom)) File.Delete(runFrom);
                var free = new DriveInfo(Path.GetPathRoot(plan.ExePath)!).AvailableFreeSpace;
                if (free < staged.Size * 2 + (64L << 20))
                    return new LaunchResult(false, Loc.Tf("Мало места на диске {0}: нужно хотя бы {1}.", Path.GetPathRoot(plan.ExePath), SystemProfile.FormatBytes(staged.Size * 2 + (64L << 20))));
                File.Copy(staged.Path, runFrom, overwrite: false);
            }

            await using var locked = await OpenVerifiedAsync(runFrom, staged.Sha256, ct).ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = runFrom,
                WorkingDirectory = Path.GetDirectoryName(runFrom) ?? "",
                UseShellExecute = plan.Route == UpdateRoute.Elevated,
                Verb = plan.Route == UpdateRoute.Elevated ? "runas" : "",
            };
            psi.ArgumentList.Add(UpdateApplyMode.Switch);
            psi.ArgumentList.Add(plan.ExePath);
            psi.ArgumentList.Add(oldPid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The handshake: the updater hashes ITS OWN file and refuses to run when it is not the file verified here
            // (the staged copy lies in a folder the user — and any program running as the user — can write to).
            psi.ArgumentList.Add(staged.Sha256);
            Log.Info("Обновление: запускаем программу обновления ({0}).", plan.Route == UpdateRoute.Elevated ? "elevated, UAC" : "normal");
            using var p = Process.Start(psi);
            return p is null ? new LaunchResult(false, Loc.T("Не удалось запустить программу обновления.")) : new LaunchResult(true, null);
        }
        catch (UpdateRefusedException ex)
        {
            if (plan.Route == UpdateRoute.InPlace) TryDelete(runFrom);
            return new LaunchResult(false, ex.Message);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LaunchResult(false, Loc.T("Запрос прав администратора отклонён — обновление не выполнено. Ничего не изменено."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            if (plan.Route == UpdateRoute.InPlace) TryDelete(runFrom);
            Log.Warn("Обновление: не удалось подготовить файл ({0}).", ex.Message);
            return new LaunchResult(false, Loc.Tf("Не удалось подготовить обновление: {0}", ex.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

/// <summary>Leftovers of an update: the old exe kept for a rollback and the staged downloads.</summary>
public static class UpdateCleanup
{
    /// <summary>
    /// A minute after a successful start: delete Lycoris.exe.old / .new beside the exe and downloaded files of versions that
    /// are not newer than this build. Never touches anything else; every failure is ignored.
    /// </summary>
    public static void Run(string exePath, string updatesFolder, SemVer current)
    {
        foreach (var leftover in new[] { exePath + ".old", exePath + ".new" })
            TryDelete(leftover);
        try
        {
            if (!Directory.Exists(updatesFolder)) return;
            foreach (var f in Directory.EnumerateFiles(updatesFolder, "Lycoris-*.exe*").Concat(Directory.EnumerateFiles(updatesFolder, "MONOLITH-*.exe*")))
            {
                var name = Path.GetFileName(f);
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(6)) TryDelete(f);
                    continue;
                }
                var text = Path.GetFileNameWithoutExtension(name)[(name.StartsWith("MONOLITH-", StringComparison.OrdinalIgnoreCase) ? "MONOLITH-" : "Lycoris-").Length..];
                if (!SemVer.TryParse(text, out var v) || v <= current) TryDelete(f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); Log.Info("Удалён остаток обновления: {0}", Path.GetFileName(path)); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
