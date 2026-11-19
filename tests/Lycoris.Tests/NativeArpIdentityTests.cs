using System.Runtime.InteropServices;
using Lycoris.Core;
using Lycoris.Modules.Apps;
using Lycoris.Modules.Games.Sources;
using Lycoris.Modules.Removal;

namespace Lycoris.Tests;

public sealed class NativeArpIdentityTests
{
    [Theory]
    [InlineData("Machine64", @"ARP\Machine\X64\")]
    [InlineData("Machine32", @"ARP\Machine\X86\")]
    [InlineData("DesktopUser", @"ARP\User\X64\")]
    public void Uses_original_case_product_code_and_exact_supported_view(string scope, string prefix)
    {
        const string code = "{12345678-1234-1234-1234-123456789ABC}_is1";
        var registration = Entry(Enum.Parse<UninstallScope>(scope), code, "2026 R2");

        var identity = Assert.IsType<NativeArpIdentity>(NativeArpIdentity.TryCreate(registration, Architecture.X64));

        Assert.Equal(prefix + code, identity.Id);
        Assert.Equal(code, identity.ProductCode);
        Assert.Equal("2026 R2", identity.Version);
        Assert.Equal(scope == "DesktopUser" ? ProgramRemovalScope.User : ProgramRemovalScope.Machine, identity.Scope);
    }

    [Fact]
    public void Unverified_os_architectures_cannot_generate_native_ids()
    {
        var entry = Entry(UninstallScope.DesktopUser, "ArbitraryTool", "1.0");
        Assert.Null(NativeArpIdentity.TryCreate(entry, Architecture.Arm64));
        Assert.Null(NativeArpIdentity.TryCreate(entry, Architecture.X86));
    }

    [Theory]
    [InlineData("Name as entered", true)]
    [InlineData("Package_name.2", true)]
    [InlineData("{12345678-1234-1234-1234-123456789ABC}", true)]
    [InlineData("{12345678-1234-1234-1234-123456789ABC}_is1", true)]
    [InlineData("{12345678-1234-1234-1234-123456789ABC}_", false)]
    [InlineData("--force", false)]
    [InlineData(@"bad\segment", false)]
    [InlineData("name/with/slash", false)]
    [InlineData("name&echo", false)]
    [InlineData("name|other", false)]
    [InlineData("name>other", false)]
    [InlineData("%TEMP%", false)]
    [InlineData("bad!expansion", false)]
    [InlineData("bad^escape", false)]
    [InlineData("bad\"argument", false)]
    [InlineData("bad\nargument", false)]
    public void Safe_product_code_has_a_single_bounded_quoted_field(string code, bool expected) =>
        Assert.Equal(expected, UnelevatedWinget.IsSafeProductCode(code));

    [Fact]
    public void Strict_transport_requires_exact_shape_scope_and_matching_native_product_code()
    {
        string[] good = ["uninstall", "--id", @"ARP\Machine\X86\Example Tool", "--exact", "--source", "winget", "--scope", "machine",
            "--disable-interactivity", "--version", "1.0", "--product-code", "Example Tool", "--interactive"];
        Assert.True(UnelevatedWinget.IsStrictRemovalCommand(good));
        Assert.True(UnelevatedWinget.IsStrictRemovalCommand(["--version"]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand(["--version", "--wait"]));
        Assert.True(UnelevatedWinget.IsStrictRemovalCommand(["list", .. good.Skip(1).Take(8)]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand([.. good, "--force"]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand([.. good[..13], "--silent"]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand([.. good[..12], "Unrelated Tool", "--interactive"]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand([.. good[..7], "user", .. good[8..]]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand([.. good[..2], "Vendor.CatalogID", .. good[3..]]));
        Assert.False(UnelevatedWinget.IsStrictRemovalCommand(["export", "--source", "winget", "--include-versions"]));
    }

    [Theory]
    [InlineData("v1.8.1791", "1.8.1791")]
    [InlineData(" V1.29.380\r\n", "1.29.380")]
    [InlineData("1.29.380.0", "1.29.380.0")]
    [InlineData("v1.8", null)]
    [InlineData("v1.8.1762-preview", null)]
    [InlineData("v1..1791", null)]
    [InlineData("diagnostic text\nv1.8.1791", null)]
    [InlineData("v1.8.1791\nv1.29.380", null)]
    [InlineData("v1.8.١٧٩١", null)]
    public void Client_version_is_one_bounded_ascii_version(string output, string? expected) =>
        Assert.Equal(expected, ProgramRemovalBatchAction.ParseClientVersion(output)?.ToString());

    [Fact]
    public void Legacy_builder_cannot_take_native_arp_argument_grammar()
    {
        Assert.Throws<ArgumentException>(() => UnelevatedWinget.BuildCommandLine(@"C:\WinGet\winget.exe",
            ["list", "--id", @"ARP\Machine\X64\Example Tool", "--exact"], "test.log", "test.exit"));
    }

    [Fact]
    public void Official_source_matches_live_source_export_shape_and_rejects_ambiguous_fields()
    {
        const string live = """
            {"Arg":"https://cdn.winget.microsoft.com/cache","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","Explicit":false,"Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Name":"winget","TrustLevel":["Trusted","StoreOrigin"],"Type":"Microsoft.PreIndexed.Package"}
            """;
        Assert.True(ProgramRemovalExport.IsOfficialSource(live));
        Assert.False(ProgramRemovalExport.IsOfficialSource(live.Replace("\"Arg\":", "\"Argument\":")));
        Assert.False(ProgramRemovalExport.IsOfficialSource(live.Replace("\"Name\":", "\"Name\":\"other\",\"Name\":")));
    }

    private static UninstallRegistration Entry(UninstallScope scope, string code, string version) =>
        new(scope, code, "Arbitrary editor", "Contoso", version, false, false, false, true, "test-signature");
}
