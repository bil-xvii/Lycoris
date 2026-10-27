using System.IO;
using Monolith.Core;
using Monolith.Modules.Drivers;

namespace Monolith.Tests;

/// <summary>Direct driver download: only vendor HTTPS servers, safe file names, vendor signature, honest texts.</summary>
public sealed class DriverDownloadsTests
{
    private const string NvidiaUrl = "https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe";
    private const string AmdUrl = "https://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe";

    // ───────────── Allow-list ─────────────

    [Theory]
    [InlineData(NvidiaUrl)]
    [InlineData(AmdUrl)]
    [InlineData("https://international.download.nvidia.com/Windows/621.01/621.01-notebook-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData("https://US.DOWNLOAD.NVIDIA.COM/Windows/621.01/x-dch-whql.exe")]
    [InlineData("https://us.download.nvidia.com:443/Windows/621.01/621.01-desktop-dch.exe")]
    public void Vendor_download_servers_are_allowed(string url) =>
        Assert.True(DriverDownloads.IsAllowedDownload(new Uri(url)));

    [Theory]
    [InlineData("http://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe")] // no TLS
    [InlineData("https://evil.com/621.01-desktop-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData("https://nvidia.com.evil.com/Windows/621.01/driver.exe")]
    [InlineData("https://download.nvidia.com.attacker/Windows/621.01/driver.exe")]
    [InlineData("https://download.nvidia.com.attacker.example/Windows/621.01/driver.exe")]
    [InlineData("https://www.nvidia.com/Windows/621.01/driver.exe")]                     // site, not the download server
    [InlineData("https://us.download.nvidia.com:8443/Windows/621.01/driver.exe")]        // non-default port
    [InlineData("https://drivers.amd.com:444/drivers/installer/x-minimalsetup.exe")]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/driver.zip")]             // not an .exe
    [InlineData("https://us.download.nvidia.com/Windows/621.01/driver.msi")]
    [InlineData("https://drivers.amd.com/drivers/installer/26.20/whql/readme.txt")]
    [InlineData("https://user:pass@us.download.nvidia.com/Windows/621.01/driver.exe")]   // credentials in the URL
    [InlineData("ftp://us.download.nvidia.com/Windows/621.01/driver.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void Anything_else_is_refused(string url) =>
        Assert.False(DriverDownloads.IsAllowedDownload(new Uri(url)));

    [Fact]
    public void Null_and_relative_urls_are_refused()
    {
        Assert.False(DriverDownloads.IsAllowedDownload(null));
        Assert.False(DriverDownloads.IsAllowedDownload(new Uri("/Windows/driver.exe", UriKind.Relative)));
    }

    // ───────────── File names ─────────────

    [Fact]
    public void File_name_comes_from_the_url_path()
    {
        Assert.Equal("621.01-desktop-win10-win11-64bit-international-dch-whql.exe", DriverDownloads.SafeFileName(new Uri(NvidiaUrl)));
        Assert.Equal("amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe", DriverDownloads.SafeFileName(new Uri(AmdUrl)));
        Assert.Equal("driver.exe", DriverDownloads.SafeFileName(new Uri("https://us.download.nvidia.com/a/driver.exe?x=1")));
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/a.exe")]                    // too short
    [InlineData("https://us.download.nvidia.com/Windows/.hidden-driver.exe")]       // starts with a dot
    [InlineData("https://us.download.nvidia.com/Windows/my%20driver.exe")]          // space
    [InlineData("https://us.download.nvidia.com/Windows/драйвер-nvidia.exe")]       // non-ASCII
    [InlineData("https://us.download.nvidia.com/Windows/driver.exe.txt")]
    [InlineData("https://us.download.nvidia.com/Windows/")]
    [InlineData("https://us.download.nvidia.com/Windows/..%5C..%5Cevil-driver.exe")] // encoded path separators
    public void Unsafe_file_names_are_rejected(string url) =>
        Assert.Null(DriverDownloads.SafeFileName(new Uri(url)));

    [Fact]
    public async Task Download_refuses_a_foreign_link_or_a_mismatching_file_name_before_touching_the_network()
    {
        var foreign = new DriverPackage(GpuVendor.Nvidia, "621.01", new Uri("https://evil.com/621.01-dch-whql.exe"), "621.01-dch-whql.exe", null, "NVIDIA Corporation", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DriverDownloads.DownloadAsync(foreign, null, CancellationToken.None));

        var renamed = new DriverPackage(GpuVendor.Nvidia, "621.01", new Uri(NvidiaUrl), @"..\..\Windows\System32\evil.exe", null, "NVIDIA Corporation", null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DriverDownloads.DownloadAsync(renamed, null, CancellationToken.None));
    }

    [Fact]
    public void Installer_outside_the_downloads_folder_is_never_started() =>
        Assert.ThrowsAny<Exception>(() => DriverDownloads.LaunchInstaller(Path.Combine(Environment.SystemDirectory, "notepad.exe")));

    // ───────────── AMD page ─────────────

    private const string AmdHtml = """
        <html><body>
        <a href="https://www.amd.com/en/support/download/release-notes.html">Release notes</a>
        <a class="btn" href="https://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe">
          Download Windows Drivers</a>
        <a href="https://drivers.amd.com/drivers/whql-amd-software-adrenalin-edition-26.10.1-win11-oct18.exe">Full package</a>
        </body></html>
        """;

    [Fact]
    public void Amd_page_yields_the_official_auto_detect_installer()
    {
        var p = DriverDownloads.ParseAmdPage(AmdHtml);
        Assert.NotNull(p);
        Assert.Equal(GpuVendor.Amd, p!.Vendor);
        Assert.Equal("26.10.1", p.Version);
        Assert.Equal(AmdUrl, p.Url.AbsoluteUri);
        Assert.Equal("amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe", p.FileName);
        Assert.Equal("https://www.amd.com/", p.Referer);
        Assert.True(DriverDownloads.SignerMatches("Advanced Micro Devices, Inc.", p.Signer));
    }

    [Theory]
    [InlineData("<html><body>No installer here</body></html>")]
    [InlineData("""<a href="http://drivers.amd.com/drivers/installer/26.20/whql/amd-software-adrenalin-edition-26.10.1-minimalsetup-261018_web.exe">x</a>""")]
    [InlineData("""<a href="https://drivers.amd.com.evil.example/drivers/installer/amd-software-adrenalin-edition-26.10.1-minimalsetup.exe">x</a>""")]
    [InlineData("""<a href="https://evil.example/https://drivers.amd.com/drivers/installer/x-minimalsetup.zip">x</a>""")]
    [InlineData("")]
    public void Amd_page_without_an_official_link_gives_nothing(string html) =>
        Assert.Null(DriverDownloads.ParseAmdPage(html));

    // ───────────── NVIDIA answer ─────────────

    private const string NvidiaJson = """
        { "Success" : "1", "IDS" : [{ "downloadInfo": { "Success" : "1", "Name" : "GeForce%20Game%20Ready%20Driver",
          "Version" : "621.01", "IsBeta" : "0", "IsWHQL" : "1", "ReleaseDateTime" : "Wed Oct 21, 2026",
          "DetailsURL" : "https://www.nvidia.com/en-us/drivers/details/282412/", "DownloadURLFileSize" : "1.04 GB",
          "DownloadURL" : "https://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe" } }] }
        """;

    [Fact]
    public void Nvidia_answer_gives_the_download_url_and_a_package()
    {
        var r = NvidiaLookup.ParseRelease(NvidiaJson);
        Assert.NotNull(r);
        Assert.Equal(NvidiaUrl, r!.DownloadUrl);
        Assert.Equal("1.04 GB", r.FileSize);

        var p = DriverDownloads.NvidiaPackage(r);
        Assert.NotNull(p);
        Assert.Equal("621.01", p!.Version);
        Assert.Equal("621.01-desktop-win10-win11-64bit-international-dch-whql.exe", p.FileName);
        Assert.Equal("NVIDIA Corporation", p.Signer);
        Assert.Null(p.Referer);
        Assert.Equal("1,04 ГБ", p.SizeDisplay);
        Assert.Equal("989,1 МБ", (p with { SizeBytes = 1037190000 }).SizeDisplay!.Replace('.', ','));
    }

    [Theory]
    [InlineData("http://us.download.nvidia.com/Windows/621.01/621.01-desktop-win10-win11-64bit-international-dch-whql.exe")]
    [InlineData("https://us.download.nvidia.com.evil.example/Windows/621.01/621.01-desktop-dch-whql.exe")]
    [InlineData("https://www.nvidia.com/download/driverResults.aspx/282412/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void Nvidia_download_url_outside_the_download_servers_is_dropped(string url)
    {
        var r = NvidiaLookup.ParseRelease(NvidiaJson.Replace(NvidiaUrl, url));
        Assert.NotNull(r);
        Assert.Equal("621.01", r!.Version);
        Assert.Null(r.DownloadUrl);
        Assert.Null(DriverDownloads.NvidiaPackage(r));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "Success" : "1", "IDS" : [ 5 ] }""")]
    [InlineData("""{ "Success" : "1", "IDS" : [ { "downloadInfo": "x" } ] }""")]
    public void Malformed_nvidia_answers_give_nothing(string json) =>
        Assert.Null(NvidiaLookup.ParseRelease(json));

    // ───────────── Signature ─────────────

    private static string SigJson(string status, string names) =>
        System.Text.Json.JsonSerializer.Serialize(new { Status = status, Subject = names.Replace("\r\n", ", "), Names = names });

    private const string NvidiaNames = "C=US\r\nS=California\r\nL=Santa Clara\r\nO=NVIDIA Corporation\r\nOU=2008B9F\r\nCN=NVIDIA Corporation";
    private const string AmdNames = "C=US\r\nS=California\r\nL=Santa Clara\r\nO=Advanced Micro Devices\r\nCN=Advanced Micro Devices";

    [Fact]
    public void Real_get_authenticode_signature_output_is_understood()
    {
        // Captured from Windows PowerShell 5.1 for an NVIDIA-signed executable.
        const string output = """{"Status":"Valid","Subject":"CN=NVIDIA Corporation, OU=2008B9F, O=NVIDIA Corporation, L=Santa Clara, S=California, C=US","Names":"C=US\r\nS=California\r\nL=Santa Clara\r\nO=NVIDIA Corporation\r\nOU=2008B9F\r\nCN=NVIDIA Corporation\r\n"}""";
        var (ok, signer) = DriverDownloads.EvaluateSignature(output, "NVIDIA Corporation");
        Assert.True(ok);
        Assert.Equal("подписан: NVIDIA Corporation", signer);
        Assert.False(DriverDownloads.EvaluateSignature(output, "Advanced Micro Devices").Ok);
    }

    [Fact]
    public void Valid_vendor_signature_is_accepted()
    {
        var (ok, signer) = DriverDownloads.EvaluateSignature(SigJson("Valid", NvidiaNames), "NVIDIA Corporation");
        Assert.True(ok);
        Assert.Contains("NVIDIA Corporation", signer);
        Assert.True(DriverDownloads.EvaluateSignature(SigJson("Valid", AmdNames), "Advanced Micro Devices").Ok);
        // PowerShell may prefix warnings before the JSON line.
        Assert.True(DriverDownloads.EvaluateSignature("WARNING: x\r\n" + SigJson("Valid", NvidiaNames), "NVIDIA Corporation").Ok);
    }

    [Theory]
    [InlineData("HashMismatch")]
    [InlineData("NotSigned")]
    [InlineData("NotTrusted")]
    [InlineData("UnknownError")]
    [InlineData("")]
    public void Invalid_signature_status_is_refused(string status)
    {
        var (ok, signer) = DriverDownloads.EvaluateSignature(SigJson(status, NvidiaNames), "NVIDIA Corporation");
        Assert.False(ok);
        Assert.Contains("статус", signer);
    }

    [Fact]
    public void Another_signer_is_refused_even_with_a_valid_signature()
    {
        var lookalike = NvidiaNames.Replace("O=NVIDIA Corporation", "O=Not NVIDIA Corporation Fans LLC").Replace("CN=NVIDIA Corporation", "CN=Not NVIDIA Corporation Fans LLC");
        Assert.False(DriverDownloads.EvaluateSignature(SigJson("Valid", lookalike), "NVIDIA Corporation").Ok);
        Assert.False(DriverDownloads.EvaluateSignature(SigJson("Valid", AmdNames), "NVIDIA Corporation").Ok);
        // Name only in OU / locality does not count.
        Assert.False(DriverDownloads.EvaluateSignature(SigJson("Valid", "OU=NVIDIA Corporation\r\nO=Evil Ltd\r\nCN=Evil Ltd"), "NVIDIA Corporation").Ok);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{ broken json")]
    [InlineData(null)]
    public void Unreadable_signature_output_is_refused(string? output) =>
        Assert.False(DriverDownloads.EvaluateSignature(output, "NVIDIA Corporation").Ok);

    [Theory]
    [InlineData("NVIDIA Corporation", "NVIDIA Corporation", true)]
    [InlineData("nvidia corporation", "NVIDIA Corporation", true)]
    [InlineData("Advanced Micro Devices, Inc.", "Advanced Micro Devices", true)]
    [InlineData("Advanced Micro Devices Inc.", "Advanced Micro Devices", true)]
    [InlineData("Advanced Micro Devices INC.", "Advanced Micro Devices", true)]
    [InlineData("NVIDIA", "NVIDIA Corporation", false)]
    [InlineData("Advanced Micro Devices Fan Club", "Advanced Micro Devices", false)]
    [InlineData("Not NVIDIA Corporation", "NVIDIA Corporation", false)]
    [InlineData("NVIDIA Corporation Ltd Inc", "NVIDIA Corporation", false)]
    [InlineData("", "NVIDIA Corporation", false)]
    [InlineData(null, "NVIDIA Corporation", false)]
    public void Signer_names_match_exactly_up_to_legal_form(string? actual, string expected, bool match) =>
        Assert.Equal(match, DriverDownloads.SignerMatches(actual, expected));

    [Fact]
    public void Quoted_distinguished_name_values_are_unquoted()
    {
        var f = DriverDownloads.ParseNameLines("C=US\r\nO=\"Advanced Micro Devices, Inc.\"\r\nCN=\"Advanced Micro Devices, Inc.\"\r\n");
        Assert.Equal("Advanced Micro Devices, Inc.", f["O"]);
        Assert.Equal("Advanced Micro Devices, Inc.", f["CN"]);
        Assert.Equal("US", f["C"]);
    }

    // ───────────── Progress and texts ─────────────

    [Fact]
    public void Progress_is_formatted_in_russian()
    {
        Assert.Equal(50, new DownloadProgress(512, 1024).Percent);
        Assert.Equal(0, new DownloadProgress(512, null).Percent);
        Assert.Equal(100, new DownloadProgress(2048, 1024).Percent);
        Assert.Contains(" из ", DriverDownloads.FormatProgress(new DownloadProgress(512L << 20, 1L << 30)));
        Assert.Equal("Проверка цифровой подписи…", DriverDownloads.FormatProgress(new DownloadProgress(10, 10) { Verifying = true }));
    }

    [Theory]
    [InlineData("1.04 GB", "1,04 ГБ")]
    [InlineData("650 MB", "650 МБ")]
    [InlineData("12,5 kb", "12,5 КБ")]
    [InlineData("huge", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Vendor_size_text_is_localized(string? text, string? expected) =>
        Assert.Equal(expected, DriverDownloads.LocalizeSize(text));

    [Fact]
    public void Confirmation_names_the_exact_source_signature_restore_point_and_clean_install()
    {
        var nv = DriverDownloads.NvidiaPackage(NvidiaLookup.ParseRelease(NvidiaJson)!)!;
        var lines = string.Join("\n", DriverDownloadText.Plan(nv, restorePointsEnabled: true));
        Assert.Contains("621.01", lines);
        Assert.Contains("1,04 ГБ", lines);
        Assert.Contains("официальный сервер NVIDIA us.download.nvidia.com", lines);
        Assert.Contains("цифровой подписи NVIDIA Corporation", lines);
        Assert.Contains("будет создана точка восстановления", lines);
        Assert.Contains("«Выборочная → Чистая установка»", lines);

        // System Protection off: no promise of a restore point.
        var off = string.Join("\n", DriverDownloadText.Plan(nv, restorePointsEnabled: false));
        Assert.DoesNotContain("будет создана точка восстановления", off);
        Assert.Contains("создана не будет", off);

        var amd = DriverDownloads.ParseAmdPage(AmdHtml)!;
        var amdLines = string.Join("\n", DriverDownloadText.Plan(amd, restorePointsEnabled: true));
        Assert.Contains("официальный сервер AMD drivers.amd.com", amdLines);
        Assert.Contains("сервер сообщит при загрузке", amdLines);
        Assert.Contains("веб-установщик", DriverDownloadText.Lead(amd));
    }
}
