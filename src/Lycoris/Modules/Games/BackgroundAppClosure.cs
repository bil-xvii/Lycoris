using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Lycoris.Core;

namespace Lycoris.Modules.Games;

public enum BackgroundCloseStatus
{
    Closed,
    Forced,
    Stale,
    Denied,
    StillRunning,
    Error,
}

public sealed class BackgroundCloseTarget
{
    internal BackgroundCloseTarget(int pid, string name, DateTime startedUtc, string imagePath, long memoryBytes, string ownerSid, int sessionId)
    {
        Pid = pid;
        Name = name;
        StartedUtc = startedUtc;
        ImagePath = imagePath;
        MemoryBytes = memoryBytes;
        OwnerSid = ownerSid;
        SessionId = sessionId;
    }

    public int Pid { get; }
    public string Name { get; }
    public DateTime StartedUtc { get; }
    public string ImagePath { get; }
    public long MemoryBytes { get; }
    internal string OwnerSid { get; }
    internal int SessionId { get; }
}

public sealed record BackgroundCloseUnavailable(string Name, int? Pid, BackgroundCloseStatus Status);
public sealed record BackgroundCloseResult(BackgroundCloseTarget Target, BackgroundCloseStatus Status);
public sealed record BackgroundCloseOutcome(IReadOnlyList<BackgroundCloseResult> Results, bool SelectionVerified, bool PreflightAborted);

/// <summary>An immutable snapshot. The fingerprint binds a subsequent CLI request to the same selection.</summary>
public sealed class BackgroundClosePlan
{
    internal const int PolicyVersion = 1;

    internal BackgroundClosePlan(BackgroundCloseContext? context, string[] names, string excludedDirectory,
        BackgroundCloseTarget[] targets, BackgroundCloseUnavailable[] unavailable, bool hasFullSelection, bool force)
    {
        Context = context;
        Names = Array.AsReadOnly(names);
        ExcludedDirectory = excludedDirectory;
        Targets = Array.AsReadOnly(targets);
        Unavailable = Array.AsReadOnly(unavailable);
        HasFullSelection = hasFullSelection;
        Force = force;

        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        writer.Write(PolicyVersion);
        writer.Write(force);
        writer.Write(context?.OwnerSid ?? "");
        writer.Write(context?.SessionId ?? 0);
        writer.Write(context?.WindowsDirectory.ToUpperInvariant() ?? "");
        writer.Write(excludedDirectory.ToUpperInvariant());
        writer.Write(hasFullSelection);
        writer.Write(names.Length);
        foreach (var name in names) writer.Write(name.ToUpperInvariant());
        writer.Write(targets.Length);
        foreach (var target in targets)
        {
            writer.Write(target.Pid);
            writer.Write(target.Name.ToUpperInvariant());
            writer.Write(target.StartedUtc.Ticks);
            writer.Write(target.ImagePath);
            writer.Write(target.OwnerSid);
            writer.Write(target.SessionId);
        }
        writer.Write(unavailable.Length);
        foreach (var item in unavailable)
        {
            writer.Write(item.Name.ToUpperInvariant());
            writer.Write(item.Pid ?? 0);
            writer.Write((int)item.Status);
        }
        writer.Flush();
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(data.GetBuffer().AsSpan(0, (int)data.Length)));
    }

    public IReadOnlyList<BackgroundCloseTarget> Targets { get; }
    public IReadOnlyList<BackgroundCloseUnavailable> Unavailable { get; }
    public bool HasFullSelection { get; }
    public string Fingerprint { get; }
    internal BackgroundCloseContext? Context { get; }
    internal IReadOnlyList<string> Names { get; }
    internal string ExcludedDirectory { get; }
    internal bool Force { get; }
}

/// <summary>Exact process shutdown used by the Games page, Game Boost and the standalone CLI.</summary>
public static class BackgroundAppClosure
{
    internal static string OperationName => Loc.T("Закрытие фоновых приложений");
    internal static readonly BackgroundCloseEngine Engine = new(new BackgroundCloseWindows());

