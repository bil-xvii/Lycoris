using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Monolith.Core;

namespace Monolith.Modules.Drivers;

// ───────────── WUA async callback interfaces (IIDs verified in HKCR\Interface, research §1.2) ─────────────

[ComImport, ComVisible(true), Guid("88AEE058-D4B0-4725-A2F1-814A67AE964C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWuSearchCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object searchJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

[ComImport, ComVisible(true), Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWuDownloadProgressChangedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object downloadJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

[ComImport, ComVisible(true), Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWuDownloadCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object downloadJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

[ComImport, ComVisible(true), Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWuInstallationProgressChangedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object installationJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

[ComImport, ComVisible(true), Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWuInstallationCompletedCallback
{
    void Invoke([MarshalAs(UnmanagedType.IUnknown)] object installationJob, [MarshalAs(UnmanagedType.IUnknown)] object callbackArgs);
}

/// <summary>
/// One object serves as both the "progress" and the "completed" callback of a WUA job.
/// WUA calls it on its own RPC threads; managed CCWs are agile, so no marshaling is involved.
/// </summary>
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class WuJobCallback :
    IWuSearchCompletedCallback,
    IWuDownloadProgressChangedCallback, IWuDownloadCompletedCallback,
    IWuInstallationProgressChangedCallback, IWuInstallationCompletedCallback
{
    internal ManualResetEventSlim Completed { get; } = new();
    internal Action<object>? Progress { get; init; }

    void IWuSearchCompletedCallback.Invoke(object job, object args) => Completed.Set();
    void IWuDownloadCompletedCallback.Invoke(object job, object args) => Completed.Set();
    void IWuInstallationCompletedCallback.Invoke(object job, object args) => Completed.Set();
    void IWuDownloadProgressChangedCallback.Invoke(object job, object args) => Report(args);
    void IWuInstallationProgressChangedCallback.Invoke(object job, object args) => Report(args);

    private void Report(object args)
    {
        try { Progress?.Invoke(args); } catch { /* progress must never break the job */ }
    }
}

// ───────────── Data ─────────────

/// <summary>A driver offered by Windows Update (IUpdate + IWindowsDriverUpdate fields).</summary>
internal sealed record WuDriverUpdate(
    string UpdateId,
    string Title,
    string? Version,
    string? DriverClass,
    string? Manufacturer,
    string? Provider,
    string? Model,
    string? HardwareId,
    DateTime? DriverDate,
    int DeviceProblemNumber,
    bool IsOptional,
    double SizeBytes,
    bool EulaAccepted,
    string? EulaText,
    int RebootBehavior,
    string? SupportUrl,
    string? Description);

internal sealed record WuSearchResult(IReadOnlyList<WuDriverUpdate> Updates, bool RebootPending, DateTime When);

internal sealed record WuItemResult(string UpdateId, string Title, int ResultCode, int HResult, bool RebootRequired, string Message)
{
    public bool Success => WuText.IsSuccess(ResultCode);
}

/// <summary>
/// Outcome of an install. <paramref name="Cancelled"/>: the user cancelled and Windows Update stopped (items show what
/// was installed before). <paramref name="StillRunning"/>: Windows Update did not confirm the stop in time and may
/// still be finishing the current driver in the background.
/// </summary>
internal sealed record WuInstallResult(IReadOnlyList<WuItemResult> Items, bool RebootRequired, bool Cancelled, bool StillRunning = false);

internal enum WuStage { Searching, Downloading, Installing }

internal readonly record struct WuProgress(WuStage Stage, int Percent, int Current, int Total);

/// <summary>A failed WUA operation with its HRESULT (mapped to Russian by <see cref="WuText.DescribeHResult"/>).</summary>
internal sealed class WuException(int hresult, string message) : Exception(message)
{
    public int Code { get; } = hresult;
}

/// <summary>
/// Windows Update Agent (Microsoft.Update.Session) for drivers. Every call runs on its own dedicated MTA thread
/// (ThreadingModel=Both: objects created on the WPF STA thread would be proxied and a synchronous Search would freeze
/// the UI). WUA has no timeouts of its own, so each stage has one; Begin*/End* with callbacks make it cancellable
/// through RequestAbort. Nothing here reboots the PC.
/// </summary>
internal static class WindowsUpdateDrivers
{
    private const string ClientId = "MONOLITH";
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbortGrace = TimeSpan.FromMinutes(2);
    /// <summary>After a cancel, Windows Update finishes the driver it is installing — a big GPU package can take minutes.</summary>
    private static readonly TimeSpan InstallAbortGrace = TimeSpan.FromMinutes(15);

    /// <summary>How a WUA job ended. <c>Result</c> is null when there is none (job abandoned or End* failed after an abort).</summary>
    private readonly record struct JobOutcome(object? Result, bool Cancelled, bool TimedOut, bool Abandoned);

    // ───────────── Public API ─────────────

    public static Task<WuSearchResult> SearchAsync(int serverSelection, CancellationToken ct) =>
        RunOnMtaThread("WUA search", SearchTimeout + TimeSpan.FromMinutes(1), ct, token =>
        {
            object session = CreateSession();
            dynamic updates = Search(session, serverSelection, token);
            var list = new List<WuDriverUpdate>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                object update = updates[i];
                WuDriverUpdate? item = Map(update);
                if (item is not null) list.Add(item);
            }
            return new WuSearchResult(list, ReadRebootPending(), DateTime.Now);
        });

    public static Task<bool> IsRebootPendingAsync(CancellationToken ct = default) =>
        RunOnMtaThread("WUA reboot check", TimeSpan.FromSeconds(30), ct, _ => ReadRebootPending());

    /// <summary>
    /// Downloads and installs the chosen driver updates. The caller has already confirmed with the user and
    /// created a restore point. Updates whose EULA was not accepted by the user in the UI are skipped.
    /// Firmware is never installed from here.
    /// Cancelling does not abandon the job: the task completes only when Windows Update has really stopped, and then
    /// returns the per-driver results (what was installed before the stop, reboot flag). It throws
    /// OperationCanceledException only when the cancel came before anything was installed.
    /// </summary>
    public static Task<WuInstallResult> InstallAsync(IReadOnlyCollection<string> updateIds, IReadOnlySet<string> eulaApproved,
        int serverSelection, IProgress<WuProgress> progress, CancellationToken ct) =>
        RunOnMtaThread("WUA install",
            SearchTimeout + AbortGrace + DownloadTimeout + AbortGrace + InstallTimeout + InstallAbortGrace + TimeSpan.FromMinutes(5),
            ct, token =>
        {
            Elevation.Demand("установка драйверов из Windows Update");
            var wanted = new HashSet<string>(updateIds, StringComparer.OrdinalIgnoreCase);
            var results = new Dictionary<string, WuItemResult>(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. Fresh search, so we install exactly what Windows Update offers right now.
            progress.Report(new WuProgress(WuStage.Searching, 0, 0, wanted.Count));
            dynamic session = CreateSession();
            dynamic found = Search((object)session, serverSelection, token);
            dynamic coll = New("Microsoft.Update.UpdateColl");
            var order = new List<string>();
            int foundCount = found.Count;
            for (var i = 0; i < foundCount; i++)
            {
                var u = found[i];
                string id = u.Identity.UpdateID;
                if (!wanted.Contains(id)) continue;
                string title = Try(() => (string)u.Title) ?? id;
                titles[id] = title;
                if (WuText.IsFirmware(Try(() => (string)u.DriverClass)))
                {
                    results[id] = new WuItemResult(id, title, WuText.OrcAborted, 0, false,
                        "Прошивка — устанавливается через параметры Windows Update или утилиту производителя.");
                    continue;
                }
                if (!(bool)u.EulaAccepted)
                {
                    if (!eulaApproved.Contains(id))
                    {
                        results[id] = new WuItemResult(id, title, WuText.OrcAborted, WuText.EulasDeclined, false,
                            WuText.DescribeHResult(WuText.EulasDeclined));
                        continue;
                    }
                    u.AcceptEula();
                }
                coll.Add(u);
                order.Add(id);
            }
            foreach (var id in wanted.Where(id => !titles.ContainsKey(id)))
                results[id] = new WuItemResult(id, id, WuText.OrcAborted, 0, false,
                    "Windows Update больше не предлагает это обновление — возможно, оно уже установлено.");

            if (order.Count == 0) return new WuInstallResult(results.Values.ToList(), false, false);

            // 2. Download.
            token.ThrowIfCancellationRequested();
            progress.Report(new WuProgress(WuStage.Downloading, 0, 1, order.Count));
            var downloader = session.CreateUpdateDownloader();
            TrySet(() => downloader.ClientApplicationID = ClientId);
            downloader.Updates = coll;
            var dl = RunJob(
                cb => downloader.BeginDownload(new UnknownWrapper(cb), new UnknownWrapper(cb), null),
                job => downloader.EndDownload(job),
                () => downloader.Download(),
                DownloadTimeout, AbortGrace, token,
                args => ReportProgress(progress, WuStage.Downloading, args, order.Count));
            // Nothing has been installed yet, so a download without a result simply ends the operation.
            if (dl.Result is null)
            {
                if (dl.TimedOut) throw new TimeoutException("Загрузка драйверов из Windows Update не завершилась за отведённое время.");
                throw new OperationCanceledException(token);
            }
            dynamic dres = dl.Result;
            bool dlCancelled = dl.Cancelled, dlTimedOut = dl.TimedOut;

            dynamic installColl = New("Microsoft.Update.UpdateColl");
            var toInstall = new List<string>();
            for (var i = 0; i < order.Count; i++)
            {
                var id = order[i];
                var index = i;
                object? r = Try<object?>(() => dres.GetUpdateResult(index));
                int rc = r is null ? WuText.OrcFailed : Try(() => (int)((dynamic)r).ResultCode, WuText.OrcFailed);
                int hr = r is null ? 0 : Try(() => (int)((dynamic)r).HResult);
                dynamic u = coll[index];
                if (WuText.IsSuccess(rc) && Try(() => (bool)u.IsDownloaded))
                {
                    installColl.Add(u);
                    toInstall.Add(id);
                }
                else
                {
                    results[id] = new WuItemResult(id, titles[id], rc == WuText.OrcSucceeded ? WuText.OrcFailed : rc, hr, false,
                        dlCancelled ? "Загрузка отменена." : dlTimedOut ? "Загрузка не завершилась за отведённое время."
                            : "Не удалось загрузить: " + (hr != 0 ? WuText.DescribeHResult(hr) : WuText.ResultText(rc).ToLowerInvariant()));
                }
            }
            if (dlCancelled || toInstall.Count == 0)
                return new WuInstallResult(Ordered(results, wanted), false, dlCancelled);

            // 3. Install.
            token.ThrowIfCancellationRequested();
            dynamic installer = session.CreateUpdateInstaller();
            TrySet(() => installer.ClientApplicationID = ClientId);
            if ((bool)installer.IsBusy)
                throw new WuException(WuText.OperationInProgress, WuText.DescribeHResult(WuText.OperationInProgress));
            if ((bool)installer.RebootRequiredBeforeInstallation)
                throw new InvalidOperationException("Windows ожидает перезагрузки перед установкой. Перезагрузите компьютер и повторите.");
            installer.AllowSourcePrompts = false;
            installer.Updates = installColl;
            progress.Report(new WuProgress(WuStage.Installing, 0, 1, toInstall.Count));
            var ins = RunJob(
                cb => installer.BeginInstall(new UnknownWrapper(cb), new UnknownWrapper(cb), null),
                job => installer.EndInstall(job),
                () => installer.Install(),
                InstallTimeout, InstallAbortGrace, token,
                args => ReportProgress(progress, WuStage.Installing, args, toInstall.Count));

            if (ins.Result is null)
            {
                // Some drivers may already be installed: report honestly instead of "cancelled, nothing changed".
                var message = ins.Abandoned
                    ? "Windows Update ещё завершает установку в фоне — дождитесь окончания и нажмите «Обновить»."
                    : "Установка прервана — Windows Update не сообщил результат. Проверьте «Журнал обновлений» в параметрах Windows.";
                foreach (var id in toInstall)
                    results[id] = new WuItemResult(id, titles[id], ins.Abandoned ? WuText.OrcInProgress : WuText.OrcAborted, 0, false, message);
                return new WuInstallResult(Ordered(results, wanted), ReadRebootPending(), ins.Cancelled, StillRunning: ins.Abandoned);
            }
            dynamic ires = ins.Result;
            bool inCancelled = ins.Cancelled, inTimedOut = ins.TimedOut;

            for (var i = 0; i < toInstall.Count; i++)
            {
                var id = toInstall[i];
                var index = i;
                object? r = Try<object?>(() => ires.GetUpdateResult(index));
                int rc = r is null ? WuText.OrcFailed : Try(() => (int)((dynamic)r).ResultCode, WuText.OrcFailed);
                int hr = r is null ? 0 : Try(() => (int)((dynamic)r).HResult);
                bool reboot = r is not null && Try(() => (bool)((dynamic)r).RebootRequired);
                string message = WuText.IsSuccess(rc)
                    ? reboot ? "Установлено. Требуется перезагрузка." : "Установлено."
                    : inCancelled ? "Установка отменена."
                    : inTimedOut ? "Установка не завершилась за отведённое время."
                    : "Не установлено: " + (hr != 0 ? WuText.DescribeHResult(hr) : WuText.ResultText(rc).ToLowerInvariant());
                results[id] = new WuItemResult(id, titles[id], rc, hr, reboot, message);
            }
            bool rebootAll = Try(() => (bool)ires.RebootRequired) || results.Values.Any(r => r.RebootRequired);
            return new WuInstallResult(Ordered(results, wanted), rebootAll, inCancelled);
        }, observeCancellation: false);

    // ───────────── Internals ─────────────

    private static List<WuItemResult> Ordered(Dictionary<string, WuItemResult> results, IEnumerable<string> wanted) =>
        wanted.Where(results.ContainsKey).Select(id => results[id]).ToList();

    private static void ReportProgress(IProgress<WuProgress> progress, WuStage stage, object args, int total)
    {
        dynamic a = args;
        var p = a.Progress;
        int percent = p.PercentComplete;
        int index = p.CurrentUpdateIndex;
        progress.Report(new WuProgress(stage, Math.Clamp(percent, 0, 100), Math.Clamp(index + 1, 1, Math.Max(1, total)), total));
    }

    private static dynamic CreateSession()
    {
        var session = New("Microsoft.Update.Session");
        TrySet(() => session.ClientApplicationID = ClientId);
        return session;
    }

    private static dynamic Search(dynamic session, int serverSelection, CancellationToken ct)
    {
        var searcher = session.CreateUpdateSearcher();
        TrySet(() => searcher.ClientApplicationID = ClientId);
        searcher.Online = true;
        searcher.ServerSelection = serverSelection;

        var job = RunJob(
            cb => searcher.BeginSearch(WuText.DriverCriteria, new UnknownWrapper(cb), null),
            j => searcher.EndSearch(j),
            () => searcher.Search(WuText.DriverCriteria),
            SearchTimeout, AbortGrace, ct, null);

        if (job.TimedOut) throw new TimeoutException("Windows Update не ответил за 5 минут.");
        if (job.Cancelled || job.Result is null) throw new OperationCanceledException(ct);
        dynamic result = job.Result;
        int rc = result.ResultCode;
        if (!WuText.IsSuccess(rc))
            throw new WuException(0, $"Windows Update не смог выполнить поиск ({WuText.ResultText(rc).ToLowerInvariant()}).");
        return result.Updates;
    }

    /// <summary>
    /// Runs a WUA job through Begin*/End* with a callback (cancellable via RequestAbort, with a timeout).
    /// After an abort it waits up to <paramref name="abortGrace"/> for Windows Update to really stop; if it does not,
    /// the outcome is <c>Abandoned</c> (no result — the job may still be running). If the callback cannot be
    /// marshaled on this system, falls back to the synchronous call.
    /// </summary>
    private static JobOutcome RunJob(
        Func<WuJobCallback, dynamic> begin, Func<dynamic, dynamic> end, Func<dynamic> sync,
        TimeSpan timeout, TimeSpan abortGrace, CancellationToken ct, Action<object>? onProgress)
    {
        ct.ThrowIfCancellationRequested();
        var cb = new WuJobCallback { Progress = onProgress };
        dynamic job;
        try
        {
            job = begin(cb);
        }
        catch (Exception ex) when (IsCallbackFailure(ex))
        {
            Log.Info($"Windows Update: асинхронный вызов недоступен ({ex.GetType().Name}) — используется синхронный.");
            return new JobOutcome((object)sync(), false, false, false);
        }

        bool cancelled = false, timedOut = false;
        var signaled = WaitHandle.WaitAny([cb.Completed.WaitHandle, ct.WaitHandle], timeout);
        if (signaled != 0)
        {
            cancelled = signaled == 1;
            timedOut = signaled == WaitHandle.WaitTimeout;
            try { job.RequestAbort(); } catch { }
            if (!cb.Completed.Wait(abortGrace))
            {
                Log.Warn("Windows Update не подтвердил остановку операции — она может завершаться в фоне.");
                return new JobOutcome(null, cancelled, timedOut, true);
            }
        }

        try
        {
            return new JobOutcome((object)end(job), cancelled, timedOut, false);
        }
        catch (COMException ex) when (cancelled || timedOut || ex.HResult == WuText.OperationCancelled)
        {
            return new JobOutcome(null, cancelled || ex.HResult == WuText.OperationCancelled, timedOut, false);
        }
        finally
        {
            try { job.CleanUp(); } catch { }
        }
    }

    private static bool IsCallbackFailure(Exception ex) => ex switch
    {
        RuntimeBinderException or InvalidCastException or ArgumentException => true,
        COMException c => c.HResult is unchecked((int)0x80004002) /* E_NOINTERFACE */
            or unchecked((int)0x80070057) /* E_INVALIDARG */
            or unchecked((int)0x80020005) /* DISP_E_TYPEMISMATCH */
            or unchecked((int)0x80004003) /* E_POINTER */,
        _ => false,
    };

    private static bool ReadRebootPending()
    {
        try
        {
            var info = New("Microsoft.Update.SystemInfo");
            return (bool)info.RebootRequired;
        }
        catch
        {
            return false;
        }
    }

    private static WuDriverUpdate? Map(dynamic u)
    {
        try
        {
            string id = u.Identity.UpdateID;
            string title = Try(() => (string)u.Title) ?? "";
            bool eula = Try(() => (bool)u.EulaAccepted, true);
            return new WuDriverUpdate(
                id,
                title,
                WuText.VersionFromTitle(title),
                Try(() => (string)u.DriverClass),
                Try(() => (string)u.DriverManufacturer),
                Try(() => (string)u.DriverProvider),
                Try(() => (string)u.DriverModel),
                Try(() => (string)u.DriverHardwareID),
                Try<DateTime?>(() => (DateTime)u.DriverVerDate),
                Try(() => (int)u.DeviceProblemNumber),
                Try(() => (bool)u.BrowseOnly),
                WuText.SizeBytes(Try(() => (object)u.MaxDownloadSize)),
                eula,
                eula ? null : Try(() => (string)u.EulaText),
                Try(() => (int)u.InstallationBehavior.RebootBehavior, 2),
                Try(() => (string)u.SupportUrl),
                Try(() => (string)u.Description));
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows Update: не удалось прочитать обновление: {ex.Message}");
            return null;
        }
    }

    private static dynamic New(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: true)!;
        return Activator.CreateInstance(type)!;
    }

    private static T? Try<T>(Func<T> f, T? fallback = default)
    {
        try { return f(); } catch { return fallback; }
    }

    private static void TrySet(Action a)
    {
        try { a(); } catch { }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a new background MTA thread with an overall timeout. The body receives the
    /// token and decides itself how to stop. With <paramref name="observeCancellation"/> = false the returned task
    /// does NOT complete early on cancel — it waits for the body, so the caller sees what really happened
    /// (used for installs: a driver being installed is always finished by Windows Update).
    /// </summary>
    private static Task<T> RunOnMtaThread<T>(string name, TimeSpan timeout, CancellationToken ct, Func<CancellationToken, T> body,
        bool observeCancellation = true)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.TrySetResult(body(ct)); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return observeCancellation ? tcs.Task.WaitAsync(timeout, ct) : tcs.Task.WaitAsync(timeout);
    }
}
