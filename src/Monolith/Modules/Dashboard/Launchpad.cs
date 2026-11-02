using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.UI;

namespace Monolith.Modules.Dashboard;

/// <summary>What one launchpad tile shows: the live value, one muted line, and the labelled button.</summary>
public sealed record TileState(string Value, string Detail, string Button, string ButtonGlyph, bool Primary = false);

/// <summary>Which list of checks the chips under the score show.</summary>
public enum CheckFilter { Warning, Info, Ok }

/// <summary>
/// The six big tiles of «Панель» (ИГРОВОЙ СЕАНС · ОПТИМИЗАЦИЯ · ОЧИСТКА · ВИДЕОКАРТА · ОТКАТ · ПИНГ). A tile is a
/// shortcut: its button runs the command of the page that owns the function (same confirmation, same journal) —
/// the tile only shows the live number and says in words what the button does.
/// </summary>
public sealed partial class LaunchTile : ObservableObject
{
    public LaunchTile(string key, string label, string glyph, ICommand command, TileState initial)
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Command = command;
        _value = initial.Value;
        _detail = initial.Detail;
        _buttonText = initial.Button;
        _buttonGlyph = initial.ButtonGlyph;
        _isPrimary = initial.Primary;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public ICommand Command { get; }

    [ObservableProperty] private string _value;
    [ObservableProperty] private string _detail;
    [ObservableProperty] private string _buttonText;
    [ObservableProperty] private string _buttonGlyph;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSecondary))] private bool _isPrimary;

    public bool IsSecondary => !IsPrimary;

    public void Apply(TileState s)
    {
        Value = s.Value;
        Detail = s.Detail;
        ButtonText = s.Button;
        ButtonGlyph = s.ButtonGlyph;
        IsPrimary = s.Primary;
    }
}

/// <summary>Pure text and decisions of the launchpad (no UI, no system access): unit-tested.</summary>
public static class LaunchpadLogic
{
    private static CultureInfo Ru => Loc.Culture;

    // ───────────── ИГРОВОЙ СЕАНС ─────────────

    public static TileState Game(bool active) => active
        ? new(Loc.T("Идёт"), Loc.T("Схема питания и приоритет игры включены"), Loc.T("СТОП"), Glyphs.Stop)
        : new(Loc.T("Выключен"), Loc.T("Схема питания · приоритет игры"), Loc.T("СТАРТ"), Glyphs.Play, Primary: true);

    // ───────────── ОПТИМИЗАЦИЯ ─────────────

    /// <param name="toChange">How many items of the recommended profile would change now (null = not detected yet).</param>
    /// <param name="total">Items in the recommended profile.</param>
    public static TileState Optimize(int? toChange, int total) => toChange switch
    {
        null => new(Loc.T("Проверка…"), Loc.T("Читаем текущие настройки Windows"), Loc.T("ОТКРЫТЬ СПИСОК"), Glyphs.OpenExternal),
        0 => new(Loc.T("Всё применено"), Loc.Tf("Рекомендуемый профиль · {0} выбрано", total), Loc.T("ОТКРЫТЬ СПИСОК"), Glyphs.OpenExternal),
        { } n => new(Loc.Tf("{0} к изменению", n), Loc.Tf("Рекомендуемый профиль · {0} выбрано", total), Loc.Tf("ПРИМЕНИТЬ · {0}", n), Glyphs.Check),
    };

    // ───────────── ОЧИСТКА ─────────────

