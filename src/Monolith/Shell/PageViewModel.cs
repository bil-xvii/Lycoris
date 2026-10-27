using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Core;
using Monolith.UI;

namespace Monolith.Shell;

/// <summary>
/// Base class for every page (one per sidebar item).
/// LoadAsync runs once on first visit; OnReturnAsync on every later visit.
/// Use RunBusyAsync for long operations: it shows the shell progress bar and catches errors.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    private bool _loaded;

    /// <summary>Stable id used for navigation ("dashboard", "optimize", ...).</summary>
    public abstract string Key { get; }
    /// <summary>Two-digit order index ("01"); no longer displayed.</summary>
    public abstract string Index { get; }
    /// <summary>Uppercase Russian title ("ПАНЕЛЬ").</summary>
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    /// <summary>Glyph from <see cref="Glyphs"/>.</summary>
    public abstract string Glyph { get; }

    public bool IsElevated => Elevation.IsElevated;
    public bool IsReadOnly => !Elevation.IsElevated;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    public async Task ActivateAsync()
    {
        try
        {
            if (!_loaded)
            {
                _loaded = true;
                await LoadAsync();
            }
            else
            {
                await OnReturnAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Title}: ошибка загрузки", ex);
            Ui.Toast($"{Title}: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>Called when the user leaves the page (stop timers here).</summary>
    public virtual void Deactivate() { }

    protected virtual Task LoadAsync() => Task.CompletedTask;
    protected virtual Task OnReturnAsync() => Task.CompletedTask;

    /// <summary>Runs work with the busy indicator; exceptions become an error toast + log entry.</summary>
    protected async Task RunBusyAsync(string text, Func<Task> work)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = text;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            Log.Error(text, ex);
            Ui.Toast(ex.Message, ToastKind.Error, text);
        }
        finally
        {
            IsBusy = false;
            BusyText = null;
        }
    }

    private static int _elevationPromptOpen;

    /// <summary>
    /// Returns false when admin rights are missing and offers to relaunch MONOLITH with them on the same page
    /// (one prompt at a time; the action itself is not run in this process).
    /// </summary>
    protected static bool RequireAdmin(string action)
    {
        if (Elevation.IsElevated) return true;
        if (Interlocked.Exchange(ref _elevationPromptOpen, 1) == 0)
        {
            Ui.Post(async () =>
            {
                try
                {
                    if (await Ui.ConfirmAsync("Нужны права администратора",
                            ElevationPromptText(action), confirmText: "ПЕРЕЗАПУСТИТЬ С ПРАВАМИ", cancelText: "НЕ СЕЙЧАС"))
                        Ui.RestartElevated();
                }
                catch (Exception ex)
                {
                    Log.Error("Запрос прав администратора", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _elevationPromptOpen, 0);
                }
            });
        }
        return false;
    }

    /// <summary>Text of the «Нужны права администратора» dialog (pure; exposed for tests).</summary>
    public static string ElevationPromptText(string action) =>
        $"«{action}» меняет настройки Windows, поэтому нужны права администратора. MONOLITH перезапустится с ними " +
        "и откроется на этой же странице — Windows покажет запрос контроля учётных записей (UAC). " +
        "После перезапуска повторите действие.";
}
