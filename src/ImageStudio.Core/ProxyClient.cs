using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImageStudio.Core;

public sealed class ProxyException : Exception
{
    public int StatusCode { get; }
    public bool ProxyDown { get; }
    public ProxyException(string message, int statusCode = 0, bool proxyDown = false, Exception? inner = null)
        : base(message, inner) { StatusCode = statusCode; ProxyDown = proxyDown; }

    public string UserMessage => ProxyDown ? "프록시에 연결할 수 없습니다. Vertex OpenAI Proxy 앱을 실행하세요."
        : StatusCode == 429 ? "쿼터 초과 (429). 잠시 후 다시 시도하세요."
        : StatusCode is 403 or 404 ? $"이 모델을 사용할 권한이 없거나 찾을 수 없습니다 ({StatusCode}): {Message}"
        : Message;
}

public sealed record ModelInfo(string Id, string Type, bool Vision, bool Reasoning);

public sealed record ChatResult(string Content, string? Reasoning, int PromptTokens, int CachedTokens, int CompletionTokens)
{
    /// <summary>Options the proxy could not apply for this model (response field <c>vertex_dropped</c>).</summary>
    public List<string> Dropped { get; init; } = new();
}

/// <summary>Thin HttpClient wrapper for the Vertex OpenAI Proxy (docs/client-app-dev-guide.md). No UI dependencies.</summary>
public sealed class ProxyClient : IDisposable
{
    readonly HttpClient _http;

    public ProxyClient(string baseUrl, int timeoutSeconds = 300)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "local");
    }

    public void Dispose() => _http.Dispose();

    public async Task<List<ModelInfo>> ListModelsAsync(string type, bool refresh = false, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, $"v1/models?type={type}{(refresh ? "&refresh=1" : "")}", null, ct);
        var list = new List<ModelInfo>();
        foreach (var m in JsonNode.Parse(json)?["data"]?.AsArray() ?? new JsonArray())
        {
            var caps = m?["capabilities"];
            list.Add(new ModelInfo((string)m!["id"]!, (string?)m["type"] ?? type,
                (bool?)caps?["vision"] ?? false, (bool?)caps?["reasoning"] ?? false));
        }
        return list;
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { await ListModelsAsync("image", false, ct); return true; }
        catch { return false; }
    }

    /// <summary>POST /v1/chat/completions. With <paramref name="stream"/>, text deltas are reported through <paramref name="onDelta"/>.</summary>
    public async Task<ChatResult> ChatAsync(JsonObject body, bool stream, Action<string>? onDelta = null, CancellationToken ct = default)
    {
        body["stream"] = stream;
        if (!stream)
        {
            var json = await SendAsync(HttpMethod.Post, "v1/chat/completions", body, ct);
            var root = JsonNode.Parse(json)!;
            var msg = root["choices"]?[0]?["message"];
            var (p, c, o) = Usage(root["usage"]);
            return new ChatResult((string?)msg?["content"] ?? "", (string?)msg?["reasoning_content"], p, c, o)
            { Dropped = Strings(root["vertex_dropped"]) };
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        using var res = await Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOk(res, ct);
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        int pt = 0, ctk = 0, ot = 0;
        var dropped = new List<string>();
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data:")) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); } catch { continue; }
            if (chunk?["error"] is JsonNode err) throw new ProxyException((string?)err["message"] ?? "스트리밍 오류");
            var delta = chunk?["choices"]?[0]?["delta"];
            if ((string?)delta?["content"] is { Length: > 0 } d) { content.Append(d); onDelta?.Invoke(d); }
            if ((string?)delta?["reasoning_content"] is { Length: > 0 } r) reasoning.Append(r);
            if (chunk?["usage"] is JsonNode u) (pt, ctk, ot) = Usage(u);
            if (chunk?["vertex_dropped"] is JsonNode vd) dropped.AddRange(Strings(vd));
        }
        return new ChatResult(content.ToString(), reasoning.Length > 0 ? reasoning.ToString() : null, pt, ctk, ot)
        { Dropped = dropped.Distinct().ToList() };
    }

    static List<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() : new();

    static (int, int, int) Usage(JsonNode? u) => u == null ? (0, 0, 0) : (
        (int?)u["prompt_tokens"] ?? 0,
        (int?)u["prompt_tokens_details"]?["cached_tokens"] ?? 0,
        (int?)u["completion_tokens"] ?? 0);

    async Task<string> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Send(req, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureOk(res, ct);
        return await res.Content.ReadAsStringAsync(ct);
    }

    async Task<HttpResponseMessage> Send(HttpRequestMessage req, HttpCompletionOption opt, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, opt, ct); }
        catch (HttpRequestException e) when (e.StatusCode == null)
        { throw new ProxyException(e.Message, 0, proxyDown: true, e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        { throw new ProxyException($"요청 시간 초과 ({_http.Timeout.TotalSeconds:0}초)", 408, false, e); }
    }

    static async Task EnsureOk(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        string msg = text;
        try { msg = (string?)JsonNode.Parse(text)?["error"]?["message"] ?? text; } catch { }
        throw new ProxyException(string.IsNullOrWhiteSpace(msg) ? res.ReasonPhrase ?? "오류" : msg, (int)res.StatusCode);
    }
}

