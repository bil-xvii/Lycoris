using System.ServiceProcess;
using Lycoris.Core;

namespace Lycoris.Modules.Services;

internal sealed record ServiceRecommendationChange(CuratedService Entry, ServiceActionFacts Facts);

internal sealed record ServiceRecommendationPlan(ServiceActionSettings Settings, IReadOnlyList<ServiceActionFacts> Observed,
    IReadOnlyList<ServiceRecommendationChange> Changes)
{
    public bool Matches(ServiceRecommendationPlan? current) => current is not null && Settings == current.Settings &&
        Observed.SequenceEqual(current.Observed) && Changes.SequenceEqual(current.Changes);

    public bool ApplyIfCurrent(Func<IEnumerable<string>, IDisposable?> hold, Func<ServiceRecommendationPlan?> readPlan,
        Func<ServiceActionSettings> readSettings, Func<ServiceRecommendationChange, ServiceActionFacts?> apply)
    {
        using var identities = hold(Observed.Select(facts => facts.Identity.Name));
        if (identities is null) return false;

        var expected = this;
        foreach (var change in Changes)
        {
            if (readSettings() != Settings || !expected.Matches(readPlan()) || readSettings() != Settings) return false;
            var written = apply(change);
            if (written is null) break;
            if (!MatchesOwnWrite(change, written)) return false;

            // Advance only the row just written and read back. Every other observation keeps the confirmed baseline.
            expected = expected with
            {
                Observed = expected.Observed.Select(facts => facts.Identity == written.Identity ? written : facts).ToArray(),
                Changes = expected.Changes.Skip(1).ToArray(),
            };
        }
        return true;
    }

    private static bool MatchesOwnWrite(ServiceRecommendationChange change, ServiceActionFacts written)
    {
        var before = change.Facts;
        var disabling = change.Entry.Target == ServiceStartMode.Disabled;
        return written.ActiveRecordId is { } recordId && recordId != before.ActiveRecordId &&
            written == (before with
            {
                Start = change.Entry.Target,
                KeyVersion = written.KeyVersion,
                ActiveRecordId = recordId,
                DependentCount = disabling ? null : before.DependentCount,
                DependencyState = disabling ? null : before.DependencyState,
            });
    }

    internal static ServiceRecommendationPlan? Capture(string[] names, ServiceActionSettings settings,
        Func<string, ServiceActionFacts?> read, IReadOnlyList<string>? alwaysObserve = null)
    {
        if (settings.Locked || settings.Demo || !settings.Elevated) return null;
        var observed = new Dictionary<string, ServiceActionFacts>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<ServiceRecommendationChange>();
        foreach (var name in names)
        {
            var entry = ServiceCatalog.Find(name);
            if (entry is null || !ServiceGuard.CanApplyRisk(entry.Risk, settings.Expert) || !Read(name, out var facts))
                return null;
            // Manual recommendations preserve a service that is already Disabled.
            if (facts.Start == entry.Target || (entry.Target == ServiceStartMode.Manual && facts.Start == ServiceStartMode.Disabled))
                continue;
            if (facts.CanChange != true || (entry.Target == ServiceStartMode.Disabled && facts.DependentCount != 0) ||
                ServiceGuard.MustStayDisabled(name))
                return null;
            changes.Add(new(entry, facts));
        }

        foreach (var change in changes)
        {
            if (change.Entry.Companion is not { } companionName) continue;
            var companion = ServiceCatalog.Find(companionName);
            if (companion is null || !Read(companion.Name, out var related)) return null;
            if (related.Start != companion.Target && !changes.Any(item => item.Entry.Name == companion.Name))
                return null;
        }

        if (alwaysObserve is not null)
            foreach (var name in alwaysObserve)
                if (!Read(name, out _)) return null;

        return new(settings, observed.Values.OrderBy(facts => facts.Identity.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            changes.OrderBy(change => change.Entry.Companion is not null).ToArray());

        bool Read(string name, out ServiceActionFacts facts)
        {
            if (observed.TryGetValue(name, out facts!)) return true;
            facts = read(name)!;
            if (facts is null || !ServiceActionWorkflow.SameName(name, facts.Identity.Name) || facts.Managed != false ||
                facts.CatalogGatePassed != true || ServiceGuard.OptimizeTweakFor(name) is not null ||
                facts.Start is not (ServiceStartMode.Automatic or ServiceStartMode.Manual or ServiceStartMode.Disabled) ||
                facts.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused) ||
                facts.Type is not (0x10 or 0x20 or 0x110 or 0x120) ||
                ServiceGuard.GetLockReason(name, facts.Type, (int)facts.Start, facts.ErrorControl, facts.LaunchProtected, facts.ImagePath) is not null ||
                ServiceGuard.IsDriverStorePath(facts.ServiceDll))
                return false;
            observed.Add(name, facts);
            return true;
        }
    }
}