    public static BackgroundClosePlan Capture(IEnumerable<string> names, string? excludeDir = null) =>
        Engine.Capture(names, excludeDir);

    public static IReadOnlyList<RunningApp> ListClosable() => Engine.ListClosable();

    public static Task<BackgroundCloseOutcome> CloseAsync(BackgroundClosePlan plan,
        Func<IReadOnlyList<BackgroundCloseTarget>, Task<bool>>? confirmForce = null, CancellationToken ct = default) =>
        OperationCoordinator.Instance.RunAsync(CreateRequest(), lease => CloseWithinAsync(Engine, lease, plan, confirmForce, ct), ct);

    internal static OperationRequest CreateRequest() => new(OperationClass.Disruptive, OperationName) { Owner = "games" };

    internal static Task<BackgroundCloseOutcome> CloseWithinAsync(BackgroundCloseEngine engine,
        OperationLease lease, BackgroundClosePlan plan, Func<IReadOnlyList<BackgroundCloseTarget>, Task<bool>>? confirmForce,
        CancellationToken ct = default) =>
        Task.Run(() => engine.CloseAsync(plan,
            confirmForce is not null ? targets => lease.WhileUserDecidesAsync(() => confirmForce(targets), ct) : null, ct), ct);
}

internal sealed record BackgroundCloseContext(string OwnerSid, int SessionId, int SelfPid, string WindowsDirectory);
internal sealed record BackgroundCloseCandidate(int Pid, string Name, long MemoryBytes);
internal sealed record BackgroundCloseIdentity(int Pid, DateTime StartedUtc, string ImagePath, string OwnerSid, int SessionId, bool IsCritical);

internal enum BackgroundProcessState { Running, Exited, Unknown }
internal enum BackgroundCloseRequest { Sent, NoWindow, Denied, Error }

internal interface IBackgroundCloseHandle : IDisposable
{
    BackgroundCloseIdentity? ReadIdentity();
    BackgroundProcessState GetState();
    BackgroundCloseRequest RequestClose();
    BackgroundCloseRequest Terminate();
}

internal interface IBackgroundClosePlatform
{
    bool CanClose();
    BackgroundCloseContext? GetContext();
    BackgroundCloseGameProtection? GetGameProtection();
    IReadOnlyList<BackgroundCloseCandidate>? Enumerate();
    IBackgroundCloseHandle? Open(int pid, bool terminate, out BackgroundCloseStatus failure);
}

internal sealed class BackgroundCloseEngine(IBackgroundClosePlatform platform, TimeSpan? gracefulTimeout = null, TimeSpan? forcedTimeout = null)
{
    private readonly TimeSpan _gracefulTimeout = gracefulTimeout ?? TimeSpan.FromSeconds(8);
    private readonly TimeSpan _forcedTimeout = forcedTimeout ?? TimeSpan.FromSeconds(3);

