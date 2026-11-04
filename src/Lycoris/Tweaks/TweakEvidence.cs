namespace Lycoris.Tweaks;

/// <summary>
/// «Центр доверия источников» (roadmap item 10): why a tweak is in the catalog, backed by an official page.
/// Basis = what the source says, Windows = versions it covers, Limits = a limitation the source names,
/// Reviewed = when a person last checked the page against the tweak. The data lives in <see cref="TweakSources"/>.
/// </summary>
public sealed record TweakEvidence(string Basis, string Windows, string Limits, DateOnly Reviewed, IReadOnlyList<string> Urls)
{
    /// <summary>A source older than this must be checked again (the tests fail, the row says so).</summary>
    public const int ReviewEveryDays = 365;

    /// <summary>Only official sites: Microsoft and the hardware vendors whose drivers the tweaks touch.</summary>
    public static readonly IReadOnlyList<string> AllowedHosts =
    [
        "learn.microsoft.com", "support.microsoft.com", "techcommunity.microsoft.com", "devblogs.microsoft.com",
        "blogs.windows.com", "nvidia.com", "amd.com", "intel.com",
        // DNS providers offered on the Network tab: their own setup pages.
        "developers.cloudflare.com", "developers.google.com",
    ];

    public static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) && u.IsDefaultPort &&
        AllowedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public bool HasOfficialSource => Urls.Count > 0;

    public bool IsStale(DateOnly today) => today.DayNumber - Reviewed.DayNumber > ReviewEveryDays;

    /// <summary>Host shown on the button, e.g. «LEARN.MICROSOFT.COM».</summary>
    public static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToUpperInvariant() : url;
}
