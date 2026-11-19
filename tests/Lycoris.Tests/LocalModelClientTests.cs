using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Lycoris.Core.Assistant;
using Xunit;

namespace Lycoris.Tests;

public sealed class LocalModelClientTests
{
    private const string Model = "qwen3:4b";
    private static readonly ChatTurn[] Turns = [new("user", "question")];
    private static string Tags(string name = Model, string extra = "", string digest = "") =>
        $$"""{"models":[{"name":"{{name}}","model":"{{name}}","size":2000000000,"digest":"{{(digest.Length == 0 ? new string('a', 64) : digest)}}","details":{"format":"gguf","parameter_size":"4.0B"}{{extra}}}]}""";

    [Fact]
    public async Task Chat_checks_exact_tag_and_uses_only_local_source_with_bounded_options()
    {
        var paths = new List<string>();
        using var client = new LocalModelClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(LocalModelClient.Endpoint.Authority, request.RequestUri!.Authority);
            var path = request.RequestUri.AbsolutePath;
            paths.Add(path);
            if (path == "/api/tags")
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                return Reply(Tags());
            }
            Assert.Equal(HttpMethod.Post, request.Method);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal(Model + ":local", root.GetProperty("model").GetString());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.False(root.GetProperty("think").GetBoolean());
            Assert.Equal("45s", root.GetProperty("keep_alive").GetString());
            Assert.Equal(4096, root.GetProperty("options").GetProperty("num_ctx").GetInt32());
            Assert.Equal(768, root.GetProperty("options").GetProperty("num_predict").GetInt32());
            return Reply("{\"message\":{\"content\":\"да\"},\"done\":false}\r\n{\"done\":true}");
        }));
        var pieces = new List<string>();
        Assert.Equal("да", await client.ChatAsync(Model, Turns, pieces.Add, default));
        Assert.Equal(["да"], pieces);
        Assert.Equal(["/api/tags", "/api/chat"], paths);
    }

    [Theory]
    [InlineData("Qwen3:4b", false)]
    [InlineData("registry.example:5000/team/_model:4b", true)]
    public async Task Exact_tag_is_required_including_for_a_private_registry_name(string selected, bool installed)
    {
        var listed = installed ? selected : Model;
        using var client = new LocalModelClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(LocalModelClient.Endpoint, new Uri(request.RequestUri!.GetLeftPart(UriPartial.Authority)));
            if (request.RequestUri.AbsolutePath == "/api/tags") return Reply(Tags(listed));
            Assert.True(installed);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(selected + ":local", body.RootElement.GetProperty("model").GetString());
            return Reply("{\"done\":true}");
        }));
        if (installed)
            Assert.Equal("", await client.ChatAsync(selected, Turns, _ => { }, default));
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(selected, Turns, _ => { }, default));
    }

    [Theory]
    [InlineData("qwen3:cloud", "", "")]
    [InlineData("qwen3:4b-cloud", "", "")]
    [InlineData("cloud", "", "")]
    [InlineData(Model, ",\"remote_model\":\"some-model\"", "")]
    [InlineData(Model, ",\"remote_host\":\"https://ollama.com\"", "")]
    [InlineData(Model, ",\"remote_host\":false", "")]
    [InlineData(Model, "", "unknown")]
    public async Task Cloud_or_unproven_entries_are_not_listed_or_sent(string name, string extra, string digest)
    {
        using var client = new LocalModelClient(new Handler((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Tags(name, extra, digest)));
        }));
        Assert.Empty((await client.ListModelsAsync())!);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(name, Turns, _ => { }, default));
    }

    [Theory]
    [InlineData("{\"models\":[]}")]
    [InlineData("{\"models\":[false,{\"name\":42}]}")]
    [InlineData("{\"models\":[{\"name\":\"qwen3:4b\",\"details\":{\"format\":\"gguf\"},\"size\":42}]}")]
    public async Task Removed_or_malformed_tags_after_listing_block_chat(string fresh)
    {
        var count = 0;
        using var client = new LocalModelClient(new Handler((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(++count == 1 ? Tags() : fresh));
        }));
        Assert.Single((await client.ListModelsAsync())!);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(Model, Turns, _ => { }, default));
        Assert.Equal(2, count);
    }

    [Theory]
    [InlineData("malformed", "ollama_invalid_response")]
    [InlineData("{\"done\":\"true\"}", "ollama_invalid_response")]
    [InlineData("{\"message\":{\"content\":\"test\"},\"done\":false}\n", "ollama_incomplete_response")]
    [InlineData("{\"error\":\"private server text\"}", "ollama_chat_failed")]
    [InlineData("{\"done\":true,\"remote_model\":\"cloud-model\"}", "ollama_chat_failed")]
    public async Task Stream_rejects_malformed_unfinished_or_error_frames(string body, string code)
    {
        using var client = ChatServer(Reply(body));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(Model, Turns, _ => { }, default));
        Assert.Equal(code, failure.Message);
    }

    [Fact]
    public async Task Refuses_redirect_and_does_not_expose_response_body()
    {
        var redirect = Reply("private server text", HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri("https://ollama.com/api/chat");
        using var client = ChatServer(redirect);
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(Model, Turns, _ => { }, default));
        Assert.Equal("ollama_chat_failed", failure.Message);
        var handler = LocalModelClient.CreateLoopbackHandler();
        using (handler)
        {
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseProxy);
        }
    }

    [Fact]
    public async Task Cancelling_chat_cancels_the_pending_request()
    {
        using var stop = new CancellationTokenSource();
        using var client = new LocalModelClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags") return Reply(Tags());
            stop.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Reply("");
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(Model, Turns, _ => { }, stop.Token));
    }

    [Fact]
    public async Task Cancelling_during_streaming_does_not_return_a_partial_answer_as_success()
    {
        using var stop = new CancellationTokenSource();
        using var client = ChatServer(Reply("{\"message\":{\"content\":\"part\"},\"done\":false}\n{\"done\":true}\n"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ChatAsync(Model, Turns, _ => stop.Cancel(), stop.Token));
    }

    [Theory]
    [InlineData(33000, 1)]
    [InlineData(5000, 2)]
    [InlineData(0, 20000)]
    public async Task Bounds_frames_answer_and_total_stream(int pieceLength, int frames)
    {
        var body = string.Concat(Enumerable.Repeat("{\"message\":{\"content\":\"" + new string('x', pieceLength) + "\"},\"done\":false}\n", frames));
        using var client = ChatServer(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new UnseekableBuffer(System.Text.Encoding.UTF8.GetBytes(body))),
        });
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ChatAsync(Model, Turns, _ => { }, default));
        Assert.Equal("ollama_response_too_large", failure.Message);
    }

    [Fact]
    public async Task Huge_outgoing_payload_is_rejected_before_chat()
    {
        using var client = new LocalModelClient(new Handler((request, _) =>
        {
            Assert.Equal("/api/tags", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Tags()));
        }));
        var failure = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ChatAsync(Model, [new("user", new string('ю', 30000))], _ => { }, default));
        Assert.Equal("ollama_request_too_large", failure.Message);
    }

    private static LocalModelClient ChatServer(HttpResponseMessage response) =>
        new(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/tags" ? Reply(Tags()) : response)));

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private sealed class UnseekableBuffer(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
