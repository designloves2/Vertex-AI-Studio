using System.Runtime.InteropServices.WindowsRuntime;
using ImageStudio.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace ImageStudio.App;

static class ImageUtil
{
    /// <summary>Re-encodes an image to PNG/JPEG (used only for the exported copy, never for history).</summary>
    public static byte[]? Convert(byte[] bytes, OutputFormat fmt)
    {
        try { return Task.Run(() => ReencodeAsync(bytes, fmt == OutputFormat.Jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId)).GetAwaiter().GetResult(); }
        catch { return null; }
    }

    public static async Task<byte[]> ReencodeAsync(byte[] bytes, Guid encoderId)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(bytes.AsBuffer());
        input.Seek(0);
        return await ReencodeAsync(input, encoderId);
    }

    public static async Task<byte[]> ReencodeAsync(IRandomAccessStream input, Guid encoderId)
    {
        var decoder = await BitmapDecoder.CreateAsync(input);
        using var sb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(encoderId, output);
        encoder.SetSoftwareBitmap(sb);
        if (encoderId == BitmapEncoder.JpegEncoderId) { /* default quality */ }
        await encoder.FlushAsync();
        var result = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(result.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
        return result;
    }

    public static async Task<byte[]> ReadAllAsync(IRandomAccessStream s)
    {
        var data = new byte[s.Size];
        s.Seek(0);
        await s.ReadAsync(data.AsBuffer(), (uint)s.Size, InputStreamOptions.None);
        return data;
    }

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" };

    /// <summary>Normalises an attachment: PNG/JPEG/WebP are kept as-is, anything else becomes PNG.</summary>
    public static async Task<(byte[] Bytes, string Mime)> NormalizeAsync(byte[] bytes)
    {
        var mime = ImageInfo.SniffMime(bytes);
        bool known = bytes.Length > 4 && ((bytes[0] == 0x89 && bytes[1] == 0x50) || (bytes[0] == 0xFF && bytes[1] == 0xD8) || mime == "image/webp");
        if (known) return (bytes, mime);
        return (await ReencodeAsync(bytes, BitmapEncoder.PngEncoderId), "image/png");
    }
}
