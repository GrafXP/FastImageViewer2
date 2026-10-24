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

/// <summary>
/// JPEG decoding with libjpeg-turbo, straight to BGRA8 or Gray8; files with more than 8 bits per
/// sample (12-bit, and lossless up to 16) to RGBA16 or Gray16.
/// </summary>
public static unsafe partial class JpegDecoder
{
    /// <summary>The reductions libjpeg-turbo performs inside the inverse DCT, i.e. almost for free.</summary>
    public static ReadOnlySpan<int> ScaleDenominators => [8, 4, 2, 1];

    public static bool TryReadSize(byte* data, nuint size, out int width, out int height) =>
        TryReadHeader(data, size, out width, out height, out _, out _);

    /// <param name="precision">Bits per sample: 8 for nearly every JPEG, 12, or 2 to 16 for a lossless one.</param>
    public static bool TryReadSize(byte* data, nuint size, out int width, out int height, out int precision) =>
        TryReadHeader(data, size, out width, out height, out _, out precision);

    static bool TryReadHeader(byte* data, nuint size, out int width, out int height, out bool gray, out int precision)
    {
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        try
        {
            bool ok = TurboJpeg.tj3DecompressHeader(tj, data, size) == 0;
            width = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegWidth) : 0;
            height = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegHeight) : 0;
            gray = ok && TurboJpeg.tj3Get(tj, TurboJpeg.Param.Subsamp) == (int)TurboJpeg.Subsampling.Gray;
            precision = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.Precision) : 0;
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
    /// decode; it stops within a few rows and the result is null. <paramref name="sourceWidth"/> and
    /// <paramref name="sourceHeight"/>, if given, are recorded as the size of the image the result
    /// stands for (a raw's, for the JPEG embedded in it) instead of the JPEG's own. A file with more
    /// than 8 bits per sample is decoded at full size whatever the denominator, and cannot be stopped.
    /// </summary>
    public static DecodedImage? Decode(byte* data, nuint size, int denominator = 1, JpegDecodeOptions options = JpegDecodeOptions.None,
        int* cancel = null, int sourceWidth = 0, int sourceHeight = 0)
    {
        if (!TryReadHeader(data, size, out int jpegWidth, out int jpegHeight, out bool gray, out int precision))
            throw new InvalidDataException("Not a JPEG file, or its header is damaged.");
        if (precision > 8) return DecodeDeep(data, size, jpegWidth, jpegHeight, gray, precision, sourceWidth, sourceHeight);
        var factor = new TurboJpeg.ScalingFactor(1, denominator);
        var image = new DecodedImage(factor.Apply(jpegWidth), factor.Apply(jpegHeight), gray ? PixelLayout.Gray8 : PixelLayout.Bgra8,
            sourceWidth > 0 ? sourceWidth : jpegWidth, sourceHeight > 0 ? sourceHeight : jpegHeight);

        const int MessageSize = 256;
        byte* message = stackalloc byte[MessageSize];
        int result = fiv_jpeg_decode(data, size, denominator, gray ? 1 : 0, (options & JpegDecodeOptions.FastUpsample) != 0 ? 1 : 0,
            image.Pixels, image.Stride, image.Width, image.Height, cancel, message, MessageSize);
        if (result == 0) return image;
        image.Dispose();
        if (result == 2) return null;
        throw new InvalidDataException(Marshal.PtrToStringUTF8((nint)message) ?? "JPEG decode failed.");
    }

    // 9 to 16 bits per sample, through the TurboJPEG calls for them, spread over the full 16-bit
    // range as WIC does it for other formats. TurboJPEG sets alpha to the largest value of the precision.
    static DecodedImage DecodeDeep(byte* data, nuint size, int width, int height, bool gray, int precision, int sourceWidth, int sourceHeight)
    {
        var image = new DecodedImage(width, height, gray ? PixelLayout.Gray16 : PixelLayout.Rgba16,
            sourceWidth > 0 ? sourceWidth : width, sourceHeight > 0 ? sourceHeight : height);
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        try
        {
            int channels = gray ? 1 : 4;
            TurboJpeg.PixelFormat format = gray ? TurboJpeg.PixelFormat.Gray : TurboJpeg.PixelFormat.Rgba;
            int result = TurboJpeg.tj3DecompressHeader(tj, data, size);
            if (result == 0)
            {
                result = precision <= 12
                    ? TurboJpeg.tj3Decompress12(tj, data, size, (short*)image.Pixels, width * channels, format)
                    : TurboJpeg.tj3Decompress16(tj, data, size, (ushort*)image.Pixels, width * channels, format);
            }
            if (result != 0) throw new InvalidDataException(TurboJpeg.LastError(tj));
            if (precision < 16)
            {
                var samples = new Span<ushort>(image.Pixels, (int)(image.ByteCount / 2));
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (ushort)(samples[i] << (16 - precision) | samples[i] >> (2 * precision - 16));
            }
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
        finally
        {
            TurboJpeg.tj3Destroy(tj);
        }
    }

    [LibraryImport("fivjpeg")]
    private static partial int fiv_jpeg_decode(byte* data, nuint size, int denominator, int gray, int fastUpsample,
        byte* dst, int pitch, int width, int height, int* cancel, byte* message, int messageSize);
}
