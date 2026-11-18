using System.Text.RegularExpressions;
using Lycoris.Modules.Services;

namespace Lycoris.Core.Assistant;

internal enum ServiceRequestKind { None, MissingTarget, Invalid, Ready }

internal readonly record struct ParsedServiceRequest(ServiceRequestKind Kind, ServiceActionRequest? Request = null);

/// <summary>Recognizes a single explicit service command without a model or a service scan.</summary>
internal static class ServiceRequestParser
{
    private const RegexOptions Words = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly string Verb = Loc.Keep(
        @"(?:(?<enable>включи(?:ть)?|enable)|(?<disable>отключи(?:ть)?|выключи(?:ть)?|disable)|(?<start>запусти(?:ть)?|start)|останови(?:ть)?|stop)");
    private static readonly string Courtesy = Loc.Keep(@"(?:(?:пожалуйста|please) *,? +)?");
    private static readonly string Desire = Loc.Keep(@"(?:(?:(?:я +)?хочу|мне +нужно|i +(?:want|need) +to) +)?");
    private static readonly Regex Command = new(@"\A" + Courtesy + Desire + Verb + @"(?: +(?<tail>.*))?\z", Words);
    private static readonly Regex CommandPrefix = new(@"\A" + Courtesy + Desire +
        Loc.Keep(@"(?:(?:не|никогда +не|do +not|don't|if|если) +)?") + Verb + @"\b", Words);
    private static readonly Regex ServiceWord = new(Loc.Keep(@"\b(?:служб[а-яё]*|services?)\b"), Words);
    private static readonly Regex MentionedVerb = new(@"\b" + Verb + @"\b", Words);
    private static readonly Regex ServicePrefix = new(Loc.Keep(@"\A(?:службу|(?:the +)?service)(?: +|\z)"), Words);
    private static readonly Regex Clause = new(Loc.Keep(
        @"\b(?:если|потому|почему|чтобы|когда|потом|не|никогда|пожалуйста|кроме|вместо|all|both|if|because|why|when|then|not|please|except|instead|все|всё|обе|оба|другие)\b"),
        Words);
    private static readonly Regex PluralService = new(Loc.Keep(@"\A(?:службы|services)\z"), Words);
    private static readonly Regex Conjunction = new(Loc.Keep(@"\b(?:и|или|либо|and|or)\b"), Words);
    private static readonly Regex PrintAlias = new(Loc.Keep(@"\A(?:print spooler|служба печати|диспетчер печати)\z"), Words);
    private static readonly Regex TargetName = new(@"\A[\p{L}\p{N}_][\p{L}\p{M}\p{N}_.()+ –-]{0,255}\z");

    internal static ParsedServiceRequest Parse(string input)
    {
        // Trim spaces only. Line breaks and other control characters are refused rather than removed.
        var text = input.Trim(' ');
        var match = text.Length <= 1000 ? Command.Match(text) : Match.Empty;
        if (!match.Success)
            return new(CommandPrefix.IsMatch(text) || (ServiceWord.IsMatch(text) && MentionedVerb.IsMatch(text))
                ? ServiceRequestKind.Invalid : ServiceRequestKind.None);

        var tail = match.Groups["tail"].Value;
        var prefix = ServicePrefix.Match(tail);
        var target = tail[prefix.Length..];
        var name = Unquote(target, out var quoted);
        if (name.Length == 0) return new(ServiceRequestKind.MissingTarget);
        if (!IsName(name) || MentionedVerb.IsMatch(name) || Clause.IsMatch(name) || PluralService.IsMatch(name) ||
            (!quoted && (Conjunction.IsMatch(name) || (!prefix.Success && name.Contains(' ') && !PrintAlias.IsMatch(name)))))
            return new(ServiceRequestKind.Invalid);

        var action = match.Groups["enable"].Success ? ServiceAction.Enable
            : match.Groups["disable"].Success ? ServiceAction.Disable
            : match.Groups["start"].Success ? ServiceAction.Start : ServiceAction.Stop;
        return new(ServiceRequestKind.Ready, new(action, name));
    }

    /// <summary>The replay payload encodes one operation and the already resolved SCM name.</summary>
    internal static string? FormatReplay(ServiceAction action, string? canonicalName)
    {
        if (canonicalName is null || !IsName(canonicalName)) return null;
        var operation = action switch
        {
            ServiceAction.Enable => "enable:",
            ServiceAction.Disable => "disable:",
            ServiceAction.Start => "start:",
            ServiceAction.Stop => "stop:",
            _ => null,
        };
        return operation is null ? null : operation + (canonicalName.Contains(' ') ? $"\"{canonicalName}\"" : canonicalName);
    }

    internal static ServiceActionRequest? ParseReplay(string? parameter)
    {
        if (parameter is not { Length: > 0 and <= 266 }) return null;
        var operationEnd = parameter.IndexOf(':');
        var action = operationEnd < 0 ? null : parameter[..operationEnd] switch
        {
            "enable" => (ServiceAction?)ServiceAction.Enable,
            "disable" => ServiceAction.Disable,
            "start" => ServiceAction.Start,
            "stop" => ServiceAction.Stop,
            _ => null,
        };
        if (action is null) return null;
        var name = Unquote(parameter[(operationEnd + 1)..], out _);
        if (!string.Equals(FormatReplay(action.Value, name), parameter, StringComparison.Ordinal)) return null;
        return new ServiceActionRequest(action.Value, name) { ExactNameOnly = true };
    }

    private static bool IsName(string name) => TargetName.IsMatch(name) && name[^1] != ' ' && !name.Contains(" -", StringComparison.Ordinal);

    private static string Unquote(string target, out bool quoted)
    {
        var closing = target.Length == 0 ? '\0' : target[0] switch
        {
            '"' => '"',
            '\'' => '\'',
            '«' => '»',
            '“' => '”',
            _ => '\0',
        };
        quoted = closing != '\0' && target.Length >= 2 && target[^1] == closing;
        return quoted ? target[1..^1] : target;
    }
}
