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
    /// <summary>Two-digit index shown in the sidebar and header ("01").</summary>
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

    /// <summary>Returns false (and tells the user) when admin rights are missing.</summary>
    protected static bool RequireAdmin(string action)
    {
        if (Elevation.IsElevated) return true;
        Ui.Toast($"Для действия «{action}» нужны права администратора. Нажмите «Перезапустить как администратор».", ToastKind.Warning);
        return false;
    }
}
