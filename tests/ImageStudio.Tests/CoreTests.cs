using System.Text.Json.Nodes;
using ImageStudio.Core;

namespace ImageStudio.Tests;

public class HistoryBuilderTests
{
    static readonly HistoryBuilder Hb = new(img => "B64" + img.Id);
    static int _seq;

    static Message U(string text, params ImageRef[] imgs) => new() { Seq = ++_seq, Role = Roles.User, Text = text, Images = imgs.ToList() };
    static Message A(string text, string status = MessageStatus.Ok, params ImageRef[] imgs) =>
        new() { Seq = ++_seq, Role = Roles.Assistant, Text = text, Status = status, Images = imgs.ToList() };
    static ImageRef Img(string id, string kind = ImageKind.Generated) => new() { Id = id, Mime = "image/png", Kind = kind };

    [Fact]
    public void InlinesOnlyNewestImages_OlderBecomeText()
    {
        var all = new List<Message>
        {
            U("one"), A("", MessageStatus.Ok, Img("i1")),
            U("two"), A("", MessageStatus.Ok, Img("i2")),
            U("three"), A("", MessageStatus.Ok, Img("i3")),
            U("four"),
        };
        var r = Hb.Build(all, maxImages: 2);
        Assert.Equal(2, r.InlinedImages);
        Assert.Equal(1, r.TextImages);
        var first = (string)r.Messages[1]!["content"]!;
        Assert.Contains("[이전 이미지: one]", first);
        Assert.Contains("data:image/png;base64,B64i3", (string)r.Messages[5]!["content"]!);
        Assert.Equal("four", (string)r.Messages[6]!["content"]!);
    }

    [Fact]
    public void PinnedImageAlwaysIncluded()
    {
        var all = new List<Message>
        {
            U("one"), A("", MessageStatus.Ok, Img("i1")),
            U("two"), A("", MessageStatus.Ok, Img("i2")),
            U("three"),
        };
        var r = Hb.Build(all, maxImages: 1, pinnedImageId: "i1");
        Assert.Equal(2, r.InlinedImages);
    }

    [Fact]
    public void FailedTurnsAreSkipped()
    {
        var all = new List<Message> { U("bad"), A("", MessageStatus.Error), U("good"), A("ok", MessageStatus.Ok, Img("x")), U("next") };
        var r = Hb.Build(all, 3);
        Assert.Equal(3, r.Messages.Count);
        Assert.Equal("good", (string)r.Messages[0]!["content"]!);
    }

    [Fact]
    public void SummaryReplacesEarlierTurns()
    {
        var all = new List<Message>
        {
            U("old"), A("", MessageStatus.Ok, Img("o1")),
            new() { Seq = ++_seq, Role = Roles.Summary, Text = "요약", Images = { Img("o1", ImageKind.Kept) } },
            U("after"),
        };
        var r = Hb.Build(all, 3, systemPrompt: "sys");
        Assert.Equal("system", (string)r.Messages[0]!["role"]!);
        Assert.Contains("요약", (string)r.Messages[1]!["content"]!);
        Assert.Contains("B64o1", (string)r.Messages[2]!["content"]!);
        Assert.Equal("after", (string)r.Messages[3]!["content"]!);
        Assert.Equal(4, r.Messages.Count);
    }

    [Fact]
    public void EnhancedPromptIsSent_AttachmentsAsImageUrl()
    {
        var u = U("raw", Img("att", ImageKind.Attached));
        u.Enhanced = "enhanced";
        var r = Hb.Build(new List<Message> { u }, 3);
        var parts = r.Messages[0]!["content"]!.AsArray();
        Assert.Equal("enhanced", (string)parts[0]!["text"]!);
        Assert.Equal("image_url", (string)parts[1]!["type"]!);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var all = new List<Message> { U("a"), A("t", MessageStatus.Ok, Img("1")), U("b") };
        Assert.Equal(Hb.Build(all, 3).Messages.ToJsonString(), Hb.Build(all, 3).Messages.ToJsonString());
    }
}

