using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ImageStudio.Core;

/// <summary>
/// Conversation engine: prompt enhance → history build → image request → persist.
/// Also /compact and context estimation. UI-free so it can be reused (e.g. ComfyUI node port).
/// </summary>
public sealed class StudioService
{
    readonly Store _store;
    readonly Func<AppSettings> _settings;
    readonly Func<ProxyClient> _client;
    readonly HistoryBuilder _history;

    public StudioService(Store store, Func<AppSettings> settings, Func<ProxyClient> client, HistoryBuilder? history = null)
    {
        _store = store;
        _settings = settings;
        _client = client;
        _history = history ?? HistoryBuilder.FromFiles();
    }

    public const string CompactCommand = "/compact";

    // ---------------- context estimate ----------------

    /// <summary>Rough token estimate of what the next request would carry (chars/3 for text, per-image cost by size).</summary>
    public static int EstimateTokens(IReadOnlyList<Message> all, int maxImages)
    {
        var (summary, turns) = HistoryBuilder.ActiveTurns(all);
        var msgs = new List<Message>();
        if (summary != null) msgs.Add(summary);
        msgs.AddRange(turns);
        int text = msgs.Sum(m => (m.Role == Roles.User ? m.PromptForModel : m.Text).Length) / 3;
        var imgs = msgs.SelectMany(m => m.Images).ToList();
        int inlined = imgs.Skip(Math.Max(0, imgs.Count - maxImages)).Sum(i => ImageTokens(i));
        int placeholders = Math.Max(0, imgs.Count - maxImages) * 12;
        return text + inlined + placeholders;
    }

    static int ImageTokens(ImageRef i) => Math.Max(i.Width, i.Height) switch
    {
        > 3000 => 2000,
        > 1500 => 1500,
        _ => 1120,
    };

    // ---------------- prompt enhance ----------------

    const string EnhanceSystem = """
        You rewrite image-generation requests into a single, vivid, production-ready prompt for Google's Gemini image model.
        - Keep every concrete instruction from the user (subject, text to render, colors, layout, edits to the previous image).
        - If the request edits a previous result, phrase it as an edit ("Keep the composition, change …").
        - Add lighting, composition, lens/style and material detail only where it helps; never contradict the user.
        - Text that must appear in the image stays in its original language, in quotes.
        - Reply with the prompt only. No preface, no markdown, no quotes around the whole prompt.
        """;

    public async Task<string> EnhanceAsync(IReadOnlyList<Message> all, string prompt, CancellationToken ct = default)
    {
        var s = _settings();
        var (summary, turns) = HistoryBuilder.ActiveTurns(all);
        var context = new List<string>();
        if (summary != null) context.Add("Conversation summary: " + summary.Text);
        foreach (var m in turns.Where(t => t.Role == Roles.User).TakeLast(4))
            context.Add("Earlier request: " + m.PromptForModel);
        var user = (context.Count > 0 ? string.Join("\n", context) + "\n\n" : "") + "New request: " + prompt;

        var body = new JsonObject
        {
            ["model"] = s.EnhanceModel,
            ["temperature"] = 0.6,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = EnhanceSystem },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        var r = await _client().ChatAsync(body, stream: false, ct: ct);
        var text = StripThink(r.Content).Trim().Trim('"');
        return string.IsNullOrWhiteSpace(text) ? prompt : text;
    }

    static string StripThink(string s)
    {
        var i = s.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? s[(i + 8)..] : s;
    }

    // ---------------- generate ----------------

    public sealed record SendRequest(
        Conversation Conversation,
        string Prompt,
        IReadOnlyList<(byte[] Bytes, string Mime)> Attachments,
        string? ContinueFromImageId = null,
        bool BypassCache = false);

