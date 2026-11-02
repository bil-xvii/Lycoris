using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Monolith.Core.Updates;

public enum ProcessWatch
{
    /// <summary>The process could not be started.</summary>
    StartFailed,
    /// <summary>Still running after the watch time: it started fine.</summary>
    StillRunning,
    /// <summary>Ended within the watch time with exit code 0 (e.g. handed over to another running copy).</summary>
    ExitedOk,
    /// <summary>Ended within the watch time with a non-zero code: a crash.</summary>
    ExitedFailed,
}

public enum DriveKindInfo { Fixed, Removable, Network, CdRom, Unknown }

/// <summary>Everything the update replacement touches, so the logic is tested with a fake file system.</summary>
public interface IUpdateEnvironment
{
    bool FileExists(string path);
    long FileLength(string path);
    void Move(string from, string to);
    void Copy(string from, string to);
    void Delete(string path);
    /// <summary>True when the process is gone (or never existed); false after the timeout.</summary>
    bool WaitForExit(int pid, TimeSpan timeout);
    ProcessWatch StartAndWatch(string exe, TimeSpan watch, out int exitCode);
    /// <summary>Starts a program and does not wait (the old version after a rollback).</summary>
    void StartDetached(string exe);
    bool CanWriteFolder(string folder);
    DriveKindInfo DriveKind(string path);
    void Delay(TimeSpan time);

    // ── Security checks of the headless updater (default: allowed, so file-system fakes stay simple; the real one checks) ──

    /// <summary>True when <paramref name="path"/> is a MONOLITH program (not an arbitrary .exe the updater could be pointed at).</summary>
    bool IsMonolithExe(string path) => true;

    /// <summary>True when no process <paramref name="pid"/> runs, or the one that runs is <paramref name="exe"/> (a forged pid names something else).</summary>
    bool PidRunsExe(int pid, string exe) => true;

    /// <summary>True when the program <paramref name="candidate"/> has a strictly newer version than <paramref name="current"/> (no downgrade, no re-install of the same).</summary>
    bool IsNewerThan(string candidate, string current) => true;

    /// <summary>
    /// Opens <paramref name="path"/> so that nobody can write, rename or delete it while the returned handle lives, hashes it and
    /// reports whether it is the file with <paramref name="sha256"/>. Default: no check (identical = true, no handle).
    /// </summary>
    IDisposable? HoldIfIdentical(string path, string sha256, out bool identical) { identical = true; return null; }
}

public enum UpdateRoute
{
    /// <summary>The folder of the exe is writable: stage next to it, no UAC.</summary>
    InPlace,
    /// <summary>The folder needs rights (Program Files): the updater itself starts elevated.</summary>
    Elevated,
    /// <summary>This copy cannot be updated in place; <see cref="UpdatePlan.Reason"/> says why.</summary>
    Unsupported,
}

public sealed record UpdatePlan(UpdateRoute Route, string ExePath, string? Reason)
{
    public string BackupPath => ExePath + ".old";
    public string StagePath => ExePath + ".new";
}

public enum ApplyOutcome
{
    /// <summary>The new version was started and runs.</summary>
    Updated,
    /// <summary>The new version did not start; the previous exe is back (and was started again).</summary>
    RolledBack,
    /// <summary>Nothing was changed (the old process did not close, the old file is locked…).</summary>
    NothingChanged,
    /// <summary>The new version failed AND the old one could not be put back — the .old file is the way out.</summary>
    RollbackFailed,
}

public static class UpdateApplier
{
    public static readonly TimeSpan OldProcessTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan StartWatch = TimeSpan.FromSeconds(10);

