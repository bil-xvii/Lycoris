using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lycoris.UI;

public enum ToastKind { Info, Success, Warning, Error }

public sealed partial class ToastItem : ObservableObject
{
    // Texts reach the screen through Loc.Sink: a Russian text the tables know is shown in the interface language.
    public required string Message { get => _message; init => _message = Loc.Sink(value); }
    private readonly string _message = "";
    public string? Title { get => _title; init => _title = value is null ? null : Loc.Sink(value); }
    private readonly string? _title;
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

    // Every text goes through Loc.Sink (translated when the tables know the Russian original, otherwise shown as is).
    public required string Title { get => _title; init => _title = Loc.Sink(value); }
    private readonly string _title = "";
    public required string Message { get => _message; init => _message = Loc.Sink(value); }
    private readonly string _message = "";
    public IReadOnlyList<string> Details { get => _details; init => _details = value.Select(Loc.Sink).ToList(); }
    private readonly IReadOnlyList<string> _details = [];
    public string ConfirmText { get => _confirmText; init => _confirmText = Loc.Sink(value); }
    private readonly string _confirmText = Loc.T("ПРИМЕНИТЬ");
    public string CancelText { get => _cancelText; init => _cancelText = Loc.Sink(value); }
    private readonly string _cancelText = Loc.T("ОТМЕНА");
    public bool ShowCancel { get; init; } = true;
    public bool IsDanger { get; init; }
    public bool HasDetails => Details.Count > 0;
    public string? Footnote { get => _footnote; init => _footnote = value is null ? null : Loc.Sink(value); }
    private readonly string? _footnote;

    /// <summary>An optional checkbox under the message («Всегда запускать с правами»); null = none.</summary>
    public string? OptionText { get => _optionText; init => _optionText = value is null ? null : Loc.Sink(value); }
    private readonly string? _optionText;
    public bool HasOption => !string.IsNullOrEmpty(OptionText);
    [ObservableProperty] private bool _optionChecked;

    /// <summary>An optional middle button (the exit dialog: «ОТМЕНИТЬ БЕЗОПАСНЫЕ И ВЫЙТИ»); null = none.</summary>
    public string? ExtraText { get => _extraText; init => _extraText = value is null ? null : Loc.Sink(value); }
    private readonly string? _extraText;
    public bool HasExtra => !string.IsNullOrEmpty(ExtraText);
    /// <summary>True when the user pressed the middle button (the result is then false).</summary>
    public bool ExtraChosen { get; private set; }

    public Task<bool> Result => _tcs.Task;

    [RelayCommand]
    private void Extra()
    {
        ExtraChosen = true;
        _tcs.TrySetResult(false);
    }

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

    /// <summary>
    /// Relaunches Lycoris with admin rights on the current page (UAC prompt); false when declined or failed.
    /// With <paramref name="intent"/> the elevated copy also repeats that action once (see <see cref="Lycoris.Shell.AdminIntent"/>).
    /// </summary>
    bool RestartElevated(Lycoris.Shell.AdminIntent? intent = null);

