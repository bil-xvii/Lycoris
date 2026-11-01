using System.Runtime.InteropServices;
using Monolith.Modules.Tools;
using Monolith.Modules.Tools.DpcLatency;

namespace Monolith.Tests;

/// <summary>Pure logic of the DPC/ISR measurement: aggregation, attribution, verdicts, the session-name rule. No ETW here.</summary>
public sealed class DpcLatencyTests
{
    private static readonly KernelModule[] Modules =
    [
        new("ntoskrnl.exe", 0xFFFFF800_1000_0000, 0x0100_0000),
        new("ndis.sys", 0xFFFFF800_2000_0000, 0x0010_0000),
        new("nvlddmkm.sys", 0xFFFFF800_3000_0000, 0x0400_0000),
        new("Wdf01000.sys", 0xFFFFF800_4000_0000, 0x0010_0000),
    ];

    // ─────────────────────────── module attribution ───────────────────────────

    [Fact]
    public void Routine_addresses_are_attributed_by_module_ranges()
    {
        var map = new ModuleMap(Modules);
        Assert.Equal(4, map.Count);
        Assert.Equal("ntoskrnl.exe", map.Resolve(0xFFFFF800_1000_0000));
        Assert.Equal("ntoskrnl.exe", map.Resolve(0xFFFFF800_10FF_FFFF));
        Assert.Equal("ndis.sys", map.Resolve(0xFFFFF800_2000_1234));
        Assert.Equal("nvlddmkm.sys", map.Resolve(0xFFFFF800_33FF_FFFF));
        Assert.Equal(ModuleMap.Unknown, map.Resolve(0xFFFFF800_1100_0000)); // gap after ntoskrnl
        Assert.Equal(ModuleMap.Unknown, map.Resolve(0xFFFFF800_0000_0000)); // below the first module
        Assert.Equal(ModuleMap.Unknown, map.Resolve(0));
    }

    [Fact]
    public void Without_sizes_the_nearest_module_below_is_used()
    {
        var map = new ModuleMap(Modules.Select(m => m with { Size = 0 }));
        Assert.Equal("ntoskrnl.exe", map.Resolve(0xFFFFF800_1F00_0000));
        Assert.Equal("Wdf01000.sys", map.Resolve(0xFFFFF800_9000_0000));
        Assert.Equal(ModuleMap.Unknown, map.Resolve(0xFFFFF800_0000_0001));
    }

    [Fact]
    public void Empty_module_list_resolves_everything_to_unknown()
    {
        var map = new ModuleMap([new KernelModule("zero.sys", 0, 10), new KernelModule("", 100, 10)]);
        Assert.Equal(0, map.Count);
        Assert.Equal(ModuleMap.Unknown, map.Resolve(105));
    }

    // ─────────────────────────── histogram and percentiles ───────────────────────────

    [Fact]
    public void Histogram_percentile_and_max_are_exact_for_integer_microseconds()
    {
        var s = new ModuleStats("x.sys");
        for (var i = 1; i <= 100; i++) s.Add(RoutineKind.Dpc, i);
        Assert.Equal(100, s.Count);
        Assert.Equal(100, s.MaxMicros);
        Assert.Equal(5050, s.TotalMicros);
        Assert.Equal(5.05, s.TotalMillis, 6);
        Assert.Equal(99, s.P99);
        Assert.Equal(50, s.Percentile(0.5));
        Assert.Equal(100, s.Percentile(1));
        Assert.Equal(1, s.Percentile(0));
    }

    [Fact]
    public void Values_above_ten_milliseconds_fall_into_the_overflow_bucket()
    {
        var s = new ModuleStats("x.sys");
        s.Add(RoutineKind.Isr, 12_345.6);
        s.Add(RoutineKind.Dpc, 3.9);
        Assert.Equal(ModuleStats.BucketCount, s.P99);
        Assert.Equal(3, s.Percentile(0.5)); // 3.9 µs lands in the 3 µs bucket (lower bound)
        Assert.Equal(12_345.6, s.MaxMicros);
        Assert.Equal(RoutineKind.Isr, s.MaxKind);
        Assert.Equal(1, s.IsrCount);
        Assert.Equal(1, s.DpcCount);
        Assert.Equal(12_345.6, s.MaxIsrMicros);
        Assert.Equal(3.9, s.MaxDpcMicros);
    }

    [Fact]
    public void Negative_durations_are_clamped_to_zero()
    {
        var s = new ModuleStats("x.sys");
        s.Add(RoutineKind.Dpc, -5);
        Assert.Equal(1, s.Count);
        Assert.Equal(0, s.TotalMicros);
        Assert.Equal(0, s.P99);
    }

