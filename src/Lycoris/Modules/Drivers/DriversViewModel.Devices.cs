using System.ServiceProcess;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.UI;

namespace Lycoris.Modules.Drivers;

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
                     .Select(p => new ProblemDeviceItem(p, p.Service))
                     .OrderByDescending(p => p.IsSevere)
                     .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Problems.Add(p);
        HasProblems = Problems.Count > 0;
        OnPropertyChanged(nameof(CanRestoreAllDriverServices));
        RestoreAllDriverServicesCommand.NotifyCanExecuteChanged();
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
            Ui.Toast(Loc.Tf("Скопировано: {0}", id), ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warn("Буфер обмена недоступен: {0}", ex.Message);
            Ui.Toast("Буфер обмена занят другой программой — повторите.", ToastKind.Warning);
        }
    }

    /// <summary>Code 22: turns the device back on (pnputil /enable-device), journaled with /disable-device as the undo.</summary>
    private bool CanModifyDevice(object? target) => CanModify();

    [RelayCommand(CanExecute = nameof(CanModifyDevice))]
    private async Task EnableDeviceAsync(object? target)
    {
        var device = target switch
        {
            ProblemDeviceItem d => d,
            string id when id.Length > 0 => Problems.FirstOrDefault(p => string.Equals(p.DeviceId, id, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        if (device is null || !device.CanEnable) return;
        if (!RequireAdmin(Loc.T("включение устройства"), "EnableDevice", device.DeviceId)) return;
        var pnputil = ProcessRunner.Sys("pnputil.exe");
        var confirmed = await Ui.ConfirmAsync(
            "ВКЛЮЧИТЬ УСТРОЙСТВО?",
            Loc.Tf("«{0}» отключено. Если вы не отключали его намеренно, его можно включить обратно.", device.Name),
            [
                Loc.Tf("Устройство: {0}", device.Name),
                Loc.Tf("ID экземпляра: {0}", device.DeviceId),
                Loc.T("Будет выполнено: pnputil /enable-device \"<ID>\""),
                Loc.T("Перед изменением создаётся точка восстановления (если включено в разделе «Восстановление»)"),
                Loc.T("Отменить можно в «Восстановлении»: устройство будет снова отключено"),
            ],
            confirmText: Loc.T("ВКЛЮЧИТЬ"));
        if (!confirmed) return;

        await RunBusyAsync(OperationClass.Disruptive, Loc.T("Включение устройства…"), async () =>
        {
            var session = await ChangeSession.BeginAsync(Loc.Tf("Включение устройства «{0}»", device.Name));
            var ok = await session.RunCommandAsync(
                target: $"device-enable:{device.DeviceId}",
                file: pnputil, args: $"/enable-device \"{device.DeviceId}\"",
                revertFile: pnputil, revertArgs: $"/disable-device \"{device.DeviceId}\"",
                oldValue: Loc.T("Отключено"), newValue: Loc.T("Включено"),
                source: Source, title: Loc.Tf("Включение устройства «{0}»", device.Name),
                successCodes: new HashSet<int> { 0, ProcessExit.RebootRequired });
            session.Complete();
            if (ok && session.RebootRequired)
                Ui.Toast(Loc.Tf("Устройство «{0}» будет включено после перезагрузки.", device.Name), ToastKind.Success);
            else if (ok) Ui.Toast(Loc.Tf("Устройство «{0}» включено.", device.Name), ToastKind.Success);
            else Ui.Toast(session.Errors.FirstOrDefault() ?? Loc.T("Не удалось включить устройство."), ToastKind.Error);
            await LoadInventoryAsync();
        });
    }

    // ───────────────────────────── Driver service restore ─────────────────────────────

    private bool CanRestoreDriverService(object? target) => target is ProblemDeviceItem item && item.CanRestoreService;

    [RelayCommand(CanExecute = nameof(CanRestoreDriverService))]
    private async Task RestoreDriverServiceAsync(object? target)
    {
        var device = target switch { ProblemDeviceItem d => d, _ => null };
        if (device is null || !device.CanRestoreService) return;
        if (!RequireAdmin(Loc.T("восстановление службы драйвера"), "RestoreDriverService", device.DeviceId)) return;
        if (string.IsNullOrWhiteSpace(device.ServiceName)) return;

        var service = device.ServiceName!;
        var confirmLines = new List<string>
        {
            Loc.Tf("Устройство: {0}", device.Name),
            Loc.Tf("Служба: {0}", service),
            Loc.T("Тип запуска: Отключена → Вручную (Windows запускает драйвер, когда устройство подключено)"),
        };
        if (device.IsHyperVService)
        {
            confirmLines.Add(Loc.T("Эта служба нужна только для виртуальных машин Hyper-V — её часто отключают специально"));
        }
        confirmLines.AddRange([
            Loc.T("Перед изменением создаётся точка восстановления (если включено в разделе «Восстановление»)"),
            Loc.T("Отменить можно в «Восстановлении»: служба будет снова отключена"),
        ]);

        var confirmed = await Ui.ConfirmAsync(
            "ВЕРНУТЬ СЛУЖБУ?",
            Loc.Tf("Служба «{0}» устройства «{1}» была отключена. Восстановить тип запуска на «Вручную»?", service, device.Name),
            confirmLines,
            confirmText: Loc.T("ВЕРНУТЬ"));
        if (!confirmed) return;

        await RunBusyAsync(OperationClass.Disruptive, Loc.T("Восстановление службы драйвера…"), async () =>
        {
            var session = await ChangeSession.BeginAsync(Loc.Tf("Восстановление службы «{0}» устройства «{1}»", service, device.Name));
            var ok = session.SetServiceStart(service, ServiceStartMode.Manual, Source, Loc.Tf("Восстановление службы «{0}»", service));
            session.Complete();
            if (ok)
            {
                Ui.Toast(Loc.Tf("Служба «{0}» восстановлена. Может потребоваться перезагрузка устройства.", service), ToastKind.Success);
            }
            else
            {
                Ui.Toast(session.Errors.FirstOrDefault() ?? Loc.Tf("Не удалось восстановить службу «{0}».", service), ToastKind.Error);
            }
            await LoadInventoryAsync();
        });
    }

    /// <summary>«ИСПРАВИТЬ ВСЕ»: code-32 devices whose driver service can be restored; Hyper-V services are left to a per-device decision.</summary>
    public bool CanRestoreAllDriverServices => Problems.Any(p => p.CanRestoreService && !p.IsHyperVService);

    [RelayCommand(CanExecute = nameof(CanRestoreAllDriverServices))]
    private async Task RestoreAllDriverServicesAsync()
    {
        var devices = Problems.Where(p => p.CanRestoreService && !p.IsHyperVService).ToList();
        if (devices.Count == 0) return;

        var confirmed = await Ui.ConfirmAsync(
            "ИСПРАВИТЬ ВСЕ?",
            Loc.Tf("У {0} устройств отключена служба драйвера. Восстановить тип запуска на «Вручную» для всех?", devices.Count),
            [
                Loc.Tf("Будет исправлено: {0}", devices.Count),
                Loc.T("Службы Hyper-V игнорируются"),
                Loc.T("Перед изменением создаётся точка восстановления (если включено в разделе «Восстановление»)"),
                Loc.T("Отменить можно в «Восстановлении»"),
            ],
            confirmText: Loc.T("ИСПРАВИТЬ ВСЕ"));
        if (!confirmed) return;

        await RunBusyAsync(OperationClass.Disruptive, Loc.T("Восстановление служб драйверов…"), async () =>
        {
            var session = await ChangeSession.BeginAsync(Loc.Tf("Восстановление служб драйверов ({0} устройств)", devices.Count));
            int restored = 0;
            foreach (var d in devices)
            {
                if (string.IsNullOrWhiteSpace(d.ServiceName)) continue;
                var ok = session.SetServiceStart(d.ServiceName!, ServiceStartMode.Manual, Source,
                    Loc.Tf("Восстановление службы «{0}»", d.ServiceName));
                if (ok) restored++;
            }
            session.Complete();
            if (restored > 0)
                Ui.Toast(Loc.Tf("Восстановлено служб: {0}. Может потребоваться перезагрузка устройств.", restored), ToastKind.Success);
            else
                Ui.Toast(Loc.T("Не удалось восстановить ни одной службы."), ToastKind.Error);
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
        InstalledSummary = Loc.Tf("ПОКАЗАНО {0} ИЗ {1}  ·  УСТРОЙСТВ {2}", shown, _installedAll.Count, devices);
        InstalledEmpty = InstalledView is not null && shown == 0 && InstalledError is null;
    }
}
