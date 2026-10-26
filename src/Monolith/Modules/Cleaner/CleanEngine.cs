using System.IO;
using System.IO.Enumeration;

namespace Monolith.Modules.Cleaner;

/// <summary>One folder the cleaner may empty, with its rules. Built by <see cref="CleanCatalog"/>.</summary>
public sealed class CleanTarget
{
    /// <summary>Folder to walk. It is never deleted itself.</summary>
    public required string Base { get; init; }
    /// <summary>Human readable origin ("Edge · Default · Code Cache") for logs and reports.</summary>
    public string Label { get; init; } = "";
    /// <summary>Only files whose max(LastWrite, Creation) is older than this are taken. LastAccess is never used.</summary>
    public TimeSpan MinAge { get; init; }
    public bool Recurse { get; init; } = true;
    /// <summary>File name patterns ("*.dmp", "CbsPersist_*.log"); null = every file.</summary>
    public IReadOnlyList<string>? Patterns { get; init; }
    /// <summary>Extra file filter (name, size in bytes).</summary>
    public Func<string, long, bool>? Filter { get; init; }
    /// <summary>Paths relative to the base (files or folders) that are skipped with everything inside them.</summary>
    public IReadOnlyCollection<string>? Exclude { get; init; }
    /// <summary>When set, only these first-level sub-folders are walked; files directly in the base are ignored.</summary>
    public IReadOnlyCollection<string>? OnlyTopDirs { get; init; }
    /// <summary>Always keep the N newest matching files (minidumps), regardless of age.</summary>
    public int KeepNewest { get; init; }
    /// <summary>Remove sub-folders that are empty after the files are gone (never the base itself).</summary>
    public bool RemoveEmptyDirs { get; init; } = true;
    /// <summary>
    /// Exactly one named file directly in <see cref="Base"/> (e.g. MEMORY.DMP in %SystemRoot%). The base folder may
    /// then be a protected folder, because nothing else in it is listed or touched.
    /// </summary>
    public bool SingleFile { get; init; }
    /// <summary>The target can be cleaned only with administrator rights.</summary>
    public bool RequiresAdmin { get; init; }
    /// <summary>Application that must be closed (browser, Steam, Discord…); null = none.</summary>
    public AppOwner? Owner { get; init; }
}

/// <summary>A file found by the analysis, with the exact metadata the cleaning pass re-checks.</summary>
public readonly record struct FileCandidate(string Path, long Size, long LastWriteFileTime, long CreationFileTime);

/// <summary>A sub-folder found by the analysis; removed only if it is empty after cleaning.</summary>
public readonly record struct DirCandidate(string Path, long CreationFileTime, int Depth);

/// <summary>Dry-run result for one target: what would be deleted. Nothing is deleted while this is built.</summary>
public sealed class TargetManifest
{
    public TargetManifest(CleanTarget target) => Target = target;

    public CleanTarget Target { get; }
    public string? CanonicalBase { get; set; }
    public long CutoffFileTime { get; set; }
    public List<FileCandidate> Files { get; } = [];
    public List<DirCandidate> Dirs { get; } = [];
    public long Bytes { get; set; }
    /// <summary>Base folder does not exist on this PC (not an error).</summary>
    public bool Missing { get; set; }
    /// <summary>The base exists but could not be listed (usually: needs administrator rights).</summary>
    public bool AccessDenied { get; set; }
    /// <summary>Refused by the path guard; nothing in this target will be touched.</summary>
    public GuardVerdict? Refused { get; set; }
    /// <summary>Matching files that are still too new for the age filter.</summary>
    public int TooNew { get; set; }
    public int Count => Files.Count;
    public bool HasWork => Files.Count > 0 || Dirs.Count > 0;
}

