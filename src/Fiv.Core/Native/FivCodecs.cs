using System.Runtime.InteropServices;

namespace Fiv.Native;

/// <summary>
/// fivcodecs.dll (native\fivcodecs\fivcodecs.cpp): libheif with libde265 and dav1d, libwebp,
/// libjxl, LibRaw and libtiff behind two calls. Signatures are blittable, so calls are direct with no marshalling.
/// </summary>
public static unsafe partial class FivCodecs
{
    const string Library = "fivcodecs";

    public const int Ok = 0;
    public const int Error = 1;
    public const int Cancelled = 2;

    public enum Format
    {
        None = 0,
        Heif = 1,
        WebP = 2,
        JpegXl = 3,
        Raw = 4,

        /// <summary>libtiff: never from <see cref="fiv_probe"/>, only for a TIFF the Windows codec does not read.</summary>
        Tiff = 5,
    }

    /// <summary>fiv_info: what <see cref="fiv_probe"/> finds out without decoding.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public Format Format;
        public int Width, Height, Orientation;
        public long PreviewOffset;
        public int PreviewLength;
    }

    [LibraryImport(Library)]
    public static partial int fiv_probe(byte* data, nuint size, Info* info);

    /// <param name="allocate">Called once the decoder knows the result: (context, width, height, layout, source width, source height) → the pixels, or null.</param>
    [LibraryImport(Library)]
    public static partial int fiv_decode(byte* data, nuint size, Format format, int hint,
        delegate* unmanaged<nint, int, int, int, int, int, byte*> allocate, nint context, int* cancel, byte* message, int messageSize);
}
