using System.IO;
using Lycoris.Core;

namespace Lycoris.Modules.Cleaner;

/// <summary>One large file found in the user's folder (read-only result).</summary>
public sealed record LargeFile(string Path, long Bytes, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public string SizeText => SystemProfile.FormatBytes(Bytes);
    public string DateText => LastWriteUtc.ToLocalTime().ToString(Loc.IsEnglish ? "M/d/yyyy" : "dd.MM.yyyy", Loc.Culture);
    /// <summary>How to free this space safely when the file belongs to a program (null = an ordinary file).</summary>
    public string? Hint => LargeFiles.HintFor(Path);
    public bool HasHint => Hint is not null;
}

/// <summary>
/// «Что занимает место»: the largest files in the user's folder — ONLY shown, never deleted by Lycoris (CLAUDE.md rule 3:
/// user data is not deleted; PathGuard keeps the cleaner out of Documents/Downloads/…). The owner opens a file's location
/// in Explorer and decides there. Enumeration never follows reparse points (junctions, symlinks, cloud placeholders) and
/// skips offline / system files; inaccessible folders are skipped silently. Cancellable; keeps only the top N in memory.
/// </summary>
public static class LargeFiles
{
    public const long DefaultMinBytes = 100L * 1024 * 1024;   // smaller files are not where the space went
    public const int DefaultTop = 25;

    public static string DefaultRoot => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// The biggest files in a profile are often a program's own storage (a Docker / WSL disk, Ollama models, Steam games):
    /// deleting them by hand in Explorer breaks the program. For those the row says how to free the space properly. Pure.
    /// </summary>
    public static string? HintFor(string path)
    {
        var p = path.Replace('/', '\\').ToLowerInvariant();
        if (p.Contains(@"\.ollama\models\")) return Loc.T("Модель Ollama: не удаляйте вручную — удалите ненужную модель командой «ollama rm <имя>».");
        if (p.Contains(@"\docker\") && p.EndsWith(".vhdx")) return Loc.T("Диск Docker: не удаляйте вручную — место освобождается в Docker Desktop (очистка образов и данных).");
        if (p.EndsWith(".vhdx") && (p.Contains(@"\packages\") || p.Contains(@"\wsl\"))) return Loc.T("Диск Linux (WSL): не удаляйте вручную — ненужный дистрибутив удаляется командой «wsl --unregister <имя>».");
        if (p.Contains(@"\steamapps\")) return Loc.T("Файл игры Steam: удаляйте игру через Steam, иначе Steam сочтёт её повреждённой.");
        if (p.Contains(@"\.git\")) return Loc.T("Часть истории Git-репозитория: не удаляйте вручную — так репозиторий сломается; сжать историю можно командой «git gc».");
        if (p.Contains(@"\.cache\huggingface\")) return Loc.T("Кэш моделей Hugging Face: можно удалить — модель скачается заново, когда понадобится.");
        return null;
    }

    internal static EnumerationOptions Options => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        // ReparsePoint on a directory also stops recursion into it: junctions / symlinks / OneDrive placeholders are not followed
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    public static IReadOnlyList<LargeFile> Scan(string root, long minBytes = DefaultMinBytes, int top = DefaultTop,
        CancellationToken ct = default, Action<int>? progress = null)
    {
        if (top <= 0 || !Directory.Exists(root)) return [];
        var best = new PriorityQueue<LargeFile, long>();   // min-heap: the smallest of the kept files is dropped first
        var seen = 0;
        foreach (var fi in new DirectoryInfo(root).EnumerateFiles("*", Options))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 4000 == 0) progress?.Invoke(seen);
            long len;
            try { len = fi.Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (len < minBytes) continue;
            if (best.Count < top) best.Enqueue(new LargeFile(fi.FullName, len, fi.LastWriteTimeUtc), len);
            else if (best.TryPeek(out _, out var smallest) && len > smallest) best.EnqueueDequeue(new LargeFile(fi.FullName, len, fi.LastWriteTimeUtc), len);
        }
        progress?.Invoke(seen);
        return best.UnorderedItems.Select(x => x.Element).OrderByDescending(f => f.Bytes).ToList();
    }
}
