using System.Collections.Generic;
using System.IO;
using System.Text;
using Lycoris.Modules.Optimize;
using Xunit;

namespace Lycoris.Tests.Robustness;

/// <summary>
/// Фьюз-тесты повреждённых файлов для CustomSelectionStore — корректная обработка ошибок без исключений.
/// </summary>
public sealed class CustomSelectionStoreFuzzTests : IAsyncDisposable
{
    private readonly string _tmpDir = Path.Combine(Path.GetTempPath(), "Lycoris.FuzzStore." + Guid.NewGuid().ToString("n"));

    public CustomSelectionStoreFuzzTests() => Directory.CreateDirectory(_tmpDir);

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_tmpDir))
        {
            try { Directory.Delete(_tmpDir, true); } catch { /* ignore */ }
        }
        return default;
    }

    private string FilePath(string name) => Path.Combine(_tmpDir, name);

    // ─── Parse (string → HashSet?) — уже без IO ───

    [Theory]
    [InlineData(null)]                       // null-ввод
    [InlineData("")]                          // пустая строка
    [InlineData("   ")]                       // пробелы
    [InlineData("{not json}")]                // не JSON
    [InlineData("\"just_a_string\"")]        // строка вместо массива
    [InlineData("42")]                        // число
    [InlineData("[1, 2, 3]")]                // числа в массиве (ожидаем string[])
    [InlineData("[\"  \", \"valid\", null]")]// смешанные — должен остаться только "valid"
    public void Parse_malformed_returns_null_or_subset(string? input)
    {
        // Parse никогда не бросает — для повреждённых данных возвращает null или подмножество.
        var result = CustomSelectionStore.Parse(input);

        if (string.IsNullOrWhiteSpace(input))
        {
            Assert.Null(result);
            return;
        }

        if (input == "42" || input == "\"just_a_string\"" || input == "{not json}")
        {
            // Не массив — Parse вернёт null
            Assert.Null(result);
            return;
        }

        if (input == "[1, 2, 3]")
        {
            // Числа не приводятся к string[] — null
            Assert.Null(result);
            return;
        }

        // Частично валидный: [null, "valid", "  "] -> {"valid"}
        if (input == "[\"  \", \"valid\", null]")
        {
            Assert.NotNull(result);
            Assert.Equal(new[] { "valid" }, result!);
            return;
        }

        Assert.NotNull(result);
        if (result != null)
        {
            foreach (var item in result)
            {
                Assert.False(string.IsNullOrWhiteSpace(item));
            }
        }
    }

    [Fact]
    public void Parse_valid_array_returns_ids()
    {
        const string json = "[\"id1\",\"  id2  \",null,\"\",\"id3\"]";
        var result = CustomSelectionStore.Parse(json);
        Assert.NotNull(result);
        Assert.Equal(new[] { "id1", "id2", "id3" }, result!);
    }

    // ─── Load (path → HashSet?) — через временную папку ───

    [Fact]
    public void Load_empty_file_returns_null()
    {
        File.WriteAllText(FilePath("empty.json"), "");
        Assert.Null(CustomSelectionStore.Load(FilePath("empty.json")));
    }

    [Fact]
    public void Load_truncated_json_returns_null()
    {
        File.WriteAllText(FilePath("truncated.json"), "[\"id1\",\"i");
        Assert.Null(CustomSelectionStore.Load(FilePath("truncated.json")));
    }

    [Fact]
    public void Load_nonexistent_file_returns_null()
    {
        Assert.Null(CustomSelectionStore.Load(FilePath("no_such_file.json")));
    }

    [Fact]
    public void Load_binary_garbage_returns_null()
    {
        var garbage = new byte[4096];
        using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        rng.GetBytes(garbage);
        File.WriteAllBytes(FilePath("binary.json"), garbage);
        Assert.Null(CustomSelectionStore.Load(FilePath("binary.json")));
    }

    [Fact]
    public void Load_nested_array_5000_no_exception()
    {
        var sb = new StringBuilder();
        sb.Append('[');
        for (int i = 0; i < 5000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"deep\":{\"key\":\"value\"}}");
        }
        sb.Append(']');
        File.WriteAllText(FilePath("nested.json"), sb.ToString(), Encoding.UTF8);
        // Главное: не выбросить исключение. Parse вернёт null (объекты ≠ string[]).
        var ex = Record.Exception(() => CustomSelectionStore.Load(FilePath("nested.json")));
        Assert.Null(ex);
    }

    [Fact]
    public void Load_3mb_array_returns_null_or_subset()
    {
        // 3 МБ строк-ключей: массив из ~50k элементов "id_N"
        var sb = new StringBuilder(3 * 1024 * 1024);
        sb.Append('[');
        const int count = 50_000;
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"').Append("id_").Append(i).Append('"');
        }
        sb.Append(']');
        File.WriteAllText(FilePath("huge.json"), sb.ToString(), Encoding.UTF8);
        var result = CustomSelectionStore.Load(FilePath("huge.json"));
        // Должен отработать без исключения, вернуть null или HashSet
        Assert.NotNull(result);
    }

    [Fact]
    public void Load_incorrect_type_object_returns_null()
    {
        File.WriteAllText(FilePath("object.json"), "{\"id1\":true}");
        Assert.Null(CustomSelectionStore.Load(FilePath("object.json")));
    }

    [Fact]
    public void Load_incorrect_type_number_returns_null()
    {
        File.WriteAllText(FilePath("number.json"), "42.5");
        Assert.Null(CustomSelectionStore.Load(FilePath("number.json")));
    }

    [Fact]
    public void Load_valid_array_returns_ids()
    {
        var ids = new[] { "svc1", "svc2", "svc3" };
        var json = CustomSelectionStore.Serialize(ids);
        File.WriteAllText(FilePath("valid.json"), json);
        var result = CustomSelectionStore.Load(FilePath("valid.json"));
        Assert.NotNull(result);
        Assert.Equal(ids, result!);
    }

    // IAsyncDisposable: cleanup handled by test framework lifetime
}
