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

    /// <summary>Title / subtitle as shown in the shell (sidebar, palette): translated when the tables know the Russian original.</summary>
    public string DisplayTitle => Loc.Sink(Title);
    public string DisplaySubtitle => Loc.Sink(Subtitle);
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
    public string? CheckedText => CheckedAt is { } t ? Loc.Tf("проверено {0:HH:mm}", t) : null;

    partial void OnCheckedAtChanged(DateTime? value) => OnPropertyChanged(nameof(CheckedText));

    /// <summary>Marks the page's data as read just now (call at the end of a load/refresh).</summary>
    protected void MarkChecked() => CheckedAt = DateTime.Now;

    public bool IsElevated => Elevation.IsElevated;
    public bool IsReadOnly => !Elevation.IsElevated;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;

    /// <summary>Counts the visits that ended: an activation that was waiting for a slow first load must not start work for a page the user already left.</summary>
    private int _leftCount;

    /// <summary>The user leaves the page: marks the visit as over and calls <see cref="Deactivate"/>.</summary>
    public void Leave()
    {
        Interlocked.Increment(ref _leftCount);
        Deactivate();
    }

    public async Task ActivateAsync()
    {
        var left = Volatile.Read(ref _leftCount);
        try
        {
            if (_loadTask is null) await EnsureLoadedAsync();
            else
            {
                // A first load still running is awaited. One that failed while nobody was looking (a warm-up started by the
                // Dashboard) is reported now and retried by this visit instead of leaving an empty page without a word.
                var failed = false;
                try { await _loadTask; }
                catch (Exception ex)
                {
                    failed = true;
                    Log.Error("{0}: ошибка загрузки", ex, Title);
                    Ui.Toast($"{Title}: {ex.Message}", ToastKind.Error);
                }
                if (failed)
                {
                    _loadTask = null;
                    await EnsureLoadedAsync();
                    return;
                }
                // Left while the first load was still running (Deactivate already ran): do not start timers/monitors now.
                if (Volatile.Read(ref _leftCount) != left) return;
                await OnReturnAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error("{0}: ошибка загрузки", ex, Title);
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

    /// <summary>The coordinator this page asks before it runs anything long (tests may replace it).</summary>
    internal OperationCoordinator Coordinator { get; set; } = OperationCoordinator.Instance;

    /// <summary>
    /// Plain Russian text when this page's last start was refused or has to wait («Сейчас идёт установка Steam — дождитесь
    /// окончания», «В очереди: …»). The shell shows it under the title bar with the «ЗАДАЧИ» button; null = nothing to say.
    /// </summary>
    [ObservableProperty] private string? _operationNote;

    /// <summary>
    /// Runs work with the busy indicator; exceptions become an error toast + log entry. Every long or system-changing
    /// operation goes through the <see cref="OperationCoordinator"/> with its <paramref name="kind"/>: when it conflicts
    /// with a running operation it waits in the visible queue or is refused with a plain reason (<see cref="OperationNote"/>).
    /// </summary>
    protected Task RunBusyAsync(OperationClass kind, string text, Func<Task> work, bool quiet = false) =>
        RunBusyAsync(kind, text, _ => work(), quiet);

    /// <summary>As above; the work gets its lease (progress text, safe cancel handler, cancellation token).</summary>
    protected async Task RunBusyAsync(OperationClass kind, string text, Func<OperationLease, Task> work, bool quiet = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = text;
        try
        {
            var request = new OperationRequest(kind, OperationName(text)) { Owner = Key, Quiet = quiet };
            var (verdict, _) = Coordinator.Check(request);
            if (verdict == OperationVerdict.Queue)
            {
                BusyText = Loc.Tf("В очереди: {0}", request.Name);
                OperationNote = Loc.Tf("В очереди: «{0}» — начнётся, когда закончится предыдущая задача. Отменить можно в списке «ЗАДАЧИ».", request.Name);
            }
            OperationLease lease;
            try
            {
                lease = await Coordinator.AcquireAsync(request);
            }
            catch (OperationRefusedException ex)
            {
                OperationNote = ex.Message;
                Log.Warn("{0}: не запущено — {1}", request.Name, ex.Message);
                if (kind is not (OperationClass.Measure or OperationClass.Background)) Ui.Toast(ex.Message, ToastKind.Warning, request.Name);
                return;
            }
            catch (OperationTimeoutException ex)
            {
                OperationNote = ex.Message;
                Log.Warn(ex.Message);
                Ui.Toast(ex.Message, ToastKind.Warning, request.Name);
                return;
            }
            catch (OperationCanceledException)
            {
                OperationNote = null;
                Log.Info("{0}: убрано из очереди.", request.Name);
                return;
            }
            using (lease)
            {
                // A read-only refresh does not wipe the reason a user action was refused a moment ago.
                if (kind is not (OperationClass.Measure or OperationClass.Background) || verdict == OperationVerdict.Queue) OperationNote = null;
                BusyText = text;
                using var scope = lease.Scope();
                CurrentLease = lease;
                try { await work(lease); }
                finally { CurrentLease = null; }
            }
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

    /// <summary>
    /// For work that does not run inside <see cref="RunBusyAsync(OperationClass, string, Func{Task}, bool)"/> (its own progress
    /// UI): takes the lease, waiting in the visible queue when needed. Null when refused, timed out or taken out of the queue
    /// (the reason is in <see cref="OperationNote"/>). Dispose the lease in <c>finally</c>; call <see cref="OperationLease.Scope"/>
    /// in the method that runs the work so operations started below it are nested.
    /// <paramref name="cancel"/> = the safe way to stop the work from the «ЗАДАЧИ» list (null = not cancellable).
    /// </summary>
    protected async Task<OperationLease?> AcquireLeaseAsync(OperationClass kind, string name, Action? cancel = null, bool quiet = false)
    {
        var request = new OperationRequest(kind, OperationName(name)) { Owner = Key, Quiet = quiet };
        var (verdict, _) = Coordinator.Check(request);
        if (verdict == OperationVerdict.Queue)
            OperationNote = Loc.Tf("В очереди: «{0}» — начнётся, когда закончится предыдущая задача. Отменить можно в списке «ЗАДАЧИ».", request.Name);
        try
        {
            var lease = await Coordinator.AcquireAsync(request);
            OperationNote = null;
            if (cancel is not null) lease.SetCancelHandler(cancel);
            return lease;
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            OperationNote = ex.Message;
            Log.Warn("{0}: не запущено — {1}", request.Name, ex.Message);
            Ui.Toast(ex.Message, ToastKind.Warning, request.Name);
            return null;
        }
        catch (OperationCanceledException)
        {
            OperationNote = null;
            Log.Info("{0}: убрано из очереди.", request.Name);
            return null;
        }
    }

    /// <summary>The lease of the work this page is running right now (set by <see cref="RunBusyAsync(OperationClass, string, Func{OperationLease, Task}, bool)"/>).</summary>
    protected OperationLease? CurrentLease { get; set; }

    /// <summary>
    /// A modal question in the middle of an operation (restore point failed — continue?). The lease is NOT held while the
    /// dialog waits for the user; it is taken back first in line afterwards. Ask only before the operation changed anything.
    /// </summary>
    protected Task<bool> AskWithoutLeaseAsync(Func<Task<bool>> ask) =>
        CurrentLease is { IsActive: true } lease ? lease.WhileUserDecidesAsync(ask) : ask();

    /// <summary>«Применение изменений…» → «Применение изменений».</summary>
    internal static string OperationName(string busyText) => busyText.TrimEnd('…', '.', ' ');

    /// <summary>
    /// Asks the coordinator BEFORE the page asks the user to confirm something that could not run now: false (with the
    /// reason in <see cref="OperationNote"/> and a toast) when the operation would be refused. Queued classes always pass.
    /// </summary>
    protected bool CanStartNow(OperationClass kind, string name, bool quiet = false)
    {
        var (verdict, reason) = Coordinator.Check(new OperationRequest(kind, name) { Owner = Key, Quiet = quiet });
        if (verdict != OperationVerdict.Refuse) return true;
        OperationNote = reason;
        Ui.Toast(reason!, ToastKind.Warning, name);
        return false;
    }

    private static int _elevationPromptOpen;

    /// <summary>
    /// Returns false when admin rights are missing and offers to relaunch Lycoris with them on the same page
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
        // A parameter the relaunched copy would refuse (too long) must not promise «the action continues by itself».
        RequireAdmin(action, !string.IsNullOrEmpty(parameter) && !AdminIntent.IsParameter(parameter) ? null : new AdminIntent(Key, command, parameter));

    private static int _writableFolderWarned;

    private static bool RequireAdmin(string action, AdminIntent? intent)
    {
        if (Elevation.IsElevated) return true;
        if (Interlocked.Exchange(ref _elevationPromptOpen, 1) == 0)
        {
            Ui.Post(async () =>
            {
                try
                {
                    var text = ElevationPromptText(action, intent is not null);
                    // The program lies in a folder any program can write to: say so (once per run), with the fix.
                    if (Core.Security.SelfIntegrity.InWritableFolder && Interlocked.Exchange(ref _writableFolderWarned, 1) == 0)
                        text += "\n\n" + Core.Security.SelfIntegrity.WritableFolderWarning;
                    var (ok, always) = await Ui.ConfirmOptionAsync("Нужны права администратора",
                        text, ElevationAlwaysOption,
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
    public static string ElevationAlwaysOption => Loc.T("Всегда запускать Lycoris с правами администратора (без лишних перезапусков)");

    /// <summary>Text of the «Нужны права администратора» dialog (pure; exposed for tests).</summary>
    public static string ElevationPromptText(string action, bool replays = false) =>
        Loc.Tf("«{0}» меняет настройки Windows, поэтому нужны права администратора. Lycoris перезапустится с ними " +
               "и откроется на этой же странице — Windows покажет запрос контроля учётных записей (UAC). ", Loc.Sink(action)) +
        (replays ? Loc.T("Действие продолжится само: перед изменением Lycoris покажет обычное подтверждение.")
                 : Loc.T("После перезапуска повторите действие."));
}
