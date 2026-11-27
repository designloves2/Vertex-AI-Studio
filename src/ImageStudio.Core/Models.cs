namespace ImageStudio.Core;

public sealed class Conversation
{
    public string Id { get; set; } = NewId();
    public string Title { get; set; } = L.T("새 대화");
    public string Model { get; set; } = ModelCatalog.DefaultImageModel;
    public long CreatedAt { get; set; } = Now();
    public long UpdatedAt { get; set; } = Now();
    public long? DeletedAt { get; set; }
    public int MessageCount { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");
    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public static class Roles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    /// <summary>Result of /compact: replaces every earlier turn in the request history.</summary>
    public const string Summary = "summary";
}

public static class MessageStatus
{
    public const string Ok = "ok";
    public const string Pending = "pending";
    public const string Error = "error";
    public const string Cancelled = "cancelled";
}

public sealed class Message
{
    public string Id { get; set; } = Conversation.NewId();
    public string ConversationId { get; set; } = "";
    public int Seq { get; set; }
    public string Role { get; set; } = Roles.User;
    /// <summary>What the user typed (user) / model text (assistant) / summary text (summary).</summary>
    public string Text { get; set; } = "";
    /// <summary>Prompt-enhanced version actually sent to the image model (user only).</summary>
    public string? Enhanced { get; set; }
    public string? Model { get; set; }
    public string Status { get; set; } = MessageStatus.Ok;
    public string? Error { get; set; }
    public long CreatedAt { get; set; } = Conversation.Now();
    public long? DurationMs { get; set; }
    /// <summary>Thinking text returned by the model, if any.</summary>
    public string? Thoughts { get; set; }
    /// <summary>Image this user turn continues from ("이걸로 계속").</summary>
    public string? ContinueFromImageId { get; set; }
    /// <summary>Settings summary shown under the result (e.g. "2:3 · 2K · png").</summary>
    public string? SettingsLabel { get; set; }
    /// <summary>Options the proxy/Vertex did not apply to this result (comma separated).</summary>
    public string? Notes { get; set; }

    public List<ImageRef> Images { get; set; } = new();

    public string PromptForModel => string.IsNullOrWhiteSpace(Enhanced) ? Text : Enhanced!;
}

public static class ImageKind
{
    public const string Generated = "generated";
    public const string Attached = "attached";
    /// <summary>Image kept across a /compact (lives on the summary message).</summary>
    public const string Kept = "kept";
}

public sealed class ImageRef
{
    public string Id { get; set; } = Conversation.NewId();
    public string MessageId { get; set; } = "";
    public int Idx { get; set; }
    /// <summary>sha1 of the base64 text — same key as the proxy's thought-signature cache.</summary>
    public string Sha1 { get; set; } = "";
    public string Mime { get; set; } = "image/png";
    /// <summary>Original bytes as received (never re-encoded).</summary>
    public string Path { get; set; } = "";
    /// <summary>Copy in the user's output directory in the chosen format.</summary>
    public string? ExportPath { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Kind { get; set; } = ImageKind.Generated;
}
