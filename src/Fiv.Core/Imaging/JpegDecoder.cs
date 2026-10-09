using System.Runtime.InteropServices;
using Fiv.Native;

namespace Fiv.Imaging;

[Flags]
public enum JpegDecodeOptions
{
    None = 0,

    /// <summary>Nearest-neighbour chroma upsampling instead of the smooth default. Faster, slightly blockier colour edges.</summary>
    FastUpsample = 1,
}

/// <summary>JPEG decoding with libjpeg-turbo, straight to BGRA8 or Gray8.</summary>
public static unsafe partial class JpegDecoder
{
    /// <summary>The reductions libjpeg-turbo performs inside the inverse DCT, i.e. almost for free.</summary>
    public static ReadOnlySpan<int> ScaleDenominators => [8, 4, 2, 1];

    public static bool TryReadSize(byte* data, nuint size, out int width, out int height) =>
        TryReadHeader(data, size, out width, out height, out _);

    static bool TryReadHeader(byte* data, nuint size, out int width, out int height, out bool gray)
    {
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        try
        {
            bool ok = TurboJpeg.tj3DecompressHeader(tj, data, size) == 0;
            width = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegWidth) : 0;
            height = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegHeight) : 0;
            gray = ok && TurboJpeg.tj3Get(tj, TurboJpeg.Param.Subsamp) == (int)TurboJpeg.Subsampling.Gray;
            return ok && width > 0 && height > 0;
        }
        finally
        {
            TurboJpeg.tj3Destroy(tj);
        }
    }

    /// <summary>
    /// The largest reduction 1/8, 1/4, 1/2 whose result still has at least
    /// <paramref name="minWidth"/> x <paramref name="minHeight"/> pixels; 1 if none has.
    /// </summary>
    public static int ChooseDenominator(int sourceWidth, int sourceHeight, int minWidth, int minHeight)
    {
        foreach (int denominator in ScaleDenominators)
        {
            var factor = new TurboJpeg.ScalingFactor(1, denominator);
            if (factor.Apply(sourceWidth) >= minWidth && factor.Apply(sourceHeight) >= minHeight) return denominator;
        }
        return 1;
    }

    /// <summary>
    /// Decodes at 1/<paramref name="denominator"/> of the file's size (1, 2, 4 or 8).
    /// <paramref name="cancel"/> may point to an int that another thread sets non-zero to stop the
    /// decode; it stops within a few rows and the result is null.
    /// </summary>
    public static DecodedImage? Decode(byte* data, nuint size, int denominator = 1, JpegDecodeOptions options = JpegDecodeOptions.None,
        int* cancel = null)
    {
        if (!TryReadHeader(data, size, out int sourceWidth, out int sourceHeight, out bool gray))
            throw new InvalidDataException("Not a JPEG file, or its header is damaged.");
        var factor = new TurboJpeg.ScalingFactor(1, denominator);
        var image = new DecodedImage(factor.Apply(sourceWidth), factor.Apply(sourceHeight),
            gray ? PixelLayout.Gray8 : PixelLayout.Bgra8, sourceWidth, sourceHeight);

        const int MessageSize = 256;
        byte* message = stackalloc byte[MessageSize];
        int result = fiv_jpeg_decode(data, size, denominator, gray ? 1 : 0, (options & JpegDecodeOptions.FastUpsample) != 0 ? 1 : 0,
            image.Pixels, image.Stride, image.Width, image.Height, cancel, message, MessageSize);
        if (result == 0) return image;
        image.Dispose();
        if (result == 2) return null;
        throw new InvalidDataException(Marshal.PtrToStringUTF8((nint)message) ?? "JPEG decode failed.");
    }

    [LibraryImport("fivjpeg")]
    private static partial int fiv_jpeg_decode(byte* data, nuint size, int denominator, int gray, int fastUpsample,
        byte* dst, int pitch, int width, int height, int* cancel, byte* message, int messageSize);
}
