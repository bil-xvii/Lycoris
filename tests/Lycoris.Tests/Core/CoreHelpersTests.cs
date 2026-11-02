using Lycoris.Core;
using Lycoris.Tweaks;

namespace Lycoris.Tests.Core;

public sealed class CoreHelpersTests
{
    [Theory]
    [InlineData("RpcSs")]
    [InlineData("WinDefend")]
    [InlineData("wuauserv")]
    [InlineData("BITS")]
    [InlineData("mpssvc")]
    [InlineData("AudioSrv")]
    [InlineData("Dhcp")]
    [InlineData("WpnUserService_3a8f2")]
    public void Critical_services_are_protected(string name) => Assert.True(ServiceControl.IsProtected(name));

    [Theory]
    [InlineData("DiagTrack")]
    [InlineData("Fax")]
    [InlineData("MapsBroker")]
    [InlineData("XblGameSave")]
    public void Optional_services_are_not_protected(string name) => Assert.False(ServiceControl.IsProtected(name));

    [Theory]
    [InlineData("CDPUserSvc_4f1a2", "CDPUserSvc")]
    [InlineData("OneSyncSvc_1a2b3c4d", "OneSyncSvc")]
    [InlineData("Spooler", "Spooler")]
    [InlineData("edgeupdate_m", "edgeupdate_m")]
    [InlineData("Some_Service", "Some_Service")]
    public void Per_user_service_suffix_is_stripped(string name, string expected) =>
        Assert.Equal(expected, ServiceControl.BaseName(name));

    [Fact]
    public void Protected_service_cannot_be_changed()
    {
        var ex = Record.Exception(() => ServiceControl.SetStartMode("RpcSs", System.ServiceProcess.ServiceStartMode.Disabled));
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Theory]
    [InlineData("-")]
    [InlineData(" \\ ")]
    [InlineData("|")]
    [InlineData("  ██████▒▒▒▒  1024 KB / 3.66 MB")]
    [InlineData("")]
    public void Winget_progress_noise_is_filtered(string line) => Assert.True(WingetClient.IsNoise(line));

    [Theory]
    [InlineData("Found Steam [Valve.Steam] Version 2.10")]
    [InlineData("Successfully installed")]
    public void Winget_real_lines_are_kept(string line) => Assert.False(WingetClient.IsNoise(line));

    [Fact]
    public void Winget_benign_codes_count_as_success()
    {
        Assert.True(new WingetResult(WingetClient.UpdateNotApplicable, "").Success);
        Assert.True(new WingetResult(WingetClient.AlreadyInstalled, "").Success);
        Assert.False(new WingetResult(WingetClient.NoApplicationsFound, "").Success);
        Assert.Equal("уже последняя версия", WingetClient.Describe(WingetClient.UpdateNotApplicable));
    }

    [Fact]
    public void Gpu_vendor_is_detected_from_pnp_id()
    {
        Assert.Equal(GpuVendor.Nvidia, SystemProfile.VendorFromPnp(@"PCI\VEN_10DE&DEV_2C05", "x"));
        Assert.Equal(GpuVendor.Amd, SystemProfile.VendorFromPnp(@"PCI\VEN_1002&DEV_13C0", "x"));
        Assert.Equal(GpuVendor.Intel, SystemProfile.VendorFromPnp(@"PCI\VEN_8086&DEV_A780", "x"));
        Assert.Equal(GpuVendor.Nvidia, SystemProfile.VendorFromPnp("", "NVIDIA GeForce RTX 5070 Ti"));
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1536, "1.5 КБ")]
    [InlineData(1073741824, "1 ГБ")]
    public void Bytes_are_formatted(double bytes, string expected) =>
        Assert.Equal(expected, SystemProfile.FormatBytes(bytes).Replace(',', '.'));

    [Fact]
    public void Power_scheme_constants_are_the_documented_guids()
    {
        Assert.Equal(new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"), PowerSchemes.Balanced);
        Assert.Equal(new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"), PowerSchemes.HighPerformance);
        Assert.Equal(new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61"), PowerSchemes.UltimateTemplate);
    }

    [Fact]
    public void Active_power_scheme_can_be_read()
    {
        Assert.NotEqual(Guid.Empty, PowerSchemes.GetActive());
        Assert.NotEmpty(PowerSchemes.List());
    }

    [Fact]
    public void Tweak_catalog_loads_and_ids_are_unique()
    {
        var ids = TweakCatalog.All.Select(t => t.Id).ToList();
        Assert.NotEmpty(ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(TweakCatalog.All, t => Assert.False(string.IsNullOrWhiteSpace(t.Title)));
    }

    [Fact]
    public async Task Process_runner_decodes_oem_output_and_reports_exit_code()
    {
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("cmd.exe"), "/c echo lycoris& exit /b 3",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(20) });
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("lycoris", r.StdOut);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task Process_runner_times_out_and_kills()
    {
        var r = await ProcessRunner.RunAsync(ProcessRunner.Sys("cmd.exe"), "/c ping -n 30 127.0.0.1 >nul",
            new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(1) });
        Assert.True(r.TimedOut);
        Assert.False(r.Success);
    }

    [Fact]
    public async Task PowerShell_output_is_utf8()
    {
        var r = await ProcessRunner.RunPowerShellAsync("Write-Output 'Проверка'", new ProcessRunOptions { LogCommand = false, Timeout = TimeSpan.FromSeconds(60) });
        Assert.True(r.Success, r.Combined);
        Assert.Contains("Проверка", r.StdOut);
    }
}
