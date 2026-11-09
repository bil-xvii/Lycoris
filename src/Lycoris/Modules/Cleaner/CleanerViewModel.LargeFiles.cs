using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Tools;
using Lycoris.UI;

namespace Lycoris.Modules.Cleaner;

/// <summary>«Что занимает место»: read-only list of the largest files in the user's folder (<see cref="LargeFiles"/>).</summary>
public sealed partial class CleanerViewModel
{
    public CleanerViewModel()
    {
        // «--page cleaner --focus large-files» (QA snapshots, command palette): runs the read-only search, like the dashboard's «doctor» focus
        Ui.FocusRequested += request =>
        {
            // the cleaner lives as a tab of «Обслуживание»: a focus request arrives with the host's key
            if (request.PageKey is "cleaner" or "maintenance" && request.ItemId == "large-files") Ui.Post(() => { if (FindLargeFilesCommand.CanExecute(null)) FindLargeFilesCommand.Execute(null); });
        };
    }

    public ObservableCollection<LargeFile> LargeFilesList { get; } = [];

    [ObservableProperty] private string _largeFilesSummary = Loc.T("Поиск ещё не запускался.");
    [ObservableProperty] private bool _largeFilesFound;

    /// <summary>
    /// Independent of the page's busy state on purpose: the cleaner analyses junk on load, and the page-wide busy lock
    /// would silently swallow this read-only search. The command itself is disabled while it runs (AsyncRelayCommand);
    /// progress and the result are written into the section's own line.
    /// </summary>
    [RelayCommand]
    private async Task FindLargeFilesAsync(CancellationToken ct)
    {
        var root = LargeFiles.DefaultRoot;
        LargeFilesSummary = Loc.Tf("Ищу в «{0}»… обычно 10–30 секунд.", root);
        IProgress<int> progress = new Progress<int>(n =>
            LargeFilesSummary = Loc.Tf("Ищу в «{0}»… просмотрено {1}", root, Loc.N(n, "{0} файл", "{0} файла", "{0} файлов")));
        IReadOnlyList<LargeFile> found;
        try
        {
            found = await Task.Run(() => LargeFiles.Scan(root, ct: ct, progress: progress.Report), ct);
        }
        catch (OperationCanceledException) { LargeFilesSummary = Loc.T("Поиск остановлен."); return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warn("Поиск крупных файлов: {0}", ex.Message);
            LargeFilesSummary = Loc.Tf("Не удалось выполнить поиск: {0}", ex.Message);
            return;
        }
        LargeFilesList.Clear();
        foreach (var f in found) LargeFilesList.Add(f);
        LargeFilesFound = found.Count > 0;
        LargeFilesSummary = found.Count == 0
            ? Loc.Tf("В «{0}» нет файлов больше {1}.", root, SystemProfile.FormatBytes(LargeFiles.DefaultMinBytes))
            : Loc.Tf("{0} — вместе {1}. Lycoris их не удаляет: откройте место файла и решите сами.",
                Loc.N(found.Count, "{0} крупный файл", "{0} крупных файла", "{0} крупных файлов"), SystemProfile.FormatBytes(found.Sum(f => f.Bytes)));
    }

    [RelayCommand]
    private void RevealLargeFile(LargeFile? file)
    {
        if (file is not null) ShellLauncher.RevealFile(file.Path, file.Name);
    }
}
