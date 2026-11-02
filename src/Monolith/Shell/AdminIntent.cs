using System.Reflection;
using System.Windows.Input;

namespace Monolith.Shell;

/// <summary>
/// What the user wanted to do when Lycoris had no admin rights: a page, the name of one of its commands and the
/// command parameter (text only). It travels to the elevated copy as launch arguments and is replayed there exactly
/// once — through the same command, with the same confirmation dialog. Only replayed commands are remembered: never
/// the content of a change, so nothing can be applied without the page's own checks.
/// </summary>
/// <param name="PageKey">Page to open ("optimize", "restore"); the host resolves aliases.</param>
/// <param name="Command">
/// Command name without the «Command» suffix ("Apply", "RevertBatch"), looked up as a public ICommand property
/// «{Command}Command» on the page view-model, then on the shell view-model.
/// </param>
/// <param name="Parameter">Command parameter as text (an id, a tab key) or null.</param>
public sealed record AdminIntent(string PageKey, string Command, string? Parameter = null)
{
    public const string PageArg = "--replay-page";
    public const string CommandArg = "--replay-command";
    public const string ParameterArg = "--replay-param";

    /// <summary>Launch arguments for the elevated copy.</summary>
    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string> { PageArg, PageKey, CommandArg, Command };
        if (Parameter is { Length: > 0 } p) args.AddRange([ParameterArg, p]);
        return args;
    }

    /// <summary>The intent carried by launch arguments, or null when there is none (or it is incomplete).</summary>
    public static AdminIntent? Parse(IReadOnlyList<string> args)
    {
        string? page = null, command = null, parameter = null;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(PageArg, StringComparison.OrdinalIgnoreCase)) page = args[++i];
            else if (args[i].Equals(CommandArg, StringComparison.OrdinalIgnoreCase)) command = args[++i];
            else if (args[i].Equals(ParameterArg, StringComparison.OrdinalIgnoreCase)) parameter = args[++i];
        }
        if (string.IsNullOrWhiteSpace(page) || string.IsNullOrWhiteSpace(command) || !IsCommandName(command) || !IsReplayable(command)) return null;
        // The arguments of an elevated copy are text anybody who can start the exe can write: the page is a plain key and the
        // parameter a short line without control characters — nothing else is ever handed to a command.
        if (!IsPageKey(page.Trim())) return null;
        if (!string.IsNullOrEmpty(parameter) && !IsParameter(parameter)) return null;
        return new AdminIntent(page.Trim(), command.Trim(), string.IsNullOrEmpty(parameter) ? null : parameter);
    }

    /// <summary>Longest parameter a replayed command may receive: ids, GUIDs, tab keys are short, a packed list of checked items (IntentList, base64) is a few KB; the command line takes 32 K.</summary>
    public const int MaxParameterLength = 4096;

    /// <summary>Page keys are lower-case words with digits and hyphens («optimize», «devices-manual»).</summary>
    public static bool IsPageKey(string? key) =>
        key is { Length: > 0 and <= 32 } && char.IsAsciiLetterLower(key[0]) && key.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    /// <summary>A parameter is one printable line: no control characters (no line breaks, no NUL), bounded length.</summary>
    public static bool IsParameter(string? parameter) =>
        parameter is { Length: > 0 and <= MaxParameterLength } && !parameter.Any(char.IsControl);

    /// <summary>
    /// Arguments that must not be carried over to a relaunched copy (they describe this run only) — the shared list in
    /// <see cref="Monolith.Core.Elevation.PerRunArgs"/>, which the relaunch strips.
    /// </summary>
    public static IReadOnlySet<string> VolatileArgs => Monolith.Core.Elevation.PerRunArgs;

    /// <summary>
    /// The commands that may be replayed after a relaunch — exactly the names passed to <c>RequireAdmin(action, command, …)</c>.
    /// Each shows its own confirmation. A launch argument naming anything else is refused, so a new command without a dialog
    /// is never reachable through <c>--replay-command</c> until it is listed here on purpose.
    /// </summary>
    public static readonly IReadOnlySet<string> Replayable = new HashSet<string>(StringComparer.Ordinal)
    {
        "Apply", "ApplyPlan", "ApplyProfile", "ApplyRecommendations", "ApplyRecommended", "ApplySetting", "ApplyStartType", "ApplyTasks",
        "Clean", "CreateRestorePoint", "DeleteBackup", "DisableExtraStartup", "DisableSelectedStartup", "DownloadDriver", "EnableDevice",
        "EnableProtection", "EnableSelectedStartup", "ExportBackup", "FixSafe", "GpuDownload", "InstallHardware", "InstallUpdates",
        "PlayBoostedGame", "ReapplyAllDrift", "ReapplyDrift", "RepairWindows", "RestoreBackup", "RestoreStale", "RevertAll", "RevertBatch",
        "RevertItem", "RevertLastBatch", "RevertRecord", "RevertService", "RevertSetting", "RevertTask", "RunCheck", "RunTool", "ShowPoints",
        "StartBoost", "StartService", "StopService", "ToggleGame", "ToggleGpuGame", "ToggleLogonTask", "ToggleStartup", "UseGpuTool", "UseIntelDsa",
    };

    public static bool IsReplayable(string name) => Replayable.Contains(name.Trim());

    /// <summary>Command names are identifiers: letters and digits only (an argument can never name a member path).</summary>
    public static bool IsCommandName(string name) => name.Length is > 0 and <= 64 && name.All(char.IsAsciiLetterOrDigit);

    /// <summary>
    /// Finds «{Command}Command» on <paramref name="target"/> and runs it once with <see cref="Parameter"/>.
    /// False when the command does not exist or cannot execute right now (the page is busy, nothing to do).
    /// </summary>
    public bool TryExecuteOn(object target)
    {
        if (!IsReplayable(Command) || FindCommand(target) is not { } command) return false;
        if (!command.CanExecute(Parameter)) return false;
        command.Execute(Parameter);
        return true;
    }

    /// <summary>The ICommand property «{Command}Command» of <paramref name="target"/>, or null.</summary>
    public ICommand? FindCommand(object target)
    {
        if (!IsCommandName(Command)) return null;
        var property = target.GetType().GetProperty(Command + "Command", BindingFlags.Public | BindingFlags.Instance);
        return property is not null && typeof(ICommand).IsAssignableFrom(property.PropertyType) ? property.GetValue(target) as ICommand : null;
    }

    /// <summary>Short Russian description for logs and toasts.</summary>
    public override string ToString() => $"{PageKey} → {Command}{(Parameter is null ? "" : $"({Parameter})")}";
}
