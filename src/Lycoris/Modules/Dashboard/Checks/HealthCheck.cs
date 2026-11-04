namespace Lycoris.Modules.Dashboard.Checks;

public enum CheckSeverity
{
    /// <summary>Everything is as it should be.</summary>
    Ok,
    /// <summary>Worth knowing, not a problem (trade-offs, recommendations).</summary>
    Info,
    /// <summary>A real problem that costs performance, safety or stability.</summary>
    Warning,
}

public enum CheckActionKind
{
    /// <summary>Open another Lycoris page (<see cref="CheckAction.Target"/> = page key).</summary>
    Navigate,
    /// <summary>Open a Windows settings page / URI (<see cref="CheckAction.Target"/> = "ms-settings:…").</summary>
    OpenUri,
    /// <summary>Open «Оптимизация» scrolled to one catalog item (<see cref="CheckAction.Target"/> = tweak id). Changes nothing.</summary>
    FocusTweak,
    /// <summary>Apply a catalog tweak by id through the journal (admin). Falls back to the Optimize page.</summary>
    ApplyTweak,
    /// <summary>
    /// Open «Устройства и сеть» focused on <see cref="CheckAction.Target"/> (a card key or "kind:&lt;kind&gt;", e.g. "kind:monitor").
    /// Changes nothing: the change itself is made there, through the journal and with an on-screen confirmation.
    /// </summary>
    FocusDevice,
    /// <summary>
    /// Restart Windows to finish pending updates: only after an on-screen confirmation, with a 60-second delay that the
    /// user can cancel. Never part of «ИСПРАВИТЬ ВСЁ БЕЗОПАСНОЕ»; not a system change, so nothing is journaled.
    /// </summary>
    Restart,
    /// <summary>Opens Windows Task Manager (System32, full path); reading only — it never ends a process by itself.</summary>
    OpenTaskManager,
}

/// <summary>What the button next to a check does. Nothing here touches the system by itself.</summary>
/// <param name="Focus">For Navigate: the focus id handed to the page ("tab:problems") so it opens on the exact place.</param>
public sealed record CheckAction(CheckActionKind Kind, string Label, string? Target = null, string? Focus = null)
{
    /// <summary>Actions that change the system need administrator rights (they go through ChangeSession).</summary>
    public bool RequiresAdmin => Kind is CheckActionKind.ApplyTweak;
}

/// <summary>
/// Result of one health check. <see cref="Weight"/> is how many points a warning costs in the health score
/// (information costs a fifth of it, OK costs nothing). <see cref="SecondaryAction"/> is an optional second button
/// (e.g. the one large, safe lever behind a low-space warning next to «К ОЧИСТКЕ»); it is as harmless as the first one.
/// </summary>
public sealed record HealthCheckResult(string Id, string Title, string Detail, CheckSeverity Severity, int Weight, CheckAction? Action = null,
    CheckAction? SecondaryAction = null);
