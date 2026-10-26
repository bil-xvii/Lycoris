using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Modules.Drivers;

/// <summary>ПРОБЛЕМНЫЕ УСТРОЙСТВА and УСТАНОВЛЕННЫЕ ДРАЙВЕРЫ.</summary>
public sealed partial class DriversViewModel
{
    // ───────────────────────────── Problem devices ─────────────────────────────

    public ObservableCollection<ProblemDeviceItem> Problems { get; } = [];

    [ObservableProperty] private string? _problemsError;
    [ObservableProperty] private bool _hasProblems;

    private void ApplyProblems(IReadOnlyList<ProblemDeviceInfo> reportable, string? error)
    {
        Problems.Clear();
        foreach (var p in reportable
                     .Select(p => new ProblemDeviceItem(p))
                     .OrderByDescending(p => p.IsSevere)
                     .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Problems.Add(p);
        HasProblems = Problems.Count > 0;
        ProblemsError = error;
    }

    [RelayCommand]
    private void FindInWindowsUpdate()
    {
        SelectedTab = DriversTab.Updates;
        if (UpdatesState != UpdatesState.Searching && !IsInstalling) _ = SearchUpdatesAsync();
    }

    [RelayCommand]
    private void CopyHardwareId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        try
        {
            Clipboard.SetText(id);
            Ui.Toast($"Скопировано: {id}", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warn($"Буфер обмена недоступен: {ex.Message}");
            Ui.Toast("Буфер обмена занят другой программой — повторите.", ToastKind.Warning);
        }
    }

    /// <summary>Code 22: turns the device back on (pnputil /enable-device), journaled with /disable-device as the undo.</summary>
    [RelayCommand(CanExecute = nameof(CanModify))]
    private async Task EnableDeviceAsync(ProblemDeviceItem? device)
    {
        if (device is null || !device.CanEnable) return;
        if (!RequireAdmin("включение устройства")) return;
        var pnputil = ProcessRunner.Sys("pnputil.exe");
        var confirmed = await Ui.ConfirmAsync(
            "ВКЛЮЧИТЬ УСТРОЙСТВО?",
            $"«{device.Name}» отключено. Если вы не отключали его намеренно, его можно включить обратно.",
            [
                $"Устройство: {device.Name}",
                $"ID экземпляра: {device.DeviceId}",
                "Будет выполнено: pnputil /enable-device \"<ID>\"",
                "Перед изменением создаётся точка восстановления (если включено в разделе «Восстановление»)",
                "Отменить можно в «Восстановлении»: устройство будет снова отключено",
            ],
            confirmText: "ВКЛЮЧИТЬ");
        if (!confirmed) return;

        await RunBusyAsync("Включение устройства…", async () =>
        {
            var session = await ChangeSession.BeginAsync($"Включение устройства «{device.Name}»");
            var ok = await session.RunCommandAsync(
                target: $"device-enable:{device.DeviceId}",
                file: pnputil, args: $"/enable-device \"{device.DeviceId}\"",
                revertFile: pnputil, revertArgs: $"/disable-device \"{device.DeviceId}\"",
                oldValue: "Отключено", newValue: "Включено",
                source: Source, title: $"Включение устройства «{device.Name}»",
                successCodes: new HashSet<int> { 0, ProcessExit.RebootRequired });
            session.Complete();
            if (ok && session.RebootRequired)
                Ui.Toast($"Устройство «{device.Name}» будет включено после перезагрузки.", ToastKind.Success);
            else if (ok) Ui.Toast($"Устройство «{device.Name}» включено.", ToastKind.Success);
            else Ui.Toast(session.Errors.FirstOrDefault() ?? "Не удалось включить устройство.", ToastKind.Error);
            await LoadInventoryAsync();
        });
    }

    // ───────────────────────────── Installed drivers ─────────────────────────────

    private List<InstalledDriverItem> _installedAll = [];
    private string[] _searchTerms = [];

    [ObservableProperty] private ListCollectionView? _installedView;
    [ObservableProperty] private string _installedSearch = "";
    [ObservableProperty] private bool _hideMicrosoft = true;
    [ObservableProperty] private string _installedSummary = "";
    [ObservableProperty] private string? _installedError;
    [ObservableProperty] private bool _installedEmpty;

    private void ApplyInstalled(IReadOnlyList<SignedDriverInfo> drivers, string? error)
    {
        // Group identical rows (e.g. 16× "AMD Processor" on cpu.inf) — research §2.
        _installedAll = drivers
            .GroupBy(d => (d.DeviceName.ToLowerInvariant(), d.Version, d.InfName.ToLowerInvariant(), d.Provider, d.DeviceClass.ToLowerInvariant()))
            .Select(g => new InstalledDriverItem(g.First(), g.Count()))
            .OrderBy(i => i.IsMicrosoft)
            .ThenBy(i => i.ClassText, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        InstalledError = error;
        InstalledView = new ListCollectionView(_installedAll) { Filter = FilterInstalled };
        UpdateInstalledSummary();
    }

    private bool FilterInstalled(object o)
    {
        if (o is not InstalledDriverItem item) return false;
        if (HideMicrosoft && item.IsMicrosoft) return false;
        return _searchTerms.Length == 0 || item.Matches(_searchTerms);
    }

    partial void OnInstalledSearchChanged(string value)
    {
        _searchTerms = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        InstalledView?.Refresh();
        UpdateInstalledSummary();
    }

    partial void OnHideMicrosoftChanged(bool value)
    {
        InstalledView?.Refresh();
        UpdateInstalledSummary();
    }

    [RelayCommand]
    private void ClearInstalledSearch() => InstalledSearch = "";

    private void UpdateInstalledSummary()
    {
        var shown = InstalledView?.Count ?? 0;
        var devices = _installedAll.Sum(i => i.Count);
        InstalledSummary = $"ПОКАЗАНО {shown} ИЗ {_installedAll.Count}  ·  УСТРОЙСТВ {devices}";
        InstalledEmpty = InstalledView is not null && shown == 0 && InstalledError is null;
    }
}
