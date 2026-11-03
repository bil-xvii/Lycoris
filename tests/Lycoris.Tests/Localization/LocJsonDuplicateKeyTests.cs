using System.IO;
using System.Text.RegularExpressions;

namespace Lycoris.Tests.Localization;

/// <summary>
/// A key written twice in one English file is silently «last wins» for the app, but it hides mistakes and broke
/// tools/loc-scan (found 03.11.2026: «ОТКРЫТЬ АВТОЗАПУСК» twice in Home.json).
/// </summary>
public sealed class LocJsonDuplicateKeyTests
{
    [Fact]
    public void No_english_file_contains_the_same_key_twice()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Lycoris.slnx"))) root = root.Parent;
        var dir = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Lycoris.slnx not found"), "src", "Lycoris", "Core", "Localization", "en");
        Assert.True(Directory.Exists(dir), dir);
        var problems = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var keys = Regex.Matches(File.ReadAllText(file), @"^\s*""((?:[^""\\]|\\.)*)""\s*:", RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value);
            problems.AddRange(keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => $"{Path.GetFileName(file)}: «{g.Key}» ×{g.Count()}"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
