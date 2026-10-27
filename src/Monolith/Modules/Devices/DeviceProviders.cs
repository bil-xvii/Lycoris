using Monolith.Core;
using Monolith.Tweaks;

namespace Monolith.Modules.Devices;

/// <summary>Finds devices of one kind and builds their cards (read-only; off the UI thread).</summary>
public interface IDeviceProvider
{
    /// <summary>Russian, lower case, for «Не удалось прочитать: мониторы».</summary>
    string Name { get; }
    Task<IReadOnlyList<DeviceCard>> DetectAsync(DeviceScanContext ctx, CancellationToken ct);
}

/// <summary>Adds to the cards after every provider ran (vendor apps, product names, inventory cards).</summary>
public interface IDeviceEnricher
{
    string Name { get; }
    Task EnrichAsync(List<DeviceCard> cards, DeviceScanContext ctx, CancellationToken ct);
}

public sealed class DeviceScanContext
{
    public required SystemProfile Profile { get; init; }
    public bool IsElevated { get; init; }
    /// <summary>MONOLITH runs as the desktop user: true / another account: false / cannot tell: null (treated as «not»).</summary>
    public bool? InteractiveUser { get; init; }
    public bool IsInteractiveUser => InteractiveUser == true;
    public required DeviceResultStore Results { get; init; }
}

/// <summary>
/// The providers and enrichers of the running build. Feature packages add theirs in their own files through the partial
/// hooks (DeviceProviders.Input.cs, .Displays.cs, .Audio.cs, .Network.cs, .Vendors.cs); a missing file only means fewer cards.
/// </summary>
public static partial class DeviceProviders
{
    private static readonly Lazy<(IReadOnlyList<IDeviceProvider> Providers, IReadOnlyList<IDeviceEnricher> Enrichers)> Built = new(() =>
    {
        var providers = new List<IDeviceProvider>();
        var enrichers = new List<IDeviceEnricher>();
        AddInput(providers);
        AddDisplays(providers);
        AddAudio(providers);
        AddNetwork(providers);
        AddVendors(enrichers);
        return (providers, enrichers);
    });

    private static (IReadOnlyList<IDeviceProvider> Providers, IReadOnlyList<IDeviceEnricher> Enrichers)? _override;

    public static IReadOnlyList<IDeviceProvider> Providers => _override?.Providers ?? Built.Value.Providers;
    public static IReadOnlyList<IDeviceEnricher> Enrichers => _override?.Enrichers ?? Built.Value.Enrichers;

    static partial void AddInput(List<IDeviceProvider> p);
    static partial void AddDisplays(List<IDeviceProvider> p);
    static partial void AddAudio(List<IDeviceProvider> p);
    static partial void AddNetwork(List<IDeviceProvider> p);
    static partial void AddVendors(List<IDeviceEnricher> e);

    /// <summary>TESTS ONLY: replaces the providers and enrichers until disposed.</summary>
    internal static IDisposable OverrideForTests(IReadOnlyList<IDeviceProvider> providers, IReadOnlyList<IDeviceEnricher>? enrichers = null)
    {
        var previous = _override;
        _override = (providers, enrichers ?? []);
        return new Restorer(() => _override = previous);
    }

