using System.IO;
using System.Text.RegularExpressions;
using Monolith.Tweaks;

namespace Monolith.Tests;

/// <summary>
/// «Что MONOLITH не делает и почему» (Tweaks/RejectedTweaks.cs): the list is complete, honest Russian text without product names,
/// every entry is backed by docs/research, and none of its registry values is ever written by the catalog.
/// </summary>
public sealed class RejectedTweaksTests
{
    /// <summary>Ids required by the wave-2 spec (capability_plan.md «myths-explainer»).</summary>
    private static readonly string[] RequiredIds =
    [
        "reserved-storage-off", "memory-compression-off", "native-nvme-flags", "cstates-idle-disable", "global-timer-resolution",
        "core-parking-off", "power-throttling-off", "mpo-disableoverlays", "hibernate-timer-modern-standby", "hibernation-off-default",
        "s3-override", "svchost-split", "standby-purge-timer", "bg-apps-global-win11", "mitigations-off", "auto-scenario-switching",
        "cpu-sleep-unpark", "stripped-windows-modes", "energy-saver-always-quiet", "battery-boosters", "streaming-network-priority",
        "turbo-99-percent", "vendor-fan-charge-writes", "dark-mode-auto", "global-notifications-off", "perf-plans-in-work-scenarios",
        "do-background-bandwidth", "search-exclude-game-libraries", "hpet-bcdedit", "registry-cleaning", "pagefile-off",
        "realtime-priority", "nic-offloads",
    ];

    /// <summary>Registry values the spec adds to the rejected list, on top of what the entries carry themselves.</summary>
    private static readonly string[] SpecValueNames =
    [
        "PlatformAoAcOverride", "GlobalTimerResolutionRequests", "SvcHostSplitThresholdInKB", "FeatureSettingsOverride",
        "FeatureSettingsOverrideMask", "DisableOverlays", "735209102", "1853569164", "156965516", "DOPercentageMaxBackgroundBandwidth",
        "PowerThrottlingOff", "DisableUserPresenceQos",
    ];

    /// <summary>Competitor names belong to docs/research only; the UI says «некоторые оптимизаторы».</summary>
    private static readonly string[] ProductNames = ["WinUtil", "Hone", "Razer", "Cortex", "Atlas", "Process Lasso", "ISLC", "CCleaner", "Armoury"];

    private static readonly Regex Kebab = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex Cyrillic = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    private static readonly Regex SentenceEnd = new(@"[.!?](\s|$)", RegexOptions.Compiled);

    [Fact]
    public void All_required_entries_exist_and_ids_are_unique_kebab_case()
    {
        var ids = RejectedTweaks.All.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches(Kebab, id));
        foreach (var required in RequiredIds) Assert.Contains(required, ids);
        Assert.All(RejectedTweaks.All, r => Assert.Same(r, RejectedTweaks.Find(r.Id)));
    }

    [Fact]
    public void Texts_are_russian_sentence_case_and_short()
    {
        foreach (var r in RejectedTweaks.All)
        {
            foreach (var text in new[] { r.Title, r.WhatOthersDo, r.WhyNot, r.Alternative })
            {
                Assert.False(string.IsNullOrWhiteSpace(text), r.Id);
                Assert.True(Cyrillic.IsMatch(text), $"{r.Id}: not Russian: {text}");
                Assert.Equal(text.Trim(), text);
            }
            // Sentence case: a capital (or a brand/acronym) first, never an all-caps title.
            var firstLetter = r.Title.First(char.IsLetterOrDigit);
            Assert.True(char.IsUpper(firstLetter) || char.IsDigit(firstLetter), $"{r.Id}: title must start with a capital: {r.Title}");
            Assert.NotEqual(r.Title.ToUpperInvariant(), r.Title);
            // WhyNot: 1–3 sentences with the mechanism.
            var sentences = SentenceEnd.Matches(r.WhyNot).Count;
            Assert.InRange(sentences, 1, 3);
            Assert.EndsWith(".", r.WhyNot);
            Assert.False(string.IsNullOrWhiteSpace(r.Keywords), r.Id);
        }
    }

    [Fact]
    public void Ui_texts_name_no_competitor_products()
    {
        foreach (var r in RejectedTweaks.All)
            foreach (var text in new[] { r.Title, r.WhatOthersDo, r.WhyNot, r.Alternative })
                foreach (var name in ProductNames)
                    Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Registry_values_of_rejected_tweaks_are_never_written_by_the_catalog()
    {
        var rejected = RejectedTweaks.All.SelectMany(r => r.RegistryValueNames).Concat(SpecValueNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(rejected);
        Assert.All(rejected, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        foreach (var expected in SpecValueNames) Assert.Contains(expected, rejected);

        var written = TweakCatalog.All.OfType<RegistryTweak>().SelectMany(t => t.Values.Select(v => (t.Id, v.Name))).ToList();
        Assert.DoesNotContain(written, w => rejected.Contains(w.Name));
    }

    [Fact]
    public void Every_entry_cites_an_existing_research_file_and_its_own_section()
    {
        var research = FindResearchDir();
        var rejectedDoc = File.ReadAllText(Path.Combine(research, "rejected_tweaks.md"));
        foreach (var r in RejectedTweaks.All)
        {
            Assert.StartsWith($"rejected_tweaks.md §{r.Id};", r.Research);
            Assert.Contains($"## `{r.Id}`", rejectedDoc);
            var files = Regex.Matches(r.Research, @"[a-z_]+\.md").Select(m => m.Value).Distinct().ToList();
            Assert.True(files.Count >= 2, $"{r.Id}: must also cite the binding research section: {r.Research}");
            foreach (var file in files) Assert.True(File.Exists(Path.Combine(research, file)), $"{r.Id}: missing docs/research/{file}");
            // The research section lists the same registry values the entry carries.
            var section = Section(rejectedDoc, r.Id);
            foreach (var value in r.RegistryValueNames) Assert.Contains(value, section);
        }
    }

    [Fact]
    public void Research_doc_has_no_sections_the_list_does_not_know()
    {
        var doc = File.ReadAllText(Path.Combine(FindResearchDir(), "rejected_tweaks.md"));
        var sections = Regex.Matches(doc, @"^## `([a-z0-9-]+)`", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(RejectedTweaks.All.Select(r => r.Id).OrderBy(x => x, StringComparer.Ordinal), sections.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string Section(string doc, string id)
    {
        var start = doc.IndexOf($"## `{id}`", StringComparison.Ordinal);
        Assert.True(start >= 0, id);
        var end = doc.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        return end < 0 ? doc[start..] : doc[start..end];
    }

    /// <summary>docs/research of the repository: walks up from the test binaries (bin/Debug/…/tests/Monolith.Tests).</summary>
    private static string FindResearchDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "research");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("docs/research not found above " + AppContext.BaseDirectory);
    }
}
