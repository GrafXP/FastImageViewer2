using System.Buffers.Binary;

namespace Fiv.Imaging;

/// <summary>
/// How the stored pixels have to be turned to be upright, as EXIF tag 0x0112 numbers it.
/// Applied as a transform when drawing; the pixels are never rearranged.
/// </summary>
public enum Orientation : byte
{
    Normal = 1,
    MirrorHorizontal = 2,
    Rotate180 = 3,
    MirrorVertical = 4,
    Transpose = 5,
    Rotate90 = 6,
    Transverse = 7,
    Rotate270 = 8,
}

public static class OrientationExtensions
{
    /// <summary>True if width and height trade places.</summary>
    public static bool SwapsAxes(this Orientation orientation) => orientation >= Orientation.Transpose;

    /// <summary>The transform from stored pixel coordinates to upright ones.</summary>
    public static Affine2 ToUpright(this Orientation orientation, double storedWidth, double storedHeight)
    {
        double w = storedWidth, h = storedHeight;
        return orientation switch
        {
            Orientation.MirrorHorizontal => new Affine2(-1, 0, 0, 1, w, 0),
            Orientation.Rotate180 => new Affine2(-1, 0, 0, -1, w, h),
            Orientation.MirrorVertical => new Affine2(1, 0, 0, -1, 0, h),
            Orientation.Transpose => new Affine2(0, 1, 1, 0, 0, 0),
            Orientation.Rotate90 => new Affine2(0, 1, -1, 0, h, 0),
            Orientation.Transverse => new Affine2(0, -1, -1, 0, h, w),
            Orientation.Rotate270 => new Affine2(0, -1, 1, 0, 0, w),
            _ => Affine2.Identity,
        };
    }
}

/// <summary>x' = A x + C y + Tx, y' = B x + D y + Ty.</summary>
public readonly record struct Affine2(double A, double B, double C, double D, double Tx, double Ty)
{
    public static readonly Affine2 Identity = new(1, 0, 0, 1, 0, 0);

    public static Affine2 ScaleTranslate(double scaleX, double scaleY, double tx, double ty) => new(scaleX, 0, 0, scaleY, tx, ty);

    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + Tx, B * x + D * y + Ty);

    /// <summary>The transform that applies <paramref name="first"/>, then this one.</summary>
    public Affine2 After(in Affine2 first) => new(
        A * first.A + C * first.B, B * first.A + D * first.B,
        A * first.C + C * first.D, B * first.C + D * first.D,
        A * first.Tx + C * first.Ty + Tx, B * first.Tx + D * first.Ty + Ty);

    public Affine2 Inverse()
    {
        double det = A * D - B * C;
        double a = D / det, b = -B / det, c = -C / det, d = A / det;
        return new Affine2(a, b, c, d, -(a * Tx + c * Ty), -(b * Tx + d * Ty));
    }
}

public static unsafe class Exif
{
    /// <summary>
    /// Reads the orientation from a JPEG's EXIF block. Only the markers before the image data are
    /// walked, so this costs microseconds. Returns Normal when there is none.
    /// </summary>
    public static Orientation ReadJpegOrientation(byte* data, nuint length)
    {
        var bytes = new ReadOnlySpan<byte>(data, (int)Math.Min(length, 1 << 20));
        return TryFindTiff(bytes, out int start, out int size) ? ReadTiffOrientation(bytes.Slice(start, size)) : Orientation.Normal;
    }

    /// <summary>
    /// Finds the small JPEG that cameras embed in the EXIF block (typically 160x120). It is in
    /// the first kilobytes of the file, so showing it needs no decode of the image itself.
    /// </summary>
    public static bool TryFindJpegThumbnail(byte* data, nuint length, out int offset, out int size)
    {
        offset = size = 0;
        var bytes = new ReadOnlySpan<byte>(data, (int)Math.Min(length, 1 << 20));
        if (!TryFindTiff(bytes, out int tiffStart, out int tiffSize)) return false;
        ReadOnlySpan<byte> tiff = bytes.Slice(tiffStart, tiffSize);
        bool little = tiff[0] == (byte)'I';
        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        if (tiff.Length < 8 || U16(tiff[2..]) != 42) return false;
        // The thumbnail is described by the second directory (IFD1), which the first one links to.
        long first = U32(tiff[4..]);
        if (first + 2 > tiff.Length) return false;
        long link = first + 2 + U16(tiff[(int)first..]) * 12L;
        if (link + 4 > tiff.Length) return false;
        long second = U32(tiff[(int)link..]);
        if (second == 0 || second + 2 > tiff.Length) return false;
        int count = U16(tiff[(int)second..]);
        long thumbnailOffset = 0, thumbnailSize = 0;
        for (int i = 0; i < count; i++)
        {
            long entry = second + 2 + i * 12L;
            if (entry + 12 > tiff.Length) break;
            ushort tag = U16(tiff[(int)entry..]);
            if (tag == 0x0201) thumbnailOffset = U32(tiff[(int)(entry + 8)..]);      // JPEGInterchangeFormat
            else if (tag == 0x0202) thumbnailSize = U32(tiff[(int)(entry + 8)..]);   // JPEGInterchangeFormatLength
        }
        if (thumbnailOffset <= 0 || thumbnailSize < 4 || thumbnailOffset + thumbnailSize > tiff.Length) return false;
        if (tiff[(int)thumbnailOffset] != 0xFF || tiff[(int)thumbnailOffset + 1] != 0xD8) return false;
        offset = tiffStart + (int)thumbnailOffset;
        size = (int)thumbnailSize;
        return true;
    }

    /// <summary>The TIFF structure inside a JPEG's EXIF segment. Only the markers before the image data are walked.</summary>
    static bool TryFindTiff(ReadOnlySpan<byte> bytes, out int start, out int size)
    {
        start = size = 0;
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
        int position = 2;
        while (position + 4 <= bytes.Length && bytes[position] == 0xFF)
        {
            byte marker = bytes[position + 1];
            if (marker is 0xDA or 0xD9) break; // start of scan / end of image
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes[(position + 2)..]);
            if (segmentLength < 2 || position + 2 + segmentLength > bytes.Length) break;
            ReadOnlySpan<byte> segment = bytes.Slice(position + 4, segmentLength - 2);
            if (marker == 0xE1 && segment.Length > 14 && segment[..6].SequenceEqual("Exif\0\0"u8))
            {
                start = position + 4 + 6;
                size = segment.Length - 6;
                return true;
            }
            position += 2 + segmentLength;
        }
        return false;
    }

    static Orientation ReadTiffOrientation(ReadOnlySpan<byte> tiff)
    {
        bool little = tiff[0] == (byte)'I';
        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);

        if (tiff.Length < 8 || U16(tiff[2..]) != 42) return Orientation.Normal;
        uint ifd = U32(tiff[4..]);
        if (ifd + 2 > tiff.Length) return Orientation.Normal;
        int count = U16(tiff[(int)ifd..]);
        for (int i = 0; i < count; i++)
        {
            int entry = (int)ifd + 2 + i * 12;
            if (entry + 12 > tiff.Length) break;
            if (U16(tiff[entry..]) != 0x0112) continue;
            int value = U16(tiff[(entry + 8)..]);
            return value is >= 1 and <= 8 ? (Orientation)value : Orientation.Normal;
        }
        return Orientation.Normal;
    }
}