    /// <summary>Creates and persists the user + pending assistant messages, so the UI can show them immediately.</summary>
    public (Message User, Message Assistant) BeginTurn(SendRequest req, List<Message> all)
    {
        var conv = req.Conversation;
        if (conv.MessageCount == 0 && all.Count == 0)
        {
            var t = req.Prompt.ReplaceLineEndings(" ").Trim();
            conv.Title = t.Length > 40 ? t[..40] : (t.Length == 0 ? L.T("새 대화") : t);
        }
        conv.Model = _settings().Generation.Model;
        conv.UpdatedAt = Conversation.Now();
        _store.SaveConversation(conv);

        int seq = all.Count == 0 ? 1 : all.Max(m => m.Seq) + 1;
        var user = new Message
        {
            ConversationId = conv.Id, Seq = seq, Role = Roles.User, Text = req.Prompt,
            ContinueFromImageId = req.ContinueFromImageId,
        };
        int idx = 0;
        foreach (var (bytes, mime) in req.Attachments)
        {
            var img = _store.SaveImageBytes(conv.Id, bytes, mime, ImageKind.Attached);
            img.Idx = idx++;
            user.Images.Add(img);
        }
        var asst = new Message
        {
            ConversationId = conv.Id, Seq = seq + 1, Role = Roles.Assistant,
            Model = _settings().Generation.Model, Status = MessageStatus.Pending,
            SettingsLabel = ImageRequest.Label(_settings().Generation),
        };
        _store.SaveMessage(user);
        _store.SaveMessage(asst);
        all.Add(user);
        all.Add(asst);
        conv.MessageCount++;
        return (user, asst);
    }

    /// <summary>Runs enhance (if on) + the image request for a turn created by <see cref="BeginTurn"/>.</summary>
    public async Task CompleteTurnAsync(Conversation conv, List<Message> all, Message user, Message asst,
        bool bypassCache, Action<string>? onPhase = null, Action<string>? onDelta = null, CancellationToken ct = default)
    {
        var s = _settings();
        var gen = s.Generation.Clone();
        var sw = Stopwatch.StartNew();
        try
        {
            if (s.EnhanceEnabled && !ModelCatalog.IsImagen(gen.Model) && string.IsNullOrWhiteSpace(user.Enhanced))
            {
                onPhase?.Invoke(L.T("프롬프트 강화 중…"));
                try
                {
                    var prior = all.Where(m => m.Seq < user.Seq).ToList();
                    user.Enhanced = await EnhanceAsync(prior, user.Text, ct);
                    _store.SaveMessage(user);
                }
                catch (ProxyException) { /* enhance is best-effort: fall back to the raw prompt */ }
            }

            onPhase?.Invoke(L.T("이미지 생성 중…"));
            var history = _history.Build(all.Where(m => m.Seq <= user.Seq).ToList(),
                s.HistoryImageCount, s.SystemPrompt, user.ContinueFromImageId);
            var body = ImageRequest.Build(gen, history.Messages);
            var key = ImageRequest.CacheKey(body);

            if (!bypassCache && _store.CacheLookup(key) is string cachedId
                && all.FirstOrDefault(m => m.Id == cachedId) is { Status: MessageStatus.Ok } cached)
            {
                asst.Text = cached.Text;
                asst.Images = cached.Images.Select(i => new ImageRef
                {
                    Idx = i.Idx, Sha1 = i.Sha1, Mime = i.Mime, Path = i.Path, ExportPath = i.ExportPath,
                    Width = i.Width, Height = i.Height, Kind = i.Kind,
                }).ToList();
                asst.Status = MessageStatus.Ok;
                asst.DurationMs = 0;
                _store.SaveMessage(asst);
                return;
            }

            var result = await _client().ChatAsync(body, gen.Streaming, onDelta, ct);
            var (images, text) = ImageInfo.Extract(result.Content);
            if (images.Count == 0)
                throw new ProxyException(string.IsNullOrWhiteSpace(text) ? L.T("모델이 이미지를 반환하지 않았습니다.") : text, 400);

            asst.Text = text;
            asst.Thoughts = result.Reasoning;
            // celebrityGeneration has no Vertex field (always dropped) — the settings panel already says so.
            var dropped = result.Dropped.Where(d => !d.StartsWith("celebrityGeneration", StringComparison.Ordinal)).ToList();
            asst.Notes = dropped.Count > 0 ? string.Join(", ", dropped) : null;
            asst.Images.Clear();
            int idx = 0;
            foreach (var img in images)
            {
                var bytes = Convert.FromBase64String(img.Base64);
                var r = _store.SaveImageBytes(conv.Id, bytes, img.Mime, ImageKind.Generated, img.Base64);
                r.Idx = idx++;
                r.ExportPath = Export(r, bytes, conv, gen.Format, s.OutputDirectory);
                asst.Images.Add(r);
            }
            asst.Status = MessageStatus.Ok;
            asst.DurationMs = sw.ElapsedMilliseconds;
            _store.SaveMessage(asst);
            _store.CachePut(key, asst.Id);
        }
        catch (OperationCanceledException)
        {
            asst.Status = MessageStatus.Cancelled;
            asst.Error = L.T("취소됨");
            asst.DurationMs = sw.ElapsedMilliseconds;
            _store.SaveMessage(asst);
            throw;
        }
        catch (Exception e)
        {
            asst.Status = MessageStatus.Error;
            asst.Error = e is ProxyException pe ? pe.UserMessage : e.Message;
            asst.DurationMs = sw.ElapsedMilliseconds;
            _store.SaveMessage(asst);
            throw;
        }
    }

