using Monolith.Modules.Services;
using Monolith.Modules.Tools;
using Monolith.Tweaks;

namespace Monolith.Shell;

/// <summary>Builds the command-palette index from the existing catalogs (in-memory C# data, read only).</summary>
public static class CommandPaletteIndex
{
    public static IReadOnlyList<PaletteEntry> Build(
        IEnumerable<(string Key, string Title, string Subtitle)> pages,
        IEnumerable<Tweak> tweaks,
        IEnumerable<ToolSection> toolSections,
        IEnumerable<QuickLink> quickLinks,
        IEnumerable<CuratedService> services)
    {
        var list = new List<PaletteEntry>();

        foreach (var (key, title, subtitle) in pages)
            list.Add(new PaletteEntry { Kind = PaletteKind.Page, Title = title, Description = subtitle, Keywords = key, PageKey = key });

        foreach (var t in tweaks)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Tweak, Title = t.Title, Description = t.Description,
                Keywords = $"{t.Id} {t.CategoryText}", Risk = t.Risk, PageKey = "optimize", FocusId = t.Id,
            });

        foreach (var section in toolSections)
            foreach (var card in section.Cards)
                list.Add(new PaletteEntry
                {
                    Kind = PaletteKind.Tool, Title = card.Title, Description = card.Description,
                    Keywords = $"{card.Command} {section.Title}", PageKey = "tools",
                });

        foreach (var link in quickLinks)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Tool, Title = link.Title, Description = $"Быстрый запуск на странице «Инструменты»: {link.Command}.",
                Keywords = link.Command, PageKey = "tools",
            });

        foreach (var s in services)
            list.Add(new PaletteEntry
            {
                Kind = PaletteKind.Service, Title = s.Title, Description = s.Description,
                Keywords = $"{s.Name} {ServiceCatalog.GroupTitle(s.Group)}", Risk = s.Risk, PageKey = "services", FocusId = s.Name,
            });

        return list;
    }

    /// <summary>The index for the running app: pages from the shell, tools from the live Tools page.</summary>
    public static IReadOnlyList<PaletteEntry> Build(IEnumerable<PageViewModel> pages)
    {
        var pageList = pages.ToList();
        var tools = pageList.OfType<ToolsViewModel>().FirstOrDefault();
        return Build(
            pageList.Select(p => (p.Key, p.Title, p.Subtitle)),
            TweakCatalog.All,
            tools?.Sections ?? [],
            tools?.QuickLinks ?? [],
            ServiceCatalog.Services);
    }
}
