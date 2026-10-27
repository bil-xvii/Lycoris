namespace Monolith.Tweaks;

public enum ProfileKind { Normal, Gaming, Recommended }

/// <summary>
/// Every optimization MONOLITH knows. The list itself is built in TweakCatalog.*.cs partial files.
///
/// Canonical ids (other modules reference these — keep them stable):
///   game-mode, hags, gamedvr-off, power-plan-performance, mouse-acceleration-off,
///   windowed-games-optimization, visual-effects-performance, ads-suggestions-off,
///   telemetry-required, bing-start-search-off, storage-sense-on, delivery-optimization-local, trim-on
/// </summary>
public static partial class TweakCatalog
{
    private static readonly Lazy<IReadOnlyList<Tweak>> AllLazy = new(() => Build().ToList());

    public static IReadOnlyList<Tweak> All => AllLazy.Value;

    public static Tweak? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    public static ProfileSet ToSet(ProfileKind kind) => kind switch
    {
        ProfileKind.Normal => ProfileSet.Normal,
        ProfileKind.Gaming => ProfileSet.Gaming,
        _ => ProfileSet.Recommended,
    };

    /// <summary>Tweaks pre-checked for a profile. Advanced tweaks are never part of a profile.</summary>
    public static IEnumerable<Tweak> DefaultsFor(ProfileKind kind) =>
        All.Where(t => t.Risk != TweakRisk.Advanced && t.DefaultIn.HasFlag(ToSet(kind)));

    /// <summary>Implemented in the catalog partial files: yields every tweak.</summary>
    private static partial IEnumerable<Tweak> Build();
}
