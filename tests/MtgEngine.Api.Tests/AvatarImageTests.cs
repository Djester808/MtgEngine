using MtgEngine.Api.Services;

namespace MtgEngine.Api.Tests;

/// <summary>
/// The avatar upload gate. Everything a user uploads is served back to other people's
/// browsers, so these pin the two properties that keeps safe: the format is decided by the
/// bytes rather than by anything the uploader said, and nothing unreadable gets stored.
/// </summary>
public sealed class AvatarImageTests
{
    // ---- Builders for the smallest valid header of each format -------------

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[24];
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        signature.CopyTo(bytes);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        WriteBigEndian(bytes.AsSpan(16), width);
        WriteBigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static void WriteBigEndian(Span<byte> target, int value)
    {
        target[0] = (byte)(value >> 24);
        target[1] = (byte)(value >> 16);
        target[2] = (byte)(value >> 8);
        target[3] = (byte)value;
    }

    /// <summary>
    /// A JPEG with a JFIF segment before the frame header, so the parser has to actually
    /// walk the segment chain rather than read a fixed offset.
    /// </summary>
    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };            // SOI

        bytes.AddRange([0xFF, 0xE0, 0x00, 0x10]);             // APP0, length 16
        bytes.AddRange(new byte[14]);                          // its payload

        bytes.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08]);       // SOF0, length 17, 8-bit
        bytes.AddRange([(byte)(height >> 8), (byte)height]);
        bytes.AddRange([(byte)(width >> 8), (byte)width]);
        bytes.AddRange(new byte[10]);                          // component spec

        return [.. bytes];
    }

    private static byte[] WebpLossy(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8 "u8.CopyTo(bytes.AsSpan(12));
        bytes[23] = 0x9D;
        bytes[24] = 0x01;
        bytes[25] = 0x2A;
        bytes[26] = (byte)(width & 0xFF);
        bytes[27] = (byte)(width >> 8);
        bytes[28] = (byte)(height & 0xFF);
        bytes[29] = (byte)(height >> 8);
        return bytes;
    }

    private static byte[] WebpExtended(int width, int height)
    {
        var bytes = new byte[30];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));
        "VP8X"u8.CopyTo(bytes.AsSpan(12));

        // Canvas dimensions are stored as 24-bit (value - 1).
        var w = width - 1;
        var h = height - 1;
        bytes[24] = (byte)(w & 0xFF);
        bytes[25] = (byte)((w >> 8) & 0xFF);
        bytes[26] = (byte)((w >> 16) & 0xFF);
        bytes[27] = (byte)(h & 0xFF);
        bytes[28] = (byte)((h >> 8) & 0xFF);
        bytes[29] = (byte)((h >> 16) & 0xFF);
        return bytes;
    }

    // ---- Accepts each supported format, and reads its real size -----------

    [Fact]
    public void Reads_png_dimensions()
    {
        Assert.True(AvatarImage.TryValidate(Png(256, 128), out var image, out _));

        Assert.Equal(AvatarImage.Png, image.ContentType);
        Assert.Equal(256, image.Width);
        Assert.Equal(128, image.Height);
    }

    [Fact]
    public void Reads_jpeg_dimensions_past_a_leading_segment()
    {
        Assert.True(AvatarImage.TryValidate(Jpeg(640, 480), out var image, out _));

        Assert.Equal(AvatarImage.Jpeg, image.ContentType);
        Assert.Equal(640, image.Width);
        Assert.Equal(480, image.Height);
    }

    [Fact]
    public void Reads_lossy_webp_dimensions()
    {
        Assert.True(AvatarImage.TryValidate(WebpLossy(320, 240), out var image, out _));

        Assert.Equal(AvatarImage.Webp, image.ContentType);
        Assert.Equal(320, image.Width);
        Assert.Equal(240, image.Height);
    }

    [Fact]
    public void Reads_extended_webp_dimensions()
    {
        // VP8X is what a WebP with alpha gets written as, which is most of them.
        Assert.True(AvatarImage.TryValidate(WebpExtended(512, 512), out var image, out _));

        Assert.Equal(AvatarImage.Webp, image.ContentType);
        Assert.Equal(512, image.Width);
        Assert.Equal(512, image.Height);
    }

    // ---- Rejections -------------------------------------------------------

    [Fact]
    public void Rejects_a_non_image()
    {
        var text = "<script>alert(1)</script>"u8.ToArray();

        Assert.False(AvatarImage.TryValidate(text, out _, out var error));
        Assert.Contains("not a readable", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_an_executable_wearing_an_image_name()
    {
        // The filename and declared content type never reach the validator; MZ is still MZ.
        byte[] exe = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

        Assert.False(AvatarImage.TryValidate(exe, out _, out _));
    }

    [Fact]
    public void Rejects_a_png_signature_with_no_readable_header()
    {
        // Right magic bytes, truncated body: the format has to parse, not just start right.
        var truncated = Png(64, 64)[..12];

        Assert.False(AvatarImage.TryValidate(truncated, out _, out _));
    }

    [Fact]
    public void Rejects_an_empty_upload()
    {
        Assert.False(AvatarImage.TryValidate([], out _, out var error));
        Assert.Contains("empty", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_bytes_over_the_size_cap()
    {
        var oversized = new byte[AvatarImage.MaxBytes + 1];
        Png(64, 64).CopyTo(oversized, 0);

        Assert.False(AvatarImage.TryValidate(oversized, out _, out var error));
        Assert.Contains("KB or smaller", error);
    }

    [Fact]
    public void Rejects_an_image_larger_than_the_dimension_cap()
    {
        // A decompression bomb is small on the wire and enormous once decoded, so the size
        // cap alone does not cover this.
        Assert.False(
            AvatarImage.TryValidate(Png(AvatarImage.MaxDimension + 1, 64), out _, out var error));
        Assert.Contains("pixels or smaller", error);
    }

    [Fact]
    public void Rejects_a_tracking_pixel()
    {
        Assert.False(AvatarImage.TryValidate(Png(1, 1), out _, out var error));
        Assert.Contains("at least", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Accepts_an_image_exactly_on_the_dimension_cap()
    {
        Assert.True(
            AvatarImage.TryValidate(
                Png(AvatarImage.MaxDimension, AvatarImage.MaxDimension), out _, out _));
    }

    // ---- ETag -------------------------------------------------------------

    [Fact]
    public void Etag_is_stable_for_identical_bytes_and_differs_otherwise()
    {
        var first = AvatarImage.ComputeETag(Png(64, 64));
        var same = AvatarImage.ComputeETag(Png(64, 64));
        var other = AvatarImage.ComputeETag(Png(65, 64));

        Assert.Equal(first, same);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void Etag_is_a_quoted_strong_validator()
    {
        // An unquoted value is not a legal ETag header and browsers drop it, which silently
        // costs every conditional request.
        var etag = AvatarImage.ComputeETag(Png(64, 64));

        Assert.StartsWith("\"", etag, StringComparison.Ordinal);
        Assert.EndsWith("\"", etag, StringComparison.Ordinal);
    }
}
