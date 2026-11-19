using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Lycoris.Core;

namespace Lycoris.Modules.Games;

internal sealed record BackgroundCloseCliOptions(IReadOnlyList<string> Names, bool Apply, bool Force, string? Plan, string? Language);

internal static class BackgroundAppCloseCommandLine
{
    public const string Switch = "--close-background-apps";
    private const int MaxArguments = 9;
    private const int MaxNames = 32;
    private const int MaxNameLength = 96;
    private const int MaxSelectionLength = MaxNames * (MaxNameLength + 1) - 1;

    internal static class Exit
    {
        public const int Ok = 0;
        public const int InvalidArguments = 2;
        public const int NeedsAdmin = 3;
        public const int StalePlan = 4;
        public const int Denied = 5;
        public const int Partial = 6;
        public const int Busy = 7;
        public const int NoConsole = 8;
        public const int Error = 9;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<BackgroundCloseStatus>(JsonNamingPolicy.CamelCase) },
    };

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));

    internal static bool TryParseNames(string? input, out IReadOnlyList<string> names)
    {
        names = [];
        if (input is null || input.Length == 0 || input.Length > MaxSelectionLength) return false;
        var parts = input.Split(',');
        if (parts.Length > MaxNames) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > MaxNameLength || !char.IsLetterOrDigit(part[0]) ||
                part.Any(c => !char.IsLetterOrDigit(c) && c is not (' ' or '.' or '-' or '_'))) return false;

            var name = part.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? part[..^4] : part;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith('.') || name.EndsWith(' ') || !seen.Add(name)) return false;
            parts[i] = name;
        }
        names = Array.AsReadOnly(parts);
        return true;
    }

    internal static BackgroundCloseCliOptions? Parse(IReadOnlyList<string> args)
    {
        if (args.Count > MaxArguments) return null;
        IReadOnlyList<string>? names = null;
        string? plan = null;
        string? language = null;
        bool dryRun = false, yes = false, force = false;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i]?.ToLowerInvariant())
            {
                case Switch when names is null:
                    if (++i >= args.Count || !TryParseNames(args[i], out names)) return null;
                    break;
                case "--dry-run" when !dryRun:
                    dryRun = true;
                    break;
                case "--yes" when !yes:
                    yes = true;
                    break;
                case "--force" when !force:
                    force = true;
                    break;
                case "--plan" when plan is null:
                    if (++i >= args.Count || args[i] is not { Length: 64 } token || !token.All(char.IsAsciiHexDigit))
                        return null;
                    plan = token;
                    break;
                case "--lang" when language is null:
                    if (++i >= args.Count || args[i] is not ("ru" or "en")) return null;
                    language = args[i];
                    break;
                default:
                    return null;
            }
        }
        if (names is null || (plan is not null && !yes) || (yes && !dryRun && plan is null)) return null;
        return new(names, yes && !dryRun, force, plan, language);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        Loc.Initialize(Parse(args)?.Language);
        using var output = CliConsole.Open();
        if (output is null)
        {
            MessageBox.Show(
                Loc.T("Запустите Lycoris из консоли или перенаправьте стандартный вывод.") + "\n\n" +
                "Lycoris.exe --close-background-apps <names> [--dry-run] [--force] [--yes --plan <64hex>] [--lang ru|en]",
                "Lycoris", MessageBoxButton.OK, MessageBoxImage.Information);
            return Exit.NoConsole;
        }
        try { return await ExecuteAsync(args, Elevation.IsElevated, BackgroundAppClosure.Engine, output); }
        catch (IOException) { return Exit.Error; }
    }

    internal static async Task<int> ExecuteAsync(IReadOnlyList<string> args, bool isElevated, BackgroundCloseEngine engine, TextWriter? output)
    {
        if (output is null) return Exit.NoConsole;
        var options = Parse(args);
        BackgroundClosePlan? plan = null;
        var mode = options?.Apply == true ? "apply" : "preview";

        int Report(int code, string status, IReadOnlyList<BackgroundCloseResult>? results = null)
        {
            output.WriteLine(JsonSerializer.Serialize(new
            {
                version = 1,
                isElevated,
                mode,
                plan = plan?.Fingerprint,
                names = options?.Names ?? [],
                targets = plan?.Targets ?? [],
                unavailable = plan?.Unavailable ?? [],
                status,
                results = results ?? [],
            }, Json));
            return code;
        }

        if (options is null) return Report(Exit.InvalidArguments, "invalidArguments");
        if (options.Apply && !isElevated) return Report(Exit.NeedsAdmin, "needsAdmin");
        try
        {
            if (options.Apply)
                return await OperationCoordinator.Instance.RunAsync(BackgroundAppClosure.CreateRequest(), _ => ApplyAsync()).ConfigureAwait(false);

            plan = engine.Capture(options.Names, force: options.Force);
            return plan.HasFullSelection ? Report(Exit.Ok, "preview") : Report(Exit.Denied, "unavailable");
        }
        catch (Exception ex) when (ex is OperationRefusedException or OperationTimeoutException)
        {
            return Report(Exit.Busy, "busy");
        }
        catch (Exception ex) when (ex is not IOException)
        {
            return Report(Exit.Error, "error");
        }

        async Task<int> ApplyAsync()
        {
            // Compare the complete selection while holding the Disruptive lease.
            plan = engine.Capture(options.Names, force: options.Force);
            if (!plan.HasFullSelection) return Report(Exit.Denied, "unavailable");
            if (!string.Equals(plan.Fingerprint, options.Plan, StringComparison.OrdinalIgnoreCase))
                return Report(Exit.StalePlan, "stalePlan");
            if (plan.Unavailable.Count > 0) return Report(Exit.Denied, "denied");
            if (plan.Targets.Count == 0) return Report(Exit.Ok, "noTargets");

            var outcome = await engine.CloseAsync(plan, options.Force ? _ => Task.FromResult(true) : null).ConfigureAwait(false);
            if (outcome.PreflightAborted)
                return outcome.Results.Any(r => r.Status == BackgroundCloseStatus.Denied)
                    ? Report(Exit.Denied, "denied", outcome.Results)
                    : Report(Exit.StalePlan, "stalePlan", outcome.Results);
            return outcome.SelectionVerified && outcome.Results.All(r => r.Status is BackgroundCloseStatus.Closed or BackgroundCloseStatus.Forced)
                ? Report(Exit.Ok, "closed", outcome.Results)
                : Report(Exit.Partial, "partial", outcome.Results);
        }
    }
}
