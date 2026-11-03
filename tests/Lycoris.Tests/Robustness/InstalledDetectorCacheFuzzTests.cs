using System.IO;
using System.Text;
using Lycoris.Core;
using Lycoris.Modules.Apps;

namespace Lycoris.Tests.Robustness;

/// <summary>
/// InstalledDetector.LoadCache — устойчивость к повреждённому кэшу (apps-installed.json).
/// При любых проблемах с файлом — метод возвращает null, исключение наружу не выходит.
/// </summary>
public sealed class InstalledDetectorCacheFuzzTests
{
    /// <summary>Внутренняя перегрузка LoadCache для тестов: принимает путь к файлу вместо AppPaths.CacheDir.</summary>
    private static Lycoris.Modules.Apps.InstalledSnapshot? LoadAt(string path) =>
        InstalledDetector.LoadCache(path);

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"cache_fuzz_{Guid.NewGuid():N}.json");

    [Fact]
    public void Empty_file_returns_null()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "");
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Truncated_json_returns_null()
    {
        var path = TempFile();
        const string good = """{"CheckedAt":"2026-11-01T12:00:00","Packages":{"a":"1"},"Unmanaged":["b"]}""";
        try
        {
            // Обрезать с конца: теряем скобку и часть данных.
            File.WriteAllText(path, good.Substring(0, good.Length / 2));
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Null_json_returns_null()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "null");
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Wrong_type_string_returns_null()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "\"просто строка\"");
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Wrong_type_array_returns_null()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "[1, 2, 3]");
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Binary_garbage_returns_null()
    {
        var path = TempFile();
        try
        {
            // Записать настоящий бинарный мусор — случайные байты, включая 0x00.
            var bytes = new byte[1024];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(bytes);
            File.WriteAllBytes(path, bytes);
        }
        finally { TryDelete(path); }
        // Должен вернуть null (JSON-парсер бросит, catch вернёт null).
        Assert.Null(LoadAt(path));
    }

    [Fact]
    public void Deep_nesting_returns_null()
    {
        var path = TempFile();
        try
        {
            // Глубокая вложенность (2000 уровней): {"a":{"a":...}} — ломает JsonDocument (DefaultMaxDepth=1024).
            var sb = new StringBuilder();
            for (int i = 0; i < 2000; i++) sb.Append('{');
            sb.Append("\"k\": \"v\"");
            for (int i = 0; i < 2000; i++) sb.Append('}');
            File.WriteAllText(path, sb.ToString());
        }
        finally { TryDelete(path); }
        Assert.Null(LoadAt(path));
    }

    [Fact]
    public void Three_mb_file_returns_null()
    {
        var path = TempFile();
        try
        {
            // 3 МБ пробелов — это невалидный JSON и может превысить лимит SafeIo.
            var chunk = new string(' ', 1024 * 1024);
            var sb = new StringBuilder();
            for (int i = 0; i < 3; i++) sb.Append(chunk);
            File.WriteAllText(path, sb.ToString());
        }
        finally { TryDelete(path); }
        Assert.Null(LoadAt(path));
    }

    [Fact]
    public void Valid_cache_returns_snapshot()
    {
        var path = TempFile();
        try
        {
            const string json = """
            {
              "CheckedAt": "2026-11-01T12:00:00",
              "Packages": { "Valve.Steam": "2.10.91.91", "Microsoft.Edge": "" },
              "Unmanaged": ["Microsoft.EdgeWebView2Runtime"]
            }
            """;
            File.WriteAllText(path, json);
            var snap = LoadAt(path);
            Assert.NotNull(snap);
            Assert.True(snap.FromCache);
            Assert.Equal("2.10.91.91", snap.Packages["Valve.Steam"]);
            Assert.Equal("", snap.Packages["Microsoft.Edge"]);
            Assert.Contains("Microsoft.EdgeWebView2Runtime", snap.Unmanaged);
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Null_packages_field_returns_null()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{"CheckedAt":"2026-11-01T12:00:00","Packages":null,"Unmanaged":[]}""");
            Assert.Null(LoadAt(path));
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public void Nonexistent_file_returns_null()
    {
        var path = TempFile(); // файл не создаётся
        try { Assert.Null(LoadAt(path)); }
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
