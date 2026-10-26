using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

/// <summary>A driver package inside a backup (from the INF [Version] section).</summary>
internal sealed record BackupPackage(string Folder, string InfFile, string? Provider, string? Class, string? Version, DateTime? Date);

/// <summary>An existing backup folder.</summary>
internal sealed record DriverBackupInfo(
    string Path,
    string Name,
    DateTime Created,
    BackupKind Kind,
    long SizeBytes,
    int InfCount,
    IReadOnlyList<BackupPackage> Packages,
    string? Note);

internal sealed record PnputilOutcome(int ExitCode, bool Success, bool RebootRequired, string Message, bool Cancelled);

/// <summary>What a restore will install: the manifest's INF files (full paths), or why the backup is refused.</summary>
internal sealed record RestorePlan(IReadOnlyList<string> InfPaths, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Manifest written next to every backup (research §5 recommends one).</summary>
internal sealed class BackupManifest
{
    public DateTime Created { get; set; }
    public string Kind { get; set; } = "full";
    public string Computer { get; set; } = "";
    public string Windows { get; set; } = "";
    public string? Note { get; set; }
    public List<BackupPackage> Packages { get; set; } = [];
}

/// <summary>
/// Driver backup and restore with pnputil (research §5):
///   export:  pnputil /export-driver * &lt;dir&gt;        (third-party oemNN.inf packages only)
///   restore: pnputil /add-driver "&lt;dir&gt;\&lt;package&gt;\&lt;name&gt;.inf" /install for each package listed in
///            monolith-backup.json (installs only when the package outranks the current driver — it never downgrades).
///            INF files that are not in the manifest are never installed; a backup with extras or links is refused.
/// Backups live in %ProgramData%\MONOLITH\DriverBackups. Deleting is limited to direct sub-folders of that root.
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
                result.Add(new DriverBackupInfo(dir, info.Name, created, kind, size, infCount, packages, manifest?.Note));
            }
            catch (Exception ex)
            {
                Log.Warn($"Резервная копия {Path.GetFileName(dir)}: {ex.Message}");
            }
        }
        return result.OrderByDescending(b => b.Created).ThenByDescending(b => b.Name, StringComparer.Ordinal).ToList();
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
            try { entries = d.EnumerateFileSystemInfos(); } catch { continue; }
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

    /// <summary>Reads the [Version] of every top-level INF in each package folder.</summary>
    public static List<BackupPackage> ScanPackages(string backupDir)
    {
        var list = new List<BackupPackage>();
        IEnumerable<string> folders;
        try { folders = Directory.EnumerateDirectories(backupDir); } catch { return list; }
        foreach (var folder in folders)
        {
            try
            {
                if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var inf in Directory.EnumerateFiles(folder, "*.inf"))
                {
                    var v = InfParser.Parse(ReadInfText(inf));
                    list.Add(new BackupPackage(Path.GetFileName(folder), Path.GetFileName(inf), v.Provider, v.Class, v.Version, v.Date));
                }
            }
            catch { }
        }
        return list.OrderBy(p => p.Provider ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(p => p.InfFile, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ReadInfText(string path)
    {
        // INFs are UTF-16 (with BOM) or ANSI; the BOM decides, ANSI falls back to the system code page.
        using var reader = new StreamReader(path, ProcessRunner.GetEncoding(OutputEncoding.Ansi), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static BackupManifest? ReadManifest(string dir)
    {
        try
        {
            var file = Path.Combine(dir, BackupFolders.ManifestFile);
            return File.Exists(file) ? JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(file)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteManifest(string dir, BackupKind kind, string? note)
    {
        try
        {
            var sp = SystemProfile.Current;
            var manifest = new BackupManifest
            {
                Created = DateTime.Now,
                Kind = kind == BackupKind.BeforeUpdate ? "before-update" : "full",
                Computer = Environment.MachineName,
                Windows = sp.OsText,
                Note = note,
                Packages = ScanPackages(dir),
            };
            File.WriteAllText(Path.Combine(dir, BackupFolders.ManifestFile), JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Log.Warn($"Не удалось записать описание резервной копии: {ex.Message}");
        }
    }

    // ───────────── Export ─────────────

    private static string CreateTarget(BackupKind kind)
    {
        Directory.CreateDirectory(Root);
        var name = BackupFolders.NewName(DateTime.Now, kind, n => Directory.Exists(Path.Combine(Root, n)));
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Exports every third-party driver package. Output lines are streamed to <paramref name="onLine"/>.</summary>
    public static async Task<(PnputilOutcome Outcome, string Folder)> ExportAllAsync(Action<string> onLine, Action<int> onPackage, CancellationToken ct)
    {
        Elevation.Demand("резервное копирование драйверов");
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
            await Task.Run(() => WriteManifest(dir, BackupKind.Full, null), CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            // A partial export must never look like a usable backup: remove the folder created moments ago.
            await Task.Run(() => TryDeleteTree(dir), CancellationToken.None).ConfigureAwait(false);
        }
        return (outcome, dir);
    }

    /// <summary>
    /// Before a Windows Update install: exports the packages currently used by the devices that will change
    /// (pnputil /export-driver oemNN.inf &lt;dir&gt;), so the old driver can be put back.
    /// </summary>
    public static async Task<(int Exported, string? Folder)> ExportPackagesAsync(IReadOnlyCollection<string> oemInfs, string note, CancellationToken ct)
    {
        Elevation.Demand("резервное копирование драйверов");
        var infs = oemInfs.Where(SignedDriverInfo.IsOemInf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (infs.Count == 0) return (0, null);
        var dir = await Task.Run(() => CreateTarget(BackupKind.BeforeUpdate), ct).ConfigureAwait(false);
        var ok = 0;
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
            TryDeleteEmpty(dir);
            return (0, null);
        }
        await Task.Run(() => WriteManifest(dir, BackupKind.BeforeUpdate, note), CancellationToken.None).ConfigureAwait(false);
        return (ok, dir);
    }

    // ───────────── Restore ─────────────

    /// <summary>
    /// Checks a backup before restoring it (research §5; review finding #3): the folder must be a MONOLITH backup
    /// owned by Administrators/SYSTEM, the machine folder must not have been writable by other users, and every
    /// INF file must be one listed in the manifest. Returns the INFs to install.
    /// </summary>
    public static RestorePlan PlanRestore(string backupDir)
    {
        if (!BackupFolders.IsDirectChild(Root, backupDir) || !Directory.Exists(backupDir))
            return new RestorePlan([], "Папка резервной копии не найдена или находится вне хранилища MONOLITH.");
        if (AppPaths.MachineDirWasInsecure)
            return new RestorePlan([], "Папка данных MONOLITH была доступна для записи другим пользователям этого компьютера — " +
                "содержимому старых копий нельзя доверять. Создайте новую резервную копию; восстановление из этой отключено.");
        try
        {
            var info = new DirectoryInfo(backupDir);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || !BackupFolders.TryParse(info.Name, out _, out _))
                return new RestorePlan([], "Это не папка резервной копии MONOLITH.");
            if (!IsTrustedOwner(info))
                return new RestorePlan([], "Владелец папки копии — не администратор системы. Восстановление из неё отключено.");

            var manifest = ReadManifest(backupDir);
            if (manifest is null)
                return new RestorePlan([], "В копии нет описания (monolith-backup.json) — нельзя проверить, что в ней только сохранённые драйверы.");
            if (!TryScanInfs(info, out var onDisk))
                return new RestorePlan([], "В копии есть ссылки на другие папки (junction/symlink) — восстановление отключено.");

            var error = CheckAgainstManifest(manifest.Packages, onDisk, out var toInstall);
            if (error is not null) return new RestorePlan([], error);
            return new RestorePlan(toInstall.Select(rel => Path.Combine(backupDir, rel)).ToList(), null);
        }
        catch (Exception ex)
        {
            return new RestorePlan([], $"Не удалось проверить копию: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure check of the INF files found in a backup (relative paths) against its manifest.
    /// INFs directly in the backup root or at the top of a package folder must all be listed; deeper INFs are part
    /// of a package's own files and are never installed directly. Returns null when it matches, else the reason.
    /// </summary>
    public static string? CheckAgainstManifest(IReadOnlyList<BackupPackage>? manifest, IReadOnlyCollection<string> infsOnDisk,
        out List<string> toInstall)
    {
        toInstall = [];
        if (manifest is not { Count: > 0 }) return "В описании копии нет ни одного пакета драйверов.";
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in manifest)
        {
            if (!IsPlainName(p.Folder) || !IsPlainName(p.InfFile) || !p.InfFile.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                return "Описание копии повреждено (недопустимое имя пакета).";
            var rel = p.Folder + Path.DirectorySeparatorChar + p.InfFile;
            if (expected.Add(rel)) toInstall.Add(rel);
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in infsOnDisk)
        {
            var rel = raw.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var depth = rel.Count(c => c == Path.DirectorySeparatorChar);
            if (depth >= 2) continue; // inside a package's own sub-folder: installed only if its package INF references it
            if (!expected.Contains(rel))
            {
                toInstall.Clear();
                return $"В копии есть драйвер, которого нет в её описании: {rel}. Восстановление отключено — копию могли изменить.";
            }
            present.Add(rel);
        }

        var missing = expected.FirstOrDefault(e => !present.Contains(e));
        if (missing is not null)
        {
            toInstall.Clear();
            return $"В копии не хватает файла {missing} — копия повреждена.";
        }
        return null;
    }

    private static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name != "." && name != ".." && name.IndexOfAny(['\\', '/', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>All *.inf files under the backup as relative paths. False when the tree contains a reparse point.</summary>
    private static bool TryScanInfs(DirectoryInfo root, out List<string> infs)
    {
        infs = [];
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        var stack = new Stack<DirectoryInfo>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            foreach (var e in d.EnumerateFileSystemInfos("*", options))
            {
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (e is DirectoryInfo sub) stack.Push(sub);
                else if (e.Extension.Equals(".inf", StringComparison.OrdinalIgnoreCase))
                    infs.Add(Path.GetRelativePath(root.FullName, e.FullName));
            }
        }
        return true;
    }

    private static bool IsTrustedOwner(DirectoryInfo dir)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
            || owner.IsWellKnown(WellKnownSidType.LocalSystemSid)
            || owner.Value == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"); // TrustedInstaller
    }

    /// <summary>Installs the INFs of <see cref="PlanRestore"/> one by one (never "*.inf /subdirs").</summary>
    public static async Task<PnputilOutcome> RestoreAsync(string backupDir, Action<string> onLine, CancellationToken ct)
    {
        Elevation.Demand("восстановление драйверов");
        var plan = await Task.Run(() => PlanRestore(backupDir), ct).ConfigureAwait(false);
        if (!plan.Ok) return new PnputilOutcome(-1, false, false, plan.Error!, false);

        var codes = new List<int>();
        foreach (var inf in plan.InfPaths)
        {
            if (ct.IsCancellationRequested) return new PnputilOutcome(-1, false, false, "Операция отменена.", true);
            var r = await ProcessRunner.RunAsync(Pnputil, ["/add-driver", inf, "/install"], new ProcessRunOptions
            {
                Encoding = OutputEncoding.Oem,
                Timeout = TimeSpan.FromMinutes(30),
                OnOutput = onLine,
            }, ct).ConfigureAwait(false);
            if (r.Cancelled) return new PnputilOutcome(r.ExitCode, false, false, "Операция отменена.", true);
            if (r.TimedOut) return new PnputilOutcome(r.ExitCode, false, false, "pnputil не завершился за 30 минут.", false);
            codes.Add(r.ExitCode);
        }
        return CombineRestore(codes);
    }

    /// <summary>One outcome for several per-package pnputil runs.</summary>
    public static PnputilOutcome CombineRestore(IReadOnlyList<int> exitCodes)
    {
        if (exitCodes.Count == 0) return new PnputilOutcome(-1, false, false, "В копии нет пакетов для восстановления.", false);
        var outcomes = exitCodes.Select(c => DescribeExit(c, export: false)).ToList();
        var failed = outcomes.Where(o => !o.Success).ToList();
        var reboot = outcomes.Any(o => o.RebootRequired);
        if (failed.Count == 0)
        {
            if (reboot) return DescribeExit(ExitRebootRequired, export: false);
            return exitCodes.Any(c => c == 0) ? DescribeExit(0, export: false) : DescribeExit(ExitNothingToUpdate, export: false);
        }
        var message = $"Восстановлено пакетов: {outcomes.Count - failed.Count} из {outcomes.Count}. " +
                      $"Не удалось: {failed.Count} — {failed[^1].Message}" + (reboot ? " Для завершения нужна перезагрузка." : "");
        return new PnputilOutcome(failed[^1].ExitCode, false, reboot, message, false);
    }

    // ───────────── Delete ─────────────

    /// <summary>Deletes a backup folder. Only direct children of the backup root; links are removed, never followed.</summary>
    public static Task DeleteAsync(string backupDir) => Task.Run(() =>
    {
        Elevation.Demand("удаление резервной копии драйверов");
        if (!BackupFolders.IsDirectChild(Root, backupDir))
            throw new InvalidOperationException("Удалять можно только папки резервных копий MONOLITH.");
        var info = new DirectoryInfo(backupDir);
        if (!info.Exists) return;
        if (!BackupFolders.TryParse(info.Name, out _, out _))
            throw new InvalidOperationException("Это не папка резервной копии MONOLITH.");
        DeleteTree(info);
        Log.Info($"Резервная копия драйверов удалена: {info.Name}");
    });

    private static void DeleteTree(DirectoryInfo dir) => SafeTree.Delete(dir.FullName);

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

    private static void TryDeleteEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
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

/// <summary>
/// Handle-based, reparse-safe recursive delete (same approach as the cleaner's NativeCleaner): every entry is
/// opened with FILE_FLAG_OPEN_REPARSE_POINT and checked through its handle. A link (junction/symlink) is deleted
/// itself and never followed. A folder is held open without FILE_SHARE_DELETE while its content is removed, so it
/// cannot be renamed or swapped for a link in the middle.
/// </summary>
internal static class SafeTree
{
    private const uint DELETE = 0x00010000;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FILE_ATTRIBUTE_READONLY = 0x1;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const int FileBasicInfo = 0;
    private const int FileDispositionInfo = 4;
    private const int FileAttributeTagInfo = 9;
    private const int FileDispositionInfoEx = 21;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1;
    private const uint FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2;
    private const uint FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;
    private const int MaxDepth = 64;

    /// <summary>Deletes <paramref name="path"/> and everything inside it. Throws IOException on failure.</summary>
    public static void Delete(string path) => Delete(path, 0);

    private static void Delete(string path, int depth)
    {
        if (depth > MaxDepth) throw new IOException($"Слишком глубокая вложенность папок: {path}");
        // A folder is opened without FILE_SHARE_DELETE: nobody else can rename/delete/replace it while we work in it.
        using var h = CreateFileW(ToExtended(path), DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw Fail(path);
        if (!GetFileInformationByHandleEx(h, FileAttributeTagInfo, out FILE_ATTRIBUTE_TAG_INFO tag, Marshal.SizeOf<FILE_ATTRIBUTE_TAG_INFO>()))
            throw Fail(path);

        var isLink = (tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0 || tag.ReparseTag != 0;
        if (!isLink && (tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", options).ToList())
                Delete(child, depth + 1);
        }
        // For a link this removes the link itself (the handle was opened on the reparse point, not its target).
        MarkForDeletion(h, path);
    }

    private static void MarkForDeletion(Microsoft.Win32.SafeHandles.SafeFileHandle h, string path)
    {
        var ex = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())) return;
        ex.Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE;
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>())) return;

        // Older builds / FAT: classic disposition, which refuses read-only entries — clear that attribute first.
        if (GetFileInformationByHandleEx(h, FileBasicInfo, out FILE_BASIC_INFO b, Marshal.SizeOf<FILE_BASIC_INFO>())
            && (b.FileAttributes & FILE_ATTRIBUTE_READONLY) != 0)
        {
            b.FileAttributes &= ~FILE_ATTRIBUTE_READONLY;
            if (b.FileAttributes == 0) b.FileAttributes = FILE_ATTRIBUTE_NORMAL;
            b.CreationTime = b.LastAccessTime = b.LastWriteTime = b.ChangeTime = 0; // 0 = unchanged
            SetFileInformationByHandle(h, FileBasicInfo, ref b, Marshal.SizeOf<FILE_BASIC_INFO>());
        }
        var classic = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        if (!SetFileInformationByHandle(h, FileDispositionInfo, ref classic, Marshal.SizeOf<FILE_DISPOSITION_INFO>()))
            throw Fail(path);
    }

    private static IOException Fail(string path)
    {
        var error = Marshal.GetLastPInvokeError();
        return new IOException($"{Marshal.GetPInvokeErrorMessage(error)} ({path})", error);
    }

    private static string ToExtended(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ? path : @"\\?\" + path;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO { public uint FileAttributes; public uint ReparseTag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO { public byte DeleteFile; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX { public uint Flags; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass,
        out FILE_ATTRIBUTE_TAG_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass,
        out FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass,
        ref FILE_BASIC_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass,
        ref FILE_DISPOSITION_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, int infoClass,
        ref FILE_DISPOSITION_INFO_EX info, int size);
}
