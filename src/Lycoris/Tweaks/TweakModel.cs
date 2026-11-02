using System.ServiceProcess;
using Microsoft.Win32;
using Lycoris.Core;

namespace Lycoris.Tweaks;

public enum TweakCategory { Gaming, Performance, Power, Network, Privacy, Interface, Input, System }

/// <summary>How careful the user should be.</summary>
public enum TweakRisk
{
    /// <summary>Officially supported setting, no downside for the target audience. Included in profiles by default.</summary>
    Safe,
    /// <summary>Harmless but changes behaviour the user may want (e.g. visual effects). Offered unchecked.</summary>
    Optional,
    /// <summary>Real trade-off (security, compatibility). Hidden behind a warning, never applied by profiles.</summary>
    Advanced,
}

[Flags]
public enum ProfileSet
{
    None = 0,
    Normal = 1,
    Gaming = 2,
    Recommended = 4,
    /// <summary>Scenario «Стриминг и запись»: no second encoder next to OBS, no capture conflicts, no recent files on air.</summary>
    Streaming = 8,
    /// <summary>Scenario «Тихий ПК» / «Ноутбук: автономность»: efficient power mode and shorter timeouts, never a performance item.</summary>
    Efficiency = 16,
    /// <summary>The three original profiles (ServiceCatalog pre-selects by these; the scenarios map onto them).</summary>
    All = Normal | Gaming | Recommended,
}

public enum RestartRequirement { None, Explorer, SignOut, Reboot }

public enum TweakState { Unknown, NotApplied, Applied, Partial, NotApplicable }

/// <summary>Names of <see cref="Tweak.ExclusiveGroup"/>: one group = one system target.</summary>
public static class TweakGroups
{
    /// <summary>The active power plan (High performance / Ultimate / Balanced / Balanced on X3D).</summary>
    public const string PowerPlan = "power-plan";
    /// <summary>The Windows 11 power mode when plugged in.</summary>
    public const string PowerModeAc = "power-mode-ac";
    /// <summary>The Windows 11 power mode on battery.</summary>
    public const string PowerModeDc = "power-mode-dc";
    /// <summary>Hardware-accelerated GPU scheduling (HwSchMode).</summary>
    public const string Hags = "hags";
}

/// <summary>Everything a tweak needs while applying itself.</summary>
public sealed class TweakContext
{
    public required ChangeSession Session { get; init; }
    public required SystemProfile System { get; init; }
    public required Tweak Tweak { get; init; }
    public string Source => "tweak:" + Tweak.Id;

    public bool SetRegistry(RegHive hive, string path, string name, RegistryValueKind kind, object? value)
        => Session.SetRegistry(hive, path, name, kind, value, Source, Tweak.TitleRu);

    public bool SetService(string service, ServiceStartMode mode, bool delayed = false)
        => Session.SetServiceStart(service, mode, Source, Tweak.TitleRu, delayed);

    public bool SetTask(string taskPath, bool enabled) => Session.SetTaskEnabled(taskPath, enabled, Source, Tweak.TitleRu);

    public bool SetPowerScheme(Guid scheme, Guid? created = null) => Session.SetPowerScheme(scheme, Source, Tweak.TitleRu, created);
}