    public BackgroundClosePlan Capture(IEnumerable<string>? names, string? excludeDir = null, bool force = false)
    {
        var orderedNames = names?.Select(UserProcesses.NormalizeName).Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var wanted = names is null ? null : new HashSet<string>(orderedNames, StringComparer.OrdinalIgnoreCase);
        var gameDir = GamePaths.NormalizeDir(excludeDir);
        var context = platform.GetContext();
        var games = context is not null ? platform.GetGameProtection() : null;
        var candidates = games is not null ? platform.Enumerate() : null;
        if (context is null || games is null || candidates is null)
            return new(context, orderedNames, gameDir, [], [], false, force);

        var unavailable = new List<BackgroundCloseUnavailable>();
        if (wanted is not null)
        {
            foreach (var name in orderedNames.Where(UserProcesses.IsProtectedCloseName))
            {
                wanted.Remove(name);
                unavailable.Add(new(name, null, BackgroundCloseStatus.Denied));
            }
        }

        var targets = new List<BackgroundCloseTarget>();
        foreach (var candidate in candidates.OrderBy(p => p.Pid))
        {
            if ((wanted is not null && !wanted.Contains(candidate.Name)) || UserProcesses.IsProtectedCloseName(candidate.Name))
                continue;
            var failure = BackgroundCloseStatus.Denied;
            if (candidate.Pid > 4 && candidate.Pid != context.SelfPid)
            {
                using var handle = platform.Open(candidate.Pid, terminate: false, out failure);
                if (handle is not null)
                {
                    if (ReadTarget(handle, context, games, gameDir, candidate.Pid, candidate.MemoryBytes, out failure) is { } target &&
                        string.Equals(target.Name, candidate.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(target);
                        continue;
                    }
                    if (failure == BackgroundCloseStatus.StillRunning) failure = BackgroundCloseStatus.Stale;
                }
            }
            if (wanted is not null) unavailable.Add(new(candidate.Name, candidate.Pid, failure));
        }
        return new(context, orderedNames, gameDir, [.. targets], [.. unavailable], true, force);
    }

    public IReadOnlyList<RunningApp> ListClosable() =>
        Capture(names: null).Targets.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RunningApp(g.Key, "", g.Count(), g.Sum(p => p.MemoryBytes), HasWindow: false))
            .OrderByDescending(a => a.MemoryBytes).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    private static BackgroundCloseTarget? ReadTarget(IBackgroundCloseHandle handle, BackgroundCloseContext context,
        BackgroundCloseGameProtection games, string gameDir, int pid, long memoryBytes, out BackgroundCloseStatus failure)
    {
        var state = handle.GetState();
        failure = state == BackgroundProcessState.Exited ? BackgroundCloseStatus.Stale : BackgroundCloseStatus.Error;
        if (state != BackgroundProcessState.Running) return null;

        var identity = handle.ReadIdentity();
        if (identity is null) return null;
        if (identity.Pid != pid)
        {
            failure = BackgroundCloseStatus.Stale;
            return null;
        }
        failure = BackgroundCloseStatus.Denied;
        if (identity.SessionId != context.SessionId || identity.OwnerSid != context.OwnerSid || identity.IsCritical ||
            !UserProcesses.TryGetClosableImage(identity.ImagePath, context.WindowsDirectory, gameDir, out var name, out var path) || games.Protects(path))
            return null;

        state = handle.GetState();
        failure = state switch
        {
            BackgroundProcessState.Running => BackgroundCloseStatus.StillRunning,
            BackgroundProcessState.Exited => BackgroundCloseStatus.Stale,
            _ => BackgroundCloseStatus.Error,
        };
        if (state != BackgroundProcessState.Running) return null;
        return new(pid, name, identity.StartedUtc, path, memoryBytes, identity.OwnerSid, identity.SessionId);
    }

