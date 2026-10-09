using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace Fiv.Imaging;

public enum ImageFileFormat : byte
{
    Unknown,
    Jpeg,
    Png,
    Gif,
    Bmp,
    Tiff,
    WebP,
}

/// <summary>
/// Reads an image's pixel size from its header without decoding anything: the file list shows
/// dimensions for thousands of files from a read of a few kilobytes each.
/// </summary>
public static class ImageProbe
{
    const int HeadSize = 4096;

    // As bytes, not as a string literal: in UTF-8 the character \x89 would be two bytes.
    static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// The size as stored in the file (EXIF rotation not applied). False if the file cannot be
    /// read or is not one of JPEG, PNG, GIF, BMP, TIFF, WebP.
    /// </summary>
    public static bool TryReadSize(string path, out int width, out int height, out ImageFileFormat format)
    {
        width = height = 0;
        format = ImageFileFormat.Unknown;
        try
        {
            using SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> head = stackalloc byte[HeadSize];
            int read = RandomAccess.Read(file, head, 0);
            head = head[..read];
            format = Identify(head);
            return format switch
            {
                ImageFileFormat.Jpeg => ReadJpeg(file, head, out width, out height),
                ImageFileFormat.Tiff => ReadTiff(file, head, out width, out height),
                ImageFileFormat.Unknown => false,
                _ => TryReadSize(head, out width, out height, out _),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static ImageFileFormat Identify(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return ImageFileFormat.Jpeg;
        if (head.StartsWith(PngSignature)) return ImageFileFormat.Png;
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return ImageFileFormat.Gif;
        if (head.StartsWith("BM"u8)) return ImageFileFormat.Bmp;
        if (head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8)) return ImageFileFormat.Tiff;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return ImageFileFormat.WebP;
        return ImageFileFormat.Unknown;
    }

    /// <summary>
    /// The size from the first bytes of a file. Enough for PNG, GIF, BMP and WebP always; for JPEG
    /// and TIFF only when the header happens to lie within <paramref name="head"/>.
    /// </summary>
    public static bool TryReadSize(ReadOnlySpan<byte> head, out int width, out int height, out ImageFileFormat format)
    {
        width = height = 0;
        format = Identify(head);
        switch (format)
        {
            case ImageFileFormat.Png when head.Length >= 24:
                width = BinaryPrimitives.ReadInt32BigEndian(head[16..]);
                height = BinaryPrimitives.ReadInt32BigEndian(head[20..]);
                break;
            case ImageFileFormat.Gif when head.Length >= 10:
                width = BinaryPrimitives.ReadUInt16LittleEndian(head[6..]);
                height = BinaryPrimitives.ReadUInt16LittleEndian(head[8..]);
                break;
            case ImageFileFormat.Bmp when head.Length >= 26:
                if (BinaryPrimitives.ReadInt32LittleEndian(head[14..]) == 12) // OS/2 header: 16-bit sizes
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(head[18..]);
                    height = BinaryPrimitives.ReadUInt16LittleEndian(head[20..]);
                }
                else
                {
                    width = BinaryPrimitives.ReadInt32LittleEndian(head[18..]);
                    height = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])); // negative: stored top-down
                }
                break;
            case ImageFileFormat.WebP when head.Length >= 30:
            {
                ReadOnlySpan<byte> chunk = head.Slice(12, 4);
                if (chunk.SequenceEqual("VP8 "u8) && head[23] == 0x9D && head[24] == 0x01 && head[25] == 0x2A)
                {
                    width = BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF;
                    height = BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF;
                }
                else if (chunk.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
                {
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
                    width = (int)(bits & 0x3FFF) + 1;
                    height = (int)((bits >> 14) & 0x3FFF) + 1;
                }
                else if (chunk.SequenceEqual("VP8X"u8))
                {
                    width = (head[24] | head[25] << 8 | head[26] << 16) + 1;
                    height = (head[27] | head[28] << 8 | head[29] << 16) + 1;
                }
                break;
            }
            case ImageFileFormat.Jpeg:
                return ReadJpeg(null, head, out width, out height);
            case ImageFileFormat.Tiff:
                return ReadTiff(null, head, out width, out height);
        }
        return width > 0 && height > 0;
    }

    /// <summary>Bytes at a file position: from the head if they are in it, otherwise read from the file.</summary>
    static bool Fetch(SafeFileHandle? file, ReadOnlySpan<byte> head, long position, Span<byte> destination)
    {
        if (position >= 0 && position + destination.Length <= head.Length)
        {
            head.Slice((int)position, destination.Length).CopyTo(destination);
            return true;
        }
        return file != null && RandomAccess.Read(file, destination, position) == destination.Length;
    }

    // Walks the markers to the frame header. Metadata segments before it (EXIF with its
    // thumbnail, colour profiles) are skipped by their length, not read.
    static bool ReadJpeg(SafeFileHandle? file, ReadOnlySpan<byte> head, out int width, out int height)
    {
        width = height = 0;
        Span<byte> segment = stackalloc byte[9];
        long position = 2;
        for (int guard = 0; guard < 256; guard++)
        {
            if (!Fetch(file, head, position, segment[..4]) || segment[0] != 0xFF) return false;
            byte marker = segment[1];
            if (marker == 0xFF) // padding
            {
                position++;
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) // markers without a length
            {
                position += 2;
                continue;
            }
            bool frameHeader = marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (frameHeader)
            {
                if (!Fetch(file, head, position, segment)) return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(segment[5..]);
                width = BinaryPrimitives.ReadUInt16BigEndian(segment[7..]);
                return width > 0 && height > 0;
            }
            if (marker is 0xDA or 0xD9) return false; // image data before any frame header
            position += 2 + BinaryPrimitives.ReadUInt16BigEndian(segment[2..]);
        }
        return false;
    }

    static bool ReadTiff(SafeFileHandle? file, ReadOnlySpan<byte> head, out int width, out int height)
    {
        width = height = 0;
        if (head.Length < 8) return false;
        bool little = head[0] == (byte)'I';
        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        long directory = U32(head[4..]);
        Span<byte> entry = stackalloc byte[12];
        if (!Fetch(file, head, directory, entry[..2])) return false;
        int count = U16(entry);
        for (int i = 0; i < count && (width == 0 || height == 0); i++)
        {
            if (!Fetch(file, head, directory + 2 + i * 12L, entry)) return false;
            ushort tag = U16(entry), type = U16(entry[2..]);
            if (tag is not (256 or 257)) continue;
            int value = type == 3 ? U16(entry[8..]) : (int)U32(entry[8..]);
            if (tag == 256) width = value;
            else height = value;
        }
        return width > 0 && height > 0;
    }
}
