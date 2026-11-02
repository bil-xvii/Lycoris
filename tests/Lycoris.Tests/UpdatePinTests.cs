using System.IO;
using Microsoft.Win32;
using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Tests;

/// <summary>«Остаться на текущей версии Windows 11» (wu-target-version) and «Отключить аппаратное планирование GPU» (hags-off): pure rules.</summary>
public sealed class UpdatePinCatalogTests
{
    private static readonly DateOnly Today = new(2026, 11, 1);

    [Theory]
    [InlineData("23H2", 2025, 11, 11)]
    [InlineData("24H2", 2026, 10, 13)]
    [InlineData("25H2", 2027, 10, 12)]
    [InlineData("26H1", 2028, 3, 14)]
    [InlineData("26H2", 2028, 10, 10)]
    [InlineData("25h2", 2027, 10, 12)] // DisplayVersion compared case-insensitively
    [InlineData(" 26H2 ", 2028, 10, 10)]
    public void End_of_servicing_table_matches_microsoft_release_health(string version, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), TweakCatalog.KnownRelease(version));

    [Theory]
    [InlineData("22H2")] // past and not in the table
    [InlineData("21H2")]
    [InlineData("2009")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_versions_are_never_offered(string? version)
    {
        Assert.Null(TweakCatalog.KnownRelease(version));
        Assert.False(TweakCatalog.IsPinnable(version, Today));
    }

    [Fact]
    public void Every_table_date_is_a_patch_tuesday()
    {
        Assert.All(TweakCatalog.Windows11HomeProEndOfServicing.Values, d =>
        {
            Assert.Equal(DayOfWeek.Tuesday, d.DayOfWeek);
            Assert.InRange(d.Day, 8, 14);
        });
    }

    [Fact]
    public void A_version_is_pinnable_until_its_end_of_servicing_inclusive()
    {
        Assert.True(TweakCatalog.IsPinnable("25H2", Today));
        Assert.True(TweakCatalog.IsPinnable("25H2", new DateOnly(2027, 10, 12)));
        Assert.False(TweakCatalog.IsPinnable("25H2", new DateOnly(2027, 10, 13)));
        Assert.False(TweakCatalog.IsPinnable("24H2", Today)); // end of updates 13.10.2026: pinning it would mean no security updates
        Assert.True(TweakCatalog.IsPinnable("24H2", new DateOnly(2026, 10, 1)));
    }

    [Theory]
    [InlineData("Professional", "25H2", true)]
    [InlineData("ProfessionalWorkstation", "26H2", true)]
    [InlineData("Enterprise", "25H2", true)]
    [InlineData("Core", "25H2", false)]
    [InlineData("CoreSingleLanguage", "25H2", false)]
    [InlineData("Professional", "24H2", false)] // past end of servicing on 01.11.2026
    [InlineData("Professional", "22H2", false)]
    [InlineData("Professional", "", false)]
    public void Pin_applies_to_pro_and_higher_on_a_pinnable_version_only(string edition, string version, bool expected) =>
        Assert.Equal(expected, TweakCatalog.IsUpdatePinApplicable(edition, version, Today));

    [Theory]
    [InlineData("Professional", "25H2", null, true)]
    [InlineData("Professional", "24H2", null, false)]   // past end of servicing, nothing pinned: the row is not offered
    [InlineData("Professional", "24H2", "24H2", true)]  // …but an applied pin keeps its row (ПРИМЕНЕНО + ОТКАТИТЬ) after that date
    [InlineData("Professional", "27H1", "25H2", true)]  // a stale pin on an unknown version: still shown (ЧАСТИЧНО, undo)
    [InlineData("Professional", "", "25H2", true)]
    [InlineData("Core", "25H2", "25H2", false)]         // Home ignores the policy — never shown
    [InlineData("Core", "24H2", null, false)]
    public void Pin_row_stays_while_a_pin_is_in_place(string edition, string version, string? pinnedTo, bool expected) =>
        Assert.Equal(expected, TweakCatalog.IsUpdatePinRowShown(edition, version, Today, pinnedTo));

    [Fact]
    public void Pin_row_applicability_uses_the_row_rule()
    {
        // Title and Details are computed from the live DisplayVersion, never from the (possibly still empty) SystemProfile.
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;
        Assert.Equal(TweakCatalog.TitleFor(TweakCatalog.ReadInstalledDisplayVersion()), pin.Title);
        Assert.Equal(TweakCatalog.UpdatePinDetails(TweakCatalog.ReadInstalledDisplayVersion()), pin.Details);
        Assert.Contains("60 дней", pin.Details);
    }

    [Fact]
    public void Title_names_the_installed_version()
    {
        Assert.Equal("Остаться на текущей версии Windows 11 (25H2)", TweakCatalog.TitleFor("25H2"));
        Assert.Equal("Остаться на текущей версии Windows 11", TweakCatalog.TitleFor(""));
        Assert.Equal("Остаться на текущей версии Windows 11", TweakCatalog.TitleFor(null));
        Assert.True(TweakCatalog.TitleFor("26H2").Length <= 60);
    }

    [Fact]
    public void Details_state_the_date_and_the_60_day_gap()
    {
        var known = TweakCatalog.UpdatePinDetails("25H2");
        Assert.Contains("25H2", known);
        Assert.Contains("12.10.2027", known);
        Assert.Contains("60 дней", known);
        Assert.Contains("Отложить крупные обновления на 90 дней", known);

        var unknown = TweakCatalog.UpdatePinDetails(null);
        Assert.Contains("24 месяца", unknown);
        Assert.DoesNotContain("  ", unknown);
    }

    [Fact]
    public void Date_text_is_dd_mm_yyyy() => Assert.Equal("10.10.2028", TweakCatalog.DateText(new DateOnly(2028, 10, 10)));

    [Fact]
    public void Pin_is_an_optional_system_item_in_no_profile()
    {
        var pin = Assert.IsType<CustomTweak>(TweakCatalog.Find(TweakCatalog.UpdatePinId));
        Assert.Equal(TweakCategory.System, pin.Category);
        Assert.Equal(TweakRisk.Optional, pin.Risk);
        Assert.Equal(ProfileSet.None, pin.DefaultIn);
        Assert.NotNull(pin.AppliesTo);
        Assert.False(string.IsNullOrWhiteSpace(pin.NotApplicableReason));
        Assert.StartsWith("Остаться на текущей версии Windows 11", pin.Title);
        foreach (var kind in Enum.GetValues<ProfileKind>())
            Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), t => t.Id == TweakCatalog.UpdatePinId);
    }

    [Fact]
    public void Pin_and_deferral_are_one_choice()
    {
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;
        var defer = TweakCatalog.Find(TweakCatalog.DeferFeatureUpdatesId)!;
        Assert.Equal(TweakCatalog.WuFeatureUpdatesGroup, pin.ExclusiveGroup);
        Assert.Equal(TweakCatalog.WuFeatureUpdatesGroup, defer.ExclusiveGroup);
        Assert.True(pin.SharesGroupWith(defer));
        Assert.True(defer.SharesGroupWith(pin));
        Assert.Equal(2, TweakCatalog.All.Count(t => t.ExclusiveGroup == TweakCatalog.WuFeatureUpdatesGroup));
        Assert.Contains("Остаться на текущей версии Windows 11", defer.Details);
        // The deferral carries its own guard (a CustomTweak), because the group's members write different values.
        Assert.IsType<CustomTweak>(defer);
        Assert.Equal(2, TweakCatalog.DeferFeatureUpdatesValues.Count);
        Assert.All(TweakCatalog.DeferFeatureUpdatesValues, v => Assert.Equal(RegHive.HKLM, v.Hive));
    }

    [Fact]
    public void Hags_off_is_an_optional_gaming_fix_in_no_profile()
    {
        var off = Assert.IsType<CustomTweak>(TweakCatalog.Find(TweakCatalog.HagsOffId));
        Assert.Equal(TweakCategory.Gaming, off.Category);
        Assert.Equal(TweakRisk.Optional, off.Risk);
        Assert.Equal(ProfileSet.None, off.DefaultIn);
        Assert.Equal(RestartRequirement.Reboot, off.Restart);
        Assert.NotNull(off.AppliesTo);
        Assert.False(string.IsNullOrWhiteSpace(off.NotApplicableReason));
        Assert.Contains("Frame Generation", off.Details);
        foreach (var kind in Enum.GetValues<ProfileKind>())
            Assert.DoesNotContain(TweakCatalog.DefaultsFor(kind), t => t.Id == TweakCatalog.HagsOffId);
    }

    [Fact]
    public void Hags_items_form_one_exclusive_group()
    {
        var members = TweakCatalog.All.Where(t => t.ExclusiveGroup == TweakGroups.Hags).Select(t => t.Id).ToList();
        Assert.Equal(["hags", "hags-other-gpu", TweakCatalog.HagsOffId], members);
        var off = TweakCatalog.Find(TweakCatalog.HagsOffId)!;
        Assert.True(off.SharesGroupWith(TweakCatalog.Find("hags")!));
        Assert.True(TweakCatalog.Find("hags-other-gpu")!.SharesGroupWith(off));
    }
}