    // ─────────────────────────── aggregator ───────────────────────────

    private static DpcAggregator Sample()
    {
        var a = new DpcAggregator(new ModuleMap(Modules));
        for (var i = 0; i < 1000; i++) a.Add(RoutineKind.Dpc, 0xFFFFF800_1000_0100, 20);   // ntoskrnl: many short
        a.Add(RoutineKind.TimerDpc, 0xFFFFF800_1000_0200, 40);                             // ntoskrnl, another routine
        for (var i = 0; i < 10; i++) a.Add(RoutineKind.Dpc, 0xFFFFF800_2000_0100, 300);    // ndis
        a.Add(RoutineKind.Dpc, 0xFFFFF800_2000_0100, 1500);                                 // ndis spike
        a.Add(RoutineKind.Isr, 0xFFFFF800_3000_0100, 120);                                  // nvidia ISR
        a.Add(RoutineKind.Dpc, 0xFFFFF800_7000_0000, 600);                                  // unknown module
        return a;
    }

    [Fact]
    public void Aggregator_groups_routines_by_module_and_tracks_the_global_maximum()
    {
        var a = Sample();
        Assert.Equal(1014, a.EventCount);
        Assert.Equal(1, a.UnknownCount);
        Assert.Equal(1500, a.MaxMicros);
        Assert.Equal(RoutineKind.Dpc, a.MaxKind);
        Assert.Equal("ndis.sys", a.MaxModule);
        Assert.Equal(1500, a.MaxDpcMicros);
        Assert.Equal(120, a.MaxIsrMicros);

        var modules = a.Modules;
        Assert.Equal(["ndis.sys", ModuleMap.Unknown, "nvlddmkm.sys", "ntoskrnl.exe"], modules.Select(m => m.Name));
        var kernel = modules.Single(m => m.Name == "ntoskrnl.exe");
        Assert.Equal(1001, kernel.Count);
        Assert.Equal(40, kernel.MaxMicros);
        Assert.Equal(RoutineKind.TimerDpc, kernel.MaxKind);
        var ndis = modules[0];
        Assert.Equal(11, ndis.Count);
        Assert.Equal(1500, ndis.P99);
    }

    [Fact]
    public void Invalid_durations_are_ignored()
    {
        var a = new DpcAggregator(new ModuleMap(Modules));
        a.Add(RoutineKind.Dpc, 0xFFFFF800_1000_0100, double.NaN);
        a.Add(RoutineKind.Dpc, 0xFFFFF800_1000_0100, -1);
        Assert.Equal(0, a.EventCount);
        Assert.Equal(DpcVerdict.Normal, a.Verdict);
    }

    // ─────────────────────────── verdicts ───────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(499.9, 0)]
    [InlineData(500, 1)]
    [InlineData(1000, 1)]
    [InlineData(1000.1, 2)]
    [InlineData(27_457, 2)]
    public void Verdict_thresholds_follow_latencymon_practice(double maxMicros, int expected)
    {
        Assert.Equal((DpcVerdict)expected, DpcAggregator.VerdictFor(maxMicros));
    }

    [Fact]
    public void Verdict_texts_are_plain_russian()
    {
        Assert.Equal("в норме", DpcAggregator.VerdictText(DpcVerdict.Normal));
        Assert.Equal("повышенные", DpcAggregator.VerdictText(DpcVerdict.Elevated));
        Assert.StartsWith("высокие", DpcAggregator.VerdictText(DpcVerdict.High));
        Assert.Equal(DpcVerdict.High, Sample().Verdict);
    }

    [Fact]
    public void Outcome_is_ok_for_normal_warn_otherwise_and_fail_without_events()
    {
        var a = new DpcAggregator(new ModuleMap(Modules));
        Assert.Equal(ToolStatus.Failed, ToolsViewModel.ClassifyDpc(a, TimeSpan.FromSeconds(60)).Status);

        a.Add(RoutineKind.Dpc, 0xFFFFF800_1000_0100, 50);
        var ok = ToolsViewModel.ClassifyDpc(a, TimeSpan.FromSeconds(60));
        Assert.Equal(ToolStatus.Success, ok.Status);
        Assert.Contains("в норме", ok.Summary);
        Assert.Contains("ntoskrnl.exe", ok.Summary);
        Assert.DoesNotContain("Драйверы", ok.Summary);

        var warn = ToolsViewModel.ClassifyDpc(Sample(), TimeSpan.FromSeconds(60));
        Assert.Equal(ToolStatus.Warning, warn.Status);
        Assert.Contains("высокие", warn.Summary);
        Assert.Contains("1500 мкс", warn.Summary);
        Assert.Contains("ndis.sys", warn.Summary);
        Assert.Contains("«Драйверы»", warn.Summary);
    }