/// <summary>What one cleaning pass did.</summary>
public sealed class CleanTally
{
    public int Deleted { get; set; }
    public long FreedBytes { get; set; }
    public long DeletedLogicalBytes { get; set; }
    public int InUse { get; set; }
    public int Denied { get; set; }
    public int Changed { get; set; }
    public int Gone { get; set; }
    public int Refused { get; set; }
    public int Errors { get; set; }
    public int DirsRemoved { get; set; }
    public bool Cancelled { get; set; }
    public string? BaseProblem { get; set; }
    public List<string> ErrorSamples { get; } = [];
    public List<(string Path, long Size)> DeletedFiles { get; } = [];

    public void Add(CleanTally other)
    {
        Deleted += other.Deleted;
        FreedBytes += other.FreedBytes;
        DeletedLogicalBytes += other.DeletedLogicalBytes;
        InUse += other.InUse;
        Denied += other.Denied;
        Changed += other.Changed;
        Gone += other.Gone;
        Refused += other.Refused;
        Errors += other.Errors;
        DirsRemoved += other.DirsRemoved;
        Cancelled |= other.Cancelled;
        foreach (var e in other.ErrorSamples)
            if (ErrorSamples.Count < 20) ErrorSamples.Add(e);
        foreach (var f in other.DeletedFiles)
            if (DeletedFiles.Count < CleanEngine.MaxReportedFiles) DeletedFiles.Add(f);
    }
}

/// <summary>
/// The deletion engine. <see cref="Analyze"/> is a pure dry run (it only lists); <see cref="Clean"/> deletes exactly
/// the files of a manifest the user approved, re-validating every one of them first. Never one pass for both.
/// </summary>
public static class CleanEngine
{
    public const int MaxReportedFiles = 20000;

    /// <summary>Manual recursion: one non-recursive enumeration per folder; links are skipped, never followed.</summary>
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
        MatchType = MatchType.Win32,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    private static readonly EnumerationOptions ProbeOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    // ───────────────────────────── Analysis (dry run) ─────────────────────────────

