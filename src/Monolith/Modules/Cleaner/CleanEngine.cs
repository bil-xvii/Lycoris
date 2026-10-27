using System.Buffers.Binary;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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

/// <summary>A file seen by a walk of a target, with the exact metadata the cleaning pass re-checks.</summary>
public readonly record struct FileCandidate(string Path, long Size, long LastWriteFileTime, long CreationFileTime);

/// <summary>A sub-folder seen by a walk; removed only if it is empty after cleaning.</summary>
public readonly record struct DirCandidate(string Path, long CreationFileTime, int Depth);

/// <summary>How a file found by the cleaning walk relates to what the user approved.</summary>
public enum ApprovalMatch
{
    /// <summary>Not part of the analysis (appeared or was moved in afterwards): never deleted.</summary>
    NotApproved,
    /// <summary>Same path as an analysed file, but its size or timestamps differ: never deleted.</summary>
    Changed,
    /// <summary>Exactly the file the analysis counted (path, size, both timestamps).</summary>
    Approved,
}

/// <summary>
/// Dry-run result for one target: how much would be deleted, plus a compact fingerprint of every counted file —
/// 16 bytes per file instead of its full path. The cleaning pass walks the target again with the same rules and
/// cut-off and deletes only files whose fingerprint is here, so it can never take more than the user approved.
/// Nothing is deleted while this is built.
/// </summary>
public sealed class TargetManifest
{
    private ulong[] _pathKeys = [];
    private ulong[] _metaKeys = [];

    public TargetManifest(CleanTarget target) => Target = target;

    public CleanTarget Target { get; }
    public string? CanonicalBase { get; set; }
    /// <summary>Age cut-off fixed at analysis time; the cleaning pass uses the very same value.</summary>
    public long CutoffFileTime { get; set; }
    /// <summary>Files the analysis counted for deletion.</summary>
    public int Count { get; private set; }
    /// <summary>Old sub-folders found by the analysis (removed only if empty after cleaning).</summary>
    public int DirCount { get; set; }
    public long Bytes { get; private set; }
    /// <summary>Base folder does not exist on this PC (not an error).</summary>
    public bool Missing { get; set; }
    /// <summary>The base exists but could not be listed (usually: needs administrator rights).</summary>
    public bool AccessDenied { get; set; }
    /// <summary>Refused by the path guard; nothing in this target will be touched.</summary>
    public GuardVerdict? Refused { get; set; }
    /// <summary>Matching files that are still too new for the age filter.</summary>
    public int TooNew { get; set; }
    public bool HasWork => Count > 0 || DirCount > 0;

    /// <summary>
    /// The approval was dropped (already cleaned, outdated, or the target was not part of what the user confirmed):
    /// a cleaning pass over this manifest deletes nothing.
    /// </summary>
    public bool IsReleased { get; private set; }

    /// <summary>Fingerprints still held in memory.</summary>
    public int RetainedFingerprints => _pathKeys.Length;

    internal void SetApproved(List<(ulong Path, ulong Meta)> keys, long bytes)
    {
        keys.Sort((a, b) => a.Path.CompareTo(b.Path));
        _pathKeys = new ulong[keys.Count];
        _metaKeys = new ulong[keys.Count];
        for (var i = 0; i < keys.Count; i++)
        {
            _pathKeys[i] = keys[i].Path;
            _metaKeys[i] = keys[i].Meta;
        }
        Count = keys.Count;
        Bytes = bytes;
    }

    /// <summary>Checks a file found by the cleaning walk against the approval.</summary>
    public ApprovalMatch Match(in FileCandidate c)
    {
        if (IsReleased || _pathKeys.Length == 0) return ApprovalMatch.NotApproved;
        var path = CleanEngine.PathKey(c.Path);
        var i = Array.BinarySearch(_pathKeys, path);
        if (i < 0) return ApprovalMatch.NotApproved;
        while (i > 0 && _pathKeys[i - 1] == path) i--;
        var meta = CleanEngine.MetaKey(c);
        for (; i < _pathKeys.Length && _pathKeys[i] == path; i++)
            if (_metaKeys[i] == meta) return ApprovalMatch.Approved;
        return ApprovalMatch.Changed;
    }

    /// <summary>Drops the approval and its fingerprints (the counters stay for display).</summary>
    public void Release()
    {
        _pathKeys = [];
        _metaKeys = [];
        IsReleased = true;
    }
}

/// <summary>What one cleaning pass did.</summary>
public sealed class CleanTally
{
    public int Deleted { get; set; }
    public long FreedBytes { get; set; }
    public long DeletedLogicalBytes { get; set; }
    public int InUse { get; set; }
    public int Denied { get; set; }
    /// <summary>Approved files that changed (size, timestamps) after the analysis — left alone.</summary>
    public int Changed { get; set; }
    /// <summary>Approved files that are no longer there (deleted by their program, moved, behind a new link).</summary>
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
/// The deletion engine. <see cref="Analyze"/> is a pure dry run: it only counts and keeps a fingerprint per file.
/// <see cref="Clean"/> walks the same target again with the same rules and the analysis cut-off and deletes only the
/// files the user approved (fingerprint match), re-validating every one of them through its own handle first.
/// Both passes share <see cref="Walk"/>, so they can never disagree about what is in scope.
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

