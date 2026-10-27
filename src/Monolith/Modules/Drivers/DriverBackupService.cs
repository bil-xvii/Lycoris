using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

/// <summary>A driver package inside a backup (from the INF [Version] section). <c>Folder</c> is relative to the backup.</summary>
internal sealed record BackupPackage(string Folder, string InfFile, string? Provider, string? Class, string? Version, DateTime? Date);

/// <summary>One file of a backup: path relative to the backup folder, size and SHA-256 (hex, upper case).</summary>
internal sealed record BackupFileEntry(string Path, long Size, string Sha256);

/// <summary>An existing backup folder.</summary>
internal sealed record DriverBackupInfo(
    string Path,
    string Name,
    DateTime Created,
    BackupKind Kind,
    long SizeBytes,
    int InfCount,
    IReadOnlyList<BackupPackage> Packages,
    string? Note,
    /// <summary>Null when the backup carries a MONOLITH integrity list; otherwise why it can never be restored.</summary>
    string? RestoreBlockedReason);

internal sealed record PnputilOutcome(int ExitCode, bool Success, bool RebootRequired, string Message, bool Cancelled);

/// <summary>Result of checking a backup against its manifest right before an install.</summary>
internal sealed record BackupVerification(bool Ok, string? Problem, IReadOnlyList<BackupPackage> Packages, int FileCount, long Bytes)
{
    public static BackupVerification Fail(string problem) => new(false, problem, [], 0, 0);
}

/// <summary>
/// Manifest written into every backup when it is created (research §5). Format 2 adds the full file list with
/// SHA-256 hashes: restore refuses any folder whose files differ from it, so only what MONOLITH exported can ever
/// be installed from a backup.
/// </summary>
internal sealed class BackupManifest
{
    public const int CurrentFormat = 2;

    /// <summary>1 = old manifest without hashes (cannot be verified), 2 = with <see cref="Files"/>.</summary>
    public int Format { get; set; } = 1;
    public DateTime Created { get; set; }
    public string Kind { get; set; } = "full";
    public string Computer { get; set; } = "";
    public string Windows { get; set; } = "";
    public string? Note { get; set; }
    public List<BackupPackage> Packages { get; set; } = [];
    public List<BackupFileEntry> Files { get; set; } = [];
}

/// <summary>
/// Driver backup and restore with pnputil (research §5):
///   export:  pnputil /export-driver * &lt;dir&gt;        (third-party oemNN.inf packages only)
///   restore: pnputil /add-driver "&lt;dir&gt;\*.inf" /subdirs /install   (installs only when the package outranks
///            the current driver — it never downgrades).
/// Backups live in %ProgramData%\MONOLITH\DriverBackups (admin-only write). A restore first proves that the folder is
/// exactly what MONOLITH exported (manifest with SHA-256 of every file, no links, no extra INF). Deleting is limited
/// to direct sub-folders of that root.
/// </summary>
internal static partial class DriverBackupService
{
    public const int ExitRebootRequired = 3010;
    /// <summary>ERROR_NO_MORE_ITEMS: packages were added to the store but no device needed them.</summary>
    public const int ExitNothingToUpdate = 259;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Root => AppPaths.DriverBackupsDir;

    private static string Pnputil => ProcessRunner.Sys("pnputil.exe");

    [GeneratedRegex(@"oem\d+\.inf", RegexOptions.IgnoreCase)]
    private static partial Regex OemInfRegex();

    private static readonly EnumerationOptions AllEntries = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    // ───────────── Listing ─────────────