public class ImageRequestTests
{
    [Fact]
    public void MapsSettingsToVertexFields()
    {
        var s = new GenerationSettings { AspectRatio = "auto", Size = ImageSize.K4, Format = OutputFormat.Jpeg, Person = PersonGeneration.DontAllow, GoogleSearch = true };
        var body = ImageRequest.Build(s, new JsonArray());
        Assert.Equal("2:3", (string)body["aspect_ratio"]!);
        var gc = body["vertex"]!["generationConfig"]!;
        Assert.Equal("4K", (string)gc["imageConfig"]!["imageSize"]!);
        Assert.Equal("image/jpeg", (string)gc["imageConfig"]!["imageOutputOptions"]!["mimeType"]!);
        Assert.Equal("ALLOW_NONE", (string)gc["imageConfig"]!["personGeneration"]!);
        Assert.Equal("MEDIUM", (string)gc["thinkingConfig"]!["thinkingLevel"]!);
        Assert.NotNull(body["vertex"]!["tools"]![0]!["googleSearch"]);
    }

    [Fact]
    public void ExtractsMarkdownImages()
    {
        var (imgs, text) = ImageInfo.Extract("설명\n\n![image 1](data:image/png;base64,AAAA)");
        Assert.Single(imgs);
        Assert.Equal("image/png", imgs[0].Mime);
        Assert.Equal("설명", text);
    }
}

public class StoreTests
{
    [Fact]
    public void RoundTripsConversationMessagesImages()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vas-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new Store(new AppPaths(dir));
            var c = new Conversation { Title = "t" };
            store.SaveConversation(c);
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
            var img = store.SaveImageBytes(c.Id, png, "image/png", ImageKind.Generated);
            Assert.Equal((1, 1), (img.Width, img.Height));
            store.SaveMessage(new Message { ConversationId = c.Id, Seq = 1, Role = Roles.User, Text = "hi" });
            store.SaveMessage(new Message { ConversationId = c.Id, Seq = 2, Role = Roles.Assistant, Images = { img } });
            var msgs = store.LoadMessages(c.Id);
            Assert.Equal(2, msgs.Count);
            Assert.Single(msgs[1].Images);
            Assert.Equal(1, store.ListConversations("hi")[0].MessageCount);
            store.TrashConversation(c.Id);
            Assert.Empty(store.ListConversations());
            store.PurgeTrash(all: true);
            Assert.Equal(0, store.TrashCount());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch { } }
    }
}

public class GoogleSetupTests
{
    [Fact]
    public void ReadsExistingProxyEnv()
    {
        var f = Path.Combine(Path.GetTempPath(), "vas-env-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(f, "﻿# c\nGOOGLE_CLOUD_PROJECT_ID=my-proj\nGOOGLE_CLOUD_LOCATION=us-central1\nPORT=3000\n");
        try
        {
            var s = GoogleSetup.ReadEnv(f)!;
            Assert.Equal("my-proj", s.ProjectId);
            Assert.Equal("us-central1", s.Location);
            Assert.Null(s.Model);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void EmbeddedProxyUrlUsesPort3001ByDefault()
    {
        var s = new AppSettings();
        Assert.Equal("http://127.0.0.1:3001", s.EffectiveProxyUrl);
        s.UseEmbeddedProxy = false;
        Assert.Equal("http://127.0.0.1:3000", s.EffectiveProxyUrl);
    }
}

public class ModelCatalogTests
{
    [Theory]
    [InlineData("gemini-3.1-flash-lite-image", "Nano Banana 2 lite (gemini-3.1-flash-lite-image)")]
    [InlineData("gemini-nano-banana-2.1", "Nano Banana 2.1 (gemini-nano-banana-2.1)")]
    [InlineData("gemini-3-pro-image", "Nano Banana Pro (gemini-3-pro-image)")]
    [InlineData("imagen-4.0-generate-001", "imagen-4.0-generate-001")]
    public void LabelShowsFriendlyNameAndId(string id, string expected) => Assert.Equal(expected, ModelCatalog.Label(id));
}
