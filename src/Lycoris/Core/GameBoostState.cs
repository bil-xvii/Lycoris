namespace Lycoris.Core;

/// <summary>
/// Shared flag: is a Game Boost session running right now? Set by the Games module; read by modules
/// that must not run heavy work during a game (Cleaner) and by the startup checks.
/// </summary>
public static class GameBoostState
{
    private static volatile bool _active;

    public static bool IsActive => _active;

    public static event Action<bool>? Changed;

    public static void Set(bool active)
    {
        if (_active == active) return;
        _active = active;
        try { Changed?.Invoke(active); } catch { }
    }
}
