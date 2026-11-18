using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Measurements;
using Lycoris.UI;
using Microsoft.Win32;

namespace Lycoris.Modules.Home;

public sealed partial class HomeViewModel
{
    private bool CanExportMeasurements() => HasMeasurements && DemoMeasurements is null;

    partial void OnHasMeasurementsChanged(bool value) => ExportMeasurementsCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanExportMeasurements))]
    private async Task ExportMeasurementsAsync()
    {
        if (!CanExportMeasurements()) return;
        var dialog = new SaveFileDialog
        {
            Title = Loc.T("Сохранить историю замеров"),
            Filter = Loc.T("CSV-файл (*.csv)") + "|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "Lycoris-measurements-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv",
        };
        if (dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;
        try
        {
            var measurements = await Task.Run(_measurementStore.Read);
            if (DemoMeasurements is not null) return;
            if (measurements.Count == 0)
            {
                Ui.Toast(Loc.T("Замеры ещё не записаны"), ToastKind.Info);
                return;
            }
            var csv = MeasurementExport.BuildCsv(measurements);
            await File.WriteAllTextAsync(dialog.FileName, csv, new UTF8Encoding(true));
            Ui.Toast(Loc.T("История замеров сохранена."), ToastKind.Success, openFolderPath: Path.GetDirectoryName(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Toast(Loc.Tf("История замеров не сохранена: {0}", ex.Message), ToastKind.Error);
        }
    }
}
