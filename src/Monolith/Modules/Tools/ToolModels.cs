namespace Monolith.Modules.Tools;

/// <summary>Every runnable operation on the Tools page.</summary>
public enum ToolOp
{
    DismCheck, DismScan, DismRestore, Sfc,
    ChkdskScan, ChkntfsQuery, CopyChkdskCommand,
    DefragAll, DefragVolume,
    FlushDns, NetworkReset,
    StoreReset, IconCache,
    BatteryReport, MemoryDiagnostic,
    SleepWakeDiagnostics, EnergyReport, SleepStudyReport, SystemSleepDiagnostics,
}

/// <summary>One read-only powercfg query of the «Сон и пробуждение» card (arguments as documented on Microsoft Learn).</summary>
public sealed record PowercfgQuery(string Arguments, string Title, bool RequiresAdmin);

/// <summary>Extra content rendered inside a tool card.</summary>
public enum ToolExtra { None, BootCheck }

/// <summary>A button on a tool card.</summary>
public sealed class ToolCardAction
{
    public required ToolOp Op { get; init; }
    public required string Label { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsDanger { get; init; }
    public bool RequiresAdmin { get; init; }
    /// <summary>The volume of a per-drive <see cref="ToolOp.DefragVolume"/> button.</summary>
    public VolumeInfo? Volume { get; init; }
    /// <summary>Optional tooltip (free space of a drive, …).</summary>
    public string? Hint { get; init; }
    public bool IsPlain => !IsPrimary && !IsDanger;
}

/// <summary>One repair / maintenance tool: what it does, how long it takes, whether it needs admin rights.</summary>
public sealed class ToolCard
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    public required string Command { get; init; }
    public required string Description { get; init; }
    /// <summary>Second paragraph: order of steps, risks, how to undo.</summary>
    public string? Note { get; init; }
    public required string Duration { get; init; }
    public bool RequiresAdmin { get; init; }
    public bool NeedsReboot { get; init; }
    public bool IsDanger { get; init; }
    public required IReadOnlyList<ToolCardAction> Actions { get; init; }
    public ToolExtra Extra { get; init; }
    public bool HasBootCheck => Extra == ToolExtra.BootCheck;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

public sealed class ToolSection
{
    public required string Title { get; init; }
    public required string Caption { get; init; }
    public required IReadOnlyList<ToolCard> Cards { get; init; }
}

/// <summary>A tile that opens a standard Windows tool or settings page.</summary>
public sealed class QuickLink
{
    public required string Glyph { get; init; }
    public required string Title { get; init; }
    /// <summary>What is started, shown in mono: "devmgmt.msc", "ms-settings:display".</summary>
    public required string Command { get; init; }
    public required string Target { get; init; }
    public string? Arguments { get; init; }
}

public enum ConsoleLineKind { Output, Command, Info, Success, Error }

public sealed record ConsoleLine(string Time, string Text, ConsoleLineKind Kind, bool IsProgress = false)
{
    public bool IsCommand => Kind == ConsoleLineKind.Command;
    public bool IsEmphasis => Kind is ConsoleLineKind.Success or ConsoleLineKind.Error;
    public string Marker => Kind switch
    {
        ConsoleLineKind.Command => ">",
        ConsoleLineKind.Success => "OK",
        ConsoleLineKind.Error => "ERR",
        ConsoleLineKind.Info => "·",
        _ => "",
    };
}

/// <summary>Segoe Fluent Icons code points used only by this module (in addition to UI/Glyphs).</summary>
internal static class ToolGlyphs
{
    public const string HardDrive = "";
    public const string AllApps = "";
    public const string List = "";
    public const string Battery = "";
    public const string Store = "";
    public const string Picture = "";
    public const string Speed = "";
    public const string Health = "";
    public const string Wifi = "";
    public const string Display = "";
    public const string Terminal = "";
}
