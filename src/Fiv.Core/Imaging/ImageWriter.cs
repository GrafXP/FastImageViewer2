using System.Buffers.Binary;
using System.IO.Compression;

namespace Fiv.Imaging;

/// <summary>
/// Writes what the viewer exports: PNG for pictures, TIFF for float values. Both are written to
/// a temporary file next to the target and renamed, so that a file is whole or not there.
/// </summary>
public static unsafe class ImageWriter
{
    /// <summary>
    /// Writes a BGRA8 or RGBA16 image as 8- or 16-bit RGB (or RGBA with <paramref name="alpha"/>),
    /// an 8- or 16-bit grey image as it is. Rows are filtered with "sub" and compressed fast: the
    /// files are some 10 % larger than they could be, and written several times sooner.
    /// </summary>
    public static void WritePng(string path, DecodedImage image, bool alpha = false)
    {
        (int channels, int bytesPerSample, byte colorType) = image.Layout switch
        {
            PixelLayout.Bgra8 => alpha ? (4, 1, (byte)6) : (3, 1, (byte)2),
            PixelLayout.Rgba16 => alpha ? (4, 2, (byte)6) : (3, 2, (byte)2),
            PixelLayout.Gray8 => (1, 1, (byte)0),
            PixelLayout.Gray16 => (1, 2, (byte)0),
            _ => throw new ArgumentException("PNG takes 8- and 16-bit colour and grey images.", nameof(image)),
        };
        int width = image.Width, height = image.Height, pixelBytes = channels * bytesPerSample, rowBytes = width * pixelBytes;

        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            Span<byte> header = stackalloc byte[13];
            header.Clear();
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
            header[8] = (byte)(bytesPerSample * 8);
            header[9] = colorType;
            WriteChunk(file, "IHDR"u8, header);

            using var data = new MemoryStream();
            using (var zlib = new ZLibStream(data, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] row = new byte[rowBytes], filtered = new byte[rowBytes + 1];
                filtered[0] = 1; // "sub": each byte less the same byte of the pixel to its left
                for (int y = 0; y < height; y++)
                {
                    byte* source = image.Pixels + (long)y * image.Stride;
                    switch (image.Layout)
                    {
                        case PixelLayout.Bgra8:
                            for (int x = 0; x < width; x++)
                            {
                                row[x * channels] = source[x * 4 + 2];
                                row[x * channels + 1] = source[x * 4 + 1];
                                row[x * channels + 2] = source[x * 4];
                                if (alpha) row[x * 4 + 3] = source[x * 4 + 3];
                            }
                            break;
                        case PixelLayout.Rgba16:
                            // Red, green, blue (and alpha) as stored, the high byte of each first.
                            for (int x = 0; x < width; x++)
                            {
                                for (int c = 0; c < channels; c++)
                                {
                                    row[(x * channels + c) * 2] = source[(x * 4 + c) * 2 + 1];
                                    row[(x * channels + c) * 2 + 1] = source[(x * 4 + c) * 2];
                                }
                            }
                            break;
                        case PixelLayout.Gray8:
                            new ReadOnlySpan<byte>(source, width).CopyTo(row);
                            break;
                        default: // 16-bit: PNG wants the high byte first
                            for (int x = 0; x < width; x++)
                            {
                                row[x * 2] = source[x * 2 + 1];
                                row[x * 2 + 1] = source[x * 2];
                            }
                            break;
                    }
                    for (int i = 0; i < rowBytes; i++) filtered[i + 1] = (byte)(row[i] - (i >= pixelBytes ? row[i - pixelBytes] : 0));
                    zlib.Write(filtered);
                }
            }
            WriteChunk(file, "IDAT"u8, data.GetBuffer().AsSpan(0, checked((int)data.Length)));
            WriteChunk(file, "IEND"u8, default);
        }
        File.Move(temporary, path, overwrite: true);
    }

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
        uint[] table = CrcTable;
        foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>
    /// Writes a 32-bit float grey image as an uncompressed TIFF, the values as they are
    /// (not-a-number included). Up to 4 GB, the limit of the format.
    /// </summary>
    public static void WriteTiffFloat(string path, DecodedImage image)
    {
        if (image.Layout != PixelLayout.GrayFloat) throw new ArgumentException("Only 32-bit float grey images are written as float TIFF.", nameof(image));
        int width = image.Width, height = image.Height;
        long dataBytes = (long)width * height * sizeof(float);
        const int dataOffset = 8;
        (ushort Tag, ushort Type, uint Value)[] entries =
        [
            (256, 4, (uint)width),                 // ImageWidth
            (257, 4, (uint)height),                // ImageLength
            (258, 3, 32),                          // BitsPerSample
            (259, 3, 1),                           // Compression: none
            (262, 3, 1),                           // Photometric: BlackIsZero
            (273, 4, dataOffset),                  // StripOffsets
            (277, 3, 1),                           // SamplesPerPixel
            (278, 4, (uint)height),                // RowsPerStrip
            (279, 4, checked((uint)dataBytes)),    // StripByteCounts
            (339, 3, 3),                           // SampleFormat: IEEE float
        ];

        string temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Span<byte> header = stackalloc byte[8];
            header[0] = header[1] = (byte)'I';
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], 42);
            BinaryPrimitives.WriteUInt32LittleEndian(header[4..], checked((uint)(dataOffset + dataBytes)));
            file.Write(header);
            for (int y = 0; y < height; y++) file.Write(new ReadOnlySpan<byte>(image.Pixels + (long)y * image.Stride, width * sizeof(float)));
            Span<byte> directory = stackalloc byte[2 + entries.Length * 12 + 4];
            directory.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(directory, (ushort)entries.Length);
            for (int i = 0; i < entries.Length; i++)
            {
                Span<byte> entry = directory.Slice(2 + i * 12, 12);
                BinaryPrimitives.WriteUInt16LittleEndian(entry, entries[i].Tag);
                BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], entries[i].Type);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 1);
                if (entries[i].Type == 3) BinaryPrimitives.WriteUInt16LittleEndian(entry[8..], (ushort)entries[i].Value);
                else BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], entries[i].Value);
            }
            file.Write(directory);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