    // ─────────────────────────── family hints and report ───────────────────────────

    [Theory]
    [InlineData("ndis.sys", "сетевой стек")]
    [InlineData("NDIS.SYS", "сетевой стек")]
    [InlineData("nvlddmkm.sys", "NVIDIA")]
    [InlineData("dxgkrnl.sys", "графическая подсистема")]
    [InlineData("ACPI.sys", "прошивка/BIOS")]
    [InlineData("storport.sys", "накопители")]
    [InlineData("stornvme.sys", "накопители")]
    [InlineData("Wdf01000.sys", "WDF")]
    [InlineData("tcpip.sys", "TCP/IP")]
    [InlineData("USBXHCI.SYS", "USB 3")]
    public void Known_driver_families_have_a_hint(string module, string fragment)
    {
        var hint = DpcAggregator.Family(module);
        Assert.NotNull(hint);
        Assert.Contains(fragment, hint);
    }

    [Fact]
    public void Unknown_driver_has_no_family_hint() => Assert.Null(DpcAggregator.Family("mysterious.sys"));

    [Fact]
    public void Report_lists_top_modules_flags_slow_ones_and_points_to_drivers()
    {
        var lines = Sample().FormatReport(TimeSpan.FromSeconds(60));
        Assert.Contains(lines, l => l.StartsWith("Событий DPC/ISR за 60 с: 1,014, без модуля: 1."));
        Assert.Contains(lines, l => l.Contains("Самый долгий вызов: 1500 мкс (DPC, ndis.sys)"));
        Assert.Contains(lines, l => l.Contains("МОДУЛЬ") && l.Contains("P99 МКС"));
        var table = lines.Where(l => l.StartsWith("ndis.sys ") && !l.Contains(" — ")).ToList();
        Assert.Single(table);
        Assert.Contains("1500", table[0]);
        Assert.Contains(lines, l => l.StartsWith("ndis.sys — сетевой стек"));
        Assert.Contains(lines, l => l.StartsWith(ModuleMap.Unknown + " — адрес не удалось сопоставить"));
        Assert.DoesNotContain(lines, l => l.StartsWith("nvlddmkm.sys —")); // 120 µs is not flagged
        Assert.Equal("Следующий шаг: раздел «Драйверы» — обновите драйвер отмеченного устройства; исправление не гарантируется.", lines[^1]);
    }