    private static bool Matches(BackgroundCloseTarget expected, BackgroundCloseTarget actual) =>
        expected.Pid == actual.Pid && expected.StartedUtc == actual.StartedUtc &&
        expected.SessionId == actual.SessionId && expected.OwnerSid == actual.OwnerSid &&
        string.Equals(expected.Name, actual.Name, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(expected.ImagePath, actual.ImagePath, StringComparison.Ordinal);

    private IBackgroundCloseHandle? OpenVerified(BackgroundCloseTarget target, BackgroundCloseContext context,
        BackgroundCloseGameProtection games, string gameDir, bool terminate, out BackgroundCloseStatus failure)
    {
        var handle = platform.Open(target.Pid, terminate, out failure);
        if (handle is null) return null;

        try
        {
            failure = Verify(handle, target, context, games, gameDir);
            if (failure == BackgroundCloseStatus.StillRunning)
            {
                var verified = handle;
                handle = null;
                return verified;
            }
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static BackgroundCloseStatus Verify(IBackgroundCloseHandle handle, BackgroundCloseTarget target,
        BackgroundCloseContext context, BackgroundCloseGameProtection games, string gameDir)
    {
        if (ReadTarget(handle, context, games, gameDir, target.Pid, target.MemoryBytes, out var failure) is { } actual &&
            !Matches(target, actual)) return BackgroundCloseStatus.Stale;
        return failure;
    }

    private sealed class Attempt(BackgroundCloseTarget target)
    {
        public BackgroundCloseTarget Target { get; } = target;
        public BackgroundCloseStatus Status { get; set; }
        public IBackgroundCloseHandle? Query { get; set; }
        public IBackgroundCloseHandle? Termination { get; set; }
        public bool EligibleForForce { get; set; }
        public bool CloseSent { get; set; }
        public bool TerminateSent { get; set; }
    }

    public async Task<BackgroundCloseOutcome> CloseAsync(BackgroundClosePlan plan,
        Func<IReadOnlyList<BackgroundCloseTarget>, Task<bool>>? confirmForce, CancellationToken ct = default)
    {
        var context = platform.GetContext();
        var games = context is not null ? platform.GetGameProtection() : null;
        if (!platform.CanClose() || !plan.HasFullSelection || context is null || plan.Context != context || games is null)
            return new(plan.Targets.Select(t => new BackgroundCloseResult(t, BackgroundCloseStatus.Denied)).ToArray(),
                SelectionVerified: false, PreflightAborted: true);

        var attempts = plan.Targets.Select(t => new Attempt(t)).ToArray();
        try
        {
            foreach (var attempt in attempts)
            {
                ct.ThrowIfCancellationRequested();
                attempt.Query = OpenVerified(attempt.Target, context, games, plan.ExcludedDirectory, terminate: false, out var status);
                attempt.Status = status;
            }

            if (attempts.Any(a => a.Query is null))
                return new(ReadResults(attempts), SelectionVerified: false, PreflightAborted: true);

            // With the approved processes pinned, detect changes to the complete selection before the first close.
            var latest = Capture(plan.Names, plan.ExcludedDirectory, plan.Force);
            if (!latest.HasFullSelection || !string.Equals(plan.Fingerprint, latest.Fingerprint, StringComparison.Ordinal))
            {
                foreach (var attempt in attempts)
                {
                    attempt.Status = Verify(attempt.Query!, attempt.Target, context, games, plan.ExcludedDirectory);
                }
                var changed = ReadResults(attempts);
                AddNewTargets(changed, plan, latest);
                return new(changed, SelectionVerified: false, PreflightAborted: true);
            }

            foreach (var attempt in attempts)
            {
                ct.ThrowIfCancellationRequested();
                if (!platform.CanClose())
                {
                    attempt.Status = BackgroundCloseStatus.Denied;
                    continue;
                }
                attempt.Status = Verify(attempt.Query!, attempt.Target, context, games, plan.ExcludedDirectory);
                if (attempt.Status != BackgroundCloseStatus.StillRunning) continue;
                attempt.EligibleForForce = true;
                var request = attempt.Query!.RequestClose();
                attempt.CloseSent = request == BackgroundCloseRequest.Sent;
                attempt.Status = RequestStatus(request);
            }
            await WaitForExitAsync(attempts.Where(a => a.CloseSent || (confirmForce is not null && a.EligibleForForce)),
                _gracefulTimeout, ct).ConfigureAwait(false);

            var remaining = attempts.Where(a => a.EligibleForForce && !ObserveExit(a)).ToArray();
            if (remaining.Length > 0 && confirmForce is not null && platform.CanClose() &&
                await confirmForce(Array.AsReadOnly(remaining.Select(a => a.Target).ToArray())).ConfigureAwait(false))
            {
                await ForceAsync(remaining, context, plan.ExcludedDirectory, ct).ConfigureAwait(false);
            }
            foreach (var attempt in attempts) ObserveExit(attempt);
            var results = ReadResults(attempts);

            var finalSelection = Capture(plan.Names, plan.ExcludedDirectory, plan.Force);
            AddNewTargets(results, plan, finalSelection);
            var complete = finalSelection.HasFullSelection &&
                finalSelection.Unavailable.All(item => plan.Unavailable.Contains(item));
            return new(results, SelectionVerified: complete, PreflightAborted: false);
        }
        finally
        {
            foreach (var attempt in attempts)
            {
                attempt.Termination?.Dispose();
                attempt.Query?.Dispose();
            }
        }
    }

    private static List<BackgroundCloseResult> ReadResults(IEnumerable<Attempt> attempts) =>
        attempts.Select(a => new BackgroundCloseResult(a.Target, a.Status)).ToList();

    private static void AddNewTargets(List<BackgroundCloseResult> results, BackgroundClosePlan approved, BackgroundClosePlan observed)
    {
        // A later matching PID was outside the confirmed snapshot. Report it without acting on it.
        var approvedPids = approved.Targets.Select(t => t.Pid).ToHashSet();
        results.AddRange(observed.Targets.Where(t => !approvedPids.Contains(t.Pid))
            .Select(t => new BackgroundCloseResult(t, BackgroundCloseStatus.Stale)));
    }

    private async Task ForceAsync(Attempt[] remaining, BackgroundCloseContext context, string gameDir, CancellationToken ct)
    {
        var games = platform.GetGameProtection();
        if (games is null)
        {
            foreach (var attempt in remaining) attempt.Status = BackgroundCloseStatus.Denied;
            return;
        }

        foreach (var attempt in remaining)
        {
            ct.ThrowIfCancellationRequested();
            if (ObserveExit(attempt)) continue;
            attempt.Termination = OpenVerified(attempt.Target, context, games, gameDir, terminate: true, out var status);
            attempt.Status = status;
        }

        foreach (var attempt in remaining)
        {
            ct.ThrowIfCancellationRequested();
            if (attempt.Termination is null || ObserveExit(attempt)) continue;
            if (!platform.CanClose())
            {
                attempt.Status = BackgroundCloseStatus.Denied;
                continue;
            }
            attempt.Status = Verify(attempt.Termination, attempt.Target, context, games, gameDir);
            if (attempt.Status != BackgroundCloseStatus.StillRunning) continue;
            var request = attempt.Termination.Terminate();
            attempt.TerminateSent = request == BackgroundCloseRequest.Sent;
            attempt.Status = RequestStatus(request);
        }
        await WaitForExitAsync(remaining.Where(a => a.TerminateSent), _forcedTimeout, ct).ConfigureAwait(false);
    }

    private static bool ObserveExit(Attempt attempt)
    {
        switch (attempt.Query!.GetState())
        {
            case BackgroundProcessState.Exited:
                attempt.Status = attempt.TerminateSent ? BackgroundCloseStatus.Forced
                    : attempt.CloseSent ? BackgroundCloseStatus.Closed : BackgroundCloseStatus.Stale;
                return true;
            case BackgroundProcessState.Unknown:
                attempt.EligibleForForce = false;
                attempt.Status = BackgroundCloseStatus.Error;
                return true;
            default:
                return false;
        }
    }

    private static BackgroundCloseStatus RequestStatus(BackgroundCloseRequest request) => request switch
    {
        BackgroundCloseRequest.Denied => BackgroundCloseStatus.Denied,
        BackgroundCloseRequest.Error => BackgroundCloseStatus.Error,
        _ => BackgroundCloseStatus.StillRunning,
    };

    private static async Task WaitForExitAsync(IEnumerable<Attempt> attempts, TimeSpan timeout, CancellationToken ct)
    {
        var pending = attempts.ToList();
        var start = Stopwatch.GetTimestamp();
        while (pending.Count > 0)
        {
            pending.RemoveAll(ObserveExit);
            if (pending.Count == 0 || Stopwatch.GetElapsedTime(start) >= timeout) break;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}
