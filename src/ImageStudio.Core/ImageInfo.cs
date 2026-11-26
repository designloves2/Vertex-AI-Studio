using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace ImageStudio.Core;

public static partial class ImageInfo
{
    [GeneratedRegex(@"!\[[^\]]*\]\((data:([^;]+);base64,([A-Za-z0-9+/=]+))\)")]
    public static partial Regex MarkdownImage();

    public sealed record Extracted(string Mime, string Base64);

    /// <summary>Splits a proxy response into markdown data-URI images and the remaining text.</summary>
    public static (List<Extracted> Images, string Text) Extract(string content)
    {
        var images = new List<Extracted>();
        foreach (Match m in MarkdownImage().Matches(content))
            images.Add(new Extracted(m.Groups[2].Value, m.Groups[3].Value));
        var text = MarkdownImage().Replace(content, "").Trim();
        return (images, text);
    }

    public static string SniffMime(byte[] b)
    {
        if (b.Length > 8 && b[0] == 0x89 && b[1] == 0x50) return "image/png";
        if (b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8) return "image/jpeg";
        if (b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[8] == 'W' && b[9] == 'E') return "image/webp";
        if (b.Length > 3 && b[0] == 'G' && b[1] == 'I') return "image/gif";
        return "image/png";
    }

    /// <summary>Reads width/height from PNG or JPEG headers without decoding.</summary>
    public static (int W, int H) Size(byte[] b)
    {
        try
        {
            if (b.Length > 24 && b[0] == 0x89 && b[1] == 0x50)
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(20)));
            if (b.Length > 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var marker = b[i + 1];
                    var len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5)));
                    i += 2 + len;
                }
            }
        }
        catch { }
        return (0, 0);
    }
}
