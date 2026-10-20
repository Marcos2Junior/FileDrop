namespace ScreenshotApi;

internal static class ContentTypes
{
    // Conservative byte signatures, never extension or client MIME. No execution or conversion.
    public static string Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 24 && bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
            bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return "image/png";
        if (bytes.Length >= 4 && bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.Length >= 13 && (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8))) return "image/gif";
        if (bytes.Length >= 16 && bytes[..4].SequenceEqual("RIFF"u8))
        {
            if (bytes.Slice(8, 4).SequenceEqual("WEBP"u8) &&
                (bytes.Slice(12, 4).SequenceEqual("VP8 "u8) || bytes.Slice(12, 4).SequenceEqual("VP8L"u8) || bytes.Slice(12, 4).SequenceEqual("VP8X"u8))) return "image/webp";
            if (bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) return "audio/wav";
        }
        if (bytes.Length >= 8 && bytes.StartsWith("%PDF-"u8) && (bytes[5] == '1' || bytes[5] == '2') && bytes[6] == '.') return "application/pdf";
        if (bytes.Length >= 16 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = bytes.Slice(8, 4);
            if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("iso2"u8) || brand.SequenceEqual("mp41"u8) ||
                brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("avc1"u8) || brand.SequenceEqual("M4V "u8)) return "video/mp4";
            if (brand.SequenceEqual("M4A "u8)) return "audio/mp4";
        }
        var head = bytes[..Math.Min(bytes.Length, 4096)];
        if (bytes.Length >= 4 && bytes.StartsWith(new byte[] { 26, 69, 223, 163 }) && head.IndexOf("webm"u8) >= 0) return "video/webm";
        if (bytes.Length >= 27 && bytes.StartsWith("OggS"u8))
        {
            if (head.IndexOf("theora"u8) >= 0) return "video/ogg";
            if (head.IndexOf("OpusHead"u8) >= 0 || head.IndexOf("vorbis"u8) >= 0) return "audio/ogg";
        }
        if (bytes.Length >= 10 && bytes.StartsWith("ID3"u8) && bytes[3] is >= 2 and <= 4 &&
            bytes[6] < 128 && bytes[7] < 128 && bytes[8] < 128 && bytes[9] < 128) return "audio/mpeg";
        return "application/octet-stream";
    }
}