/// <summary>Maps <see cref="GenerationSettings"/> onto the request body.</summary>
public static class ImageRequest
{
    /// <summary>
    /// Standard fields (model, aspect_ratio, n, max_tokens) are understood by the current proxy.
    /// Everything else travels in the <c>vertex</c> extension object using Vertex field names,
    /// so the proxy can copy it into generationConfig / imageConfig / tools.
    /// </summary>
    public static JsonObject Build(GenerationSettings s, JsonArray messages)
    {
        var imageConfig = new JsonObject
        {
            ["aspectRatio"] = s.EffectiveAspectRatio,
            ["imageSize"] = s.Size switch { ImageSize.K1 => "1K", ImageSize.K4 => "4K", _ => "2K" },
            ["imageOutputOptions"] = new JsonObject
            {
                ["mimeType"] = s.Format == OutputFormat.Jpeg ? "image/jpeg" : "image/png",
            },
            ["personGeneration"] = s.Person switch
            {
                PersonGeneration.AllowAll => "ALLOW_ALL",
                PersonGeneration.DontAllow => "ALLOW_NONE",
                _ => "ALLOW_ADULT",
            },
        };
        var generationConfig = new JsonObject
        {
            ["responseModalities"] = s.Output == OutputModality.ImageOnly ? new JsonArray("IMAGE") : new JsonArray("TEXT", "IMAGE"),
            ["maxOutputTokens"] = s.MaxOutputTokens,
            ["thinkingConfig"] = new JsonObject
            {
                ["thinkingLevel"] = s.Thinking.ToString().ToUpperInvariant(),
                ["includeThoughts"] = true,
            },
            ["imageConfig"] = imageConfig,
        };
        if (s.MediaResolution != MediaResolution.Default)
            generationConfig["mediaResolution"] = "MEDIA_RESOLUTION_" + s.MediaResolution.ToString().ToUpperInvariant();

        var tools = new JsonArray();
        if (s.GoogleSearch)
        {
            var search = new JsonObject();
            if (s.GoogleSearchServices) search["searchTypes"] = new JsonObject { ["webSearch"] = new JsonObject(), ["imageSearch"] = new JsonObject() };
            tools.Add(new JsonObject { ["googleSearch"] = search });
        }
        if (s.Partner)
            tools.Add(new JsonObject { ["partnerGrounding"] = new JsonObject { ["includePartnerResults"] = s.PartnerResults } });

        var vertex = new JsonObject
        {
            ["location"] = s.Region,
            ["generationConfig"] = generationConfig,
            ["celebrityGeneration"] = s.AllowCelebrities ? "ALLOW" : "BLOCK",
        };
        if (tools.Count > 0) vertex["tools"] = tools;

        return new JsonObject
        {
            ["model"] = s.Model,
            ["aspect_ratio"] = s.EffectiveAspectRatio,
            ["n"] = 1,
            ["max_tokens"] = s.MaxOutputTokens,
            ["messages"] = messages,
            ["vertex"] = vertex,
        };
    }

    /// <summary>Short label stored with each result, e.g. "2:3 · 2K · png".</summary>
    public static string Label(GenerationSettings s) =>
        $"{s.EffectiveAspectRatio} · {s.Size switch { ImageSize.K1 => "1K", ImageSize.K4 => "4K", _ => "2K" }} · {(s.Format == OutputFormat.Jpeg ? "jpeg" : "png")}";

    /// <summary>Cache key over model + messages + options (for "same request" reuse).</summary>
    public static string CacheKey(JsonObject body)
    {
        var clone = (JsonObject)body.DeepClone();
        clone.Remove("stream");
        return Store.Sha1Hex(clone.ToJsonString());
    }
}