    /// <summary>Decides where the new exe is staged and with which rights the updater runs. Pure given the environment.</summary>
    public static UpdatePlan Plan(IUpdateEnvironment env, string exePath, bool elevated)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !env.FileExists(exePath))
            return new UpdatePlan(UpdateRoute.Unsupported, exePath, Loc.T("Не найден файл запущенной программы — обновить на месте нельзя."));
        var dir = Path.GetDirectoryName(exePath) ?? "";
        if (Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) || env.FileExists(Path.Combine(dir, "MONOLITH.dll")))
            return new UpdatePlan(UpdateRoute.Unsupported, exePath, Loc.T("Это сборка для разработки (программа состоит из нескольких файлов), а не готовый MONOLITH.exe — обновление на месте не выполняется."));
        switch (env.DriveKind(exePath))
        {
            case DriveKindInfo.Network:
                return new UpdatePlan(UpdateRoute.Unsupported, exePath, Loc.T("MONOLITH запущен с сетевого диска: заменять файл на общем ресурсе небезопасно. Скачайте новую версию вручную со страницы релизов."));
            case DriveKindInfo.CdRom:
                return new UpdatePlan(UpdateRoute.Unsupported, exePath, Loc.T("MONOLITH запущен с диска только для чтения. Скопируйте его на компьютер и запустите оттуда."));
        }
        if (env.CanWriteFolder(dir)) return new UpdatePlan(UpdateRoute.InPlace, exePath, null);
        if (!elevated) return new UpdatePlan(UpdateRoute.Elevated, exePath, null);
        return new UpdatePlan(UpdateRoute.Unsupported, exePath, Loc.T("Папка с программой защищена от записи даже для администратора (диск только для чтения?). Скопируйте MONOLITH.exe в другую папку."));
    }

    /// <summary>
    /// The work of the new exe (started as «--apply-update &lt;old exe&gt; &lt;pid&gt;»): wait for the old process, set the old exe
    /// aside as .old, COPY itself to the old name (never move: it is running), start it and watch it for 10 s. If anything
    /// fails the old exe is put back. The user always ends up with one working MONOLITH.exe.
    /// </summary>
    public static ApplyOutcome Apply(IUpdateEnvironment env, string self, string oldExe, int oldPid)
    {
        var backup = oldExe + ".old";
        Log.Info("Обновление: ждём закрытия прежней копии (PID {0}).", oldPid);
        if (!env.WaitForExit(oldPid, OldProcessTimeout))
        {
            Log.Warn("Обновление отменено: прежняя копия MONOLITH не закрылась за {0} с.", (int)OldProcessTimeout.TotalSeconds);
            return ApplyOutcome.NothingChanged;
        }
        if (!env.FileExists(self) || string.Equals(Path.GetFullPath(self), Path.GetFullPath(oldExe), StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn("Обновление отменено: новый файл не найден или совпадает с текущим.");
            return ApplyOutcome.NothingChanged;
        }

        // 1. The old exe aside (one backup only).
        var hadOld = env.FileExists(oldExe);
        try
        {
            if (env.FileExists(backup)) env.Delete(backup);
            if (hadOld) MoveWithRetry(env, oldExe, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            Log.Warn("Обновление отменено: не удалось отложить прежний файл ({0}). Ничего не изменено.", ex.Message);
            return ApplyOutcome.NothingChanged;
        }

        // 2. The new exe takes its name (a copy: this process keeps running from its own file).
        try
        {
            env.Copy(self, oldExe);
            if (env.FileLength(oldExe) != env.FileLength(self)) throw new IOException("copy is incomplete");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Обновление: не удалось записать новый файл ({0}) — возвращаем прежний.", ex.Message);
            return Rollback(env, oldExe, backup, hadOld, restart: true);
        }

        // 3. Start it and watch for a quick crash.
        var watch = env.StartAndWatch(oldExe, StartWatch, out var code);
        if (watch is ProcessWatch.StillRunning or ProcessWatch.ExitedOk)
        {
            Log.Success("Обновление применено: новая версия запущена.");
            return ApplyOutcome.Updated;
        }
        Log.Warn("Новая версия не запустилась ({0}, код {1}) — возвращаем прежнюю.", watch, code);
        return Rollback(env, oldExe, backup, hadOld, restart: true);
    }

    private static void MoveWithRetry(IUpdateEnvironment env, string from, string to)
    {
        // Antivirus or the shell may hold the just-closed exe for a moment.
        for (var attempt = 0; ; attempt++)
        {
            try { env.Move(from, to); return; }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                env.Delay(TimeSpan.FromMilliseconds(500));
            }
        }
    }

    private static ApplyOutcome Rollback(IUpdateEnvironment env, string oldExe, string backup, bool hadOld, bool restart)
    {
        if (!hadOld) return ApplyOutcome.RollbackFailed; // nothing to restore
        try
        {
            for (var attempt = 0; env.FileExists(oldExe); attempt++)
            {
                try { env.Delete(oldExe); }
                catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException) { env.Delay(TimeSpan.FromMilliseconds(500)); }
            }
            env.Move(backup, oldExe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Откат обновления не удался: {0}. Прежняя версия лежит в файле {1}.", ex.Message, backup);
            return ApplyOutcome.RollbackFailed;
        }
        Log.Info("Прежняя версия возвращена на место.");
        if (restart)
        {
            try { env.StartDetached(oldExe); } catch (Exception ex) { Log.Warn("Не удалось снова запустить прежнюю версию: {0}", ex.Message); }
        }
        return ApplyOutcome.RolledBack;
    }
}