    [Fact]
    public void Report_without_events_says_so()
    {
        var a = new DpcAggregator(new ModuleMap(Modules));
        var lines = a.FormatReport(TimeSpan.FromSeconds(60));
        Assert.Equal(2, lines.Count);
        Assert.Contains("не дала данных", lines[1]);
        Assert.Contains("не удалось", a.Summary(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Report_truncates_to_top_ten_modules()
    {
        var modules = Enumerable.Range(0, 15).Select(i => new KernelModule($"m{i}.sys", 0x1000UL * (ulong)(i + 1), 0x1000)).ToList();
        var a = new DpcAggregator(new ModuleMap(modules));
        for (var i = 0; i < 15; i++) a.Add(RoutineKind.Dpc, 0x1000UL * (ulong)(i + 1) + 8, 10 + i);
        var lines = a.FormatReport(TimeSpan.FromSeconds(60));
        Assert.Equal(10, lines.Count(l => l.StartsWith("m") && l.Contains(".sys ")));
        Assert.Contains("… ещё модулей: 5.", lines);
    }

    // ─────────────────────────── ETW session rules ───────────────────────────

    [Fact]
    public void Only_sessions_with_monoliths_own_prefix_may_be_stopped()
    {
        Assert.True(EtwSession.IsOwnLoggerName(EtwSession.NewLoggerName()));
        Assert.True(EtwSession.IsOwnLoggerName("MONOLITH-DpcLatency-0123456789abcdef0123456789abcdef"));
        Assert.False(EtwSession.IsOwnLoggerName("MONOLITH-DpcLatency-")); // the bare prefix is not a session of ours
        Assert.False(EtwSession.IsOwnLoggerName("NT Kernel Logger"));
        Assert.False(EtwSession.IsOwnLoggerName("Circular Kernel Context Logger"));
        Assert.False(EtwSession.IsOwnLoggerName("LatencyMon"));
        Assert.False(EtwSession.IsOwnLoggerName("monolith-dpclatency-abc")); // case matters: never a near-match
        Assert.False(EtwSession.IsOwnLoggerName(" MONOLITH-DpcLatency-abc"));
        Assert.False(EtwSession.IsOwnLoggerName(null));
        Assert.False(EtwSession.IsOwnLoggerName(""));
    }

    [Fact]
    public void Logger_names_are_unique_per_run()
    {
        var a = EtwSession.NewLoggerName();
        var b = EtwSession.NewLoggerName();
        Assert.NotEqual(a, b);
        Assert.StartsWith(EtwSession.LoggerPrefix, a);
        Assert.True(a.Length < 1024);
    }

    [Fact]
    public void Native_structures_have_the_documented_x64_sizes()
    {
        // evntrace.h on x64: EVENT_TRACE_PROPERTIES 120 bytes (WNODE_HEADER 48 + 18 ULONG/HANDLE fields), EVENT_TRACE_LOGFILEW 448 bytes.
        Assert.Equal(120, EtwSession.PropertiesSize);
        Assert.Equal(448, EtwSession.LogFileSize);
        Assert.Equal(new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc"), EtwSession.PerfInfoGuid);
    }

    // ─────────────────────────── EVENT_RECORD parsing ───────────────────────────

    /// <summary>Builds a 112-byte x64 EVENT_RECORD with a native UserData buffer {InitialTime, Routine}; the caller frees both.</summary>
    private static (IntPtr Record, IntPtr Data) BuildRecord(Guid provider, byte opcode, byte level, long timeStamp, long initialTime,
        ulong routine, ushort flags = 0, ushort userDataLength = 16)
    {
        var record = Marshal.AllocHGlobal(112);
        var data = Marshal.AllocHGlobal(16);
        for (var i = 0; i < 112; i++) Marshal.WriteByte(record, i, 0);
        Marshal.WriteInt16(record, 4, (short)flags);
        Marshal.WriteInt64(record, 16, timeStamp);
        Marshal.Copy(provider.ToByteArray(), 0, record + 24, 16);
        Marshal.WriteInt16(record, 40, 0x1234);           // Id
        Marshal.WriteByte(record, 42, 2);                 // Version
        Marshal.WriteByte(record, 43, 0);                 // Channel
        Marshal.WriteByte(record, 44, level);             // Level
        Marshal.WriteByte(record, 45, opcode);            // Opcode
        Marshal.WriteInt16(record, 86, (short)userDataLength);
        Marshal.WriteIntPtr(record, 96, data);
        Marshal.WriteInt64(data, 0, initialTime);
        Marshal.WriteInt64(data, 8, unchecked((long)routine));
        return (record, data);
    }

    private static void Free((IntPtr Record, IntPtr Data) r)
    {
        Marshal.FreeHGlobal(r.Record);
        Marshal.FreeHGlobal(r.Data);
    }

    [Fact]
    public void Dpc_record_is_parsed_from_the_opcode_byte_not_the_level()
    {
        // Level @44 carries a decoy value that is itself a valid opcode (67 = ISR): the kind must come from Opcode @45.
        var r = BuildRecord(EtwSession.PerfInfoGuid, opcode: 68, level: 67, timeStamp: 1_000_500, initialTime: 1_000_000,
            routine: 0xFFFFF800_2000_1234);
        try
        {
            Assert.True(EtwSession.TryParseRecord(r.Record, out var kind, out var routine, out var ticks));
            Assert.Equal(RoutineKind.Dpc, kind);
            Assert.Equal(0xFFFFF800_2000_1234UL, routine);
            Assert.Equal(500, ticks);
        }
        finally { Free(r); }
    }

    [Theory]
    [InlineData(66, 2)] // ThreadedDPC
    [InlineData(67, 0)] // ISR
    [InlineData(68, 1)] // DPC
    [InlineData(69, 3)] // TimerDPC
    public void Every_perfinfo_routine_opcode_maps_to_its_kind(int opcode, int expectedKind)
    {
        var r = BuildRecord(EtwSession.PerfInfoGuid, (byte)opcode, level: 0, timeStamp: 20, initialTime: 10, routine: 42);
        try
        {
            Assert.True(EtwSession.TryParseRecord(r.Record, out var kind, out _, out var ticks));
            Assert.Equal(expectedKind, (int)kind);
            Assert.Equal(10, ticks);
        }
        finally { Free(r); }
    }

    [Fact]
    public void Other_providers_opcodes_and_short_payloads_are_rejected()
    {
        var foreign = BuildRecord(Guid.NewGuid(), 68, 0, 20, 10, 42);
        var otherOpcode = BuildRecord(EtwSession.PerfInfoGuid, 46, 68, 20, 10, 42); // 68 in Level only
        var shortPayload = BuildRecord(EtwSession.PerfInfoGuid, 68, 0, 20, 10, 42, userDataLength: 12);
        try
        {
            Assert.False(EtwSession.TryParseRecord(foreign.Record, out _, out _, out _));
            Assert.False(EtwSession.TryParseRecord(otherOpcode.Record, out _, out _, out _));
            Assert.False(EtwSession.TryParseRecord(shortPayload.Record, out _, out _, out _));
            Assert.False(EtwSession.TryParseRecord(IntPtr.Zero, out _, out _, out _));
        }
        finally { Free(foreign); Free(otherOpcode); Free(shortPayload); }
    }

    [Fact]
    public void Records_with_a_32_bit_header_read_a_4_byte_routine()
    {
        // EVENT_HEADER_FLAG_32_BIT_HEADER (0x20): the routine pointer is 4 bytes, so 12 payload bytes are enough.
        var r = BuildRecord(EtwSession.PerfInfoGuid, 67, 0, 300, 100, 0xDEADBEEF_8000_1000, flags: 0x0020, userDataLength: 12);
        try
        {
            Assert.True(EtwSession.TryParseRecord(r.Record, out var kind, out var routine, out var ticks));
            Assert.Equal(RoutineKind.Isr, kind);
            Assert.Equal(0x8000_1000UL, routine);
            Assert.Equal(200, ticks);
        }
        finally { Free(r); }
    }

    [Fact]
    public void Cancel_and_game_stop_texts_say_nothing_was_changed()
    {
        Assert.Contains("ничего не изменено", ToolsViewModel.DpcCancelledText);
        Assert.DoesNotContain("сохраняются", ToolsViewModel.DpcCancelledText);
        Assert.Contains("игровой сеанс", ToolsViewModel.DpcGameStoppedText);
        Assert.Contains("Ничего не изменено", ToolsViewModel.DpcGameStoppedText);
    }

    [Theory]
    [InlineData(5, "права администратора")]
    [InlineData(183, "уже существует")]
    [InlineData(1450, "не более 8")]
    [InlineData(24, "параметры")]
    [InlineData(4242, "код 4242")]
    public void Start_errors_are_explained(int code, string fragment) =>
        Assert.Contains(fragment, EtwSession.DescribeStartError(code));

    // ─────────────────────────── the card ───────────────────────────

    [Fact]
    public void Diagnostics_section_has_the_dpc_card_admin_only()
    {
        var cards = ToolsViewModel.BuildSections([], "C:", hasBattery: false, modernStandby: false).Single(s => s.Title == "ДИАГНОСТИКА").Cards;
        var card = cards.Single(c => c.Title == "ЗАДЕРЖКИ ДРАЙВЕРОВ (DPC/ISR)");
        Assert.True(card.RequiresAdmin);
        Assert.False(card.NeedsReboot);
        Assert.False(card.IsDanger);
        Assert.Equal("60 С", card.Duration);
        Assert.Equal(Monolith.UI.Glyphs.Diagnostic, card.Glyph);
        Assert.Contains("ISR", card.Description);
        Assert.Contains("DPC", card.Description);
        Assert.Contains("Ничего не меняет", card.Note!);
        Assert.Contains("«Драйверы»", card.Note!);
        Assert.Contains("не играйте", card.Note!);
        var action = Assert.Single(card.Actions);
        Assert.Equal(ToolOp.DpcLatencyTest, action.Op);
        Assert.True(action.RequiresAdmin);
        Assert.True(action.IsPrimary);
        Assert.Equal(TimeSpan.FromSeconds(60), ToolsViewModel.DpcDuration);
        Assert.Equal(ToolsViewModel.DpcCommandText, card.Command);
    }

    [Fact]
    public void Dpc_card_is_always_present_on_windows_10_and_11()
    {
        // Private system loggers need Windows 8 (6.2); the catalog never hides the card because every supported OS is newer.
        foreach (var battery in new[] { false, true })
        foreach (var modern in new[] { false, true })
            Assert.Contains(ToolsViewModel.BuildSections([], "C:", battery, modern).Single(s => s.Title == "ДИАГНОСТИКА").Cards,
                c => c.Actions.Any(a => a.Op == ToolOp.DpcLatencyTest));
    }
}
