using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Optimize;

/// <summary>«ЭКСПОРТ» / «ИМПОРТ» of the selection as a .json profile. Import only changes the selection, never applies.</summary>
public sealed partial class OptimizeViewModel
{
    private const string ProfileFilter = "Профиль MONOLITH (*.json)|*.json";
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
            Title = "MONOLITH — сохранить профиль оптимизации",
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
        var json = OptimizeProfileFile.Serialize(ids, CurrentTile.SentenceName, version, DateTimeOffset.UtcNow);
        try
        {
            // The user picked the folder, so the file inherits that folder's permissions and stays readable for them.
            await Task.Run(() => File.WriteAllText(path, json, new UTF8Encoding(false)));
            Log.Success($"Оптимизация: профиль сохранён ({ids.Count} пунктов) — {path}");
            Ui.Toast($"{OptimizeLogic.Plural(ids.Count, "Сохранена", "Сохранены", "Сохранено")} {ids.Count} {OptimizeLogic.Plural(ids.Count, "настройка", "настройки", "настроек")}: {Path.GetFileName(path)}.",
                ToastKind.Success, "ЭКСПОРТ");
        }
        catch (Exception ex)
        {
            Log.Error($"Оптимизация: не удалось сохранить профиль ({ex.Message}).");
            Ui.Toast($"Не удалось сохранить файл: {ex.Message}", ToastKind.Error, "ЭКСПОРТ");
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportProfile))]
    private async Task ImportProfileAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "MONOLITH — открыть профиль оптимизации",
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
            error = $"Не удалось прочитать файл: {ex.Message}";
        }
        if (data is null)
        {
            Log.Warn($"Оптимизация: профиль не загружен — {error}");
            await Ui.AlertAsync("ПРОФИЛЬ НЕ ЗАГРУЖЕН", (error ?? "Файл не распознан.") + " Выбор на странице не изменён.");
            return;
        }

        // Expert and data-loss tweaks have their own warning, so a file never checks them.
        var catalog = _items.Select(i => new ProfileCatalogEntry(i.Id, i.IsApplicable, i.IsAdvanced || i.HasDataLoss));
        var plan = OptimizeProfileFile.Classify(data.Ids, data.InvalidIds, catalog);

        var chosen = plan.Selected.ToHashSet(StringComparer.Ordinal);
        foreach (var i in _items) i.SetChecked(chosen.Contains(i.Id) && i.IsApplicable && !i.IsAdvanced && !i.HasDataLoss);
        Profile = OptimizeProfile.Custom;
        UpdateTileSelection();
        RememberCustomSelection();
        UpdateCounts();
        Log.Info($"Оптимизация: импортирован профиль «{data.Name ?? Path.GetFileName(path)}» — {OptimizeProfileFile.Summary(plan)}");

        var details = new List<string>();
        if (data.Name is not null) details.Add($"Профиль: «{data.Name}»" +
            (data.Created is { } c ? $", создан {c.ToLocalTime():dd.MM.yyyy HH:mm}" : "") +
            (data.AppVersion is not null ? $", MONOLITH {data.AppVersion}" : ""));
        AddGroup(details, "НЕИЗВЕСТНЫЕ", plan.Unknown, id => id);
        AddGroup(details, "НЕ ПОДХОДЯТ ЭТОМУ ПК", plan.NotApplicable, TitleOf);
        AddGroup(details, "ТРЕБУЮТ РУЧНОГО ВЫБОРА", plan.Manual, TitleOf);

        var message = OptimizeProfileFile.Summary(plan) +
                      " Ничего не применено — проверьте список и нажмите «ПРИМЕНИТЬ»." +
                      (plan.Manual.Count > 0 ? " Экспертные и необратимые пункты отметьте вручную: у каждого своё предупреждение." : "");
        await Ui.AlertAsync("ПРОФИЛЬ ЗАГРУЖЕН", message, details);
    }

    private string TitleOf(string id) => _items.FirstOrDefault(i => i.Id == id)?.Title is { } t ? $"{t} ({id})" : id;

    private static void AddGroup(List<string> details, string label, IReadOnlyList<string> ids, Func<string, string> describe)
    {
        if (ids.Count == 0) return;
        var shown = ids.Take(MaxListedIds).Select(describe).ToList();
        var more = ids.Count > shown.Count ? $" и ещё {ids.Count - shown.Count}" : "";
        details.Add($"{label}: {string.Join(", ", shown)}{more}");
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
