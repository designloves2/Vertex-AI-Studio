using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageStudio.Core;

public enum OutputModality { ImageAndText, ImageOnly }
public enum ThinkingLevel { Low, Medium, High }
public enum MediaResolution { Default, Low, Medium, High }
public enum ImageSize { K1, K2, K4 }
public enum OutputFormat { Png, Jpeg }
public enum PersonGeneration { AllowAll, AllowAdult, DontAllow }
public enum SendKey { Enter, CtrlEnter }

/// <summary>Per-request image generation options (right-hand settings panel).</summary>
public sealed class GenerationSettings
{
    public string Model { get; set; } = ModelCatalog.DefaultImageModel;
    public OutputModality Output { get; set; } = OutputModality.ImageAndText;
    public ThinkingLevel Thinking { get; set; } = ThinkingLevel.Medium;
    public MediaResolution MediaResolution { get; set; } = MediaResolution.Default;
    /// <summary>"auto" or a ratio such as "16:9".</summary>
    public string AspectRatio { get; set; } = "auto";
    public ImageSize Size { get; set; } = ImageSize.K2;
    public OutputFormat Format { get; set; } = OutputFormat.Png;
    public PersonGeneration Person { get; set; } = PersonGeneration.AllowAdult;
    public bool AllowCelebrities { get; set; } = false;

    // Advanced
    public bool GoogleSearch { get; set; } = false;
    public bool GoogleSearchServices { get; set; } = false;
    public bool Partner { get; set; } = false;
    public bool PartnerResults { get; set; } = false;
    public string Region { get; set; } = "global";
    public int MaxOutputTokens { get; set; } = 32768;
    public bool Streaming { get; set; } = true;

    public static readonly string[] AspectRatios =
        { "auto", "1:1", "2:3", "3:2", "3:4", "4:3", "4:5", "5:4", "9:16", "16:9", "21:9" };
    public const string AutoAspectDefault = "2:3";

    public string EffectiveAspectRatio => AspectRatio == "auto" ? AutoAspectDefault : AspectRatio;

    public GenerationSettings Clone() => (GenerationSettings)MemberwiseClone();
}

public sealed class AppSettings
{
    /// <summary>External proxy URL, used only when <see cref="UseEmbeddedProxy"/> is off.</summary>
    public string ProxyUrl { get; set; } = "http://127.0.0.1:3000";

    // Embedded proxy (bundled index.js), started with the app.
    public bool UseEmbeddedProxy { get; set; } = true;
    public int ProxyPort { get; set; } = 3001;
    public string? GcpProjectId { get; set; }
    public string GcpLocation { get; set; } = "global";
    public string ProxyDefaultModel { get; set; } = "gemini-3.7-flash";
    /// <summary>Set once the existing proxy's .env was looked for, so it is imported only once.</summary>
    public bool ProxyImportChecked { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveProxyUrl => UseEmbeddedProxy ? $"http://127.0.0.1:{ProxyPort}" : ProxyUrl;
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "VertexAIStudio");
    public int TimeoutSeconds { get; set; } = 300;
    public string Theme { get; set; } = "system";
    public string? SystemPrompt { get; set; }

    public GenerationSettings Generation { get; set; } = new();

    public bool EnhanceEnabled { get; set; } = true;
    public string EnhanceModel { get; set; } = ModelCatalog.DefaultEnhanceModel;
    /// <summary>Chat model used to write /compact summaries (defaults to the enhance model).</summary>
    public string? CompactModel { get; set; }

    public SendKey SendKey { get; set; } = SendKey.Enter;
    public int HistoryImageCount { get; set; } = 3;
    public int ContextTokenLimit { get; set; } = 65536;
    /// <summary>Auto /compact when the estimated context reaches this percentage (0 = off).</summary>
    public int AutoCompactPercent { get; set; } = 80;

    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>"system" (Korean if Windows is Korean, else English), "ko" or "en". Applied at startup.</summary>
    public string Language { get; set; } = "system";

    public bool SessionListCollapsed { get; set; }
    public bool SettingsPanelCollapsed { get; set; }
    public string? LastConversationId { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch { /* corrupt settings: fall back to defaults */ }
        return new();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

public static class ModelCatalog
{
    public const string DefaultImageModel = "gemini-nano-banana-2.1";
    public const string DefaultEnhanceModel = "gemini-3.7-flash";

    static readonly Dictionary<string, string> Friendly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-nano-banana-2.1"] = "Nano Banana 2.1",
        ["gemini-2.5-flash-image"] = "Nano Banana",
        ["gemini-3-pro-image"] = "Nano Banana Pro",
        ["gemini-3-pro-image-preview"] = "Nano Banana Pro (preview)",
        ["gemini-3.1-flash-image"] = "Nano Banana 2",
        ["gemini-3.1-flash-image-preview"] = "Nano Banana 2 (preview)",
        ["gemini-3.1-flash-lite-image"] = "Nano Banana 2 lite",
        ["gemini-3.1-flash-lite-image-preview"] = "Nano Banana 2 lite (preview)",
    };

    public static string DisplayName(string id) => Friendly.TryGetValue(id, out var n) ? n : id;

    /// <summary>"Nano Banana 2 lite (gemini-3.1-flash-lite-image)" — friendly name plus the model id.</summary>
    public static string Label(string id) => Friendly.TryGetValue(id, out var n) ? $"{n} ({id})" : id;
    public static bool IsImagen(string id) => id.StartsWith("imagen", StringComparison.OrdinalIgnoreCase);
}