    public static TargetManifest Analyze(CleanTarget t, PathGuard guard, DateTime nowUtc, CancellationToken ct,
        Action<int>? onFilesSeen = null)
    {
        var m = new TargetManifest(t) { CutoffFileTime = (nowUtc - t.MinAge).ToFileTimeUtc() };

        if (t.SingleFile && (t.Recurse || t.Patterns is not { Count: 1 } || t.Patterns[0].IndexOfAny(['*', '?']) >= 0))
        {
            m.Refused = GuardVerdict.NotAbsolute;
            return m;
        }

        var verdict = t.SingleFile ? guard.CheckSingleFileBase(t.Base) : guard.CheckBase(t.Base);
        if (verdict != GuardVerdict.Ok)
        {
            m.Refused = verdict;
            return m;
        }

        var normalized = PathGuard.Normalize(t.Base)!;
        verdict = PathGuard.CheckOnDisk(normalized, out var canonical);
        switch (verdict)
        {
            case GuardVerdict.Ok:
                break;
            case GuardVerdict.Missing:
                m.Missing = true;
                return m;
            case GuardVerdict.Inaccessible:
                m.AccessDenied = true;
                return m;
            default:
                m.Refused = verdict;
                return m;
        }
        // Re-check the canonical form too (it may reveal a protected location the requested path hid).
        verdict = t.SingleFile ? guard.CheckSingleFileBase(canonical) : guard.CheckBase(canonical);
        if (verdict != GuardVerdict.Ok)
        {
            m.Refused = verdict;
            return m;
        }
        m.CanonicalBase = canonical;

        if (!CanList(canonical))
        {
            m.AccessDenied = true;
            return m;
        }

        if (t.SingleFile)
        {
            AnalyzeSingleFile(m, t, guard, canonical);
            return m;
        }

        var keepPool = t.KeepNewest > 0 ? new List<FileCandidate>() : null;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((canonical, 0));
        var seen = 0;

        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var e = new EntryEnumerator(current.Dir, WalkOptions);
                while (e.MoveNext())
                {
                    var entry = e.Current;
                    if (++seen % 512 == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        onFilesSeen?.Invoke(seen);
                    }

                    // Never follow, never delete: junctions, symlinks, mount points, cloud placeholders.
                    if (PathGuard.ShouldSkip(entry.Attributes)) continue;
                    if (IsExcluded(t, canonical, entry.FullPath)) continue;
                    if (guard.IsInNoTouchArea(entry.FullPath)) continue;

                    if (entry.IsDirectory)
                    {
                        if (!t.Recurse) continue;
                        if (current.Depth == 0 && t.OnlyTopDirs is { } only &&
                            !only.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)) continue;
                        stack.Push((entry.FullPath, current.Depth + 1));
                        if (t.RemoveEmptyDirs && Math.Max(entry.LastWriteFileTime, entry.CreationFileTime) < m.CutoffFileTime)
                            m.Dirs.Add(new DirCandidate(entry.FullPath, entry.CreationFileTime, current.Depth + 1));
                        continue;
                    }

                    if (current.Depth == 0 && t.OnlyTopDirs is not null) continue;
                    if (!Matches(t, entry.Name, entry.Length)) continue;
                    if (!guard.IsFileAllowed(entry.FullPath, canonical)) continue;

                    var candidate = new FileCandidate(entry.FullPath, entry.Length, entry.LastWriteFileTime, entry.CreationFileTime);
                    if (keepPool is not null)
                    {
                        keepPool.Add(candidate);
                        continue;
                    }
                    AddIfOldEnough(m, candidate);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder vanished or became unreadable mid-walk: skip it, keep the rest.
            }
        }

        if (keepPool is not null)
        {
            var newest = keepPool
                .OrderByDescending(c => Math.Max(c.LastWriteFileTime, c.CreationFileTime))
                .Take(t.KeepNewest)
                .Select(c => c.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in keepPool)
                if (!newest.Contains(c.Path))
                    AddIfOldEnough(m, c);
        }

        onFilesSeen?.Invoke(seen);
        return m;
    }

    private static void AnalyzeSingleFile(TargetManifest m, CleanTarget t, PathGuard guard, string canonical)
    {
        var path = Path.Combine(canonical, t.Patterns![0]);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            if (PathGuard.ShouldSkip(info.Attributes) || (info.Attributes & FileAttributes.Directory) != 0) return;
            if (!guard.IsFileAllowed(info.FullName, canonical)) return;
            if (t.Filter is { } f && !f(info.Name, info.Length)) return;
            AddIfOldEnough(m, new FileCandidate(info.FullName, info.Length,
                info.LastWriteTimeUtc.ToFileTimeUtc(), info.CreationTimeUtc.ToFileTimeUtc()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            m.AccessDenied = true;
        }
    }

    private static void AddIfOldEnough(TargetManifest m, FileCandidate c)
    {
        if (Math.Max(c.LastWriteFileTime, c.CreationFileTime) >= m.CutoffFileTime)
        {
            m.TooNew++;
            return;
        }
        m.Files.Add(c);
        m.Bytes += c.Size;
    }

    private static bool Matches(CleanTarget t, string name, long size)
    {
        if (t.Patterns is { Count: > 0 } patterns)
        {
            var any = false;
            foreach (var p in patterns)
            {
                if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true))
                {
                    any = true;
                    break;
                }
            }
            if (!any) return false;
        }
        return t.Filter is null || t.Filter(name, size);
    }

    private static bool IsExcluded(CleanTarget t, string canonicalBase, string fullPath)
    {
        if (t.Exclude is not { Count: > 0 } excluded) return false;
        if (!PathGuard.IsStrictlyUnder(fullPath, canonicalBase)) return true;
        var rel = fullPath[(canonicalBase.Length + (canonicalBase.EndsWith('\\') ? 0 : 1))..];
        foreach (var x in excluded)
        {
            var e = x.Trim().Trim('\\').Replace('/', '\\');
            if (e.Length == 0) continue;
            if (rel.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.Length > e.Length && rel.StartsWith(e, StringComparison.OrdinalIgnoreCase) && rel[e.Length] == '\\') return true;
        }
        return false;
    }

    /// <summary>True when the folder can be listed (false = access denied).</summary>
    private static bool CanList(string dir)
    {
        try
        {
            using var e = new EntryEnumerator(dir, ProbeOptions);
            e.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // ───────────────────────────── Cleaning ─────────────────────────────

    /// <summary>
    /// Deletes the files of an approved manifest. Each file is re-validated through its own handle; locked files are
    /// skipped and counted; afterwards empty sub-folders are removed deepest first. The base folder always stays.
    /// </summary>
    public static CleanTally Clean(TargetManifest m, PathGuard guard, CancellationToken ct, Action<int>? onProgress = null)
    {
        var tally = new CleanTally();
        if (m.CanonicalBase is null || !m.HasWork) return tally;

        // The base itself must still be the very same plain folder.
        var verdict = PathGuard.CheckOnDisk(m.CanonicalBase, out var canonicalNow);
        if (verdict != GuardVerdict.Ok || !string.Equals(canonicalNow, m.CanonicalBase, StringComparison.OrdinalIgnoreCase))
        {
            tally.BaseProblem = PathGuard.Describe(verdict == GuardVerdict.Ok ? GuardVerdict.ReparsePoint : verdict);
            return tally;
        }

        var processed = 0;
        foreach (var f in m.Files)
        {
            if (ct.IsCancellationRequested)
            {
                tally.Cancelled = true;
                break;
            }
            var r = NativeCleaner.DeleteFile(f, m.CanonicalBase, m.CutoffFileTime, guard);
            switch (r.Outcome)
            {
                case DeleteOutcome.Deleted:
                    tally.Deleted++;
                    tally.FreedBytes += r.FreedBytes;
                    tally.DeletedLogicalBytes += f.Size;
                    if (tally.DeletedFiles.Count < MaxReportedFiles) tally.DeletedFiles.Add((f.Path, f.Size));
                    break;
                case DeleteOutcome.InUse: tally.InUse++; break;
                case DeleteOutcome.Denied: tally.Denied++; break;
                case DeleteOutcome.Gone: tally.Gone++; break;
                case DeleteOutcome.Changed: tally.Changed++; break;
                case DeleteOutcome.Refused: tally.Refused++; break;
                default:
                    tally.Errors++;
                    if (tally.ErrorSamples.Count < 20) tally.ErrorSamples.Add($"{f.Path}: ошибка Win32 {r.Win32Error}");
                    break;
            }
            if (++processed % 64 == 0) onProgress?.Invoke(processed);
        }
        onProgress?.Invoke(processed);

        if (!tally.Cancelled && m.Target.RemoveEmptyDirs)
        {
            foreach (var d in m.Dirs.OrderByDescending(d => d.Depth).ThenByDescending(d => d.Path.Length))
            {
                if (ct.IsCancellationRequested) break;
                if (NativeCleaner.DeleteEmptyDirectory(d, m.CanonicalBase, guard).Outcome == DeleteOutcome.Deleted)
                    tally.DirsRemoved++;
            }
        }
        return tally;
    }

    // ───────────────────────────── Enumeration ─────────────────────────────

    private readonly record struct Entry(string FullPath, string Name, FileAttributes Attributes, long Length,
        long LastWriteFileTime, long CreationFileTime, bool IsDirectory);

    /// <summary>Allocation-light enumerator that reads everything from the directory listing itself.</summary>
    private sealed class EntryEnumerator : FileSystemEnumerator<Entry>
    {
        public EntryEnumerator(string directory, EnumerationOptions options) : base(directory, options) { }

        protected override Entry TransformEntry(ref FileSystemEntry entry) => new(
            entry.ToFullPath(),
            entry.FileName.ToString(),
            entry.Attributes,
            entry.Length,
            entry.LastWriteTimeUtc.ToFileTime(),
            entry.CreationTimeUtc.ToFileTime(),
            entry.IsDirectory);
    }
}