/// <summary>The real file system and processes for <see cref="UpdateApplier"/>.</summary>
public sealed class SystemUpdateEnvironment : IUpdateEnvironment
{
    public bool FileExists(string path) => File.Exists(path);
    public long FileLength(string path) => new FileInfo(path).Length;
    public void Move(string from, string to) => File.Move(from, to);
    public void Copy(string from, string to) => File.Copy(from, to, overwrite: false);
    public void Delete(string path) => File.Delete(path);
    public void Delay(TimeSpan time) => Thread.Sleep(time);

    public bool IsMonolithExe(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return string.Equals(info.ProductName, "MONOLITH", StringComparison.Ordinal) && new FileInfo(path).Length > (1 << 20);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool PidRunsExe(int pid, string exe)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var image = p.MainModule?.FileName;
            return image is null || string.Equals(Path.GetFullPath(image), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return true; }         // no such process: it has already closed
        catch (InvalidOperationException) { return true; } // exited meanwhile
        catch (Win32Exception) { return true; }            // cannot read an elevated process's path: the wait step decides
    }

    public bool IsNewerThan(string candidate, string current)
    {
        var next = VersionOf(candidate);
        if (next is null) return false;
        var now = VersionOf(current);
        return now is null || next.Value > now.Value;
    }

    private static SemVer? VersionOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return SemVer.Parse(info.ProductVersion) ?? SemVer.Parse(info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public IDisposable? HoldIfIdentical(string path, string sha256, out bool identical)
    {
        identical = false;
        FileStream? fs = null;
        try
        {
            // FileShare.Read: while this handle lives nobody can write, rename or delete the file (a running exe can still be renamed otherwise).
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            identical = string.Equals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fs)), sha256, StringComparison.OrdinalIgnoreCase);
            if (identical) return fs;
            fs.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fs?.Dispose();
            return null;
        }
    }

    public bool WaitForExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(timeout);
        }
        catch (ArgumentException) { return true; }          // no such process
        catch (InvalidOperationException) { return true; }  // exited meanwhile
        catch (Win32Exception) { return false; }            // cannot wait: do not touch the file
    }

    public ProcessWatch StartAndWatch(string exe, TimeSpan watch, out int exitCode)
    {
        exitCode = 0;
        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            using var p = Process.Start(psi);
            if (p is null) return ProcessWatch.StartFailed;
            if (!p.WaitForExit(watch)) return ProcessWatch.StillRunning;
            exitCode = p.ExitCode;
            return exitCode == 0 ? ProcessWatch.ExitedOk : ProcessWatch.ExitedFailed;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("Не удалось запустить {0}: {1}", Path.GetFileName(exe), ex.Message);
            return ProcessWatch.StartFailed;
        }
    }

    public void StartDetached(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
    }

    public bool CanWriteFolder(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".monolith-write-test-" + Environment.ProcessId + ".tmp");
            using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    public DriveKindInfo DriveKind(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return DriveKindInfo.Network;
            return new DriveInfo(Path.GetPathRoot(full)!).DriveType switch
            {
                DriveType.Fixed => DriveKindInfo.Fixed,
                DriveType.Removable => DriveKindInfo.Removable,
                DriveType.Network => DriveKindInfo.Network,
                DriveType.CDRom => DriveKindInfo.CdRom,
                _ => DriveKindInfo.Unknown,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return DriveKindInfo.Unknown; }
    }
}

