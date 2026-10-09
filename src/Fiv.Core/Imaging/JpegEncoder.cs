using Fiv.Native;

namespace Fiv.Imaging;

/// <summary>JPEG encoding with libjpeg-turbo. The viewer uses it for the thumbnails it keeps on disk.</summary>
public static unsafe class JpegEncoder
{
    /// <summary>Encodes a BGRA8 image (alpha is dropped). Chroma is stored at half resolution unless <paramref name="fullChroma"/>.</summary>
    public static byte[] Encode(DecodedImage image, int quality, bool fullChroma = false, bool progressive = false)
    {
        if (image.Layout != PixelLayout.Bgra8) throw new ArgumentException("Only BGRA8 images can be encoded.", nameof(image));
        nint tj = TurboJpeg.tj3Init(TurboJpeg.InitCompress);
        byte* jpeg = null;
        try
        {
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Quality, quality);
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Subsamp, (int)(fullChroma ? TurboJpeg.Subsampling.S444 : TurboJpeg.Subsampling.S420));
            TurboJpeg.tj3Set(tj, TurboJpeg.Param.Progressive, progressive ? 1 : 0);
            nuint size = 0;
            if (TurboJpeg.tj3Compress8(tj, image.Pixels, image.Width, image.Stride, image.Height, TurboJpeg.PixelFormat.Bgra, &jpeg, &size) != 0)
                throw new InvalidOperationException("JPEG encode failed: " + TurboJpeg.LastError(tj));
            return new ReadOnlySpan<byte>(jpeg, checked((int)size)).ToArray();
        }
        finally
        {
            TurboJpeg.tj3Free(jpeg);
            TurboJpeg.tj3Destroy(tj);
        }
    }
}
