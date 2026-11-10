using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Lycoris.Core;

namespace Lycoris.Modules.Drivers;

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

internal sealed record WuInstallResult(IReadOnlyList<WuItemResult> Items, bool RebootRequired, bool Cancelled);

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
    private const string ClientId = "Lycoris";
    public static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AbortGrace = TimeSpan.FromMinutes(2);

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
    /// </summary>
    public static Task<WuInstallResult> InstallAsync(IReadOnlyCollection<string> updateIds, IReadOnlySet<string> eulaApproved,
        int serverSelection, IProgress<WuProgress> progress, CancellationToken ct) =>
        // Cancellation is handled inside (RequestAbort) and must not end the await early: the caller keeps waiting
        // for the real result, so drivers installed before the abort and the reboot flag are reported.
        RunOnMtaThread("WUA install", SearchTimeout + DownloadTimeout + InstallTimeout + TimeSpan.FromMinutes(10), ct, observeCancellation: false, token =>
        {
            Elevation.Demand(Loc.T("установка драйверов из Windows Update"));
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
                        Loc.T("Прошивка — устанавливается через параметры Windows Update или утилиту производителя."));
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
                    Loc.T("Windows Update больше не предлагает это обновление — возможно, оно уже установлено."));

            if (order.Count == 0) return new WuInstallResult(results.Values.ToList(), false, false);

            // 2. Download.
            token.ThrowIfCancellationRequested();
            progress.Report(new WuProgress(WuStage.Downloading, 0, 1, order.Count));
            var downloader = session.CreateUpdateDownloader();
            TrySet(() => downloader.ClientApplicationID = ClientId);
            downloader.Updates = coll;
            var (dres, dlCancelled, dlTimedOut) = RunJob(
                cb => downloader.BeginDownload(new UnknownWrapper(cb), new UnknownWrapper(cb), null),
                job => downloader.EndDownload(job),
                () => downloader.Download(),
                DownloadTimeout, token,
                args => ReportProgress(progress, WuStage.Downloading, args, order.Count));

            dynamic installColl = New("Microsoft.Update.UpdateColl");
            var toInstall = new List<string>();
            var failed = new List<(string Id, int Index, int Rc, int Hr)>();
            for (var i = 0; i < order.Count; i++)
            {
                var index = i;
                object? r = Try<object?>(() => dres.GetUpdateResult(index));
                int rc = r is null ? WuText.OrcFailed : Try(() => (int)((dynamic)r).ResultCode, WuText.OrcFailed);
                int hr = r is null ? 0 : Try(() => (int)((dynamic)r).HResult);
                dynamic u = coll[index];
                if (WuText.IsSuccess(rc) && Try(() => (bool)u.IsDownloaded)) { installColl.Add(u); toInstall.Add(order[i]); }
                else failed.Add((order[i], index, rc, hr));
            }

            // 2b. One automatic retry of the downloads that failed for a transient reason (a dropped CDN file, a network blip):
            // without it the user had to press the button again (10.11.2026). Only the failed items are downloaded again.
            var retry = dlCancelled || dlTimedOut ? [] : failed.Where(f => WuText.IsRetryableDownload(f.Rc, f.Hr)).ToList();
            if (retry.Count > 0)
            {
                Log.Info("Windows Update: повтор загрузки ({0}) после ошибки {1}", retry.Count, string.Join(", ", retry.Select(f => $"0x{unchecked((uint)f.Hr):X8}").Distinct()));
                token.WaitHandle.WaitOne(WuText.RetryDelayFor(retry.Select(f => f.Hr)));
                token.ThrowIfCancellationRequested();
                dynamic retryColl = New("Microsoft.Update.UpdateColl");
                foreach (var f in retry) retryColl.Add(coll[f.Index]);
                downloader.Updates = retryColl;
                progress.Report(new WuProgress(WuStage.Downloading, 0, 1, retry.Count));
                var (rres, rCancelled, rTimedOut) = RunJob(
                    cb => downloader.BeginDownload(new UnknownWrapper(cb), new UnknownWrapper(cb), null),
                    job => downloader.EndDownload(job),
                    () => downloader.Download(),
                    DownloadTimeout, token,
                    args => ReportProgress(progress, WuStage.Downloading, args, retry.Count));
                dlCancelled |= rCancelled; dlTimedOut |= rTimedOut;
                for (var k = 0; k < retry.Count; k++)
                {
                    var index = k;
                    object? r = Try<object?>(() => rres.GetUpdateResult(index));
                    int rc = r is null ? WuText.OrcFailed : Try(() => (int)((dynamic)r).ResultCode, WuText.OrcFailed);
                    int hr = r is null ? 0 : Try(() => (int)((dynamic)r).HResult);
                    dynamic u = coll[retry[k].Index];
                    var at = failed.FindIndex(f => f.Id == retry[index].Id);
                    if (WuText.IsSuccess(rc) && Try(() => (bool)u.IsDownloaded)) { installColl.Add(u); toInstall.Add(retry[k].Id); failed.RemoveAt(at); }
                    else failed[at] = (retry[k].Id, retry[k].Index, rc, hr);
                }
            }
            foreach (var (id, _, rc, hr) in failed)
                results[id] = new WuItemResult(id, titles[id], rc == WuText.OrcSucceeded ? WuText.OrcFailed : rc, hr, false,
                    dlCancelled ? Loc.T("Загрузка отменена.") : dlTimedOut ? Loc.T("Загрузка не завершилась за отведённое время.")
                        : Loc.Tf("Не удалось загрузить: {0}", hr != 0 ? WuText.DescribeHResult(hr) : WuText.ResultText(rc).ToLowerInvariant()));
            if (dlCancelled || toInstall.Count == 0)
                return new WuInstallResult(Ordered(results, wanted), false, dlCancelled);

            // 3. Install.
            token.ThrowIfCancellationRequested();
            dynamic installer = session.CreateUpdateInstaller();
            TrySet(() => installer.ClientApplicationID = ClientId);
            if ((bool)installer.IsBusy)
                throw new WuException(WuText.OperationInProgress, WuText.DescribeHResult(WuText.OperationInProgress));
            if ((bool)installer.RebootRequiredBeforeInstallation)
                throw new InvalidOperationException(Loc.T("Windows ожидает перезагрузки перед установкой. Перезагрузите компьютер и повторите."));
            installer.AllowSourcePrompts = false;
            installer.Updates = installColl;
            progress.Report(new WuProgress(WuStage.Installing, 0, 1, toInstall.Count));
            var (ires, inCancelled, inTimedOut) = RunJob(
                cb => installer.BeginInstall(new UnknownWrapper(cb), new UnknownWrapper(cb), null),
                job => installer.EndInstall(job),
                () => installer.Install(),
                InstallTimeout, token,
                args => ReportProgress(progress, WuStage.Installing, args, toInstall.Count));

            for (var i = 0; i < toInstall.Count; i++)
            {
                var id = toInstall[i];
                var index = i;
                object? r = Try<object?>(() => ires.GetUpdateResult(index));
                int rc = r is null ? WuText.OrcFailed : Try(() => (int)((dynamic)r).ResultCode, WuText.OrcFailed);
                int hr = r is null ? 0 : Try(() => (int)((dynamic)r).HResult);
                bool reboot = r is not null && Try(() => (bool)((dynamic)r).RebootRequired);
                string message = WuText.IsSuccess(rc)
                    ? reboot ? Loc.T("Установлено. Требуется перезагрузка.") : Loc.T("Установлено.")
                    : inCancelled ? Loc.T("Установка отменена.")
                    : inTimedOut ? Loc.T("Установка не завершилась за отведённое время.")
                    : Loc.Tf("Не установлено: {0}", hr != 0 ? WuText.DescribeHResult(hr) : WuText.ResultText(rc).ToLowerInvariant());
                results[id] = new WuItemResult(id, titles[id], rc, hr, reboot, message);
            }
            bool rebootAll = Try(() => (bool)ires.RebootRequired) || results.Values.Any(r => r.RebootRequired);
            return new WuInstallResult(Ordered(results, wanted), rebootAll, inCancelled);
        });

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

        var (result, cancelled, timedOut) = RunJob(
            cb => searcher.BeginSearch(WuText.DriverCriteria, new UnknownWrapper(cb), null),
            job => searcher.EndSearch(job),
            () => searcher.Search(WuText.DriverCriteria),
            SearchTimeout, ct, null);

        if (cancelled) throw new OperationCanceledException(ct);
        if (timedOut) throw new TimeoutException(Loc.T("Windows Update не ответил за 5 минут."));
        int rc = result.ResultCode;
        if (!WuText.IsSuccess(rc))
            throw new WuException(0, Loc.Tf("Windows Update не смог выполнить поиск ({0}).", WuText.ResultText(rc).ToLowerInvariant()));
        return result.Updates;
    }

    /// <summary>
    /// Runs a WUA job through Begin*/End* with a callback (cancellable via RequestAbort, with a timeout).
    /// If the callback cannot be marshaled on this system, falls back to the synchronous call.
    /// </summary>
    private static (dynamic Result, bool Cancelled, bool TimedOut) RunJob(
        Func<WuJobCallback, dynamic> begin, Func<dynamic, dynamic> end, Func<dynamic> sync,
        TimeSpan timeout, CancellationToken ct, Action<object>? onProgress)
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
            Log.Info("Windows Update: асинхронный вызов недоступен ({0}) — используется синхронный.", ex.GetType().Name);
            return (sync(), false, false);
        }

        bool cancelled = false, timedOut = false;
        var signaled = WaitHandle.WaitAny([cb.Completed.WaitHandle, ct.WaitHandle], timeout);
        if (signaled != 0)
        {
            cancelled = signaled == 1;
            timedOut = signaled == WaitHandle.WaitTimeout;
            try { job.RequestAbort(); } catch { }
            if (!cb.Completed.Wait(AbortGrace))
            {
                if (cancelled) throw new OperationCanceledException(ct);
                throw new TimeoutException(Loc.T("Windows Update не ответил вовремя."));
            }
        }

        try
        {
            return (end(job), cancelled, timedOut);
        }
        catch (COMException ex) when (cancelled || timedOut || ex.HResult == WuText.OperationCancelled)
        {
            if (timedOut) throw new TimeoutException(Loc.T("Windows Update не ответил вовремя."));
            throw new OperationCanceledException(ct);
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
            Log.Warn("Windows Update: не удалось прочитать обновление: {0}", ex.Message);
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

    /// <summary>Runs <paramref name="body"/> on a new background MTA thread with an overall timeout.</summary>
    private static Task<T> RunOnMtaThread<T>(string name, TimeSpan timeout, CancellationToken ct, Func<CancellationToken, T> body) =>
        RunOnMtaThread(name, timeout, ct, observeCancellation: true, body);

    /// <summary>
    /// Runs <paramref name="body"/> on a new background MTA thread with an overall timeout.
    /// With <paramref name="observeCancellation"/> = false the returned task ignores <paramref name="ct"/> (only the
    /// body sees it) and completes when the body really ends — or on the timeout.
    /// </summary>
    private static Task<T> RunOnMtaThread<T>(string name, TimeSpan timeout, CancellationToken ct, bool observeCancellation,
        Func<CancellationToken, T> body)
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
