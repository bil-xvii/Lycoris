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
    private Task? _loadTask;

    /// <summary>Stable id used for navigation ("dashboard", "optimize", ...).</summary>
    public abstract string Key { get; }
    /// <summary>Two-digit order index ("01"); no longer displayed.</summary>
    public abstract string Index { get; }
    /// <summary>Uppercase Russian title ("ПАНЕЛЬ").</summary>
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    /// <summary>Glyph from <see cref="Glyphs"/>.</summary>
    public abstract string Glyph { get; }

    /// <summary>Sidebar group (ОБЗОР · УСКОРЕНИЕ · ОБСЛУЖИВАНИЕ · ЗАЩИТА); set by the shell.</summary>
    public string NavGroup { get; set; } = "";

    /// <summary>
    /// The short fact shown as a badge next to the page in the sidebar («2», «654 МБ», «71»), or null for none.
    /// Pages set it from values they already have (the last analysis, the journal count, the loaded list) — never
    /// start work only to fill the badge.
    /// </summary>
    [ObservableProperty] private string? _badgeText;

    /// <summary>When the page's data was last read — the header shows it as «проверено HH:MM».</summary>
    [ObservableProperty] private DateTime? _checkedAt;

    /// <summary>«проверено 15:40» for the page header, or null before the first read.</summary>
    public string? CheckedText => CheckedAt is { } t ? $"проверено {t:HH:mm}" : null;

    partial void OnCheckedAtChanged(DateTime? value) => OnPropertyChanged(nameof(CheckedText));

    /// <summary>Marks the page's data as read just now (call at the end of a load/refresh).</summary>
    protected void MarkChecked() => CheckedAt = DateTime.Now;

    public bool IsElevated => Elevation.IsElevated;
    public bool IsReadOnly => !Elevation.IsElevated;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    public async Task ActivateAsync()
    {
        try
        {
            if (_loadTask is null) await EnsureLoadedAsync();
            else
            {
                // A first load still running is awaited; a failed one was reported on that visit and is not repeated.
                if (!_loadTask.IsCompleted)
                {
                    try { await _loadTask; }
                    catch { /* reported by the first visit */ }
                }
                await OnReturnAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"{Title}: ошибка загрузки", ex);
            Ui.Toast($"{Title}: {ex.Message}", ToastKind.Error);
        }
    }

    /// <summary>
    /// Runs <see cref="LoadAsync"/> once (the first visit) and lets callers await it — the shell waits for it before
    /// replaying an action after a relaunch with admin rights or jumping to a row.
    /// </summary>
    public Task EnsureLoadedAsync() => _loadTask ??= LoadAsync();

    /// <summary>True once the first load has finished (successfully or not).</summary>
    public bool IsLoaded => _loadTask is { IsCompleted: true };

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
    /// Prefer the instance overload with a command name: the relaunched copy then repeats the action by itself.
    /// </summary>
    protected static bool RequireAdmin(string action) => RequireAdmin(action, intent: null);

    /// <summary>
    /// Like <see cref="RequireAdmin(string)"/>, but remembers what the user wanted: the elevated copy opens this page
    /// and runs «{<paramref name="command"/>}Command» once with <paramref name="parameter"/> — through the same
    /// command, so with the same confirmation dialog. <paramref name="command"/> is the name without the «Command» suffix
    /// («Apply», «RevertBatch»); the parameter is text only (an id, a tab key).
    /// </summary>
    protected bool RequireAdmin(string action, string command, string? parameter = null) =>
        RequireAdmin(action, new AdminIntent(Key, command, parameter));

    private static bool RequireAdmin(string action, AdminIntent? intent)
    {
        if (Elevation.IsElevated) return true;
        if (Interlocked.Exchange(ref _elevationPromptOpen, 1) == 0)
        {
            Ui.Post(async () =>
            {
                try
                {
                    var (ok, always) = await Ui.ConfirmOptionAsync("Нужны права администратора",
                        ElevationPromptText(action, intent is not null), ElevationAlwaysOption,
                        confirmText: "ПЕРЕЗАПУСТИТЬ С ПРАВАМИ", cancelText: "НЕ СЕЙЧАС");
                    if (!ok) return;
                    if (always) AppSettings.Current.ElevateAtStartup = true;
                    Ui.RestartElevated(intent);
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

    /// <summary>The checkbox of the «Нужны права» dialog (sets <see cref="AppSettings.ElevateAtStartup"/>).</summary>
    public const string ElevationAlwaysOption = "Всегда запускать MONOLITH с правами администратора (без лишних перезапусков)";

    /// <summary>Text of the «Нужны права администратора» dialog (pure; exposed for tests).</summary>
    public static string ElevationPromptText(string action, bool replays = false) =>
        $"«{action}» меняет настройки Windows, поэтому нужны права администратора. MONOLITH перезапустится с ними " +
        "и откроется на этой же странице — Windows покажет запрос контроля учётных записей (UAC). " +
        (replays ? "Действие продолжится само: перед изменением MONOLITH покажет обычное подтверждение."
                 : "После перезапуска повторите действие.");
}
