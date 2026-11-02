namespace Monolith.Core;

/// <summary>
/// Global write lock used by the self-test mode: every API that changes the system checks it,
/// so an automated run can never modify the registry, services, power plans, tasks or install software.
/// </summary>
public static class SafetyLock
{
    public static bool IsLocked => ChangeSession.Locked;

    public static void ThrowIfLocked(string action)
    {
        if (ChangeSession.Locked)
            throw new InvalidOperationException(Loc.Tf("«{0}» заблокировано: режим самопроверки.", Loc.Sink(action)));
    }
}