    private sealed class Restorer(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>Cards found, plus the providers that failed or timed out (shown honestly: «Не удалось прочитать: …»).</summary>
public sealed record DeviceScanResult(IReadOnlyList<DeviceCard> Cards, IReadOnlyList<string> Failed);

/// <summary>Runs every provider (in parallel, each with a timeout), then the enrichers, then reads the tweak rows.</summary>
public static class DeviceScan
{
    /// <summary>Per provider / enricher (tests lower it).</summary>
    internal static TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task<DeviceScanResult> RunAsync(DeviceScanContext ctx, CancellationToken ct = default)
    {
        var failed = new List<string>();
        var providers = DeviceProviders.Providers;
        var runs = providers.Select(p => RunOneAsync(p.Name, () => p.DetectAsync(ctx, ct), ct)).ToList();
        var results = await Task.WhenAll(runs).ConfigureAwait(false);

        var cards = new List<DeviceCard>();
        for (var i = 0; i < providers.Count; i++)
        {
            if (results[i] is { } found) cards.AddRange(found.Where(c => c is not null));
            else failed.Add(providers[i].Name);
        }

        foreach (var e in DeviceProviders.Enrichers)
        {
            var done = await RunOneAsync(e.Name, async () =>
            {
                await e.EnrichAsync(cards, ctx, ct).ConfigureAwait(false);
                return (IReadOnlyList<DeviceCard>)cards;
            }, ct).ConfigureAwait(false);
            if (done is null) failed.Add(e.Name);
        }

        // Duplicate keys (two providers claiming one device) keep the first card.
        var unique = cards.GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        await Task.Run(() => ReadTweakRows(unique, ctx.Profile), CancellationToken.None).ConfigureAwait(false);
        foreach (var card in unique) CheckHints(card);
        return new DeviceScanResult(
            unique.OrderBy(c => c.Order).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase).ToList(),
            failed.Distinct().ToList());
    }

    /// <summary>One provider on the thread pool with the timeout. Failures are expected (missing hardware, drivers): Warn, never Error.</summary>
    private static async Task<IReadOnlyList<DeviceCard>?> RunOneAsync(string name, Func<Task<IReadOnlyList<DeviceCard>>> body, CancellationToken ct)
    {
        try
        {
            var work = Task.Run(body, ct);
            var finished = await Task.WhenAny(work, Task.Delay(Timeout, ct)).ConfigureAwait(false);
            if (finished != work)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
                Log.Warn($"Устройства: «{name}» не ответили за {Timeout.TotalSeconds:0} с — пропущено.");
                return null;
            }
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Устройства: «{name}» — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rows backed by Optimize tweaks: the tweak's live state (Detect) becomes the row's value, and «applied» its
    /// recommendation — the plan applies the tweak through TweakEngine. Inapplicable or advanced tweaks never enter the plan.
    /// </summary>
    internal static void ReadTweakRows(IEnumerable<DeviceCard> cards, SystemProfile sp)
    {
        foreach (var setting in cards.SelectMany(c => c.Settings).Where(s => s.TweakId is not null))
        {
            var tweak = TweakCatalog.Find(setting.TweakId!);
            if (tweak is null)
            {
                setting.DisabledReason = "Этой настройки нет в «Оптимизации» этой версии MONOLITH.";
                continue;
            }
            switch (TweakEngine.Applicability(tweak, sp))
            {
                case false:
                    setting.DisabledReason = tweak.NotApplicableReason ?? "Не подходит для этого компьютера.";
                    setting.Current = null;
                    setting.Recommended = null;
                    continue;
                case null:
                    setting.DisabledReason = "Не удалось определить характеристики компьютера.";
                    continue;
            }
            TweakState state;
            try { state = tweak.Detect(); }
            catch { state = TweakState.Unknown; }
            setting.Current = state switch
            {
                TweakState.Applied => DeviceValue.Of(true),
                TweakState.NotApplied or TweakState.Partial => DeviceValue.Of(false),
                _ => null,
            };
            var policy = setting.TweakPolicy ?? tweak.Risk switch
            {
                TweakRisk.Safe => RecommendPolicy.Include,
                TweakRisk.Optional => RecommendPolicy.Suggest,
                _ => RecommendPolicy.None,
            };
            if (tweak.Risk == TweakRisk.Advanced) policy = RecommendPolicy.None; // never planned, like Dashboard shortcuts
            setting.Recommended = new Recommendation(DeviceValue.Of(true), setting.Hint.Length > 0 ? setting.Hint : tweak.Description, policy,
                new DeviceLink(DeviceLinkKind.FocusTweak, "В ОПТИМИЗАЦИИ", tweak.Id));
        }
    }

    /// <summary>Every row that can change something explains itself in one short line.</summary>
    internal static string? HintProblem(DeviceSetting s) =>
        s.Apply is null && s.TweakId is null ? null
        : string.IsNullOrWhiteSpace(s.Hint) ? $"у настройки «{s.Title}» нет пояснения"
        : s.Hint.Length > 120 ? $"пояснение настройки «{s.Title}» длиннее 120 символов"
        : null;

    private static void CheckHints(DeviceCard card)
    {
        foreach (var s in card.Settings)
            if (HintProblem(s) is { } problem) Log.Warn($"Устройства: {problem}.");
    }
}