    public static List<DriverBackupInfo> List()
    {
        var result = new List<DriverBackupInfo>();
        if (!Directory.Exists(Root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(Root))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!BackupFolders.TryParse(info.Name, out var created, out var kind)) continue;
                var (size, infCount) = Measure(info);
                var manifest = ReadManifest(dir);
                var packages = manifest?.Packages is { Count: > 0 } m ? m : ScanPackages(dir);
                result.Add(new DriverBackupInfo(dir, info.Name, created, kind, size, infCount, packages, manifest?.Note,
                    RestoreBlockedReason(manifest)));
            }
            catch (Exception ex)
            {
                Log.Warn($"Резервная копия {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        return result.OrderByDescending(b => b.Created).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Why a backup can never be restored (checked again, file by file, right before the install).</summary>
    internal static string? RestoreBlockedReason(BackupManifest? manifest)
    {
        if (AppPaths.MachineDirWasInsecure)
            return "Папка данных MONOLITH была доступна для записи обычным пользователям — содержимому копий нельзя доверять. Создайте новую копию.";
        if (manifest is null)
            return "В папке нет описи MONOLITH — восстановление из неё недоступно.";
        if (manifest.Format < BackupManifest.CurrentFormat || manifest.Files.Count == 0)
            return "Копия создана до появления проверки целостности — MONOLITH не может подтвердить её содержимое. Создайте новую копию.";
        return null;
    }

    /// <summary>Total size and number of .inf files (reparse points are never followed).</summary>
    private static (long Size, int InfCount) Measure(DirectoryInfo root)
    {
        long size = 0;
        var infs = 0;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos("*", AllEntries).ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (e is DirectoryInfo sub) stack.Push(sub);
                else if (e is FileInfo f)
                {
                    size += f.Length;
                    if (f.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase)) infs++;
                }
            }
        }
        return (size, infs);
    }

    /// <summary>
    /// Reads the [Version] of every INF in the backup, at any depth — the same set "pnputil /add-driver *.inf /subdirs"
    /// would pick up. Links are never followed.
    /// </summary>
    public static List<BackupPackage> ScanPackages(string backupDir)
    {
        var list = new List<BackupPackage>();
        var root = new DirectoryInfo(backupDir);
        if (!root.Exists) return list;
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = d.EnumerateFileSystemInfos("*", AllEntries).ToList(); } catch { continue; }
            foreach (var e in entries)
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (e is DirectoryInfo sub)
                {
                    stack.Push(sub);
                    continue;
                }
                if (!e.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var v = InfParser.Parse(ReadInfText(e.FullName));
                    var folder = Path.GetRelativePath(root.FullName, d.FullName);
                    list.Add(new BackupPackage(folder == "." ? "" : folder, e.Name, v.Provider, v.Class, v.Version, v.Date));
                }
                catch (Exception ex)
                {
                    Log.Info($"INF {e.Name} не прочитан: {ex.Message}");
                }
            }
        }
        return list.OrderBy(p => p.Provider ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.InfFile, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadInfText(string path)
    {
        // INFs are UTF-16 (with BOM) or ANSI; the BOM decides, ANSI falls back to the system code page.
        using var reader = new StreamReader(path, ProcessRunner.GetEncoding(OutputEncoding.Ansi), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    internal static BackupManifest? ReadManifest(string dir)
    {
        try
        {
            var file = Path.Combine(dir, BackupFolders.ManifestFile);
            if (!File.Exists(file) || new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            return JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file));
        }
        catch
        {
            return null;
        }
    }

    // ───────────── Manifest (integrity list) ─────────────

    /// <summary>
    /// Builds the manifest of a finished export: package list plus every file's size and SHA-256.
    /// Throws when the folder contains a link (an export never creates one).
    /// </summary>
    internal static BackupManifest CreateManifest(string dir, BackupKind kind, string? note, string computer, string windows) => new()
    {
        Format = BackupManifest.CurrentFormat,
        Created = DateTime.Now,
        Kind = kind == BackupKind.BeforeUpdate ? "before-update" : "full",
        Computer = computer,
        Windows = windows,
        Note = note,
        Packages = ScanPackages(dir),
        Files = HashFiles(dir),
    };

    internal static void SaveManifest(string dir, BackupManifest manifest)
    {
        var file = Path.Combine(dir, BackupFolders.ManifestFile);
        File.WriteAllText(file, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
    }

    /// <summary>Every file of the backup (except the manifest), sorted by path. Links make the backup invalid.</summary>
    internal static List<BackupFileEntry> HashFiles(string dir)
    {
        var root = new DirectoryInfo(dir);
        var files = new List<BackupFileEntry>();
        foreach (var f in EnumerateBackupFiles(root))
            files.Add(new BackupFileEntry(RelativePath(root, f), f.Length, HashFile(f.FullName)));
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>All files under <paramref name="root"/> except the manifest. Throws on any reparse point.</summary>
    private static IEnumerable<FileInfo> EnumerateBackupFiles(DirectoryInfo root)
    {
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var e in d.EnumerateFileSystemInfos("*", AllEntries).ToList())
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException($"в копии есть ссылка «{RelativePath(root, e)}» — MONOLITH такие не создаёт");
                if (e is DirectoryInfo sub) stack.Push(sub);
                else if (e is FileInfo f && !(ReferenceEquals(d, root) && f.Name.Equals(BackupFolders.ManifestFile, StringComparison.OrdinalIgnoreCase)))
                    yield return f;
            }
        }
    }

    private static string RelativePath(DirectoryInfo root, FileSystemInfo e) => Path.GetRelativePath(root.FullName, e.FullName);

    private static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>
    /// Proves that <paramref name="dir"/> is exactly what MONOLITH exported: a format-2 manifest, the same set of files
    /// (nothing added, nothing missing, no links), the same sizes and SHA-256, and every INF listed as a package.
    /// </summary>
    internal static BackupVerification Verify(string dir)
    {
        try
        {
            var root = new DirectoryInfo(dir);
            if (!root.Exists) return BackupVerification.Fail("папка копии не найдена");
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return BackupVerification.Fail("папка копии — это ссылка");
            var manifest = ReadManifest(dir);
            if (manifest is null) return BackupVerification.Fail("в копии нет описи MONOLITH (monolith-backup.json)");
            if (manifest.Format < BackupManifest.CurrentFormat || manifest.Files.Count == 0)
                return BackupVerification.Fail("копия создана до появления проверки целостности — её содержимое нельзя подтвердить");

            var expected = new Dictionary<string, BackupFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                if (!expected.TryAdd(NormalizeRelative(f.Path), f))
                    return BackupVerification.Fail($"опись повреждена: файл «{f.Path}» указан дважды");
            }

            var onDisk = EnumerateBackupFiles(root).ToList();
            foreach (var f in onDisk)
            {
                if (!expected.ContainsKey(NormalizeRelative(RelativePath(root, f))))
                    return BackupVerification.Fail($"лишний файл «{RelativePath(root, f)}» — его нет в описи");
            }
            var present = new HashSet<string>(onDisk.Select(f => NormalizeRelative(RelativePath(root, f))), StringComparer.OrdinalIgnoreCase);
            foreach (var key in expected.Keys)
            {
                if (!present.Contains(key)) return BackupVerification.Fail($"нет файла «{expected[key].Path}» из описи");
            }

            var listedInfs = new HashSet<string>(manifest.Packages.Select(p => NormalizeRelative(Path.Combine(p.Folder, p.InfFile))), StringComparer.OrdinalIgnoreCase);
            foreach (var inf in listedInfs)
            {
                if (!expected.ContainsKey(inf)) return BackupVerification.Fail($"опись повреждена: драйвер «{inf}» указан, но его файла нет в списке");
            }
            long bytes = 0;
            foreach (var f in onDisk)
            {
                var rel = RelativePath(root, f);
                var entry = expected[NormalizeRelative(rel)];
                if (f.Length != entry.Size || !string.Equals(HashFile(f.FullName), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    return BackupVerification.Fail($"файл «{rel}» изменён после создания копии");
                if (f.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase) && !listedInfs.Contains(NormalizeRelative(rel)))
                    return BackupVerification.Fail($"драйвер «{rel}» не указан в описи");
                bytes += f.Length;
            }
            if (listedInfs.Count == 0) return BackupVerification.Fail("в копии нет драйверов");
            return new BackupVerification(true, null, manifest.Packages, onDisk.Count, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return BackupVerification.Fail(ex.Message);
        }
    }

    private static string NormalizeRelative(string path) => path.Replace('/', '\\').Trim('\\');

    /// <summary>
    /// Writes the integrity manifest. A backup without one can never be restored, so a failure here makes the
    /// whole export fail (the caller removes the folder).
    /// </summary>
    private static void WriteManifest(string dir, BackupKind kind, string? note)
    {
        var sp = SystemProfile.Current;
        SaveManifest(dir, CreateManifest(dir, kind, note, Environment.MachineName, sp.OsText));
    }

    // ───────────── Export ─────────────

    private static string CreateTarget(BackupKind kind)
    {
        if (!AppPaths.TryEnsureDir(Root))
            throw new InvalidOperationException("Не удалось создать папку резервных копий — нужны права администратора.");
        if (new DirectoryInfo(Root).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("Папка резервных копий подменена ссылкой — операция отменена.");
        var name = BackupFolders.NewName(DateTime.Now, kind, n => Directory.Exists(Path.Combine(Root, n)));
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Exports every third-party driver package. Output lines are streamed to <paramref name="onLine"/>.</summary>
    public static async Task<(PnputilOutcome Outcome, string Folder)> ExportAllAsync(Action<string> onLine, Action<int> onPackage, CancellationToken ct)
    {
        Elevation.Demand("резервное копирование драйверов");
        SafetyLock.ThrowIfLocked("резервное копирование драйверов");
        var dir = await Task.Run(() => CreateTarget(BackupKind.Full), ct).ConfigureAwait(false);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var r = await ProcessRunner.RunAsync(Pnputil, ["/export-driver", "*", dir], new ProcessRunOptions
        {
            Encoding = OutputEncoding.Oem,
            Timeout = TimeSpan.FromMinutes(30),
            OnOutput = line =>
            {
                onLine(line);
                foreach (Match m in OemInfRegex().Matches(line))
                    if (seen.Add(m.Value)) onPackage(seen.Count);
            },
        }, ct).ConfigureAwait(false);

        var outcome = Describe(r, export: true);
        if (outcome.Success)
        {
            onLine("Опись копии: SHA-256 каждого файла…");
            try
            {
                await Task.Run(() => WriteManifest(dir, BackupKind.Full, null), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Не удалось записать опись резервной копии", ex);
                outcome = new PnputilOutcome(outcome.ExitCode, false, false,
                    $"Драйверы скопированы, но опись копии не записана ({ex.Message}). Без описи восстановление невозможно — неполная копия удалена.", false);
            }
        }
        // A partial or unverifiable export must never look like a usable backup: remove the folder created moments ago.
        if (!outcome.Success)
            await Task.Run(() => TryDeleteTree(dir), CancellationToken.None).ConfigureAwait(false);
        return (outcome, dir);
    }

    /// <summary>
    /// Before a Windows Update install: exports the packages currently used by the devices that will change
    /// (pnputil /export-driver oemNN.inf &lt;dir&gt;), so the driver is kept if it ever disappears.
    /// </summary>
    public static async Task<(int Exported, string? Folder)> ExportPackagesAsync(IReadOnlyCollection<string> oemInfs, string note, CancellationToken ct)
    {
        Elevation.Demand("резервное копирование драйверов");
        SafetyLock.ThrowIfLocked("резервное копирование драйверов");
        var infs = oemInfs.Where(SignedDriverInfo.IsOemInf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (infs.Count == 0) return (0, null);
        var dir = await Task.Run(() => CreateTarget(BackupKind.BeforeUpdate), ct).ConfigureAwait(false);
        var ok = 0;
        try
        {
            foreach (var inf in infs)
            {
                ct.ThrowIfCancellationRequested();
                var r = await ProcessRunner.RunAsync(Pnputil, ["/export-driver", inf, dir],
                    new ProcessRunOptions { Encoding = OutputEncoding.Oem, Timeout = TimeSpan.FromMinutes(10) }, ct).ConfigureAwait(false);
                if (r.Success) ok++;
                else Log.Warn($"Не удалось сохранить {inf}: код {r.ExitCode}");
            }
            if (ok == 0)
            {
                TryDeleteTree(dir);
                return (0, null);
            }
            await Task.Run(() => WriteManifest(dir, BackupKind.BeforeUpdate, note), CancellationToken.None).ConfigureAwait(false);
            return (ok, dir);
        }
        catch
        {
            await Task.Run(() => TryDeleteTree(dir), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    // ───────────── Restore ─────────────

    /// <summary>
    /// Installs the drivers of a backup. First proves the folder is exactly what MONOLITH exported
    /// (<see cref="Verify"/>); then <paramref name="beforeInstall"/> runs (restore point — it may cancel by returning
    /// false); then pnputil adds the verified packages. The folder is admin-only, so nobody without admin rights can
    /// change it between the check and the install.
    /// </summary>
    public static async Task<(PnputilOutcome? Outcome, BackupVerification Verification)> RestoreAsync(
        string backupDir, Action<string> onLine, Func<BackupVerification, Task<bool>> beforeInstall, CancellationToken ct)
    {
        Elevation.Demand("восстановление драйверов");
        SafetyLock.ThrowIfLocked("восстановление драйверов");
        if (!BackupFolders.IsDirectChild(Root, backupDir) || !Directory.Exists(backupDir) ||
            !BackupFolders.TryParse(Path.GetFileName(Path.TrimEndingDirectorySeparator(backupDir)), out _, out _))
            throw new InvalidOperationException("Папка резервной копии не найдена или находится вне хранилища MONOLITH.");
        if (AppPaths.MachineDirWasInsecure)
            return (null, BackupVerification.Fail("папка данных MONOLITH была доступна для записи обычным пользователям — копиям нельзя доверять"));

        var verification = await Task.Run(() => Verify(backupDir), ct).ConfigureAwait(false);
        if (!verification.Ok)
        {
            Log.Warn($"Резервная копия {Path.GetFileName(backupDir)} не прошла проверку: {verification.Problem}");
            return (null, verification);
        }
        onLine($"Проверка целостности: {verification.FileCount} файлов, {SystemProfile.FormatBytes(verification.Bytes)} — совпадает с описью.");
        if (!await beforeInstall(verification).ConfigureAwait(false)) return (null, verification);

        var pattern = Path.Combine(backupDir, "*.inf");
        var r = await ProcessRunner.RunAsync(Pnputil, ["/add-driver", pattern, "/subdirs", "/install"], new ProcessRunOptions
        {
            Encoding = OutputEncoding.Oem,
            Timeout = TimeSpan.FromMinutes(30),
            OnOutput = onLine,
        }, ct).ConfigureAwait(false);
        return (Describe(r, export: false), verification);
    }

    // ───────────── Delete ─────────────

    /// <summary>Deletes a backup folder. Only direct children of the backup root; links are removed, never followed.</summary>
    public static Task DeleteAsync(string backupDir) => Task.Run(() =>
    {
        Elevation.Demand("удаление резервной копии драйверов");
        SafetyLock.ThrowIfLocked("удаление резервной копии драйверов");
        if (!BackupFolders.IsDirectChild(Root, backupDir))
            throw new InvalidOperationException("Удалять можно только папки резервных копий MONOLITH.");
        var info = new DirectoryInfo(backupDir);
        if (!info.Exists) return;
        if (!BackupFolders.TryParse(info.Name, out _, out _))
            throw new InvalidOperationException("Это не папка резервной копии MONOLITH.");
        DeleteTree(info);
        Log.Info($"Резервная копия драйверов удалена: {info.Name}");
    });

    /// <summary>
    /// Recursive delete that never follows a link: each entry's attributes come from the same enumeration that
    /// lists it, a link is removed as a link, and only real folders are descended into.
    /// </summary>
    private static void DeleteTree(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(false); // removes the link itself, not its target
            return;
        }
        foreach (var e in dir.EnumerateFileSystemInfos("*", AllEntries).ToList())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (e is DirectoryInfo link) link.Delete(false);
                else e.Delete();
                continue;
            }
            if (e is DirectoryInfo sub) DeleteTree(sub);
            else
            {
                if (e.Attributes.HasFlag(FileAttributes.ReadOnly)) e.Attributes &= ~FileAttributes.ReadOnly;
                e.Delete();
            }
        }
        dir.Delete(false);
    }

    private static void TryDeleteTree(string dir)
    {
        try
        {
            if (BackupFolders.IsDirectChild(Root, dir) && Directory.Exists(dir)) DeleteTree(new DirectoryInfo(dir));
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось удалить неполную копию {Path.GetFileName(dir)}: {ex.Message}");
        }
    }

    // ───────────── Results ─────────────

    public static PnputilOutcome Describe(ProcessResult r, bool export)
    {
        if (r.Cancelled) return new PnputilOutcome(r.ExitCode, false, false, "Операция отменена.", true);
        if (r.TimedOut) return new PnputilOutcome(r.ExitCode, false, false, "pnputil не завершился за 30 минут.", false);
        return DescribeExit(r.ExitCode, export);
    }

    public static PnputilOutcome DescribeExit(int exitCode, bool export) => exitCode switch
    {
        0 => new PnputilOutcome(0, true, false, export ? "Драйверы сохранены." : "Драйверы добавлены и установлены.", false),
        ExitRebootRequired => new PnputilOutcome(exitCode, true, true, "Готово. Для завершения нужна перезагрузка.", false),
        ExitNothingToUpdate when !export => new PnputilOutcome(exitCode, true, false,
            "Пакеты добавлены в хранилище драйверов, но устройства не обновлены: текущие драйверы такие же или новее.", false),
        5 => new PnputilOutcome(exitCode, false, false, "Отказано в доступе — нужны права администратора.", false),
        -1 => new PnputilOutcome(exitCode, false, false, "Не удалось запустить pnputil.", false),
        _ => new PnputilOutcome(exitCode, false, false, $"pnputil завершился с кодом {exitCode}.", false),
    };
}
