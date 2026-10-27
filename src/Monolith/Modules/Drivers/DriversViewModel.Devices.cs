using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

    /// <summary>Reference shown when there are no problems: the codes users meet most often.</summary>
    public IReadOnlyList<ProblemCodeHint> CommonProblemCodes { get; } =
        new[] { 28, 43, 10, 22, 52, 48 }.Select(c => new ProblemCodeHint(DeviceProblemCodes.Describe(c))).ToList();
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
                ChangeSessionRestorePointLine(),
                "Иногда Windows включает устройство только после перезагрузки — MONOLITH скажет об этом",
                "Отменить можно в «Восстановлении»: устройство будет снова отключено",
            ],
            confirmText: "ВКЛЮЧИТЬ");
        if (!confirmed) return;

        await RunBusyAsync("Включение устройства…", async () =>
        {
            var title = $"Включение устройства «{device.Name}»";
            var target = $"device-enable:{device.DeviceId}";
            var session = await ChangeSession.BeginAsync(title);
            var ok = await session.RunCommandAsync(
                target: target,
                file: pnputil, args: $"/enable-device \"{device.DeviceId}\"",
                revertFile: pnputil, revertArgs: $"/disable-device \"{device.DeviceId}\"",
                oldValue: "Отключено", newValue: "Включено",
                source: Source, title: title);

            var afterReboot = false;
            if (!ok && session.Errors.Any(DeviceEnable.IsRebootRequiredError))
            {
                // pnputil exit code 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: the device IS enabled, Windows finishes after a
                // restart. ChangeSession counts every non-zero exit as a failure and dropped the undo record, so the
                // undo is journaled again; the forward command is a read-only query that confirms the device exists.
                session.Errors.RemoveAll(DeviceEnable.IsRebootRequiredError);
                Log.Info($"{title}: pnputil вернул 3010 — устройство будет включено после перезагрузки.");
                ok = afterReboot = await session.RunCommandAsync(
                    target: target,
                    file: pnputil, args: $"/enum-devices /instanceid \"{device.DeviceId}\"",
                    revertFile: pnputil, revertArgs: $"/disable-device \"{device.DeviceId}\"",
                    oldValue: "Отключено", newValue: "Включено (после перезагрузки)",
                    source: Source, title: title);
            }
            session.Complete();
            if (afterReboot)
                Ui.Toast($"Устройство «{device.Name}» будет включено после перезагрузки. Отменить можно в «Восстановлении».", ToastKind.Warning, "НУЖНА ПЕРЕЗАГРУЗКА");
            else if (ok)
                Ui.Toast($"Устройство «{device.Name}» включено.", ToastKind.Success);
            else
                Ui.Toast(session.Errors.FirstOrDefault() ?? "Не удалось включить устройство.", ToastKind.Error);
            await LoadInventoryAsync();
        });
    }

    /// <summary>The restore point ChangeSession.BeginAsync tries to create — described as it really is.</summary>
    private static string ChangeSessionRestorePointLine() =>
        !AppSettings.Current.CreateRestorePoints
            ? "Точка восстановления не создаётся: это отключено в настройках MONOLITH"
            : RestorePointService.IsEnabled()
                ? "Перед изменением MONOLITH попробует создать точку восстановления системы"
                : "Защита системы отключена — точка восстановления создана не будет";

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

/// <summary>pnputil /enable-device results.</summary>
internal static partial class DeviceEnable
{
    [GeneratedRegex(@"код выхода 3010(?!\d)")]
    private static partial Regex RebootExitRegex();

    /// <summary>
    /// True for the ChangeSession error of a command that exited with 3010 (ERROR_SUCCESS_REBOOT_REQUIRED):
    /// for pnputil that means "done, takes effect after a restart", not a failure.
    /// </summary>
    public static bool IsRebootRequiredError(string error) => RebootExitRegex().IsMatch(error);
}
