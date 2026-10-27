namespace Monolith.Modules.Dashboard.Checks;

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
    /// <summary>Open another MONOLITH page (<see cref="CheckAction.Target"/> = page key, <see cref="CheckAction.Section"/> = tab).</summary>
    Navigate,
    /// <summary>Open a Windows settings page / URI (<see cref="CheckAction.Target"/> = "ms-settings:…").</summary>
    OpenUri,
    /// <summary>Open the classic Power Options control panel.</summary>
    OpenPowerOptions,
    /// <summary>Turn on System Restore for the system drive (admin).</summary>
    EnableRestore,
    /// <summary>Apply a catalog tweak by id through the journal (admin). Falls back to the Optimize page.</summary>
    ApplyTweak,
}

/// <summary>
/// What the button next to a check does. Nothing here touches the system by itself.
/// <paramref name="Section"/>: for <see cref="CheckActionKind.Navigate"/>, the tab to open on that page
/// (for "drivers" a <c>DriversTab</c> name such as "Hardware" or "Problems").
/// </summary>
public sealed record CheckAction(CheckActionKind Kind, string Label, string? Target = null, string? Section = null)
{
    /// <summary>Actions that change the system need administrator rights (they go through ChangeSession / WMI).</summary>
    public bool RequiresAdmin => Kind is CheckActionKind.EnableRestore or CheckActionKind.ApplyTweak;
}

/// <summary>What the tweak catalog says about the tweak behind an <see cref="CheckActionKind.ApplyTweak"/> button.</summary>
public enum TweakAvailability
{
    /// <summary>Offered for this PC and not set yet: the button applies it through the journal.</summary>
    Available,
    /// <summary>Already set; if it needs a restart, the check still sees the old state until then.</summary>
    AlreadyApplied,
    /// <summary>The catalog does not offer it for this PC: there is no button at all.</summary>
    NotApplicable,
    /// <summary>Unknown to the catalog or an expert item: the Dashboard never applies it, the button only opens Optimize.</summary>
    OptimizeOnly,
}

/// <summary>
/// Result of one health check. <see cref="Weight"/> is how many points a warning costs in the health score
/// (information costs a fifth of it, OK costs nothing).
/// </summary>
public sealed record HealthCheckResult(string Id, string Title, string Detail, CheckSeverity Severity, int Weight, CheckAction? Action = null);
