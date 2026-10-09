using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Fiv.Native;

namespace Fiv.Bench;

/// <summary>Minimal encoders for the test corpus. Not used by the viewer.</summary>
public static unsafe class ImageWriters
{
    public static byte[] EncodeJpeg(byte* bgra, int stride, int width, int height, int quality,
        TurboJpeg.Subsampling subsampling, bool progressive = false)
    {
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
        try
        {
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Quality, quality);
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Subsamp, (int)subsampling);
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Progressive, progressive ? 1 : 0);
            byte* jpeg = null;
            nuint size = 0;
            if (TurboJpeg.tj3Compress8(tj, bgra, width, stride, height, TurboJpeg.PixelFormat.Bgra, &jpeg, &size) != 0)
                throw new InvalidOperationException("JPEG encode failed: " + TurboJpeg.LastError(tj));
            byte[] result = new ReadOnlySpan<byte>(jpeg, checked((int)size)).ToArray();
            TurboJpeg.tj3Free(jpeg);
            return result;
        }
        finally
        {
            TurboJpeg.tj3Destroy(tj);
        }
    }

    /// <summary>
    /// Inserts an EXIF block with the orientation tag (1..8) and, if given, an embedded thumbnail
    /// (a small JPEG), laid out the way cameras write it: in a second directory after the first.
    /// </summary>
    public static byte[] WithExif(byte[] jpeg, int orientation, byte[]? thumbnail = null)
    {
        static byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        var tiff = new List<byte>();
        tiff.AddRange([(byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00]);                               // header, first directory at 8
        tiff.AddRange([0x01, 0x00, 0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, (byte)orientation, 0, 0, 0]); // one entry: orientation
        if (thumbnail == null)
        {
            tiff.AddRange(Le32(0));
        }
        else
        {
            tiff.AddRange(Le32(26));                                               // second directory at 26
            tiff.AddRange([0x02, 0x00]);
            tiff.AddRange([0x01, 0x02, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);       // JPEGInterchangeFormat
            tiff.AddRange(Le32(56));
            tiff.AddRange([0x02, 0x02, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00]);       // JPEGInterchangeFormatLength
            tiff.AddRange(Le32(thumbnail.Length));
            tiff.AddRange(Le32(0));
            tiff.AddRange(thumbnail);                                              // at 56
        }
        int segmentLength = 2 + 6 + tiff.Count;
        if (segmentLength > 0xFFFF) throw new ArgumentException("The thumbnail does not fit in one EXIF segment.", nameof(thumbnail));
        var result = new List<byte>(jpeg.Length + segmentLength + 2);
        result.AddRange(jpeg.AsSpan(0, 2));
        result.AddRange([0xFF, 0xE1, (byte)(segmentLength >> 8), (byte)segmentLength]);
        result.AddRange("Exif\0\0"u8);
        result.AddRange(tiff);
        result.AddRange(jpeg.AsSpan(2));
        return result.ToArray();
    }

    /// <summary>PNG with the Paeth filter on every row (the most expensive one to undo) and zlib level 6.</summary>
    public static void WritePng(string path, byte* pixels, int stride, int width, int height, PngKind kind)
    {
        (int channels, int bytesPerSample, byte colorType) = kind switch
        {
            PngKind.Rgb8 => (3, 1, (byte)2),
            PngKind.Rgba8 => (4, 1, (byte)6),
            PngKind.Gray16 => (1, 2, (byte)0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        int bpp = channels * bytesPerSample, rowBytes = width * bpp;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = (byte)(bytesPerSample * 8);
        header[9] = colorType;
        WriteChunk(file, "IHDR"u8, header);

        using var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] current = new byte[rowBytes], previous = new byte[rowBytes], filtered = new byte[rowBytes + 1];
            for (int y = 0; y < height; y++)
            {
                byte* source = pixels + (long)y * stride;
                switch (kind)
                {
                    case PngKind.Rgb8: // BGRA -> RGB
                        for (int x = 0; x < width; x++)
                        {
                            current[x * 3] = source[x * 4 + 2];
                            current[x * 3 + 1] = source[x * 4 + 1];
                            current[x * 3 + 2] = source[x * 4];
                        }
                        break;
                    case PngKind.Rgba8: // BGRA -> RGBA
                        for (int x = 0; x < width; x++)
                        {
                            current[x * 4] = source[x * 4 + 2];
                            current[x * 4 + 1] = source[x * 4 + 1];
                            current[x * 4 + 2] = source[x * 4];
                            current[x * 4 + 3] = source[x * 4 + 3];
                        }
                        break;
                    case PngKind.Gray16: // little-endian ushort -> big-endian
                        for (int x = 0; x < width; x++)
                        {
                            current[x * 2] = source[x * 2 + 1];
                            current[x * 2 + 1] = source[x * 2];
                        }
                        break;
                }
                filtered[0] = 4;
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? current[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predictor = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                    filtered[i + 1] = (byte)(current[i] - predictor);
                }
                zlib.Write(filtered);
                (current, previous) = (previous, current);
            }
        }
        WriteChunk(file, "IDAT"u8, idat.GetBuffer().AsSpan(0, (int)idat.Length));
        WriteChunk(file, "IEND"u8, default);
    }

    public enum PngKind { Rgb8, Rgba8, Gray16 }

    static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        stream.Write(word);
        stream.Write(type);
        stream.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc32(Crc32(0xFFFFFFFF, type), data));
        stream.Write(word);
    }

    static readonly uint[] CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>Uncompressed single-strip grayscale TIFF: 16-bit unsigned or 32-bit float samples.</summary>
    public static void WriteTiffGray(string path, void* samples, int width, int height, bool isFloat)
    {
        int bytesPerSample = isFloat ? 4 : 2;
        long dataBytes = (long)width * height * bytesPerSample;
        const int dataOffset = 8;
        (ushort tag, ushort type, uint count, uint value)[] entries =
        [
            (256, 4, 1, (uint)width),                 // ImageWidth
            (257, 4, 1, (uint)height),                // ImageLength
            (258, 3, 1, (uint)(bytesPerSample * 8)),  // BitsPerSample
            (259, 3, 1, 1),                           // Compression: none
            (262, 3, 1, 1),                           // Photometric: BlackIsZero
            (273, 4, 1, dataOffset),                  // StripOffsets
            (277, 3, 1, 1),                           // SamplesPerPixel
            (278, 4, 1, (uint)height),                // RowsPerStrip
            (279, 4, 1, checked((uint)dataBytes)),    // StripByteCounts
            (339, 3, 1, isFloat ? 3u : 1u),           // SampleFormat: IEEE float / unsigned
        ];

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        Span<byte> header = stackalloc byte[8];
        header[0] = header[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(dataOffset + dataBytes)));
        file.Write(header);
        for (long offset = 0; offset < dataBytes;)
        {
            int chunk = (int)Math.Min(dataBytes - offset, 1 << 24);
            file.Write(new ReadOnlySpan<byte>((byte*)samples + offset, chunk));
            offset += chunk;
        }
        Span<byte> ifd = stackalloc byte[2 + entries.Length * 12 + 4];
        ifd.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(ifd, (ushort)entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            Span<byte> e = ifd.Slice(2 + i * 12, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(e, entries[i].tag);
            BinaryPrimitives.WriteUInt16LittleEndian(e[2..], entries[i].type);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], entries[i].count);
            if (entries[i].type == 3) BinaryPrimitives.WriteUInt16LittleEndian(e[8..], (ushort)entries[i].value);
            else BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].value);
        }
        file.Write(ifd);
    }

    public static float* ToFloat(ushort* gray16, int width, int height)
    {
        long count = (long)width * height;
        float* result = (float*)NativeMemory.Alloc((nuint)count * 4);
        for (long i = 0; i < count; i++) result[i] = gray16[i] * (1f / 65535f);
        return result;
    }
}
