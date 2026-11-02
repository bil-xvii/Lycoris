using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Optimize;

/// <summary>«ЭКСПОРТ» / «ИМПОРТ» of the selection as a .json profile. Import only changes the selection, never applies.</summary>
public sealed partial class OptimizeViewModel
{
    private static readonly string ProfileFilter = Loc.T("Профиль Lycoris (*.json)|*.json");
    private const int MaxListedIds = 15;

    private bool CanExportProfile() => !IsBusy && _items.Any(i => i.IsChecked);
    private bool CanImportProfile() => !IsBusy && !IsDetecting && _detected && _items.Count > 0;

    [RelayCommand(CanExecute = nameof(CanExportProfile))]
    private async Task ExportProfileAsync()
    {
        var ids = _items.Where(i => i.IsChecked).Select(i => i.Id).ToList();
        if (ids.Count == 0)
        {
            Ui.Toast("Отметьте хотя бы один пункт — пустой профиль сохранять незачем.", ToastKind.Info, "ЭКСПОРТ");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = Loc.T("Lycoris — сохранить профиль оптимизации"),
            Filter = ProfileFilter,
            DefaultExt = ".json",
            AddExtension = true,
            FileName = OptimizeProfileFile.DefaultFileName,
            OverwritePrompt = true,
            InitialDirectory = SafeDocumentsDir(),
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;

        var path = dlg.FileName;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        var json = OptimizeProfileFile.Serialize(ids, CurrentTile.SentenceName, version, DateTimeOffset.UtcNow, Profile);
        try
        {
            // The user picked the folder, so the file inherits that folder's permissions and stays readable for them.
            await Task.Run(() => File.WriteAllText(path, json, new UTF8Encoding(false)));
            Log.Success("Оптимизация: профиль сохранён ({0} пунктов) — {1}", ids.Count, path);
            Ui.Toast(Loc.Tf("{0}: {1}.", Loc.N(ids.Count, "Сохранена {0} настройка", "Сохранены {0} настройки", "Сохранено {0} настроек"), Path.GetFileName(path)),
                ToastKind.Success, "ЭКСПОРТ");
        }
        catch (Exception ex)
        {
            Log.Error("Оптимизация: не удалось сохранить профиль ({0}).", ex.Message);
            Ui.Toast(Loc.Tf("Не удалось сохранить файл: {0}", ex.Message), ToastKind.Error, "ЭКСПОРТ");
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportProfile))]
    private async Task ImportProfileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("Lycoris — открыть профиль оптимизации"),
            Filter = ProfileFilter,
            CheckFileExists = true,
            Multiselect = false,
            DereferenceLinks = true,
            InitialDirectory = SafeDocumentsDir(),
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return;
        var path = dlg.FileName;

        ProfileFileData? data;
        string? error;
        try
        {
            (data, error) = await Task.Run(() =>
            {
                var bytes = ReadLimited(path, OptimizeProfileFile.MaxBytes);
                var d = OptimizeProfileFile.Parse(bytes, out var e);
                return (d, e);
            });
        }
        catch (Exception ex)
        {
            data = null;
            error = Loc.Tf("Не удалось прочитать файл: {0}", ex.Message);
        }
        if (data is null)
        {
            Log.Warn("Оптимизация: профиль не загружен — {0}", error);
            await Ui.AlertAsync("ПРОФИЛЬ НЕ ЗАГРУЖЕН", (error ?? Loc.T("Файл не распознан.")) + Loc.T(" Выбор на странице не изменён."));
            return;
        }

        // Expert and data-loss tweaks have their own warning, so a file never checks them.
        var catalog = _items.Select(i => new ProfileCatalogEntry(i.Id, i.IsApplicable, i.IsAdvanced || i.HasDataLoss));
        var plan = OptimizeProfileFile.Classify(data.Ids, data.InvalidIds, catalog);

