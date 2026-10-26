namespace Monolith.Tweaks;

/// <summary>
/// The full catalog, assembled from the category partial files:
///   TweakCatalog.Gaming.cs    — Game Mode, HAGS, windowed games / VRR, Game DVR, MPO
///   TweakCatalog.Power.cs     — power plans (X3D-aware), power settings, Fast Startup, hibernation
///   TweakCatalog.Interface.cs — tips and ads, Start and search, Explorer, visual effects, mouse
///   TweakCatalog.Privacy.cs   — diagnostic data, advertising ID, activity history, Recall, DiagTrack
///   TweakCatalog.Network.cs   — Delivery Optimization, Wi-Fi power, DNS, network throttling, TCP ACK
///   TweakCatalog.System.cs    — Storage Sense, Windows Update scheduling, background apps, file system, HVCI
/// Sources and classifications: docs/research/gaming_tweaks.md and docs/research/general_tweaks.md.
/// DO-NOT-INCLUDE items from the research are deliberately absent.
/// </summary>
public static partial class TweakCatalog
{
    private static partial IEnumerable<Tweak> Build() =>
        GamingTweaks()
            .Concat(PowerTweaks())
            .Concat(InterfaceTweaks())
            .Concat(PrivacyTweaks())
            .Concat(NetworkTweaks())
            .Concat(SystemTweaks());
}