    /// <summary>
    /// Copies the original to the user's output folder. When the requested format differs from what the
    /// model returned, the hook <see cref="Converter"/> (set by the app, e.g. WinRT BitmapEncoder) re-encodes the copy;
    /// the original used for history is never touched.
    /// </summary>
    public static Func<byte[], OutputFormat, byte[]?>? Converter { get; set; }

    static string? Export(ImageRef img, byte[] bytes, Conversation conv, OutputFormat fmt, string outDir)
    {
        try
        {
            var wantMime = fmt == OutputFormat.Jpeg ? "image/jpeg" : "image/png";
            var data = bytes;
            var mime = img.Mime;
            if (wantMime != img.Mime && Converter?.Invoke(bytes, fmt) is byte[] converted) { data = converted; mime = wantMime; }
            var dir = Path.Combine(outDir, Safe(conv.Title) + "_" + conv.Id[..6]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{img.Sha1[..8]}{Store.MimeExt(mime)}");
            File.WriteAllBytes(path, data);
            return path;
        }
        catch { return null; }
    }

    static string Safe(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Length > 30 ? s[..30] : s;
    }

    // ---------------- /compact ----------------

    const string CompactSystem = """
        You compress an image-generation chat so it can continue with a much shorter history.
        Write a concise summary (in {LANG}, max ~15 lines) that preserves:
        - the overall goal and style decisions that are still in effect
        - exact text that must appear in images, brand names, colors, sizes, aspect ratio choices
        - what the latest image looks like and which edits were already applied
        - rejected ideas the user explicitly does not want
        Reply with the summary only.
        """;

    /// <summary>Summarises the active history into a summary message that keeps the latest images.</summary>
    public async Task<Message> CompactAsync(Conversation conv, List<Message> all, int keepImages = 2, CancellationToken ct = default)
    {
        var s = _settings();
        var (summary, turns) = HistoryBuilder.ActiveTurns(all);
        if (turns.Count < 2) throw new InvalidOperationException(L.T("압축할 대화가 충분하지 않습니다."));

        var transcript = new List<string>();
        if (summary != null) transcript.Add("[이전 요약]\n" + summary.Text);
        foreach (var m in turns)
            transcript.Add(m.Role == Roles.User
                ? $"사용자: {m.PromptForModel}{(m.Images.Count > 0 ? $" (참조 이미지 {m.Images.Count}장)" : "")}"
                : $"모델: {(string.IsNullOrWhiteSpace(m.Text) ? "(설명 없음)" : m.Text)} [이미지 {m.Images.Count}장 생성]");

        var body = new JsonObject
        {
            ["model"] = s.CompactModel ?? s.EnhanceModel,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = CompactSystem.Replace("{LANG}", L.IsKorean ? "Korean" : "English") },
                new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", transcript) }),
        };
        var r = await _client().ChatAsync(body, stream: false, ct: ct);
        var text = StripThink(r.Content).Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new ProxyException(L.T("요약 결과가 비어 있습니다."));

        // Keep the newest generated images (falling back to any image) as the visual baseline.
        var pool = new List<ImageRef>();
        if (summary != null) pool.AddRange(summary.Images);
        foreach (var m in turns) pool.AddRange(m.Images);
        var keep = pool.Where(i => i.Kind != ImageKind.Attached).TakeLast(keepImages).ToList();
        if (keep.Count == 0) keep = pool.TakeLast(keepImages).ToList();

        var msg = new Message
        {
            ConversationId = conv.Id,
            Seq = all.Max(m => m.Seq) + 1,
            Role = Roles.Summary,
            Text = text,
            Model = (string?)body["model"],
            Images = keep.Select((i, k) => new ImageRef
            {
                Idx = k, Sha1 = i.Sha1, Mime = i.Mime, Path = i.Path, ExportPath = i.ExportPath,
                Width = i.Width, Height = i.Height, Kind = ImageKind.Kept,
            }).ToList(),
        };
        // turns replaced = user turns since the previous summary
        msg.Error = null;
        msg.SettingsLabel = L.F("{0}턴 요약 · 기준 이미지 {1}장 유지", turns.Count(t => t.Role == Roles.User), keep.Count);
        _store.SaveMessage(msg);
        all.Add(msg);
        return msg;
    }
}
