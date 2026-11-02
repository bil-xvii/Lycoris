namespace Monolith.Modules.Devices.Displays;

/// <summary>One timing the monitor lists (detailed timing descriptor or DisplayID timing), landscape as stored.</summary>
public sealed record EdidTiming(int Width, int Height, double RefreshHz, long PixelClockHz);

/// <summary>What Lycoris needs from an EDID: ids, the preferred timing, VRR ranges, HDR metadata and every listed timing.</summary>
public sealed record EdidInfo
{
    /// <summary>Three-letter PNP manufacturer id («ACR»).</summary>
    public string ManufacturerId { get; init; } = "";
    public ushort ProductCode { get; init; }
    /// <summary>Monitor name descriptor (0xFC), when present.</summary>
    public string? Name { get; init; }
    /// <summary>Bits per colour channel declared by an EDID 1.4 digital input (null = undefined).</summary>
    public int? Bpc { get; init; }
    /// <summary>The first detailed timing = the preferred timing (often 60 Hz even on a 240 Hz monitor).</summary>
    public EdidTiming? FirstDtd { get; init; }
    /// <summary>Display range limits (0xFD), vertical rate in Hz, with the EDID 1.4 +255 offsets applied.</summary>
    public int? RangeMinHz { get; init; }
    public int? RangeMaxHz { get; init; }
    /// <summary>AMD FreeSync vendor-specific data block (OUI 00-00-1A) range.</summary>
    public int? FreeSyncMinHz { get; init; }
    public int? FreeSyncMaxHz { get; init; }
    /// <summary>CTA-861 HDR static metadata block present.</summary>
    public bool HasHdrMetadata { get; init; }
    public bool SupportsPq { get; init; }
    public bool SupportsHlg { get; init; }
    /// <summary>Desired content max luminance, 50·2^(cv/32) nits.</summary>
    public double? MaxLuminanceNits { get; init; }
    public double? MinLuminanceNits { get; init; }
    /// <summary>Every detailed timing (base block, CTA-861, DisplayID type I / VII).</summary>
    public IReadOnlyList<EdidTiming> Timings { get; init; } = [];
    /// <summary>An extension block had a wrong checksum and was ignored.</summary>
    public bool BadExtensionChecksum { get; init; }

    /// <summary>The variable refresh range: FreeSync when present, else the range limits (min &lt; max).</summary>
    public (int Min, int Max)? VrrRange =>
        FreeSyncMinHz is { } fmin && FreeSyncMaxHz is { } fmax && fmin > 0 && fmax > fmin ? (fmin, fmax)
        : RangeMinHz is { } rmin && RangeMaxHz is { } rmax && rmin > 0 && rmax > rmin ? (rmin, rmax)
        : null;

    /// <summary>The highest refresh of a listed timing at this size (either orientation); null when none is listed.</summary>
    public double? MaxRefreshAt(int width, int height)
    {
        double? best = null;
        foreach (var t in Timings)
        {
            if (!(t.Width == width && t.Height == height) && !(t.Width == height && t.Height == width)) continue;
            if (best is null || t.RefreshHz > best) best = t.RefreshHz;
        }
        return best;
    }

    /// <summary>The fastest pixel clock among the timings at this size (for the link bandwidth estimate).</summary>
    public long? PixelClockAt(int width, int height, double refreshHz) =>
        Timings.Where(t => ((t.Width == width && t.Height == height) || (t.Width == height && t.Height == width))
                           && Math.Abs(t.RefreshHz - refreshHz) < 1.0)
            .Select(t => (long?)t.PixelClockHz).Max();
}

/// <summary>
/// EDID 1.3/1.4 with CTA-861 and DisplayID extensions (docs/research/devices_displays.md §5.2). Pure; never throws: garbage
/// returns null, a bad extension is skipped and flagged.
/// </summary>
public static class EdidParser
{
    private static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static EdidInfo? Parse(byte[]? edid)
    {
        try
        {
            return ParseCore(edid);
        }
        catch
        {
            return null;
        }
    }