    // ───────────────────────────── Fingerprints ─────────────────────────────

    /// <summary>64-bit key of the exact path (first 8 bytes of its SHA-256; ordinal, as the file system lists it).</summary>
    public static ulong PathKey(string path)
    {
        var bytes = MemoryMarshal.AsBytes(path.AsSpan());
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    /// <summary>64-bit key of the metadata the deletion re-checks: size, last write and creation time.</summary>
    public static ulong MetaKey(in FileCandidate c)
    {
        var h = Mix(unchecked((ulong)c.Size) ^ 0x9E3779B97F4A7C15UL);
        h = Mix(h ^ unchecked((ulong)c.LastWriteFileTime));
        return Mix(h ^ unchecked((ulong)c.CreationFileTime));

        static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    // ───────────────────────────── Analysis (dry run) ─────────────────────────────

    public static TargetManifest Analyze(CleanTarget t, PathGuard guard, DateTime nowUtc, CancellationToken ct,
        Action<int>? onFilesSeen = null)
    {
        var m = new TargetManifest(t) { CutoffFileTime = (nowUtc - t.MinAge).ToFileTimeUtc() };
        if (!Resolve(m, guard)) return m;
        var canonical = m.CanonicalBase!;

        var keys = new List<(ulong Path, ulong Meta)>();
        long bytes = 0;
        var tooNew = 0;
        void Count(List<FileCandidate> files)
        {
            foreach (var c in files)
            {
                if (!IsOldEnough(c, m.CutoffFileTime))
                {
                    tooNew++;
                    continue;
                }
                keys.Add((PathKey(c.Path), MetaKey(c)));
                bytes += c.Size;
            }
        }

        if (t.SingleFile)
        {
            try
            {
                if (ReadSingleFile(t, guard, canonical) is { } file) Count([file]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                m.AccessDenied = true;
            }
        }
        else
        {
            var dirs = 0;
            Walk(t, canonical, m.CutoffFileTime, guard, ct, onFilesSeen, Count, _ => dirs++);
            m.DirCount = dirs;
        }

        m.TooNew = tooNew;
        m.SetApproved(keys, bytes);
        return m;
    }

    /// <summary>Path checks shared by both passes. False = nothing in this target may be touched.</summary>
    private static bool Resolve(TargetManifest m, PathGuard guard)
    {
        var t = m.Target;
        if (t.SingleFile && (t.Recurse || t.Patterns is not { Count: 1 } || t.Patterns[0].IndexOfAny(['*', '?']) >= 0))
        {
            m.Refused = GuardVerdict.NotAbsolute;
            return false;
        }

        var verdict = t.SingleFile ? guard.CheckSingleFileBase(t.Base) : guard.CheckBase(t.Base);
        if (verdict != GuardVerdict.Ok)
        {
            m.Refused = verdict;
            return false;
        }

        var normalized = PathGuard.Normalize(t.Base)!;
        verdict = PathGuard.CheckOnDisk(normalized, out var canonical);
        switch (verdict)
        {
            case GuardVerdict.Ok:
                break;
            case GuardVerdict.Missing:
                m.Missing = true;
                return false;
            case GuardVerdict.Inaccessible:
                m.AccessDenied = true;
                return false;
            default:
                m.Refused = verdict;
                return false;
        }
        // Re-check the canonical form too (it may reveal a protected location the requested path hid).
        verdict = t.SingleFile ? guard.CheckSingleFileBase(canonical) : guard.CheckBase(canonical);
        if (verdict != GuardVerdict.Ok)
        {
            m.Refused = verdict;
            return false;
        }
        m.CanonicalBase = canonical;

        if (!CanList(canonical))
        {
            m.AccessDenied = true;
            return false;
        }
        return true;
    }

    /// <summary>The one named file of a single-file target, or null when it is absent or not a plain file.</summary>
    private static FileCandidate? ReadSingleFile(CleanTarget t, PathGuard guard, string canonical)
    {
        var info = new FileInfo(Path.Combine(canonical, t.Patterns![0]));
        if (!info.Exists) return null;
        if (PathGuard.ShouldSkip(info.Attributes) || (info.Attributes & FileAttributes.Directory) != 0) return null;
        if (!guard.IsFileAllowed(info.FullName, canonical)) return null;
        if (t.Filter is { } f && !f(info.Name, info.Length)) return null;
        return new FileCandidate(info.FullName, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(), info.CreationTimeUtc.ToFileTimeUtc());
    }

    private static bool IsOldEnough(in FileCandidate c, long cutoffFileTime) =>
        Math.Max(c.LastWriteFileTime, c.CreationFileTime) < cutoffFileTime;

    /// <summary>
    /// Lists a target with all of its rules: links and cloud placeholders are skipped (never followed), excluded and
    /// no-touch entries are skipped with their content, patterns / filters / guard apply, the newest N files of a
    /// keep-newest target are held back. Matching files are handed over one folder at a time, after that folder's
    /// listing is closed (so the cleaning pass never deletes inside an open enumeration). Old sub-folders (older than
    /// <paramref name="cutoffFileTime"/>) go to <paramref name="onDir"/>.
    /// </summary>
    private static void Walk(CleanTarget t, string canonical, long cutoffFileTime, PathGuard guard, CancellationToken ct,
        Action<int>? onSeen, Action<List<FileCandidate>> onFiles, Action<DirCandidate> onDir)
    {
        var keepPool = t.KeepNewest > 0 ? new List<FileCandidate>() : null;
        var batch = new List<FileCandidate>();
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((canonical, 0));
        var seen = 0;

        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            batch.Clear();
            try
            {
                using var e = new EntryEnumerator(current.Dir, WalkOptions);
                while (e.MoveNext())
                {
                    var entry = e.Current;
                    if (++seen % 512 == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        onSeen?.Invoke(seen);
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
                        if (t.RemoveEmptyDirs && Math.Max(entry.LastWriteFileTime, entry.CreationFileTime) < cutoffFileTime)
                            onDir(new DirCandidate(entry.FullPath, entry.CreationFileTime, current.Depth + 1));
                        continue;
                    }

                    if (current.Depth == 0 && t.OnlyTopDirs is not null) continue;
                    if (!Matches(t, entry.Name, entry.Length)) continue;
                    if (!guard.IsFileAllowed(entry.FullPath, canonical)) continue;

                    var candidate = new FileCandidate(entry.FullPath, entry.Length, entry.LastWriteFileTime, entry.CreationFileTime);
                    (keepPool ?? batch).Add(candidate);
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
            if (batch.Count > 0) onFiles(batch);
        }

        if (keepPool is not null)
        {
            var newest = keepPool
                .OrderByDescending(c => Math.Max(c.LastWriteFileTime, c.CreationFileTime))
                .Take(t.KeepNewest)
                .Select(c => c.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            batch.Clear();
            batch.AddRange(keepPool.Where(c => !newest.Contains(c.Path)));
            if (batch.Count > 0) onFiles(batch);
        }

        onSeen?.Invoke(seen);
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
    /// Deletes what the user approved for this target. The target is walked again with the analysis rules and cut-off;
    /// a file is deleted only when its path, size and both timestamps match a file the analysis counted, and only after
    /// it is re-validated through its own handle. Files that appeared or changed since the analysis stay; locked files
    /// are skipped and counted; afterwards empty old sub-folders are removed deepest first. The base folder always stays.
    /// </summary>
    public static CleanTally Clean(TargetManifest m, PathGuard guard, CancellationToken ct, Action<int>? onProgress = null)
    {
        var tally = new CleanTally();
        if (m.CanonicalBase is null || !m.HasWork || m.IsReleased) return tally;

        // The base itself must still be the very same plain folder.
        var verdict = PathGuard.CheckOnDisk(m.CanonicalBase, out var canonicalNow);
        if (verdict != GuardVerdict.Ok || !string.Equals(canonicalNow, m.CanonicalBase, StringComparison.OrdinalIgnoreCase))
        {
            tally.BaseProblem = PathGuard.Describe(verdict == GuardVerdict.Ok ? GuardVerdict.ReparsePoint : verdict);
            return tally;
        }

        var processed = 0;
        var found = 0;
        var dirs = new List<DirCandidate>();

        void Delete(List<FileCandidate> files)
        {
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                switch (m.Match(f))
                {
                    case ApprovalMatch.NotApproved:
                        continue;
                    case ApprovalMatch.Changed:
                        found++;
                        tally.Changed++;
                        continue;
                }
                found++;
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
        }

        try
        {
            if (m.Target.SingleFile)
            {
                try
                {
                    if (ReadSingleFile(m.Target, guard, m.CanonicalBase) is { } file) Delete([file]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    tally.Denied++;
                    found++;
                }
            }
            else
            {
                Walk(m.Target, m.CanonicalBase, m.CutoffFileTime, guard, ct, null, Delete, dirs.Add);
            }
        }
        catch (OperationCanceledException)
        {
            tally.Cancelled = true;
        }
        onProgress?.Invoke(processed);

        // Approved files the walk did not find any more: deleted by their program, moved away, or now behind a link.
        if (!tally.Cancelled) tally.Gone += Math.Max(0, m.Count - found);

        if (!tally.Cancelled && m.Target.RemoveEmptyDirs)
        {
            foreach (var d in dirs.OrderByDescending(d => d.Depth).ThenByDescending(d => d.Path.Length))
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
