using System.Globalization;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Tests.Localization;

/// <summary>Loc: language resolution, lookup, fallback, formatting, plurals. Uses <see cref="Loc.UseLanguage"/> (per async flow), never the global language.</summary>
public sealed class LocTests
{
    private static readonly Dictionary<string, string> Table = new()
    {
        ["Тестовая кнопка"] = "Test button",
        ["Выбрано {0} из {1}"] = "{1} items, {0} selected",
        ["{0} тестовый файл"] = "{0} test file|{0} test files",
        ["тестовое слово"] = "test word|test words",
    };

    public LocTests() => Loc.RegisterArea("UnitTestArea", Table);

    [Theory]
    [InlineData("auto", "ru-RU", "ru")]
    [InlineData("auto", "ru", "ru")]
    [InlineData("auto", "ru-UA", "ru")]
    [InlineData("auto", "en-US", "en")]
    [InlineData("auto", "de-DE", "en")]
    [InlineData("auto", "uk-UA", "en")]
    [InlineData("auto", "", "en")]
    [InlineData("auto", null, "en")]
    [InlineData("ru", "en-US", "ru")]
    [InlineData("en", "ru-RU", "en")]
    [InlineData("EN", "ru-RU", "en")]
    [InlineData("garbage", "ru-RU", "ru")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData(null, "fr-FR", "en")]
    public void ResolveLanguage_follows_the_setting_then_the_windows_language(string? mode, string? culture, string expected) =>
        Assert.Equal(expected, Loc.ResolveLanguage(mode, culture));

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("RU", "ru")]
    [InlineData("en", "en")]
    [InlineData("klingon", "auto")]
    [InlineData(null, "auto")]
    public void NormalizeMode_keeps_only_known_values(string? mode, string expected) =>
        Assert.Equal(expected, Loc.NormalizeMode(mode));

    [Fact]
    public void Russian_returns_the_text_unchanged_and_records_nothing()
    {
        using var _ = Loc.UseLanguage("ru");
        Loc.ClearMissing();
        Assert.Equal("Тестовая кнопка", Loc.T("Тестовая кнопка"));
        Assert.Equal("Нет такого перевода", Loc.T("Нет такого перевода"));
        Assert.DoesNotContain("Нет такого перевода", Loc.MissingKeys);
    }

    [Fact]
    public void English_translates_known_text_and_falls_back_to_russian_with_a_missing_key()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Test button", Loc.T("Тестовая кнопка"));
        Assert.Equal("Нет такого перевода в тестах", Loc.T("Нет такого перевода в тестах"));
        Assert.Contains("Нет такого перевода в тестах", Loc.MissingKeys);
        Assert.DoesNotContain("Тестовая кнопка", Loc.MissingKeys);
    }

    [Fact]
    public void Text_without_cyrillic_is_never_a_missing_key()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("MONOLITH 1.0", Loc.T("MONOLITH 1.0"));
        Assert.DoesNotContain("MONOLITH 1.0", Loc.MissingKeys);
        Assert.Equal("", Loc.T(""));
    }

    [Fact]
    public void Sink_translates_known_text_and_stays_quiet_about_unknown_text()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("Test button", Loc.Sink("Тестовая кнопка"));
        Assert.Equal("Неизвестный текст из системы", Loc.Sink("Неизвестный текст из системы"));
        Assert.DoesNotContain("Неизвестный текст из системы", Loc.MissingKeys);
        Assert.Equal("", Loc.Sink(null));
    }

    [Fact]
    public void Tf_formats_with_the_russian_format_as_the_key_and_lets_english_reorder()
    {
        using (Loc.UseLanguage("ru")) Assert.Equal("Выбрано 3 из 10", Loc.Tf("Выбрано {0} из {1}", 3, 10));
        using (Loc.UseLanguage("en")) Assert.Equal("10 items, 3 selected", Loc.Tf("Выбрано {0} из {1}", 3, 10));
    }

    [Fact]
    public void Tf_with_a_broken_format_does_not_throw()
    {
        using var _ = Loc.UseLanguage("ru");
        Assert.Equal("Сломано {", Loc.Tf("Сломано {", 1));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(11, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(21, 0)]
    [InlineData(22, 1)]
    [InlineData(25, 2)]
    [InlineData(101, 0)]
    [InlineData(111, 2)]
    [InlineData(-1, 0)]
    public void RuForm_follows_the_russian_plural_rules(long n, int form) => Assert.Equal(form, Loc.RuForm(n));

    [Theory]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(11, "файлов")]
    public void Plural_in_russian_picks_the_form(long n, string expected)
    {
        using var _ = Loc.UseLanguage("ru");
        Assert.Equal(expected, Loc.Plural(n, "файл", "файла", "файлов"));
    }

    [Fact]
    public void N_in_english_uses_one_and_other_from_the_table()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("1 test file", Loc.N(1, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
        Assert.Equal("0 test files", Loc.N(0, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
        Assert.Equal("5 test files", Loc.N(5, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
        Assert.Equal("test word", Loc.Plural(1, "тестовое слово", "тестовых слова", "тестовых слов"));
        Assert.Equal("test words", Loc.Plural(3, "тестовое слово", "тестовых слова", "тестовых слов"));
    }

    [Fact]
    public void N_in_russian_formats_the_number_with_the_right_form()
    {
        using var _ = Loc.UseLanguage("ru");
        Assert.Equal("1 тестовый файл", Loc.N(1, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
        Assert.Equal("3 тестовых файла", Loc.N(3, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
        Assert.Equal("7 тестовых файлов", Loc.N(7, "{0} тестовый файл", "{0} тестовых файла", "{0} тестовых файлов"));
    }

    [Fact]
    public void Plural_without_an_english_entry_falls_back_to_russian_and_is_missing()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("штуки", Loc.Plural(3, "штука", "штуки", "штук"));
        Assert.Contains("штука", Loc.MissingKeys);
    }

    [Fact]
    public void Language_scope_is_per_flow_and_restored()
    {
        Assert.Equal("ru", Loc.Language);
        using (Loc.UseLanguage("en"))
        {
            Assert.True(Loc.IsEnglish);
            using (Loc.UseLanguage("ru")) Assert.Equal("ru", Loc.Language);
            Assert.Equal("en", Loc.Language);
        }
        Assert.Equal("ru", Loc.Language);
    }

    [Fact]
    public async Task Language_scope_does_not_leak_into_a_parallel_flow()
    {
        var other = Task.Run(async () =>
        {
            await Task.Delay(30);
            return Loc.Language;
        });
        using (Loc.UseLanguage("en")) await Task.Delay(60);
        Assert.Equal("ru", await other);
    }

    [Fact]
    public void Culture_and_dates_follow_the_language()
    {
        var t = new DateTime(2026, 11, 2, 15, 40, 0);
        using (Loc.UseLanguage("ru"))
        {
            Assert.Equal("ru-RU", Loc.Culture.Name);
            Assert.Equal("02.11.2026", Loc.DateText(t));
            Assert.Equal("02.11.2026 15:40", Loc.DateTimeText(t));
            Assert.Equal("15:40", Loc.TimeText(t));
            Assert.Equal("0,5 с", Loc.SecondsText(0.5));
        }
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("en-US", Loc.Culture.Name);
            Assert.Equal("Nov 2, 2026", Loc.DateText(t));
            Assert.Equal("Nov 2, 2026 15:40", Loc.DateTimeText(t));
            Assert.Equal("15:40", Loc.TimeText(t));
        }
    }

    [Fact]
    public void Areas_with_a_key_defined_twice_differently_are_reported_as_conflicts()
    {
        Loc.RegisterArea("ConflictA", new Dictionary<string, string> { ["Конфликтный ключ теста"] = "One" });
        Loc.RegisterArea("ConflictB", new Dictionary<string, string> { ["Конфликтный ключ теста"] = "Two" });
        Assert.Contains(Loc.Conflicts, c => c.Contains("Конфликтный ключ теста"));
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("One", Loc.T("Конфликтный ключ теста")); // the first definition wins
    }

    // ───────── The pieces that show text ─────────

    [Fact]
    public void Dialog_defaults_and_buttons_follow_the_language()
    {
        using (Loc.UseLanguage("ru"))
        {
            var d = new DialogViewModel { Title = "Т", Message = "М" };
            Assert.Equal("ПРИМЕНИТЬ", d.ConfirmText);
            Assert.Equal("ОТМЕНА", d.CancelText);
        }
        using (Loc.UseLanguage("en"))
        {
            var d = new DialogViewModel { Title = "Т", Message = "М", Details = ["ПРИМЕНЕНО"] };
            Assert.Equal("APPLY", d.ConfirmText);
            Assert.Equal("CANCEL", d.CancelText);
            Assert.Equal("APPLIED", Assert.Single(d.Details));
        }
    }

    [Fact]
    public void Log_keeps_the_russian_text_for_the_file_and_translates_the_console_line()
    {
        using var _ = Loc.UseLanguage("en");
        LogEntry? seen = null;
        void Handler(LogEntry e) => seen = e;
        Log.Written += Handler;
        try { Log.Info("Выбрано {0} из {1}", 3, 10); }
        finally { Log.Written -= Handler; }
        Assert.NotNull(seen);
        Assert.Equal("Выбрано 3 из 10", seen!.Message);
        Assert.Equal("10 items, 3 selected", seen.Display);
    }

    [Fact]
    public void Sentence_case_converter_keeps_english_titles_in_sentence_case()
    {
        using (Loc.UseLanguage("en"))
        {
            Assert.Equal("Startup & services", SentenceCaseConverter.Apply("STARTUP & SERVICES"));
            Assert.Equal("Windows restore points", SentenceCaseConverter.Apply("WINDOWS RESTORE POINTS"));
            Assert.Equal("GPU", SentenceCaseConverter.Apply("GPU")); // acronyms stay
            Assert.Equal("Already Mixed", SentenceCaseConverter.Apply("Already Mixed"));
        }
        using (Loc.UseLanguage("ru"))
            Assert.Equal("Временные файлы Windows", SentenceCaseConverter.Apply("ВРЕМЕННЫЕ ФАЙЛЫ WINDOWS"));
    }

    [Fact]
    public void Bytes_are_written_in_the_language_units()
    {
        using (Loc.UseLanguage("ru")) Assert.EndsWith(" МБ", SystemProfile.FormatBytes(5 * 1024d * 1024));
        using (Loc.UseLanguage("en")) Assert.EndsWith(" MB", SystemProfile.FormatBytes(5 * 1024d * 1024));
    }

    [Fact]
    public void Palette_entries_show_english_but_stay_searchable_in_russian()
    {
        using var _ = Loc.UseLanguage("en");
        var entry = new Monolith.Shell.PaletteEntry
        {
            Kind = Monolith.Shell.PaletteKind.Page, Title = "ПАНЕЛЬ", Description = "Что нажать, чтобы ПК был готов к игре.", PageKey = "dashboard",
        };
        Assert.Equal("DASHBOARD", entry.Title);
        var found = Monolith.Shell.CommandPaletteSearch.Search([entry], "панель");
        Assert.Single(found);
        Assert.Single(Monolith.Shell.CommandPaletteSearch.Search([entry], "dashboard"));
    }

    [Fact]
    public void Elevation_prompt_text_is_identical_in_russian_to_before()
    {
        using var _ = Loc.UseLanguage("ru");
        Assert.StartsWith("«Применение» меняет настройки Windows, поэтому нужны права администратора.", Monolith.Shell.PageViewModel.ElevationPromptText("Применение"));
    }

    [Fact]
    public void Language_setting_defaults_to_auto_and_the_override_is_never_carried_to_a_relaunch()
    {
        Assert.Equal("auto", new AppSettings().Language);
        var args = Elevation.RelaunchArguments(["--lang", "en", "--page", "restore", "--no-elevate"], ["--page", "settings"], 42);
        Assert.DoesNotContain("--lang", args);
        Assert.DoesNotContain("en", args);
        Assert.Contains("--no-elevate", args);
        Assert.Equal(["--no-elevate", "--page", "settings", "--elevated", "--parent", "42"], args);
    }

    [Fact]
    public void Number_formatting_in_tf_uses_en_us_in_english()
    {
        using var _ = Loc.UseLanguage("en");
        Assert.Equal("1.5 с", Loc.Tf("{0:0.0} с", 1.5)); // no table entry: the Russian text, but formatted with en-US
        Assert.Equal(CultureInfo.GetCultureInfo("en-US").Name, Loc.Culture.Name);
    }
}
