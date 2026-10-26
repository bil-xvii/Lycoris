using System.IO;
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
///   restore: pnputil /add-driver "&lt;dir&gt;\*.inf" /subdirs /install   (installs only when the package outranks
///            the current driver — it never downgrades).
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

    public static async Task<PnputilOutcome> RestoreAsync(string backupDir, Action<string> onLine, CancellationToken ct)
    {
        Elevation.Demand("восстановление драйверов");
        if (!BackupFolders.IsDirectChild(Root, backupDir) || !Directory.Exists(backupDir))
            throw new InvalidOperationException("Папка резервной копии не найдена или находится вне хранилища MONOLITH.");
        var pattern = Path.Combine(backupDir, "*.inf");
        var r = await ProcessRunner.RunAsync(Pnputil, ["/add-driver", pattern, "/subdirs", "/install"], new ProcessRunOptions
        {
            Encoding = OutputEncoding.Oem,
            Timeout = TimeSpan.FromMinutes(30),
            OnOutput = onLine,
        }, ct).ConfigureAwait(false);
        return Describe(r, export: false);
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

    private static void DeleteTree(DirectoryInfo dir)
    {
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            dir.Delete(false); // removes the link itself, not its target
            return;
        }
        foreach (var e in dir.EnumerateFileSystemInfos())
        {
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