/// <summary>
/// One optimization. Tweaks never write to the system directly: they go through <see cref="TweakContext"/>,
/// so every change is journaled with its original value and can be reverted from the Restore center.
/// </summary>
public abstract class Tweak
{
    public required string Id { get; init; }
    private string _title = "";
    /// <summary>Shown text (translated); the journal always gets <see cref="TitleRu"/> so data on disk stays language-neutral.</summary>
    public required string Title { get => TitleDisplay?.Invoke() ?? Loc.T(_title); init => _title = value; }
    /// <summary>For a title built from live data (e.g. the installed version): the translated text; <see cref="Title"/> init then holds the Russian one.</summary>
    public Func<string>? TitleDisplay { get; init; }
    /// <summary>The Russian title (the translation key); written to the journal.</summary>
    public string TitleRu => _title;
    /// <summary>One or two sentences: what changes, in plain Russian.</summary>
    public required string Description { get; init; }
    /// <summary>Why / evidence / caveats. Shown when the user expands the item.</summary>
    public string Details { get; init; } = "";
    public TweakCategory Category { get; init; }
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;
    /// <summary>Profiles in which this tweak is pre-checked.</summary>
    public ProfileSet DefaultIn { get; init; } = ProfileSet.None;
    public RestartRequirement Restart { get; init; } = RestartRequirement.None;
    /// <summary>Optional applicability check (e.g. desktop only, Windows 11 only, NVIDIA only).</summary>
    public Func<SystemProfile, bool>? AppliesTo { get; init; }
    /// <summary>Shown when <see cref="AppliesTo"/> returns false.</summary>
    public string? NotApplicableReason { get; init; }
    /// <summary>For Advanced tweaks: the warning the user must accept.</summary>
    public string? Warning { get; init; }
    /// <summary>
    /// Members of one group write the same target (the active power plan, the AC power mode, HwSchMode…), so only one
    /// can be selected at a time: checking a member unchecks the others. Names in <see cref="TweakGroups"/>.
    /// </summary>
    public string? ExclusiveGroup { get; init; }
    /// <summary>
    /// Ids of tweaks this one depends on (e.g. a power mode needs the Balanced plan). Satisfied when ANY of them is
    /// applied, or checked and pending in the same apply — then this row can be selected even while its own detection
    /// says «not applicable», and the engine applies it after the dependency (re-detecting first).
    /// </summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    public bool IsApplicable(SystemProfile sp) => AppliesTo?.Invoke(sp) ?? true;

    /// <summary>True when <paramref name="other"/> is another member of this tweak's <see cref="ExclusiveGroup"/>.</summary>
    public bool SharesGroupWith(Tweak other) =>
        ExclusiveGroup is not null && !ReferenceEquals(this, other) && other.Id != Id &&
        string.Equals(ExclusiveGroup, other.ExclusiveGroup, StringComparison.Ordinal);

    /// <summary>Reads the current state from the system. Must be fast and side-effect free.</summary>
    public abstract TweakState Detect();

    /// <summary>Applies the tweak through the context (journaled).</summary>
    public abstract void Apply(TweakContext ctx);

    public string RiskText => Risk switch
    {
        TweakRisk.Safe => Loc.T("БЕЗОПАСНО"),
        TweakRisk.Optional => Loc.T("ПО ЖЕЛАНИЮ"),
        _ => Loc.T("ЭКСПЕРТ"),
    };

    public string CategoryText => Category switch
    {
        TweakCategory.Gaming => Loc.T("ИГРЫ"),
        TweakCategory.Performance => Loc.T("ПРОИЗВОДИТЕЛЬНОСТЬ"),
        TweakCategory.Power => Loc.T("ПИТАНИЕ"),
        TweakCategory.Network => Loc.T("СЕТЬ"),
        TweakCategory.Privacy => Loc.T("КОНФИДЕНЦИАЛЬНОСТЬ"),
        TweakCategory.Interface => Loc.T("ИНТЕРФЕЙС"),
        TweakCategory.Input => Loc.T("ВВОД"),
        _ => Loc.T("СИСТЕМА"),
    };

    public string RestartText => Restart switch
    {
        RestartRequirement.Explorer => Loc.T("перезапуск Проводника"),
        RestartRequirement.SignOut => Loc.T("нужен выход из системы"),
        RestartRequirement.Reboot => Loc.T("нужна перезагрузка"),
        _ => "",
    };

    /// <summary>When the change takes effect, for «Вступит в силу {RestartWhen}».</summary>
    public string RestartWhen => Restart switch
    {
        RestartRequirement.Explorer => Loc.T("после перезапуска Проводника"),
        RestartRequirement.SignOut => Loc.T("после выхода из системы"),
        RestartRequirement.Reboot => Loc.T("после перезагрузки"),
        _ => Loc.T("сразу"),
    };
}

