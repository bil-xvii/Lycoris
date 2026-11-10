using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Games;

/// <summary>One choice in the «Приоритет программ» level list.</summary>
public sealed record PriorityLevelOption(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One rule row: file name, level, who set it and whether Lycoris may change it (the reason is text, never only a tooltip).</summary>
public sealed record PriorityRuleItem(string Exe, string LevelText, string Note, bool CanRemove);

/// <summary>
/// «Приоритет программ»: persistent per-program CPU priority (IFEO PerfOptions\CpuPriorityClass, see <see cref="PriorityRules"/>).
/// The logic lives in <see cref="PriorityRuleStore"/>; every change is journaled with a restore point and undone by «УДАЛИТЬ» here
/// or the record in «Восстановление».
/// </summary>
public sealed partial class GamesViewModel
{
    public ObservableCollection<PriorityRuleItem> PriorityRuleItems { get; } = [];
    public ObservableCollection<PriorityLevelOption> PriorityLevels { get; } = [];
    [ObservableProperty] private PriorityLevelOption? _selectedPriorityLevel;
    [ObservableProperty] private string _priorityLevelNote = "";
    [ObservableProperty] private bool _isPriorityChanging;

    public bool HasPriorityRules => PriorityRuleItems.Count > 0;
    public bool NoPriorityRules => PriorityRuleItems.Count == 0;
    public string PriorityRulesCount => PriorityRuleItems.Count.ToString();

    /// <summary>The «Высокий»/«Низкий» levels exist only in expert mode; this caption says so next to the list.</summary>
    public string PriorityModeNote => AppSettings.Current.ExpertMode
        ? Loc.T("«Высокий» и «Низкий» — экспертные уровни: могут замедлить другие программы или саму программу.")
        : Loc.T("В простом режиме — «Выше среднего» и «Ниже среднего». «Высокий» и «Низкий» — в режиме «Эксперт».");

    partial void OnSelectedPriorityLevelChanged(PriorityLevelOption? value) =>
        PriorityLevelNote = value is null ? "" : PriorityRules.Explain(value.Value);

    private void FillPriorityLevels()
    {
        var expert = AppSettings.Current.ExpertMode;
        var keep = SelectedPriorityLevel?.Value;
        PriorityLevels.Clear();
        foreach (var v in PriorityRules.OfferedLevels.Where(v => expert || !PriorityRules.IsExpertLevel(v)))
            PriorityLevels.Add(new PriorityLevelOption(v, PriorityRules.Describe(v)));
        SelectedPriorityLevel = PriorityLevels.FirstOrDefault(o => o.Value == keep) ?? PriorityLevels.FirstOrDefault();
        OnPropertyChanged(nameof(PriorityModeNote));
    }

    /// <summary>Reads the rules (read-only, also without admin).</summary>
    private async Task RefreshPriorityRulesAsync()
    {
        FillPriorityLevels();
        try
        {
            var rows = await Task.Run(() => PriorityRuleStore.ReadAll().Select(ToItem).ToList());
            PriorityRuleItems.Clear();
            foreach (var r in rows) PriorityRuleItems.Add(r);
        }
        catch (Exception ex)
        {
            Log.Warn("Приоритет программ: {0}", ex.Message);
        }
        OnPropertyChanged(nameof(HasPriorityRules));
        OnPropertyChanged(nameof(NoPriorityRules));
        OnPropertyChanged(nameof(PriorityRulesCount));
    }

    private static PriorityRuleItem ToItem(PriorityRule r)
    {
        var why = PriorityRules.WhyNot(r.Exe) ?? (r.IsKnown ? null : Loc.T("необычное значение"));
        var note = why is not null ? Loc.Tf("Задано не Lycoris; Lycoris это правило не меняет: {0}.", why)
            : r.ByLycoris ? Loc.T("Задано Lycoris · действует со следующего запуска программы")
            : Loc.T("Задано другой программой или вручную");
        return new PriorityRuleItem(r.Exe, PriorityRules.Describe(r.Value).ToUpper(Loc.Culture), note, why is null);
    }

    /// <summary>Picks a program and sets its rule to the chosen level. Parameter (after a relaunch with rights): the level number.</summary>
    [RelayCommand]
    private async Task AddPriorityRule(string? level)
    {
        if (level is not null && int.TryParse(level, out var lv) && PriorityLevels.FirstOrDefault(o => o.Value == lv) is { } fromReplay)
            SelectedPriorityLevel = fromReplay;
        if (SelectedPriorityLevel is not { } choice) return;
        if (IsPriorityChanging || !RequireAdmin(Loc.Key("Приоритет программы"), "AddPriorityRule", choice.Value.ToString())) return;

        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Lycoris — выберите .exe программы"),
            Filter = Loc.T("Исполняемые файлы (*.exe)|*.exe"),
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = true,
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;

        string path;
        try { path = Path.GetFullPath(dlg.FileName); }
        catch (Exception ex) { Ui.Toast(ex.Message, ToastKind.Warning, "ПРИОРИТЕТ ПРОГРАММ"); return; }
        var exe = Path.GetFileName(path);
        var windows = GamePaths.NormalizeDir(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        var why = GamePaths.IsUnder(path, windows) ? Loc.T("это программа Windows — её приоритет Lycoris не меняет") : PriorityRules.WhyNot(exe);
        if (why is not null)
        {
            Ui.Toast(Loc.Tf("{0}: {1}.", exe, why), ToastKind.Warning, "ПРИОРИТЕТ ПРОГРАММ");
            return;
        }

        var current = await Task.Run(() => RegistryHelper.Read(RegHive.HKLM, PriorityRules.KeyPath(exe), PriorityRules.ValueName) as int?);
        if (current == choice.Value)
        {
            Ui.Toast(Loc.Tf("У {0} уже приоритет «{1}».", exe, choice.Label), ToastKind.Info, "ПРИОРИТЕТ ПРОГРАММ");
            return;
        }
        var ok = await Ui.ConfirmAsync("ПРИОРИТЕТ ПРОГРАММЫ?",
            Loc.Tf("Windows будет запускать {0} с приоритетом «{1}». {2} Подействует со следующего запуска — уже запущенная программа не меняется. " +
                   "Правило действует для всех программ с таким именем файла.", exe, choice.Label, PriorityRules.Explain(choice.Value)),
            [
                Loc.Tf("Файл: {0}", path),
                Loc.Tf(@"Реестр: HKLM\{0}", PriorityRules.KeyPath(exe)),
                Loc.Tf("Параметр {0}: сейчас — {1}", PriorityRules.ValueName, current is { } c ? $"{c} ({PriorityRules.Describe(c)})" : PriorityRules.Describe(null)),
                Loc.Tf("Станет: {0} ({1})", choice.Value, choice.Label),
                Loc.T("Перед изменением создаётся точка восстановления."),
            ],
            "ЗАДАТЬ ПРИОРИТЕТ",
            footnote: "Риск: некоторые античиты проверяют приоритет игры — если игра перестала запускаться, удалите правило. " +
                      "Откат: «УДАЛИТЬ» в этом списке или строка в «Восстановлении» — вернётся ровно то, что было.");
        if (!ok) return;

        await ChangePriorityAsync(exe, choice.Value, Loc.Tf("{0} будет запускаться с приоритетом «{1}».", exe, choice.Label));
    }

    /// <summary>Removes a rule (parameter = the exe file name). Lycoris's own rule is reverted exactly; another program's rule is deleted through the journal.</summary>
    [RelayCommand]
    private async Task RemovePriorityRule(string? exe)
    {
        if (exe is null || PriorityRuleItems.FirstOrDefault(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase)) is not { } row) return;
        if (!row.CanRemove || IsPriorityChanging || !RequireAdmin(Loc.Key("Удаление правила приоритета"), "RemovePriorityRule", row.Exe)) return;
        var ok = await Ui.ConfirmAsync("УДАЛИТЬ ПРАВИЛО ПРИОРИТЕТА?",
            Loc.Tf("{0} снова будет запускаться с обычным приоритетом (со следующего запуска). Если правило задал Lycoris, вернётся ровно то, что было до него.", row.Exe),
            [
                Loc.Tf(@"Реестр: HKLM\{0}", PriorityRules.KeyPath(row.Exe)),
                Loc.Tf("Сейчас: {0} → станет: {1}", row.LevelText, PriorityRules.Describe(null)),
            ],
            "УДАЛИТЬ",
            footnote: "Удаление тоже записывается в журнал и откатывается в «Восстановлении».");
        if (!ok) return;
        await ChangePriorityAsync(row.Exe, null, Loc.Tf("Правило приоритета для {0} удалено.", row.Exe));
    }

    private async Task ChangePriorityAsync(string exe, int? level, string success)
    {
        IsPriorityChanging = true;
        try
        {
            var outcome = await Task.Run(() => PriorityRuleStore.SetAsync(exe, level));
            if (outcome.Success) Ui.Toast(success, ToastKind.Success, "ПРИОРИТЕТ ПРОГРАММ");
            else Ui.Toast(Loc.Tf("Не удалось: {0}", string.Join("; ", outcome.Errors)), ToastKind.Error, "ПРИОРИТЕТ ПРОГРАММ");
        }
        catch (Exception ex)
        {
            Log.Error("Приоритет программы {0}", ex, exe);
            Ui.Toast(ex.Message, ToastKind.Error, "ПРИОРИТЕТ ПРОГРАММ");
        }
        finally
        {
            IsPriorityChanging = false;
            await RefreshPriorityRulesAsync();
        }
    }
}
