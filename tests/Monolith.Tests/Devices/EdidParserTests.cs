using Monolith.Modules.Devices.Displays;

namespace Monolith.Tests.Devices;

/// <summary>EDID parsing on synthetic EDIDs built byte by byte (the layout of the dev machine's Acer X27U W3).</summary>
public sealed class EdidParserTests
{
    /// <summary>Builds an EDID: base block + optional CTA-861 and DisplayID extensions, with correct checksums.</summary>
    internal sealed class EdidBuilder
    {
        private readonly byte[] _base = new byte[128];
        private readonly List<byte[]> _extensions = [];
        private int _descriptor;

        public EdidBuilder(string pnp = "ACR", ushort product = 0x1132)
        {
            byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
            header.CopyTo(_base, 0);
            var code = ((pnp[0] - 'A' + 1) << 10) | ((pnp[1] - 'A' + 1) << 5) | (pnp[2] - 'A' + 1);
            _base[8] = (byte)(code >> 8);
            _base[9] = (byte)code;
            _base[10] = (byte)product;
            _base[11] = (byte)(product >> 8);
            _base[18] = 1;
            _base[19] = 4;
            _base[20] = 0xB5; // digital, 10 bpc, DisplayPort
        }

        private int NextDescriptor() => 54 + 18 * _descriptor++;

        /// <summary>A detailed timing: active size, blanking and pixel clock (10 kHz units).</summary>
        public EdidBuilder Dtd(int w, int h, int hBlank, int vBlank, int clock10kHz)
        {
            var o = NextDescriptor();
            WriteDtd(_base, o, w, h, hBlank, vBlank, clock10kHz);
            return this;
        }

        public static void WriteDtd(byte[] b, int o, int w, int h, int hBlank, int vBlank, int clock10kHz)
        {
            b[o] = (byte)clock10kHz;
            b[o + 1] = (byte)(clock10kHz >> 8);
            b[o + 2] = (byte)w;
            b[o + 3] = (byte)hBlank;
            b[o + 4] = (byte)(((w >> 8) << 4) | (hBlank >> 8));
            b[o + 5] = (byte)h;
            b[o + 6] = (byte)vBlank;
            b[o + 7] = (byte)(((h >> 8) << 4) | (vBlank >> 8));
            b[o + 17] = 0x1E;
        }

        /// <summary>Display range limits (0xFD) with the raw bytes and offset flags.</summary>
        public EdidBuilder Range(byte minV, byte maxV, byte flags = 0)
        {
            var o = NextDescriptor();
            _base[o + 3] = 0xFD;
            _base[o + 4] = flags;
            _base[o + 5] = minV;
            _base[o + 6] = maxV;
            _base[o + 7] = 30;
            _base[o + 8] = 255;
            _base[o + 9] = 120;
            return this;
        }

        public EdidBuilder Name(string name)
        {
            var o = NextDescriptor();
            _base[o + 3] = 0xFC;
            for (var i = 0; i < 13; i++) _base[o + 5 + i] = i < name.Length ? (byte)name[i] : i == name.Length ? (byte)0x0A : (byte)0x20;
            return this;
        }

        /// <summary>CTA-861: HDR static metadata (PQ, max luminance code) and the AMD FreeSync VSDB.</summary>
        public EdidBuilder Cta(byte? maxLuminanceCode = null, (byte Min, byte Max)? freeSync = null)
        {
            var b = new byte[128];
            b[0] = 0x02;
            b[1] = 3;
            var p = 4;
            if (maxLuminanceCode is { } cv)
            {
                b[p++] = (7 << 5) | 6; // extended tag, length 6
                b[p++] = 6;            // HDR static metadata
                b[p++] = 0x05;         // SDR + PQ
                b[p++] = 0x01;
                b[p++] = cv;
                b[p++] = 0;
                b[p++] = 0;
            }
            if (freeSync is { } fs)
            {
                b[p++] = (3 << 5) | 8; // vendor-specific, length 8
                b[p++] = 0x1A;
                b[p++] = 0x00;
                b[p++] = 0x00;         // OUI 00-00-1A
                b[p++] = 2;
                b[p++] = 0;
                b[p++] = fs.Min;
                b[p++] = fs.Max;
                b[p++] = 0;
            }
            b[2] = (byte)p; // detailed timings would start here
            _extensions.Add(b);
            return this;
        }

