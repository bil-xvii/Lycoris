using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lycoris.Core.Assistant;

/// <summary>A model the local Ollama server has: its name («qwen3:4b») and size on disk.</summary>
public sealed record LocalModel(string Name, long SizeBytes, string? ParameterSize);

/// <summary>One line of the conversation sent to the model.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>
/// The «Помощник» talks only to Ollama on THIS computer (http://127.0.0.1:11434): no proxy, no other host, nothing leaves the PC.
/// The address is fixed on purpose — an environment variable or a setting cannot point it somewhere else.
/// </summary>
public sealed class LocalModelClient : IDisposable
{
    public static readonly Uri Endpoint = new("http://127.0.0.1:11434/");
    private readonly HttpClient _http;

    public LocalModelClient(HttpMessageHandler? handler = null)
    {
        // UseProxy = false: a system proxy must never see the PC facts, even on loopback.
        handler ??= new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(2) };
        _http = new HttpClient(handler) { BaseAddress = Endpoint, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    }

    /// <summary>The installed models, or null when Ollama does not answer (not installed or not running).</summary>
    public async Task<IReadOnlyList<LocalModel>?> ListModelsAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var response = await _http.GetAsync("api/tags", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return ParseModels(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>Pure: the models from an /api/tags answer (tested).</summary>
    internal static IReadOnlyList<LocalModel> ParseModels(string json)
    {
        var list = new List<LocalModel>();
        if (JsonNode.Parse(json)?["models"] is not JsonArray models) return list;
        foreach (var m in models)
        {
            var name = m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            long size = 0;
            try { size = m?["size"]?.GetValue<long>() ?? 0; } catch (Exception ex) when (ex is FormatException or InvalidOperationException) { }
            list.Add(new LocalModel(name, size, m?["details"]?["parameter_size"]?.GetValue<string>()));
        }
        return list;
    }

    /// <summary>
    /// Streams the answer: <paramref name="onText"/> gets each new piece as it arrives. Returns the whole answer.
    /// Throws <see cref="OperationCanceledException"/> on «СТОП» and <see cref="HttpRequestException"/> when Ollama fails.
    /// </summary>
    public async Task<string> ChatAsync(string model, IReadOnlyList<ChatTurn> turns, Action<string> onText, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["stream"] = true,
            ["keep_alive"] = "10m",
            ["options"] = new JsonObject { ["temperature"] = 0.3, ["num_ctx"] = 8192 },
            ["messages"] = new JsonArray(turns.Select(t => (JsonNode)new JsonObject { ["role"] = t.Role, ["content"] = t.Content }).ToArray()),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(ErrorText(error) ?? $"HTTP {(int)response.StatusCode}");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var all = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0) continue;
            var (piece, done, failure) = ParseChunk(line);
            if (failure is not null) throw new HttpRequestException(failure);
            if (piece.Length > 0) { all.Append(piece); onText(piece); }
            if (done) break;
        }
        return all.ToString();
    }

    /// <summary>Pure: one NDJSON line of a streamed /api/chat answer (tested).</summary>
    internal static (string Text, bool Done, string? Error) ParseChunk(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["error"]?.GetValue<string>() is { Length: > 0 } error) return ("", true, error);
            return (node?["message"]?["content"]?.GetValue<string>() ?? "", node?["done"]?.GetValue<bool>() ?? false, null);
        }
        catch (JsonException) { return ("", false, null); }
    }

    private static string? ErrorText(string json)
    {
        try { return JsonNode.Parse(json)?["error"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();
}
