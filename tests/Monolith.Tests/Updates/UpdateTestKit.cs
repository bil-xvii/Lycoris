using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Monolith.Core;
using Monolith.Core.Updates;

namespace Monolith.Tests.Updates;

/// <summary>An HttpMessageHandler that answers from a function and remembers every request (no network, ever).</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(answer(request));
    }

    public static HttpResponseMessage Json(string json, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return r;
    }

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };

    public static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }

    public static HttpResponseMessage Bytes(byte[] data) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) { Headers = { ContentType = new("application/octet-stream") } } };
}

internal sealed class FakeStore : IUpdateStore
{
    public bool AutoCheck { get; set; } = true;
    public DateTime? LastCheckUtc { get; set; }
    public string ETag { get; set; } = "";
    public string DismissedVersion { get; set; } = "";
    public DateTime? SnoozedUntilUtc { get; set; }
    public string CachedRelease { get; set; } = "";
}

internal sealed class Clock(DateTime start)
{
    public DateTime Now { get; set; } = start;
    public DateTime Get() => Now;
}

internal static class Kit
{
    public const string BaseUrl = "https://github.com/bil-xvii/MONOLITH-releases/releases/download/";

    /// <summary>A minimal but valid PE32+ x64 header padded with <paramref name="seed"/>-dependent bytes up to <paramref name="size"/>.</summary>
    public static byte[] FakePe(int size, byte seed = 1)
    {
        var b = new byte[size];
        for (var i = 0; i < b.Length; i++) b[i] = (byte)(i * 31 + seed);
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3C);
        b[0x80] = (byte)'P'; b[0x81] = (byte)'E'; b[0x82] = 0; b[0x83] = 0;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(b, 0x84);
        BitConverter.GetBytes((ushort)0x20B).CopyTo(b, 0x98);
        return b;
    }

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string ReleaseJson(string tag = "v1.0.1", long size = 2048, string? digest = null, bool sha = true,
        bool draft = false, bool prerelease = false, string notes = "## What's new\n- one", bool exe = true) =>
        "{" +
        $"\"tag_name\":\"{tag}\",\"name\":\"MONOLITH {tag}\",\"draft\":{(draft ? "true" : "false")},\"prerelease\":{(prerelease ? "true" : "false")}," +
        $"\"html_url\":\"https://github.com/bil-xvii/MONOLITH-releases/releases/tag/{tag}\"," +
        $"\"body\":{System.Text.Json.JsonSerializer.Serialize(notes)},\"assets\":[" +
        (exe ? "{\"name\":\"MONOLITH.exe\",\"state\":\"uploaded\",\"size\":" + size + "," +
               (digest is null ? "" : $"\"digest\":\"sha256:{digest}\",") +
               $"\"browser_download_url\":\"{BaseUrl}{tag}/MONOLITH.exe\"}}" : "") +
        (exe && sha ? "," : "") +
        (sha ? $"{{\"name\":\"MONOLITH.exe.sha256\",\"state\":\"uploaded\",\"size\":80,\"browser_download_url\":\"{BaseUrl}{tag}/MONOLITH.exe.sha256\"}}" : "") +
        "]}";

    public static ReleaseInfo Release(string tag = "v1.0.1", long size = 2048, string? digest = null, bool sha = true) =>
        ReleaseParser.TryParse(ReleaseJson(tag, size, digest, sha), out _)!;

    public static UpdatePolicy SmallPolicy => new() { MinSize = 1024, MaxSize = 1 << 20 };

    public static string TempDir(string name)
    {
        var d = Path.Combine(Path.GetTempPath(), "monolith-update-tests", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }
}
