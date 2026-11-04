using Lycoris.Tweaks;
using Xunit;

namespace Lycoris.Tests;

/// <summary>«Центр доверия источников» (roadmap item 10): every tweak says why it exists, only official sites, reviewed within a year.</summary>
public class TweakSourcesTests
{
    [Fact]
    public void Every_tweak_has_an_evidence_record()
    {
        var missing = TweakCatalog.All.Where(t => TweakSources.For(t.Id) is null).Select(t => t.Id).ToList();
        Assert.True(missing.Count == 0, "Нет записи об основании: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_record_for_a_tweak_that_does_not_exist()
    {
        var ids = TweakCatalog.All.Select(t => t.Id).ToHashSet();
        var orphans = TweakSources.Ids.Where(id => !ids.Contains(id)).ToList();
        Assert.True(orphans.Count == 0, "Запись без пункта каталога: " + string.Join(", ", orphans));
    }

    [Fact]
    public void Only_https_urls_on_official_hosts()
    {
        var bad = TweakSources.Ids.SelectMany(id => TweakSources.For(id)!.Urls.Where(u => !TweakEvidence.IsAllowedUrl(u)).Select(u => id + ": " + u)).ToList();
        Assert.True(bad.Count == 0, "Неофициальный или небезопасный адрес: " + string.Join("; ", bad));
    }

    /// <summary>
    /// Recommended items with no official page (checked 04.11.2026). Each is a plain Windows UI option whose registry value Microsoft
    /// does not document, or a vendor behaviour whose page could not be opened; the basis text says so. Adding an id here needs a reason.
    /// </summary>
    private static readonly Dictionary<string, string> RecommendedWithoutOfficialPage = new()
    {
        ["audio-ducking"] = "«Ничего не делать» on the Sound → Communications tab; the value is not documented",
        ["ads-explorer-sync-notifications-off"] = "File Explorer options check box; the value is not documented",
        ["power-plan-balanced-x3d"] = "AMD chipset driver behaviour; AMD's release notes did not load during the check",
    };

    [Fact]
    public void Recommended_tweaks_have_an_official_source()
    {
        var bare = TweakCatalog.All.Where(t => t.DefaultIn != ProfileSet.None && TweakSources.For(t.Id) is { HasOfficialSource: false })
            .Select(t => t.Id).Where(id => !RecommendedWithoutOfficialPage.ContainsKey(id)).ToList();
        Assert.True(bare.Count == 0, "Рекомендуемый пункт без официального источника: " + string.Join(", ", bare));
    }

    [Fact]
    public void Sources_were_reviewed_within_a_year()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var stale = TweakSources.Ids.Where(id => TweakSources.For(id)!.IsStale(today)).ToList();
        Assert.True(stale.Count == 0, "Источник проверялся больше года назад — проверьте страницу снова и обновите дату: " + string.Join(", ", stale));
        Assert.DoesNotContain(TweakSources.Ids, id => TweakSources.For(id)!.Reviewed > today);
    }

    [Fact]
    public void Every_record_has_a_basis_and_windows_text()
    {
        foreach (var id in TweakSources.Ids)
        {
            var e = TweakSources.For(id)!;
            Assert.False(string.IsNullOrWhiteSpace(e.Basis), id);
            Assert.False(string.IsNullOrWhiteSpace(e.Windows), id);
        }
    }

    /// <summary>The Windows line is shown as is in both languages: only versions, builds, KB numbers and edition names, no English prose.</summary>
    [Fact]
    public void Windows_line_is_language_neutral()
    {
        var prose = new[] { " and ", "later", "only", " with ", "page", " also", "policy", " is ", " on ", ";" };
        var bad = TweakSources.Ids.Select(id => (id, w: TweakSources.For(id)!.Windows))
            .Where(x => !x.w.StartsWith("Windows ", StringComparison.Ordinal) || prose.Any(p => x.w.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.id + ": " + x.w).ToList();
        Assert.True(bad.Count == 0, "Строка версий Windows с текстом на английском: " + string.Join("; ", bad));
    }

    [Theory]
    [InlineData("https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-system", true)]
    [InlineData("https://www.nvidia.com/en-us/geforce/news/", true)]
    [InlineData("http://learn.microsoft.com/x", false)]
    [InlineData("https://learn.microsoft.com.evil.example/x", false)]
    [InlineData("https://user@learn.microsoft.com/x", false)]
    [InlineData("https://learn.microsoft.com:8443/x", false)]
    [InlineData("https://notnvidia.com/x", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    public void Url_allow_list(string url, bool allowed) => Assert.Equal(allowed, TweakEvidence.IsAllowedUrl(url));

    [Fact]
    public void English_basis_is_translated()
    {
        using var _ = Lycoris.Core.Localization.Loc.UseLanguage("en");
        var russian = TweakSources.Ids.Where(id => TweakSources.For(id)!.Basis.Any(c => c is >= 'А' and <= 'я')).ToList();
        Assert.True(russian.Count == 0, "Нет английского текста основания: " + string.Join(", ", russian));
    }
}
