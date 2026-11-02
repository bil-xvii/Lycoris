using CommunityToolkit.Mvvm.ComponentModel;

namespace Lycoris.Modules.Devices;

public enum PlanItemKind
{
    /// <summary>A device setting applied through its own Apply.</summary>
    Setting,
    /// <summary>An Optimize tweak applied through TweakEngine (its own source "tweak:&lt;id&gt;").</summary>
    OptimizeTweak,
    /// <summary>Needs a measurement first: not selectable, links the tool.</summary>
    Measurement,
    /// <summary>A finding: not selectable, a link only.</summary>
    Advice,
}

/// <summary>One row of the АВТОНАСТРОЙКА plan: «сейчас → станет» with a one-line reason.</summary>
public sealed partial class PlanItem : ObservableObject
{
    public required DeviceCard Card { get; init; }
    public required DeviceSetting Setting { get; init; }
    public required PlanItemKind Kind { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Reason { get; init; } = "";
    public RecommendPolicy Policy { get; init; }
    public DeviceLink? Link { get; init; }
    public bool Selectable { get; init; }
    public string? DisabledReason { get; init; }

    /// <summary>
    /// A measurement row runs its tool right here («ЗАМЕРИТЬ»: progress and result under the row) instead of sending the user
    /// to another tab. Null for rows that are not measurements or whose tool could not be found.
    /// </summary>
    public DeviceTool? MeasureTool { get; init; }
    public bool HasMeasureTool => MeasureTool is not null;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isHighlighted;

    /// <summary>Card key + setting id: keeps the user's checkbox across re-scans.</summary>
    public string Key => $"{Card.Key}|{Setting.Id}";
    public string? TweakId => Setting.TweakId;
    public string GroupLabel => Card.GroupTitle.ToUpperInvariant();
    public string DeviceTitle => Card.Title;
    /// <summary>«ЗВУК · Микрофон (…)»; just «МЫШЬ» when the device has no own name (never «МЫШЬ · Мышь»).</summary>
    public string Header => string.Equals(Card.Title.Trim(), Card.GroupTitle, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(Card.Title.Trim(), Card.KindLabel, StringComparison.OrdinalIgnoreCase)
        ? GroupLabel
        : $"{GroupLabel}  ·  {DeviceTitle}";
    public bool NeedsCountdown => Setting.NeedsCountdown;
    public bool IsOptional => Policy == RecommendPolicy.Suggest;
    public bool HasChange => Kind is PlanItemKind.Setting or PlanItemKind.OptimizeTweak;

    /// <summary>The tag of the row: БЕЗОПАСНО / ПО ЖЕЛАНИЮ / НУЖНО ИЗМЕРЕНИЕ / С ПОДТВЕРЖДЕНИЕМ.</summary>
    public string TagText => Kind switch
    {
        PlanItemKind.Measurement => Loc.T("НУЖНО ИЗМЕРЕНИЕ"),
        PlanItemKind.Advice => Loc.T("СОВЕТ"),
        _ when NeedsCountdown => Loc.T("С ПОДТВЕРЖДЕНИЕМ"),
        _ => Policy == RecommendPolicy.Include ? Loc.T("БЕЗОПАСНО") : Loc.T("ПО ЖЕЛАНИЮ"),
    };

    /// <summary>"Tag" for БЕЗОПАСНО, "Muted" for the rest (EqualsToVis in the view).</summary>
    public string TagStyle => Kind is PlanItemKind.Setting or PlanItemKind.OptimizeTweak && Policy == RecommendPolicy.Include && !NeedsCountdown
        ? "Tag" : "Muted";

    /// <summary>Tweak rows say where the setting lives.</summary>
    public string? Source => Kind == PlanItemKind.OptimizeTweak ? Loc.T("пункт «Оптимизации»") : null;
}

/// <summary>Builds the plan from the cards (pure).</summary>
public static class DevicePlan
{
    /// <summary>
    /// Every setting whose recommendation (policy ≠ None) differs from the live value, plus findings (Advice, always) and
    /// rows that need a measurement. Include is pre-selected, Suggest is not; per-user rows are not selectable when Lycoris
    /// does not run as the desktop user; read-only rows never are. Rows of one Optimize tweak or link appear once. Rows
    /// with an on-screen confirmation come last. <paramref name="previousSelection"/> keeps the user's checkboxes by key.
    /// </summary>
    public static IReadOnlyList<PlanItem> Build(IEnumerable<DeviceCard> cards, bool interactiveUser,
        IReadOnlyDictionary<string, bool>? previousSelection = null)
    {
        return BuildCore(cards, interactiveUser, previousSelection);
    }

    /// <summary>
    /// The row writes into the account's profile: a per-user device setting, or a tweak row whose Optimize tweak writes only
    /// HKCU («mouse-acceleration-off», «audio-ducking», «vrr»): applied from the plan it would land in the desktop user's hive
    /// only when Lycoris runs as that user.
    /// </summary>
    public static bool IsPerUser(DeviceSetting s) =>
        s.PerUser || (s.TweakId is { } id && Lycoris.Tweaks.TweakCatalog.Find(id) is Lycoris.Tweaks.RegistryTweak t
                      && t.Values.Count > 0 && t.Values.All(v => v.Hive == Lycoris.Core.RegHive.HKCU));

    /// <summary>The tool a measurement row asks for: the setting's own tool, else the card's tool named by the row's link.</summary>
    internal static DeviceTool? ToolOf(DeviceCard card, DeviceSetting setting, DeviceLink? link)
    {
        if (setting.Tool is { } own) return own;
        if (link is not { Kind: DeviceLinkKind.Tool, FocusId: { Length: > 0 } id }) return null;
        return card.Tools.Concat(card.Settings.Select(s => s.Tool).OfType<DeviceTool>()).FirstOrDefault(t => t.Id == id);
    }

    private static IReadOnlyList<PlanItem> BuildCore(IEnumerable<DeviceCard> cards, bool interactiveUser, IReadOnlyDictionary<string, bool>? previousSelection)
    {
        var items = new List<PlanItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in cards)
        {
            foreach (var s in card.Settings.OrderBy(s => s.Order))
            {
                if (s.Recommended is not { Policy: not RecommendPolicy.None } rec) continue;

                // One row per Optimize tweak / per link target, whichever card asks first.
                var dedupe = s.TweakId is { } tid ? "tweak:" + tid
                    : rec.Link is { FocusId: { } fid } ? "link:" + rec.Link.Kind + ":" + fid
                    : null;

                PlanItemKind kind;
                if (rec.Policy == RecommendPolicy.Advice) kind = PlanItemKind.Advice;
                else if (rec.NeedsMeasurement) kind = PlanItemKind.Measurement;
                else
                {
                    if (s.Current is not { } current || s.SameValue(current, rec.Value)) continue;
                    kind = s.TweakId is not null ? PlanItemKind.OptimizeTweak : PlanItemKind.Setting;
                }
                if (dedupe is not null && !seen.Add(dedupe)) continue;

                string? disabled = null;
                var selectable = kind is PlanItemKind.Setting or PlanItemKind.OptimizeTweak;
                if (selectable && kind == PlanItemKind.Setting && s.Apply is null) { selectable = false; disabled = Loc.T("Только для просмотра."); }
                if (selectable && s.DisabledReason is { } why) { selectable = false; disabled = why; }
                if (selectable && IsPerUser(s) && !interactiveUser)
                {
                    selectable = false;
                    disabled = Loc.T("Недоступно: Lycoris запущен не от вашей учётной записи (или её не удалось определить).");
                }

                var item = new PlanItem
                {
                    Card = card, Setting = s, Kind = kind, Policy = rec.Policy, Link = rec.Link ?? s.Link, Selectable = selectable,
                    DisabledReason = disabled, Reason = rec.Reason,
                    MeasureTool = kind == PlanItemKind.Measurement ? ToolOf(card, s, rec.Link ?? s.Link) : null,
                    From = s.Current is { } c ? s.FormatValue(c) : "—",
                    To = kind is PlanItemKind.Advice or PlanItemKind.Measurement ? "" : s.FormatValue(rec.Value),
                };
                item.IsSelected = selectable && (previousSelection is not null && previousSelection.TryGetValue(item.Key, out var was)
                    ? was
                    : rec.Policy == RecommendPolicy.Include);
                items.Add(item);
            }
        }
        // Stable: changes first (countdown ones last among them), then measurements, then findings.
        return items
            .Select((item, index) => (item, index))
            .OrderBy(x => x.item.Kind switch
            {
                PlanItemKind.Setting or PlanItemKind.OptimizeTweak => x.item.NeedsCountdown ? 1 : 0,
                PlanItemKind.Measurement => 2,
                _ => 3,
            })
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
    }
}