/// <summary>A registry value to set. <see cref="Value"/> = null means "delete the value".</summary>
public sealed record RegSet(RegHive Hive, string Path, string Name, RegistryValueKind Kind, object? Value)
{
    public static RegSet Dword(RegHive hive, string path, string name, int value) => new(hive, path, name, RegistryValueKind.DWord, value);
    public static RegSet Dword(RegHive hive, string path, string name, uint value) => new(hive, path, name, RegistryValueKind.DWord, unchecked((int)value));
    public static RegSet Str(RegHive hive, string path, string name, string value) => new(hive, path, name, RegistryValueKind.String, value);
    public static RegSet Delete(RegHive hive, string path, string name) => new(hive, path, name, RegistryValueKind.DWord, null);
}

/// <summary>A tweak made of registry values. Applied = all values already match.</summary>
public sealed class RegistryTweak : Tweak
{
    public required IReadOnlyList<RegSet> Values { get; init; }

    public override TweakState Detect()
    {
        if (Values.Count == 0) return TweakState.Unknown;
        var matches = Values.Count(v => RegistryHelper.ValueEquals(v.Hive, v.Path, v.Name, v.Kind, v.Value));
        return matches == Values.Count ? TweakState.Applied : matches == 0 ? TweakState.NotApplied : TweakState.Partial;
    }

    public override void Apply(TweakContext ctx)
    {
        foreach (var v in Values)
        {
            if (RegistryHelper.ValueEquals(v.Hive, v.Path, v.Name, v.Kind, v.Value)) continue;
            ctx.SetRegistry(v.Hive, v.Path, v.Name, v.Kind, v.Value);
        }
    }
}

/// <summary>A tweak that changes service start types (to Manual by default — services still start on demand).</summary>
public sealed class ServiceTweak : Tweak
{
    public required IReadOnlyList<(string Service, ServiceStartMode Mode)> Services { get; init; }

    /// <summary>"Manual" is also satisfied by a stricter "Disabled" the user chose — never loosen it.</summary>
    internal static bool Satisfies(ServiceStartMode current, ServiceStartMode target) =>
        current == target || (target == ServiceStartMode.Manual && current == ServiceStartMode.Disabled);

    public override TweakState Detect()
    {
        var present = Services.Where(s => ServiceControl.Exists(s.Service)).ToList();
        if (present.Count == 0) return TweakState.NotApplicable;
        var matches = present.Count(s => ServiceControl.Query(s.Service) is { } q && Satisfies(q.StartMode, s.Mode));
        return matches == present.Count ? TweakState.Applied : matches == 0 ? TweakState.NotApplied : TweakState.Partial;
    }

    public override void Apply(TweakContext ctx)
    {
        foreach (var (service, mode) in Services)
        {
            var current = ServiceControl.Query(service);
            if (current is null || Satisfies(current.StartMode, mode)) continue;
            ctx.SetService(service, mode);
        }
    }
}

/// <summary>A tweak that disables (or enables) scheduled tasks.</summary>
public sealed class ScheduledTaskTweak : Tweak
{
    public required IReadOnlyList<string> Tasks { get; init; }
    public bool Enable { get; init; }

    public override TweakState Detect()
    {
        var infos = Tasks.Select(ScheduledTasks.Query).Where(t => t.Exists).ToList();
        if (infos.Count == 0) return TweakState.NotApplicable;
        var matches = infos.Count(t => t.Enabled == Enable);
        return matches == infos.Count ? TweakState.Applied : matches == 0 ? TweakState.NotApplied : TweakState.Partial;
    }

    public override void Apply(TweakContext ctx)
    {
        foreach (var t in Tasks)
        {
            var info = ScheduledTasks.Query(t);
            if (!info.Exists || info.Enabled == Enable) continue;
            ctx.SetTask(t, Enable);
        }
    }
}

/// <summary>A tweak with custom detection and application logic (still journaled through the context).</summary>
public sealed class CustomTweak : Tweak
{
    public required Func<TweakState> DetectFunc { get; init; }
    public required Action<TweakContext> ApplyAction { get; init; }

    public override TweakState Detect()
    {
        try { return DetectFunc(); }
        catch { return TweakState.Unknown; }
    }

    public override void Apply(TweakContext ctx) => ApplyAction(ctx);
}