    /// <summary>
    /// Relaunches Lycoris with the same rights on the current page (a setting that is read at startup, the language, changed);
    /// false when it cannot be done now (operations running) — the host then says why.
    /// </summary>
    bool RestartSameRights();
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
        string? confirmText = null, string? cancelText = null, bool danger = false, string? footnote = null)
    {
        confirmText ??= Loc.T("ПРИМЕНИТЬ");
        cancelText ??= Loc.T("ОТМЕНА");
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

    /// <summary>The answer of a three-button dialog (<see cref="ChooseAsync"/>).</summary>
    public enum Choice { Cancel, Confirm, Extra }

    /// <summary>A dialog with three labelled buttons: cancel, the middle one and confirm. Self-test: answered «cancel».</summary>
    public static Task<Choice> ChooseAsync(string title, string message, IEnumerable<string>? details,
        string confirmText, string? extraText, string cancelText, bool danger = false)
    {
        if (AutoAnswer is not null)
        {
            lock (AutoAnswered) AutoAnswered.Add(title);
            return Task.FromResult(Choice.Cancel);
        }
        return OnUiChoice(async () =>
        {
            if (Host is null) return Choice.Cancel;
            await DialogGate.WaitAsync();
            try
            {
                var vm = new DialogViewModel
                {
                    Title = title, Message = message, Details = details?.ToList() ?? [],
                    ConfirmText = confirmText, CancelText = cancelText, ExtraText = extraText, IsDanger = danger,
                };
                Host.Dialog = vm;
                try
                {
                    var ok = await vm.Result;
                    return ok ? Choice.Confirm : vm.ExtraChosen ? Choice.Extra : Choice.Cancel;
                }
                finally { if (Host.Dialog == vm) Host.Dialog = null; }
            }
            finally
            {
                DialogGate.Release();
            }
        });
    }

    private static Task<Choice> OnUiChoice(Func<Task<Choice>> func)
    {
        var d = Dispatcher;
        if (d is null) return Task.FromResult(Choice.Cancel);
        if (d.CheckAccess()) return func();
        return d.InvokeAsync(func).Task.Unwrap();
    }

    /// <summary>
    /// A confirmation with a checkbox under the message. Returns (confirmed, checkbox). Self-test: answered like
    /// <see cref="ConfirmAsync"/>, the checkbox off.
    /// </summary>
    public static async Task<(bool Confirmed, bool Option)> ConfirmOptionAsync(string title, string message, string optionText,
        string? confirmText = null, string? cancelText = null)
    {
        confirmText ??= Loc.T("ПРИМЕНИТЬ");
        cancelText ??= Loc.T("ОТМЕНА");
        if (AutoAnswer is { } auto)
        {
            lock (AutoAnswered) AutoAnswered.Add(title);
            return (auto, false);
        }
        DialogViewModel? shown = null;
        var ok = await OnUi(async () =>
        {
            if (Host is null) return false;
            await DialogGate.WaitAsync();
            try
            {
                var vm = new DialogViewModel
                {
                    Title = title, Message = message, ConfirmText = confirmText, CancelText = cancelText, OptionText = optionText,
                };
                shown = vm;
                Host.Dialog = vm;
                try { return await vm.Result; }
                finally { if (Host.Dialog == vm) Host.Dialog = null; }
            }
            finally
            {
                DialogGate.Release();
            }
        });
        return (ok, ok && shown is { OptionChecked: true });
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
                    ConfirmText = Loc.T("ПОНЯТНО"), ShowCancel = false,
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

    /// <summary>
    /// The «keep or go back» countdown of a device change (DeviceConfirm.Countdown): a topmost window on a monitor other
    /// than the one being changed, default «go back». True = keep. Cancelling <paramref name="ct"/> closes it (= go back).
    /// Self-test (<see cref="AutoAnswer"/>): recorded and answered «go back» without a window.
    /// </summary>
    public static Task<bool> CountdownConfirmAsync(Lycoris.Core.Devices.CountdownRequest request, CancellationToken ct)
    {
        if (AutoAnswer is not null)
        {
            lock (AutoAnswered) AutoAnswered.Add(request.Title);
            return Task.FromResult(false);
        }
        var d = Dispatcher;
        if (d is null) return Task.FromResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        d.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetResult(false);
                return;
            }
            try
            {
                var window = new CountdownWindow(request);
                var registration = ct.Register(() => d.BeginInvoke(() => window.CloseAsRevert()));
                window.Closed += (_, _) =>
                {
                    registration.Dispose();
                    tcs.TrySetResult(window.Kept);
                };
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                Lycoris.Core.Log.Warn("Окно подтверждения не открылось: {0}", ex.Message);
                tcs.TrySetResult(false);
            }
        });
        return tcs.Task;
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

    /// <summary>
    /// Relaunches Lycoris with admin rights on the current page (see <see cref="IUiHost.RestartElevated"/>); with an
    /// intent, the elevated copy repeats that action once.
    /// </summary>
    public static void RestartElevated(Lycoris.Shell.AdminIntent? intent = null) => Post(() => Host?.RestartElevated(intent));

    /// <summary>Restarts Lycoris with the same rights (language change); see <see cref="IUiHost.RestartSameRights"/>.</summary>
    public static bool RestartApp() => Host?.RestartSameRights() ?? false;

    /// <summary>
    /// Opens a page. Old keys still work: "cleaner" / "tools" open the tab of «maintenance»,
    /// "startup" opens «services» with the focus request "tab:startup".
    /// </summary>
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
