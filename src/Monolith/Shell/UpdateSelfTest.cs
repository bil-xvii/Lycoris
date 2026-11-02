using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Monolith.Core;
using Monolith.Core.Updates;

namespace Monolith.Shell;

/// <summary>
/// The «--selftest» stage of the update machinery: a FAKE check and a FAKE download against an in-memory HTTP handler.
/// No network, no settings are touched (own store), no file outside a temp folder is written, nothing is ever replaced.
/// </summary>
internal static class UpdateSelfTest
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answer(request));
        }
    }

    private sealed class Store : IUpdateStore
    {
        public bool AutoCheck => true;
        public DateTime? LastCheckUtc { get; set; }
        public string ETag { get; set; } = "";
        public string DismissedVersion { get; set; } = "";
        public DateTime? SnoozedUntilUtc { get; set; }
        public string CachedRelease { get; set; } = "";
    }

    public static async Task RunAsync(List<string> exercised, List<string> errors)
    {
        const string baseUrl = "https://github.com/bil-xvii/Lycoris-releases/releases/download/v9.9.9/";
        var exe = new byte[8192];
        new Random(7).NextBytes(exe);
        exe[0] = (byte)'M'; exe[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(exe, 0x3C);
        exe[0x80] = (byte)'P'; exe[0x81] = (byte)'E'; exe[0x82] = 0; exe[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(exe, 0x84);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(exe, 0x98);
        var sha = Convert.ToHexStringLower(SHA256.HashData(exe));

        var json = "{\"tag_name\":\"v9.9.9\",\"draft\":false,\"prerelease\":false,\"body\":\"## Selftest\\n- fake\"," +
                   "\"html_url\":\"https://github.com/bil-xvii/Lycoris-releases/releases/tag/v9.9.9\",\"assets\":[" +
                   $"{{\"name\":\"Lycoris.exe\",\"size\":{exe.Length},\"digest\":\"sha256:{sha}\",\"browser_download_url\":\"{baseUrl}Lycoris.exe\"}}," +
                   $"{{\"name\":\"Lycoris.exe.sha256\",\"size\":80,\"browser_download_url\":\"{baseUrl}Lycoris.exe.sha256\"}}]}}";

        var handler = new Handler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.RequestUri.Host == "api.github.com")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (path.EndsWith(".sha256")) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha + "  Lycoris.exe\n") };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) { Headers = { ContentType = new("application/octet-stream") } } };
        });
        using var http = new HttpClient(handler);

        // 1. The check: finds 9.9.9, caches it, then the 12-hour throttle answers without a request.
        var now = DateTime.UtcNow;
        var store = new Store();
        var service = new UpdateService(http, store, () => AppVersion.Current, () => now);
        var check = await service.CheckAsync(manual: false);
        if (check.Status != UpdateCheckStatus.Available || check.Release?.VersionText != "9.9.9") errors.Add("fake check did not find the fake release: " + check.Status);
        var calls = handler.Calls;
        var again = await service.CheckAsync(manual: false);
        if (again.Status != UpdateCheckStatus.Skipped || handler.Calls != calls) errors.Add("the 12-hour throttle did not hold");
        service.Dismiss(check.Release!);
        if (service.ShouldAnnounce) errors.Add("a skipped version is still announced");
        exercised.Add($"fake update check: {check.Status}, version {check.Release?.VersionText}, throttled repeat: {again.Status}, requests: {handler.Calls}");

        // 2. The download into a temp folder: hash, size, PE and signer checks; then the plan for THIS exe (read-only look).
        var folder = Path.Combine(Path.GetTempPath(), "monolith-selftest-update-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var policy = new UpdatePolicy { MinSize = 1024, MaxSize = 1 << 20 };
            var downloader = new UpdateDownloader(http, policy, _ => Task.FromResult(new SignatureInfo("NotSigned", "", "")), AppPaths.ExecutablePath);
            var staged = await downloader.DownloadAsync(check.Release!, folder, null, CancellationToken.None);
            if (staged.Sha256 != sha || !File.Exists(staged.Path)) errors.Add("fake download did not verify");
            exercised.Add($"fake download verified: {staged.Size} bytes, SHA-256 {staged.Sha256[..12]}…");

            var plan = UpdateApplier.Plan(new SystemUpdateEnvironment(), AppPaths.ExecutablePath, Elevation.IsElevated);
            exercised.Add("update route for this exe (nothing is replaced): " + plan.Route);
        }
        catch (Exception ex)
        {
            errors.Add("fake download failed: " + ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