        /// <summary>DisplayID 1.x section with one type I timing (pixel clock in 10 kHz units, stored minus one).</summary>
        public EdidBuilder DisplayIdTypeI(int w, int h, int hBlank, int vBlank, int clock10kHz, bool interlaced = false)
        {
            var b = new byte[128];
            b[0] = 0x70;
            b[1] = 0x12;
            b[2] = 23; // one data block: 3 header bytes + 20
            b[5] = 0x03;
            b[6] = 0;
            b[7] = 20;
            var t = 8;
            var clock = clock10kHz - 1;
            b[t] = (byte)clock;
            b[t + 1] = (byte)(clock >> 8);
            b[t + 2] = (byte)(clock >> 16);
            b[t + 3] = (byte)(interlaced ? 0x10 : 0x00);
            Le16(b, t + 4, w - 1);
            Le16(b, t + 6, hBlank - 1);
            Le16(b, t + 8, 47);
            Le16(b, t + 10, 31);
            Le16(b, t + 12, h - 1);
            Le16(b, t + 14, vBlank - 1);
            Le16(b, t + 16, 2);
            Le16(b, t + 18, 4);
            _extensions.Add(b);
            return this;
        }

        private static void Le16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }

        public byte[] Build(bool breakFirstExtension = false)
        {
            _base[126] = (byte)_extensions.Count;
            Checksum(_base);
            var all = new List<byte>(_base);
            for (var i = 0; i < _extensions.Count; i++)
            {
                var e = (byte[])_extensions[i].Clone();
                Checksum(e);
                if (breakFirstExtension && i == 0) e[127] ^= 0x55;
                all.AddRange(e);
            }
            return all.ToArray();
        }

