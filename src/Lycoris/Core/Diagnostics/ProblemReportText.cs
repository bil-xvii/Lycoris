using Lycoris.Core.Localization;

namespace Lycoris.Core.Diagnostics;

/// <summary>Localized text shown by the report action and its palette entry.</summary>
public static class ProblemReportText
{
    public static string Button => Loc.T("СООБЩИТЬ О ПРОБЛЕМЕ");
    public static string Confirmation => Loc.T("Архив содержит обезличенные сведения о системе и настройках. Ничего не отправляется — файл сохранится на рабочем столе, отправьте его сами");
    public static string Create => Loc.T("СОЗДАТЬ АРХИВ");
    public static string Busy => Loc.T("Создание отчёта…");
    public static string FileSize => Loc.T("{0} · до {1}");
    public static string LargeSize => Loc.T("1,5 МБ");
    public static string SmallSize => Loc.T("1 КБ");
    public static string Saved => Loc.T("Отчёт сохранён на рабочем столе.");
    public static string OpenFolder => Loc.T("ОТКРЫТЬ ПАПКУ");
    public static string PaletteDescription => Loc.T("Создать обезличенный архив диагностики на рабочем столе.");
}
