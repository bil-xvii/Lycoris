using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lycoris.Core.Assistant;

/// <summary>An installed Ollama tag with its disk size and manifest metadata.</summary>
public sealed record LocalModel(string Name, long SizeBytes, string? ParameterSize, string? Digest = null, string? Format = null);

/// <summary>One line of the conversation sent to the model.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>Chats only with a freshly verified installed model over Ollama's loopback endpoint.</summary>
public sealed class LocalModelClient : IDisposable
{
    public static readonly Uri Endpoint = new("http://127.0.0.1:11434/");
    private const int MaxTurns = 8;
    private const int MaxInputChars = 32 * 1024;
    private const int MaxRequestBytes = 64 * 1024;
    private const int MaxFrameBytes = 32 * 1024;
    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxAnswerChars = 8192;
    private const string ChatFailed = "ollama_chat_failed";
    private const string InvalidResponse = "ollama_invalid_response";
    private const string ResponseTooLarge = "ollama_response_too_large";
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };
    private readonly HttpClient _http;

    public LocalModelClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? CreateLoopbackHandler())
        {
            BaseAddress = Endpoint, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024,
        };
    }

    internal static SocketsHttpHandler CreateLoopbackHandler() =>
        new() { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };

    /// <summary>The eligible installed models, or null if Ollama cannot provide a usable tags response.</summary>
    public async Task<IReadOnlyList<LocalModel>?> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/tags");
            request.Headers.CacheControl = new() { NoCache = true };
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseModels(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    internal static IReadOnlyList<LocalModel> ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json, StrictJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("models", out var models) ||
            models.ValueKind != JsonValueKind.Array) throw new JsonException();

        var list = new List<LocalModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in models.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = StringProperty(entry, "name") ?? StringProperty(entry, "model");
            if (name is null) continue;
            if (!seen.Add(name)) ambiguous.Add(name);
            if (entry.TryGetProperty("model", out _) && StringProperty(entry, "model") != name) continue;
            if (entry.TryGetProperty("name", out _) && StringProperty(entry, "name") != name) continue;
            if (entry.TryGetProperty("remote_model", out _) || entry.TryGetProperty("remote_host", out _)) continue;
            if (!entry.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number ||
                !size.TryGetInt64(out var sizeBytes) ||
                !entry.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object) continue;
            var model = new LocalModel(name, sizeBytes, StringProperty(details, "parameter_size"),
                StringProperty(entry, "digest"), StringProperty(details, "format"));
            if (AssistantBrain.IsChatModel(model)) list.Add(model);
        }
        list.RemoveAll(model => ambiguous.Contains(model.Name));
        return list;
    }

    private static string? StringProperty(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Streams the local answer. An unfinished response is an error. Caller cancellation propagates.</summary>
    public async Task<string> ChatAsync(string model, IReadOnlyList<ChatTurn> turns, Action<string> onText, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var body = BuildRequest(model, turns);
            var installed = await ListModelsAsync(cts.Token).ConfigureAwait(false);
            if (installed is null || !installed.Any(m => m.Name == model)) throw new ChatFailure("ollama_local_model_unavailable");
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ChatFailure(ChatFailed);
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new ChatFailure(ResponseTooLarge);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await ReadAnswerAsync(stream, onText, cts.Token).ConfigureAwait(false);
        }
        catch (ChatFailure failure)
        {
            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException(failure.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException("ollama_chat_timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            throw new HttpRequestException(ChatFailed);
        }
    }

    private static byte[] BuildRequest(string model, IReadOnlyList<ChatTurn> turns)
    {
        if (!AssistantBrain.IsLocalTag(model)) throw new ChatFailure("ollama_local_model_unavailable");
        if (turns.Count is 0 or > MaxTurns || turns.Any(t => t.Role is not ("system" or "user" or "assistant")) ||
            turns.Sum(t => (long)t.Content.Length) > MaxInputChars) throw new ChatFailure("ollama_request_too_large");

        var body = new JsonObject
        {
            // An explicit source makes the server fail closed if the tag is removed or replaced after /api/tags.
            ["model"] = model + ":local",
            ["stream"] = true,
            ["think"] = false,
            ["keep_alive"] = "45s",
            ["options"] = new JsonObject { ["temperature"] = 0.3, ["num_ctx"] = 4096, ["num_predict"] = 768 },
            ["messages"] = new JsonArray(turns.Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray()),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        if (bytes.Length > MaxRequestBytes) throw new ChatFailure("ollama_request_too_large");
        return bytes;
    }

    private static async Task<string> ReadAnswerAsync(Stream stream, Action<string> onText, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var frame = new byte[MaxFrameBytes];
        var frameLength = 0;
        var total = 0;
        var answer = new StringBuilder();
        int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > MaxResponseBytes) throw new ChatFailure(ResponseTooLarge);
            var offset = 0;
            while (offset < count)
            {
                var remaining = buffer.AsSpan(offset, count - offset);
                var newline = remaining.IndexOf((byte)'\n');
                var length = newline >= 0 ? newline : remaining.Length;
                if (length > frame.Length - frameLength) throw new ChatFailure(ResponseTooLarge);
                remaining[..length].CopyTo(frame.AsSpan(frameLength));
                frameLength += length;
                offset += length;
                if (newline < 0) continue;
                offset++;
                if (AppendFrame()) return answer.ToString();
                frameLength = 0;
            }
        }
        if (AppendFrame()) return answer.ToString();
        throw new ChatFailure("ollama_incomplete_response");

        bool AppendFrame()
        {
            ct.ThrowIfCancellationRequested();
            if (frameLength == 0 || frameLength == 1 && frame[0] == '\r') return false;
            var (piece, done, failure) = ParseChunk(frame.AsMemory(0, frameLength));
            if (failure is not null) throw new ChatFailure(failure);
            if (piece.Length > MaxAnswerChars - answer.Length) throw new ChatFailure(ResponseTooLarge);
            if (piece.Length > 0)
            {
                answer.Append(piece);
                onText(piece);
                ct.ThrowIfCancellationRequested();
            }
            return done;
        }
    }

    internal static (string Text, bool Done, string? Error) ParseChunk(string line) => ParseChunk(Encoding.UTF8.GetBytes(line));

    private static (string Text, bool Done, string? Error) ParseChunk(ReadOnlyMemory<byte> line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line, StrictJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ("", false, InvalidResponse);
            if (root.TryGetProperty("error", out _) || root.TryGetProperty("remote_model", out _) ||
                root.TryGetProperty("remote_host", out _)) return ("", false, ChatFailed);
            if (!root.TryGetProperty("done", out var done) || done.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ("", false, InvalidResponse);
            if (!root.TryGetProperty("message", out var message))
                return done.GetBoolean() ? ("", true, null) : ("", false, InvalidResponse);
            var text = message.ValueKind == JsonValueKind.Object ? StringProperty(message, "content") : null;
            return text is null ? ("", false, InvalidResponse) : (text, done.GetBoolean(), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ("", false, InvalidResponse);
        }
    }

    private sealed class ChatFailure(string code) : Exception(code);

    public void Dispose() => _http.Dispose();
}