/// <summary>
/// The same two items against a redirected registry (HKCU\Software\Lycoris-Tests\Redirect-*): detection matrix, apply through
/// ChangeSession, exact undo (absent → deleted, created policy key removed), the one-of-two guard, and JournalTrust accepting
/// every record the real catalog code produces. Windows only (registry).
/// </summary>
[Collection("Registry")]
public sealed class UpdatePinRegistryTests : IDisposable
{
    private const string Policy = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate";
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private readonly string _root = $@"Software\Lycoris-Tests\Redirect-{Guid.NewGuid():N}";
    private readonly string _journalPath = Path.Combine(Path.GetTempPath(), $"lycoris-pintest-{Guid.NewGuid():N}.json");

    public UpdatePinRegistryTests()
    {
        RegistryHelper.Redirect = (hive, path) => (RegHive.HKCU, $@"{_root}\{hive}\{path}");
        ChangeSession.BypassElevationForTests = true;
        TweakCatalog.UpdatePinToday = () => new DateOnly(2026, 11, 1);
        // Keys that exist on every Windows: they must survive every revert.
        RegistryHelper.Write(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows", "lycoris-test-baseline", RegistryValueKind.String, "x");
        RegistryHelper.Write(RegHive.HKLM, GraphicsDrivers, "lycoris-test-baseline", RegistryValueKind.String, "x");
    }

    public void Dispose()
    {
        RegistryHelper.Redirect = null;
        ChangeSession.BypassElevationForTests = false;
        TweakCatalog.UpdatePinToday = () => DateOnly.FromDateTime(DateTime.Now);
        try { Registry.CurrentUser.DeleteSubKeyTree(_root, false); } catch { }
        foreach (var f in new[] { _journalPath, _journalPath + ".bak", _journalPath + ".tmp" })
            try { File.Delete(f); } catch { }
    }

    private static void Installed(string version) => RegistryHelper.Write(RegHive.HKLM, CurrentVersion, "DisplayVersion", RegistryValueKind.String, version);

    private static void Policy3(string product = "Windows 11", int target = 1, string info = "25H2")
    {
        RegistryHelper.Write(RegHive.HKLM, Policy, "ProductVersion", RegistryValueKind.String, product);
        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersion", RegistryValueKind.DWord, target);
        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersionInfo", RegistryValueKind.String, info);
    }

    private static async Task<ChangeJournal> ApplyAsync(ChangeJournal journal, Tweak tweak)
    {
        var session = await ChangeSession.BeginAsync("test " + tweak.Id, createRestorePoint: false, journal: journal);
        try
        {
            session.Exclusive(() => tweak.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = tweak }));
        }
        finally
        {
            session.Complete();
        }
        Assert.Empty(session.Errors);
        return journal;
    }

    private static string Capture(params (string Path, string Name)[] values) =>
        string.Join("\n", values.Select(v =>
        {
            var s = RegistryHelper.Snapshot(RegHive.HKLM, v.Path, v.Name);
            return $"{v.Path}\\{v.Name} = {(s.ValueExisted ? $"{s.Kind}:{s.Data}" : "absent")}";
        }));

    private static readonly (string, string)[] PinValues =
        [(Policy, "ProductVersion"), (Policy, "TargetReleaseVersion"), (Policy, "TargetReleaseVersionInfo")];

    [Fact]
    public void Detect_matrix()
    {
        Installed("25H2");
        Assert.Equal(TweakState.NotApplied, TweakCatalog.DetectUpdatePin());

        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersion", RegistryValueKind.DWord, 1);
        Assert.Equal(TweakState.Partial, TweakCatalog.DetectUpdatePin());

        Policy3(info: "24H2"); // pinned to another version than the installed one
        Assert.Equal(TweakState.Partial, TweakCatalog.DetectUpdatePin());

        Policy3();
        Assert.Equal(TweakState.Applied, TweakCatalog.DetectUpdatePin());
        Assert.Equal(TweakState.Applied, TweakCatalog.Find(TweakCatalog.UpdatePinId)!.Detect());

        Policy3(target: 0);
        Assert.Equal(TweakState.Partial, TweakCatalog.DetectUpdatePin());
    }

    [Fact]
    public void Pinned_version_is_read_only_when_the_policy_is_on()
    {
        Assert.Null(TweakCatalog.ReadUpdatePinnedTo());
        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersion", RegistryValueKind.DWord, 1);
        Assert.Null(TweakCatalog.ReadUpdatePinnedTo()); // no version named
        Policy3(info: "25H2");
        Assert.Equal("25H2", TweakCatalog.ReadUpdatePinnedTo());
        Policy3(target: 0, info: "25H2");
        Assert.Null(TweakCatalog.ReadUpdatePinnedTo());
    }

    [Fact]
    public async Task Apply_writes_the_installed_version_and_undo_removes_values_and_the_created_key()
    {
        Installed("25H2");
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;
        var before = Capture(PinValues);
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), pin);

        Assert.Equal(TweakState.Applied, pin.Detect());
        Assert.Equal("Windows 11", RegistryHelper.ReadString(RegHive.HKLM, Policy, "ProductVersion"));
        Assert.Equal(1, RegistryHelper.ReadInt(RegHive.HKLM, Policy, "TargetReleaseVersion"));
        Assert.Equal("25H2", RegistryHelper.ReadString(RegHive.HKLM, Policy, "TargetReleaseVersionInfo"));
        Assert.Equal("25H2", TweakCatalog.ReadUpdatePinnedTo());

        var records = journal.ActiveRecords.Where(r => r.Source == "tweak:" + TweakCatalog.UpdatePinId).ToList();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Null(JournalTrust.Check(r))); // every record shape the catalog produces passes the trust gate
        Assert.All(records, r => Assert.Equal(ChangeKind.Registry, r.Kind));

        var result = await new ChangeJournal(_journalPath).RevertAllAsync(); // reloaded from disk, like after a restart
        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(before, Capture(PinValues));
        Assert.False(RegistryHelper.KeyExists(RegHive.HKLM, Policy));                          // Lycoris created it
        Assert.True(RegistryHelper.KeyExists(RegHive.HKLM, @"SOFTWARE\Policies\Microsoft\Windows")); // existed before
        Assert.Equal(TweakState.NotApplied, pin.Detect());
    }

    [Fact]
    public async Task Undo_puts_back_values_of_another_kind_exactly()
    {
        Installed("26H2");
        RegistryHelper.Write(RegHive.HKLM, Policy, "ProductVersion", RegistryValueKind.DWord, 7);
        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersion", RegistryValueKind.String, "0");
        RegistryHelper.Write(RegHive.HKLM, Policy, "TargetReleaseVersionInfo", RegistryValueKind.String, "24H2");
        var before = Capture(PinValues);
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;

        await ApplyAsync(new ChangeJournal(_journalPath), pin);
        Assert.Equal(TweakState.Applied, pin.Detect());
        Assert.True((await new ChangeJournal(_journalPath).RevertAllAsync()).Success);

        Assert.Equal(before, Capture(PinValues));
        Assert.True(RegistryHelper.KeyExists(RegHive.HKLM, Policy)); // not created by Lycoris: stays
    }

    [Fact]
    public async Task Pin_refuses_while_the_90_day_deferral_is_active()
    {
        Installed("25H2");
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), TweakCatalog.Find(TweakCatalog.DeferFeatureUpdatesId)!);
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;

        var session = await ChangeSession.BeginAsync("test pin", createRestorePoint: false, journal: journal);
        var ex = Assert.Throws<InvalidOperationException>(() => pin.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = pin }));
        session.Complete();
        Assert.Contains("сначала откатите «Отложить крупные обновления на 90 дней»", ex.Message);
        Assert.Equal(TweakState.NotApplied, pin.Detect());
        Assert.Equal(0, session.Applied);

        // After the deferral is undone the pin is allowed.
        Assert.True((await journal.RevertSourceAsync("tweak:" + TweakCatalog.DeferFeatureUpdatesId)).Success);
        await ApplyAsync(journal, pin);
        Assert.Equal(TweakState.Applied, pin.Detect());
    }

    private static readonly (string, string)[] DeferValues = [(Policy, "DeferFeatureUpdates"), (Policy, "DeferFeatureUpdatesPeriodInDays")];

    [Fact]
    public async Task Deferral_refuses_while_the_pin_is_active_and_the_pin_stays_untouched()
    {
        Installed("25H2");
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), TweakCatalog.Find(TweakCatalog.UpdatePinId)!);
        var pinned = Capture(PinValues);
        var defer = TweakCatalog.Find(TweakCatalog.DeferFeatureUpdatesId)!;

        var session = await ChangeSession.BeginAsync("test defer", createRestorePoint: false, journal: journal);
        var ex = Assert.Throws<InvalidOperationException>(() => defer.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = defer }));
        session.Complete();
        Assert.Contains("сначала откатите «Остаться на текущей версии Windows 11»", ex.Message);
        Assert.Equal(0, session.Applied);
        Assert.Equal(TweakState.NotApplied, defer.Detect());
        Assert.Equal(pinned, Capture(PinValues)); // the pin is neither replaced nor doubled

        Assert.True((await journal.RevertSourceAsync("tweak:" + TweakCatalog.UpdatePinId)).Success);
        await ApplyAsync(journal, defer);
        Assert.Equal(TweakState.Applied, defer.Detect());
    }

    [Fact]
    public async Task Deferral_detects_applies_and_undoes_exactly_like_a_registry_tweak()
    {
        var defer = TweakCatalog.Find(TweakCatalog.DeferFeatureUpdatesId)!;
        Assert.Equal(TweakState.NotApplied, defer.Detect());
        RegistryHelper.Write(RegHive.HKLM, Policy, "DeferFeatureUpdates", RegistryValueKind.DWord, 1);
        Assert.Equal(TweakState.Partial, defer.Detect());
        RegistryHelper.Write(RegHive.HKLM, Policy, "DeferFeatureUpdatesPeriodInDays", RegistryValueKind.DWord, 30);
        var before = Capture(DeferValues);

        var journal = await ApplyAsync(new ChangeJournal(_journalPath), defer);
        Assert.Equal(TweakState.Applied, defer.Detect());
        Assert.Equal(90, RegistryHelper.ReadInt(RegHive.HKLM, Policy, "DeferFeatureUpdatesPeriodInDays"));
        var record = Assert.Single(journal.ActiveRecords, r => r.Source == "tweak:" + TweakCatalog.DeferFeatureUpdatesId); // only the value that differed
        Assert.Null(JournalTrust.Check(record));

        Assert.True((await new ChangeJournal(_journalPath).RevertAllAsync()).Success);
        Assert.Equal(before, Capture(DeferValues));
        Assert.Equal(TweakState.Partial, defer.Detect());
    }

    [Theory]
    [InlineData("24H2", "закончилась")]     // end of updates 13.10.2026 is behind the pinned "today" (01.11.2026)
    [InlineData("22H2", "не входит")]       // not in the table
    [InlineData("", "не удалось прочитать")]
    public async Task Pin_refuses_a_version_it_must_not_pin(string installed, string reason)
    {
        if (installed.Length > 0) Installed(installed);
        var pin = TweakCatalog.Find(TweakCatalog.UpdatePinId)!;
        var session = await ChangeSession.BeginAsync("test pin", createRestorePoint: false, journal: new ChangeJournal(_journalPath));
        var ex = Assert.Throws<InvalidOperationException>(() => pin.Apply(new TweakContext { Session = session, System = SystemProfile.Current, Tweak = pin }));
        session.Complete();
        Assert.Contains(reason, ex.Message);
        Assert.False(RegistryHelper.KeyExists(RegHive.HKLM, Policy));
    }

    [Fact]
    public void Hags_off_detects_from_HwSchMode()
    {
        var off = TweakCatalog.Find(TweakCatalog.HagsOffId)!;
        var on = TweakCatalog.Find("hags")!;
        RegistryHelper.Write(RegHive.HKLM, GraphicsDrivers, "HwSchMode", RegistryValueKind.DWord, 1);
        Assert.Equal(TweakState.Applied, off.Detect());
        Assert.Equal(TweakState.NotApplied, on.Detect());
        RegistryHelper.Write(RegHive.HKLM, GraphicsDrivers, "HwSchMode", RegistryValueKind.DWord, 2);
        Assert.Equal(TweakState.NotApplied, off.Detect());
        Assert.Equal(TweakState.Applied, on.Detect());
        RegistryHelper.DeleteValue(RegHive.HKLM, GraphicsDrivers, "HwSchMode");
        // No value: the driver's own default decides (the real D3DKMT answer of this machine).
        var driverOff = TweakCatalog.GpuScheduling?.Enabled == false;
        Assert.Equal(driverOff ? TweakState.Applied : TweakState.NotApplied, off.Detect());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task Hags_off_writes_1_and_undo_restores_the_previous_state(int? previous)
    {
        if (previous is { } p) RegistryHelper.Write(RegHive.HKLM, GraphicsDrivers, "HwSchMode", RegistryValueKind.DWord, p);
        var before = Capture((GraphicsDrivers, "HwSchMode"));
        var off = TweakCatalog.Find(TweakCatalog.HagsOffId)!;

        var journal = await ApplyAsync(new ChangeJournal(_journalPath), off);
        Assert.Equal(1, RegistryHelper.ReadInt(RegHive.HKLM, GraphicsDrivers, "HwSchMode"));
        Assert.Equal(TweakState.Applied, off.Detect());
        var record = Assert.Single(journal.ActiveRecords, r => r.Source == "tweak:" + TweakCatalog.HagsOffId);
        Assert.Null(JournalTrust.Check(record));

        Assert.True((await new ChangeJournal(_journalPath).RevertAllAsync()).Success);
        Assert.Equal(before, Capture((GraphicsDrivers, "HwSchMode")));
        Assert.True(RegistryHelper.KeyExists(RegHive.HKLM, GraphicsDrivers));
    }

    [Fact]
    public async Task Hags_off_is_idempotent()
    {
        RegistryHelper.Write(RegHive.HKLM, GraphicsDrivers, "HwSchMode", RegistryValueKind.DWord, 1);
        var journal = await ApplyAsync(new ChangeJournal(_journalPath), TweakCatalog.Find(TweakCatalog.HagsOffId)!);
        Assert.Empty(journal.ActiveRecords); // nothing to change → nothing journaled
    }
}
