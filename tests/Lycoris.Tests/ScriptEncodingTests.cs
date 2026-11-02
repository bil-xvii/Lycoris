using System.IO;

namespace Lycoris.Tests;

public sealed class ScriptEncodingTests
{
    // Regression (1.0.5 release): a mass rename dropped the UTF-8 BOM and Windows PowerShell 5.1 then misread Cyrillic strings,
    // so tools\publish.ps1 did not parse and the release was refused.
    [Fact]
    public void PowerShell_scripts_with_non_ascii_text_start_with_a_utf8_bom()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "tools"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(dir!, "tools"), "*.ps1", SearchOption.AllDirectories))
        {
            var b = File.ReadAllBytes(f);
            var hasBom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
            if (!hasBom && b.Any(x => x > 127)) bad.Add(Path.GetFileName(f));
        }
        Assert.Empty(bad);
    }
}
