using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MtgEngine.Api.Services;

/// <summary>
/// Decides whether an uploaded byte array is an image this app will store and serve back.
/// </summary>
/// <remarks>
/// The declared <c>Content-Type</c> and the filename are attacker-controlled and are not
/// consulted at all: the format is read out of the bytes, and the content type we later
/// serve is the one derived here. That is the whole point — a stored file is served back
/// to other users' browsers, so the response's type must describe what is actually in the
/// blob, not what the uploader claimed.
/// <para>
/// This does not decode pixels, so it is not a re-encode and does not strip a payload
/// appended past the image data. Three things carry that weight instead: the format and
/// dimensions must genuinely parse (a .exe renamed to .png fails here), the response is
/// pinned to the sniffed type with <c>X-Content-Type-Options: nosniff</c> and
/// <c>Content-Disposition: inline</c> so a browser will not run it as script or HTML, and
/// the size cap keeps anything smuggled small. Adding a full decode means taking an image
/// library as a dependency; if that day comes, re-encode here and the callers do not change.
/// </para>
/// </remarks>
public static class AvatarImage
{
    /// <summary>Hard cap on stored bytes. The client resizes before upload; this is the backstop.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>Largest edge accepted. An avatar renders at 96px, so this is already generous.</summary>
    public const int MaxDimension = 1024;

    /// <summary>Below this an "image" is not a portrait of anything, and is usually a probe.</summary>
    public const int MinDimension = 16;

    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";

    /// <summary>The content types an upload may resolve to, for advertising to clients.</summary>
    public static readonly string[] AcceptedContentTypes = [Jpeg, Png, Webp];

    /// <summary>A validated image: the format we sniffed, its size, and a strong ETag.</summary>
    public readonly record struct Validated(string ContentType, int Width, int Height, string ETag);

    /// <summary>
    /// Validates <paramref name="bytes"/>, returning the sniffed image or the reason it was
    /// rejected. <paramref name="error"/> is safe to show a user: it never echoes any part
    /// of the upload.
    /// </summary>
    public static bool TryValidate(ReadOnlySpan<byte> bytes, out Validated result, out string error)
    {
        result = default;
        error = string.Empty;

        if (bytes.Length == 0)
        {
            error = "The image is empty.";
            return false;
        }

        if (bytes.Length > MaxBytes)
        {
            error = $"The image must be {MaxBytes / 1024} KB or smaller.";
            return false;
        }

        if (!TryReadSize(bytes, out var contentType, out var width, out var height))
        {
            error = "That file is not a readable JPEG, PNG or WebP image.";
            return false;
        }

        if (width < MinDimension || height < MinDimension)
        {
            error = $"The image must be at least {MinDimension}×{MinDimension} pixels.";
            return false;
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            error = $"The image must be {MaxDimension}×{MaxDimension} pixels or smaller.";
            return false;
        }

        result = new Validated(contentType, width, height, ComputeETag(bytes));
        return true;
    }

    /// <summary>A strong, quoted ETag over the exact bytes stored.</summary>
    public static string ComputeETag(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return $"\"{Convert.ToHexString(hash[..16]).ToLowerInvariant()}\"";
    }

    /// <summary>Sniffs the container and reads the image's declared pixel dimensions.</summary>
    private static bool TryReadSize(
        ReadOnlySpan<byte> b, out string contentType, out int width, out int height)
    {
        contentType = string.Empty;
        width = height = 0;

        if (IsPng(b))
        {
            contentType = Png;
            return TryReadPngSize(b, out width, out height);
        }

        if (IsJpeg(b))
        {
            contentType = Jpeg;
            return TryReadJpegSize(b, out width, out height);
        }

        if (IsWebp(b))
        {
            contentType = Webp;
            return TryReadWebpSize(b, out width, out height);
        }

        return false;
    }

    /// <summary>The eight-byte PNG signature, including the CR/LF pair that catches text-mode transfers.</summary>
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 8 && b[..8].SequenceEqual(PngSignature);

    private static bool IsJpeg(ReadOnlySpan<byte> b) =>
        b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static bool IsWebp(ReadOnlySpan<byte> b) =>
        b.Length >= 12
        && b[..4].SequenceEqual("RIFF"u8)
        && b[8..12].SequenceEqual("WEBP"u8);

    /// <summary>PNG: IHDR is mandated to be the first chunk, so width/height sit at a fixed offset.</summary>
    private static bool TryReadPngSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 24 || !b[12..16].SequenceEqual("IHDR"u8))
            return false;

        width = BinaryPrimitives.ReadInt32BigEndian(b[16..20]);
        height = BinaryPrimitives.ReadInt32BigEndian(b[20..24]);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// JPEG: walk the marker segments to the start-of-frame, which is the only one carrying
    /// the size. Everything before it (EXIF, ICC, comments, quantisation tables) is skipped
    /// by its own length field.
    /// </summary>
    private static bool TryReadJpegSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        var i = 2; // past SOI

        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF)
                return false; // not aligned on a marker: malformed

            // Padding fill bytes are legal between segments.
            while (i < b.Length && b[i] == 0xFF)
                i++;
            if (i >= b.Length)
                return false;

            var marker = b[i++];

            // Standalone markers: no length, no payload.
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD9))
                continue;

            if (i + 1 >= b.Length)
                return false;

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(b[i..(i + 2)]);
            if (segmentLength < 2 || i + segmentLength > b.Length)
                return false;

            // SOF0-SOF15 hold the frame size. C4 (DHT), C8 (JPG) and CC (DAC) share the
            // range without being frame headers.
            if (marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (segmentLength < 7)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 3)..(i + 5)]);
                width = BinaryPrimitives.ReadUInt16BigEndian(b[(i + 5)..(i + 7)]);
                return width > 0 && height > 0;
            }

            // Start of scan: entropy-coded data follows, so a frame header can no longer come.
            if (marker == 0xDA)
                return false;

            i += segmentLength;
        }

        return false;
    }

    /// <summary>
    /// WebP: three sub-formats, each storing the size differently — lossy (<c>VP8 </c>),
    /// lossless (<c>VP8L</c>) and extended (<c>VP8X</c>, which is what anything with alpha
    /// or animation gets written as).
    /// </summary>
    private static bool TryReadWebpSize(ReadOnlySpan<byte> b, out int width, out int height)
    {
        width = height = 0;
        if (b.Length < 30)
            return false;

        var fourCc = b[12..16];

        if (fourCc.SequenceEqual("VP8 "u8))
        {
            // Frame tag (3 bytes) then the 3-byte start code 0x9D 0x01 0x2A, then 14-bit
            // width and height, each with a 2-bit scale in the top bits.
            if (b.Length < 30 || b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A)
                return false;
            width = BinaryPrimitives.ReadUInt16LittleEndian(b[26..28]) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(b[28..30]) & 0x3FFF;
            return width > 0 && height > 0;
        }

        if (fourCc.SequenceEqual("VP8L"u8))
        {
            if (b.Length < 25 || b[20] != 0x2F)
                return false;
            // 14 bits of (width-1) then 14 bits of (height-1), little-endian bit order.
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(b[21..25]);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        if (fourCc.SequenceEqual("VP8X"u8))
        {
            if (b.Length < 30)
                return false;
            // Canvas size is stored as 24-bit (value - 1).
            width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
            height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
            return true;
        }

        return false;
    }
}
