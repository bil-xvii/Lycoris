using Lycoris.Core.Localization;

namespace Lycoris.Core.Diagnostics;

/// <summary>Localized text shown by the report action and its palette entry.</summary>
public static class ProblemReportText
{
    public static string Button => Loc.T("СООБЩИТЬ О ПРОБЛЕМЕ");
    public static string Confirmation => Loc.T("Архив содержит сведения о системе, настройках и журналах работы; имя пользователя, компьютера, почта, адреса, серийные номера и похожие на секреты значения убираются автоматически, но очистка может быть неполной: в журналах могут остаться пути вне вашего профиля (например, на другом диске), названия сетей, программ и устройств. Откройте архив и просмотрите его перед отправкой. Ничего не отправляется — файл сохранится на рабочем столе; если рабочий стол синхронизируется с OneDrive, файл попадёт в облако");
    public static string Readme => Loc.T("Локальный диагностический архив Lycoris. Внутри: report.txt (сведения о системе), settings.json (безопасные настройки), journal.txt (число изменений по видам) и до трёх последних журналов работы. Имя пользователя и компьютера, почта, адреса, серийные номера и похожие на секреты значения убраны автоматически, но полнота очистки не гарантируется: в журналах могут остаться пути вне профиля (например, на другом диске), названия сетей, программ и устройств. Откройте файлы и просмотрите их перед отправкой. Архив не отправляется автоматически.");
    public static string Create => Loc.T("СОЗДАТЬ АРХИВ");
    public static string Busy => Loc.T("Создание отчёта…");
    public static string FileSize => Loc.T("{0} · до {1}");
    public static string LargeSize => Loc.T("1,5 МБ");
    public static string SmallSize => Loc.T("1 КБ");
    public static string DesktopRefused => Loc.T("Отчёт не сохранён: рабочий стол не найден в вашей папке профиля, находится на сетевом диске или перенаправлен ссылкой. Ничего не записано.");
    public static string DesktopUnknownUser => Loc.T("Отчёт не сохранён: не удалось определить пользователя рабочего стола. Запустите Lycoris без прав администратора и повторите.");
    public static string Saved => Loc.T("Отчёт сохранён на рабочем столе.");
    public static string OpenFolder => Loc.T("ОТКРЫТЬ ПАПКУ");
    public static string PaletteDescription => Loc.T("Создать обезличенный архив диагностики на рабочем столе.");
}
