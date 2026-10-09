using Fiv.Native;

namespace Fiv.Imaging;

[Flags]
public enum JpegDecodeOptions
{
    None = 0,

    /// <summary>Nearest-neighbour chroma upsampling instead of the smooth default. Faster, slightly blockier colour edges.</summary>
    FastUpsample = 1,

    /// <summary>The faster, less accurate inverse DCT.</summary>
    FastDct = 2,
}

/// <summary>JPEG decoding with libjpeg-turbo, straight to BGRA8 or Gray8.</summary>
public static unsafe class JpegDecoder
{
    /// <summary>The reductions libjpeg-turbo performs inside the inverse DCT, i.e. almost for free.</summary>
    public static ReadOnlySpan<int> ScaleDenominators => [8, 4, 2, 1];

    public static bool TryReadSize(byte* data, nuint size, out int width, out int height)
    {
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        try
        {
            bool ok = TurboJpeg.tj3DecompressHeader(tj, data, size) == 0;
            width = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegWidth) : 0;
            height = ok ? TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegHeight) : 0;
            return ok;
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

    /// <summary>Decodes at 1/<paramref name="denominator"/> of the file's size (1, 2, 4 or 8).</summary>
    public static DecodedImage Decode(byte* data, nuint size, int denominator = 1, JpegDecodeOptions options = JpegDecodeOptions.None)
    {
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitDecompress);
        try
        {
            if (TurboJpeg.tj3DecompressHeader(tj, data, size) != 0) throw new InvalidDataException(TurboJpeg.LastError(tj));
            int sourceWidth = TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegWidth);
            int sourceHeight = TurboJpeg.tj3Get(tj, TurboJpeg.Param.JpegHeight);
            bool gray = TurboJpeg.tj3Get(tj, TurboJpeg.Param.Subsamp) == (int)TurboJpeg.Subsampling.Gray;

            var factor = new TurboJpeg.ScalingFactor(1, denominator);
            if (TurboJpeg.tj3SetScalingFactor(tj, factor) != 0) throw new ArgumentOutOfRangeException(nameof(denominator));
            if ((options & JpegDecodeOptions.FastUpsample) != 0) TurboJpeg.tj3Set(tj, TurboJpeg.Param.FastUpsample, 1);
            if ((options & JpegDecodeOptions.FastDct) != 0) TurboJpeg.tj3Set(tj, TurboJpeg.Param.FastDct, 1);

            var image = new DecodedImage(factor.Apply(sourceWidth), factor.Apply(sourceHeight),
                gray ? PixelLayout.Gray8 : PixelLayout.Bgra8, sourceWidth, sourceHeight);
            var format = gray ? TurboJpeg.PixelFormat.Gray : TurboJpeg.PixelFormat.Bgra;
            if (TurboJpeg.tj3Decompress8(tj, data, size, image.Pixels, image.Stride, format) != 0)
            {
                string error = TurboJpeg.LastError(tj);
                image.Dispose();
                throw new InvalidDataException(error);
            }
            return image;
        }
        finally
        {
            TurboJpeg.tj3Destroy(tj);
        }
    }
}
