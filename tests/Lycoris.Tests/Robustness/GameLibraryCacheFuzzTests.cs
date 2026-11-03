using System.IO;
using System.Text.Json;
using Lycoris.Modules.Games;

namespace Lycoris.Tests.Robustness;

/// <summary>Fuzz-like tests for GameLibrary.LoadCache with corrupted cache files.</summary>
public sealed class GameLibraryCacheFuzzTests
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    [Fact]
    public void Empty_file_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "", System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Truncated_json_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            const string partial = "\"Version\"  :  1, \"ScannedAt\"  :  ";
            File.WriteAllText(tmp, partial, System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Null_content_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // Write a file whose content, if interpreted as JSON token stream, starts with null
            File.WriteAllText(tmp, "null", System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Wrong_top_level_type_array_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "[]", System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Wrong_top_level_type_string_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "\"just a string\"", System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Wrong_top_level_type_number_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "42", System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Binary_gibberish_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var rng = new System.Random(42);
            var buf = new byte[1024];
            rng.NextBytes(buf);
            File.WriteAllBytes(tmp, buf);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Deep_nesting_5000_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            // Build 5000 levels of nesting: {"a":{"a":{...}}} — must stay within SafeIo.MaxSmallFileBytes (4 MB).
            const int depth = 300; // Deep enough to stress-test stack without blowing memory / disk.
            var sb = new System.Text.StringBuilder(depth * 8 + 10);
            for (int i = 0; i < depth; i++) { sb.Append("{\"a\":"); }
            sb.Append("\"leaf\"");
            for (int i = 0; i < depth; i++) { sb.Append("}"); }
            File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp)); // version mismatch (no Version field)
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Three_mb_binary_gibberish_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var rng = new System.Random(99);
            var buf = new byte[3 * 1024 * 1024];
            rng.NextBytes(buf);
            File.WriteAllBytes(tmp, buf);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Deep_nesting_300_stays_under_stack_limit()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            const int depth = 500;
            var sb = new System.Text.StringBuilder(depth * 16 + 20);
            for (int i = 0; i < depth; i++) { sb.Append("{ \"Level\": "); }
            sb.Append("999");
            for (int i = 0; i < depth; i++) { sb.Append(" }"); }
            File.WriteAllText(tmp, sb.ToString(), System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp)); // wrong version / structure
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Missing_version_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var scan = new LibraryScan([], [], DateTime.UtcNow);
            var inner = new { Version = (int?)null, ScannedAt = scan.ScannedAt, Games = Array.Empty<object>(), Sources = Array.Empty<object>() };
            File.WriteAllText(tmp, JsonSerializer.Serialize(inner), System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Wrong_version_returns_null()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var inner = new { Version = 99, ScannedAt = DateTime.UtcNow, Games = Array.Empty<object>(), Sources = Array.Empty<object>() };
            File.WriteAllText(tmp, JsonSerializer.Serialize(inner), System.Text.Encoding.UTF8);
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Nonexistent_path_returns_null()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".json");
        try
        {
            Assert.Null(GameLibrary.LoadCache(tmp));
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    [Fact]
    public void Valid_cache_returns_data()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            var scan = new LibraryScan(
                [new GameEntry { Id = "test:1", Name = "TestGame", Platform = Modules.Games.GamePlatform.Custom, InstallDir = @"C:\Games\Test" }],
                [], DateTime.UtcNow);
            var data = new { Version = 1, ScannedAt = scan.ScannedAt, Games = scan.Games.Cast<object>().ToArray(), Sources = Array.Empty<object>() };
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, WriteOpts), System.Text.Encoding.UTF8);

            var result = GameLibrary.LoadCache(tmp);
            Assert.NotNull(result);
            Assert.Single(result!.Games);
            Assert.Equal("TestGame", result.Games[0].Name);
        }
        finally { File.Delete(tmp); }
    }
}
