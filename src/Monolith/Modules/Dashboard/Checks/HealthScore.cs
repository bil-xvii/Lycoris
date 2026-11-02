namespace Monolith.Modules.Dashboard.Checks;

/// <summary>Health score 0–100 from weighted check results (pure, unit-tested).</summary>
public static class HealthScore
{
    /// <summary>Share of the weight that an informational result costs.</summary>
    public const double InfoFactor = 0.2;

    public static int Calculate(IEnumerable<HealthCheckResult> results)
    {
        double penalty = 0;
        foreach (var r in results)
        {
            var w = Math.Max(0, r.Weight);
            penalty += r.Severity switch
            {
                CheckSeverity.Warning => w,
                CheckSeverity.Info => w * InfoFactor,
                _ => 0,
            };
        }
        return (int)Math.Clamp(Math.Round(100 - penalty, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>Verdict word for the gauge.</summary>
    public static string Verdict(int score) => score switch
    {
        >= 90 => Loc.T("ОТЛИЧНО"),
        >= 70 => Loc.T("ХОРОШО"),
        _ => Loc.T("ТРЕБУЕТ ВНИМАНИЯ"),
    };

    /// <summary>One-line explanation under the verdict.</summary>
    public static string Summary(int warnings, int infos)
    {
        if (warnings == 0 && infos == 0) return Loc.T("Все проверки пройдены. Система настроена правильно.");
        if (warnings == 0) return Loc.T("Проблем не найдено. Есть рекомендации — их стоит прочитать.");
        return warnings == 1
            ? Loc.T("Найдена одна проблема. Исправление описано ниже.")
            : Loc.Tf("Найдено проблем: {0}. Начните с тех, что выше в списке.", warnings);
    }

    /// <summary>Order for display: warnings first (heaviest first), then information, then OK.</summary>
    public static IEnumerable<HealthCheckResult> Ordered(IEnumerable<HealthCheckResult> results) =>
        results.OrderByDescending(r => r.Severity).ThenByDescending(r => r.Weight);
}
