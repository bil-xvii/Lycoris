using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Monolith.UI;

public enum ToastKind { Info, Success, Warning, Error }

public sealed partial class ToastItem : ObservableObject
{
    public required string Message { get; init; }
    public string? Title { get; init; }
    public ToastKind Kind { get; init; }
    public string Glyph => Kind switch
    {
        ToastKind.Success => Glyphs.Check,
        ToastKind.Warning => Glyphs.Warning,
        ToastKind.Error => Glyphs.Error,
        _ => Glyphs.Info,
    };
    public bool IsAlert => Kind is ToastKind.Warning or ToastKind.Error;
}

/// <summary>In-app modal dialog (no Windows MessageBox anywhere).</summary>
public sealed partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public required string Title { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> Details { get; init; } = [];
    public string ConfirmText { get; init; } = "ПРИМЕНИТЬ";
    public string CancelText { get; init; } = "ОТМЕНА";
    public bool ShowCancel { get; init; } = true;
    public bool IsDanger { get; init; }
    public bool HasDetails => Details.Count > 0;
    public string? Footnote { get; init; }

    public Task<bool> Result => _tcs.Task;

    [RelayCommand]
    private void Confirm() => _tcs.TrySetResult(true);

    [RelayCommand]
    private void Cancel() => _tcs.TrySetResult(false);
}

/// <summary>Implemented by the main window view-model.</summary>
public interface IUiHost
{
    DialogViewModel? Dialog { get; set; }
    ObservableCollection<ToastItem> Toasts { get; }
    void NavigateTo(string pageKey);
}

/// <summary>
/// Static facade for dialogs, toasts and navigation, usable from any view-model.
/// All members are safe to call from background threads.
/// </summary>
public static class Ui
{
    public static IUiHost? Host { get; set; }

    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    /// <summary>Asks the user to confirm. Returns true on confirm.</summary>
    public static Task<bool> ConfirmAsync(string title, string message, IEnumerable<string>? details = null,
        string confirmText = "ПРИМЕНИТЬ", string cancelText = "ОТМЕНА", bool danger = false, string? footnote = null)
    {
        return OnUi(async () =>
        {
            if (Host is null) return true;
            var vm = new DialogViewModel
            {
                Title = title, Message = message, Details = details?.ToList() ?? [],
                ConfirmText = confirmText, CancelText = cancelText, IsDanger = danger, Footnote = footnote,
            };
            Host.Dialog = vm;
            try { return await vm.Result; }
            finally { if (Host.Dialog == vm) Host.Dialog = null; }
        });
    }

    /// <summary>Shows an informational dialog with a single OK button.</summary>
    public static Task AlertAsync(string title, string message, IEnumerable<string>? details = null)
    {
        return OnUi(async () =>
        {
            if (Host is null) return true;
            var vm = new DialogViewModel
            {
                Title = title, Message = message, Details = details?.ToList() ?? [],
                ConfirmText = "ПОНЯТНО", ShowCancel = false,
            };
            Host.Dialog = vm;
            try { return await vm.Result; }
            finally { if (Host.Dialog == vm) Host.Dialog = null; }
        });
    }

    public static void Toast(string message, ToastKind kind = ToastKind.Info, string? title = null)
    {
        var d = Dispatcher;
        if (d is null || Host is null) return;
        d.BeginInvoke(() =>
        {
            var item = new ToastItem { Message = message, Kind = kind, Title = title };
            Host.Toasts.Add(item);
            while (Host.Toasts.Count > 4) Host.Toasts.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 7 : 4.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Host?.Toasts.Remove(item);
            };
            timer.Start();
        });
    }

    public static void Navigate(string pageKey)
    {
        Dispatcher?.BeginInvoke(() => Host?.NavigateTo(pageKey));
    }

    /// <summary>Runs an action on the UI thread.</summary>
    public static void Post(Action action)
    {
        var d = Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    private static Task<bool> OnUi(Func<Task<bool>> func)
    {
        var d = Dispatcher;
        if (d is null) return Task.FromResult(false);
        if (d.CheckAccess()) return func();
        return d.InvokeAsync(func).Task.Unwrap();
    }
}