    private static EdidInfo? ParseCore(byte[]? b)
    {
        if (b is null || b.Length < 128) return null;
        for (var i = 0; i < Header.Length; i++)
            if (b[i] != Header[i]) return null;
        if (!ChecksumOk(b, 0)) return null;

        var mfg = (b[8] << 8) | b[9];
        var letters = new[] { (mfg >> 10) & 0x1F, (mfg >> 5) & 0x1F, mfg & 0x1F };
        if (letters.Any(l => l is < 1 or > 26)) return null;
        var manufacturer = new string(letters.Select(l => (char)('A' + l - 1)).ToArray());
        var product = (ushort)(b[10] | (b[11] << 8));

        int? bpc = null;
        if ((b[20] & 0x80) != 0 && b[18] == 1 && b[19] >= 4)
            bpc = ((b[20] >> 4) & 0x7) switch { 1 => 6, 2 => 8, 3 => 10, 4 => 12, 5 => 14, 6 => 16, _ => null };

        var timings = new List<EdidTiming>();
        EdidTiming? first = null;
        int? rangeMin = null, rangeMax = null;
        string? name = null;
        for (var d = 54; d <= 108; d += 18)
        {
            if (b[d] != 0 || b[d + 1] != 0)
            {
                if (Dtd(b, d) is { } t)
                {
                    first ??= t;
                    timings.Add(t);
                }
                continue;
            }
            switch (b[d + 3])
            {
                case 0xFD:
                {
                    var flags = b[d + 4];
                    var vMin = b[d + 5] + ((flags & 0x03) == 0x03 ? 255 : 0);
                    var vMax = b[d + 6] + ((flags & 0x02) != 0 ? 255 : 0);
                    if (vMin > 0 && vMax >= vMin)
                    {
                        rangeMin = vMin;
                        rangeMax = vMax;
                    }
                    break;
                }
                case 0xFC:
                    name = Ascii(b, d + 5, 13);
                    break;
            }
        }

        var info = new EdidInfo
        {
            ManufacturerId = manufacturer, ProductCode = product, Bpc = bpc, FirstDtd = first, RangeMinHz = rangeMin, RangeMaxHz = rangeMax,
            Name = string.IsNullOrWhiteSpace(name) ? null : name,
        };

        var badExtension = false;
        var extensions = b[126];
        for (var e = 1; e <= extensions && (e + 1) * 128 <= b.Length; e++)
        {
            var o = e * 128;
            if (!ChecksumOk(b, o))
            {
                badExtension = true;
                continue;
            }
            switch (b[o])
            {
                case 0x02:
                    info = ParseCta(b, o, info, timings);
                    break;
                case 0x70:
                    ParseDisplayId(b, o, timings);
                    break;
            }
        }
        return info with { Timings = timings, BadExtensionChecksum = badExtension };
    }

    private static bool ChecksumOk(byte[] b, int offset)
    {
        var sum = 0;
        for (var i = 0; i < 128; i++) sum += b[offset + i];
        return (sum & 0xFF) == 0;
    }

    private static string? Ascii(byte[] b, int offset, int max)
    {
        var chars = new List<char>();
        for (var i = 0; i < max && offset + i < b.Length; i++)
        {
            var c = b[offset + i];
            if (c == 0x0A || c == 0) break;
            if (c is < 0x20 or > 0x7E) return null;
            chars.Add((char)c);
        }
        return new string(chars.ToArray()).Trim();
    }

    /// <summary>An 18-byte detailed timing descriptor (pixel clock in 10 kHz). Interlaced timings are skipped.</summary>
    private static EdidTiming? Dtd(byte[] b, int o)
    {
        if (o + 18 > b.Length) return null;
        long clock = (b[o] | (b[o + 1] << 8)) * 10_000L;
        var hActive = b[o + 2] | ((b[o + 4] & 0xF0) << 4);
        var hBlank = b[o + 3] | ((b[o + 4] & 0x0F) << 8);
        var vActive = b[o + 5] | ((b[o + 7] & 0xF0) << 4);
        var vBlank = b[o + 6] | ((b[o + 7] & 0x0F) << 8);
        if ((b[o + 17] & 0x80) != 0) return null; // interlaced
        return Timing(hActive, vActive, hActive + hBlank, vActive + vBlank, clock);
    }

