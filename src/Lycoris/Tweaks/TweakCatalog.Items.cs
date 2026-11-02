namespace Lycoris.Tweaks;

/// <summary>
/// The full catalog, assembled from the category partial files:
///   TweakCatalog.Gaming.cs    — Game Mode, HAGS, windowed games / VRR, Game DVR, MPO
///   TweakCatalog.Power.cs     — power plans (X3D-aware), Windows 11 power mode, power settings, Fast Startup, hibernation
///   TweakCatalog.Interface.cs — tips and ads, Start and search, Widgets / News and interests, Explorer, visual effects,
///                               mouse, accessibility shortcuts
///   TweakCatalog.Privacy.cs   — diagnostic data, advertising ID, activity history, Recall, Paint/Notepad AI, DiagTrack
///   TweakCatalog.Network.cs   — Delivery Optimization, Wi-Fi power, TCP auto-tuning repair, DNS, network throttling, TCP ACK
///   TweakCatalog.System.cs    — Storage Sense, Edge background, Windows Update scheduling, background apps, TRIM and
///                               scheduled drive optimization repairs, file system, HVCI
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