/// <summary>«MONOLITH.exe --apply-update &lt;old exe&gt; &lt;pid&gt;»: the headless mode of the NEW exe (see <see cref="UpdateApplier"/>).</summary>
public static class UpdateApplyMode
{
    public const string Switch = "--apply-update";

    /// <summary>
    /// Exit codes: 0 updated, 2 bad arguments, 10 rolled back, 11 nothing changed, 12 rollback failed, 13 refused by a security check.
    /// Arguments: <c>--apply-update &lt;old exe&gt; &lt;pid&gt; [&lt;sha256 of this updater&gt;]</c>. Anybody can start this mode with any arguments (and an
    /// elevated copy would then act with administrator rights), so before touching a file it checks that (1) the target is a MONOLITH
    /// program outside the Windows folders, (2) the pid is not a forged one (it is the target's own process, or gone), (3) when a hash is
    /// given — and an elevated updater REQUIRES it — this very file is the one the old copy verified, and it is held against swapping
    /// while the files are exchanged, (4) the new program is strictly newer (no downgrade).
    /// </summary>
    public static int Run(string[] args, IUpdateEnvironment? env = null, string? self = null, bool? elevated = null)
    {
        if (args.Length < 3 || !int.TryParse(args[2], out var pid) || pid <= 0) return 2;
        string oldExe;
        try { oldExe = Path.GetFullPath(args[1]); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return 2; }
        if (!oldExe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return 2;
        var expectedHash = args.Length > 3 ? args[3] : null;
        if (expectedHash is not null && !UpdateVerifier.IsSha256Hex(expectedHash)) return 2;

        env ??= new SystemUpdateEnvironment();
        self ??= AppPaths.ExecutablePath;
        if (IsProtectedFolder(oldExe) || !env.IsMonolithExe(oldExe) || !env.PidRunsExe(pid, oldExe))
        {
            Log.Warn("Обновление отклонено: целевой файл не похож на MONOLITH или номер процесса не его.");
            return 13;
        }
        if ((elevated ?? Elevation.IsElevated) && expectedHash is null)
        {
            Log.Warn("Обновление отклонено: программа обновления с правами администратора запущена без контрольной суммы.");
            return 13;
        }
        IDisposable? held = null;
        try
        {
            if (expectedHash is not null)
            {
                held = env.HoldIfIdentical(self, expectedHash, out var identical);
                if (!identical)
                {
                    Log.Warn("Обновление отклонено: файл программы обновления не совпадает с проверенным.");
                    return 13;
                }
            }
            if (!env.IsNewerThan(self, oldExe))
            {
                Log.Warn("Обновление отклонено: новая программа не новее установленной.");
                return 13;
            }
            return ExitCodeOf(UpdateApplier.Apply(env, self, oldExe, pid));
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>The Windows folder and everything below it: never a target of the updater (pure).</summary>
    internal static bool IsProtectedFolder(string path)
    {
        try
        {
            var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static int ExitCodeOf(ApplyOutcome outcome)
    {
        return outcome switch
        {
            ApplyOutcome.Updated => 0,
            ApplyOutcome.RolledBack => 10,
            ApplyOutcome.NothingChanged => 11,
            _ => 12,
        };
    }

    /// <summary>The text for the one message box a failed update shows (the old window is already gone).</summary>
    public static string? FailureText(int exitCode, string oldExe) => exitCode switch
    {
        10 => Loc.T("Новая версия MONOLITH не запустилась. Прежняя версия возвращена и запущена снова — ничего не потеряно."),
        11 => Loc.T("Обновление не выполнено: прежняя копия MONOLITH не закрылась или её файл занят. Ничего не изменено — запустите MONOLITH и повторите обновление."),
        12 => Loc.Tf("Новая версия MONOLITH не запустилась, и прежнюю не удалось вернуть автоматически. Она сохранена рядом: {0}.old — переименуйте её в MONOLITH.exe.", oldExe),
        13 => Loc.T("Обновление отклонено проверкой безопасности: файл обновления не совпал с проверенным, либо целью указана не программа MONOLITH. Ничего не изменено."),
        _ => null,
    };
}
