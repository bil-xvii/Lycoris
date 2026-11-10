using System.Net;
using System.Text;

namespace Lycoris.Core.Diagnostics;

/// <summary>One line of the report: a check of the «Панель» or a row of the system summary.</summary>
public sealed record HealthReportCheck(string Title, string Detail, string Status);

public sealed record HealthReportRow(string Label, string Value, string Detail);

/// <summary>Everything the PC report shows. Built from what «Панель» already measured; nothing new is read here.</summary>
public sealed record HealthReportData(
    DateTime CreatedAt,
    string LycorisVersion,
    int Score,
    string Verdict,
    IReadOnlyList<HealthReportCheck> Checks,
    IReadOnlyList<HealthReportRow> System,
    string? Measurement);

/// <summary>
/// «ОТЧЁТ О ПК» (roadmap item 8): a readable, self-contained HTML page the owner keeps or shows to a helper.
/// Every text goes through <see cref="ProblemReport.Anonymize"/> (user and computer names, profile paths) and is HTML-encoded;
/// no serial numbers, no network addresses, nothing is sent anywhere. Pure, so the tests check exactly what lands in the file.
/// </summary>
public static class HealthReport
{
    /// <summary>What the confirmation lists before saving.</summary>
    public static IReadOnlyList<string> Contents =>
    [
        Loc.T("Оценка ПК и все проверки «Панели» с пояснениями"),
        Loc.T("Сводка о системе: Windows, процессор, видеокарта, память, диски, мониторы"),
        Loc.T("Последний замер «до / после», если он есть"),
        Loc.T("Без имени пользователя и компьютера, без серийных номеров и сетевых адресов"),
    ];

    /// <summary>Suggested filename for the «ОТЧЁТ О ПК» dialog.</summary>
    public static string SuggestedFileName =>
        Loc.Tf("Отчёт о ПК — {0}.html", DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));

    public static string Build(HealthReportData d, string? userName = null, string? computerName = null)
    {
        // Data from the system is anonymized; the report's own words are only encoded (the owner's account may be named «Lycoris» too).
        string E(string? s) => WebUtility.HtmlEncode(ProblemReport.Anonymize(s ?? "", userName, computerName));
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"").Append(Loc.IsEnglish ? "en" : "ru").Append("\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>").Append(H(Loc.T("Отчёт о ПК — Lycoris"))).Append("</title><style>");
        sb.Append("body{margin:0;background:#0b0b0b;color:#f2f2f2;font:15px/1.5 'Segoe UI',sans-serif;padding:28px}main{max-width:900px;margin:auto}");
        sb.Append("h1{font:700 26px 'Segoe UI Variable Display','Segoe UI';margin:0}h2{font:600 13px 'Segoe UI';letter-spacing:1px;color:#9a9a9a;margin:26px 0 8px}");
        sb.Append(".muted{color:#9a9a9a}.score{font:700 44px Bahnschrift,'Segoe UI';margin:14px 0 0}.row{display:flex;gap:12px;padding:9px 0;border-bottom:1px solid #222}");
        sb.Append(".row b{min-width:150px;display:inline-block}.st{font:700 11px 'Segoe UI';letter-spacing:.6px;padding:3px 8px;border-radius:6px;border:1px solid;white-space:nowrap;height:fit-content}");
        sb.Append(".ok{color:#58be86;border-color:#2f8f5b}.warn{color:#e0aa4a;border-color:#b8893a}.info{color:#78a6d6;border-color:#4f7fb0}");
        sb.Append("@media print{body{background:#fff;color:#000}.muted,h2{color:#555}.row{border-color:#ccc}}</style></head><body><main>");
        sb.Append("<h1>").Append(H(Loc.T("Отчёт о ПК"))).Append("</h1><div class=\"muted\">")
          .Append(H(Loc.Tf("Создан {0} · Lycoris {1}", d.CreatedAt.ToString("g"), d.LycorisVersion))).Append("</div>");
        sb.Append("<div class=\"score\">").Append(d.Score).Append(" / 100</div><div>").Append(E(d.Verdict)).Append("</div>");

        sb.Append("<h2>").Append(H(Loc.T("ПРОВЕРКИ"))).Append("</h2>");
        foreach (var c in d.Checks)
        {
            var cls = c.Status switch { "warn" => "warn", "info" => "info", _ => "ok" };
            var label = c.Status switch { "warn" => Loc.T("ВНИМАНИЕ"), "info" => Loc.T("СОВЕТ"), _ => Loc.T("В НОРМЕ") };
            sb.Append("<div class=\"row\"><span class=\"st ").Append(cls).Append("\">").Append(H(label)).Append("</span><div><b>")
              .Append(E(c.Title)).Append("</b><div class=\"muted\">").Append(E(c.Detail)).Append("</div></div></div>");
        }

        sb.Append("<h2>").Append(H(Loc.T("СИСТЕМА"))).Append("</h2>");
        foreach (var r in d.System)
            sb.Append("<div class=\"row\"><b>").Append(E(r.Label)).Append("</b><div>").Append(E(r.Value))
              .Append(string.IsNullOrWhiteSpace(r.Detail) ? "" : "<div class=\"muted\">" + E(r.Detail) + "</div>").Append("</div></div>");

        if (!string.IsNullOrWhiteSpace(d.Measurement))
            sb.Append("<h2>").Append(H(Loc.T("ЗАМЕР ДО / ПОСЛЕ"))).Append("</h2><div>").Append(E(d.Measurement)).Append("</div>");

        sb.Append("<p class=\"muted\" style=\"margin-top:28px\">").Append(H(Loc.T("Отчёт создан на этом ПК и никуда не отправлялся."))).Append("</p>");
        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
