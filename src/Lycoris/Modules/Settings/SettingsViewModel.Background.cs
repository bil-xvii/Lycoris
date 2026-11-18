using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Core.Background;
using Lycoris.UI;

namespace Lycoris.Modules.Settings;

public sealed partial class SettingsViewModel
{
    private BackgroundStartupState? _backgroundStartup;
    private string _backgroundStartupDetail = Loc.T("Проверяем автозапуск Windows…");
    private int _startupRead;

    public string BackgroundStartupDetail => _backgroundStartupDetail;
    public bool CanSetBackgroundStartup => _backgroundStartup is not null;
    public string BackgroundStartupAction => _backgroundStartup == BackgroundStartupState.Absent ? "on" : "off";
    public string BackgroundStartupButton => _backgroundStartup switch
    {
        BackgroundStartupState.Enabled => Loc.T("ВЫКЛЮЧИТЬ"),
        BackgroundStartupState.Disabled => Loc.T("УДАЛИТЬ ЗАДАЧУ"),
        _ => Loc.T("ВКЛЮЧИТЬ"),
    };

    [RelayCommand]
    private async Task RefreshBackgroundStartupAsync()
    {
        var read = ++_startupRead;
        var (state, error) = await ReadBackgroundStartupAsync();
        if (read != _startupRead) return;
        _backgroundStartup = state;
        _backgroundStartupDetail = state switch
        {
            BackgroundStartupState.Enabled => Loc.T("Автозапуск включён."),
            BackgroundStartupState.Disabled => Loc.T("Задача автозапуска отключена в планировщике заданий Windows."),
            BackgroundStartupState.Absent => Loc.T("Автозапуск выключен."),
            _ => error ?? Loc.T("Не удалось прочитать состояние автозапуска Windows."),
        };
        OnPropertyChanged(nameof(BackgroundStartupDetail));
        OnPropertyChanged(nameof(CanSetBackgroundStartup));
        OnPropertyChanged(nameof(BackgroundStartupAction));
        OnPropertyChanged(nameof(BackgroundStartupButton));
    }

    private static Task<(BackgroundStartupState? State, string? Error)> ReadBackgroundStartupAsync() =>
        Task.Run<(BackgroundStartupState?, string?)>(() =>
        {
            try { return (BackgroundStartupRegistration.ForInteractiveUser().ReadState(), null); }
            catch (InvalidOperationException ex) { return (null, ex.Message); }
            catch (Exception ex)
            {
                Log.Warn("Не удалось проверить автозапуск Windows: {0}", ex.Message);
                return (null, Loc.T("Не удалось прочитать состояние автозапуска Windows."));
            }
        });

    [RelayCommand]
    private async Task SetBackgroundStartupAsync(string? action)
    {
        if (ChangeSession.Locked || action is not ("on" or "off")) return;
        var desired = action == "on" ? BackgroundStartupState.Enabled : BackgroundStartupState.Absent;
        var (state, error) = await ReadBackgroundStartupAsync();
        if (state is null || state == desired)
        {
            await RefreshBackgroundStartupAsync();
            if (error is not null) await Ui.AlertAsync("Автозапуск Windows", error);
            return;
        }
        var title = Loc.T("Автозапуск Windows");
        if (!RequireAdmin(title, "SetBackgroundStartup", action)) return;

        var enable = desired == BackgroundStartupState.Enabled;
        if (!await Ui.ConfirmAsync(enable ? Loc.T("Включить автозапуск Lycoris?") : Loc.T("Удалить задачу автозапуска Lycoris?"),
                enable ? Loc.T("Windows запустит эту копию Lycoris при входе в вашу учётную запись без запроса прав администратора. Окно можно открыть через значок в области уведомлений.")
                    : Loc.T("Lycoris перестанет запускаться при входе в Windows. Уже открытая копия продолжит работу."),
                [Loc.T("Изменение можно отменить в разделе «Восстановление».")],
                confirmText: enable ? Loc.T("ВКЛЮЧИТЬ") : Loc.T("УДАЛИТЬ ЗАДАЧУ"))) return;

        await RunBusyAsync(OperationClass.JournalChange, title, async () =>
        {
            var (applied, failure) = await Task.Run(async () =>
            {
                var session = await ChangeSession.BeginAsync(title, createRestorePoint: false);
                var ok = session.SetBackgroundStartup(state.Value, desired, title);
                session.Complete();
                return (ok, session.Errors.FirstOrDefault());
            });
            await RefreshBackgroundStartupAsync();
            if (applied && _backgroundStartup == desired)
                Ui.Toast(BackgroundStartupDetail, ToastKind.Success);
            else
                Ui.Toast(failure ?? Loc.T("Не удалось подтвердить состояние автозапуска Windows."), ToastKind.Error);
        });
    }
}
