using System.Collections.Concurrent;
using Monolith.Core;

namespace Monolith.Modules.Dashboard.Checks;

/// <summary>
/// Runs one read-only probe on a worker thread with a time limit. A probe that does not answer in time reads as
/// "unknown" (the fallback) and is left to finish on its own; it is not started again while it is still running, so a
/// hung WMI provider or event log query can never pile up threads. Optional caching keeps slow sources (drive SMART
/// data can spin up sleeping HDDs) from being read on every visit of the dashboard.
/// </summary>
internal static class Probe
{
    private static readonly ConcurrentDictionary<string, Task> InFlight = new();
    private static readonly ConcurrentDictionary<string, (DateTime At, object? Value)> Cache = new();

    public static Task<T> RunAsync<T>(string name, Func<T> read, TimeSpan limit, T fallback, TimeSpan? cacheFor = null) =>
        RunAsync(name, () => Task.Run(read), limit, fallback, cacheFor);

    public static async Task<T> RunAsync<T>(string name, Func<Task<T>> read, TimeSpan limit, T fallback, TimeSpan? cacheFor = null)
    {
        if (cacheFor is { } age && Cache.TryGetValue(name, out var hit) && DateTime.UtcNow - hit.At < age && hit.Value is T cached)
            return cached;
        if (InFlight.TryGetValue(name, out var previous) && !previous.IsCompleted)
            return fallback; // still stuck from an earlier check: don't start a second one

        var work = Task.Run(async () =>
        {
            try { return await read().ConfigureAwait(false); }
            catch (Exception ex)
            {
                Log.Warn($"Проверка системы ({name}): {ex.Message}");
                return fallback;
            }
        });
        InFlight[name] = work;
        if (await Task.WhenAny(work, Task.Delay(limit)).ConfigureAwait(false) != work)
        {
            Log.Warn($"Проверка системы: «{name}» не ответила за {limit.TotalSeconds:0} с — пропущено.");
            return fallback;
        }
        var value = await work.ConfigureAwait(false);
        if (cacheFor is not null && value is not null && !Equals(value, fallback)) Cache[name] = (DateTime.UtcNow, value);
        return value;
    }
}
