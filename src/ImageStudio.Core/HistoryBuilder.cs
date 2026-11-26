using System.Text.Json.Nodes;

namespace ImageStudio.Core;

/// <summary>
/// Turns stored messages into the OpenAI-style <c>messages</c> array sent to the proxy.
/// Rules (docs/image-app-spec.md §5):
///  - optional system prompt first; then turns in order, byte-identical every time (implicit caching)
///  - only the most recent N images are inlined; older ones become "[이전 이미지: …]" text
///  - the "이걸로 계속" image is always inlined
///  - failed / cancelled turns are skipped
///  - after a /compact, everything before the latest summary is replaced by that summary (+ its kept images)
/// </summary>
public sealed class HistoryBuilder
{
    public const long MaxBodyBytes = 40L * 1024 * 1024;

    readonly Func<ImageRef, string> _loadBase64;

    public HistoryBuilder(Func<ImageRef, string> loadBase64) => _loadBase64 = loadBase64;

    public static HistoryBuilder FromFiles() => new(img => Convert.ToBase64String(File.ReadAllBytes(img.Path)));

    public sealed record Result(JsonArray Messages, int InlinedImages, int TextImages, long ApproxBytes);

    public Result Build(IReadOnlyList<Message> all, int maxImages, string? systemPrompt = null, string? pinnedImageId = null)
    {
        for (int n = Math.Max(0, maxImages); ; n = n > 1 ? n / 2 : 0)
        {
            var r = BuildOnce(all, n, systemPrompt, pinnedImageId);
            if (r.ApproxBytes <= MaxBodyBytes || n == 0) return r;
        }
    }

    /// <summary>Messages after the latest summary, with failed turns removed.</summary>
    public static (Message? Summary, List<Message> Turns) ActiveTurns(IReadOnlyList<Message> all)
    {
        int s = -1;
        for (int i = all.Count - 1; i >= 0; i--)
            if (all[i].Role == Roles.Summary && all[i].Status == MessageStatus.Ok) { s = i; break; }
        var summary = s >= 0 ? all[s] : null;

        var turns = new List<Message>();
        var rest = all.Skip(s + 1).Where(m => m.Role is Roles.User or Roles.Assistant).ToList();
        for (int i = 0; i < rest.Count; i++)
        {
            var m = rest[i];
            if (m.Role == Roles.User)
            {
                var reply = i + 1 < rest.Count && rest[i + 1].Role == Roles.Assistant ? rest[i + 1] : null;
                bool isLast = i == rest.Count - 1;
                if (reply == null && !isLast) continue;            // orphan user turn
                if (reply != null && reply.Status != MessageStatus.Ok) { i++; continue; } // failed turn
                turns.Add(m);
                if (reply != null) { turns.Add(reply); i++; }
            }
        }
        return (summary, turns);
    }

    Result BuildOnce(IReadOnlyList<Message> all, int maxImages, string? systemPrompt, string? pinnedImageId)
    {
        var (summary, turns) = ActiveTurns(all);

        // Decide which images get inlined: the newest N in conversation order, plus the pinned one.
        var ordered = new List<ImageRef>();
        if (summary != null) ordered.AddRange(summary.Images);
        foreach (var t in turns) ordered.AddRange(t.Images);
        var inline = ordered.Skip(Math.Max(0, ordered.Count - maxImages)).Select(i => i.Id).ToHashSet();
        if (pinnedImageId != null && ordered.Any(i => i.Id == pinnedImageId)) inline.Add(pinnedImageId);

        var msgs = new JsonArray();
        long bytes = 0;
        int inlined = 0, asText = 0;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgs.Add(new JsonObject { ["role"] = "system", ["content"] = systemPrompt });

        if (summary != null)
        {
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = "[이전 대화 요약]\n" + summary.Text });
            var keptText = Assistant(summary.Images, "요약된 대화의 기준 이미지입니다.", inline, "", ref bytes, ref inlined, ref asText);
            msgs.Add(new JsonObject { ["role"] = "assistant", ["content"] = keptText });
        }

        string lastPrompt = "";
        foreach (var m in turns)
        {
            if (m.Role == Roles.User)
            {
                lastPrompt = m.PromptForModel;
                var text = m.PromptForModel;
                var imageParts = new List<JsonNode>();
                foreach (var img in m.Images)
                {
                    if (inline.Contains(img.Id))
                    {
                        var uri = DataUri(img);
                        bytes += uri.Length;
                        inlined++;
                        imageParts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = uri } });
                    }
                    else
                    {
                        asText++;
                        text += "\n[첨부했던 이미지]";
                    }
                }
                JsonNode content;
                if (imageParts.Count == 0) content = JsonValue.Create(text)!;
                else
                {
                    var arr = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
                    foreach (var p in imageParts) arr.Add(p);
                    content = arr;
                }
                msgs.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            else
            {
                msgs.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = Assistant(m.Images, m.Text, inline, lastPrompt, ref bytes, ref inlined, ref asText),
                });
            }
        }
        foreach (var n in msgs) bytes += (n!["content"] is JsonValue v ? v.ToString().Length : 0);
        return new Result(msgs, inlined, asText, bytes);
    }

    string Assistant(List<ImageRef> images, string text, HashSet<string> inline, string prompt,
        ref long bytes, ref int inlined, ref int asText)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        int k = 0;
        foreach (var img in images)
        {
            k++;
            if (inline.Contains(img.Id))
            {
                var uri = DataUri(img);
                bytes += uri.Length;
                inlined++;
                parts.Add($"![image {k}]({uri})");
            }
            else
            {
                asText++;
                parts.Add($"[이전 이미지: {Short(prompt)}]");
            }
        }
        return parts.Count == 0 ? "(이미지 없음)" : string.Join("\n\n", parts);
    }

    string DataUri(ImageRef img) => $"data:{img.Mime};base64,{_loadBase64(img)}";

    static string Short(string s)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= 60 ? s : s[..60] + "…";
    }
}
