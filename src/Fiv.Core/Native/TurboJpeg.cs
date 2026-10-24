using System.Runtime.InteropServices;

namespace Fiv.Native;

/// <summary>
/// The part of the libjpeg-turbo 3 TurboJPEG API that the viewer uses. Signatures are blittable,
/// so calls are direct with no marshalling.
/// </summary>
public static unsafe partial class TurboJpeg
{
    const string Library = "turbojpeg";

    public const int InitCompress = 0;
    public const int InitDecompress = 1;

    public enum Param
    {
        StopOnWarning = 0,
        BottomUp = 1,
        NoRealloc = 2,
        Quality = 3,
        Subsamp = 4,
        JpegWidth = 5,
        JpegHeight = 6,
        Precision = 7,
        ColorSpace = 8,
        FastUpsample = 9,
        FastDct = 10,
        Optimize = 11,
        Progressive = 12,
    }

    public enum PixelFormat
    {
        Rgb = 0,
        Bgr = 1,
        Rgbx = 2,
        Bgrx = 3,
        Xbgr = 4,
        Xrgb = 5,
        Gray = 6,
        Rgba = 7,
        Bgra = 8,
        Abgr = 9,
        Argb = 10,
        Cmyk = 11,
    }

    public enum Subsampling
    {
        S444 = 0,
        S422 = 1,
        S420 = 2,
        Gray = 3,
        S440 = 4,
        S411 = 5,
        S441 = 6,
    }

    public struct ScalingFactor(int num, int denom)
    {
        public int Num = num, Denom = denom;

        /// <summary>The dimension after scaling, rounded up as libjpeg-turbo does (TJSCALED).</summary>
        public readonly int Apply(int dimension) => (int)(((long)dimension * Num + Denom - 1) / Denom);
    }

    [LibraryImport(Library)] public static partial nint tj3Init(int initType);
    [LibraryImport(Library)] public static partial void tj3Destroy(nint handle);
    [LibraryImport(Library)] public static partial int tj3Set(nint handle, Param param, int value);
    [LibraryImport(Library)] public static partial int tj3Get(nint handle, Param param);
    [LibraryImport(Library)] public static partial sbyte* tj3GetErrorStr(nint handle);
    [LibraryImport(Library)] public static partial void tj3Free(void* buffer);
    [LibraryImport(Library)] public static partial nuint tj3JPEGBufSize(int width, int height, Subsampling subsamp);

    [LibraryImport(Library)]
    public static partial int tj3DecompressHeader(nint handle, byte* jpegBuf, nuint jpegSize);

    [LibraryImport(Library)]
    public static partial int tj3SetScalingFactor(nint handle, ScalingFactor scalingFactor);

    [LibraryImport(Library)]
    public static partial int tj3Decompress8(nint handle, byte* jpegBuf, nuint jpegSize, byte* dstBuf, int pitch, PixelFormat pixelFormat);

    /// <summary>9 to 12 bits per sample, lossy or lossless. <paramref name="pitch"/> in samples.</summary>
    [LibraryImport(Library)]
    public static partial int tj3Decompress12(nint handle, byte* jpegBuf, nuint jpegSize, short* dstBuf, int pitch, PixelFormat pixelFormat);

    /// <summary>13 to 16 bits per sample, lossless only. <paramref name="pitch"/> in samples.</summary>
    [LibraryImport(Library)]
    public static partial int tj3Decompress16(nint handle, byte* jpegBuf, nuint jpegSize, ushort* dstBuf, int pitch, PixelFormat pixelFormat);

    [LibraryImport(Library)]
    public static partial int tj3Compress8(nint handle, byte* srcBuf, int width, int pitch, int height, PixelFormat pixelFormat, byte** jpegBuf, nuint* jpegSize);

    public static string LastError(nint handle) => Marshal.PtrToStringUTF8((nint)tj3GetErrorStr(handle)) ?? "";
}