    private static EdidTiming? Timing(int w, int h, long hTotal, long vTotal, long clockHz)
    {
        if (w < 320 || h < 200 || w > 16384 || h > 16384 || clockHz <= 0 || hTotal <= w || vTotal <= h) return null;
        var hz = (double)clockHz / (hTotal * vTotal);
        return hz is >= 10 and <= 2000 ? new EdidTiming(w, h, hz, clockHz) : null;
    }

    // ───────────────────────────── CTA-861 ─────────────────────────────

    private static EdidInfo ParseCta(byte[] b, int o, EdidInfo info, List<EdidTiming> timings)
    {
        var dtdStart = b[o + 2];
        if (dtdStart is >= 4 and <= 127)
        {
            var p = o + 4;
            var end = o + dtdStart;
            while (p < end)
            {
                var tag = (b[p] >> 5) & 0x7;
                var len = b[p] & 0x1F;
                if (p + 1 + len > end) break;
                var payload = p + 1;
                if (tag == 7 && len >= 1)
                {
                    if (b[payload] == 6 && len >= 3) info = HdrStatic(b, payload, len, info);
                }
                else if (tag == 3 && len >= 3)
                {
                    // Vendor-specific: IEEE OUI little-endian. AMD FreeSync: 00-00-1A; after the OUI: version, minor, min Hz, max Hz.
                    if (b[payload] == 0x1A && b[payload + 1] == 0x00 && b[payload + 2] == 0x00 && len >= 7)
                    {
                        int min = b[payload + 5], max = b[payload + 6];
                        if (min > 0 && max > min) info = info with { FreeSyncMinHz = min, FreeSyncMaxHz = max };
                    }
                }
                p += 1 + len;
            }
            // Detailed timings after the data block collection.
            for (var d = o + dtdStart; d + 18 <= o + 127; d += 18)
            {
                if (b[d] == 0 && b[d + 1] == 0) break;
                if (Dtd(b, d) is { } t) timings.Add(t);
            }
        }
        return info;
    }

    /// <summary>HDR static metadata (extended tag 6): EOTFs, then max / max-frame-average / min luminance codes.</summary>
    private static EdidInfo HdrStatic(byte[] b, int payload, int len, EdidInfo info)
    {
        var eotf = b[payload + 1];
        double? max = null, min = null;
        if (len >= 4 && b[payload + 3] > 0) max = 50 * Math.Pow(2, b[payload + 3] / 32.0);
        if (len >= 6 && max is { } m) min = m * Math.Pow(b[payload + 5] / 255.0, 2) / 100;
        return info with
        {
            HasHdrMetadata = true, SupportsPq = (eotf & 0x04) != 0, SupportsHlg = (eotf & 0x08) != 0, MaxLuminanceNits = max, MinLuminanceNits = min,
        };
    }

    // ───────────────────────────── DisplayID ─────────────────────────────

    /// <summary>DisplayID section inside an extension block: type I (0x03, 10 kHz) and type VII (0x22, 1 kHz) detailed timings.</summary>
    private static void ParseDisplayId(byte[] b, int o, List<EdidTiming> timings)
    {
        var sectionLength = b[o + 2];
        var p = o + 5;
        var end = Math.Min(o + 5 + sectionLength, o + 127);
        while (p + 3 <= end)
        {
            var tag = b[p];
            var len = b[p + 2];
            if (tag == 0 && len == 0) break; // padding
            var payload = p + 3;
            if (payload + len > end) break;
            if (tag is 0x03 or 0x22)
            {
                var unit = tag == 0x03 ? 10_000L : 1_000L;
                for (var t = payload; t + 20 <= payload + len; t += 20)
                {
                    long clock = ((b[t] | (b[t + 1] << 8) | (b[t + 2] << 16)) + 1L) * unit;
                    if ((b[t + 3] & 0x10) != 0) continue; // interlaced
                    var hActive = (b[t + 4] | (b[t + 5] << 8)) + 1;
                    var hBlank = (b[t + 6] | (b[t + 7] << 8)) + 1;
                    var vActive = (b[t + 12] | (b[t + 13] << 8)) + 1;
                    var vBlank = (b[t + 14] | (b[t + 15] << 8)) + 1;
                    if (Timing(hActive, vActive, hActive + hBlank, vActive + vBlank, clock) is { } timing) timings.Add(timing);
                }
            }
            p = payload + len;
        }
    }
}