        var chosen = plan.Selected.ToHashSet(StringComparer.Ordinal);
        foreach (var i in _items) i.SetChecked(chosen.Contains(i.Id) && i.IsApplicable && !i.IsAdvanced && !i.HasDataLoss);
        // Same rules as a profile tile: one checked member per exclusive group (a file may list two — hand-edited or from an
        // older build), then dependent rows the file lists that are applicable only through a now-pending dependency.
        EnforceExclusiveGroups();
        var listed = data.Ids.ToHashSet(StringComparer.Ordinal);
        foreach (var i in _items)
        {
            if (i.IsApplicable || i.Tweak.Requires.Count == 0) continue;
            i.SetChecked(listed.Contains(i.Id) && i.IsApplicableNow && !i.IsAdvanced && !i.HasDataLoss);
        }
        EnforceExclusiveGroups();
        SyncDependents();
        // Report what is actually checked: a dependent kept through its dependency is selected, not «не подходит».
        var checkedIds = _items.Where(i => i.IsChecked).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var surplus = plan.Selected.Where(id => !checkedIds.Contains(id)).ToList();
        plan = plan with
        {
            Selected = plan.Selected.Where(checkedIds.Contains).Concat(plan.NotApplicable.Where(checkedIds.Contains)).ToList(),
            NotApplicable = plan.NotApplicable.Where(id => !checkedIds.Contains(id)).ToList(),
        };
        Profile = OptimizeProfile.Custom;
        UpdateTileSelection();
        RememberCustomSelection();
        UpdateCounts();
        Log.Info("Оптимизация: импортирован профиль «{0}» — {1}", data.Name ?? Path.GetFileName(path), OptimizeProfileFile.Summary(plan));

        var details = new List<string>();
        if (data.Name is not null)
        {
            var profileLine = Loc.Tf("Профиль: «{0}»", data.Name);
            if (data.Created is { } c) profileLine += Loc.Tf(", создан {0}", Loc.DateTimeText(c.ToLocalTime().DateTime));
            if (data.AppVersion is not null) profileLine += Loc.Tf(", Lycoris {0}", data.AppVersion);
            details.Add(profileLine);
        }
        // The file remembers the tile it was exported from; the set still lands in «Свой набор» (it may differ from today's defaults).
        if (data.Scenario is { } scenario)
            details.Add(Loc.Tf("Сохранён из сценария «{0}» — здесь он открыт как «Свой набор».", OptimizeLogic.ScenarioName(scenario, _laptop)));
        AddGroup(details, Loc.T("НЕИЗВЕСТНЫЕ"), plan.Unknown, id => id);
        AddGroup(details, Loc.T("НЕ ПОДХОДЯТ ЭТОМУ ПК"), plan.NotApplicable, TitleOf);
        AddGroup(details, Loc.T("ТРЕБУЮТ РУЧНОГО ВЫБОРА"), plan.Manual, TitleOf);
        AddGroup(details, Loc.T("ВЗАИМОИСКЛЮЧАЮЩИЕ — ОСТАВЛЕН ПЕРВЫЙ ПУНКТ ГРУППЫ"), surplus, TitleOf);

        var message = OptimizeProfileFile.Summary(plan) +
                      Loc.T(" Ничего не применено — проверьте список и нажмите «ПРИМЕНИТЬ».") +
                      (plan.Manual.Count > 0 ? Loc.T(" Экспертные и необратимые пункты отметьте вручную: у каждого своё предупреждение.") : "");
        await Ui.AlertAsync("ПРОФИЛЬ ЗАГРУЖЕН", message, details);
    }

    private string TitleOf(string id) => _items.FirstOrDefault(i => i.Id == id)?.Title is { } t ? $"{t} ({id})" : id; // loc:ignore: no Russian text, title is already localized

    private static void AddGroup(List<string> details, string label, IReadOnlyList<string> ids, Func<string, string> describe)
    {
        if (ids.Count == 0) return;
        var shown = ids.Take(MaxListedIds).Select(describe).ToList();
        var more = ids.Count > shown.Count ? Loc.Tf(" и ещё {0}", ids.Count - shown.Count) : "";
        details.Add($"{label}: {string.Join(", ", shown)}{more}"); // loc:ignore: no Russian text (label is already localized)
    }

    /// <summary>Reads at most <paramref name="limit"/> + 1 bytes, so an oversized file is rejected without loading it.</summary>
    private static byte[] ReadLimited(string path, int limit)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length > limit) return new byte[limit + 1];
        var buffer = new byte[limit + 1];
        var total = 0;
        int n;
        while (total < buffer.Length && (n = fs.Read(buffer, total, buffer.Length - total)) > 0) total += n;
        return buffer[..total];
    }

    private static string SafeDocumentsDir()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); }
        catch { return ""; }
    }
}
