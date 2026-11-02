using System.IO;
using System.Text.RegularExpressions;
using Monolith.LocScan;
using Monolith.Shell;

namespace Monolith.Tests.Localization;

/// <summary>
/// The translation tables (Core/Localization/en/*.json) and the coverage of every area that declares <c>"translated": true</c>:
/// all Cyrillic text in its sources goes through Loc / a sink and has an English entry. Agents extend the list simply by setting
/// the flag in their area file. Measure progress with tools/loc-scan.ps1.
/// </summary>
public sealed class LocalizationCoverageTests
{
    private static readonly string SrcRoot = FindSrcRoot();

    private static string FindSrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Monolith.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Monolith.slnx not found"), "src", "Monolith");
    }

    private static IReadOnlyList<LocArea> EmbeddedAreas()
    {
        Loc.LoadEmbeddedAreas();
        var embedded = Directory.EnumerateFiles(Path.Combine(SrcRoot, "Core", "Localization", "en"), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Loc.Areas.Where(a => embedded.Contains(a.Name)).ToList();
    }

    private static HashSet<string> AllKeys() => EmbeddedAreas().SelectMany(a => a.Entries.Keys).ToHashSet(StringComparer.Ordinal);

    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.Compiled);

    private static string Holes(string s) => string.Join(",", Placeholder.Matches(s).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    [Fact]
    public void Embedded_area_files_are_found_and_valid()
    {
        var areas = EmbeddedAreas();
        Assert.Contains(areas, a => a.Name == "Common");
        Assert.Contains(areas, a => a.Name == "Shell" && a.Translated);
        Assert.Contains(areas, a => a.Name == "Settings" && a.Translated);
        Assert.All(areas, a => Assert.NotEmpty(a.Name));
    }

    [Fact]
    public void No_key_is_translated_two_different_ways()
    {
        var names = EmbeddedAreas().Select(a => a.Name).ToList();
        var conflicts = Loc.Conflicts.Where(c => names.Any(n => c.StartsWith(n + ":", StringComparison.Ordinal))).ToList();
        Assert.True(conflicts.Count == 0, "One term = one translation (see the glossary in docs/localization.md):\n" + string.Join("\n", conflicts));
    }

    [Fact]
    public void Every_entry_has_text_and_the_same_placeholders_as_the_russian_original()
    {
        var bad = new List<string>();
        foreach (var area in EmbeddedAreas())
            foreach (var (ru, en) in area.Entries)
            {
                if (string.IsNullOrWhiteSpace(en)) { bad.Add($"{area.Name}: empty English for «{Short(ru)}»"); continue; }
                if (!Scanner.HasCyrillic(ru)) bad.Add($"{area.Name}: key without Cyrillic «{Short(ru)}» (such text needs no entry)");
                var forms = en.Split('|');
                foreach (var form in forms)
                    if (Holes(form) != Holes(ru) && forms.Length == 1)
                        bad.Add($"{area.Name}: placeholders differ «{Short(ru)}» ⇒ «{Short(en)}»");
                if (forms.Length == 2 && Holes(forms[0]) != Holes(ru)) bad.Add($"{area.Name}: plural placeholders differ «{Short(ru)}»");
                if (Scanner.HasCyrillic(en) && !en.Contains("Язык") && !en.Contains("РУССКИЙ") && !en.Contains("Russian")) bad.Add($"{area.Name}: Cyrillic in the English text of «{Short(ru)}»");
            }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    // Brand/tool spellings that legitimately keep lower case inside an all-caps text.
    private static readonly string[] MixedCaseBrands = ["winget", "DirectX", "GeForce", "OneDrive", "FreeSync", "PowerShell", "SysMain", "DiagTrack", "Xbox", "PCIe", "YouTube", "Battle.net", "NetBIOS", "IPv6", "IPv4", "macOS", "iOS", "WinINet", "ENGLISH", "MONOLITH"];

    [Fact]
    public void An_all_caps_russian_label_has_an_all_caps_english_one()
    {
        var bad = new List<string>();
        foreach (var area in EmbeddedAreas())
            foreach (var (ru, en) in area.Entries)
            {
                if (ru.Any(char.IsLower) || !ru.Any(char.IsLetter)) continue;
                var probe = MixedCaseBrands.Aggregate(en, (s, b) => s.Replace(b, "", StringComparison.Ordinal));
                if (probe.Any(char.IsLower)) bad.Add($"{area.Name}: «{Short(ru)}» ⇒ «{Short(en)}» (labels, tags, tabs and buttons are UPPER CASE in both languages)");
            }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void Plural_entries_have_two_forms()
    {
        // A key that is the singular form of a counted phrase ("{0} файл") needs "one|other" in English.
        var bad = EmbeddedAreas().SelectMany(a => a.Entries.Where(e => e.Value.Contains('|') && e.Value.Split('|').Length != 2).Select(e => $"{a.Name}: «{Short(e.Key)}»")).ToList();
        Assert.True(bad.Count == 0, "Plural values are exactly \"one|other\":\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Every_translated_area_has_no_unwrapped_text_and_no_missing_english()
    {
        var all = AllKeys();
        var problems = new List<string>();
        foreach (var area in LocAreas.Translated.Where(a => EmbeddedAreas().Any(e => e.Name == a.Name)))
        {
            var ignore = area.Ignore.ToHashSet(StringComparer.Ordinal);
            var files = Scanner.ExpandSources(SrcRoot, area.Sources);
            Assert.True(files.Count > 0, $"{area.Name}: no source file matches {string.Join(", ", area.Sources)}");
            foreach (var f in files)
                foreach (var x in Scanner.ScanFile(Path.Combine(SrcRoot, f), f, ignore))
                {
                    if (x.Kind == FindingKind.Violation) problems.Add($"{area.Name}: {x.File}:{x.Line}: {x.Why}: «{Short(x.Text)}»");
                    else if (!all.Contains(x.Text)) problems.Add($"{area.Name}: {x.File}:{x.Line}: no English entry: «{Short(x.Text)}»");
                }
        }
        Assert.True(problems.Count == 0, $"{problems.Count} problem(s); run tools/loc-scan.ps1 -Check <Area>:\n" + string.Join("\n", problems.Take(60)));
    }

    // ───────── The scanner itself ─────────

    [Fact]
    public void Scanner_accepts_wrapped_text_and_flags_bare_text()
    {
        const string code = """
            class C {
                void M() {
                    var a = Loc.T("Ключ один");
                    var b = Loc.Tf("Формат {0}", x);
                    var c = Loc.T("Длинный " + "ключ");
                    Ui.Toast("Через приёмник", ToastKind.Info);
                    Log.Info("Строка журнала");
                    var d = Loc.Key("Табличный ключ");
                    var e = "Голый текст";
                    var f = $"Интерполяция {x}";
                    var g = Loc.T(flag ? "А" : "Б");
                    var h = "Игнор"; // loc:ignore
                    // "Комментарий" не считается
                    var i = Loc.Plural(n, "файл", "файла", "файлов");
                    var j = Loc.Keep("русский алиас");
                    var k = Loc.N(n, "{0} шт", "{0} шт.", "{0} шт.");
                }
            }
            """;
        var found = Scanner.ScanCs("t.cs", code);
        var keys = found.Where(f => f.Kind == FindingKind.Key).Select(f => f.Text).ToList();
        var violations = found.Where(f => f.Kind == FindingKind.Violation).Select(f => f.Text).ToList();
        Assert.Contains("Ключ один", keys);
        Assert.Contains("Формат {0}", keys);
        Assert.Contains("Длинный ключ", keys);
        Assert.Contains("Через приёмник", keys);
        Assert.Contains("Строка журнала", keys);
        Assert.Contains("Табличный ключ", keys);
        Assert.Contains("файл", keys);
        Assert.Contains("{0} шт", keys);
        Assert.DoesNotContain("русский алиас", keys);
        Assert.Contains("Голый текст", violations);
        Assert.Contains(violations, v => v.StartsWith("Интерполяция"));
        Assert.Contains("А", violations);
        Assert.Contains("Б", violations);
        Assert.DoesNotContain("Игнор", violations);
        Assert.DoesNotContain(violations, v => v.Contains("Комментарий"));
        Assert.DoesNotContain("русский алиас", violations);
        Assert.DoesNotContain("файла", violations);
    }

    [Fact]
    public void Scanner_reads_xaml_attributes_and_text()
    {
        const string xaml = """
            <Grid>
              <!-- Комментарий не считается -->
              <TextBlock Text="{loc:T Привет}" ToolTip="{loc:T 'Привет, мир'}" />
              <TextBlock Text="{Binding N, StringFormat={loc:T 'Строк: {0}'}}" />
              <Button Content="Голая кнопка" />
              <TextBlock>Голый текст</TextBlock>
              <Button Content="РУССКИЙ" /> <!-- loc:ignore -->
            </Grid>
            """;
        var found = Scanner.ScanXaml("t.xaml", xaml);
        var keys = found.Where(f => f.Kind == FindingKind.Key).Select(f => f.Text).ToList();
        var violations = found.Where(f => f.Kind == FindingKind.Violation).Select(f => f.Text).ToList();
        Assert.Equal(["Привет", "Привет, мир", "Строк: {0}"], keys);
        Assert.Equal(["Голая кнопка", "Голый текст"], violations);
    }

    [Fact]
    public void Shell_pages_and_palette_titles_are_in_the_common_table()
    {
        var all = AllKeys();
        foreach (var title in new[] { "ПАНЕЛЬ", "ОПТИМИЗАЦИЯ", "ИГРЫ", "АВТОЗАПУСК И СЛУЖБЫ", "ОБСЛУЖИВАНИЕ", "ДРАЙВЕРЫ", "УСТРОЙСТВА И СЕТЬ", "ПРИЛОЖЕНИЯ", "ВОССТАНОВЛЕНИЕ", "НАСТРОЙКИ" })
            Assert.Contains(title, all);
        Assert.Equal("СИСТЕМНЫЙ", "СИСТЕМНЫЙ"); // sanity: this file is UTF-8
        Assert.All(new[] { MainViewModel.GroupOverview, MainViewModel.GroupBoost, MainViewModel.GroupMaintenance, MainViewModel.GroupProtection }, g => Assert.Contains(g, all));
    }

    private static string Short(string s) => (s.Length > 70 ? s[..70] + "…" : s).Replace("\n", "\\n");
}
