using System.IO;
using Lycoris.Core.Diagnostics;

namespace Lycoris.Tests.Diagnostics;

/// <summary>«ОТЧЁТ О ПК»: everything is encoded and anonymized; the page carries the score, every check and the system rows.</summary>
public sealed class HealthReportTests
{
    private static HealthReportData Data(string detail = "ok", string value = "AMD Ryzen 7") => new(
        new DateTime(2026, 11, 4, 1, 0, 0), "1.0.13", 88, "Хорошо",
        [new HealthReportCheck("ОЖИДАЕТСЯ ПЕРЕЗАГРУЗКА", detail, "warn"), new HealthReportCheck("ДИСК", "свободно 40 %", "ok")],
        [new HealthReportRow("ПРОЦЕССОР", value, "8 ядер")], "Последний замер …");

    [Fact]
    public void Report_has_score_checks_and_system_rows()
    {
        var html = HealthReport.Build(Data());
        Assert.Contains("88 / 100", html);
        Assert.Contains("ОЖИДАЕТСЯ ПЕРЕЗАГРУЗКА", html);
        Assert.Contains("AMD Ryzen 7", html);
        Assert.Contains("Последний замер", html);
        Assert.StartsWith("<!doctype html>", html);
    }

    [Fact]
    public void Markup_from_the_system_is_encoded_not_executed()
    {
        var html = HealthReport.Build(Data(detail: "<script>alert(1)</script>"));
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void User_and_computer_names_and_profile_paths_are_removed()
    {
        var html = HealthReport.Build(Data(detail: @"C:\Users\Lycoris\AppData\x on DESKTOP-ABC"), userName: "Lycoris", computerName: "DESKTOP-ABC");
        Assert.DoesNotContain("DESKTOP-ABC", html);
        Assert.DoesNotContain(@"C:\Users\Lycoris", html);
    }

    [Fact]
    public void SuggestedFileName_contains_date_and_has_html_extension()
    {
        var name = HealthReport.SuggestedFileName;
        Assert.EndsWith(".html", name);
        Assert.Contains("Отчёт о ПК — ", name);
        Assert.Matches(@"\d{4}-\d{2}-\d{2}_\d{2}-\d{2}", name);
    }
}

public sealed class HealthReportSample
{
    /// <summary>Writes a sample page for a visual check when LYCORIS_REPORT_SAMPLE names a file (skipped otherwise).</summary>
    [Fact]
    public void Write_sample_when_asked()
    {
        var path = Environment.GetEnvironmentVariable("LYCORIS_REPORT_SAMPLE");
        if (string.IsNullOrEmpty(path)) return;
        File.WriteAllText(path, HealthReport.Build(new HealthReportData(DateTime.Now, "1.0.13", 88, "Хорошо — решите 2 пункта",
            [new("ОЖИДАЕТСЯ ПЕРЕЗАГРУЗКА", "Перезапуска ждут: Центр обновления Windows. До перезагрузки часть исправлений не действует.", "warn"),
             new("МНОГО ПРОГРАММ В АВТОЗАПУСКЕ", "Вместе с Windows запускается 14 программ.", "warn"),
             new("ДИСК C: В НОРМЕ", "Свободно 753 ГБ из 931 ГБ.", "ok"), new("СХЕМА ПИТАНИЯ", "Высокая производительность.", "info")],
            [new("ПРОЦЕССОР", "AMD Ryzen 7 9800X3D 8-Core Processor", "8 ядер · 16 потоков · 4,7 ГГц"), new("ВИДЕОКАРТА", "NVIDIA GeForce RTX 5070 Ti", "16 ГБ · драйвер 621.01"),
             new("ПАМЯТЬ", "32 ГБ DDR5-6000", "2 модуля")], "Последний замер 04.11.2026 1:00: автозапуск — 14, свободная память — 18 000 МБ, время загрузки — 21,4 с")));
    }
}

public sealed class HealthReportProductNameTests
{
    /// <summary>Found in the visual check (04.11.2026): the owner's account is also «Lycoris», and the product name became «&lt;user&gt;».</summary>
    [Fact]
    public void Product_name_survives_when_the_account_has_the_same_name()
    {
        var html = HealthReport.Build(new HealthReportData(DateTime.Now, "1.0.13", 90, "Хорошо", [], [], null), userName: "Lycoris", computerName: "PC");
        Assert.Contains("Lycoris 1.0.13", html);
        Assert.DoesNotContain("&lt;user&gt;", html);
    }
}