        private static void Checksum(byte[] block)
        {
            var sum = 0;
            for (var i = 0; i < 127; i++) sum += block[i];
            block[127] = (byte)((256 - sum % 256) % 256);
        }
    }

    /// <summary>The X27U W3: 1st DTD 2560×1440 @ 59,951 Гц, range 48–240, CTA HDR (≈ 456 нит) + FreeSync 48–240, 239,97 Гц in DisplayID.</summary>
    internal static byte[] X27u() => new EdidBuilder()
        .Dtd(2560, 1440, 160, 41, 24150)
        .Range(48, 240)
        .Name("X27U W3")
        .Cta(maxLuminanceCode: 102, freeSync: (48, 240))
        .DisplayIdTypeI(2560, 1440, 160, 179, 105675)
        .Build();

    [Fact]
    public void Header_ids_and_preferred_timing()
    {
        var e = EdidParser.Parse(X27u())!;

        Assert.Equal("ACR", e.ManufacturerId);
        Assert.Equal(0x1132, e.ProductCode);
        Assert.Equal("X27U W3", e.Name);
        Assert.Equal(10, e.Bpc);
        Assert.NotNull(e.FirstDtd);
        Assert.Equal((2560, 1440), (e.FirstDtd!.Width, e.FirstDtd.Height));
        Assert.Equal(59.951, e.FirstDtd.RefreshHz, 3);
        Assert.Equal(241_500_000, e.FirstDtd.PixelClockHz);
        Assert.False(e.BadExtensionChecksum);
    }

    [Fact]
    public void Range_limits_with_and_without_the_offset_flags()
    {
        var plain = EdidParser.Parse(new EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Range(48, 240).Build())!;
        Assert.Equal((48, 240), (plain.RangeMinHz, plain.RangeMaxHz));
        Assert.Equal((48, 240), plain.VrrRange);

        // EDID 1.4: bit 1 = max + 255, bits 1–0 = 11 → min and max + 255.
        var maxOffset = EdidParser.Parse(new EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Range(48, 105, flags: 0x02).Build())!;
        Assert.Equal((48, 360), (maxOffset.RangeMinHz, maxOffset.RangeMaxHz));
        var both = EdidParser.Parse(new EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Range(10, 50, flags: 0x03).Build())!;
        Assert.Equal((265, 305), (both.RangeMinHz, both.RangeMaxHz));
    }

    [Fact]
    public void Cta_hdr_metadata_and_freesync()
    {
        var e = EdidParser.Parse(X27u())!;

        Assert.True(e.HasHdrMetadata);
        Assert.True(e.SupportsPq);
        Assert.False(e.SupportsHlg);
        Assert.InRange(e.MaxLuminanceNits!.Value, 450, 460); // 50·2^(102/32) ≈ 455,5
        Assert.Equal((48, 240), (e.FreeSyncMinHz, e.FreeSyncMaxHz));
        Assert.Equal((48, 240), e.VrrRange);
    }

    [Fact]
    public void DisplayId_type_I_timing_and_max_refresh()
    {
        var e = EdidParser.Parse(X27u())!;

        var fast = e.Timings.Single(t => t.RefreshHz > 200);
        Assert.Equal((2560, 1440), (fast.Width, fast.Height));
        Assert.Equal(239.97, fast.RefreshHz, 2);
        Assert.Equal(1_056_750_000, fast.PixelClockHz);
        Assert.Equal(239.97, e.MaxRefreshAt(2560, 1440)!.Value, 2);
        Assert.Equal(239.97, e.MaxRefreshAt(1440, 2560)!.Value, 2); // a portrait monitor asks in its own orientation
        Assert.Null(e.MaxRefreshAt(1920, 1080));
        Assert.Equal(1_056_750_000, e.PixelClockAt(2560, 1440, 239.97));
    }

    [Fact]
    public void Interlaced_DisplayId_timings_are_ignored()
    {
        var e = EdidParser.Parse(new EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).DisplayIdTypeI(2560, 1440, 160, 179, 105675, interlaced: true).Build())!;
        Assert.Equal(59.951, e.MaxRefreshAt(2560, 1440)!.Value, 3);
    }

    [Fact]
    public void A_bad_extension_is_skipped_and_flagged()
    {
        var e = EdidParser.Parse(new EdidBuilder().Dtd(2560, 1440, 160, 41, 24150).Cta(102, (48, 240)).DisplayIdTypeI(2560, 1440, 160, 179, 105675)
            .Build(breakFirstExtension: true))!;

        Assert.True(e.BadExtensionChecksum);
        Assert.False(e.HasHdrMetadata);       // the CTA block was ignored
        Assert.Equal(239.97, e.MaxRefreshAt(2560, 1440)!.Value, 2); // the DisplayID block still counts
    }

    [Fact]
    public void Garbage_and_short_arrays_return_null_without_throwing()
    {
        Assert.Null(EdidParser.Parse(null));
        Assert.Null(EdidParser.Parse([]));
        Assert.Null(EdidParser.Parse(new byte[127]));
        var random = new byte[384];
        new Random(42).NextBytes(random);
        Assert.Null(EdidParser.Parse(random));

        var badChecksum = X27u();
        badChecksum[127] ^= 0xFF;
        Assert.Null(EdidParser.Parse(badChecksum));

        // A truncated EDID (the extension count promises more blocks than there are): base data only, no exception.
        var truncated = X27u()[..200];
        var e = EdidParser.Parse(truncated);
        Assert.NotNull(e);
        Assert.Equal("ACR", e!.ManufacturerId);

        // Fuzz: random corruption of a valid EDID never throws.
        var rng = new Random(7);
        for (var i = 0; i < 500; i++)
        {
            var copy = X27u();
            for (var k = 0; k < 6; k++) copy[rng.Next(copy.Length)] = (byte)rng.Next(256);
            var ex = Record.Exception(() => EdidParser.Parse(copy));
            Assert.Null(ex);
        }
    }
}