    /// <summary>"654,8" → 654.8; "—" and empty → null.</summary>
    public static double? ParseAmount(string? value) =>
        double.TryParse(value?.Replace(' ', ' ').Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// <paramref name="selected"/> is what the clean would delete now («654,8 МБ»); <paramref name="found"/> everything the dry run
    /// found («4,7 ГБ»). Nothing analysed yet = the button runs the (read-only) analysis.
    /// </summary>
    public static TileState Clean(bool working, string selectedValue, string selectedUnit, string foundValue, string foundUnit)
    {
        if (working) return new(Loc.T("Считаем…"), Loc.T("Анализ только читает диск и ничего не удаляет"), Loc.T("ПРОВЕРИТЬ"), Glyphs.Refresh);
        var analysed = ParseAmount(foundValue) is not null;
        if (!analysed) return new("—", Loc.T("Анализ ещё не запускался"), Loc.T("ПРОВЕРИТЬ"), Glyphs.Refresh);
        var selected = ParseAmount(selectedValue) ?? 0;
        if (selected <= 0)
            return new(Loc.T("Нечего чистить"), Loc.Tf("Найдено {0}, в выбранных пунктах — ничего", $"{foundValue} {foundUnit}".TrimEnd()), Loc.T("ПРОВЕРИТЬ СНОВА"), Glyphs.Refresh);
        var size = $"{selectedValue} {selectedUnit}".TrimEnd();
        return new(size, Loc.Tf("Найдено всего {0}", $"{foundValue} {foundUnit}".TrimEnd()), Loc.Tf("ОЧИСТИТЬ · {0}", size), Glyphs.Delete);
    }

    /// <summary>The size for the problem row «ОЧИСТИТЬ 654,8 МБ»; null while nothing is known to delete.</summary>
    public static string? CleanSize(string selectedValue, string selectedUnit) =>
        ParseAmount(selectedValue) is > 0 ? $"{selectedValue} {selectedUnit}".TrimEnd() : null;

    // ───────────── ВИДЕОКАРТА ─────────────

    public enum GpuState { Unchecked, Checking, UpToDate, UpdateReady, Unknown }

    /// <param name="installed">Installed driver version text («621.01»), empty when unknown.</param>
    public static TileState Gpu(GpuState state, string installed, string? gpuName, DateTime? driverDate, string? newVersion, DateTime? checkedAt)
    {
        var installedLine = installed.Length > 0 ? installed : "—";
        var detail = new List<string>();
        if (!string.IsNullOrWhiteSpace(gpuName)) detail.Add(gpuName!.Trim());
        if (driverDate is { Year: > 2000 } d) detail.Add(Loc.Tf("от {0}", Loc.DateText(d)));
        var info = string.Join(" · ", detail);
        return state switch
        {
            GpuState.Checking => new(installedLine, Loc.T("Спрашиваем сайт производителя…"), Loc.T("ПРОВЕРКА…"), Glyphs.Refresh),
            GpuState.UpdateReady when !string.IsNullOrEmpty(newVersion) =>
                new(newVersion!, Loc.Tf("Новый драйвер · сейчас {0}", installedLine), Loc.Tf("СКАЧАТЬ И УСТАНОВИТЬ {0}", newVersion), Glyphs.Download, Primary: true),
            GpuState.UpToDate => new(installedLine, Loc.T("Установлена последняя версия") + (checkedAt is { } t ? $" · {Loc.TimeText(t)}" : ""), Loc.T("ПРОВЕРИТЬ СНОВА"), Glyphs.Refresh),
            _ => new(installedLine, info.Length > 0 ? info : Loc.T("Версия драйвера видеокарты"), Loc.T("ПРОВЕРИТЬ ВЕРСИЮ"), Glyphs.Refresh),
        };
    }

    // ───────────── ОТКАТ ─────────────

    public static TileState Undo(int activeChanges, string? lastBatchTitle, DateTime? lastBatchAt)
    {
        if (activeChanges <= 0 || lastBatchTitle is null)
            return new(Loc.T("Нечего откатывать"), Loc.T("Изменений MONOLITH пока нет"), Loc.T("ОТКРЫТЬ ВОССТАНОВЛЕНИЕ"), Glyphs.History);
        var when = lastBatchAt is { } t ? t.ToString(Loc.IsEnglish ? "MMM d, HH:mm" : "dd.MM HH:mm", Ru) : Loc.T("Последний пакет");
        return new(when, Loc.Tf("{0} · всего {1}", lastBatchTitle, Loc.N(activeChanges, "{0} изм.", "{0} изм.", "{0} изм.")), Loc.T("ОТКАТИТЬ ПОСЛЕДНЕЕ"), Glyphs.Undo);
    }

    // ───────────── ПИНГ ─────────────

    public static TileState Ping(string? lastResult) => string.IsNullOrWhiteSpace(lastResult)
        ? new(Loc.T("не измерен"), Loc.T("Роутер · интернет · DNS"), Loc.T("ЗАМЕРИТЬ"), Glyphs.Network)
        : new(lastResult!.Trim(), Loc.T("Роутер · интернет · DNS"), Loc.T("ЗАМЕРИТЬ СНОВА"), Glyphs.Network);

    // ───────────── Score strip ─────────────

    public static string FixSafeText(int count) => Loc.Tf("ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ · {0}", count);

    /// <summary>The list shown first: problems if any, otherwise advice, otherwise what is fine.</summary>
    public static CheckFilter DefaultFilter(int warnings, int infos) =>
        warnings > 0 ? CheckFilter.Warning : infos > 0 ? CheckFilter.Info : CheckFilter.Ok;

    /// <summary>The line shown when the chosen list is empty — says what that means, never a bare void.</summary>
    public static string EmptyText(CheckFilter filter) => filter switch
    {
        CheckFilter.Warning => Loc.T("Проблем не найдено — ничего исправлять не нужно."),
        CheckFilter.Info => Loc.T("Советов нет: настройки уже подходят этому компьютеру."),
        _ => Loc.T("Пока ни одна проверка не завершилась успешно."),
    };

    /// <summary>The sidebar badge: problems and advice together («2»), null when there are none.</summary>
    public static string? Badge(int warnings, int infos) => warnings + infos is > 0 and var n ? n.ToString(Ru) : null;
}
