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
    /// <summary>Page key opened by the toast's «ОТКРЫТЬ» link; null = no link.</summary>
    public string? NavigateTo { get; init; }
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

/// <summary>A request for a page to scroll to and highlight one of its items (see <see cref="Ui.RequestFocus"/>).</summary>
public sealed record FocusRequest(string PageKey, string ItemId);

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

    /// <summary>
    /// Self-test mode: when set, every confirmation is answered automatically with this value
    /// (the self-test sets false = "Cancel", so nothing can ever be changed) and recorded in <see cref="AutoAnswered"/>.
    /// </summary>
    public static bool? AutoAnswer { get; set; }
    public static List<string> AutoAnswered { get; } = [];

    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;
    private static readonly SemaphoreSlim DialogGate = new(1, 1);

    /// <summary>Asks the user to confirm. Returns true on confirm.</summary>
    public static Task<bool> ConfirmAsync(string title, string message, IEnumerable<string>? details = null,
        string confirmText = "ПРИМЕНИТЬ", string cancelText = "ОТМЕНА", bool danger = false, string? footnote = null)
    {
        if (AutoAnswer is { } auto)
        {
            lock (AutoAnswered) AutoAnswered.Add(title);
            return Task.FromResult(auto);
        }
        return OnUi(async () =>
        {
            if (Host is null) return false;
            // One dialog at a time: a second request waits instead of replacing (and orphaning) the first.
            await DialogGate.WaitAsync();
            try
            {
                var vm = new DialogViewModel
                {
                    Title = title, Message = message, Details = details?.ToList() ?? [],
                    ConfirmText = confirmText, CancelText = cancelText, IsDanger = danger, Footnote = footnote,
                };
                Host.Dialog = vm;
                try { return await vm.Result; }
                finally { if (Host.Dialog == vm) Host.Dialog = null; }
            }
            finally
            {
                DialogGate.Release();
            }
        });
    }

    /// <summary>Shows an informational dialog with a single OK button.</summary>
    public static Task AlertAsync(string title, string message, IEnumerable<string>? details = null)
    {
        if (AutoAnswer is not null)
        {
            lock (AutoAnswered) AutoAnswered.Add(title);
            return Task.CompletedTask;
        }
        return OnUi(async () =>
        {
            if (Host is null) return true;
            await DialogGate.WaitAsync();
            try
            {
                var vm = new DialogViewModel
                {
                    Title = title, Message = message, Details = details?.ToList() ?? [],
                    ConfirmText = "ПОНЯТНО", ShowCancel = false,
                };
                Host.Dialog = vm;
                try { return await vm.Result; }
                finally { if (Host.Dialog == vm) Host.Dialog = null; }
            }
            finally
            {
                DialogGate.Release();
            }
        });
    }

    public static void Toast(string message, ToastKind kind = ToastKind.Info, string? title = null, string? navigateTo = null)
    {
        var d = Dispatcher;
        if (d is null || Host is null) return;
        d.BeginInvoke(() =>
        {
            var item = new ToastItem { Message = message, Kind = kind, Title = title, NavigateTo = navigateTo };
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

    // ───────────── Focus requests (command palette → page) ─────────────

    private static readonly object FocusLock = new();
    private static FocusRequest? _pendingFocus;

    /// <summary>
    /// Raised on the UI thread after <see cref="RequestFocus"/>, once the navigation queued with it has run.
    /// Handlers call <see cref="TakeFocusRequest"/> (only one consumer gets the request).
    /// </summary>
    public static event Action<FocusRequest>? FocusRequested;

    /// <summary>
    /// Asks <paramref name="pageKey"/> to scroll to and highlight <paramref name="itemId"/> (a tweak id on «optimize»,
    /// a service name on «services»). Only a hint for the UI: it never applies anything. A newer request replaces an older one.
    /// Call before <see cref="Navigate"/>.
    /// </summary>
    public static void RequestFocus(string pageKey, string itemId)
    {
        var request = new FocusRequest(pageKey, itemId);
        lock (FocusLock) _pendingFocus = request;
        // Background priority: runs after the Normal-priority navigation queued by Navigate().
        Dispatcher?.BeginInvoke(DispatcherPriority.Background, () => FocusRequested?.Invoke(request));
    }

    /// <summary>Returns and clears the pending item id for <paramref name="pageKey"/>, or null.</summary>
    public static string? TakeFocusRequest(string pageKey)
    {
        lock (FocusLock)
        {
            if (_pendingFocus is not { } r || r.PageKey != pageKey) return null;
            _pendingFocus = null;
            return r.ItemId;
        }
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
