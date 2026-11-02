using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Lycoris.Core.Updates;

/// <summary>
/// A semantic version "1.2.3" (optionally "v" prefix, a 4th number, "-beta.1" pre-release label, "+build" suffix).
/// A pre-release is lower than the same release; labels compare dot-wise (numbers numerically), as in semver.org.
/// </summary>
public readonly partial record struct SemVer(int Major, int Minor, int Patch, int Build, string Pre) : IComparable<SemVer>
{
    [GeneratedRegex(@"^[vV]?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:\.(\d{1,5}))?(?:-([0-9A-Za-z][0-9A-Za-z.-]*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    public bool IsPrerelease => Pre.Length > 0;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;
        int N(int g) => m.Groups[g].Success ? int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture) : 0;
        version = new SemVer(N(1), N(2), N(3), N(4), m.Groups[5].Success ? m.Groups[5].Value : "");
        return true;
    }

    public static SemVer? Parse(string? text) => TryParse(text, out var v) ? v : null;

    public int CompareTo(SemVer o)
    {
        var c = Major.CompareTo(o.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(o.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(o.Patch);
        if (c != 0) return c;
        c = Build.CompareTo(o.Build);
        if (c != 0) return c;
        if (Pre.Length == 0 && o.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (o.Pre.Length == 0) return -1;
        var a = Pre.Split('.');
        var b = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    /// <summary>"1.2.3" (a 4th number only when it is not zero) plus "-label".</summary>
    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Build > 0 ? "." + Build : "") + (Pre.Length > 0 ? "-" + Pre : "");
}

/// <summary>The running build's version: one source of truth, <c>&lt;Version&gt;</c> in Lycoris.csproj.</summary>
public static class AppVersion
{
    /// <summary>TESTS ONLY: pretends to be another build.</summary>
    internal static SemVer? OverrideForTests { get; set; }

    public static SemVer Current => OverrideForTests ?? OfAssembly(Assembly.GetExecutingAssembly());

    public static SemVer OfAssembly(Assembly a)
    {
        var info = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (SemVer.TryParse(info, out var v)) return v;
        var asm = a.GetName().Version;
        return asm is null ? new SemVer(0, 0, 0, 0, "") : new SemVer(asm.Major, asm.Minor, Math.Max(asm.Build, 0), 0, "");
    }

    public static string CurrentText => Current.ToString();
}
