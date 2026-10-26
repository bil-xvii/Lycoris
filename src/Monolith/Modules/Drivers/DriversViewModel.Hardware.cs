namespace Monolith.Modules.Drivers;

/// <summary>
/// "ПОДБОР ДЛЯ ВАШЕГО ПК": hardware analysis and recommended official components.
/// Owned by the hardware-analysis work; the rest of DriversViewModel lives in the other partial files.
/// </summary>
public sealed partial class DriversViewModel
{
    /// <summary>Called from LoadAsync (fire-and-forget): scans the hardware and builds the recommendations.</summary>
    private partial Task StartHardwareScanAsync() => Task.CompletedTask;
}
